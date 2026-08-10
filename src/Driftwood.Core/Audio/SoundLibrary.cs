namespace Driftwood.Core.Audio;

/// <summary>
/// A sparse stack of local sounds and one optional Minecraft resource-pack ZIP, decoded on first use.
/// </summary>
public sealed class SoundLibrary
{
    private sealed record ClipSource(
        string Extension,
        string Description,
        string? FilePath = null,
        string? ArchivePath = null,
        string? ArchiveEntry = null,
        string? ResourceName = null);

    private static readonly (string Key, string Resource)[] Embedded =
    [
        ("animals/frog", "Driftwood.Core.Sounds.animals/frog.wav"),
        ("enemies/bat", "Driftwood.Core.Sounds.enemies/bat.wav"),
        ("enemies/spider", "Driftwood.Core.Sounds.enemies/spider.wav"),
        ("enemies/spider_attack", "Driftwood.Core.Sounds.enemies/spider_attack.wav"),
        ("enemies/zombie", "Driftwood.Core.Sounds.enemies/zombie.wav"),
    ];

    private readonly Dictionary<string, ClipSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _bare = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WavClip?> _clips = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The local fallback folder, whether or not it exists in a single-file build.</summary>
    public string Root { get; }

    /// <summary>The selected original ZIP, or null for local fallback only.</summary>
    public string? ActivePack { get; }

    public int Count => _sources.Count;
    public int LocalCount { get; private set; }
    public int PackCount { get; private set; }
    public List<string> Faults { get; } = [];

    public static IReadOnlySet<string> BuiltInNames { get; } =
        new HashSet<string>(Embedded.Select(item => item.Key), StringComparer.OrdinalIgnoreCase);

    public SoundLibrary(string root, string? packPath = null)
    {
        Root = root;
        ActivePack = string.IsNullOrWhiteSpace(packPath) ? null : packPath;

        IndexFolder(root);
        IndexEmbeddedFallback();
        LocalCount = _sources.Count;

        if (ActivePack is not null) IndexPack(ActivePack);
        BuildBareIndex();
    }

    private void IndexFolder(string root)
    {
        if (!Directory.Exists(root)) return;

        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(path);
                if (!AudioExtension(extension)) continue;

                var relative = Path.GetRelativePath(root, path);
                var key = relative[..^extension.Length].Replace('\\', '/');
                Add(key, new ClipSource(extension, path, FilePath: path), replace: false);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Faults.Add($"could not index local sounds: {error.Message}");
        }
    }

    private void IndexEmbeddedFallback()
    {
        foreach (var (key, resource) in Embedded)
        {
            if (_sources.ContainsKey(key)) continue;
            Add(key, new ClipSource(".wav", resource, ResourceName: resource), replace: false);
        }
    }

    private void IndexPack(string path)
    {
        try
        {
            var inspection = SoundPackArchive.Inspect(path);
            PackCount = inspection.Clips;

            foreach (var (key, entry) in inspection.Entries)
            {
                var extension = Path.GetExtension(entry);
                Add(key, new ClipSource(
                    extension, $"{Path.GetFileName(path)}:{entry}",
                    ArchivePath: path, ArchiveEntry: entry), replace: true);
            }
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            Faults.Add($"could not use sound pack '{Path.GetFileName(path)}': {error.Message}");
        }
    }

    private void Add(string key, ClipSource source, bool replace)
    {
        key = key.Replace('\\', '/').Trim('/');
        if (key.Length == 0) return;

        if (replace)
        {
            _sources[key] = source;
            return;
        }

        if (!_sources.TryAdd(key, source)) Faults.Add($"'{key}' is available more than once locally");
    }

    private void BuildBareIndex()
    {
        foreach (var key in _sources.Keys)
        {
            var slash = key.LastIndexOf('/');
            var bare = slash >= 0 ? key[(slash + 1)..] : key;
            if (!_bare.TryAdd(bare, key) && _bare[bare] != key) _bare[bare] = null;
        }
    }

    private string? Resolve(string name, out string fault)
    {
        fault = "";
        var key = name.Replace('\\', '/');
        if (_sources.ContainsKey(key)) return key;

        if (_bare.TryGetValue(name, out var owner))
        {
            if (owner is not null) return owner;
            fault = $"'{name}' names more than one sound — say which folder's";
            return null;
        }

        fault = ActivePack is null
            ? $"'{name}' is not in Driftwood's local fallback; install a sound pack from Options > Audio"
            : $"'{name}' is not in {Path.GetFileName(ActivePack)} or Driftwood's local fallback";
        return null;
    }

    public bool Has(string name) => Resolve(name, out _) is not null;
    public IEnumerable<string> AllKeys => _sources.Keys;

    public WavClip? Load(string name)
    {
        var key = Resolve(name, out var whyNot);
        if (key is null)
        {
            if (!_clips.ContainsKey(name)) Faults.Add(whyNot);
            _clips[name] = null;
            return null;
        }

        if (_clips.TryGetValue(key, out var cached)) return cached;

        WavClip? clip = null;
        var source = _sources[key];
        try
        {
            var bytes = Read(source);
            var decoded = source.Extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
                ? OggVorbis.TryDecode(bytes, out clip, out var fault)
                : Wav.TryDecode(bytes, out clip, out fault);
            if (!decoded) Faults.Add($"'{key}' from {source.Description}: {fault}");
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Faults.Add($"'{key}' from {source.Description}: {error.Message}");
        }

        _clips[key] = clip;
        return clip;
    }

    private static byte[] Read(ClipSource source)
    {
        if (source.FilePath is not null) return File.ReadAllBytes(source.FilePath);
        if (source.ArchivePath is not null && source.ArchiveEntry is not null)
            return SoundPackArchive.Read(source.ArchivePath, source.ArchiveEntry);

        if (source.ResourceName is null) throw new InvalidDataException("a sound has no source");
        using var stream = typeof(SoundLibrary).Assembly.GetManifestResourceStream(source.ResourceName)
            ?? throw new InvalidDataException($"embedded sound '{source.ResourceName}' is missing");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static bool AudioExtension(string extension) =>
        extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase);

    public static string FindRoot(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;

        var here = AppContext.BaseDirectory;
        for (var up = 0; up < 8 && here.Length > 0; up++)
        {
            var candidate = Path.Combine(here, "assets", "sounds");
            if (Directory.Exists(candidate)) return candidate;

            var parent = Path.GetDirectoryName(here.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(parent)) break;
            here = parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "assets", "sounds");
    }
}
