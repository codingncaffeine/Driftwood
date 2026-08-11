using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Driftwood.Core.Textures;

/// <summary>The installed texture-pack shelf and its Driftwood-owned metadata.</summary>
/// <remarks>
/// Packs are copied into one durable shelf rather than remembered at an arbitrary download path.
/// A small sidecar avoids reopening every archive whenever a large shelf is drawn. The sidecar is
/// disposable cache/provenance: packs copied into the folder by hand remain first-class entries.
/// </remarks>
public static class PackLibrary
{
    private const int MaximumSidecarBytes = 4 * 1024 * 1024;
    public static IReadOnlyList<string> Extensions { get; } = TexturePack.Extensions;
    public static string FilterLabel => $"texture packs ({string.Join(", ", Extensions)})";
    public static string FilterSpec => string.Join(";", Extensions.Select(static extension => $"*{extension}"));

    public enum SortOrder
    {
        Name,
        RecentlyAdded,
        Source,
    }

    /// <summary>Provenance supplied by a catalog download. Every field is optional for local packs.</summary>
    public sealed record Provenance(
        string Provider = "",
        string ProjectId = "",
        string VersionId = "",
        string Version = "",
        string Author = "",
        string Source = "",
        string License = "",
        string Sha512 = "",
        IReadOnlyList<PackDependency>? Dependencies = null,
        string Title = "",
        string Description = "");

    public sealed record PackDependency(
        string Type,
        string ProjectId = "",
        string VersionId = "",
        string FileName = "");

    /// <summary>One pack on the shelf. Additional fields are cached, never prerequisites to listing.</summary>
    public readonly record struct Entry(
        string Name,
        string Path,
        string Kind,
        bool Readable,
        string Title = "",
        string Description = "",
        string Author = "",
        string Source = "",
        string License = "",
        string Dialect = "",
        int Resolution = 0,
        long ArchiveBytes = 0,
        DateTimeOffset Installed = default,
        string Provider = "",
        string ProjectId = "",
        string VersionId = "",
        string Version = "",
        string Sha512 = "",
        bool UpdateAvailable = false,
        string Compatibility = "NOT CHECKED",
        byte[]? Icon = null,
        IReadOnlyList<PackDependency>? Dependencies = null)
    {
        public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Name : Title;
        public IReadOnlyList<PackDependency> PackDependencies => Dependencies ?? [];
    }

