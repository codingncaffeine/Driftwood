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

    public string Name { get; }
    public string Description { get; private set; } = string.Empty;
    public int Format { get; private set; }

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

        if (_zip is null) return;
        foreach (var entry in _zip.Entries) _entries[entry.FullName] = entry;
    }

    /// <summary>Opens a pack from a directory or a .zip. Returns null if the path is neither.</summary>
    public static TexturePack? Open(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);

        TexturePack pack;
        if (Directory.Exists(path))
        {
            pack = new TexturePack(name, null, path);
        }
        else if (File.Exists(path) && Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
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
        var raw = ReadAllBytes("pack.mcmeta");
        if (raw is null) return;

        var text = Encoding.UTF8.GetString(raw);

        // Deliberately not a JSON parser. Two numbers and a string out of a file whose schema has
        // changed three times does not justify one, and a manifest we cannot read must not stop us
        // reading the textures beside it.
        Format = ReadInt(text, "pack_format") ?? ReadInt(text, "min_format") ?? 0;
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
        if (_zip is not null)
        {
            foreach (var entry in _zip.Entries)
                if (entry.Length > 0) yield return entry.FullName;
            yield break;
        }

        if (_root is null || !Directory.Exists(_root)) yield break;

        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            yield return Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>Loads one texture by its path inside the pack, scaled to the given tile size.</summary>
    public byte[]? TryLoadTile(string assetPath, int size)
    {
        var raw = ReadAllBytes($"assets/minecraft/{assetPath}");
        if (raw is null)
        {
            Missing++;
            return null;
        }

        if (!Png.TryDecode(raw, out var image, out var error))
        {
            Faults.Add($"{assetPath}: {error}");
            return null;
        }

        Loaded++;
        return Resample(image, size);
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
