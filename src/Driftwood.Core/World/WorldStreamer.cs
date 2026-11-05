using System.Collections.Concurrent;
using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Textures;
using Driftwood.Core.Gen;
using Driftwood.Core.Lighting;
using Driftwood.Core.Meshing;

namespace Driftwood.Core.World;

/// <summary>
/// Keeps the world loaded around a moving point: generates chunks that come into range, meshes
/// them once their neighbours exist, and drops the ones left behind.
/// </summary>
/// <remarks>
/// <para>Three stages and three radii. A chunk cannot be meshed until every neighbour it samples
/// has been generated <em>and lit</em> — the mesher reads a one-block skirt in all directions for
/// face culling, ambient occlusion and baked light — and a column cannot be lit until its own
/// eight neighbour columns exist, because light crosses seams. So generation runs two rings wider
/// than meshing rather than one. Without that margin the outermost chunks mesh against absent
/// neighbours, read them as air, and grow a wall of faces that vanishes the moment the neighbour
/// loads.</para>
/// <para>Work is queued rather than done inline. Generation and meshing both run on background
/// workers; the main thread only ever enqueues, drains finished meshes, and decides what to
/// forget. Nothing here touches the graphics API, so the whole pipeline is exercisable headlessly.</para>
/// </remarks>
public sealed class WorldStreamer : IDisposable
{
    private readonly TerrainGenerator _generator;
    private readonly BlockRegistry _registry;
    private readonly BlockTinter _tinter;
    private readonly int _meshRadius;

    // Squared chunk distances, not radii. Each ring has to clear the one inside it by more than a
    // diagonal step, because "all eight neighbours of a chunk at radius r" reaches r + sqrt(2) —
    // a plain +1 leaves the corner neighbours of the outermost meshable chunks outside the
    // lighting ring, and those chunks then wait for light that is never coming.
    private readonly int _meshLimit;
    private readonly int _lightLimit;
    private readonly int _loadLimit;
    private readonly int _dropLimit;

    /// <summary>Squared chunk distance for the ball of chunks round the viewer's own position.</summary>
    private readonly int _deepLimit;

    /// <summary>
    /// Chunk layers above and below the viewer's own that count as near.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Not the same number as the horizontal ring, and it must never share one.</b> The obvious
    /// change — make the ring distances three-dimensional — makes the loaded set about six times
    /// bigger rather than smaller: twelve chunks is 384 blocks and the whole world is only 320 tall,
    /// so "within twelve chunks vertically" is every chunk there is. You can see 384 blocks along the
    /// ground and about eight down a hole, and those are different quantities.
    /// </remarks>
    private const int VerticalBand = 2;

    /// <summary>Wider than <see cref="VerticalBand"/>, so hopping over a seam does not thrash.</summary>
    private const int VerticalDropBand = VerticalBand + 2;

    /// <summary>
    /// How far under its own surface the viewer has to be before the horizon stops being loaded.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>This is what makes the deep cheaper than the surface rather than dearer.</b> There are
    /// exactly two things a player can see — the terrain surface out to the full ring, and the room
    /// they are standing in — and once they are a hundred blocks underground the first of those is
    /// not one of them.
    /// </remarks>
    private const int SurfaceRingDepth = 96;

    /// <summary>
    /// The chunk layers each column's terrain surface can occupy, worked out once per column.
    /// </summary>
    /// <remarks>
    /// Free to ask: <see cref="TerrainGenerator.SurfaceHeight"/> is a pure function of x and z and
    /// needs no chunk to exist. Nine samples over the chunk's own footprint, widened by a layer each
    /// way — terrain's fastest octave has a 38-block wavelength and four blocks of amplitude, so a
    /// sample every sixteen blocks cannot miss more than that and the margin covers it twice over.
    /// </remarks>
    private readonly Dictionary<(int X, int Z), (int Low, int High)> _surfaceBand = [];

    /// <summary>False once the viewer is deep enough that the horizon is rock.</summary>
    private bool _surfaceRing = true;

    /// <summary>Relights taken back to back before a waiting chunk is let through.</summary>
    private const int EditBurst = 64;

    /// <summary>
    /// The fluid, if anybody has handed one over. Null leaves the whole system costing nothing.
    /// </summary>
    /// <remarks>
    /// Owned by the caller rather than by the streamer because the tick rate is a game decision and
    /// the budget is a frame-time one, and neither belongs to the thing that loads chunks. What the
    /// streamer does own is the two moments the flow cannot see for itself: a chunk arriving, which
    /// is where a stalled fall resumes, and a chunk leaving, which is where its queue would
    /// otherwise keep pointing at cells that no longer exist.
    /// </remarks>
    public FluidEngine? Fluids { get; set; }

    /// <summary>The signal pass, when anybody has handed one over. Null costs nothing.</summary>
    public Blocks.SignalPass? Signals { get; set; }

    /// <summary>The track's reshape table, when anybody has handed one over.</summary>
    public Blocks.RailTable? Rails { get; set; }

    /// <summary>
    /// Sinks the signal pass switched — a door swung by a wire rather than a hand — for the
    /// client to voice and then clear. Only ever touched on the main thread.
    /// </summary>
    public List<(int X, int Y, int Z, BlockId Now)> SignalSwitched { get; } = [];

    private readonly VoxelWorld _world;

    /// <summary>Positions whose terrain is complete and safe for a neighbour to sample.</summary>
    private readonly ConcurrentDictionary<ChunkPos, bool> _generated = new();

    /// <summary>Chunks whose light has been computed.</summary>
    private readonly ConcurrentDictionary<ChunkPos, bool> _litChunks = new();

    private readonly ConcurrentQueue<ChunkPos> _generateQueue = new();
    private readonly ConcurrentQueue<ChunkPos> _lightQueue = new();
    private readonly ConcurrentQueue<(int X, int Y, int Z)> _editQueue = new();
    private readonly ConcurrentQueue<ChunkPos> _meshQueue = new();
    private readonly ConcurrentQueue<ChunkMeshData> _finishedMeshes = new();
    private readonly ConcurrentQueue<ChunkPos> _dropped = new();

