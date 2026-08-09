using Driftwood.Core.Gen;
using Driftwood.Core.World;

namespace Driftwood.Core.Blocks;

/// <summary>
/// Everything the flow needs to know about a block, read off the registry once.
/// </summary>
/// <remarks>
/// Flat arrays indexed by raw block id, the same shape <see cref="SupportTable"/> and the mesher's
/// tables use, because the inner loop of a settling river asks these questions a few hundred
/// thousand times and a dictionary or a virtual call would be most of its cost.
/// </remarks>
public sealed class FluidTable
{
    /// <summary>A full cell. Sources hold this; a flowing tail holds less.</summary>
    public const int MaxLevel = 8;

    private readonly FluidKind[] _kind;
    private readonly byte[] _level;
    private readonly bool[] _falling;
    private readonly bool[] _source;
    private readonly bool[] _replaceable;
    private readonly bool[] _wet;

    /// <summary>kind, level, falling → block id. Built from the registry, never written out.</summary>
    private readonly ushort[] _states;

    private const int Kinds = 3;   // None, Water, Lava

    /// <summary>
    /// What a lava SOURCE leaves when water reaches it, and what a flowing tail leaves.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Two results on purpose, and the reason is that one would make coal infinite.</b>
    /// Coal is fuel AND black dye AND every torch in the game, and charcoal is already the renewable
    /// one; a second infinite source would flatten the early economy outright. Consuming a source
    /// makes it a <em>transformation</em> — a finite reward for a clever bit of plumbing — while a
    /// flowing edge quenched into rubble is the stone generator the genre expects and is welcome to
    /// keep, because rubble is worth almost nothing and the source that fed it is still there.</para>
    /// <para>⛳ Both terminate. Rubble is solid, so it blocks the path that made it; making another
    /// costs a player one swing. And quenching a source strictly reduces the number of sources there
    /// are, so a lake crusts over and stops rather than crusting for ever.</para>
    /// <para>⚠ <b>Both are real world edits and go in the save</b>, unlike everything else the flow
    /// does. That is the line: reversible fluid state is derived, irreversible terrain change is
    /// written down. It costs one entry per cell and there are never many of them, because the
    /// generator never puts the two fluids near each other — water is only ever placed at or under
    /// the sea, and lava never reaches above y −84.</para>
    /// </remarks>
    public BlockId Quenched { get; }

    public BlockId Chilled { get; }

    public FluidTable(BlockRegistry registry)
    {
        _kind = new FluidKind[registry.Count];
        _level = new byte[registry.Count];
        _falling = new bool[registry.Count];
        _source = new bool[registry.Count];
        _replaceable = new bool[registry.Count];
        _wet = new bool[registry.Count];
        _states = new ushort[Kinds * (MaxLevel + 1) * 2];

        for (var id = 0; id < registry.Count; id++)
        {
            var type = registry[(ushort)id];

            _kind[id] = type.Fluid;
            _level[id] = (byte)type.FluidLevel;
            _falling[id] = type.FluidFalling;
            _source[id] = type.FluidSource;
            _replaceable[id] = type.Replaceable;
            _wet[id] = type.Waterlogged;

            if (type.Fluid == FluidKind.None) continue;

            // ⛔⛔ ONLY A TRUE FLUID MAY CLAIM ITS STATE SLOT. Every waterlogged block reads
            // (Water, 8, still) — that is what makes the flow, drowning and light treat its cell
            // as water — so without this guard the LAST wet form registered would own the slot,
            // and Block(Water, 8, false) would answer it: a settling river writing seagrass into
            // every cell it fills. The audit's guarded-states check goes red the moment this line
            // stops filtering.
            if (type.Waterlogged) continue;

            _states[StateIndex(type.Fluid, type.FluidLevel, type.FluidFalling)] = (ushort)id;
        }

        Quenched = registry.ByName("coal_block").Id;
        Chilled = registry.ByName("rubble").Id;
    }

    private static int StateIndex(FluidKind kind, int level, bool falling) =>
        (((int)kind * (MaxLevel + 1)) + level) * 2 + (falling ? 1 : 0);

    public FluidKind KindOf(ushort block) => _kind[block];

    public int LevelOf(ushort block) => _level[block];

