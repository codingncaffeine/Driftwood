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

        if (centre == _lastCentre && horizon == _surfaceRing) return;

        _lastCentre = centre;
        _surfaceRing = horizon;

        QueueGeneration(centre);
        DropDistant(centre);
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
    private int RingDistance(ChunkPos pos, ChunkPos centre)
    {
        var dx = pos.X - centre.X;
        var dz = pos.Z - centre.Z;
        var d2 = dx * dx + dz * dz;

        if (d2 <= _deepLimit && Math.Abs(pos.Y - centre.Y) <= VerticalBand) return d2;
        if (!_surfaceRing) return -1;

        var (low, high) = SurfaceBand(pos.X, pos.Z);
        return pos.Y >= low && pos.Y <= high ? d2 : -1;
    }

    /// <summary>The same question with a margin on it, so walking a seam does not thrash.</summary>
    private bool WorthKeeping(ChunkPos pos, ChunkPos centre)
    {
        var dx = pos.X - centre.X;
        var dz = pos.Z - centre.Z;
        var d2 = dx * dx + dz * dz;
        if (d2 > _dropLimit) return false;

        if (d2 <= _deepLimit && Math.Abs(pos.Y - centre.Y) <= VerticalDropBand) return true;
        if (!_surfaceRing) return false;

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

        foreach (var pos in _requested)
        {
            if (WorthKeeping(pos, centre)) continue;
            (stale ??= []).Add(pos);
        }

        if (stale is not null)
        {
            foreach (var pos in stale)
            {
                _requested.Remove(pos);
                _meshRequested.Remove(pos);
                _generated.TryRemove(pos, out _);
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
            if (d2 < 0 || d2 > _lightLimit) continue;

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
    /// "not asked for yet". A neighbour that becomes wanted later can only make this chunk brighter,
    /// which the flood carries in on its own.</para>
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
            if (_requested.Contains(data.Position)) return true;
        }

        return false;
    }

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