    /// <summary>
    /// Chunks that have finished generating and have not yet been shown to the fluid.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Handed over on the main thread, never from the worker that made the chunk.</b> The flow
    /// keeps one queue and one in-queue set and is not thread safe by design — it is cheap
    /// specifically because it is not. Generation finishes on any of half a dozen workers, so what
    /// crosses is a position in a concurrent queue and nothing else.
    /// </remarks>
    private readonly ConcurrentQueue<ChunkPos> _fluidReady = new();

    // Main-thread bookkeeping: what has already been asked for, so nothing is queued twice.
    private readonly HashSet<ChunkPos> _requested = [];
    private readonly HashSet<ChunkPos> _lightRequested = [];
    private readonly HashSet<ChunkPos> _meshRequested = [];

    /// <summary>
    /// Chunks queued for meshing or being meshed right now.
    /// </summary>
    /// <remarks>
    /// A chunk can need meshing again after it has already been meshed once — light arriving from a
    /// column that loaded later changes its vertices — so "have we ever meshed this" is not enough
    /// to stop it being queued twice. Without this set the first build of the remesh path enqueued
    /// every dirty chunk on every frame; at 2700 frames a second the queue reached seventy-five
    /// million entries, the workers spent the entire run rebuilding the same handful of chunks, and
    /// the world never finished loading at all.
    /// </remarks>
    private readonly ConcurrentDictionary<ChunkPos, bool> _meshInFlight = new();

    private readonly Task[] _workers;
    private readonly Task _lightWorker;
    private readonly CancellationTokenSource _cancel = new();
    private readonly SemaphoreSlim _work = new(0);

    /// <summary>
    /// Wakes the single lighting thread. Lighting gets its own worker rather than a lock shared
    /// with the pool: one flood writes across many chunks and reads its neighbours while it does,
    /// so two at once on adjacent columns would each see half of the other's work — and a lock
    /// would let every worker in the pool pile up behind it while generation and meshing starve.
    /// </summary>
    private readonly SemaphoreSlim _lightWork = new(0);

    private ChunkPos _lastCentre = new(int.MinValue, int.MinValue, int.MinValue);

    /// <summary>
    /// Where the other players are, on a co-op host: one small ring each, generated and lit, never meshed.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>The host is the world, so the world has to exist wherever a guest is standing</b> —
    /// a guest's block in a chunk the host never loaded used to vanish, and anything the host
    /// simulates (water, wiring, later creatures) can only run where the host has the chunks. A guest
    /// sees with its own generator; the host only needs the cells round it to be real.</para>
    /// <para>Smaller than the host's own: <see cref="VisitorRadius"/> chunks, lit a ring and a half
    /// inside that so every lit chunk's neighbours are generated, and forgotten two rings beyond.</para>
    /// </remarks>
    private readonly List<Ring> _visitors = [];

    private bool _visitorsChanged;

    /// <summary>The co-op host's simulation reach round each guest, in chunks (32 blocks each).</summary>
    public const int VisitorRadius = 4;

    private const int VisitorLoad = VisitorRadius * VisitorRadius;
    private static readonly int VisitorLight = Square(VisitorRadius - 1.5f);
    private static readonly int VisitorKeep = Square(VisitorRadius + 2f);

    private readonly record struct Ring(ChunkPos Centre, bool Surface);

    /// <summary>
    /// Each chunk's terrain as the generator made it — before any edit — as a 64-bit sum, kept for
    /// as long as this streamer lives once <see cref="KeepTerrainSums"/> is on.
    /// </summary>
    /// <remarks>
    /// D3 of the co-op plan: <c>MathF.Pow</c>/<c>Sin</c>/<c>Cos</c> come from the C runtime, and
    /// glibc, musl and Windows may round the last bit differently, so a guest's generator is not
    /// trusted to have made the host's world. Kept past a drop so a guest far from the host can still
    /// be answered for any chunk the host ever made: twenty bytes a chunk.
    /// </remarks>
    private readonly ConcurrentDictionary<ChunkPos, ulong> _terrainSums = new();

    private readonly ConcurrentQueue<(ChunkPos Pos, ulong Sum)> _newSums = new();

    /// <summary>Record <see cref="TryTerrainSum"/> for every chunk generated from now on (co-op only).</summary>
    public bool KeepTerrainSums { get; set; }

    /// <summary>Also queue each new sum for <see cref="TryTakeNewSum"/> — a co-op guest, which sends them.</summary>
    /// <remarks>Off everywhere else, or the queue would only ever grow.</remarks>
    public bool ReportTerrainSums { get; set; }

    /// <summary>
    /// ⚠ Instruments only: runs on each chunk between generation and its sum, to make a guest whose
    /// generator disagrees with the host's — the control for the checksum repair.
    /// </summary>
    public Action<Chunk>? TerrainFault { get; set; }
    private int _generatingCount;
    private int _lightingCount;
    private int _meshingCount;
    private int _restoredEdits;

    public VoxelWorld World => _world;

    /// <summary>
    /// Cells a loaded save has put back so far, as their chunks arrive.
    /// </summary>
    /// <remarks>
    /// Worth counting because the alternative to noticing is not noticing: edits belonging to
    /// chunks nobody ever walks to stay held, which is correct, so "some are still waiting" is the
    /// normal state and cannot be an error on its own. This against
    /// <see cref="VoxelWorld.PendingEdits"/> is what says whether a load is progressing.
    /// </remarks>
    public int RestoredEdits => Volatile.Read(ref _restoredEdits);

