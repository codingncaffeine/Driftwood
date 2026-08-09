using Driftwood.Core.World;

namespace Driftwood.Core.Blocks;

/// <summary>
/// Works out what falls down when the thing holding it up is taken away.
/// </summary>
/// <remarks>
/// <para>The other half of <see cref="ConnectionTable"/>, and deliberately not the same pass. A
/// connecting block re-picks its <em>shape</em> and stays where it is; a supported block is removed
/// outright and leaves something on the floor, so one of them can run inside the streamer and the
/// other has to hand its results back to whoever owns the item layer.</para>
/// <para><b>A queue and a visited set, not one ring.</b> The argument that makes
/// <see cref="ConnectionTable"/> safe with neither — that a swap changes a shape and never changes
/// whether a neighbour would join on — does not hold here at all: removing a block is exactly the
/// event that can make a seventh cell want to move, and a door's upper half stands on its lower.
/// Building the cheap version first would work for torches and fail silently the day the first
/// stacked shape arrives, which is the next thing after this one.</para>
/// </remarks>
public sealed class SupportTable
{
    /// <summary>Which side each block needs something on, or -1. Indexed by raw block id.</summary>
    private readonly int[] _needs;

    /// <summary>Anything a foot would rest on. Indexed by raw block id.</summary>
    private readonly bool[] _solid;

    /// <summary>A whole block face to fix something to. Indexed by raw block id.</summary>
    private readonly bool[] _firm;

    /// <summary>Which way the rest of each block is, or -1. Indexed by raw block id.</summary>
    private readonly int[] _partner;

    /// <summary>What a cell keeps when the block in it falls: the water a wet one stood in.</summary>
    /// <remarks>
    /// ⛔ Writing bare air here would quietly delete the sea from around a shed block: seagrass over
    /// a mined floor, a wet ladder losing its wall. One answer, shared with mining and the blast,
    /// through <see cref="Waterlogging.Remains"/>.
    /// </remarks>
    private readonly ushort[] _remains;

    /// <summary>Reused between calls so a pass costs no allocation.</summary>
    private readonly Queue<(int X, int Y, int Z)> _pending = [];

    /// <summary>What is waiting in <see cref="_pending"/> right now, so nothing is queued twice.</summary>
    /// <remarks>
    /// ⚠ <b>Emptied on dequeue, not kept as a "has been looked at" set.</b> A cell that held when it
    /// was checked can stop holding later, when the block it was leaning on comes down further along
    /// the same pass — so a visited set that never forgets would answer for a world that has since
    /// changed and leave the thing standing in mid-air. Termination does not need it either: every
    /// enqueue after the seeds is caused by a block actually falling, and a cell that has fallen is
    /// air and is skipped, so the work is bounded by the number of blocks there were to take down.
    /// </remarks>
    private readonly HashSet<(int X, int Y, int Z)> _queued = [];

    public SupportTable(BlockRegistry registry)
    {
        _needs = new int[registry.Count];
        _solid = new bool[registry.Count];
        _firm = new bool[registry.Count];
        _partner = new int[registry.Count];
        _remains = new ushort[registry.Count];

        var wet = new Waterlogging(registry);

        for (var id = 0; id < registry.Count; id++)
        {
            var type = registry[(ushort)id];
            _needs[id] = type.SupportFace;
            _solid[id] = type.Solid;
            _firm[id] = type.Solid && type.Model.IsFullCube;
            _partner[id] = type.PartnerFace;
            _remains[id] = wet.Remains(new BlockId((ushort)id)).Value;
        }
    }

    /// <summary>How many block kinds need holding up, for the check that says this table is not empty.</summary>
    public int Supported
    {
        get
        {
            var count = 0;
            foreach (var face in _needs) if (face >= 0) count++;
            return count;
        }
    }

    /// <summary>The side a block needs something on, or -1.</summary>
    public int NeedsOn(BlockId id) => id.Value < _needs.Length ? _needs[id.Value] : -1;

