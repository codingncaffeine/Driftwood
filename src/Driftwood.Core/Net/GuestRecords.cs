using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Driftwood.Core.Net;

/// <summary>
/// The characters of everybody who has visited a world, kept by the host beside the world's save.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The user's model (2026-11-04): the host carries the saving.</b> A guest's pockets,
/// armour, health and progress belong to the world they were earned in, filed under the guest's
/// player key, so coming back to the same host picks up where they left off and no server anywhere
/// has to remember anything.</para>
/// <para>One file per key in <c>&lt;world&gt;.players/</c>. Read once when hosting starts; written on
/// a worker thread, newest wins, atomically (temp file then move) like the world itself.</para>
/// </remarks>
public sealed class GuestRecords
{
    private readonly string _folder;
    private readonly ConcurrentDictionary<string, byte[]> _known = new(StringComparer.Ordinal);
    private readonly object _writing = new();
    private Task _queue = Task.CompletedTask;

    public GuestRecords(string folder)
    {
        _folder = folder;
        if (!Directory.Exists(folder)) return;

        foreach (var file in Directory.EnumerateFiles(folder, "*.dwc"))
        {
            var key = Path.GetFileNameWithoutExtension(file);
            if (!IsKey(key)) continue;
            try { _known[key] = File.ReadAllBytes(file); }
            catch (IOException) { }
        }
    }

    /// <summary>Where a world's visitors are kept, beside its save file.</summary>
    public static string FolderFor(string savePath) =>
        Path.ChangeExtension(savePath, null) + ".players";

    /// <summary>A key is 32 lowercase hex digits — which is also what makes it safe as a file name.</summary>
    public static bool IsKey(string key) =>
        key.Length == 32 && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public int Count => _known.Count;

    public byte[]? Find(string key) => _known.TryGetValue(key, out var record) ? record : null;

    public void Keep(string key, byte[] record)
    {
        if (!IsKey(key) || record.Length == 0) return;
        _known[key] = record;

        // One chain, so writes land in the order they were asked for; the newest record per key wins.
        lock (_writing) _queue = _queue.ContinueWith(_ => Write(key, record), TaskScheduler.Default);
    }

    private void Write(string key, byte[] record)
    {
        if (!_known.TryGetValue(key, out var newest) || !ReferenceEquals(newest, record)) return;
        try
        {
            Directory.CreateDirectory(_folder);
            var path = Path.Combine(_folder, key + ".dwc");
            var temporary = path + ".writing";
            File.WriteAllBytes(temporary, record);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"driftwood: a visitor's character was not written: {error.Message}");
        }
    }

    /// <summary>Waits for every queued write. For the end of a session and for checks.</summary>
    public void Flush()
    {
        Task last;
        lock (_writing) last = _queue;
        last.Wait();
    }
}

/// <summary>This install's player key: who this machine is, to every host it visits.</summary>
public static class PlayerKey
{
    public static string Load(string configFolder)
    {
        var path = Path.Combine(configFolder, "player-key");
        try
        {
            if (File.Exists(path))
            {
                var kept = File.ReadAllText(path).Trim();
                if (GuestRecords.IsKey(kept)) return kept;
            }
        }
        catch (IOException) { }

        var made = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        try
        {
            Directory.CreateDirectory(configFolder);
            File.WriteAllText(path, made);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return made;
    }
}