    public int WorkerCount => _workers.Length;
    public int LoadedChunks => _world.ChunkCount;
    public int PendingGenerate => _generateQueue.Count + Volatile.Read(ref _generatingCount);
    public int PendingLight => _lightQueue.Count + _editQueue.Count + Volatile.Read(ref _lightingCount);
    public int PendingMesh => _meshQueue.Count + Volatile.Read(ref _meshingCount);
    public int ReadyMeshes => _finishedMeshes.Count;

    public WorldStreamer(
        BlockRegistry registry,
        TerrainGenerator generator,
        int meshRadius,
        int workerCount = 0,
        BlockTinter? tinter = null)
    {
        _registry = registry;
        _generator = generator;
        _tinter = tinter ?? new BlockTinter(new ClimateField(generator.Seed));
        _meshRadius = Math.Max(1, meshRadius);

        // Generation leads lighting by a ring and lighting leads meshing by another; chunks are
        // only forgotten two rings beyond that, so walking back and forth across a boundary does
        // not thrash the same chunk in and out.
        _meshLimit = Square(_meshRadius);
        _lightLimit = Square(_meshRadius + 1.5f);
        _loadLimit = Square(_meshRadius + 3f);
        _dropLimit = Square(_meshRadius + 5f);

        // The ball round the viewer reaches as far as the drop ring, so nothing inside it can be
        // dropped for being vertically out of band and then immediately wanted again — but never
        // further than the horizontal ring itself, because a small world would otherwise load the
        // whole of it twice over.
        _deepLimit = Square(Math.Min(_meshRadius + 5f, 6f));

        _world = new VoxelWorld(registry);

        var count = workerCount > 0 ? workerCount : Math.Max(1, Environment.ProcessorCount - 3);
        _workers = new Task[count];
        for (var i = 0; i < count; i++)
            _workers[i] = Task.Factory.StartNew(WorkerLoop, TaskCreationOptions.LongRunning).Unwrap();

        _lightWorker = Task.Factory.StartNew(LightLoop, TaskCreationOptions.LongRunning).Unwrap();
    }

    /// <summary>
    /// Changes one block and schedules the light and mesh work that follows from it.
    /// </summary>
    /// <remarks>
    /// The write happens here, on the caller's thread, so the change is visible to the next
    /// raycast immediately — a player who breaks a block and swings again must not hit it a second
    /// time. Everything expensive is handed to the lighting thread, which owns relighting and which
    /// dirties whatever chunks the new light reached; the mesh queue picks those up on its own.
    /// </remarks>
    public void EditBlock(int wx, int wy, int wz, BlockId id)
    {
        if (!TerrainGenerator.InWorld(wy)) return;

        var pos = ChunkPos.FromWorld(wx, wy, wz);
        if (!_world.TryGetChunk(pos, out _)) return;

        // A lamp follows the current signal level; a door also remembers what a hand chose. The
        // signal pass therefore needs the old neighbourhood before this write so it can distinguish
        // a real power edge from an unrelated edit beside an already-open door.
        Signals?.CapturePoweredSinks(_world, wx, wy, wz);

        _world.SetBlock(wx, wy, wz, id);
        _editQueue.Enqueue((wx, wy, wz));
        _lightWork.Release();

        // ⛳ Breaking a block beside a river is the whole feature, and it goes to the FRONT of the
        // queue. A chunk arriving can wait a moment; a block somebody just broke cannot, whatever
        // else is settling. The cell and its six neighbours, because an edit can be a placement as
        // easily as a removal and only one of those two reads as "something opened up".
        Fluids?.Touch(wx, wy, wz, urgent: true);

        Rewire(wx, wy, wz);

        // The track re-picks its shapes on the same existence argument the fence pass stands on —
        // one ring, and a reshape write cannot make a further cell want to move. Through the
        // signal door so the mesh and light hear it without re-entering this method.
        Rails?.Reshape(_world, wx, wy, wz, WriteSignal);

        // The wiring hears about it last, once the world holds whatever the edit, the rewire and
        // the reshape made of it. The pass writes through its own door below, so it cannot re-enter.
        Signals?.Update(_world, wx, wy, wz, WriteSignal, SignalSwitched);
    }

    /// <summary>
    /// How the signal pass reaches the world: block, light, mesh and fluid, but never back into
    /// <see cref="EditBlock"/> — the pass settles its own component and re-entering would run it
    /// once per cell it writes.
    /// </summary>
    /// <remarks>
    /// Through <see cref="VoxelWorld.SetBlock"/> deliberately, unlike the fluid: a wire's strength
    /// is keyed on its cell in the edit dictionary, so re-strengthing overwrites one entry rather
    /// than growing the save, and a lever thrown IS a change worth an autosave.
    /// </remarks>
    public void WriteSignal(int x, int y, int z, BlockId id)
    {
        if (!TerrainGenerator.InWorld(y)) return;
        if (!_world.TryGetChunk(ChunkPos.FromWorld(x, y, z), out _)) return;

        _world.SetBlock(x, y, z, id);
        _editQueue.Enqueue((x, y, z));
        _lightWork.Release();
        Fluids?.Touch(x, y, z, urgent: true);
    }

    /// <summary>
    /// A cell the co-op host says changed, applied on a guest.
    /// </summary>
    /// <remarks>
    /// <para>Written like a signal (block, light, mesh, fluid) and NOT like a player's edit: the host
    /// already rewired, reshaped and powered everything around it and sends those cells too, so
    /// redoing that work here would only race the host's own answer.</para>
    /// <para>⚠ A chunk that has not been generated here takes the cell as a saved edit does —
    /// waiting for generation — because a write into it now would be filled in over the top.</para>
    /// </remarks>
    public void ApplyRemote(int wx, int wy, int wz, BlockId id)
    {
        if (!TerrainGenerator.InWorld(wy)) return;

        var pos = ChunkPos.FromWorld(wx, wy, wz);
        if (!_generated.ContainsKey(pos))
        {
            _world.Restore(wx, wy, wz, id);
            CatchLateGeneration(pos, wx, wy, wz);
            return;
        }

        _world.SetBlock(wx, wy, wz, id);
        _editQueue.Enqueue((wx, wy, wz));
        _lightWork.Release();
        Fluids?.Touch(wx, wy, wz, urgent: true);
    }

