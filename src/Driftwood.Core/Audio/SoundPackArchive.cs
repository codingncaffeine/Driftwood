using System.IO.Compression;
using Driftwood.Core.Entities;

namespace Driftwood.Core.Audio;

/// <summary>The useful part of a Minecraft sound-pack archive, indexed without extracting it.</summary>
public sealed record SoundPackInspection(
    int Clips,
    int Covered,
    int Required,
    long ExpandedBytes,
    IReadOnlyDictionary<string, string> Entries);

/// <summary>
/// Opens a downloaded resource pack far enough to prove it is bounded and to map its sounds onto
/// Driftwood's sound names. The original ZIP remains intact on the sound-pack shelf.
/// </summary>
public static class SoundPackArchive
{
    // Large, audio-heavy packs routinely pass 128 MiB. The archive is never held in memory and
    // never extracted wholesale, so the useful outer bound here is a disk/network guard rather
    // than the ZIP-bomb guard. Entry count, individual sound size and expanded sound bytes remain
    // separately bounded below.
    public const long MaximumArchiveBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumClipBytes = 32L * 1024 * 1024;
    public const long MaximumExpandedBytes = 512L * 1024 * 1024;
    public const int MaximumArchiveEntries = 8_192;

    /// <summary>Every file name the game can currently ask a pack to provide.</summary>
    public static IReadOnlySet<string> RequiredNames { get; } = BuildRequiredNames();

    public static SoundPackInspection Inspect(string path, bool requireSounds = true)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("no sound-pack path was given");
        if (Directory.Exists(path)) return InspectFolder(path, requireSounds);
        if (!File.Exists(path)) throw new FileNotFoundException("the sound pack is no longer on disk", path);

