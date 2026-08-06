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

    /// <summary>
    /// Loaded edits waiting for the chunk they belong to, keyed by that chunk.
    /// </summary>
    /// <remarks>
    /// Guarded rather than concurrent because the two sides are not symmetric: it is filled once, by
    /// whoever is reading a save, and then drained one chunk at a time by whichever generation
    /// worker happens to build that chunk. A lock held for a dictionary lookup, once per chunk
    /// generated, is not measurable next to generating the chunk.
    /// </remarks>
    private readonly Dictionary<ChunkPos, List<(int X, int Y, int Z, BlockId Id)>> _pending = [];

    /// <summary>How many loaded edits are still waiting for their chunk.</summary>
    public int PendingEdits
    {
        get
        {
            lock (_pending)
            {
                var held = 0;
                foreach (var list in _pending.Values) held += list.Count;
                return held;
            }
        }
    }

    /// <summary>
    /// Takes a saved edit, to be put back when the chunk it belongs to has been generated.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>It does not write the block, and that is the entire point.</b> This used to call
    /// <see cref="SetBlock"/>, which calls <see cref="GetOrCreateChunk"/> — so a saved edit landing
    /// in a chunk the streamer had not generated yet made an empty chunk to hold it, and generation
    /// then filled that chunk in over the top. Every edit in a loaded world came back as whatever
    /// the generator would have put there, which is to say the world came back without anything
    /// anybody had built in it. Measured, not argued: the audit's load check reported a saved bench
    /// coming back as stone.</para>
    /// <para>So the cell is recorded — in <see cref="Edits"/>, so re-saving keeps it whether or not
    /// anybody ever walks to it — and held until <see cref="ApplyPending"/> is called with the chunk
    /// it belongs to, which generation does the moment it has finished making one.</para>
    /// <para>⚠ <b>A save has to be read before streaming starts.</b> Nothing here can put an edit
    /// into a chunk that has already been generated, because by then generation has been and gone.
    /// The order is a requirement on the caller, and the audit's load check runs in that order on
    /// purpose.</para>
    /// <para>It also leaves <see cref="Changed"/> alone. Marking a world dirty as it opens is how
    /// every load ends up followed by an autosave of the thing just loaded.</para>
    /// </remarks>
    public void Restore(int wx, int wy, int wz, BlockId id)
    {
        _edits[(wx, wy, wz)] = id;

        var pos = ChunkPos.FromWorld(wx, wy, wz);

        lock (_pending)
        {
            if (!_pending.TryGetValue(pos, out var held)) _pending[pos] = held = [];
            held.Add((wx, wy, wz, id));
        }
    }

    /// <summary>
    /// Puts any loaded edits belonging to this chunk into it, and forgets them.
    /// </summary>
    /// <remarks>
    /// <para>Called by generation once a chunk is made and decorated and <em>before</em> it is
    /// declared generated, so nothing has read it yet — not the mesher, and not the first light
    /// flood, which waits on that same flag and therefore lights the world as it actually is rather
    /// than as the generator left it. That ordering is what makes a loaded world need no relight.
    /// </para>
    /// <para>Taking the list out under the lock is what makes it safe to call from several workers
    /// at once: whichever one gets it is the only one that can, and the writes that follow go to a
    /// chunk nobody else is touching.</para>
    /// </remarks>
    /// <returns>How many cells were put back.</returns>
    public int ApplyPending(Chunk chunk)
    {
        List<(int X, int Y, int Z, BlockId Id)>? held;

        lock (_pending)
        {
            if (!_pending.Remove(chunk.Position, out held)) return 0;
        }

        foreach (var (wx, wy, wz, id) in held)
            chunk.Set(wx & Chunk.SizeMask, wy & Chunk.SizeMask, wz & Chunk.SizeMask, id);

        return held.Count;
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
        Write(wx, wy, wz, id, create: true);

        _edits[(wx, wy, wz)] = id;
        Changed = true;
    }

    /// <summary>
    /// Writes a cell that flowed there rather than one somebody changed.
    /// </summary>
    /// <remarks>
    /// <para>⛔⛔ <b>The whole reason this exists is that <see cref="SetBlock"/> is the save's edit
    /// log</b>, deliberately — it is hooked there rather than in the streamer precisely so that it
    /// catches the connection rewire and the support-shed passes as well as a player's own swing. A
    /// settling river through that door writes a few thousand entries to disk and sets
    /// <see cref="Changed"/>, so the two-minute autosave fires on a world nobody touched.</para>
    /// <para>⛳ <b>And it does not need to.</b> At rest, a fluid configuration is a deterministic
    /// function of where the sources are and where the solids are — and both of those are already
    /// described by the seed plus the player's edit diff. Flow is recomputed when a world opens and
    /// never stored. The obvious objection, "I channelled lava into a moat and blocked the channel,
    /// and on reload the moat is gone", is answered by the model itself: it was gone before the save,
    /// because fluid cut off from a source drains. "Persists" and "is fed by a source" are the same
    /// statement.</para>
    /// <para>⚠ <b>It will not create a chunk.</b> Fluid must stall at the edge of the loaded world
    /// rather than pour into it; a write that made the chunk would be filled in over the top by
    /// generation, which is the exact failure a saved edit had before <see cref="Restore"/> existed.
    /// </para>
    /// <para>The line, stated once: <b>reversible fluid state is derived; irreversible terrain change
    /// is saved.</b> The only thing that crosses it is a lava/water reaction, which goes through
    /// <see cref="SetBlock"/> like anything else that cannot be undone.</para>
    /// </remarks>
    public void SetFluid(int wx, int wy, int wz, BlockId id) => Write(wx, wy, wz, id, create: false);

    /// <summary>
    /// Puts a block in a cell and dirties any neighbouring chunk whose mesh it could change — a block
    /// on a chunk seam is a face-culling input for the chunk on the other side, and forgetting this
    /// leaves a hole in the world.
    /// </summary>
    private void Write(int wx, int wy, int wz, BlockId id, bool create)
    {
        var pos = ChunkPos.FromWorld(wx, wy, wz);

        Chunk chunk;
        if (create) chunk = GetOrCreateChunk(pos);
        else if (!_chunks.TryGetValue(pos, out chunk!)) return;

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