    /// <summary>Lets the gates think, at whatever cadence the caller owns.</summary>
    /// <returns>How many gates changed state.</returns>
    public int TickSignals() =>
        Signals is { } pass ? pass.Tick(_world, WriteSignal, SignalSwitched) : 0;

    /// <summary>
    /// Advances the flow and books the light and mesh work every cell it moved needs.
    /// </summary>
    /// <remarks>
    /// Called from whoever owns the frame, because the tick rate is a game decision and the budget
    /// is a frame-time one. The cells go through <see cref="TouchBlock"/> rather than
    /// <see cref="EditBlock"/> — the flow has already written them, and going back through the block
    /// path would re-run the neighbour sweep once per cell and log every one of them as a save edit.
    /// </remarks>
    /// <returns>How many cells moved.</returns>
    public int StepFluid(int budget, List<(int X, int Y, int Z)> scratch)
    {
        if (Fluids is not { } fluids || fluids.Pending == 0) return 0;

        scratch.Clear();
        fluids.Step(_world, budget, scratch);

        foreach (var (x, y, z) in scratch) TouchBlock(x, y, z);
        return scratch.Count;
    }

    /// <summary>
    /// Books the light and mesh work for a cell somebody else has already written.
    /// </summary>
    /// <remarks>
    /// The one way into this queue that does not also write the block, for the passes that run over
    /// several cells at once and have their own reason to touch each. Going back through
    /// <see cref="EditBlock"/> for each would re-run the whole neighbour sweep once per cell.
    /// </remarks>
    public void TouchBlock(int wx, int wy, int wz)
    {
        if (!TerrainGenerator.InWorld(wy)) return;
        if (!_world.TryGetChunk(ChunkPos.FromWorld(wx, wy, wz), out _)) return;

        _editQueue.Enqueue((wx, wy, wz));
        _lightWork.Release();
    }

    /// <summary>
    /// Lets anything that joins up with its neighbours re-pick its shape after an edit.
    /// </summary>
    /// <remarks>
    /// Null until somebody hands over a table, so the whole pass costs nothing on a world with no
    /// connecting blocks in it — which is every world the generator makes.
    /// </remarks>
    public ConnectionTable? Connections { get; set; }

    /// <summary>
    /// Re-picks the shape of the cell that was edited and of the six around it.
    /// </summary>
    /// <remarks>
    /// One ring, and no queue. Every member of a family connects the same way, so a variant swap
    /// changes a shape and never changes whether a neighbour would join on to it — which means a
    /// change here can never make a seventh cell want to move. The audit checks that property
    /// directly rather than trusting the argument, because the day a family breaks it this pass
    /// starts silently under-updating rather than failing.
    /// </remarks>
    private void Rewire(int wx, int wy, int wz)
    {
        if (Connections is not { } table) return;

        Fix(wx, wy, wz);
        for (var face = 0; face < Faces.Count; face++)
        {
            var (dx, dy, dz) = Faces.Normals[face];
            Fix(wx + dx, wy + dy, wz + dz);
        }

        void Fix(int x, int y, int z)
        {
            if (!TerrainGenerator.InWorld(y)) return;
            if (!_world.TryGetChunk(ChunkPos.FromWorld(x, y, z), out _)) return;
            if (!table.TryRewire(_world, x, y, z, out var become)) return;

            _world.SetBlock(x, y, z, become);
            _editQueue.Enqueue((x, y, z));
            _lightWork.Release();
            Fluids?.Touch(x, y, z, urgent: true);
        }
    }

    /// <summary>
    /// Brings the load state in line with a new viewer position. Cheap and safe to call every
    /// frame; it does real work only when the viewer crosses into a different chunk.
    /// </summary>
    public void Update(Vector3 viewer)
    {
        var bx = (int)MathF.Floor(viewer.X);
        var by = (int)MathF.Floor(viewer.Y);
        var bz = (int)MathF.Floor(viewer.Z);

        var centre = ChunkPos.FromWorld(
            bx, Math.Clamp(by, TerrainGenerator.WorldBottom, TerrainGenerator.WorldTop - 1), bz);

        // Whether there is a horizon worth loading. Asked of the generator rather than of the world,
        // so it costs three noise stacks and needs nothing to be loaded to answer it.
        var horizon = by > _generator.SurfaceHeight(bx, bz) - SurfaceRingDepth;

        if (centre == _lastCentre && horizon == _surfaceRing && !_visitorsChanged) return;

        _lastCentre = centre;
        _surfaceRing = horizon;
        _visitorsChanged = false;

        QueueGeneration(centre);
        foreach (var ring in _visitors) QueueVisitor(ring);
        DropDistant(centre);
    }

    /// <summary>
    /// Where the other players in a co-op session are, every frame, on the host. An empty list
    /// (the default, and every single-player game) leaves the streamer exactly as it was.
    /// </summary>
    /// <remarks>Cheap: only a guest crossing into another chunk makes <see cref="Update"/> do any work.</remarks>
    public void SetVisitors(IReadOnlyList<Vector3> players)
    {
        var rings = new List<Ring>(players.Count);
        foreach (var at in players)
        {
            var bx = (int)MathF.Floor(at.X);
            var by = (int)MathF.Floor(at.Y);
            var bz = (int)MathF.Floor(at.Z);
            var centre = ChunkPos.FromWorld(
                bx, Math.Clamp(by, TerrainGenerator.WorldBottom, TerrainGenerator.WorldTop - 1), bz);
            var ring = new Ring(centre, by > _generator.SurfaceHeight(bx, bz) - SurfaceRingDepth);
            if (!rings.Contains(ring)) rings.Add(ring);
        }

        if (rings.SequenceEqual(_visitors)) return;
        _visitors.Clear();
        _visitors.AddRange(rings);
        _visitorsChanged = true;
    }

