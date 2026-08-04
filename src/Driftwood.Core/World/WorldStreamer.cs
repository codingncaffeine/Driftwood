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
    private readonly int _chunksTall;
    private readonly int _meshRadius;

    // Squared chunk distances, not radii. Each ring has to clear the one inside it by more than a
    // diagonal step, because "all eight neighbours of a chunk at radius r" reaches r + sqrt(2) —
    // a plain +1 leaves the corner neighbours of the outermost meshable chunks outside the
    // lighting ring, and those chunks then wait for light that is never coming.
    private readonly int _meshLimit;
    private readonly int _lightLimit;
    private readonly int _loadLimit;
    private readonly int _dropLimit;

    private readonly VoxelWorld _world;

    /// <summary>Positions whose terrain is complete and safe for a neighbour to sample.</summary>
    private readonly ConcurrentDictionary<ChunkPos, bool> _generated = new();

    /// <summary>Columns whose light has been computed, keyed by chunk (x, z).</summary>
    private readonly ConcurrentDictionary<(int X, int Z), bool> _litColumns = new();

    private readonly ConcurrentQueue<ChunkPos> _generateQueue = new();
    private readonly ConcurrentQueue<(int X, int Z)> _lightQueue = new();
    private readonly ConcurrentQueue<(int X, int Y, int Z)> _editQueue = new();
    private readonly ConcurrentQueue<ChunkPos> _meshQueue = new();
    private readonly ConcurrentQueue<ChunkMeshData> _finishedMeshes = new();
    private readonly ConcurrentQueue<ChunkPos> _dropped = new();

    // Main-thread bookkeeping: what has already been asked for, so nothing is queued twice.
    private readonly HashSet<ChunkPos> _requested = [];
    private readonly HashSet<(int X, int Z)> _lightRequested = [];
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

    private ChunkPos _lastCentre = new(int.MinValue, 0, int.MinValue);
    private int _generatingCount;
    private int _lightingCount;
    private int _meshingCount;

    public VoxelWorld World => _world;

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

        _chunksTall = TerrainGenerator.WorldHeight / Chunk.Size;
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
        if (wy < 0 || wy >= TerrainGenerator.WorldHeight) return;

        var pos = ChunkPos.FromWorld(wx, wy, wz);
        if (!_world.TryGetChunk(pos, out _)) return;

        _world.SetBlock(wx, wy, wz, id);
        _editQueue.Enqueue((wx, wy, wz));
        _lightWork.Release();
    }

    /// <summary>
    /// Brings the load state in line with a new viewer position. Cheap and safe to call every
    /// frame; it does real work only when the viewer crosses into a different chunk.
    /// </summary>
    public void Update(Vector3 viewer)
    {
        var centre = ChunkPos.FromWorld(
            (int)MathF.Floor(viewer.X), 0, (int)MathF.Floor(viewer.Z));

        if (centre.X == _lastCentre.X && centre.Z == _lastCentre.Z) return;
        _lastCentre = centre;

        QueueGeneration(centre);
        DropDistant(centre);
    }

    private static int Square(float radius) => (int)MathF.Ceiling(radius * radius);

    private void QueueGeneration(ChunkPos centre)
    {
        // Nearest first, so the ground under the viewer appears before the horizon fills in.
        var wanted = new List<(ChunkPos Pos, int DistanceSquared)>();
        var reach = (int)MathF.Ceiling(MathF.Sqrt(_loadLimit));

        for (var dz = -reach; dz <= reach; dz++)
        for (var dx = -reach; dx <= reach; dx++)
        {
            var d2 = dx * dx + dz * dz;
            if (d2 > _loadLimit) continue;

            for (var cy = 0; cy < _chunksTall; cy++)
            {
                var pos = new ChunkPos(centre.X + dx, cy, centre.Z + dz);
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
        var limit = _dropLimit;
        List<ChunkPos>? stale = null;

        foreach (var pos in _requested)
        {
            var dx = pos.X - centre.X;
            var dz = pos.Z - centre.Z;
            if (dx * dx + dz * dz <= limit) continue;
            (stale ??= []).Add(pos);
        }

        if (stale is null) return;

        foreach (var pos in stale)
        {
            _requested.Remove(pos);
            _meshRequested.Remove(pos);
            _generated.TryRemove(pos, out _);
            _world.RemoveChunk(pos);
            _dropped.Enqueue(pos);

            // Forgetting a column's light along with its blocks matters: coming back, the column
            // is regenerated from scratch and has to be re-lit from scratch. Leaving the flag set
            // would leave the chunk dark for good.
            _lightRequested.Remove((pos.X, pos.Z));
            _litColumns.TryRemove((pos.X, pos.Z), out _);
        }
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
                    _generated[genPos] = true;
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
        var lighter = new LightEngine(_registry);
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
            if (_editQueue.TryDequeue(out var edit))
            {
                Interlocked.Increment(ref _lightingCount);
                try
                {
                    lighter.UpdateBlock(_world, edit.X, edit.Y, edit.Z);
                }
                finally
                {
                    Interlocked.Decrement(ref _lightingCount);
                }
                continue;
            }

            if (!_lightQueue.TryDequeue(out var column)) continue;

            Interlocked.Increment(ref _lightingCount);
            try
            {
                lighter.LightColumn(_world, column.X, column.Z);
                _litColumns[column] = true;
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
    }

    private void PromoteToLighting()
    {
        var limit = _lightLimit;

        foreach (var pos in _requested)
        {
            if (pos.Y != 0) continue;   // one entry per column, not per chunk

            var column = (pos.X, pos.Z);
            if (_lightRequested.Contains(column)) continue;

            var dx = pos.X - _lastCentre.X;
            var dz = pos.Z - _lastCentre.Z;
            if (dx * dx + dz * dz > limit) continue;

            if (!ColumnNeighbourhoodGenerated(pos.X, pos.Z)) continue;

            _lightRequested.Add(column);
            _lightQueue.Enqueue(column);
            _lightWork.Release();
        }
    }

    private void PromoteToMeshing()
    {
        var limit = _meshLimit;

        foreach (var pos in _requested)
        {
            var dx = pos.X - _lastCentre.X;
            var dz = pos.Z - _lastCentre.Z;
            if (dx * dx + dz * dz > limit) continue;

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

    /// <summary>True when a column and its eight neighbours have all finished generating.</summary>
    /// <remarks>
    /// Lighting reads a one-block shell around the column it is lighting, so the neighbours have to
    /// be there — not lit, just present. Requiring them lit as well would deadlock: each column
    /// would be waiting on the others.
    /// </remarks>
    private bool ColumnNeighbourhoodGenerated(int cx, int cz)
    {
        for (var dz = -1; dz <= 1; dz++)
        for (var dx = -1; dx <= 1; dx++)
        for (var cy = 0; cy < _chunksTall; cy++)
        {
            if (!_generated.ContainsKey(new ChunkPos(cx + dx, cy, cz + dz))) return false;
        }

        return true;
    }

    /// <summary>
    /// True when every cell the mesher will sample is settled — generated and lit. Positions above
    /// or below the world count as ready: they are permanently empty, so waiting on them would
    /// stall the top and bottom slabs forever.
    /// </summary>
    private bool NeighboursLit(ChunkPos pos)
    {
        for (var dz = -1; dz <= 1; dz++)
        for (var dx = -1; dx <= 1; dx++)
        {
            if (!_litColumns.ContainsKey((pos.X + dx, pos.Z + dz))) return false;
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
