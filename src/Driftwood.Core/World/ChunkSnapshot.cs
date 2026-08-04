using Driftwood.Core.Blocks;

namespace Driftwood.Core.World;

/// <summary>
/// A 34x34x34 padded copy of one chunk plus a one-block skirt from every neighbour, including
/// the diagonals. Meshing reads this instead of the world.
/// </summary>
/// <remarks>
/// <para>Two reasons this exists rather than having the mesher call
/// <see cref="VoxelWorld.GetBlock"/>: correctness and speed. Correctness, because ambient
/// occlusion at a chunk seam samples diagonally into the neighbour, so face-only skirts are not
/// enough — the corners have to be there too. Speed, because meshing does roughly six neighbour
/// tests per block, and a dictionary lookup per test is the difference between a chunk meshing
/// in under a millisecond and stalling the frame.</para>
/// <para>Taking the snapshot also decouples meshing from the world: once copied, the mesh job can
/// run on a worker thread while the main thread keeps editing blocks.</para>
/// </remarks>
public sealed class ChunkSnapshot
{
    public const int Pad = 1;
    public const int PadSize = Chunk.Size + Pad * 2;   // 34
    public const int PadVolume = PadSize * PadSize * PadSize;

    private readonly ushort[] _blocks = new ushort[PadVolume];

    public ChunkPos Position { get; private set; }

    private static int PadIndex(int x, int y, int z) =>
        ((y + Pad) * PadSize + (z + Pad)) * PadSize + (x + Pad);

    /// <summary>Reads a block by chunk-local coordinate, valid from -1 to <see cref="Chunk.Size"/>.</summary>
    public ushort Get(int x, int y, int z) => _blocks[PadIndex(x, y, z)];

    /// <summary>
    /// Refills this snapshot from the world. Copies the chunk body in whole rows, then walks the
    /// six skirt faces, twelve edges and eight corners as a single sweep over the padded shell.
    /// </summary>
    public void Capture(VoxelWorld world, ChunkPos pos)
    {
        Position = pos;
        Array.Clear(_blocks);

        var (ox, oy, oz) = pos.Origin;

        if (world.TryGetChunk(pos, out var chunk) && !chunk.IsEmpty)
        {
            var src = chunk.Raw;
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var srcOffset = Chunk.Index(0, y, z);
                var dstOffset = PadIndex(0, y, z);
                Array.Copy(src, srcOffset, _blocks, dstOffset, Chunk.Size);
            }
        }

        // The shell: every padded cell with at least one coordinate outside 0..31.
        for (var y = -Pad; y < Chunk.Size + Pad; y++)
        for (var z = -Pad; z < Chunk.Size + Pad; z++)
        for (var x = -Pad; x < Chunk.Size + Pad; x++)
        {
            var inside = (uint)x < Chunk.Size && (uint)y < Chunk.Size && (uint)z < Chunk.Size;
            if (inside) continue;
            _blocks[PadIndex(x, y, z)] = world.GetBlock(ox + x, oy + y, oz + z).Value;
        }
    }
}
