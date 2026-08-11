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
        string? ResourceName = null,
        string? ProceduralKey = null);

    private readonly record struct WeightedSource(ClipSource Source, int Weight);
    private sealed record SourceLayer(IReadOnlyList<WeightedSource> Choices, SourceLayer? Fallback = null);

    private static readonly (string Key, string Resource)[] Embedded =
    [
        ("animals/frog", "Driftwood.Core.Sounds.animals/frog.wav"),
        ("enemies/bat", "Driftwood.Core.Sounds.enemies/bat.wav"),
        ("enemies/spider", "Driftwood.Core.Sounds.enemies/spider.wav"),
        ("enemies/spider_attack", "Driftwood.Core.Sounds.enemies/spider_attack.wav"),
        ("enemies/zombie", "Driftwood.Core.Sounds.enemies/zombie.wav"),
    ];

    private readonly Dictionary<string, SourceLayer> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _bare = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WavClip?> _clips = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The local fallback folder, whether or not it exists in a single-file build.</summary>
    public string Root { get; }

    /// <summary>The highest-priority active resource pack, or null for local fallback only.</summary>
    public string? ActivePack { get; private set; }

    /// <summary>Resource packs contributing sounds, texture pack first and audio choice last.</summary>
    public IReadOnlyList<string> ActivePacks => _activePacks;

    private readonly List<string> _activePacks = [];

    public int Count => _sources.Count;
    public int LocalCount { get; private set; }
    public int PackCount { get; private set; }
    public List<string> Faults { get; } = [];

    public static IReadOnlySet<string> BuiltInNames { get; } =
        new HashSet<string>(Embedded.Select(item => item.Key).Concat(MagicSounds.All),
            StringComparer.OrdinalIgnoreCase);

    public SoundLibrary(string root, string? packPath = null)
        : this(root, texturePackPath: null, soundPackPath: packPath)
    {
    }

    /// <summary>
    /// Builds the standard resource-pack stack: Driftwood fallbacks, the active texture pack's
    /// sounds, then the explicitly selected audio pack. Later layers sparsely override earlier ones.
    /// </summary>
    public SoundLibrary(string root, string? texturePackPath, string? soundPackPath)
    {
        Root = root;

        IndexFolder(root);
        IndexEmbeddedFallback();
        IndexProceduralFallback();
        LocalCount = _sources.Count;

        if (!string.IsNullOrWhiteSpace(texturePackPath))
            IndexPack(texturePackPath, requireSounds: false);
        if (!string.IsNullOrWhiteSpace(soundPackPath)
            && !string.Equals(soundPackPath, texturePackPath, StringComparison.OrdinalIgnoreCase))
            IndexPack(soundPackPath, requireSounds: true);
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

    private void IndexProceduralFallback()
    {
        foreach (var key in MagicSounds.All)
        {
            if (_sources.ContainsKey(key)) continue;
            Add(key, new ClipSource(".synth", $"Driftwood synthesis:{key}", ProceduralKey: key),
                replace: false);
        }
    }

    private void IndexPack(string path, bool requireSounds)
    {
        try
        {
            var inspection = SoundPackArchive.Inspect(path, requireSounds);
            if (inspection.Clips == 0) return;

            PackCount += inspection.Clips;
            _activePacks.Add(path);
            ActivePack = path;
            var folder = Directory.Exists(path);

            foreach (var (key, entry) in inspection.Entries)
            {
                Add(key, PackSource(path, entry, folder), replace: true);
            }

            if (inspection.Variants is not null)
            {
                foreach (var (key, variants) in inspection.Variants)
                {
                    var sources = variants
                        .Select(item => new WeightedSource(PackSource(path, item.Entry, folder), item.Weight))
                        .ToList();
                    if (sources.Count > 0)
                    {
                        _sources.TryGetValue(key, out var fallback);
                        _sources[key] = new SourceLayer(sources, fallback);
                    }
                }
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
            _sources.TryGetValue(key, out var fallback);
            _sources[key] = new SourceLayer([new WeightedSource(source, 1)], fallback);
            return;
        }

        if (!_sources.TryAdd(key, new SourceLayer([new WeightedSource(source, 1)])))
            Faults.Add($"'{key}' is available more than once locally");
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
            : $"'{name}' is not in {string.Join(" or ", _activePacks.Select(Path.GetFileName))} "
              + "or Driftwood's local fallback";
        return null;
    }

    public bool Has(string name) => Resolve(name, out _) is not null;
    public IEnumerable<string> AllKeys => _sources.Keys;

    public WavClip? Load(string name)
    {
        var key = Resolve(name, out var whyNot);
        if (key is null)
        {
            if (_reported.Add($"missing:{name}")) Faults.Add(whyNot);
            return null;
        }

        for (SourceLayer? layer = _sources[key]; layer is not null; layer = layer.Fallback)
        {
            foreach (var source in PickOrder(layer.Choices))
            {
                var clip = Load(source, key);
                if (clip is not null) return clip;
            }
        }
        return null;
    }

    private WavClip? Load(ClipSource source, string key)
    {
        var cacheKey = source.Description;
        if (_clips.TryGetValue(cacheKey, out var cached)) return cached;

        WavClip? clip = null;
        try
        {
            if (source.ProceduralKey is { } procedural)
            {
                clip = MagicSoundSynthesis.Create(procedural);
                _clips[cacheKey] = clip;
                return clip;
            }
            var bytes = ReadSource(source);
            var decoded = source.Extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
                ? OggVorbis.TryDecode(bytes, out clip, out var fault)
                : Wav.TryDecode(bytes, out clip, out fault);
            if (!decoded && _reported.Add($"decode:{cacheKey}"))
                Faults.Add($"'{key}' from {source.Description}: {fault}");
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (_reported.Add($"read:{cacheKey}"))
                Faults.Add($"'{key}' from {source.Description}: {error.Message}");
        }

        _clips[cacheKey] = clip;
        return clip;
    }

    private static ClipSource PackSource(string path, string entry, bool folder) => new(
        Path.GetExtension(entry), $"{Path.GetFileName(path)}:{entry}",
        FilePath: folder ? Path.Combine(path, entry.Replace('/', Path.DirectorySeparatorChar)) : null,
        ArchivePath: folder ? null : path,
        ArchiveEntry: folder ? null : entry);

    private static ClipSource Pick(IReadOnlyList<WeightedSource> choices)
    {
        if (choices.Count == 1) return choices[0].Source;
        var total = choices.Sum(choice => (long)Math.Max(1, choice.Weight));
        var pick = Random.Shared.NextInt64(total);
        foreach (var choice in choices)
        {
            pick -= Math.Max(1, choice.Weight);
            if (pick < 0) return choice.Source;
        }
        return choices[^1].Source;
    }

    private static IEnumerable<ClipSource> PickOrder(IReadOnlyList<WeightedSource> choices)
    {
        var first = Pick(choices);
        yield return first;
        foreach (var choice in choices)
            if (!ReferenceEquals(choice.Source, first)) yield return choice.Source;
    }

    private static byte[] ReadSource(ClipSource source)
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
