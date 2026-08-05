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

    /// <summary>
    /// Every cell anybody has changed since the world was made, and what it was changed to.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>This is the whole of what a save has to store about the world.</b> A chunk is a
    /// pure function of seed and position, decoration included — so terrain is never written down,
    /// only the difference between what the generator makes and what somebody built.</para>
    /// <para>⚠ <b>Recorded here rather than in <c>WorldStreamer.EditBlock</c>, and that is the whole
    /// reason it is correct.</b> Generation writes through <see cref="Chunk.Set"/> directly and
    /// never comes through this method, so this is exactly and only "somebody changed the world" —
    /// which catches the connection rewire and the support-shed pass as well as a player's own
    /// swing. Hooking the caller instead would have missed both, and missed them silently.</para>
    /// <para>Kept even when an edit puts a cell back to what the generator would have made. Knowing
    /// a cell was touched is cheaper than asking the generator what it would have been, and the
    /// count of them is small enough that the tidy-up is not worth the risk of getting it wrong.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<(int X, int Y, int Z), BlockId> Edits => _edits;

    private readonly Dictionary<(int X, int Y, int Z), BlockId> _edits = [];

    /// <summary>True once anything has been changed that a save would have to remember.</summary>
    public bool Changed { get; private set; }

    /// <summary>Puts a saved edit back without counting it as a fresh change.</summary>
    /// <remarks>
    /// What loading does. Replaying edits through <see cref="SetBlock"/> would work and would leave
    /// the world marked dirty the instant it was opened, so every load would be followed by an
    /// autosave of the thing just loaded.
    /// </remarks>
    public void Restore(int wx, int wy, int wz, BlockId id)
    {
        SetBlock(wx, wy, wz, id);
        Changed = false;
    }

    /// <summary>Forgets that anything has changed. What a save does once it is safely written.</summary>
    public void Settled() => Changed = false;

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

        _edits[(wx, wy, wz)] = id;
        Changed = true;

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