    /// <summary>True when whatever is at this cell still has something to hold on to.</summary>
    /// <remarks>
    /// <para>True for everything that needs nothing, so a caller can ask about any cell without
    /// first asking whether the question applies.</para>
    /// <para>Half of a block whose other half is gone is not held either, and that is the rule that
    /// makes a door one object rather than two that happen to be stacked. It is symmetric on
    /// purpose: each half asks after the other and neither is the real one, so breaking either end
    /// takes the whole thing and no code anywhere has to know which end was struck.</para>
    /// </remarks>
    public bool Holds(VoxelWorld world, int x, int y, int z)
    {
        var here = world.GetBlock(x, y, z).Value;
        if (here >= _needs.Length) return true;

        var half = _partner[here];
        if (half >= 0)
        {
            var (px, py, pz) = Faces.Normals[half];
            var other = world.GetBlock(x + px, y + py, z + pz).Value;

            // The other half has to be one, and has to be pointing back at this one. A door beside
            // a door is two doors, and neither of them holds the other up.
            if (other >= _partner.Length) return false;
            if (_partner[other] != Placeable.Opposite(half)) return false;
        }

        var face = _needs[here];
        if (face < 0) return true;

        var (dx, dy, dz) = Faces.Normals[face];
        var against = world.GetBlock(x + dx, y + dy, z + dz).Value;
        if (against >= _solid.Length) return false;

        return Placeable.NeedsFirmSupport(face) ? _firm[against] : _solid[against];
    }

    /// <summary>
    /// Takes down everything around an edit that has lost what was holding it up, and says what fell.
    /// </summary>
    /// <param name="fell">
    /// Appended with each cell cleared and the block that was in it, so the caller can leave
    /// something on the floor. Never cleared here — a caller may be collecting several edits.
    /// </param>
    /// <returns>How many blocks came down.</returns>
    /// <remarks>
    /// The edited cell itself is a seed as well as its six neighbours, because an edit can be a
    /// placement: putting a block into the cell a torch is standing in is the same event as taking
    /// away the one under it, and only one of those two reads as "the wall went".
    /// </remarks>
    public int Shed(VoxelWorld world, int x, int y, int z, List<(int X, int Y, int Z, BlockId Was)> fell)
    {
        _pending.Clear();
        _queued.Clear();

        Seed(x, y, z);
        Ring(x, y, z);

        var count = 0;
        while (_pending.TryDequeue(out var cell))
        {
            _queued.Remove(cell);

            var was = world.GetBlock(cell.X, cell.Y, cell.Z);
            if (was.IsAir) continue;
            if (Holds(world, cell.X, cell.Y, cell.Z)) continue;

            // What the cell keeps is the water a wet block stood in — air for everything else.
            world.SetBlock(cell.X, cell.Y, cell.Z, new BlockId(_remains[was.Value]));
            fell.Add((cell.X, cell.Y, cell.Z, was));
            count++;

            // What was leaning on this one is in the same position it was, which is the whole
            // reason there is a queue: a door's upper half stands on its lower and nothing else in
            // the pass would ever look at it.
            Ring(cell.X, cell.Y, cell.Z);
        }

        return count;

        void Ring(int cx, int cy, int cz)
        {
            for (var face = 0; face < Faces.Count; face++)
            {
                var (dx, dy, dz) = Faces.Normals[face];
                Seed(cx + dx, cy + dy, cz + dz);
            }
        }

        void Seed(int cx, int cy, int cz)
        {
            if (!Gen.TerrainGenerator.InWorld(cy)) return;

            // Unloaded space reads as air, so a cell whose chunk is not here would look unsupported
            // and be "cleared" — which would create the chunk to write air into it.
            if (!world.TryGetChunk(ChunkPos.FromWorld(cx, cy, cz), out _)) return;

            if (_queued.Add((cx, cy, cz))) _pending.Enqueue((cx, cy, cz));
        }
    }
}
