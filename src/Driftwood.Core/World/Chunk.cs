using Driftwood.Core.Blocks;

namespace Driftwood.Core.World;

/// <summary>
/// A cubic 32x32x32 block of the world. Cubic rather than a full-height column because a single
/// block edit only ever dirties one of these, which bounds both the remesh cost and (at P2) the
/// light re-propagation cost.
/// </summary>
/// <remarks>
/// Storage is one flat <c>ushort[32768]</c> — 64 KB raw. Palette compression comes at P5 when
/// chunks start going to disk; the accessors here are the seam that change hides behind.
/// </remarks>
public sealed class Chunk
{
    public const int SizeLog2 = 5;
    public const int Size = 1 << SizeLog2;      // 32
    public const int SizeMask = Size - 1;       // 31
    public const int Volume = Size * Size * Size;

    public ChunkPos Position { get; }

    private readonly ushort[] _blocks = new ushort[Volume];

    /// <summary>Count of non-air blocks. Lets the mesher skip empty chunks without scanning them.</summary>
    public int SolidCount { get; private set; }

    /// <summary>Set whenever contents change; cleared by the mesher once it has rebuilt.</summary>
    public bool Dirty { get; set; } = true;

    public Chunk(ChunkPos position) => Position = position;

    public bool IsEmpty => SolidCount == 0;

    /// <summary>Index into the flat store. X varies fastest, which matches the mesher's inner loop.</summary>
    public static int Index(int x, int y, int z) => (y << (SizeLog2 * 2)) | (z << SizeLog2) | x;

    /// <summary>Reads a block by chunk-local coordinate. Callers must pass 0..31.</summary>
    public BlockId Get(int x, int y, int z) => new(_blocks[Index(x, y, z)]);

    public void Set(int x, int y, int z, BlockId id)
    {
        var i = Index(x, y, z);
        var old = _blocks[i];
        if (old == id.Value) return;

        if (old == 0 && id.Value != 0) SolidCount++;
        else if (old != 0 && id.Value == 0) SolidCount--;

        _blocks[i] = id.Value;
        Dirty = true;
    }

    /// <summary>
    /// Direct view of the backing store for bulk operations (generation, serialisation, meshing
    /// snapshots). Writing through this bypasses <see cref="SolidCount"/> upkeep — call
    /// <see cref="RecountSolid"/> afterwards.
    /// </summary>
    public ushort[] Raw => _blocks;

    public void RecountSolid()
    {
        var n = 0;
        foreach (var b in _blocks) if (b != 0) n++;
        SolidCount = n;
        Dirty = true;
    }
}