    private sealed class Sidecar
    {
        public int Schema { get; set; } = 1;
        public long SourceBytes { get; set; }
        public long SourceWriteUtcTicks { get; set; }
        public bool Readable { get; set; }
        public string Kind { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Author { get; set; } = "";
        public string Source { get; set; } = "";
        public string License { get; set; } = "";
        public string Dialect { get; set; } = "";
        public int Resolution { get; set; }
        public long ArchiveBytes { get; set; }
        public DateTimeOffset Installed { get; set; }
        public string Provider { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string VersionId { get; set; } = "";
        public string Version { get; set; } = "";
        public string Sha512 { get; set; } = "";
        public bool UpdateAvailable { get; set; }
        public string Compatibility { get; set; } = "NOT CHECKED";
        public string IconBase64 { get; set; } = "";
        public List<PackDependency> Dependencies { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public static string Folder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Driftwood", "packs");

    /// <summary>Path of the disposable Driftwood metadata stored beside a pack.</summary>
    public static string MetadataPath(string packPath) => $"{packPath}.driftwood.json";

    /// <summary>Everything on a shelf, ordered stably. A custom shelf keeps audits out of AppData.</summary>
    public static IReadOnlyList<Entry> List(string? shelf = null)
    {
        shelf = ResolveShelf(shelf);
        var found = new List<Entry>();
        if (!Directory.Exists(shelf)) return found;

        foreach (var path in Directory.EnumerateFileSystemEntries(shelf))
        {
            if (path.EndsWith(".driftwood.json", StringComparison.OrdinalIgnoreCase)
                || System.IO.Path.GetFileName(path).StartsWith('.', StringComparison.Ordinal)) continue;

            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name.Length == 0) continue;

            var isFolder = Directory.Exists(path);
            if (!isFolder && !Extensions.Contains(
                    System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;

            found.Add(Describe(name, path));
        }

        found.Sort(CompareByName);
        return found;
    }

    /// <summary>Searches cached card metadata and pins the worn pack ahead of the chosen order.</summary>
    public static IReadOnlyList<Entry> Query(
        IEnumerable<Entry> entries,
        string? search = null,
        SortOrder sort = SortOrder.Name,
        string? worn = null)
    {
        var words = (search ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var filtered = entries.Where(entry => words.All(word => SearchText(entry).Contains(
            word, StringComparison.OrdinalIgnoreCase)));

        IOrderedEnumerable<Entry> ordered = sort switch
        {
            SortOrder.RecentlyAdded => filtered.OrderByDescending(static entry => entry.Installed)
                .ThenBy(static entry => entry.DisplayTitle, StringComparer.OrdinalIgnoreCase),
            SortOrder.Source => filtered.OrderBy(static entry => entry.Provider.Length > 0
                    ? entry.Provider : entry.Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.DisplayTitle, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(static entry => entry.DisplayTitle, StringComparer.OrdinalIgnoreCase),
        };

        return ordered
            .ThenBy(static entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(entry => IsWorn(entry, worn))
            .ToArray();
    }

    public static string? PathOf(string name, string? shelf = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return List(shelf).FirstOrDefault(entry => string.Equals(
            entry.Name, name, StringComparison.OrdinalIgnoreCase)).Path is { Length: > 0 } path ? path : null;
    }

    /// <summary>
    /// Validates and atomically copies a pack into the shelf. A failed replacement restores the old
    /// pack and its sidecar; a staging or backup path is never returned as an installed entry.
    /// </summary>
    public static Entry? Install(
        string from,
        out string why,
        string? shelf = null,
        Provenance? provenance = null,
        string? installName = null)
    {
        why = "";
        shelf = ResolveShelf(shelf);

        if (string.IsNullOrWhiteSpace(from))
        {
            why = "no path given";
            return null;
        }

        from = from.Trim().Trim('"');
        var isFolder = Directory.Exists(from);
        if (!isFolder && !File.Exists(from))
        {
            why = "there is nothing at that path";
            return null;
        }

        if (!isFolder && !Extensions.Contains(
                System.IO.Path.GetExtension(from), StringComparer.OrdinalIgnoreCase))
        {
            TexturePack.Open(from, out var refused)?.Dispose();
            why = refused ?? $"a pack is a folder or {string.Join(", ", Extensions)}";
            return null;
        }

        var sourceName = string.IsNullOrWhiteSpace(installName)
            ? System.IO.Path.GetFileName(from.TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
            : System.IO.Path.GetFileName(installName);
        var probe = Describe(System.IO.Path.GetFileNameWithoutExtension(sourceName), from, useCache: false);
        if (!probe.Readable)
        {
            why = probe.Kind;
            return null;
        }

        string? stage = null;
        string? backup = null;
        string? sidecarBackup = null;
        try
        {
            Directory.CreateDirectory(shelf);
            var landing = SafeLanding(shelf, sourceName);

            if (string.Equals(System.IO.Path.GetFullPath(landing), System.IO.Path.GetFullPath(from),
                    StringComparison.OrdinalIgnoreCase))
            {
                var current = Describe(System.IO.Path.GetFileNameWithoutExtension(landing), landing);
                if (provenance is not null) SaveMetadata(current, provenance, installed: current.Installed);
                return Describe(current.Name, landing);
            }

            var token = Guid.NewGuid().ToString("N");
            stage = System.IO.Path.Combine(shelf, $".{sourceName}.{token}.staging");
            if (isFolder) CopyFolder(from, stage);
            else File.Copy(from, stage);

            // Open the exact staged bytes. This catches a truncated copy and keeps a bad landing out
            // of the visible shelf even if the source changed between the first probe and the copy.
            var staged = Describe(probe.Name, stage, useCache: false, extensionHint: System.IO.Path.GetExtension(from));
            if (!staged.Readable) throw new InvalidDataException(staged.Kind);

            backup = $"{landing}.{token}.backup";
            var sidecar = MetadataPath(landing);
            sidecarBackup = $"{sidecar}.{token}.backup";
            var movedLanding = false;
            var movedSidecar = false;
            var stageLanded = false;
            try
            {
                if (Directory.Exists(landing) || File.Exists(landing))
                {
                    MoveIfPresent(landing, backup);
                    movedLanding = true;
                }
                if (File.Exists(sidecar))
                {
                    File.Move(sidecar, sidecarBackup);
                    movedSidecar = true;
                }
                Move(stage, landing);
                stageLanded = true;
                stage = null;
                var installed = Describe(System.IO.Path.GetFileNameWithoutExtension(landing), landing,
                    useCache: false);
                SaveMetadata(installed, provenance, DateTimeOffset.UtcNow);
            }
            catch
            {
                if (stageLanded) DeleteIfPresent(landing);
                if (movedLanding)
                {
                    MoveIfPresent(backup, landing);
                    backup = null;
                }
                if (movedSidecar)
                {
                    if (File.Exists(sidecar)) File.Delete(sidecar);
                    if (File.Exists(sidecarBackup)) File.Move(sidecarBackup, sidecar);
                    sidecarBackup = null;
                }
                throw;
            }

            DeleteIfPresent(backup);
            backup = null;
            if (File.Exists(sidecarBackup)) File.Delete(sidecarBackup);
            sidecarBackup = null;
            return Describe(System.IO.Path.GetFileNameWithoutExtension(landing), landing);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            why = error.Message;
            return null;
        }
        finally
        {
            if (stage is not null) DeleteIfPresent(stage);
            // A backup surviving a failed restore is recoverable evidence. Never erase it merely
            // because cleanup ran; successful installs null/delete both paths above.
        }
    }

    public static bool Remove(string name, string? shelf = null)
    {
        var path = PathOf(name, shelf);
        return path is null || RemovePath(path, shelf);
    }

    /// <summary>Removes one exact card, which makes duplicate display names unambiguous.</summary>
    public static bool RemovePath(string path, string? shelf = null)
    {
        try
        {
            shelf = ResolveShelf(shelf);
            var full = System.IO.Path.GetFullPath(path);
            var root = System.IO.Path.GetFullPath(shelf);
            if (!string.Equals(Directory.GetParent(full)?.FullName, root,
                    StringComparison.OrdinalIgnoreCase)) return false;

            DeleteIfPresent(full);
            var sidecar = MetadataPath(full);
            if (File.Exists(sidecar)) File.Delete(sidecar);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Updates provider/update/compatibility metadata without touching the pack bytes.</summary>
    public static bool UpdateMetadata(
        string path,
        string? compatibility = null,
        bool? updateAvailable = null,
        Provenance? provenance = null)
    {
        try
        {
            var entry = Describe(System.IO.Path.GetFileNameWithoutExtension(path), path);
            if (entry.Path.Length == 0) return false;
            var sidecar = ReadSidecar(path) ?? FromEntry(entry);
            Apply(sidecar, provenance);
            if (compatibility is not null) sidecar.Compatibility = compatibility;
            if (updateAvailable.HasValue) sidecar.UpdateAvailable = updateAvailable.Value;
            WriteSidecar(path, sidecar);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>SHA-512 of exact archive bytes, or a canonical path/content walk for a folder pack.</summary>
    public static string Fingerprint(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[128 * 1024];
            for (var read = stream.Read(buffer); read > 0; read = stream.Read(buffer))
                hash.AppendData(buffer, 0, read);
        }
        else if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", FolderEnumeration())
                         .OrderBy(file => System.IO.Path.GetRelativePath(path, file), StringComparer.Ordinal))
            {
                var relative = System.IO.Path.GetRelativePath(path, file).Replace('\\', '/');
                hash.AppendData(Encoding.UTF8.GetBytes(relative));
                hash.AppendData([0]);
                using var stream = File.OpenRead(file);
                var buffer = new byte[128 * 1024];
                for (var read = stream.Read(buffer); read > 0; read = stream.Read(buffer))
                    hash.AppendData(buffer, 0, read);
            }
        }
        else
        {
            throw new FileNotFoundException("there is no pack to fingerprint", path);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static Entry Describe(
        string name,
        string path,
        bool useCache = true,
        string extensionHint = "")
    {
        var stamp = Stamp(path);
        if (useCache && ReadSidecar(path) is { } cached
            && cached.SourceBytes == stamp.Bytes
            && cached.SourceWriteUtcTicks == stamp.WriteUtcTicks)
            return ToEntry(name, path, cached);

        try
        {
            // Staging names deliberately end in .staging. Open under a temporary hard copy with the
            // source extension only for validation; the installed landing always has its real name.
            var openedPath = path;
            string? probeCopy = null;
            if (File.Exists(path) && !Extensions.Contains(
                    System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                && extensionHint.Length > 0)
            {
                probeCopy = $"{path}{extensionHint}";
                File.Copy(path, probeCopy, overwrite: true);
                openedPath = probeCopy;
            }

            try
            {
                using var pack = TexturePack.Open(openedPath, out var why);
                if (pack is null) return Cache(new Entry(name, path, why ?? "not a texture pack", false), stamp);
                if (!pack.WithinSafetyBounds(out var unsafeWhy))
                    return Cache(new Entry(name, path, unsafeWhy, false), stamp);
                if (pack.Dialect is not (PackDialect.Java or PackDialect.JavaLegacy
                                         or PackDialect.Bedrock or PackDialect.Atlas))
                    return Cache(new Entry(name, path, Unrecognised(pack), false), stamp);

                var size = pack.DetectResolution();
                var dialect = pack.Dialect switch
                {
                    PackDialect.Java => "Java",
                    PackDialect.JavaLegacy => "Java, pre-flattening",
                    PackDialect.Atlas => "pre-2013 terrain.png",
                    _ => "Bedrock",
                };
                byte[]? icon;
                try
                {
                    icon = pack.TryReadRootBytes(
                        pack.Dialect == PackDialect.Bedrock ? "pack_icon.png" : "pack.png",
                        2 * 1024 * 1024);
                }
                catch (InvalidDataException) { icon = null; }

                return Cache(new Entry(
                    name,
                    path,
                    $"{dialect}, {size}px",
                    true,
                    name,
                    pack.Description,
                    Dialect: dialect,
                    Resolution: size,
                    ArchiveBytes: stamp.Bytes,
                    Installed: InstalledTime(path),
                    Icon: icon), stamp);
            }
            finally
            {
                if (probeCopy is not null && File.Exists(probeCopy)) File.Delete(probeCopy);
            }
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return Cache(new Entry(name, path, error.Message, false, ArchiveBytes: stamp.Bytes,
                Installed: InstalledTime(path)), stamp);
        }

        Entry Cache(Entry entry, (long Bytes, long WriteUtcTicks) sourceStamp)
        {
            if (!useCache) return entry;
            try
            {
                var sidecar = FromEntry(entry);
                sidecar.SourceBytes = sourceStamp.Bytes;
                sidecar.SourceWriteUtcTicks = sourceStamp.WriteUtcTicks;
                WriteSidecar(path, sidecar);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return entry;
        }
    }

    private static void SaveMetadata(Entry entry, Provenance? provenance, DateTimeOffset installed)
    {
        var sidecar = FromEntry(entry);
        sidecar.Installed = installed == default ? DateTimeOffset.UtcNow : installed;
        Apply(sidecar, provenance);
        var stamp = Stamp(entry.Path);
        sidecar.SourceBytes = stamp.Bytes;
        sidecar.SourceWriteUtcTicks = stamp.WriteUtcTicks;
        WriteSidecar(entry.Path, sidecar);
    }

    private static void Apply(Sidecar sidecar, Provenance? provenance)
    {
        if (provenance is null) return;
        sidecar.Provider = provenance.Provider;
        sidecar.ProjectId = provenance.ProjectId;
        sidecar.VersionId = provenance.VersionId;
        sidecar.Version = provenance.Version;
        sidecar.Author = provenance.Author;
        sidecar.Source = provenance.Source;
        sidecar.License = provenance.License;
        sidecar.Sha512 = provenance.Sha512;
        sidecar.Dependencies = provenance.Dependencies?.ToList() ?? [];
        if (!string.IsNullOrWhiteSpace(provenance.Title)) sidecar.Title = provenance.Title;
        if (!string.IsNullOrWhiteSpace(provenance.Description)) sidecar.Description = provenance.Description;
    }

    private static Sidecar FromEntry(Entry entry) => new()
    {
        Readable = entry.Readable,
        Kind = entry.Kind,
        Title = entry.DisplayTitle,
        Description = entry.Description,
        Author = entry.Author,
        Source = entry.Source,
        License = entry.License,
        Dialect = entry.Dialect,
        Resolution = entry.Resolution,
        ArchiveBytes = entry.ArchiveBytes,
        Installed = entry.Installed,
        Provider = entry.Provider,
        ProjectId = entry.ProjectId,
        VersionId = entry.VersionId,
        Version = entry.Version,
        Sha512 = entry.Sha512,
        UpdateAvailable = entry.UpdateAvailable,
        Compatibility = entry.Compatibility,
        IconBase64 = entry.Icon is { Length: > 0 } ? Convert.ToBase64String(entry.Icon) : "",
        Dependencies = entry.PackDependencies.ToList(),
    };

    private static Entry ToEntry(string name, string path, Sidecar sidecar)
    {
        byte[]? icon = null;
        if (sidecar.IconBase64.Length > 0)
        {
            try { icon = Convert.FromBase64String(sidecar.IconBase64); }
            catch (FormatException) { }
        }

        return new Entry(
            name, path, sidecar.Kind, sidecar.Readable, sidecar.Title, sidecar.Description,
            sidecar.Author, sidecar.Source, sidecar.License, sidecar.Dialect, sidecar.Resolution,
            sidecar.ArchiveBytes, sidecar.Installed, sidecar.Provider, sidecar.ProjectId,
            sidecar.VersionId, sidecar.Version, sidecar.Sha512, sidecar.UpdateAvailable,
            sidecar.Compatibility, icon, sidecar.Dependencies);
    }

    private static Sidecar? ReadSidecar(string path)
    {
        var sidecar = MetadataPath(path);
        if (!File.Exists(sidecar)) return null;
        try
        {
            using var stream = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 0 || stream.Length > MaximumSidecarBytes) return null;
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() >= 0) return null;
            return JsonSerializer.Deserialize<Sidecar>(bytes, Json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteSidecar(string path, Sidecar sidecar)
    {
        var destination = MetadataPath(path);
        var temporary = $"{destination}.{Guid.NewGuid():N}.part";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(sidecar, Json));
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static (long Bytes, long WriteUtcTicks) Stamp(string path)
    {
        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            return (file.Length, file.LastWriteTimeUtc.Ticks);
        }

        long bytes = 0, ticks = Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path).Ticks : 0;
        if (Directory.Exists(path))
        {
            foreach (var filePath in Directory.EnumerateFiles(path, "*", FolderEnumeration()))
            {
                var file = new FileInfo(filePath);
                bytes += file.Length;
                ticks = Math.Max(ticks, file.LastWriteTimeUtc.Ticks);
            }
        }
        return (bytes, ticks);
    }

    private static DateTimeOffset InstalledTime(string path)
    {
        if (File.Exists(path)) return new DateTimeOffset(File.GetCreationTimeUtc(path), TimeSpan.Zero);
        if (Directory.Exists(path)) return new DateTimeOffset(Directory.GetCreationTimeUtc(path), TimeSpan.Zero);
        return DateTimeOffset.UtcNow;
    }

    private static string SearchText(Entry entry) => string.Join(' ',
        entry.Name, entry.Title, entry.Author, entry.Source, entry.Provider);

    private static bool IsWorn(Entry entry, string? worn) => worn is { Length: > 0 }
        && string.Equals(entry.Name, worn, StringComparison.OrdinalIgnoreCase);

    private static int CompareByName(Entry left, Entry right)
    {
        var byName = string.Compare(left.DisplayTitle, right.DisplayTitle, StringComparison.OrdinalIgnoreCase);
        return byName != 0 ? byName : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveShelf(string? shelf) => System.IO.Path.GetFullPath(
        string.IsNullOrWhiteSpace(shelf) ? Folder : shelf);

    private static string SafeLanding(string shelf, string suppliedName)
    {
        var name = System.IO.Path.GetFileName(suppliedName);
        if (name.Length == 0 || name is "." or "..") throw new InvalidDataException("the pack has no safe filename");
        var landing = System.IO.Path.GetFullPath(System.IO.Path.Combine(shelf, name));
        var root = System.IO.Path.GetFullPath(shelf) + System.IO.Path.DirectorySeparatorChar;
        if (!landing.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("the pack filename escapes the shelf");
        return landing;
    }

    private static string Unrecognised(TexturePack pack) => pack.Has("terrain.png")
        ? "a pre-2013 terrain.png pack whose grid is not where it should be"
        : "the layout is not one we know: no assets/, no textures/, no terrain.png";

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", FolderEnumeration()))
        {
            var relative = System.IO.Path.GetRelativePath(from, file);
            if (relative.Split(System.IO.Path.DirectorySeparatorChar).Any(part => part is "." or ".."))
                throw new InvalidDataException("a folder entry escapes the pack");
            var landing = System.IO.Path.Combine(to, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(landing)!);
            File.Copy(file, landing);
        }
    }

    private static EnumerationOptions FolderEnumeration() => new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
    };

    private static void Move(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    private static void MoveIfPresent(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else if (File.Exists(from)) File.Move(from, to);
    }

    private static void DeleteIfPresent(string? path)
    {
        if (path is null) return;
        if (Directory.Exists(path))
        {
            // A shelf can be edited by hand. Removing a junction/symlink must remove only that
            // shelf entry, never recurse into the external directory it happens to target.
            var reparse = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            Directory.Delete(path, recursive: !reparse);
        }
        else if (File.Exists(path)) File.Delete(path);
    }
}