    public bool IsFalling(ushort block) => _falling[block];

    public bool IsSource(ushort block) => _source[block];

    /// <summary>True when a fluid or a placed block may take this cell without asking.</summary>
    public bool Replaceable(ushort block) => _replaceable[block];

    /// <summary>True for a block sharing its cell with water — a real block the flow reads as a
    /// full still source and never overwrites.</summary>
    public bool IsWet(ushort block) => _wet[block];

    /// <summary>Whether either of a pair is a fluid at all, for the mesher's face test.</summary>
    public bool AnyFluid(ushort a, ushort b) =>
        _kind[a] != FluidKind.None || _kind[b] != FluidKind.None;

    /// <summary>
    /// True when a fluid's face toward this neighbour is inside the same body of fluid.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The mesher's own test was identity — <c>neighbour == here</c> — and identity stopped
    /// being the right question the moment a fluid had levels.</b> That test culls water against
    /// water and leaves water level 7 against water level 6 drawing <em>both</em> of the faces
    /// between them: a double surface running the length of every river, invisible in a block census
    /// and in a vertex total, and exactly the sort of thing that reads as a renderer bug.
    /// <para>Not symmetric on purpose. A tall cell's face toward a short one is a real surface and
    /// has to be drawn; a short cell's face toward a tall one is under it and does not.</para>
    /// </remarks>
    public bool HiddenBy(ushort here, ushort neighbour) =>
        _kind[here] != FluidKind.None
        && _kind[neighbour] == _kind[here]
        && _level[neighbour] >= _level[here];

    /// <summary>The block for one fluid state, or air.</summary>
    public BlockId Block(FluidKind kind, int level, bool falling)
    {
        if (kind == FluidKind.None || level <= 0) return BlockId.Air;
        return new BlockId(_states[StateIndex(kind, Math.Min(level, MaxLevel), falling)]);
    }

    /// <summary>
    /// What one sideways step costs, which is where the Emberdeep gets its character.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>A property of the cell being flowed into, so it may depend on depth and the fixpoint is
    /// still unique.</b> Lava reaches three blocks from a source in the ordinary world and seven in
    /// the Emberdeep — so the deep does not merely have more lava in it, it has lava that behaves
    /// differently, and a river down there is a river rather than a puddle. Water is water anywhere.
    /// </remarks>
    public static int Decay(FluidKind kind, int y) => kind switch
    {
        FluidKind.Lava => y < TerrainGenerator.EmberdeepTop ? 1 : 2,
        _ => 1,
    };
}

/// <summary>
/// Makes fluid flow: down before sideways, weakening as it spreads, settling to a state that
/// depends only on where the sources and the solids are.
/// </summary>
/// <remarks>
/// <para>⛳ <b>A fluid is light, and that is the design rather than an analogy.</b>
/// <see cref="Lighting.LightEngine"/> computes the least fixpoint of a monotone level function over
/// the grid — every cell keeps the best thing offered it and only passes it on when it improves —
/// which is why light can be flooded chunk by chunk in whatever order the player walks and still
/// land on the same answer. This is the same fixpoint with sources instead of emitters, full
/// strength downward instead of a beam, and one rule of its own: <b>a cell that can fall does not
/// feed sideways</b>.</para>
/// <para>⛳⛳ <b>There is no tear-down pass, and the reason is worth writing down.</b> Light needs one
/// because a cell can be lit by a neighbour that is lit by it — light has no direction. A fluid's
/// support graph cannot contain a cycle: a flowing cell's level is <em>strictly</em> below the
/// neighbour that feeds it, or it is fed from directly above, and y strictly decreases that way. So
/// the graph is a DAG ordered by (height, level), stale support is impossible, and re-resolving a
/// cell from its neighbours until nothing changes reaches the true answer on its own. Removing a
/// source drains the river by arithmetic rather than by a second algorithm.</para>
/// <para><b>What that buys.</b> Termination is a theorem: levels are bounded, the graph is acyclic,
/// and a cell can only be re-visited as many times as it has levels to lose. Order independence is
/// a theorem too, and it is what makes a save that stores no fluid <em>correct</em> rather than
/// merely small — the settled world is a function of the sources and the solids, both of which are
/// already a seed plus the player's own edits.</para>
/// <para>⛔ <b>Unloaded space reads as air, which is not the same as being empty.</b> A river at the
/// edge of the loaded world must stall rather than pour into a chunk nobody has generated — both
/// because the write would create that chunk and because generation would then fill over the top of
/// it. Every read that decides whether fluid may move asks whether the cell is loaded first.</para>
/// </remarks>
public sealed class FluidEngine
{
    public const int MaxLevel = FluidTable.MaxLevel;

