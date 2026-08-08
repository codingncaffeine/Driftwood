namespace Driftwood.Core.Audio;

/// <summary>
/// Every sound file on disk, found by name and decoded on first use.
/// </summary>
/// <remarks>
/// <para>Indexed by path relative to the sounds folder, without its extension and with forward
/// slashes — <c>mob/wolf/step1</c> — because the pack layout this library now reads repeats bare
/// names on purpose: <c>dig/stone1</c> and <c>step/stone1</c> are different recordings of the same
/// rock. A bare name still resolves when only one file anywhere carries it, which keeps the tables
/// readable for sounds that never had a twin; a bare name two folders share is refused with both
/// candidates named, never answered with whichever indexed first.</para>
/// <para>Decoded on demand and kept. Twenty megabytes of source audio is not worth loading at
/// startup when a session might only ever hear a dozen of the files, and it is not worth decoding
/// twice when it hears one of them a thousand times.</para>
/// </remarks>
public sealed class SoundLibrary
{
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _bare = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WavClip?> _clips = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the sounds were found, or the folder that was looked in and was not there.</summary>
    public string Root { get; }

    /// <summary>Files indexed.</summary>
    public int Count => _paths.Count;

    /// <summary>Anything that would not decode or resolve, named with the reason.</summary>
    public List<string> Faults { get; } = [];

    public SoundLibrary(string root)
    {
        Root = root;
        if (!Directory.Exists(root)) return;

        foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(path);
            var wav = extension.Equals(".wav", StringComparison.OrdinalIgnoreCase);
            var ogg = extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase);
            if (!wav && !ogg) continue;

            var relative = Path.GetRelativePath(root, path);
            var key = relative[..^extension.Length].Replace('\\', '/');

            // The same clip in both formats is an ambiguous reference, not a silent choice.
            if (!_paths.TryAdd(key, path))
            {
                Faults.Add($"'{key}' is on disk more than once");
                continue;
            }

            // A bare name maps to its one file, or to null once a second file claims it — the
            // null is what lets a lookup say "ambiguous" instead of guessing.
            var bare = Path.GetFileNameWithoutExtension(path);
            if (!_bare.TryAdd(bare, key) && _bare[bare] != key) _bare[bare] = null;
        }
    }

    /// <summary>Resolves a reference to its index key, or explains why it cannot.</summary>
    private string? Resolve(string name, out string fault)
    {
        fault = "";
        var key = name.Replace('\\', '/');
        if (_paths.ContainsKey(key)) return key;

        if (_bare.TryGetValue(name, out var owner))
        {
            if (owner is not null) return owner;
            fault = $"'{name}' names more than one file — say which folder's";
            return null;
        }

        fault = $"'{name}' is not in {Shorten(Root)}";
        return null;
    }

    /// <summary>True when a name resolves to a file on disk, decoded or not.</summary>
    public bool Has(string name) => Resolve(name, out _) is not null;

    /// <summary>Every index key on disk, for the check that decodes the whole shelf.</summary>
    public IEnumerable<string> AllKeys => _paths.Keys;

    /// <summary>
    /// Decodes a clip, or returns null and records why. Cached either way, so a broken file is
    /// read once rather than on every hit.
    /// </summary>
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
        try
        {
            var bytes = File.ReadAllBytes(_paths[key]);
            var decoded = _paths[key].EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                ? OggVorbis.TryDecode(bytes, out clip, out var fault)
                : Wav.TryDecode(bytes, out clip, out fault);
            if (!decoded) Faults.Add($"'{key}': {fault}");
        }
        catch (IOException ex)
        {
            Faults.Add($"'{key}': {ex.Message}");
        }

        _clips[key] = clip;
        return clip;
    }

    /// <summary>
    /// Finds the sounds folder from wherever the game was started.
    /// </summary>
    /// <remarks>
    /// The published artifact carries them beside the exe; a build run out of <c>bin</c> is several
    /// folders down from the repository they live in. Walking up until the folder appears covers
    /// both without either one having to know about the other, and stops rather than climbing off
    /// the top of the drive.
    /// </remarks>
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

    private static string Shorten(string path) => Path.GetFileName(Path.GetDirectoryName(path)) ?? path;
}
