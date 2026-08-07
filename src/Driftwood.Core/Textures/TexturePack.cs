using System.IO.Compression;
using System.Text;

namespace Driftwood.Core.Textures;

/// <summary>
/// Reads block textures out of a texture pack — a folder or a zip — that the player already has.
/// </summary>
/// <remarks>
/// <para>Nothing is ever bundled, copied or written back out. The pack stays where the player put
/// it; this opens it, reads the images it needs, and closes it. That is the same line every
/// clean-room engine that loads original game data draws, and it is the only one that matters:
/// reading a file somebody already owns is not distributing it.</para>
/// <para>A pack is a <em>sparse override set</em>. Real ones ship only the textures their author
/// chose to repaint and lean on the base game underneath for the rest. Driftwood has no such base
/// game, so a pack layers over Driftwood's own generated tiles and any texture it does not carry
/// simply stays ours. That is what stops a half-finished pack leaving holes in the world.</para>
/// <para>The lookup goes through an explicit name map rather than matching our block names against
/// the pack's file names. Ours are deliberately not theirs — that is what clean-room means — so the
/// correspondence has to be written down somewhere, and one table is a better place for it than
/// scattered through the block definitions.</para>
/// </remarks>
public sealed class TexturePack : IDisposable
{
    private readonly ZipArchive? _zip;
    private readonly string? _root;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What sits in front of every path inside the pack. Usually nothing.
    /// </summary>
    /// <remarks>
    /// A pack is supposed to have <c>assets/</c> at the root of its archive, and an enormous number
    /// of them do not — because zipping a folder is one click and zipping its contents is three,
    /// so the archive comes out as <c>MyPack/assets/…</c>. Looking for a literal path finds nothing
    /// in one of those, loads not a single texture, and reports success, which a player reads as
    /// the import having quietly done nothing. So the root is <em>found</em> rather than assumed.
    /// </remarks>
    private readonly string _prefix = "";

    /// <summary>
    /// Which namespaces the pack actually ships, <c>minecraft</c> first.
    /// </summary>
    /// <remarks>
    /// Almost everything lives under <c>minecraft</c>, and almost is not all: the pack this was
    /// tested against ships three. A texture in one of the others used to stay ours with nothing
    /// anywhere saying why.
    /// </remarks>
    private readonly string[] _namespaces = ["minecraft"];

    public string Name { get; }
    public string Description { get; private set; } = string.Empty;
    public int Format { get; private set; }

    /// <summary>Which of the two layouts this pack was written for.</summary>
    public PackDialect Dialect { get; private set; }

    /// <summary>The namespaces this pack was found to contain, for the report.</summary>
    public IReadOnlyList<string> Namespaces => _namespaces;

    /// <summary>Textures asked for and found.</summary>
    public int Loaded { get; private set; }

    /// <summary>Textures asked for that the pack does not carry, which keep Driftwood's own.</summary>
    public int Missing { get; private set; }

    /// <summary>Files that were present but could not be read, with the reason.</summary>
    public List<string> Faults { get; } = [];

    private TexturePack(string name, ZipArchive? zip, string? root)
    {
        Name = name;
        _zip = zip;
        _root = root;

        if (_zip is not null)
            foreach (var entry in _zip.Entries) _entries[entry.FullName] = entry;

        _prefix = FindRoot();
        _namespaces = FindNamespaces();
        Dialect = FindDialect();
    }