        var length = new FileInfo(path).Length;
        if (length <= 0) throw new InvalidDataException("the sound pack is empty");
        if (length > MaximumArchiveBytes)
            throw new InvalidDataException($"the sound pack is larger than {DescribeBytes(MaximumArchiveBytes)}");

        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > MaximumArchiveEntries)
            throw new InvalidDataException($"the sound pack contains more than {MaximumArchiveEntries:N0} files");

        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;

        foreach (var entry in archive.Entries)
        {
            ValidateArchiveName(entry.FullName);

            var key = KeyOf(entry.FullName);
            if (key is null) continue;
            if (entry.Length <= 0) throw new InvalidDataException($"'{entry.FullName}' is an empty sound");
            if (entry.Length > MaximumClipBytes)
                throw new InvalidDataException(
                    $"'{entry.FullName}' is larger than {MaximumClipBytes / 1024 / 1024} MiB");

            expanded += entry.Length;
            if (expanded > MaximumExpandedBytes)
                throw new InvalidDataException(
                    $"the sounds expand past {MaximumExpandedBytes / 1024 / 1024} MiB");

            if (!entries.TryAdd(key, entry.FullName))
                throw new InvalidDataException($"the sound pack provides '{key}' more than once");
        }

        if (entries.Count == 0 && requireSounds)
            throw new InvalidDataException("no sounds were found under assets/minecraft/sounds");

        var covered = entries.Keys.Count(RequiredNames.Contains);
        return new SoundPackInspection(entries.Count, covered, RequiredNames.Count, expanded, entries);
    }

    /// <summary>
    /// The same bounded sound walk for an unpacked texture pack. Folder packs are a supported shape
    /// on the texture shelf, so their standard audio overrides must not disappear merely because
    /// there is no ZIP central directory to index.
    /// </summary>
    private static SoundPackInspection InspectFolder(string root, bool requireSounds)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        var files = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };

        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            files++;
            if (files > MaximumArchiveEntries)
                throw new InvalidDataException($"the sound pack contains more than {MaximumArchiveEntries:N0} files");

            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            ValidateArchiveName(relative);
            var key = KeyOf(relative);
            if (key is null) continue;

            var length = new FileInfo(path).Length;
            if (length <= 0) throw new InvalidDataException($"'{relative}' is an empty sound");
            if (length > MaximumClipBytes)
                throw new InvalidDataException(
                    $"'{relative}' is larger than {MaximumClipBytes / 1024 / 1024} MiB");

            expanded += length;
            if (expanded > MaximumExpandedBytes)
                throw new InvalidDataException(
                    $"the sounds expand past {MaximumExpandedBytes / 1024 / 1024} MiB");

            if (!entries.TryAdd(key, relative))
                throw new InvalidDataException($"the sound pack provides '{key}' more than once");
        }

        if (entries.Count == 0 && requireSounds)
            throw new InvalidDataException("no sounds were found under assets/minecraft/sounds");

        var covered = entries.Keys.Count(RequiredNames.Contains);
        return new SoundPackInspection(entries.Count, covered, RequiredNames.Count, expanded, entries);
    }

    /// <summary>A compact binary size for the sound-pack browser and its safety messages.</summary>
    public static string DescribeBytes(long bytes)
    {
        const double KiB = 1024;
        const double MiB = KiB * 1024;
        const double GiB = MiB * 1024;
        return bytes switch
        {
            >= (long)GiB => $"{bytes / GiB:0.#} GiB",
            >= (long)MiB => $"{bytes / MiB:0.#} MiB",
            >= (long)KiB => $"{bytes / KiB:0.#} KiB",
            _ => $"{Math.Max(0, bytes):N0} B",
        };
    }

    /// <summary>Reads one already-indexed entry with the same bounds used at installation.</summary>
    public static byte[] Read(string archivePath, string entryName)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry(entryName)
            ?? throw new InvalidDataException($"'{entryName}' disappeared from the sound pack");

        if (entry.Length <= 0 || entry.Length > MaximumClipBytes)
            throw new InvalidDataException($"'{entryName}' has an unsafe uncompressed size");

        using var source = entry.Open();
        using var destination = new MemoryStream((int)entry.Length);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = source.Read(buffer);
            if (read == 0) break;

            // Do not trust the central directory alone. A deliberately inconsistent ZIP must not
            // turn its advertised small entry into an unbounded allocation while it is inflated.
            if (destination.Length + read > entry.Length
                || destination.Length + read > MaximumClipBytes)
                throw new InvalidDataException($"'{entryName}' expanded past its advertised size");
            destination.Write(buffer, 0, read);
        }

        if (destination.Length != entry.Length)
            throw new InvalidDataException($"'{entryName}' ended before its advertised size");
        return destination.ToArray();
    }

    private static string? KeyOf(string fullName)
    {
        var name = fullName.Replace('\\', '/');
        const string root = "assets/minecraft/sounds/";

        var at = name.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? 0
            : name.IndexOf("/" + root, StringComparison.OrdinalIgnoreCase) is var nested && nested >= 0
                ? nested + 1
                : -1;

        if (at < 0) return null;

        var relative = name[(at + root.Length)..];
        var extension = Path.GetExtension(relative);
        if (!extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)) return null;

        var key = relative[..^extension.Length].Trim('/');
        return key.Length == 0 ? null : key;
    }

    private static void ValidateArchiveName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 1_024 || name.Any(char.IsControl))
            throw new InvalidDataException("the sound pack contains an unsafe file name");

        var normal = name.Replace('\\', '/');
        if (normal.StartsWith('/') || normal.Split('/').Any(part => part is "." or ".."))
            throw new InvalidDataException($"the sound pack contains an unsafe path ('{name}')");
    }

    private static IReadOnlySet<string> BuildRequiredNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        names.UnionWith(MaterialSounds.AllNames());
        names.UnionWith(CreatureSounds.All);
        names.UnionWith(ActionSounds.AllOneShots);
        names.UnionWith(ActionSounds.Ambience);
        return names;
    }
}
