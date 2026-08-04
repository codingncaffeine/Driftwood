using System.Collections.Concurrent;
using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Gen;
using Driftwood.Core.Meshing;

namespace Driftwood.Core.World;

/// <summary>
/// Keeps the world loaded around a moving point: generates chunks that come into range, meshes
/// them once their neighbours exist, and drops the ones left behind.
/// </summary>
/// <remarks>
/// <para>Two radii, not one. A chunk cannot be meshed until every neighbour it samples has been
/// generated — the mesher reads a one-block skirt in all directions to cull faces and compute
/// ambient occlusion — so generation runs one ring wider than meshing. Without that margin the
/// outermost chunks mesh against absent neighbours, read them as air, and grow a wall of faces
/// that vanishes the moment the neighbour loads.</para>
/// <para>Work is queued rather than done inline. Generation and meshing both run on background
/// workers; the main thread only ever enqueues, drains finished meshes, and decides what to
/// forget. Nothing here touches the graphics API, so the whole pipeline is exercisable headlessly.</para>
/// </remarks>
public sealed class WorldStreamer : IDisposable
{
    private readonly TerrainGenerator _generator;
    private readonly BlockRegistry _registry;
    private readonly int _chunksTall;
    private readonly int _meshRadius;
    private readonly int _loadRadius;
    private readonly int _dropRadius;

    private readonly VoxelWorld _world;

    /// <summary>Positions whose terrain is complete and safe for a neighbour to sample.</summary>
    private readonly ConcurrentDictionary<ChunkPos, bool> _generated = new();

    private readonly ConcurrentQueue<ChunkPos> _generateQueue = new();
    private readonly ConcurrentQueue<ChunkPos> _meshQueue = new();
    private readonly ConcurrentQueue<ChunkMeshData> _finishedMeshes = new();
    private readonly ConcurrentQueue<ChunkPos> _dropped = new();

    // Main-thread bookkeeping: what has already been asked for, so nothing is queued twice.
    private readonly HashSet<ChunkPos> _requested = [];
    private readonly HashSet<ChunkPos> _meshRequested = [];

    private readonly Task[] _workers;
    private readonly CancellationTokenSource _cancel = new();
    private readonly SemaphoreSlim _work = new(0);

    private ChunkPos _lastCentre = new(int.MinValue, 0, int.MinValue);
    private int _generatingCount;
    private int _meshingCount;

    public VoxelWorld World => _world;

    public int WorkerCount => _workers.Length;
    public int LoadedChunks => _world.ChunkCount;
    public int PendingGenerate => _generateQueue.Count + Volatile.Read(ref _generatingCount);
    public int PendingMesh => _meshQueue.Count + Volatile.Read(ref _meshingCount);
    public int ReadyMeshes => _finishedMeshes.Count;

    public WorldStreamer(
        BlockRegistry registry,
        TerrainGenerator generator,
        int meshRadius,
        int workerCount = 0)
    {
        _registry = registry;
        _generator = generator;
        _meshRadius = Math.Max(1, meshRadius);

        // Generation leads meshing by a ring; chunks are only forgotten a ring beyond that, so
        // walking back and forth across a boundary does not thrash the same chunk in and out.
        _loadRadius = _meshRadius + 1;
        _dropRadius = _meshRadius + 3;

        _chunksTall = TerrainGenerator.WorldHeight / Chunk.Size;
        _world = new VoxelWorld(registry);

        var count = workerCount > 0 ? workerCount : Math.Max(1, Environment.ProcessorCount - 2);
        _workers = new Task[count];
        for (var i = 0; i < count; i++)
            _workers[i] = Task.Factory.StartNew(WorkerLoop, TaskCreationOptions.LongRunning).Unwrap();
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

    private void QueueGeneration(ChunkPos centre)
    {
        // Nearest first, so the ground under the viewer appears before the horizon fills in.
        var wanted = new List<(ChunkPos Pos, int DistanceSquared)>();

        for (var dz = -_loadRadius; dz <= _loadRadius; dz++)
        for (var dx = -_loadRadius; dx <= _loadRadius; dx++)
        {
            var d2 = dx * dx + dz * dz;
            if (d2 > _loadRadius * _loadRadius) continue;

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
        var limit = _dropRadius * _dropRadius;
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
        }
    }

    private async Task WorkerLoop()
    {
        var mesher = new ChunkMesher(_registry);
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
                    var data = mesher.Build(_world, meshPos);
                    if (data is not null) _finishedMeshes.Enqueue(data);
                }
                finally
                {
                    Interlocked.Decrement(ref _meshingCount);
                }
            }
        }
    }

    /// <summary>
    /// Promotes chunks whose neighbours have all finished generating into the mesh queue. Called
    /// from the main thread so the "already asked for" bookkeeping needs no lock.
    /// </summary>
    public void PromoteReadyChunks()
    {
        var limit = _meshRadius * _meshRadius;

        foreach (var pos in _requested)
        {
            if (_meshRequested.Contains(pos)) continue;
            if (!_generated.ContainsKey(pos)) continue;

            var dx = pos.X - _lastCentre.X;
            var dz = pos.Z - _lastCentre.Z;
            if (dx * dx + dz * dz > limit) continue;

            if (!NeighboursReady(pos)) continue;

            _meshRequested.Add(pos);
            _meshQueue.Enqueue(pos);
            _work.Release();
        }
    }

    /// <summary>
    /// True when every cell the mesher will sample is settled. Positions above or below the world
    /// count as ready: they are permanently empty, so waiting on them would stall the top and
    /// bottom slabs forever.
    /// </summary>
    private bool NeighboursReady(ChunkPos pos)
    {
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        for (var dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;

            var n = pos.Offset(dx, dy, dz);
            if (n.Y < 0 || n.Y >= _chunksTall) continue;
            if (!_generated.ContainsKey(n)) return false;
        }

        return true;
    }

    public bool TryDequeueMesh(out ChunkMeshData data) => _finishedMeshes.TryDequeue(out data!);

    public bool TryDequeueDropped(out ChunkPos pos) => _dropped.TryDequeue(out pos);

    public void Dispose()
    {
        _cancel.Cancel();
        try
        {
            Task.WaitAll(_workers, TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Workers unwinding through cancellation; nothing to salvage.
        }
        _cancel.Dispose();
        _work.Dispose();
    }
}
