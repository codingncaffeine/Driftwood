using System.Text;

namespace Driftwood.Core.Saves;

/// <summary>
/// The names a save file uses, and the small numbers it writes instead of them.
/// </summary>
/// <remarks>
/// <para>⛔ <b>Without this, every save in existence breaks the next time a block is added.</b> Block
/// and item ids are handed out in registration order, so inserting one shifts every id after it —
/// and three of the four commits made on 2026-08-05 did exactly that. A save that stored raw ids
/// would come back with stone bricks turned into ladders, silently, and only in worlds saved before
/// the change. That is the worst kind of bug this project could ship: invisible, delayed, and
/// destructive.</para>
/// <para>So a save stores <em>names</em>. The palette is the list of names that save actually uses,
/// written once at the front, and everything else refers to a position in that list. It costs a few
/// hundred bytes and it makes a save independent of the order anything was registered in.</para>
/// <para>It also does the other half: a name that no longer exists resolves to nothing rather than
/// to whatever now occupies that id. Renaming or removing a block becomes a thing a player notices
/// as a missing block, not as a wall that turned into glass.</para>
/// </remarks>
public sealed class Palette
{
    private readonly List<string> _names = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public int Count => _names.Count;

    public IReadOnlyList<string> Names => _names;

    /// <summary>The number this name is written as, adding it if this is the first time.</summary>
    public int Of(string name)
    {
        if (_index.TryGetValue(name, out var at)) return at;

        at = _names.Count;
        _names.Add(name);
        _index[name] = at;
        return at;
    }

    /// <summary>The name behind a number, or null when the file is asking for one it never listed.</summary>
    public string? At(int index) => (uint)index < (uint)_names.Count ? _names[index] : null;

    public void Add(string name)
    {
        if (_index.ContainsKey(name)) return;
        _index[name] = _names.Count;
        _names.Add(name);
    }

    /// <summary>
    /// Resolves every name to whatever the running game calls that id now.
    /// </summary>
    /// <param name="lookup">
    /// Answers with the id for a name, or -1 when this build has never heard of it.
    /// </param>
    /// <param name="missing">
    /// Filled with every name this build no longer has, so a load can say so out loud rather than
    /// quietly dropping part of somebody's world.
    /// </param>
    /// <returns>Indexed by palette position; -1 where the name is gone.</returns>
    public int[] Resolve(Func<string, int> lookup, List<string> missing)
    {
        var map = new int[_names.Count];

        for (var i = 0; i < _names.Count; i++)
        {
            map[i] = lookup(_names[i]);
            if (map[i] < 0) missing.Add(_names[i]);
        }

        return map;
    }

    public void Write(BinaryWriter into)
    {
        into.Write(_names.Count);
        foreach (var name in _names) into.Write(name);
    }

    public static Palette Read(BinaryReader from)
    {
        var palette = new Palette();
        var count = from.ReadInt32();

        // A count read out of a damaged file is a count that can be anything at all, and the first
        // thing it would do is allocate it. Bounded by something no real save comes near.
        if (count is < 0 or > 1_000_000)
            throw new InvalidDataException($"a palette of {count} names is not a palette");

        for (var i = 0; i < count; i++) palette.Add(from.ReadString());
        return palette;
    }

    public override string ToString() => $"{_names.Count} names";
}

/// <summary>
/// Reading and writing the sections a save is made of.
/// </summary>
/// <remarks>
/// <para>A save is a sequence of <b>tagged, length-prefixed sections</b> — the same shape as the PNG
/// and WAV files this project already decodes by hand, and chosen for the same reason those formats
/// chose it: <b>a reader that does not recognise a section can skip it, because the length says how
/// far.</b></para>
/// <para>⚠ <b>And skipping is not the same as discarding.</b> An unknown section is kept and written
/// back out, so a world saved by a newer build and opened by an older one comes back whole rather
/// than losing whatever the older build had no name for. That is the rule
/// <see cref="Settings.GameSettings"/> already follows for unknown keys, and it is what makes it
/// safe to run two builds against one save folder — which is exactly what happens during
/// development.</para>
/// </remarks>
public static class SaveSection
{
    /// <summary>Four bytes at the front of every save, so a wrong file is refused rather than read.</summary>
    public static readonly byte[] Magic = "DWSV"u8.ToArray();

    /// <summary>
    /// The format's own version, which is not the game's.
    /// </summary>
    /// <remarks>
    /// Only ever raised when the <em>shape</em> of the file changes in a way a reader could not work
    /// out for itself. Adding a section does not need it — an old reader skips what it does not know
    /// and a new reader finds nothing where it looks, which is exactly what versioning is usually
    /// used to avoid needing.
    /// </remarks>
    public const int Version = 1;

    public static void Write(BinaryWriter into, string tag, ReadOnlySpan<byte> payload)
    {
        if (tag.Length != 4) throw new ArgumentException($"'{tag}' is not a four-letter tag", nameof(tag));

        into.Write(Encoding.ASCII.GetBytes(tag));
        into.Write(payload.Length);
        into.Write(payload);
    }

    /// <summary>Reads the next section, or reports that the file is finished.</summary>
    public static bool TryRead(BinaryReader from, out string tag, out byte[] payload)
    {
        tag = "";
        payload = [];

        if (from.BaseStream.Position >= from.BaseStream.Length) return false;

        var name = from.ReadBytes(4);
        if (name.Length < 4) return false;

        var length = from.ReadInt32();
        if (length < 0 || from.BaseStream.Position + length > from.BaseStream.Length)
            throw new InvalidDataException(
                $"section '{Encoding.ASCII.GetString(name)}' says it is {length} bytes and the file is not");

        tag = Encoding.ASCII.GetString(name);
        payload = from.ReadBytes(length);
        return true;
    }
}