    private readonly FluidTable _table;

    private readonly Queue<(int X, int Y, int Z)> _pending = [];

    /// <summary>
    /// Cells a player's own edit put in, drained before anything else.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The same lesson the lighting thread's edit queue taught, in the same session.</b> One
    /// queue is fair and fairness is wrong here: a chunk arriving can wait a moment, and a block
    /// somebody just broke cannot. Whatever else is in flight — a cave system filling, a river
    /// settling after a wall came down — the water beside the block a player is standing at has to
    /// arrive within a tick or two, or the whole feature reads as not working.
    /// <para>⛳ Safe because the settled state does not depend on the order cells are looked at in.
    /// The fixpoint is unique; the queue only decides what a player watches happen first.</para>
    /// </remarks>
    private readonly Queue<(int X, int Y, int Z)> _urgent = [];

    /// <summary>
    /// What is waiting in <see cref="_pending"/>, so nothing is queued twice.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Emptied on dequeue, never kept as a "has been looked at" set</b> — the same rule
    /// <see cref="SupportTable"/> is written to, and for the same reason. A cell that resolved
    /// correctly can stop being correct later in the same pass, when the thing feeding it drains.
    /// </remarks>
    private readonly HashSet<(int X, int Y, int Z)> _queued = [];

    private static readonly (int X, int Z)[] Sides =
        [(1, 0), (-1, 0), (0, 1), (0, -1)];

    public FluidEngine(FluidTable table) => _table = table;

    public FluidEngine(BlockRegistry registry) : this(new FluidTable(registry)) { }

    public FluidTable Table => _table;

    /// <summary>Cells waiting to be looked at.</summary>
    public int Pending => _pending.Count + _urgent.Count;

    /// <summary>Cells this engine has changed since it was made. For the instruments.</summary>
    public long Changed { get; private set; }

    /// <summary>Cells taken off the queue since it was made, changed or not.</summary>
    public long Visited { get; private set; }

    public void ResetCounters()
    {
        Changed = 0;
        Visited = 0;
    }

    /// <summary>Asks for a cell and everything touching it to be looked at again.</summary>
    /// <remarks>
    /// The entry point for anything that changes the world: a block broken, a bucket emptied, a
    /// chunk arriving. The cell itself as well as its six neighbours, because an edit can be a
    /// <em>placement</em> — putting a block into a cell full of water is the same event as taking
    /// away the wall beside it, and only one of those reads as "something moved".
    /// </remarks>
    public void Touch(int x, int y, int z, bool urgent = false)
    {
        _urgentNow = urgent;
        Seed(x, y, z);
        Ring(x, y, z);
        _urgentNow = false;
    }

    /// <summary>Whether the cells being seeded right now go to the front. Set only by Touch.</summary>
    private bool _urgentNow;

