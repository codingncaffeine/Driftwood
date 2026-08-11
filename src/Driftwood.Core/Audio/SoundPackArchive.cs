using System.IO.Compression;
using Driftwood.Core.Entities;
using Driftwood.Core.Textures;

namespace Driftwood.Core.Audio;

/// <summary>The useful part of a Minecraft sound-pack archive, indexed without extracting it.</summary>
public sealed record SoundPackInspection(
    int Clips,
    int Covered,
    int Required,
    long ExpandedBytes,
    IReadOnlyDictionary<string, string> Entries,
    IReadOnlyDictionary<string, IReadOnlyList<WeightedSoundEntry>>? Variants = null);

/// <summary>One physical recording selected by a standard Java sound event.</summary>
public readonly record struct WeightedSoundEntry(string Entry, int Weight);

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
    public const int MaximumSoundEntries = 8_192;

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
        if (archive.Entries.Count > TexturePack.MaxEntries)
            throw new InvalidDataException(
                $"the resource pack contains more than {TexturePack.MaxEntries:N0} files");

        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var definitions = new List<SoundsJson.Document>();
        long expanded = 0;
        var soundEntries = 0;

        foreach (var entry in archive.Entries)
        {
            ValidateArchiveName(entry.FullName);

            if (SoundsJson.TryDocument(entry.FullName, out var soundNamespace))
            {
                GuardSoundEntries(++soundEntries);
                definitions.Add(new SoundsJson.Document(
                    soundNamespace, ReadBounded(entry, SoundsJson.MaximumBytes)));
                continue;
            }

            var key = KeyOf(entry.FullName);
            var resource = ResourceIdOf(entry.FullName);
            if (resource is null) continue;
            if (entry.Length <= 0) continue;
            GuardSoundEntries(++soundEntries);
            if (entry.Length > MaximumClipBytes)
                throw new InvalidDataException(
                    $"'{entry.FullName}' is larger than {MaximumClipBytes / 1024 / 1024} MiB");
            ValidateRecordingHeader(entry.FullName, ReadHeader(entry));

            expanded += entry.Length;
            if (expanded > MaximumExpandedBytes)
                throw new InvalidDataException(
                    $"the sounds expand past {MaximumExpandedBytes / 1024 / 1024} MiB");

            if (!resources.TryAdd(resource, entry.FullName))
                throw new InvalidDataException($"the sound pack provides '{resource}' more than once");
            if (key is not null && !entries.TryAdd(key, entry.FullName))
                throw new InvalidDataException($"the sound pack provides '{key}' more than once");
        }

        if (resources.Count == 0 && requireSounds)
            throw new InvalidDataException("no sounds were found under assets/minecraft/sounds");

        var variants = SoundsJson.Resolve(definitions, resources);
        var covered = RequiredNames.Count(name => entries.ContainsKey(name) || variants.ContainsKey(name));
        return new SoundPackInspection(resources.Count, covered, RequiredNames.Count, expanded, entries, variants);
    }

    /// <summary>
    /// The same bounded sound walk for an unpacked texture pack. Folder packs are a supported shape
    /// on the texture shelf, so their standard audio overrides must not disappear merely because
    /// there is no ZIP central directory to index.
    /// </summary>
    private static SoundPackInspection InspectFolder(string root, bool requireSounds)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var definitions = new List<SoundsJson.Document>();
        long expanded = 0;
        var files = 0;
        var soundEntries = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };

        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            files++;
            if (files > TexturePack.MaxEntries)
                throw new InvalidDataException(
                    $"the resource pack contains more than {TexturePack.MaxEntries:N0} files");

            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            ValidateArchiveName(relative);
            if (SoundsJson.TryDocument(relative, out var soundNamespace))
            {
                GuardSoundEntries(++soundEntries);
                var jsonLength = new FileInfo(path).Length;
                if (jsonLength > SoundsJson.MaximumBytes)
                    throw new InvalidDataException($"'{relative}' is too large to be a sounds.json file");
                definitions.Add(new SoundsJson.Document(soundNamespace, File.ReadAllBytes(path)));
                continue;
            }

            var key = KeyOf(relative);
            var resource = ResourceIdOf(relative);
            if (resource is null) continue;

            var length = new FileInfo(path).Length;
            if (length <= 0) continue;
            GuardSoundEntries(++soundEntries);
            if (length > MaximumClipBytes)
                throw new InvalidDataException(
                    $"'{relative}' is larger than {MaximumClipBytes / 1024 / 1024} MiB");
            using (var stream = File.OpenRead(path))
                ValidateRecordingHeader(relative, ReadHeader(stream));

            expanded += length;
            if (expanded > MaximumExpandedBytes)
                throw new InvalidDataException(
                    $"the sounds expand past {MaximumExpandedBytes / 1024 / 1024} MiB");

            if (!resources.TryAdd(resource, relative))
                throw new InvalidDataException($"the sound pack provides '{resource}' more than once");
            if (key is not null && !entries.TryAdd(key, relative))
                throw new InvalidDataException($"the sound pack provides '{key}' more than once");
        }

        if (resources.Count == 0 && requireSounds)
            throw new InvalidDataException("no sounds were found under assets/minecraft/sounds");

        var variants = SoundsJson.Resolve(definitions, resources);
        var covered = RequiredNames.Count(name => entries.ContainsKey(name) || variants.ContainsKey(name));
        return new SoundPackInspection(resources.Count, covered, RequiredNames.Count, expanded, entries, variants);
    }

    private static void GuardSoundEntries(int count)
    {
        if (count > MaximumSoundEntries)
            throw new InvalidDataException(
                $"the pack contains more than {MaximumSoundEntries:N0} sound files and definitions");
    }

    private static byte[] ReadHeader(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return ReadHeader(stream);
    }

    private static byte[] ReadHeader(Stream stream)
    {
        var header = new byte[12];
        var total = 0;
        while (total < header.Length)
        {
            var read = stream.Read(header, total, header.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total == header.Length ? header : header[..total];
    }

    private static void ValidateRecordingHeader(string name, ReadOnlySpan<byte> header)
    {
        var extension = Path.GetExtension(name);
        if (extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            if (header.Length < 5 || !header[..4].SequenceEqual("OggS"u8) || header[4] != 0)
                throw new InvalidDataException($"'{name}' is not an Ogg bitstream");
            return;
        }

        if (header.Length < 12 || !header[..4].SequenceEqual("RIFF"u8)
            || !header.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException($"'{name}' is not a RIFF WAVE file");
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
        var resource = ResourceIdOf(fullName);
        if (resource is null || !resource.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase))
            return null;
        var relative = resource["minecraft:".Length..];
        return relative.Length == 0 ? null : relative;
    }

    private static string? ResourceIdOf(string fullName)
    {
        var name = fullName.Replace('\\', '/').Trim('/');
        var segments = name.Split('/');
        var assets = Array.FindIndex(segments, part => part.Equals("assets", StringComparison.OrdinalIgnoreCase));
        if (assets < 0 || assets + 3 >= segments.Length
            || !segments[assets + 2].Equals("sounds", StringComparison.OrdinalIgnoreCase)) return null;

        var soundNamespace = segments[assets + 1].ToLowerInvariant();
        var relative = string.Join('/', segments[(assets + 3)..]);
        var extension = Path.GetExtension(relative);
        if (!extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)) return null;

        var key = relative[..^extension.Length].Trim('/').ToLowerInvariant();
        return key.Length == 0 ? null : $"{soundNamespace}:{key}";
    }

    private static byte[] ReadBounded(ZipArchiveEntry entry, int maximum)
    {
        if (entry.Length < 0 || entry.Length > maximum)
            throw new InvalidDataException($"'{entry.FullName}' is too large to be a sounds.json file");
        using var source = entry.Open();
        using var destination = new MemoryStream((int)entry.Length);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = source.Read(buffer);
            if (read == 0) break;
            if (destination.Length + read > maximum || destination.Length + read > entry.Length)
                throw new InvalidDataException($"'{entry.FullName}' expanded past its advertised size");
            destination.Write(buffer, 0, read);
        }
        if (destination.Length != entry.Length)
            throw new InvalidDataException($"'{entry.FullName}' ended before its advertised size");
        return destination.ToArray();
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
