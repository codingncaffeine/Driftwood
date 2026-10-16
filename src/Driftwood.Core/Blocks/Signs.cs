namespace Driftwood.Core.Blocks;

/// <summary>
/// What is written on every sign in a world, by the cell it stands in.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Signs, 2026-10-16</b> — the user: "signs aren't in the game yet". Four short lines
/// on a board, the reference's own shape for them.</para>
/// <para>⚠ <b>Kept by cell, and never trusted on its own.</b> A sign can leave its cell a dozen
/// ways — mined, shed when its wall goes, blown up, washed out — and hooking every one of them is
/// how a stale line turns up on the next sign built there. Instead a new sign clears its cell
/// when placed, the renderer only reads a cell that still holds a sign, and the save drops any
/// line whose cell no longer does (<see cref="Prune"/>).</para>
/// </remarks>
public sealed class SignBank
{
    /// <summary>Lines on one board, the reference's four.</summary>
    public const int Lines = 4;

    /// <summary>
    /// The longest line, in characters. The board is a block wide; fifteen of the font's letters at
    /// the size the text is drawn fill it with a margin, as the reference's line does.
    /// </summary>
    public const int MaxChars = 15;

    private readonly Dictionary<(int X, int Y, int Z), string[]> _signs = [];

    /// <summary>Raised whenever a sign's words change, so the renderer can rebuild.</summary>
    public int Version { get; private set; }

    public int Count => _signs.Count;

    /// <summary>True once a sign has changed since the world was last written.</summary>
    public bool Dirty { get; private set; }

    /// <summary>Called by the save once what is here is on disk.</summary>
    public void Settled() => Dirty = false;

    public IEnumerable<KeyValuePair<(int X, int Y, int Z), string[]>> All => _signs;

    /// <summary>The four lines at a cell, blank when nothing has been written there.</summary>
    public string[] Get(int x, int y, int z) =>
        _signs.TryGetValue((x, y, z), out var lines) ? [.. lines] : [.. Enumerable.Repeat("", Lines)];

    /// <summary>Writes a sign; one with nothing on it is not kept at all.</summary>
    public void Set(int x, int y, int z, IReadOnlyList<string> lines)
    {
        var kept = new string[Lines];
        for (var i = 0; i < Lines; i++)
        {
            var line = i < lines.Count ? lines[i] ?? "" : "";
            kept[i] = line.Length > MaxChars ? line[..MaxChars] : line;
        }

        if (kept.All(string.IsNullOrEmpty)) _signs.Remove((x, y, z));
        else _signs[(x, y, z)] = kept;
        Version++;
        Dirty = true;
    }

    public void Remove(int x, int y, int z)
    {
        if (_signs.Remove((x, y, z)))
        {
            Version++;
            Dirty = true;
        }
    }

    public void Clear()
    {
        _signs.Clear();
        Version++;
    }

    /// <summary>Drops every entry whose cell no longer holds a sign. Returns how many went.</summary>
    public int Prune(Func<int, int, int, bool> isSign)
    {
        var gone = _signs.Keys.Where(cell => !isSign(cell.X, cell.Y, cell.Z)).ToList();
        foreach (var cell in gone) _signs.Remove(cell);
        if (gone.Count > 0) Version++;
        return gone.Count;
    }
}