    /// <summary>Queues every fluid cell in a chunk, and the shell around it.</summary>
    /// <remarks>
    /// ⛳ <b>What makes a river resume at a boundary.</b> A fall that stalled because the chunk below
    /// it was not loaded has no reason to start again on its own — nothing in that chunk changed,
    /// because it did not exist. The shell is what re-offers it: the fluid sitting one cell above the
    /// new chunk is asked again, finds somewhere to go, and the fall carries on.
    /// </remarks>
    public void TouchChunk(VoxelWorld world, ChunkPos pos)
    {
        if (!world.TryGetChunk(pos, out var chunk)) return;

        var (ox, oy, oz) = pos.Origin;
        var raw = chunk.Raw;

        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        for (var x = 0; x < Chunk.Size; x++)
        {
            var here = raw[Chunk.Index(x, y, z)];
            if (_table.KindOf(here) == FluidKind.None) continue;

            // ⛔ ONLY THE CELLS WITH SOMEWHERE TO GO, and this is not an optimisation to be talked
            // out of. Generation places bulk lava at rest, so a chunk at the floor of the world can
            // be 32,768 cells of it — every one of which would go into the queue, be looked at, and
            // be found already correct. At two hundred chunks in the loaded set that is millions of
            // queue entries per walk for no writes at all. A cell walled in by its own kind cannot
            // move and does not need asking.
            if (!CanSpread(world, ox + x, oy + y, oz + z, here)) continue;

            Seed(ox + x, oy + y, oz + z);
        }

        const int last = Chunk.Size - 1;
        for (var a = 0; a <= last; a++)
        for (var b = 0; b <= last; b++)
        {
            Offer(ox + a, oy + b, oz - 1);
            Offer(ox + a, oy + b, oz + Chunk.Size);
            Offer(ox - 1, oy + a, oz + b);
            Offer(ox + Chunk.Size, oy + a, oz + b);
            Offer(ox + a, oy - 1, oz + b);
            Offer(ox + a, oy + Chunk.Size, oz + b);
        }

        void Offer(int wx, int wy, int wz)
        {
            var block = world.GetBlock(wx, wy, wz).Value;
            if (_table.KindOf(block) == FluidKind.None) return;

            // ⛔ AND THE SAME FILTER AS THE CELLS INSIDE, which is what separates "stalled at this
            // seam" from "sitting in the middle of the ocean". The shell of a sea chunk is a
            // thousand water cells with nowhere to go; offering all of them cost 70,405 queued cells
            // per walk to the beach, every one of which was looked at and found already correct.
            // ⛳ It is exactly the right question: before this chunk arrived a stalled cell could not
            // spread, because the space it wanted was unloaded and unloaded is not empty. Now it can.
            if (!CanSpread(world, wx, wy, wz, block)) return;

            // ⛔ THE RING, NOT THE CELL. A fall stalled at a seam is already in the state it should
            // be in — full, falling, nothing to change — so re-examining it does nothing and it does
            // not wake its neighbours, because a cell only wakes them when it moves. What has to be
            // asked is the cell on the other side of the seam, in the chunk that has just arrived.
            Touch(wx, wy, wz);
        }
    }

    /// <summary>
    /// Looks at up to <paramref name="budget"/> cells, writing whatever has to move.
    /// </summary>
    /// <param name="changed">Appended with every cell written, for the caller to relight and remesh.</param>
    /// <returns>How many cells were taken off the queue.</returns>
    /// <remarks>
    /// The budget is what keeps a flood from being a frame hitch. A cave system filling takes many
    /// ticks, which is what it should look like, and the per-tick cost is bounded and measurable
    /// rather than a cliff nobody sees until somebody breaks the wrong wall.
    /// </remarks>
    public int Step(VoxelWorld world, int budget, List<(int X, int Y, int Z)> changed)
    {
        var looked = 0;

        while (looked < budget && NextCell(out var cell))
        {
            _queued.Remove(cell);
            looked++;
            Visited++;

            if (!Advance(world, cell.X, cell.Y, cell.Z)) continue;

            changed.Add(cell);
            Changed++;
        }

        return looked;
    }

    /// <summary>
    /// Runs to quiescence.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>What a load does, and what the checks compare against.</b> A save stores no flowing
    /// fluid at all — the settled state is a function of the sources and the solids, and both of
    /// those are already the seed plus the player's own edits — so opening a world settles it rather
    /// than reading it. That "budgeted incremental result equals unbudgeted instant result" is the
    /// order-independence claim in a form that can be run.
    /// </remarks>
    /// <param name="limit">A backstop. Termination is a theorem; this catches the theorem being wrong.</param>
    public int Settle(VoxelWorld world, List<(int X, int Y, int Z)> changed, int limit = 4_000_000)
    {
        var looked = 0;

        while (Pending > 0 && looked < limit)
            looked += Step(world, Math.Min(4096, limit - looked), changed);

        return looked;
    }