    /// <summary>
    /// Whatever directory holds <c>pack.mcmeta</c>, as a prefix ending in a slash.
    /// </summary>
    /// <remarks>
    /// <para>The manifest is the one file every pack has exactly one of, so finding it finds the
    /// root without having to guess at folder names. The shallowest wins, because a pack that also
    /// carries somebody else's pack inside it should be read as itself.</para>
    /// <para>⚠ <b>TWO INDEPENDENT MECHANISMS ANSWER THE NESTED CASE, and a control run on either one
    /// alone stays GREEN.</b> Measured: with the manifest search disabled the <c>assets/</c> fallback
    /// below finds the same prefix, and the audit's nested-zip check goes on passing. That is a good
    /// property of the reader and a trap for anybody testing it — the control that actually fires is
    /// the whole function returning nothing. Worth knowing before deleting either half as redundant.
    /// </para>
    /// </remarks>
    private string FindRoot()
    {
        // Whichever manifest is there says where the root is. Neither layout carries the other's,
        // so the file that turns up answers it without having to guess at folder names.
        if (FindShallowest("pack.mcmeta") is { } java) return java;
        if (FindShallowest("manifest.json") is { } bedrock) return bedrock;

        // No manifest anywhere. Some packs genuinely ship without one, so fall back to wherever the
        // textures begin rather than refusing — "assets/" for one layout, "textures/" for the other.
        var best = (string?)null;

        foreach (var path in RawEntries())
        {
            var at = path.IndexOf("assets/", StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            if (best is null || at < best.Length) best = path[..at];
        }

        if (best is not null) return best;

        foreach (var path in RawEntries())
        {
            var at = path.IndexOf("textures/", StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            if (best is null || at < best.Length) best = path[..at];
        }

        return best ?? "";
    }

    /// <summary>
    /// Which layout the pack turned out to be, read off what it actually contains.
    /// </summary>
    /// <remarks>
    /// <para>The folders decide it, not the manifest. A <c>pack_format</c> says which game version a
    /// pack was written against and packs get that wrong constantly; whether the block folder is
    /// singular or plural, and whether there is an <c>assets/</c> above it at all, are facts about
    /// the files in front of us.</para>
    /// <para>This only picks the order candidates are tried in and what the report says. Every
    /// layout is tried for every texture regardless, so a pack that is half one thing and half
    /// another — and merged packs exist — resolves per texture rather than per pack.</para>
    /// </remarks>
    /// <summary>True when the pack holds a file at exactly this path, whatever shape it is.</summary>
    /// <remarks>
    /// ⛳ Asked so a layout we do <em>not</em> read can still be named. A pack with a
    /// <c>terrain.png</c> at its root is a 2012 atlas pack rather than a broken download, and being
    /// told which it is turns a dead end into something a player can go and look up.
    /// </remarks>
    public bool Has(string path)
    {
        foreach (var entry in RawEntries())
            if (string.Equals(entry, path, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private PackDialect FindDialect()
    {
        bool java = false, plural = false, textures = false, assets = false, atlas = false;

        foreach (var path in RawEntries())
        {
            if (path.Contains("/textures/block/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/textures/item/", StringComparison.OrdinalIgnoreCase)) java = true;

            if (path.Contains("/textures/blocks/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/textures/items/", StringComparison.OrdinalIgnoreCase)) plural = true;

            if (path.StartsWith($"{_prefix}textures/", StringComparison.OrdinalIgnoreCase)) textures = true;
            if (path.StartsWith($"{_prefix}assets/", StringComparison.OrdinalIgnoreCase)) assets = true;

            if (string.Equals(path, $"{_prefix}terrain.png", StringComparison.OrdinalIgnoreCase)) atlas = true;
        }

        // ⛳ ASKED FIRST, and it has to be. A 2012 pack is the only layout with no per-texture files
        // at all, so every other test below reads false on it and the answer would be "unknown" —
        // which is what it was, and it is why the shelf refused a perfectly readable pack by name.
        // ⚠ Before the folder tests rather than after, because a few of these packs also ship a
        // stray textures/ folder for the things that were never on the grid.
        if (atlas) return PackDialect.Atlas;

        // No assets/ above the textures at all is the thing only one layout does.
        if (!assets && textures) return PackDialect.Bedrock;
        if (java) return PackDialect.Java;
        if (plural) return PackDialect.JavaLegacy;

        return PackDialect.Unknown;
    }

    /// <summary>The grid is always sixteen cells across and sixteen down, whatever it is stored at.</summary>
    public const int AtlasCells = 16;

    private Image? _terrain;
    private Image? _guiItems;
    private bool _triedTerrain;
    private bool _triedGuiItems;

    /// <summary>
    /// One sheet of a 2012 pack, decoded once and kept.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Held rather than re-read per cell.</b> Every block in the game comes off one image, and
    /// this pack's is 2048×2048 — decoding it a hundred and eighty times would be the whole import.
    /// </remarks>
    private Image? Sheet(bool items)
    {
        if (items)
        {
            if (_triedGuiItems) return _guiItems;
            _triedGuiItems = true;
            _guiItems = Decode($"{_prefix}gui/items.png");
            return _guiItems;
        }

        if (_triedTerrain) return _terrain;
        _triedTerrain = true;
        _terrain = Decode($"{_prefix}terrain.png");
        return _terrain;
    }

    private Image? Decode(string path)
    {
        var raw = ReadAllBytes(path);
        if (raw is null) return null;

        if (Png.TryDecode(raw, out var image, out var error)) return image;

        Faults.Add($"{path}: {error}");
        return null;
    }

    /// <summary>How big one cell of the grid is, in pixels. Zero when there is no grid.</summary>
    public int AtlasTileSize => Sheet(items: false) is { } sheet ? sheet.Width / AtlasCells : 0;

    /// <summary>
    /// One cell of the grid, resampled to a tile.
    /// </summary>
    /// <param name="index">Row-major: <c>row * 16 + column</c>, which is the scheme itself.</param>
    /// <param name="items">True to read <c>gui/items.png</c> rather than <c>terrain.png</c>.</param>
    public byte[]? TryLoadAtlasTile(int index, int size, bool items, out string from)
    {
        from = items ? $"gui/items.png#{index}" : $"terrain.png#{index}";

        if (index < 0 || index >= AtlasCells * AtlasCells || Sheet(items) is not { } sheet)
        {
            Missing++;
            return null;
        }

        var cell = sheet.Width / AtlasCells;
        if (cell <= 0 || sheet.Height < cell * AtlasCells)
        {
            Missing++;
            return null;
        }

        var left = index % AtlasCells * cell;
        var top = index / AtlasCells * cell;
        var tile = new byte[size * size * 4];

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var sx = left + Math.Min(x * cell / size, cell - 1);
            var sy = top + Math.Min(y * cell / size, cell - 1);

            var src = (sy * sheet.Width + sx) * 4;
            var dst = (y * size + x) * 4;

            tile[dst] = sheet.Pixels[src];
            tile[dst + 1] = sheet.Pixels[src + 1];
            tile[dst + 2] = sheet.Pixels[src + 2];
            tile[dst + 3] = sheet.Pixels[src + 3];
        }

        Loaded++;
        return tile;
    }

    /// <summary>
    /// Every cell's mean colour and how much of it is opaque, for identifying the grid.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>THE TABLE OF WHAT IS WHERE IS NOT TRANSCRIBED FROM MEMORY, AND THIS IS WHY.</b> The
    /// classic grid is well known and half-remembering it puts diamond where lapis is — a sampled
    /// check of one candidate table read grey at the cell diamond was expected in. What a cell holds
    /// is a fact about the image, so it is measured off the image: gold block is 228,169,18 and
    /// nothing else on the sheet is, bedrock is near-black, sand is pale. This is the instrument
    /// that says so.
    /// </remarks>
    public (byte R, byte G, byte B, int Opaque)[] AtlasCensus(bool items)
    {
        var census = new (byte, byte, byte, int)[AtlasCells * AtlasCells];
        if (Sheet(items) is not { } sheet) return census;

        var cell = sheet.Width / AtlasCells;
        if (cell <= 0 || sheet.Height < cell * AtlasCells) return census;

        for (var index = 0; index < census.Length; index++)
        {
            var left = index % AtlasCells * cell;
            var top = index / AtlasCells * cell;

            long r = 0, g = 0, b = 0;
            var opaque = 0;

            for (var y = 0; y < cell; y++)
            for (var x = 0; x < cell; x++)
            {
                var at = ((top + y) * sheet.Width + left + x) * 4;
                if (sheet.Pixels[at + 3] < 128) continue;

                r += sheet.Pixels[at];
                g += sheet.Pixels[at + 1];
                b += sheet.Pixels[at + 2];
                opaque++;
            }

            census[index] = opaque == 0
                ? ((byte)0, (byte)0, (byte)0, 0)
                : ((byte)(r / opaque), (byte)(g / opaque), (byte)(b / opaque), opaque * 100 / (cell * cell));
        }

        return census;
    }

    /// <summary>
    /// The directory holding the shallowest copy of a file, as a prefix ending in a slash.
    /// </summary>
    /// <remarks>
    /// Shallowest, because a pack that carries somebody else's pack inside it should be read as
    /// itself — and one with art beside it beats one without, because an <c>.mcaddon</c> carries a
    /// behaviour pack whose manifest is exactly as shallow and which holds no textures at all.
    /// </remarks>
    private string? FindShallowest(string fileName)
    {
        string? best = null;
        string? withArt = null;

        foreach (var path in RawEntries())
        {
            if (!path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)) continue;

            var directory = path[..^fileName.Length];
            if (best is null || directory.Length < best.Length) best = directory;

            if (!HasArtUnder(directory)) continue;
            if (withArt is null || directory.Length < withArt.Length) withArt = directory;
        }

        return withArt ?? best;
    }

    private bool HasArtUnder(string directory)
    {
        foreach (var path in RawEntries())
            if (path.StartsWith($"{directory}textures/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith($"{directory}assets/", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private string[] FindNamespaces()
    {
        var found = new List<string> { "minecraft" };
        var root = $"{_prefix}assets/";

        foreach (var path in RawEntries())
        {
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = path[root.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0) continue;

            var space = rest[..slash];
            if (!found.Contains(space, StringComparer.OrdinalIgnoreCase)) found.Add(space);
        }

        return [.. found];
    }

    /// <summary>Every path in the archive or folder, before the root prefix is taken off.</summary>
    private IEnumerable<string> RawEntries()
    {
        if (_zip is not null)
        {
            foreach (var entry in _zip.Entries) yield return entry.FullName;
            yield break;
        }

        if (_root is null || !Directory.Exists(_root)) yield break;

        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            yield return Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// The archive extensions a pack is actually distributed under.
    /// </summary>
    /// <remarks>
    /// <para>All of them are zips. <c>.mcpack</c> is how Bedrock packs are handed round — it opens on
    /// a double-click and installs itself, which is why nobody renames them — and <c>.mcaddon</c> is
    /// the same container holding one or more of those. Refusing an extension is refusing a file
    /// that would have loaded perfectly.</para>
    /// <para>⛳ <c>.jar</c> is a zip with a different name on it, and packs from the era when a
    /// resource pack and a mod were the same download still ship as one. It cost one array entry.
    /// </para>
    /// </remarks>
    public static readonly string[] Extensions = [".zip", ".jar", ".mcpack", ".mcaddon"];

    /// <summary>
    /// Archives we can see are archives and cannot open, and what each one is.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Refused BY NAME with a reason, because the alternative is indistinguishable from "that
    /// is not a pack".</b> A <c>.rar</c> is somebody's pack — they downloaded it, they can see the
    /// textures inside it — and answering "there is no pack there" sends them looking for a
    /// corrupted download instead of for an unzip program. We read zips and only zips; saying which
    /// of the two problems they have is the entire difference between a message and a shrug.
    /// </remarks>
    public static readonly (string Extension, string Format)[] UnreadableArchives =
    [
        (".rar", "RAR"),
        (".7z", "7-Zip"),
        (".tar", "tar"),
        (".gz", "gzip"),
        (".tgz", "gzipped tar"),
        (".bz2", "bzip2"),
        (".xz", "xz"),
    ];

    /// <summary>Opens a pack from a directory or an archive. Returns null if the path is neither.</summary>
    public static TexturePack? Open(string path) => Open(path, out _);

    /// <summary>
    /// Opens a pack, and says why when it will not.
    /// </summary>
    /// <param name="why">
    /// Null on success. Otherwise a sentence a player can act on — which is the point of the
    /// overload: <see cref="Open(string)"/> answering null makes "an archive we cannot read", "a
    /// path with nothing at it" and "a folder full of holiday photographs" the same answer.
    /// </param>
    public static TexturePack? Open(string path, out string? why)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        why = null;

        TexturePack pack;
        if (Directory.Exists(path))
        {
            pack = new TexturePack(name, null, path);
        }
        else if (!File.Exists(path))
        {
            why = "there is nothing at that path";
            return null;
        }
        else if (Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            pack = new TexturePack(name, ZipFile.OpenRead(path), null);
        }
        else
        {
            var extension = Path.GetExtension(path);

            var known = UnreadableArchives.FirstOrDefault(
                a => string.Equals(a.Extension, extension, StringComparison.OrdinalIgnoreCase));

            why = known.Extension is not null
                ? $"{known.Format} archives cannot be read — unpack it, or re-zip it as {string.Join(" or ", Extensions)}"
                : $"a pack is a folder or {string.Join(", ", Extensions)} — not {extension}";

            return null;
        }

        pack.ReadManifest();
        return pack;
    }

    /// <summary>
    /// Reads what the pack says about itself. Tolerant on purpose: a pack whose manifest is odd is
    /// still worth loading textures from, and the version fields have been through three schemas.
    /// </summary>
    private void ReadManifest()
    {
        var raw = ReadAllBytes(
            $"{_prefix}{(Dialect == PackDialect.Bedrock ? "manifest.json" : "pack.mcmeta")}");

        if (raw is null) return;

        var text = Encoding.UTF8.GetString(raw);

        // Deliberately not a JSON parser. Two numbers and a string out of a file whose schema has
        // changed three times does not justify one, and a manifest we cannot read must not stop us
        // reading the textures beside it.
        //
        // The two manifests say different things: one carries a pack format number, the other a
        // format_version and a name in a header block. Both are read for what they have.
        Format = ReadInt(text, "pack_format") ?? ReadInt(text, "min_format") ?? ReadInt(text, "format_version") ?? 0;
        Description = ReadString(text, "description") ?? string.Empty;

        // Strip the legacy colour codes packs put in their descriptions.
        var cleaned = new StringBuilder();
        for (var i = 0; i < Description.Length; i++)
        {
            if (Description[i] == '§') { i++; continue; }
            cleaned.Append(Description[i]);
        }
        Description = cleaned.ToString().Trim();
    }

    private static int? ReadInt(string text, string key)
    {
        var at = text.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        if (at < 0) return null;

        var colon = text.IndexOf(':', at);
        if (colon < 0) return null;

        var i = colon + 1;
        while (i < text.Length && !char.IsDigit(text[i]) && text[i] != '-')
        {
            if (text[i] is '[' or '{' or '"') return null;   // an array or object, not a plain number
            i++;
        }

        var start = i;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '-')) i++;
        return int.TryParse(text.AsSpan(start, i - start), out var value) ? value : null;
    }

    private static string? ReadString(string text, string key)
    {
        var at = text.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        if (at < 0) return null;

        var open = text.IndexOf('"', text.IndexOf(':', at) + 1);
        if (open < 0) return null;

        var close = text.IndexOf('"', open + 1);
        return close < 0 ? null : text[(open + 1)..close];
    }

    /// <summary>
    /// Every file in the pack, as forward-slashed paths from its root.
    /// </summary>
    /// <remarks>
    /// For the coverage report rather than for loading. A pack is a complete inventory of the
    /// reference game's art, so its file list is the clearest statement anywhere of what a game in
    /// this genre contains — and by subtraction, of what this one does not.
    /// </remarks>
    public IEnumerable<string> Entries()
    {
        foreach (var path in RawEntries())
        {
            if (path.Length <= _prefix.Length) continue;
            if (path.EndsWith('/')) continue;
            yield return path[_prefix.Length..];
        }
    }

    /// <summary>Loads one texture by its modern path, scaled to the given tile size.</summary>
    /// <param name="from">
    /// Where it actually came off, which for an old pack is not the path that was asked for. The
    /// report answers "ours or theirs, and from where", and half an answer is worse than none.
    /// </param>
    public byte[]? TryLoadTile(string assetPath, int size, out string from)
    {
        var raw = ReadAsset(assetPath, out from);
        if (raw is null)
        {
            Missing++;
            return null;
        }

        if (!Png.TryDecode(raw, out var image, out var error))
        {
            Faults.Add($"{from}: {error}");
            return null;
        }

        Loaded++;
        return Resample(image, size);
    }

    /// <summary>Loads one texture without caring which of the layouts it came out of.</summary>
    public byte[]? TryLoadTile(string assetPath, int size) => TryLoadTile(assetPath, size, out _);

    /// <summary>
    /// Loads one texture at whatever shape it was painted, without squaring it.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>A creature's skin is not a tile and must not go through <see cref="TryLoadTile"/>.</b>
    /// Every path in this class until now led to a square: a block face is square, so the tile
    /// loader resamples to size×size and that is right for all of it. An entity sheet is a NET —
    /// 64×32 for a cow, 64×64 for a sheep — and squaring one moves every patch on it, so the model
    /// would wear the correct texture with every face reading from the wrong place. The player's own
    /// skin already avoids this by going through <c>PlayerSkin</c> rather than through here; a
    /// creature has no such door, and this is it.
    /// <para>Handed back at the pack's own resolution rather than scaled to anything. A net is
    /// addressed in texels of a 64-wide sheet whatever it is stored at, so the reader scales the
    /// coordinates and never the image — which is also how a 256-pixel pack keeps its detail.</para>
    /// </remarks>
    public Image? TryLoadSheet(string assetPath, out string from)
    {
        var raw = ReadAsset(assetPath, out from);
        if (raw is null)
        {
            Missing++;
            return null;
        }

        if (!Png.TryDecode(raw, out var image, out var error))
        {
            Faults.Add($"{from}: {error}");
            return null;
        }

        Loaded++;
        return image;
    }

    /// <summary>
    /// One texture's frames, ready to play.
    /// </summary>
    /// <param name="Seconds">How long each frame in <paramref name="Frames"/> is held.</param>
    /// <param name="Interpolate">
    /// Whether the pack asked for a fade between frames. Recorded and not yet acted on — a cross-fade
    /// wants two layers live at once, which is a bigger change than the flag is worth on its own.
    /// </param>
    /// <param name="Strip">How many frames the file actually holds, before any ordering was applied.</param>
    public readonly record struct TextureFrames(
        byte[][] Frames, float[] Seconds, bool Interpolate, string From, int Strip);

    /// <summary>
    /// Every frame of an animated texture, in the order they are played, with how long each is held.
    /// </summary>
    /// <remarks>
    /// <para>Returns null for a texture that is one frame, so the caller can keep the plain path. A
    /// strip is detected the same way <see cref="Resample"/> detects it — a file taller than it is
    /// wide by a whole multiple — because that is the only thing every pack agrees on. Water in a
    /// real pack is 16x512: thirty-two frames, and every one of them is what makes it move.</para>
    /// <para>⚠ <b>The sidecar is optional and its absence is not "no animation".</b> A pack that
    /// ships <c>water_still.png</c> as a strip and no <c>.mcmeta</c> beside it still means it to
    /// animate; the sidecar only changes the timing and the order. Treating a missing sidecar as
    /// "still" is how a pack's water ends up frozen while the file plainly holds thirty-two
    /// pictures of it moving.</para>
    /// <para>⚠ <b>The sidecar's schema is not <c>pack.mcmeta</c>'s</b>, despite the extension. Same
    /// name, unrelated contents — do not read one with the other's keys.</para>
    /// </remarks>
    public TextureFrames? TryLoadFrames(string assetPath, int size)
    {
        var raw = ReadAsset(assetPath, out var from);
        if (raw is null) return null;
        if (!Png.TryDecode(raw, out var image, out _)) return null;

        if (image.Height <= image.Width || image.Height % image.Width != 0) return null;

        var count = image.Height / image.Width;
        if (count < 2) return null;

        var frames = new byte[count][];
        for (var f = 0; f < count; f++) frames[f] = Resample(image, size, f);

        // The sidecar sits beside the picture under the same name plus .mcmeta, and it is read
        // through the same candidate list so it is found in whichever layout the texture was.
        var meta = ReadAsset($"{assetPath}.mcmeta", out _);
        var frametime = 1;
        var order = Enumerable.Range(0, count).ToArray();
        var interpolate = false;

        if (meta is not null)
        {
            var text = Encoding.UTF8.GetString(meta);
            frametime = Math.Max(1, ReadInt(text, "frametime") ?? 1);
            interpolate = ReadBool(text, "interpolate");

            // ⚠ Only a plain list of indices is taken. The other form a pack may use is a list of
            // objects carrying their own times, and half-reading that would produce a sequence in
            // the right length and the wrong order — worse than not reading it. Anything else falls
            // back to every frame in the order they are stacked, which is what the file already is.
            if (ReadIntList(text, "frames") is { Length: > 0 } listed
                && Array.TrueForAll(listed, i => i >= 0 && i < count))
            {
                order = listed;
            }
        }

        // Twenty ticks a second is the clock every frametime in every pack is written against.
        var seconds = new float[order.Length];
        Array.Fill(seconds, frametime / 20f);

        var played = new byte[order.Length][];
        for (var i = 0; i < order.Length; i++) played[i] = frames[order[i]];

        return new TextureFrames(played, seconds, interpolate, from, count);
    }

    private static bool ReadBool(string text, string key)
    {
        var at = text.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        if (at < 0) return false;

        var colon = text.IndexOf(':', at);
        if (colon < 0) return false;

        return text.AsSpan(colon + 1).TrimStart().StartsWith("true", StringComparison.OrdinalIgnoreCase);
    }

    private static int[]? ReadIntList(string text, string key)
    {
        var at = text.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        if (at < 0) return null;

        var open = text.IndexOf('[', at);
        if (open < 0) return null;

        var close = text.IndexOf(']', open);
        if (close < 0) return null;

        var body = text[(open + 1)..close];
        if (body.Contains('{')) return null;   // objects with their own times; see the remark above

        var values = new List<int>();
        foreach (var part in body.Split(','))
        {
            if (!int.TryParse(part.Trim(), out var value)) return null;
            values.Add(value);
        }

        return values.Count > 0 ? [.. values] : null;
    }

    /// <summary>
    /// What resolution this pack is actually painted at.
    /// </summary>
    /// <remarks>
    /// <para>Asked rather than told. The tile size used to default to sixteen and had to be repeated
    /// on the command line, so importing a 512-pixel pack without also saying so squashed every
    /// texture in it down to a sixteenth of its width — the import worked, and what came out looked
    /// like a bad copy of the pack a player had just chosen.</para>
    /// <para>Several textures are tried and the widest wins, because any single one of them might
    /// be missing from a partial pack, and because a pack that repaints only its blocks at high
    /// resolution should still be read as high resolution. Animation strips are measured across
    /// rather than down, for the same reason <see cref="Resample"/> only takes their first frame.
    /// </para>
    /// </remarks>
    public int DetectResolution()
    {
        // Named the Java way; ReadAsset translates them for a Bedrock pack, so one list covers both.
        string[] probes =
        [
            "textures/block/stone.png",
            "textures/block/dirt.png",
            "textures/block/oak_planks.png",
            "textures/block/cobblestone.png",
            "textures/item/stick.png",
        ];

        var widest = 0;
        foreach (var probe in probes)
        {
            var raw = ReadAsset(probe, out _);
            if (raw is null) continue;
            if (!Png.TryDecode(raw, out var image, out _)) continue;

            widest = Math.Max(widest, image.Width);
        }

        // ⛳ A 2012 pack carries none of the five and is still perfectly measurable: its resolution
        // is the whole sheet divided by sixteen, which is the one number that layout states about
        // itself. Asked after the probes rather than instead of them, because a pack of that era can
        // also ship a stray modern file and the widest real texture is still the honest answer.
        widest = Math.Max(widest, AtlasTileSize);

        // A pack that carries none of the five is a pack we cannot measure, so keep our own size
        // rather than guessing large and spending a gigabyte on it.
        return widest > 0 ? widest : TileGen.Size;
    }

    /// <summary>
    /// Every place a texture might be, in the order worth trying, given the path modern Java uses.
    /// </summary>
    /// <remarks>
    /// <para>Every caller in the project names one path — the modern one — and this is the only
    /// place that knows there is more than one layout. That is what keeps the layer table one column
    /// of our names against one column of theirs, and what makes a new layout a few lines here
    /// rather than a branch through everything.</para>
    /// <para>Ordered by what the pack looks like, but <b>all of them are tried</b>. Detection only
    /// decides which is quickest, never which is possible, so a merged or half-converted pack
    /// resolves texture by texture instead of failing as a whole.</para>
    /// </remarks>
    private IEnumerable<string> Candidates(string assetPath)
    {
        // ⛳ The 2012 era's own paths for the handful of files that were never on the grid. The
        // colormaps are the ones worth having: they are the same two 256×256 images the modern
        // layout keeps under textures/colormap/, at a different name in a different folder, and
        // they are what decides the colour of every field and every canopy in the world.
        foreach (var old in AtlasPaths(assetPath)) yield return $"{_prefix}{old}";

        if (Dialect == PackDialect.Bedrock)
        {
            // No assets/, no namespace, and the old names.
            foreach (var legacy in PackLayouts.Legacy(assetPath)) yield return $"{_prefix}{legacy}";
            foreach (var space in _namespaces) yield return $"{_prefix}assets/{space}/{assetPath}";
            yield break;
        }

        if (Dialect != PackDialect.JavaLegacy)
            foreach (var space in _namespaces) yield return $"{_prefix}assets/{space}/{assetPath}";

        // Pre-flattening Java: the same shape, folders plural, and the names Bedrock still uses.
        foreach (var legacy in PackLayouts.Legacy(assetPath))
        foreach (var space in _namespaces)
            yield return $"{_prefix}assets/{space}/{legacy}";

        if (Dialect == PackDialect.JavaLegacy)
            foreach (var space in _namespaces) yield return $"{_prefix}assets/{space}/{assetPath}";

        // And the rootward one, for a pack with no assets/ that carries no manifest either.
        foreach (var legacy in PackLayouts.Legacy(assetPath)) yield return $"{_prefix}{legacy}";
    }

    /// <summary>
    /// Where a 2012 pack keeps the few things that were never on the grid.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>A short list on purpose.</b> Everything else in that era is a cell, and a path candidate
    /// for a file that layout never had is a lookup that always misses. These are the ones that
    /// genuinely are files: the two colormaps, and the player's own skin, which is 64×32 and which
    /// <c>PlayerSkin</c> already reads as the legacy sheet.
    /// </remarks>
    private static IEnumerable<string> AtlasPaths(string assetPath) => assetPath switch
    {
        "textures/colormap/grass.png" => ["misc/grasscolor.png"],
        "textures/colormap/foliage.png" => ["misc/foliagecolor.png"],
        "textures/entity/steve.png" => ["mob/char.png"],
        _ => [],
    };

    /// <summary>Reads one asset, and says which of the candidates it actually came off.</summary>
    private byte[]? ReadAsset(string assetPath, out string from)
    {
        foreach (var candidate in Candidates(assetPath))
        {
            var raw = ReadAllBytes(candidate);
            if (raw is null) continue;

            from = candidate.Length > _prefix.Length ? candidate[_prefix.Length..] : candidate;
            return raw;
        }

        from = assetPath;
        return null;
    }

    /// <summary>
    /// Scales an image to a square tile with nearest-neighbour sampling.
    /// </summary>
    /// <remarks>
    /// <para>Nearest neighbour, not bilinear, and that is not laziness. Block art is pixel art; the
    /// whole look depends on hard edges, and smoothing a 16x16 tile up to 64x64 turns a texture
    /// pack into a blurred version of itself. Downscaling a 512x pack does lose detail this way,
    /// which is the honest trade for keeping every other pack crisp.</para>
    /// <para>Animated textures arrive as a vertical strip of frames — a 16x64 file is four frames,
    /// not a tall texture — so a frame index picks which one is taken, and squashing the whole strip
    /// into one tile (which is what a naive scale does) is how water ends up looking like four wrong
    /// textures stacked. See <see cref="TryLoadFrames"/> for reading all of them.</para>
    /// </remarks>
    private static byte[] Resample(Image image, int size, int frame = 0)
    {
        var frameHeight = image.Height > image.Width && image.Height % image.Width == 0
            ? image.Width
            : image.Height;

        var top = Math.Min(frame * frameHeight, image.Height - frameHeight);
        var tile = new byte[size * size * 4];

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var sx = Math.Min(x * image.Width / size, image.Width - 1);
            var sy = top + Math.Min(y * frameHeight / size, frameHeight - 1);

            var src = (sy * image.Width + sx) * 4;
            var dst = (y * size + x) * 4;

            tile[dst] = image.Pixels[src];
            tile[dst + 1] = image.Pixels[src + 1];
            tile[dst + 2] = image.Pixels[src + 2];
            tile[dst + 3] = image.Pixels[src + 3];
        }

        return tile;
    }

    private byte[]? ReadAllBytes(string relativePath)
    {
        if (_root is not null)
        {
            var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(full) ? File.ReadAllBytes(full) : null;
        }

        if (!_entries.TryGetValue(relativePath, out var entry)) return null;

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public void Dispose() => _zip?.Dispose();
}
