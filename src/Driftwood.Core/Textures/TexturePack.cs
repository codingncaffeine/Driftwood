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
    /// The manifest is the one file every pack has exactly one of, so finding it finds the root
    /// without having to guess at folder names. The shallowest wins, because a pack that also
    /// carries somebody else's pack inside it should be read as itself.
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
    private PackDialect FindDialect()
    {
        bool java = false, plural = false, textures = false, assets = false;

        foreach (var path in RawEntries())
        {
            if (path.Contains("/textures/block/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/textures/item/", StringComparison.OrdinalIgnoreCase)) java = true;

            if (path.Contains("/textures/blocks/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/textures/items/", StringComparison.OrdinalIgnoreCase)) plural = true;

            if (path.StartsWith($"{_prefix}textures/", StringComparison.OrdinalIgnoreCase)) textures = true;
            if (path.StartsWith($"{_prefix}assets/", StringComparison.OrdinalIgnoreCase)) assets = true;
        }

        // No assets/ above the textures at all is the thing only one layout does.
        if (!assets && textures) return PackDialect.Bedrock;
        if (java) return PackDialect.Java;
        if (plural) return PackDialect.JavaLegacy;

        return PackDialect.Unknown;
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
    /// All of them are zips. <c>.mcpack</c> is how Bedrock packs are handed round — it opens on a
    /// double-click and installs itself, which is why nobody renames them — and <c>.mcaddon</c> is
    /// the same container holding one or more of those. Refusing an extension is refusing a file
    /// that would have loaded perfectly.
    /// </remarks>
    public static readonly string[] Extensions = [".zip", ".mcpack", ".mcaddon"];

    /// <summary>Opens a pack from a directory or an archive. Returns null if the path is neither.</summary>
    public static TexturePack? Open(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);

        TexturePack pack;
        if (Directory.Exists(path))
        {
            pack = new TexturePack(name, null, path);
        }
        else if (File.Exists(path) && Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            pack = new TexturePack(name, ZipFile.OpenRead(path), null);
        }
        else
        {
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
    /// not a tall texture. Only the first frame is taken until animation exists, because squashing
    /// the strip into one tile is how water ends up looking like four wrong textures stacked.</para>
    /// </remarks>
    private static byte[] Resample(Image image, int size)
    {
        var frameHeight = image.Height > image.Width && image.Height % image.Width == 0
            ? image.Width
            : image.Height;

        var tile = new byte[size * size * 4];

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var sx = Math.Min(x * image.Width / size, image.Width - 1);
            var sy = Math.Min(y * frameHeight / size, frameHeight - 1);

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
