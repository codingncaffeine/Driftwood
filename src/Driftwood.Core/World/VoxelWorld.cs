using Driftwood.Core.Blocks;

namespace Driftwood.Core.World;

/// <summary>
/// The set of loaded chunks and world-coordinate access across them.
/// </summary>
/// <remarks>
/// P0 fills a fixed box up front on one thread and then only reads, so a plain dictionary is
/// safe. Streaming at P1 turns this into a concurrent store with a generation queue; keeping
/// every world-coordinate read behind these accessors is what makes that swap local.
/// </remarks>
public sealed class VoxelWorld
{
    private readonly Dictionary<ChunkPos, Chunk> _chunks = [];

    public BlockRegistry Registry { get; }

    public VoxelWorld(BlockRegistry registry) => Registry = registry;

    public int ChunkCount => _chunks.Count;

    public IEnumerable<Chunk> Chunks => _chunks.Values;

    public bool TryGetChunk(ChunkPos pos, out Chunk chunk) => _chunks.TryGetValue(pos, out chunk!);

    public Chunk GetOrCreateChunk(ChunkPos pos)
    {
        if (_chunks.TryGetValue(pos, out var existing)) return existing;
        var chunk = new Chunk(pos);
        _chunks[pos] = chunk;
        return chunk;
    }

    /// <summary>Reads a world block. Unloaded space reads as air.</summary>
    public BlockId GetBlock(int wx, int wy, int wz)
    {
        var pos = ChunkPos.FromWorld(wx, wy, wz);
        if (!_chunks.TryGetValue(pos, out var chunk)) return BlockId.Air;
        return chunk.Get(wx & Chunk.SizeMask, wy & Chunk.SizeMask, wz & Chunk.SizeMask);
    }

    /// <summary>
    /// Writes a world block, creating the chunk if needed. Also dirties any neighbouring chunk
    /// whose mesh this edit could change — a block on a chunk seam is a face-culling input for
    /// the chunk on the other side, and forgetting this leaves a hole in the world.
    /// </summary>
    public void SetBlock(int wx, int wy, int wz, BlockId id)
    {
        var pos = ChunkPos.FromWorld(wx, wy, wz);
        var chunk = GetOrCreateChunk(pos);

        var lx = wx & Chunk.SizeMask;
        var ly = wy & Chunk.SizeMask;
        var lz = wz & Chunk.SizeMask;
        chunk.Set(lx, ly, lz, id);

        if (lx == 0) DirtyNeighbour(pos.Offset(-1, 0, 0));
        else if (lx == Chunk.SizeMask) DirtyNeighbour(pos.Offset(1, 0, 0));
        if (ly == 0) DirtyNeighbour(pos.Offset(0, -1, 0));
        else if (ly == Chunk.SizeMask) DirtyNeighbour(pos.Offset(0, 1, 0));
        if (lz == 0) DirtyNeighbour(pos.Offset(0, 0, -1));
        else if (lz == Chunk.SizeMask) DirtyNeighbour(pos.Offset(0, 0, 1));
    }

    private void DirtyNeighbour(ChunkPos pos)
    {
        if (_chunks.TryGetValue(pos, out var c)) c.Dirty = true;
    }
}
