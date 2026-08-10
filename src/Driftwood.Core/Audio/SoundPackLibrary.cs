using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Driftwood.Core.Audio;

/// <summary>A player's durable shelf of original, unmodified sound-pack archives.</summary>
public sealed class SoundPackLibrary
{
    public static IReadOnlyList<string> Extensions { get; } = [".zip", ".mcpack"];
    public static string FilterLabel => $"sound packs ({string.Join(", ", Extensions)})";
    public static string FilterSpec => string.Join(";", Extensions.Select(static extension => $"*{extension}"));

    public sealed record Entry(
        string Id,
        string Name,
        string Path,
        string Author,
        string License,
        string Version,
        string SourceUrl,
        int Clips,
        int Covered,
        int Required,
        string Kind,
        bool Readable);

    private sealed class Metadata
    {
        public int Format { get; set; } = 1;
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Author { get; set; } = "";
        public string License { get; set; } = "";
        public string Version { get; set; } = "";
        public string SourceUrl { get; set; } = "";
        public string Provider { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string VersionId { get; set; } = "";
        public string Sha512 { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public static string DefaultFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Driftwood", "sound-packs");

    public string Folder { get; }

    public SoundPackLibrary(string? folder = null) => Folder = folder ?? DefaultFolder;

    public IReadOnlyList<Entry> List()
    {
        var found = new List<Entry>();
        if (!Directory.Exists(Folder)) return found;

        foreach (var path in Directory.EnumerateFiles(Folder))
        {
            if (!Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
            found.Add(Describe(path));
        }

        found.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    public Entry? Find(string id) => List().FirstOrDefault(
        entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));

    public string? PathOf(string id)
    {
        var entry = Find(id);
        return entry is { Readable: true } ? entry.Path : null;
    }

    /// <summary>Copies a local archive onto the shelf, preserving its bytes.</summary>
    public Entry? InstallLocal(string from, out string why)
    {
        why = "";
        from = from.Trim().Trim('"');

        if (!File.Exists(from))
        {
            why = "there is no file at that path";
            return null;
        }

        var extension = System.IO.Path.GetExtension(from);
        if (!Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            why = $"'{extension}' is not a sound pack; choose {string.Join(" or ", Extensions)}";
            return null;
        }

        try
        {
            var inspection = SoundPackArchive.Inspect(from);
            using var source = File.OpenRead(from);
            var digest = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
            var id = "local-" + digest[..12];
            var metadata = new Metadata
            {
                Id = id,
                Name = System.IO.Path.GetFileNameWithoutExtension(from),
                Author = "local file",
                License = "not supplied",
                Provider = "local",
            };

            return LandFile(from, id, extension.ToLowerInvariant(), metadata, inspection, out why);
        }
        catch (Exception error) when (IsPackError(error))
        {
            why = error.Message;
            return null;
        }
    }

    /// <summary>Verifies and stores a Modrinth download, with attribution beside the original ZIP.</summary>
    public Entry? Install(RemoteSoundPackFile remote, out string why)
    {
        why = "";

        try
        {
            if (remote.Remote.Id.Length is < 3 or > 64
                || !remote.Remote.Id.All(char.IsAsciiLetterOrDigit))
                throw new InvalidDataException("the downloaded sound pack has an invalid provider ID");

            if (remote.Encoded.LongLength <= 0 || remote.Encoded.LongLength > SoundPackArchive.MaximumArchiveBytes)
                throw new InvalidDataException("the downloaded sound pack has an unsafe size");

            var actual = Convert.ToHexString(SHA512.HashData(remote.Encoded)).ToLowerInvariant();
            if (!actual.Equals(remote.Sha512, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the downloaded sound pack did not match Modrinth's SHA-512");

            Directory.CreateDirectory(Folder);
            var id = "mr-" + remote.Remote.Id;
            var temporary = System.IO.Path.Combine(Folder, id + ".download");
            File.WriteAllBytes(temporary, remote.Encoded);

            try
            {
                var inspection = SoundPackArchive.Inspect(temporary);
                var metadata = new Metadata
                {
                    Id = id,
                    Name = remote.Remote.Name,
                    Author = remote.Remote.Author,
                    License = remote.Remote.License,
                    Version = remote.Version,
                    SourceUrl = remote.Remote.ProjectUri.ToString(),
                    Provider = "Modrinth",
                    ProjectId = remote.Remote.Id,
                    VersionId = remote.VersionId,
                    Sha512 = actual,
                };

                return LandTemporary(temporary, id, ".zip", metadata, inspection, out why);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
            }
        }
        catch (Exception error) when (IsPackError(error))
        {
            why = error.Message;
            return null;
        }
    }

    public bool Remove(string id)
    {
        var entry = Find(id);
        if (entry is null) return true;

        try
        {
            File.Delete(entry.Path);
            var metadata = MetadataPath(entry.Path);
            if (File.Exists(metadata)) File.Delete(metadata);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private Entry? LandFile(
        string from, string id, string extension, Metadata metadata,
        SoundPackInspection inspection, out string why)
    {
        Directory.CreateDirectory(Folder);
        var temporary = System.IO.Path.Combine(Folder, id + ".copying");

        try
        {
            File.Copy(from, temporary, overwrite: true);
            return LandTemporary(temporary, id, extension, metadata, inspection, out why);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }

    private Entry? LandTemporary(
        string temporary, string id, string extension, Metadata metadata,
        SoundPackInspection inspection, out string why)
    {
        why = "";
        var landing = System.IO.Path.Combine(Folder, id + extension);
        var metadataPath = MetadataPath(landing);
        var metadataNew = metadataPath + ".new";

        try
        {
            File.WriteAllText(metadataNew, JsonSerializer.Serialize(metadata, JsonOptions));
            File.Move(temporary, landing, overwrite: true);
            File.Move(metadataNew, metadataPath, overwrite: true);
            return EntryFrom(metadata, landing, inspection);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            why = error.Message;
            try { if (File.Exists(metadataNew)) File.Delete(metadataNew); }
            catch (IOException) { }
            return null;
        }
    }

    private Entry Describe(string path)
    {
        var id = System.IO.Path.GetFileNameWithoutExtension(path);
        var metadata = ReadMetadata(path) ?? new Metadata
        {
            Id = id,
            Name = id,
            Author = "unknown",
            License = "not supplied",
            Provider = "local",
        };

        metadata.Id = id;

        try
        {
            return EntryFrom(metadata, path, SoundPackArchive.Inspect(path));
        }
        catch (Exception error) when (IsPackError(error))
        {
            return new Entry(
                id, metadata.Name, path, metadata.Author, metadata.License, metadata.Version,
                metadata.SourceUrl, 0, 0, SoundPackArchive.RequiredNames.Count,
                error.Message, Readable: false);
        }
    }

    private static Entry EntryFrom(Metadata metadata, string path, SoundPackInspection inspection)
    {
        var kind = $"{inspection.Clips:N0} sounds; {inspection.Covered:N0} of "
                 + $"{inspection.Required:N0} Driftwood slots";
        return new Entry(
            metadata.Id,
            string.IsNullOrWhiteSpace(metadata.Name) ? metadata.Id : metadata.Name,
            path,
            string.IsNullOrWhiteSpace(metadata.Author) ? "unknown" : metadata.Author,
            string.IsNullOrWhiteSpace(metadata.License) ? "not supplied" : metadata.License,
            metadata.Version,
            metadata.SourceUrl,
            inspection.Clips,
            inspection.Covered,
            inspection.Required,
            kind,
            Readable: true);
    }

    private static Metadata? ReadMetadata(string archivePath)
    {
        var path = MetadataPath(archivePath);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return null;
            var metadata = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(path), JsonOptions);
            return metadata is { Format: 1 } ? metadata : null;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string MetadataPath(string archivePath) =>
        System.IO.Path.ChangeExtension(archivePath, ".json");

    private static bool IsPackError(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException
            or NotSupportedException;
}

/// <summary>Offline controls for ZIP bounds, sparse override, metadata, replacement and removal.</summary>
public static class SoundPackLibrarySelfTest
{
    public static List<string> Run(out string detail)
    {
        var faults = new List<string>();
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "driftwood-sound-shelf-" + Guid.NewGuid().ToString("N"));
        var incoming = System.IO.Path.Combine(root, "incoming");
        var shelfPath = System.IO.Path.Combine(root, "shelf");
        var packPath = System.IO.Path.Combine(incoming, "Tiny Sounds.zip");

        try
        {
            Directory.CreateDirectory(incoming);
            WritePack(packPath, unsafePath: false);
            var shelf = new SoundPackLibrary(shelfPath);
            var installed = shelf.InstallLocal(packPath, out var why);

            if (installed is null)
            {
                faults.Add($"a valid local sound pack was refused: {why}");
                detail = "the valid fixture was refused";
                return faults;
            }

            if (!installed.Readable || installed.Clips != 1 || installed.Covered != 1)
                faults.Add($"the valid pack was described as '{installed.Kind}'");
            if (shelf.PathOf(installed.Id) is null)
                faults.Add("an installed sound pack could not be found by its stable ID");

            var localRoot = System.IO.Path.Combine(root, "no-local-files");
            var library = new SoundLibrary(localRoot, installed.Path);
            var clip = library.Load("step/stone1");
            if (clip is null || clip.Peak < 0.1f)
                faults.Add("a WAV inside the installed ZIP did not become a playable sparse override");
            if (!SoundLibrary.BuiltInNames.All(library.Has))
                faults.Add("selecting a sound pack hid Driftwood's embedded fallback recordings");

            shelf.InstallLocal(packPath, out _);
            if (shelf.List().Count(entry => entry.Id == installed.Id) != 1)
                faults.Add("installing the same local sound pack twice duplicated it");

            var encoded = File.ReadAllBytes(packPath);
            var hash = Convert.ToHexString(SHA512.HashData(encoded)).ToLowerInvariant();
            var remote = new RemoteSoundPack(
                "Ab12Cd34", "tiny-sounds", "Tiny Remote Sounds", "fixture maker", "CC0-1.0", 12, "",
                new Uri("https://modrinth.com/resourcepack/tiny-sounds"));
            var remoteFile = new RemoteSoundPackFile(
                remote, "Version1", "r1", "Tiny Sounds.zip", hash, encoded);
            var downloaded = shelf.Install(remoteFile, out var remoteWhy);
            if (downloaded is null)
                faults.Add($"a verified remote sound pack was refused: {remoteWhy}");
            else
            {
                var found = shelf.Find(downloaded.Id);
                if (found is null || found.License != "CC0-1.0" || found.Author != "fixture maker")
                    faults.Add("a downloaded pack lost its author or license metadata on the shelf");
                if (!shelf.Remove(downloaded.Id)) faults.Add("a downloaded sound pack could not be removed");
            }

            var unsafeRemote = remoteFile with { Remote = remote with { Id = "/../../escape" } };
            if (shelf.Install(unsafeRemote, out var unsafeRemoteWhy) is not null)
                faults.Add("a provider ID containing a path was accepted");
            else if (!unsafeRemoteWhy.Contains("provider ID", StringComparison.OrdinalIgnoreCase))
                faults.Add($"an unsafe provider ID was refused as '{unsafeRemoteWhy}'");

            var unsafePack = System.IO.Path.Combine(incoming, "Unsafe.zip");
            WritePack(unsafePack, unsafePath: true);
            if (shelf.InstallLocal(unsafePack, out var unsafeWhy) is not null)
                faults.Add("a ZIP with a parent-directory path was accepted");
            else if (!unsafeWhy.Contains("unsafe", StringComparison.OrdinalIgnoreCase))
                faults.Add($"an unsafe ZIP was refused as '{unsafeWhy}'");

            if (!shelf.Remove(installed.Id)) faults.Add("a local sound pack could not be removed");
            detail = "original ZIP kept, sparse override decoded, fallback survived, metadata persisted, unsafe path refused";
        }
        catch (Exception error)
        {
            faults.Add($"sound-pack shelf threw {error.GetType().Name}: {error.Message}");
            detail = "the shelf self-test threw";
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }

        return faults;
    }

    private static void WritePack(string path, bool unsafePath)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        var name = unsafePath
            ? "../assets/minecraft/sounds/step/stone1.wav"
            : "wrapper/assets/minecraft/sounds/step/stone1.wav";
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(TinyWav());
    }

    private static byte[] TinyWav()
    {
        const int rate = 8_000;
        const int samples = 800;
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * sizeof(short));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * sizeof(short));
        for (var i = 0; i < samples; i++)
            writer.Write((short)(Math.Sin(i * Math.PI * 2 / 40) * 12_000));
        writer.Flush();
        return bytes.ToArray();
    }
}