    /// <summary>Brings one cell into line with its neighbours. True when it actually moved.</summary>
    private bool Advance(VoxelWorld world, int x, int y, int z)
    {
        if (!TerrainGenerator.InWorld(y)) return false;
        if (!Loaded(world, x, y, z)) return false;

        var have = world.GetBlock(x, y, z).Value;

        // The two fluids meeting is resolved before either of them is allowed to move, or a cell
        // would flow into the space the reaction is about to fill.
        if (React(world, x, y, z, have)) return true;

        // Never write over anything that is not ours to write over. A source resolves to itself and
        // stops here; stone stops here because stone is not replaceable.
        if (!_table.Replaceable(have)) return false;

        var (kind, level, falling) = Resolve(world, x, y, z, have);
        var want = _table.Block(kind, level, falling);
        if (want.Value == have) return false;

        world.SetFluid(x, y, z, want);

        // Everything that could now want to move: the four sides, the cell below that may start
        // filling, and the cell above that may stop falling.
        Ring(x, y, z);
        return true;
    }

    /// <summary>
    /// Turns lava that has met water into something solid. True when it did.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>The lava is what changes, whichever of the two is being looked at.</b> A water cell
    /// that finds lava beside it does not transform — it asks the lava to look again, which keeps the
    /// rule in one place and means the result cannot depend on which of the pair came off the queue
    /// first. That matters: the whole settle is order-independent and a reaction that broke it would
    /// make the save wrong rather than merely surprising.</para>
    /// <para>⚠ <b>Through <see cref="VoxelWorld.SetBlock"/> and not <see cref="VoxelWorld.SetFluid"/>,
    /// which is the only place in the flow that does that.</b> A quenched source does not come back
    /// when the world is reopened, so it is not derivable and has to be written down.</para>
    /// </remarks>
    private bool React(VoxelWorld world, int x, int y, int z, ushort have)
    {
        var kind = _table.KindOf(have);
        if (kind == FluidKind.None) return false;

        var other = kind == FluidKind.Lava ? FluidKind.Water : FluidKind.Lava;
        var asked = false;

        for (var face = 0; face < Faces.Count; face++)
        {
            var (dx, dy, dz) = Faces.Normals[face];
            int nx = x + dx, ny = y + dy, nz = z + dz;

            if (!Loaded(world, nx, ny, nz)) continue;
            if (_table.KindOf(world.GetBlock(nx, ny, nz).Value) != other) continue;

            if (kind == FluidKind.Water)
            {
                Seed(nx, ny, nz);
                asked = true;
                continue;
            }

            world.SetBlock(x, y, z, _table.IsSource(have) ? _table.Quenched : _table.Chilled);
            Ring(x, y, z);
            return true;
        }

        // A water cell that asked the lava to react has not moved and must not be counted as having.
        _ = asked;
        return false;
    }

    /// <summary>The state this cell should hold, given what is around it.</summary>
    private (FluidKind Kind, int Level, bool Falling) Resolve(
        VoxelWorld world, int x, int y, int z, ushort have)
    {
        // A source is permanent until a bucket or a block takes it away. Nothing derives one.
        if (_table.IsSource(have)) return (_table.KindOf(have), MaxLevel, false);

        // Fed from directly above: full strength, and falling. This is the rule that makes a
        // waterfall reach the floor at the strength it left the top rather than fading out on the
        // way down, and it is the same exception sunlight already has for a beam going straight
        // down through open air.
        if (TerrainGenerator.InWorld(y + 1) && Loaded(world, x, y + 1, z))
        {
            var above = world.GetBlock(x, y + 1, z).Value;
            var kind = _table.KindOf(above);
            if (kind != FluidKind.None) return (kind, MaxLevel, true);
        }

        // Otherwise the best a side neighbour can offer, ignoring any that has somewhere to fall.
        var bestKind = FluidKind.None;
        var best = 0;

        foreach (var (dx, dz) in Sides)
        {
            int nx = x + dx, nz = z + dz;
            if (!Loaded(world, nx, y, nz)) continue;

            var n = world.GetBlock(nx, y, nz).Value;
            var kind = _table.KindOf(n);
            if (kind == FluidKind.None) continue;

            // ⛳ THE ONE RULE THAT MAKES THIS A FLUID RATHER THAN A GLOW. A cell with somewhere to
            // fall sends everything down and feeds nothing sideways, which is why a river runs along
            // a channel instead of spreading into a disc, and why breaking the floor under one
            // drains it rather than widening it.
            if (Draining(world, nx, y, nz, kind)) continue;

            var level = _table.LevelOf(n) - FluidTable.Decay(kind, y);
            if (level <= best) continue;

            best = level;
            bestKind = kind;
        }

        return best > 0 ? (bestKind, best, false) : (FluidKind.None, 0, false);
    }