    /// <summary>How many other players' rings the host is keeping.</summary>
    public int VisitorCount => _visitors.Count;

    /// <summary>True when a chunk is generated and its terrain complete — what a held edit waits for.</summary>
    public bool IsGenerated(ChunkPos pos) => _generated.ContainsKey(pos);

    /// <summary>True when a chunk has been asked for as part of a mesh (the host's own view).</summary>
    public bool IsMeshWanted(ChunkPos pos) => _meshRequested.Contains(pos);

    /// <summary>True when a chunk has been lit.</summary>
    public bool IsLit(ChunkPos pos) => _litChunks.ContainsKey(pos);

    private void QueueVisitor(Ring ring)
    {
        var wanted = new List<(ChunkPos Pos, int DistanceSquared)>();
        for (var dz = -VisitorRadius; dz <= VisitorRadius; dz++)
        for (var dx = -VisitorRadius; dx <= VisitorRadius; dx++)
        {
            if (dx * dx + dz * dz > VisitorLoad) continue;
            for (var cy = TerrainGenerator.ChunkBottom; cy < TerrainGenerator.ChunkTop; cy++)
            {
                var pos = new ChunkPos(ring.Centre.X + dx, cy, ring.Centre.Z + dz);
                var d2 = RingDistance(pos, ring.Centre, ring.Surface, VisitorLoad);
                if (d2 < 0 || d2 > VisitorLoad) continue;
                if (!_requested.Add(pos)) continue;
                wanted.Add((pos, d2));
            }
        }

        wanted.Sort(static (a, b) => a.DistanceSquared.CompareTo(b.DistanceSquared));
        foreach (var (pos, _) in wanted)
        {
            _generateQueue.Enqueue(pos);
            _work.Release();
        }
    }

    /// <summary>True when any visitor's ring holds the chunk within <paramref name="limit"/>.</summary>
    private bool VisitorWants(ChunkPos pos, int limit)
    {
        foreach (var ring in _visitors)
        {
            var d2 = RingDistance(pos, ring.Centre, ring.Surface, VisitorLoad);
            if (d2 >= 0 && d2 <= limit) return true;
        }
        return false;
    }

    private bool VisitorKeeps(ChunkPos pos)
    {
        foreach (var ring in _visitors)
            if (WorthKeeping(pos, ring.Centre, ring.Surface, VisitorKeep, VisitorKeep)) return true;
        return false;
    }

    /// <summary>
    /// A cell changed by somebody this machine trusts — a co-op guest, on the host — wherever it is.
    /// </summary>
    /// <remarks>
    /// <para>In a generated chunk it is <see cref="EditBlock"/>, so wiring and rails answer it here.
    /// Anywhere else it is held as a saved edit is (<see cref="VoxelWorld.Hold"/>): in the save at
    /// once, and put in when that chunk is generated — never dropped, which is what
    /// <see cref="EditBlock"/> alone does with an unloaded chunk.</para>
    /// <para>⚠ A chunk can be mid-generation when the check is made: its held edits taken already and
    /// the flag not yet set. Looking again after holding catches it — once the flag is up, nothing
    /// else will ever take that list, so it is put in here.</para>
    /// </remarks>
    public void EditOrHold(int wx, int wy, int wz, BlockId id)
    {
        if (!TerrainGenerator.InWorld(wy)) return;

        var pos = ChunkPos.FromWorld(wx, wy, wz);
        if (_generated.ContainsKey(pos))
        {
            EditBlock(wx, wy, wz, id);
            return;
        }

        _world.Hold(wx, wy, wz, id);
        CatchLateGeneration(pos, wx, wy, wz);
    }

    private void CatchLateGeneration(ChunkPos pos, int wx, int wy, int wz)
    {
        if (!_generated.ContainsKey(pos) || !_world.TryGetChunk(pos, out var chunk)) return;
        if (_world.ApplyPending(chunk) == 0) return;

        chunk.Dirty = true;
        _editQueue.Enqueue((wx, wy, wz));
        _lightWork.Release();
        Fluids?.Touch(wx, wy, wz, urgent: true);
    }

    /// <summary>The terrain sum of a chunk this streamer generated, if it kept one.</summary>
    public bool TryTerrainSum(ChunkPos pos, out ulong sum) => _terrainSums.TryGetValue(pos, out sum);

    /// <summary>Sums made since the last call, in the order chunks finished — what a guest sends.</summary>
    public bool TryTakeNewSum(out (ChunkPos Pos, ulong Sum) made) => _newSums.TryDequeue(out made);

    /// <summary>A copy of a generated chunk's blocks as they stand now, edits and all, or null.</summary>
    public ushort[]? CurrentBlocks(ChunkPos pos) =>
        _generated.ContainsKey(pos) && _world.TryGetChunk(pos, out var chunk) ? (ushort[])chunk.Raw.Clone() : null;

