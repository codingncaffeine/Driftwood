using Driftwood.Core.Blocks;

namespace Driftwood.Core.World;

/// <summary>
/// ⚠ Instruments only: a generator that disagrees with the real one, the control for co-op's terrain
/// checksums (plan D3). One cell in the middle of every chunk that has anything in it is flipped —
/// air to stone, anything else to air — so the sum differs and a mend has something real to undo.
/// </summary>
public static class TerrainFault
{
    public const int Cell = Chunk.Size / 2;

    public static void Plant(Chunk chunk)
    {
        if (chunk.IsEmpty) return;
        var now = chunk.Get(Cell, Cell, Cell);
        chunk.Set(Cell, Cell, Cell, now == BlockId.Air ? new BlockId(1) : BlockId.Air);
    }
}