    /// <summary>True when this fluid cell has somewhere below it to go.</summary>
    private bool Draining(VoxelWorld world, int x, int y, int z, FluidKind kind)
    {
        if (!TerrainGenerator.InWorld(y - 1)) return false;

        // ⛔ Unloaded is not empty. Reading absent space as air here would have a river decide it
        // was pouring into a chunk nobody has generated — and the write that followed would create
        // that chunk, which generation would then fill in over the top of.
        if (!Loaded(world, x, y - 1, z)) return false;

        var below = world.GetBlock(x, y - 1, z).Value;
        if (!_table.Replaceable(below)) return false;

        var there = _table.KindOf(below);
        if (there == FluidKind.None) return true;          // air: everything goes down
        if (there != kind) return false;                   // the other fluid; #70 resolves that

        // ⛔ FULL IS NOT THE QUESTION — STILL IS, and getting that wrong makes a waterfall a cone.
        // A falling cell is full by definition, so "is the cell below full" said no cell in a
        // twenty-block column was draining, and every one of them fed sideways: measured as a pool
        // six cells wide where the decay allows three, at every height of the fall. What stops a
        // drain is a body of fluid that has come to rest under it, which is a source or a partly
        // filled cell — not fluid that is itself still on its way down.
        if (_table.IsFalling(below)) return true;

        return _table.LevelOf(below) < MaxLevel;
    }

    /// <summary>True when any neighbour of this fluid cell is somewhere it could go.</summary>
    /// <remarks>
    /// ⛔⛔ <b>DOWN AND SIDEWAYS. NEVER UP — and getting that wrong made the whole feature look
    /// broken in the game while every check passed.</b> Every cell on the surface of the sea has air
    /// above it, so counting up as "somewhere to go" queued <em>every ocean surface cell in every
    /// chunk</em> the moment it loaded: measured at <b>117,124 cells queued with nothing to do</b>
    /// after a walk to the beach. Not one of them could move, so nothing was ever visibly wrong — but
    /// the queue is first-in-first-out, so a player breaking a block beside the water went to the
    /// back of it and their water arrived some minutes later.
    /// <para>⛳ It was invisible to every fluid check because they all drive the engine in a box they
    /// filled themselves. Reported by the user, in one sentence, from the beach.</para>
    /// </remarks>
    private bool CanSpread(VoxelWorld world, int x, int y, int z, ushort here)
    {
        var kind = _table.KindOf(here);
        var level = _table.LevelOf(here);

        for (var face = 0; face < Faces.Count; face++)
        {
            var (dx, dy, dz) = Faces.Normals[face];
            if (dy > 0) continue;

            int nx = x + dx, ny = y + dy, nz = z + dz;

            if (!Loaded(world, nx, ny, nz)) continue;

            var n = world.GetBlock(nx, ny, nz).Value;
            if (!_table.Replaceable(n)) continue;

            // Its own kind at the same depth or deeper is a wall as far as movement goes.
            if (_table.KindOf(n) == kind && _table.LevelOf(n) >= level) continue;

            return true;
        }

        return false;
    }

    private static bool Loaded(VoxelWorld world, int x, int y, int z) =>
        TerrainGenerator.InWorld(y) && world.TryGetChunk(ChunkPos.FromWorld(x, y, z), out _);

    private void Ring(int x, int y, int z)
    {
        for (var face = 0; face < Faces.Count; face++)
        {
            var (dx, dy, dz) = Faces.Normals[face];
            Seed(x + dx, y + dy, z + dz);
        }
    }

    private void Seed(int x, int y, int z)
    {
        if (!TerrainGenerator.InWorld(y)) return;
        if (!_queued.Add((x, y, z))) return;

        if (_urgentNow) _urgent.Enqueue((x, y, z));
        else _pending.Enqueue((x, y, z));
    }

    private bool NextCell(out (int X, int Y, int Z) cell) =>
        _urgent.TryDequeue(out cell) || _pending.TryDequeue(out cell);
}