    /// <summary>
    /// Makes a generated chunk hold exactly these blocks: the host's, after this machine's generator
    /// made something else. Returns how many cells differed, or -1 when the chunk is not here.
    /// </summary>
    /// <remarks>
    /// <para>Each cell that differs goes through the edit queue, so light is fixed in the neighbours
    /// too, the way a broken block's is. Written through <see cref="VoxelWorld.SetFluid"/> and NOT
    /// <see cref="VoxelWorld.SetBlock"/>: these are not edits anybody made here, so they must not be
    /// sent back to the host or logged as this player's changes.</para>
    /// </remarks>
    public int ReplaceChunk(ChunkPos pos, ReadOnlySpan<ushort> blocks)
    {
        if (blocks.Length != Chunk.Volume) return -1;
        if (!_generated.ContainsKey(pos) || !_world.TryGetChunk(pos, out var chunk)) return -1;

        var (ox, oy, oz) = pos.Origin;
        var raw = chunk.Raw;
        var edits = _world.Edits;
        var differed = 0;

        for (var i = 0; i < Chunk.Volume; i++)
        {
            if (raw[i] == blocks[i] || blocks[i] >= _registry.Count) continue;

            var x = ox + (i & Chunk.SizeMask);
            var z = oz + ((i >> Chunk.SizeLog2) & Chunk.SizeMask);
            var y = oy + (i >> (Chunk.SizeLog2 * 2));

            // ⛔ Terrain only. An edited cell is the cell messages' business, and they are already in
            // step: the host may have taken these blocks just BEFORE it applied this guest's newest
            // cell, and writing them would undo that cell here while the host keeps it.
            if (edits.ContainsKey((x, y, z))) continue;

            // Through the world's no-log write (the fluid's door), which dirties the seam neighbours too.
            _world.SetFluid(x, y, z, new BlockId(blocks[i]));
            _editQueue.Enqueue((x, y, z));
            Fluids?.Touch(x, y, z, urgent: false);
            differed++;
        }

        if (differed > 0) _lightWork.Release(differed);
        return differed;
    }

    /// <summary>FNV-1a over the block numbers: the same on every machine for the same blocks.</summary>
    public static ulong SumOf(ReadOnlySpan<ushort> blocks)
    {
        var hash = 14695981039346656037UL;
        foreach (var block in blocks)
        {
            hash = (hash ^ (byte)block) * 1099511628211UL;
            hash = (hash ^ (byte)(block >> 8)) * 1099511628211UL;
        }
        return hash;
    }

    private static int Square(float radius) => (int)MathF.Ceiling(radius * radius);

    /// <summary>
    /// Squared horizontal distance at which a chunk counts, or -1 when it is not wanted at all.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>Two rings, not one, and that is the whole of vertical streaming.</b> There are
    /// exactly two things a player can see: the terrain surface, out to the full ring — which across
    /// a 384-block horizon spans about four chunk layers and no more — and the room they are standing
    /// in, which underground is a handful of blocks in every direction. Everything else in a
    /// 384-tall world is rock nobody will ever look at.</para>
    /// <para>The vertical extent of the surface ring comes from the generator's own height field, so
    /// a column knows which layers its terrain occupies without a single chunk existing. Deep enough
    /// underground the surface ring is dropped altogether and only the ball is left, which is why
    /// standing in the Emberdeep costs about a third of standing in a field.</para>
    /// </remarks>
    private int RingDistance(ChunkPos pos, ChunkPos centre) => RingDistance(pos, centre, _surfaceRing, _deepLimit);

    private int RingDistance(ChunkPos pos, ChunkPos centre, bool surfaceRing, int deepLimit)
    {
        var dx = pos.X - centre.X;
        var dz = pos.Z - centre.Z;
        var d2 = dx * dx + dz * dz;

        if (d2 <= deepLimit && Math.Abs(pos.Y - centre.Y) <= VerticalBand) return d2;
        if (!surfaceRing) return -1;

        var (low, high) = SurfaceBand(pos.X, pos.Z);
        return pos.Y >= low && pos.Y <= high ? d2 : -1;
    }

    /// <summary>The same question with a margin on it, so walking a seam does not thrash.</summary>
    private bool WorthKeeping(ChunkPos pos, ChunkPos centre) =>
        WorthKeeping(pos, centre, _surfaceRing, _dropLimit, _deepLimit);

    private bool WorthKeeping(ChunkPos pos, ChunkPos centre, bool surfaceRing, int dropLimit, int deepLimit)
    {
        var dx = pos.X - centre.X;
        var dz = pos.Z - centre.Z;
        var d2 = dx * dx + dz * dz;
        if (d2 > dropLimit) return false;

        if (d2 <= deepLimit && Math.Abs(pos.Y - centre.Y) <= VerticalDropBand) return true;
        if (!surfaceRing) return false;

        var (low, high) = SurfaceBand(pos.X, pos.Z);
        return pos.Y >= low - 1 && pos.Y <= high + 1;
    }

    private (int Low, int High) SurfaceBand(int cx, int cz)
    {
        if (_surfaceBand.TryGetValue((cx, cz), out var band)) return band;

        var ox = cx * Chunk.Size;
        var oz = cz * Chunk.Size;
        var min = int.MaxValue;
        var max = int.MinValue;

        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
        {
            var h = _generator.SurfaceHeight(
                ox + Math.Min(i * (Chunk.Size / 2), Chunk.SizeMask),
                oz + Math.Min(j * (Chunk.Size / 2), Chunk.SizeMask));

            if (h < min) min = h;
            if (h > max) max = h;
        }

        band = ((min >> Chunk.SizeLog2) - 1, (max >> Chunk.SizeLog2) + 1);
        _surfaceBand[(cx, cz)] = band;
        return band;
    }

    private void QueueGeneration(ChunkPos centre)
    {
        // Nearest first, so the ground under the viewer appears before the horizon fills in.
        var wanted = new List<(ChunkPos Pos, int DistanceSquared)>();
        var reach = (int)MathF.Ceiling(MathF.Sqrt(_loadLimit));

        for (var dz = -reach; dz <= reach; dz++)
        for (var dx = -reach; dx <= reach; dx++)
        {
            if (dx * dx + dz * dz > _loadLimit) continue;

            for (var cy = TerrainGenerator.ChunkBottom; cy < TerrainGenerator.ChunkTop; cy++)
            {
                var pos = new ChunkPos(centre.X + dx, cy, centre.Z + dz);

                var d2 = RingDistance(pos, centre);
                if (d2 < 0 || d2 > _loadLimit) continue;
                if (!_requested.Add(pos)) continue;

                wanted.Add((pos, d2));
            }
        }

        wanted.Sort(static (a, b) => a.DistanceSquared.CompareTo(b.DistanceSquared));

        foreach (var (pos, _) in wanted)
        {
            _generateQueue.Enqueue(pos);
            _work.Release();
        }
    }

