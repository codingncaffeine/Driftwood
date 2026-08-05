namespace Driftwood.Core.Audio;

/// <summary>
/// Every sound file on disk, found by name and decoded on first use.
/// </summary>
/// <remarks>
/// <para>Indexed by bare file name across the folders they are sorted into, so the sound table can
/// say <c>digital_footstep_grass_1</c> and not care that it lives under <c>Footsteps</c>. Moving a
/// file between folders is then a thing that cannot break anything, which matters because the
/// folders are the pack author's organisation rather than ours.</para>
/// <para>Decoded on demand and kept. Nineteen megabytes of source audio is not worth loading at
/// startup when a session might only ever hear a dozen of the files, and it is not worth decoding
/// twice when it hears one of them a thousand times.</para>
/// </remarks>
public sealed class SoundLibrary
{
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WavClip?> _clips = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the sounds were found, or the folder that was looked in and was not there.</summary>
    public string Root { get; }

    /// <summary>Files indexed.</summary>
    public int Count => _paths.Count;

    /// <summary>Anything that would not decode, named with the reason.</summary>
    public List<string> Faults { get; } = [];

    public SoundLibrary(string root)
    {
        Root = root;
        if (!Directory.Exists(root)) return;

        foreach (var path in Directory.EnumerateFiles(root, "*.wav", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(path);

            // First one wins, and a duplicate is a fault rather than a silent choice: two files of
            // the same name in different folders means the table's reference is ambiguous.
            if (_paths.TryAdd(name, path)) continue;
            Faults.Add($"'{name}' is in both {Shorten(_paths[name])} and {Shorten(path)}");
        }
    }

    /// <summary>True when a name is on disk, whether or not it has been decoded yet.</summary>
    public bool Has(string name) => _paths.ContainsKey(name);

    /// <summary>
    /// Decodes a clip, or returns null and records why. Cached either way, so a broken file is
    /// read once rather than on every hit.
    /// </summary>
    public WavClip? Load(string name)
    {
        if (_clips.TryGetValue(name, out var cached)) return cached;

        if (!_paths.TryGetValue(name, out var path))
        {
            Faults.Add($"'{name}' is not in {Shorten(Root)}");
            _clips[name] = null;
            return null;
        }

        WavClip? clip = null;
        try
        {
            if (!Wav.TryDecode(File.ReadAllBytes(path), out clip, out var fault))
                Faults.Add($"'{name}': {fault}");
        }
        catch (IOException ex)
        {
            Faults.Add($"'{name}': {ex.Message}");
        }

        _clips[name] = clip;
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
