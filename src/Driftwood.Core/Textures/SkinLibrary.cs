using Driftwood.Core.Entities;

namespace Driftwood.Core.Textures;

/// <summary>A persistent, per-user shelf of validated player skins.</summary>
/// <remarks>
/// A shelf entry is a PNG plus a tiny adjacent <c>.skin</c> file. The PNG is copied rather than
/// referenced so tidying Downloads cannot break a setting; the sidecar keeps the model choice and
/// source without modifying or re-encoding somebody's art.
/// </remarks>
public static class SkinLibrary
{
    public const int MaximumBytes = 512 * 1024;
    private const int MaximumMetadataBytes = 16 * 1024;

    public readonly record struct Entry(
        string Name, string Path, ArmStyle Arms, bool Legacy, bool Readable,
        string Kind, string Source, string SourceUrl);

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Driftwood", "skins");

    public static string FilterLabel => "player skins (*.png)";
    public const string FilterSpec = "*.png";

    public static IReadOnlyList<Entry> List(string? folder = null)
    {
        folder ??= Folder;
        var found = new List<Entry>();
        if (!Directory.Exists(folder)) return found;

        foreach (var path in Directory.EnumerateFiles(folder, "*.png", SearchOption.TopDirectoryOnly))
            found.Add(Describe(path));

        found.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    public static string? PathOf(string name, string? folder = null) =>
        Find(name, folder) is { Readable: true } entry ? entry.Path : null;

    public static Entry? Find(string name, string? folder = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return List(folder).FirstOrDefault(entry =>
            string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) is { Path.Length: > 0 } found
                ? found
                : null;
    }

    public static Entry? Install(
        string from, ArmStyle? arms, out string why, string? folder = null)
    {
        why = "";
        if (string.IsNullOrWhiteSpace(from)) { why = "no path given"; return null; }

        from = from.Trim().Trim('"');
        if (!File.Exists(from)) { why = "there is no file at that path"; return null; }
        if (!string.Equals(Path.GetExtension(from), ".png", StringComparison.OrdinalIgnoreCase))
        {
            why = $"'{Path.GetExtension(from)}' is not a skin; wanted .png";
            return null;
        }

        byte[] bytes;
        try
        {
            if (new FileInfo(from).Length > MaximumBytes)
            {
                why = $"the PNG is larger than {MaximumBytes / 1024} KiB";
                return null;
            }
            bytes = File.ReadAllBytes(from);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            why = $"could not read it: {error.Message}";
            return null;
        }

        return Install(bytes, Path.GetFileNameWithoutExtension(from), arms,
            "local import", "", out why, folder);
    }

    /// <summary>Validates encoded bytes, then writes a collision-safe shelf entry atomically.</summary>
    public static Entry? Install(
        byte[] encoded, string suggestedName, ArmStyle? arms,
        string source, string sourceUrl, out string why, string? folder = null)
    {
        why = "";
        if (encoded.Length > MaximumBytes)
        {
            why = $"the PNG is larger than {MaximumBytes / 1024} KiB";
            return null;
        }

        var safe = SafeName(suggestedName);
        if (!PlayerSkin.TryBuild(encoded, safe + ".png", arms, exactSize: true, out var skin, out why))
            return null;

        folder ??= Folder;

        string? path = null;
        string? temporary = null;
        var moved = false;
        try
        {
            Directory.CreateDirectory(folder);
            path = CollisionPath(folder, safe);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
            File.WriteAllBytes(temporary, encoded);
            File.Move(temporary, path);
            moved = true;

            WriteMetadata(path, skin!.Arms, source, sourceUrl);
            return Describe(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            if (moved)
            {
                TryDelete(path);
                TryDelete(path is null ? null : MetadataPath(path));
                TryDelete(path is null ? null : MetadataPath(path) + ".new");
            }
            why = $"could not copy it to the shelf: {error.Message}";
            return null;
        }
    }

    public static bool SetArms(string name, ArmStyle arms, string? folder = null)
    {
        if (Find(name, folder) is not { } entry) return false;
        try
        {
            WriteMetadata(entry.Path, arms, entry.Source, entry.SourceUrl);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool Remove(string name, string? folder = null)
    {
        if (Find(name, folder) is not { } entry) return false;
        try
        {
            File.Delete(entry.Path);
            File.Delete(MetadataPath(entry.Path));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Resolves a remembered name, explicitly reporting a missing or broken selection.</summary>
    public static Entry? Resolve(string name, out string why, string? folder = null)
    {
        why = "";
        if (string.IsNullOrWhiteSpace(name)) return null;

        var entry = Find(name, folder);
        if (entry is null) { why = $"'{name}' is no longer on the skin shelf"; return null; }
        if (!entry.Value.Readable) { why = $"'{name}' cannot be read: {entry.Value.Kind}"; return null; }
        return entry;
    }

    private static Entry Describe(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var meta = ReadMetadata(path);

        try
        {
            if (new FileInfo(path).Length > MaximumBytes)
                return new Entry(name, path, meta.Arms ?? ArmStyle.Classic, false, false,
                    $"the PNG is larger than {MaximumBytes / 1024} KiB", meta.Source, meta.Url);

            var bytes = File.ReadAllBytes(path);
            if (!PlayerSkin.TryBuild(bytes, Path.GetFileName(path), meta.Arms, exactSize: true,
                    out var skin, out var why))
                return new Entry(name, path, meta.Arms ?? ArmStyle.Classic, false, false, why,
                    meta.Source, meta.Url);

            var built = skin!;
            var kind = built.Legacy ? "legacy 64x32" : "modern 64x64";
            kind += $", {(built.Arms == ArmStyle.Slim ? "slim" : "classic")} arms";
            return new Entry(name, path, built.Arms, built.Legacy, true, kind, meta.Source, meta.Url);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new Entry(name, path, meta.Arms ?? ArmStyle.Classic, false, false,
                $"could not read it: {error.Message}", meta.Source, meta.Url);
        }
    }

    private static string SafeName(string suggested)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var kept = new string((suggested ?? "skin").Trim()
            .Where(c => !invalid.Contains(c) && c is not '\r' and not '\n').Take(72).ToArray());
        kept = kept.Trim(' ', '.');
        return kept.Length == 0 ? "skin" : kept;
    }

    private static string CollisionPath(string folder, string name)
    {
        var path = Path.Combine(folder, name + ".png");
        if (!File.Exists(path) && !File.Exists(MetadataPath(path))) return path;

        for (var copy = 2; copy < 10_000; copy++)
        {
            path = Path.Combine(folder, $"{name} ({copy}).png");
            if (!File.Exists(path) && !File.Exists(MetadataPath(path))) return path;
        }

        throw new IOException("too many skins with the same name");
    }

    private readonly record struct Metadata(ArmStyle? Arms, string Source, string Url);

    private static string MetadataPath(string png) => Path.ChangeExtension(png, ".skin");

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static Metadata ReadMetadata(string png)
    {
        ArmStyle? arms = null;
        var source = "installed skin";
        var url = "";

        try
        {
            var path = MetadataPath(png);
            if (!File.Exists(path)) return new Metadata(arms, source, url);
            if (new FileInfo(path).Length > MaximumMetadataBytes)
                return new Metadata(arms, source, url);

            foreach (var raw in File.ReadAllLines(path))
            {
                var split = raw.IndexOf('=');
                if (split <= 0) continue;
                var key = raw[..split].Trim().ToLowerInvariant();
                var value = raw[(split + 1)..].Trim();
                if (key == "model" && Enum.TryParse<ArmStyle>(value, true, out var parsed)
                    && Enum.IsDefined(parsed)) arms = parsed;
                else if (key == "source") source = new string(value.Take(256).ToArray());
                else if (key == "url") url = new string(value.Take(2_048).ToArray());
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

        return new Metadata(arms, source, url);
    }

    private static void WriteMetadata(string png, ArmStyle arms, string source, string url)
    {
        static string OneLine(string text, int maximum) => new string(text
            .Replace('\r', ' ').Replace('\n', ' ').Trim().Take(maximum).ToArray());

        var path = MetadataPath(png);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            File.WriteAllLines(temporary,
            [
                $"model={arms.ToString().ToLowerInvariant()}",
                $"source={OneLine(source, 256)}",
                $"url={OneLine(url, 2_048)}",
            ]);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }
}