    private void DropDistant(ChunkPos centre)
    {
        List<ChunkPos>? stale = null;

        List<ChunkPos>? unseen = null;

        foreach (var pos in _requested)
        {
            if (WorthKeeping(pos, centre)) continue;
            if (_visitors.Count > 0 && VisitorKeeps(pos))
            {
                // Kept for a guest, but out of the host's own sight: its mesh goes, its blocks stay.
                if (_meshRequested.Contains(pos)) (unseen ??= []).Add(pos);
                continue;
            }
            (stale ??= []).Add(pos);
        }

        if (unseen is not null)
        {
            foreach (var pos in unseen)
            {
                _meshRequested.Remove(pos);
                _dropped.Enqueue(pos);
            }
        }

        if (stale is not null)
        {
            foreach (var pos in stale)
            {
                _requested.Remove(pos);
                _meshRequested.Remove(pos);
                _generated.TryRemove(pos, out _);
                Fluids?.ForgetChunk(pos);
                _world.RemoveChunk(pos);
                _dropped.Enqueue(pos);

                // Forgetting a chunk's light along with its blocks matters: coming back, it is
                // regenerated from scratch and has to be re-lit from scratch. Leaving the flag set
                // would leave it dark for good.
                _lightRequested.Remove(pos);
                _litChunks.TryRemove(pos, out _);
            }
        }

        // The height cache grows with every column ever considered, and a long walk considers a lot
        // of them. Pruned against the drop ring rather than the load ring so a step back and forth
        // does not re-sample the same nine heights.
        if (_surfaceBand.Count <= 4096) return;

        var far = _dropLimit + _dropLimit;
        List<(int X, int Z)>? cold = null;

        foreach (var (cx, cz) in _surfaceBand.Keys)
        {
            var dx = cx - centre.X;
            var dz = cz - centre.Z;
            if (dx * dx + dz * dz <= far) continue;
            (cold ??= []).Add((cx, cz));
        }

        if (cold is null) return;
        foreach (var key in cold) _surfaceBand.Remove(key);
    }

