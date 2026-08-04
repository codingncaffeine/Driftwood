using System.Collections.Concurrent;
using Driftwood.Core.Blocks;

namespace Driftwood.Core.World;

/// <summary>
/// The set of loaded chunks and world-coordinate access across them.
/// </summary>
/// <remarks>
/// <para>The store is concurrent because streaming generates and meshes on worker threads while
/// the main thread walks the same map. Safety does not come from the dictionary alone: a chunk is
/// written once during generation and only read afterwards, and meshing of a chunk never begins
/// until every neighbour it samples has finished generating. The dictionary protects the shape of
/// the map; that ordering protects the contents.</para>
/// <para>Player edits at P3 break that assumption — they write a chunk that is already being
/// meshed — and will need the mesh job to work from its own snapshot, which
/// <see cref="ChunkSnapshot"/> already takes.</para>
/// </remarks>
public sealed class VoxelWorld
{
    private readonly ConcurrentDictionary<ChunkPos, Chunk> _chunks = new();

    public BlockRegistry Registry { get; }

    public VoxelWorld(BlockRegistry registry) => Registry = registry;

    public int ChunkCount => _chunks.Count;

    public IEnumerable<Chunk> Chunks => _chunks.Values;

    public bool TryGetChunk(ChunkPos pos, out Chunk chunk) => _chunks.TryGetValue(pos, out chunk!);

    public Chunk GetOrCreateChunk(ChunkPos pos) => _chunks.GetOrAdd(pos, static p => new Chunk(p));

    /// <summary>Drops a chunk from the store. Used by streaming when it leaves the load radius.</summary>
    public bool RemoveChunk(ChunkPos pos) => _chunks.TryRemove(pos, out _);

    /// <summary>Reads a world block. Unloaded space reads as air.</summary>
    public BlockId GetBlock(int wx, int wy, int wz)
    {
        var pos = ChunkPos.FromWorld(wx, wy, wz);
        if (!_chunks.TryGetValue(pos, out var chunk)) return BlockId.Air;
        return chunk.Get(wx & Chunk.SizeMask, wy & Chunk.SizeMask, wz & Chunk.SizeMask);
    }

    /// <summary>Reads packed light at a world position. Unloaded space reads as dark.</summary>
    public ushort GetLight(int wx, int wy, int wz)
    {
        var pos = ChunkPos.FromWorld(wx, wy, wz);
        if (!_chunks.TryGetValue(pos, out var chunk)) return 0;
        return chunk.GetLight(wx & Chunk.SizeMask, wy & Chunk.SizeMask, wz & Chunk.SizeMask);
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