    private async Task WorkerLoop()
    {
        var mesher = new ChunkMesher(_registry, _tinter);
        var token = _cancel.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await _work.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Generation before meshing: a mesh job whose neighbours are still pending would
            // only have to be thrown away and redone.
            if (_generateQueue.TryDequeue(out var genPos))
            {
                Interlocked.Increment(ref _generatingCount);
                try
                {
                    var chunk = _world.GetOrCreateChunk(genPos);
                    _generator.GenerateChunk(chunk);
                    _generator.DecorateChunk(chunk);
                    TerrainFault?.Invoke(chunk);

                    if (KeepTerrainSums)
                    {
                        var sum = SumOf(chunk.Raw);
                        _terrainSums[genPos] = sum;
                        if (ReportTerrainSums) _newSums.Enqueue((genPos, sum));
                    }

                    // ⛳ Whatever a loaded save put here, on top of the terrain and before the chunk
                    // is declared generated. Everything downstream — the first light flood, the
                    // mesher, a raycast — waits on that flag, so putting the edits in ahead of it
                    // means the world is never seen in a state somebody had already changed.
                    var restored = _world.ApplyPending(chunk);
                    if (restored > 0) Interlocked.Add(ref _restoredEdits, restored);

                    _generated[genPos] = true;
                    _fluidReady.Enqueue(genPos);
                }
                finally
                {
                    Interlocked.Decrement(ref _generatingCount);
                }
                continue;
            }

            if (_meshQueue.TryDequeue(out var meshPos))
            {
                Interlocked.Increment(ref _meshingCount);
                try
                {
                    // Cleared before the snapshot, never after: a light change landing between the
                    // two would otherwise be swallowed by the clear and the chunk would keep a mesh
                    // lit by values nothing in the world holds any more.
                    if (_world.TryGetChunk(meshPos, out var chunk)) chunk.Dirty = false;

                    var data = mesher.Build(_world, meshPos);
                    if (data is not null) _finishedMeshes.Enqueue(data);
                }
                finally
                {
                    _meshInFlight.TryRemove(meshPos, out _);
                    Interlocked.Decrement(ref _meshingCount);
                }
            }
        }
    }

    /// <summary>
    /// The single lighting thread. Floods one column at a time, in the order they were promoted.
    /// </summary>
    private async Task LightLoop()
    {
        var lighter = new LightEngine(_registry, _generator);
        var token = _cancel.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await _lightWork.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Player edits jump the queue. A column arriving in the background can wait a few
            // milliseconds; a block the player just swung at cannot, and the whole point of the
            // incremental relight is that it is fast enough to feel immediate.
            //
            // ⛔ BUT ONLY SO FAR, AND THAT IS NEW. Absolute priority was right when an edit meant a
            // player swinging a pickaxe, which is a few a second. A settling river generates them
            // continuously and by the thousand, so a chunk waiting to be lit would wait until the
            // river finished — which is to say the world would stop loading while the water ran.
            // Sixty-four edits, then whatever is next in line.
            var burst = 0;
            while (burst < EditBurst && _editQueue.TryDequeue(out var edit))
            {
                burst++;
                Interlocked.Increment(ref _lightingCount);
                try
                {
                    lighter.UpdateBlock(_world, edit.X, edit.Y, edit.Z);
                }
                finally
                {
                    Interlocked.Decrement(ref _lightingCount);
                }
            }

            if (burst > 0 && _lightQueue.IsEmpty) continue;

            if (!_lightQueue.TryDequeue(out var chunk)) continue;

            Interlocked.Increment(ref _lightingCount);
            try
            {
                lighter.LightChunk(_world, chunk);
                _litChunks[chunk] = true;
            }
            finally
            {
                Interlocked.Decrement(ref _lightingCount);
            }
        }
    }

    /// <summary>
    /// Moves work down the pipeline: generated columns into lighting, lit chunks into meshing, and
    /// chunks whose light later changed back into meshing. Called from the main thread so the
    /// "already asked for" bookkeeping needs no lock.
    /// </summary>
    public void PromoteReadyChunks()
    {
        PromoteToLighting();
        PromoteToMeshing();
        PromoteToFluid();
    }

    /// <summary>
    /// Shows the flow every chunk that has arrived since the last frame.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>This is what makes a river resume at a boundary.</b> A fall that stopped because the
    /// chunk under it was not loaded has no reason to start again on its own — nothing in that chunk
    /// changed, because until now it did not exist. Handing the chunk over re-offers the fluid
    /// standing in the shell above it, and the fall carries on from where it stopped.
    /// </remarks>
    private void PromoteToFluid()
    {
        if (Fluids is not { } fluids)
        {
            // Nobody is running a flow, so the queue would grow without bound.
            while (_fluidReady.TryDequeue(out _)) { }
            return;
        }

        while (_fluidReady.TryDequeue(out var pos)) fluids.TouchChunk(_world, pos);
    }

    private void PromoteToLighting()
    {
        List<ChunkPos>? ready = null;

        foreach (var pos in _requested)
        {
            if (_lightRequested.Contains(pos)) continue;

            var d2 = RingDistance(pos, _lastCentre);
            if ((d2 < 0 || d2 > _lightLimit) && (_visitors.Count == 0 || !VisitorWants(pos, VisitorLight))) continue;

            if (!NeighbourhoodGenerated(pos)) continue;

            (ready ??= []).Add(pos);
        }

        if (ready is null) return;

        // ⛳ Top down. A chunk's ceiling is answered by the chunk above it when that one is already
        // lit and by the generator when it is not — both are correct, since light arriving later
        // only ever brightens and the flood carries it down — but the first needs no second pass.
        ready.Sort(static (a, b) => b.Y.CompareTo(a.Y));

        foreach (var pos in ready)
        {
            _lightRequested.Add(pos);
            _lightQueue.Enqueue(pos);
            _lightWork.Release();
        }
    }

    private void PromoteToMeshing()
    {
        foreach (var pos in _requested)
        {
            var d2 = RingDistance(pos, _lastCentre);
            if (d2 < 0 || d2 > _meshLimit) continue;

            if (!NeighboursLit(pos)) continue;

            if (_meshRequested.Contains(pos))
            {
                // Already meshed once. Light arriving from a column that loaded later changes the
                // vertices, so the chunk has to be built again — the same path a block edit will
                // take at P3.
                if (!_world.TryGetChunk(pos, out var chunk) || !chunk.Dirty) continue;
            }

            // Queued or being built already. Checked before the request is recorded so that a
            // chunk waiting its turn is never queued a second time.
            if (!_meshInFlight.TryAdd(pos, true)) continue;

            _meshRequested.Add(pos);
            _meshQueue.Enqueue(pos);
            _work.Release();
        }
    }

    /// <summary>True when a chunk and its twenty-six neighbours have all finished generating.</summary>
    /// <remarks>
    /// Lighting reads a one-block shell around the chunk it is lighting, so the neighbours have to
    /// be there — not lit, just present. Requiring them lit as well would deadlock: each would be
    /// waiting on the others.
    /// </remarks>
    private bool NeighbourhoodGenerated(ChunkPos pos) => Neighbourhood(pos, _generated);

    /// <summary>
    /// True when every cell the mesher will sample is settled — generated and lit.
    /// </summary>
    private bool NeighboursLit(ChunkPos pos) => Neighbourhood(pos, _litChunks);

    /// <remarks>
    /// ⛔ <b>A neighbour that was never asked for counts as ready, and without that rule vertical
    /// streaming deadlocks.</b> Every chunk on the top or bottom face of the loaded band has a
    /// neighbour beyond it that nothing will ever request; waiting for it means that chunk is never
    /// lit, never meshed, and shows up as a missing slab of world. Positions outside the world count
    /// the same way and always did — they are permanently empty.
    /// <para>Safe because <see cref="QueueGeneration"/> fills <see cref="_requested"/> for the whole
    /// wanted set before anything is promoted, so "not requested" means "outside the set" rather than
    /// "not asked for yet". A neighbour that becomes wanted later is put right when it lights: light it
    /// adds the flood carries in, and sun the generator over-guessed under its trees
    /// <c>LightEngine.TakeBackGuessedSun</c> takes back.</para>
    /// </remarks>
    private bool Neighbourhood<T>(ChunkPos pos, System.Collections.Concurrent.ConcurrentDictionary<ChunkPos, T> done)
    {
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var n = pos.Offset(dx, dy, dz);
            if (n.Y < TerrainGenerator.ChunkBottom || n.Y >= TerrainGenerator.ChunkTop) continue;
            if (done.ContainsKey(n)) continue;
            if (!_requested.Contains(n)) continue;

            return false;
        }

        return true;
    }

    /// <summary>
    /// Takes the next finished mesh, skipping any whose chunk was forgotten while it was being
    /// built. Uploading one of those would put a chunk back on screen that streaming had already
    /// decided was out of range, with nothing left to drop it again.
    /// </summary>
    public bool TryDequeueMesh(out ChunkMeshData data)
    {
        while (_finishedMeshes.TryDequeue(out data!))
        {
            if (_meshRequested.Contains(data.Position)) return true;
        }

        return false;
    }

    /// <summary>
    /// A chunk whose mesh is no longer wanted: forgotten, or (on a co-op host) kept only for a guest.
    /// </summary>
    public bool TryDequeueDropped(out ChunkPos pos) => _dropped.TryDequeue(out pos);

    public void Dispose()
    {
        _cancel.Cancel();
        try
        {
            Task.WaitAll([.. _workers, _lightWorker], TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Workers unwinding through cancellation; nothing to salvage.
        }
        _cancel.Dispose();
        _work.Dispose();
        _lightWork.Dispose();
    }
}
