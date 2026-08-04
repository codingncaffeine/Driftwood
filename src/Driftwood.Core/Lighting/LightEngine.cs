using System.Diagnostics;
using Driftwood.Core.Blocks;
using Driftwood.Core.Gen;
using Driftwood.Core.World;

namespace Driftwood.Core.Lighting;

/// <summary>
/// Fills the world's light arrays: sunlight down from the sky, coloured light out from emitters,
/// both carried onward by a breadth-first flood — and takes it away again when a block changes.
/// </summary>
/// <remarks>
/// <para>Two seeds, one flood. Sunlight is seeded by walking each column down from the top of the
/// world, which is exact and costs one pass; the flood then only has to carry it sideways into
/// whatever the column pass left dark — under overhangs, into cave mouths, through doorways. Block
/// light has no column structure at all and is seeded straight from its emitters.</para>
/// <para>All four channels — sun, red, green, blue — run through the same code with a channel
/// index. They obey one rule each way and only sunlight has an exception to it, so writing them
/// out four times would be four places for that exception to be got wrong.</para>
/// <para>Everything here works in world coordinates through <see cref="VoxelWorld"/> rather than
/// inside a single chunk, because light does not respect chunk seams and pretending otherwise is
/// how you get a bright chunk against a dark one along a line the player can see. Chunks whose
/// light changed are marked dirty so the mesher rebuilds them.</para>
/// <para>Order independence is the property that matters, and it is not obvious: a flood that takes
/// cells in a different order still lands on the same values, because every cell keeps the
/// brightest thing ever offered it and only passes light on when it improves. That is what lets
/// columns be lit as they stream in, in whatever order the player walks. The audit checks it
/// against a whole-world pass rather than trusting the argument.</para>
/// </remarks>
public sealed class LightEngine
{
    public const int ChannelSky = 0;
    public const int ChannelRed = 1;
    public const int ChannelGreen = 2;
    public const int ChannelBlue = 3;
    public const int ChannelCount = 4;

    private readonly byte[] _attenuation;
    private readonly ushort[] _emission;

    private readonly Queue<Node>[] _additions;
    private readonly Queue<Removal> _removals = new();

    /// <summary>
    /// The chunk the last cell was in. A flood walks neighbours, and thirty-one cells in
    /// thirty-two share their neighbour's chunk, so remembering one turns almost every read into
    /// an array index instead of a dictionary lookup. Worth doing here specifically: an
    /// interactive relight is a few thousand cells and the lookups were most of its cost.
    /// </summary>
    private ChunkPos _cachedPos = new(int.MinValue, int.MinValue, int.MinValue);
    private Chunk? _cachedChunk;

    private readonly record struct Node(int X, int Y, int Z);
    private readonly record struct Removal(int X, int Y, int Z, int Level);

    public LightEngine(BlockRegistry registry)
    {
        _attenuation = registry.BuildLightAttenuationTable();
        _emission = registry.BuildLightEmissionTable();

        _additions = new Queue<Node>[ChannelCount];
        for (var c = 0; c < ChannelCount; c++) _additions[c] = new Queue<Node>();
    }

    /// <summary>Cells taken off a queue by the last flood, for reporting propagation cost.</summary>
    public long LastCellsVisited { get; private set; }

    /// <summary>
    /// The same work split by where it went, because "slow" and "large" are different problems and
    /// a single total cannot tell them apart.
    /// </summary>
    /// <remarks>
    /// Added while chasing an edit that took 2.5 ms to visit 458 cells — a hundred times the
    /// per-cell cost of every other edit in the same run. Two plausible explanations were wrong
    /// before these existed: collections provoked by the verification pass, and measurement noise.
    /// Both were ruled out by experiment, and neither would have been ruled out by argument.
    /// </remarks>
    public long LastRemovalCells { get; private set; }

    public long LastFillCells { get; private set; }

    /// <summary>Neighbour cells examined, which is the real inner loop.</summary>
    public long LastNeighbourTests { get; private set; }

    /// <summary>Times the one-chunk cache missed and the world dictionary had to be asked.</summary>
    public long LastChunkMisses { get; private set; }

    /// <summary>Where the last edit's time actually went, split at the phase boundary.</summary>
    /// <remarks>
    /// Four timestamp reads per edit, which is nothing against the work being measured. Counting
    /// cells was not enough: one edit was thirty times slower than another that visited three times
    /// as many, so the cost was not in any loop the counters watch and only a clock could say where
    /// it was instead.
    /// </remarks>
    public double LastRemovalMs { get; private set; }

    public double LastFillMs { get; private set; }

    /// <summary>How much of the removal phase was inside <see cref="Unfill"/>, and how many passes.</summary>
    public double LastUnfillMs => _unfillTicks * 1000.0 / Stopwatch.Frequency;

    public int LastUnfillPasses { get; private set; }

    /// <summary>Which single channel's teardown was the most expensive, and what it cost.</summary>
    public double LastSlowestUnfillMs { get; private set; }

    public int LastSlowestUnfillChannel { get; private set; } = -1;

    public long LastSlowestUnfillCells { get; private set; }

    private long _unfillTicks;

    private void ResetCounters()
    {
        LastCellsVisited = 0;
        LastRemovalCells = 0;
        LastFillCells = 0;
        LastNeighbourTests = 0;
        LastChunkMisses = 0;
        _unfillTicks = 0;
        LastUnfillPasses = 0;
        LastSlowestUnfillMs = 0;
        LastSlowestUnfillChannel = -1;
        LastSlowestUnfillCells = 0;
    }

    /// <summary>
    /// Lights every column in the world at once. Used by the audit and by anything that has the
    /// whole region in hand up front.
    /// </summary>
    public void LightAll(VoxelWorld world)
    {
        ResetChunkCache();
        LastCellsVisited = 0;
        var columns = new HashSet<(int X, int Z)>();
        foreach (var chunk in world.Chunks) columns.Add((chunk.Position.X, chunk.Position.Z));

        foreach (var (x, z) in columns) SeedColumn(world, x, z);
        foreach (var chunk in world.Chunks) SeedEmitters(world, chunk);

        FloodAll(world);
        ReleaseBulkCapacity();

        foreach (var chunk in world.Chunks) chunk.Lit = true;
    }

    /// <summary>
    /// Seeds and floods one column of chunks. The streamer calls this once every chunk in the
    /// column has generated and its neighbours are in place.
    /// </summary>
    public void LightColumn(VoxelWorld world, int cx, int cz)
    {
        ResetChunkCache();
        LastCellsVisited = 0;
        SeedColumn(world, cx, cz);

        // Light already standing in the neighbours has to be re-offered to the new column, or a
        // cave mouth one block over the seam stays black. The column's own seeding cannot find it:
        // those cells were drained from the queue when their column was lit and this one did not
        // exist yet.
        EnqueueShell(world, cx, cz);

        var chunksTall = TerrainGenerator.WorldHeight / Chunk.Size;
        for (var cy = 0; cy < chunksTall; cy++)
            if (world.TryGetChunk(new ChunkPos(cx, cy, cz), out var chunk)) SeedEmitters(world, chunk);

        FloodAll(world);
        ReleaseBulkCapacity();

        for (var cy = 0; cy < chunksTall; cy++)
            if (world.TryGetChunk(new ChunkPos(cx, cy, cz), out var chunk)) chunk.Lit = true;
    }

    /// <summary>
    /// Brings light back into agreement with the world after one block changed. The block must
    /// already have been written.
    /// </summary>
    /// <remarks>
    /// <para>Re-lighting the whole column instead would be simpler and wrong. Light that spilled out
    /// of this column into its neighbours survives the clear, and the shell pass carries it straight
    /// back in — so walling off a shaft would leave the shaft lit by its own reflection. Light that
    /// has to <em>go away</em> is the case a from-scratch pass cannot do, and going away is half of
    /// what block editing is.</para>
    /// <para>So each channel is torn down before it is rebuilt: everything the old cell was feeding
    /// is zeroed, and any cell found to be at least as bright as the wave passing it is set aside as
    /// a source that will fill the hole back in. The second half is then the ordinary flood.</para>
    /// </remarks>
    public void UpdateBlock(VoxelWorld world, int wx, int wy, int wz)
    {
        if (wy < 0 || wy >= TerrainGenerator.WorldHeight) return;
        ResetChunkCache();
        ResetCounters();

        var started = Stopwatch.GetTimestamp();
        var block = BlockAt(world, wx, wy, wz);
        var loss = _attenuation[block];
        var emission = _emission[block];

        for (var channel = 0; channel < ChannelCount; channel++)
        {
            var existing = Channel(GetLight(world, wx, wy, wz), channel);
            if (existing > 0)
            {
                SetChannel(world, wx, wy, wz, channel, 0);
                _removals.Enqueue(new Removal(wx, wy, wz, existing));

                var before = Stopwatch.GetTimestamp();
                var cellsBefore = LastRemovalCells;
                Unfill(world, channel);

                var spent = Stopwatch.GetTimestamp() - before;
                _unfillTicks += spent;
                LastUnfillPasses++;

                var ms = spent * 1000.0 / Stopwatch.Frequency;
                if (ms > LastSlowestUnfillMs)
                {
                    LastSlowestUnfillMs = ms;
                    LastSlowestUnfillChannel = channel;
                    LastSlowestUnfillCells = LastRemovalCells - cellsBefore;
                }
            }

            // Whatever stands around the cell now gets the chance to fill it in again. Offering the
            // neighbours rather than the cell itself is what lets sunlight fall into a hole that was
            // just opened: the cell above is already at full strength and knows the rule for down.
            if (loss >= LightValue.Max) continue;

            for (var face = 0; face < Faces.Count; face++)
            {
                var n = Faces.Normals[face];
                var ny = wy + n.Y;
                if (ny < 0 || ny >= TerrainGenerator.WorldHeight) continue;
                _additions[channel].Enqueue(new Node(wx + n.X, ny, wz + n.Z));
            }
        }

        if (emission != 0) SeedEmitterAt(world, wx, wy, wz, emission);

        var split = Stopwatch.GetTimestamp();
        FloodAll(world);
        var done = Stopwatch.GetTimestamp();

        LastRemovalMs = (split - started) * 1000.0 / Stopwatch.Frequency;
        LastFillMs = (done - split) * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>
    /// Walks each of the column's 32x32 world columns down from the top of the world, handing out
    /// sunlight until something stops it.
    /// </summary>
    /// <remarks>
    /// Straight down is the one direction light does not weaken in, which is what makes a shaft dug
    /// to the surface bright at the bottom rather than fading out after fifteen blocks. The moment
    /// anything dims the beam — a leaf, a metre of water — it stops being a beam and falls off
    /// normally from there.
    /// </remarks>
    private void SeedColumn(VoxelWorld world, int cx, int cz)
    {
        var chunksTall = TerrainGenerator.WorldHeight / Chunk.Size;

        // Start from nothing so re-lighting a column is the same operation as lighting it. What
        // was standing in it either gets re-derived here or re-offered from the shell.
        for (var cy = 0; cy < chunksTall; cy++)
        {
            if (!world.TryGetChunk(new ChunkPos(cx, cy, cz), out var chunk)) continue;
            Array.Clear(chunk.RawLight);
            chunk.Dirty = true;
        }

        var ox = cx * Chunk.Size;
        var oz = cz * Chunk.Size;
        var top = TerrainGenerator.WorldHeight - 1;

        for (var lz = 0; lz < Chunk.Size; lz++)
        for (var lx = 0; lx < Chunk.Size; lx++)
        {
            var wx = ox + lx;
            var wz = oz + lz;
            var level = LightValue.Max;

            for (var wy = top; wy >= 0; wy--)
            {
                var loss = _attenuation[BlockAt(world, wx, wy, wz)];
                if (loss >= LightValue.Max) break;   // opaque: everything below stays dark

                // Deliberately the same arithmetic the flood uses one step downward, not a second
                // rule that happens to agree. Two formulas for the same physical step is how a
                // seeded column and a flooded one end up differing by a level along a seam.
                level = Attenuate(level, loss, ChannelSky, down: true);
                if (level <= 0) break;

                SetChannel(world, wx, wy, wz, ChannelSky, level);
                _additions[ChannelSky].Enqueue(new Node(wx, wy, wz));
            }
        }
    }

    /// <summary>Re-offers the light standing in the one-block shell around a column.</summary>
    private void EnqueueShell(VoxelWorld world, int cx, int cz)
    {
        var ox = cx * Chunk.Size;
        var oz = cz * Chunk.Size;
        var top = TerrainGenerator.WorldHeight - 1;

        for (var wy = 0; wy <= top; wy++)
        for (var i = 0; i < Chunk.Size; i++)
        {
            Offer(ox + i, wy, oz - 1);
            Offer(ox + i, wy, oz + Chunk.Size);
            Offer(ox - 1, wy, oz + i);
            Offer(ox + Chunk.Size, wy, oz + i);
        }

        void Offer(int wx, int wy, int wz)
        {
            var light = GetLight(world, wx, wy, wz);
            if (light == 0) return;

            for (var channel = 0; channel < ChannelCount; channel++)
                if (Channel(light, channel) > 0) _additions[channel].Enqueue(new Node(wx, wy, wz));
        }
    }

    private void SeedEmitters(VoxelWorld world, Chunk chunk)
    {
        var (ox, oy, oz) = chunk.Position.Origin;
        var raw = chunk.Raw;

        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        for (var x = 0; x < Chunk.Size; x++)
        {
            var emission = _emission[raw[Chunk.Index(x, y, z)]];
            if (emission == 0) continue;

            SeedEmitterAt(world, ox + x, oy + y, oz + z, emission);
        }
    }

    private void SeedEmitterAt(VoxelWorld world, int wx, int wy, int wz, ushort emission)
    {
        var light = GetLight(world, wx, wy, wz);

        for (var channel = ChannelRed; channel < ChannelCount; channel++)
        {
            var level = Math.Max(Channel(light, channel), Channel(emission, channel));
            if (level == 0) continue;

            SetChannel(world, wx, wy, wz, channel, level);
            _additions[channel].Enqueue(new Node(wx, wy, wz));
        }
    }

    /// <summary>Drains every channel's queue, spreading whatever is brighter into whatever is darker.</summary>
    /// <remarks>
    /// The visit counter is not cleared here. It used to be, which meant it was cleared after the
    /// removal pass had already run and before the refill — so a relight that tore out two thousand
    /// cells reported one. An instrument that resets in the middle of the thing it is measuring
    /// reads as a constant overhead with no work behind it, which is exactly how it looked.
    /// </remarks>
    private void FloodAll(VoxelWorld world)
    {
        for (var channel = 0; channel < ChannelCount; channel++) Fill(world, channel);
    }

    private void Fill(VoxelWorld world, int channel)
    {
        var queue = _additions[channel];

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            LastCellsVisited++;
            LastFillCells++;

            var level = Channel(GetLight(world, node.X, node.Y, node.Z), channel);
            if (level <= 0) continue;

            for (var face = 0; face < Faces.Count; face++)
            {
                var n = Faces.Normals[face];
                var nx = node.X + n.X;
                var ny = node.Y + n.Y;
                var nz = node.Z + n.Z;
                if (ny < 0 || ny >= TerrainGenerator.WorldHeight) continue;

                LastNeighbourTests++;
                var loss = _attenuation[BlockAt(world, nx, ny, nz)];
                if (loss >= LightValue.Max) continue;

                var target = Attenuate(level, loss, channel, n.Y < 0);
                if (target <= 0) continue;
                if (Channel(GetLight(world, nx, ny, nz), channel) >= target) continue;

                SetChannel(world, nx, ny, nz, channel, target);
                queue.Enqueue(new Node(nx, ny, nz));
            }
        }
    }

    /// <summary>
    /// Tears out everything one darkened cell was feeding, collecting the brighter cells that will
    /// fill the hole back in.
    /// </summary>
    private void Unfill(VoxelWorld world, int channel)
    {
        while (_removals.Count > 0)
        {
            var node = _removals.Dequeue();
            LastCellsVisited++;
            LastRemovalCells++;

            for (var face = 0; face < Faces.Count; face++)
            {
                var n = Faces.Normals[face];
                var nx = node.X + n.X;
                var ny = node.Y + n.Y;
                var nz = node.Z + n.Z;
                if (ny < 0 || ny >= TerrainGenerator.WorldHeight) continue;

                LastNeighbourTests++;
                var level = Channel(GetLight(world, nx, ny, nz), channel);
                if (level <= 0) continue;

                // Dimmer than the wave passing it means this cell was lit by the wave and has to
                // go. Equal or brighter means something else feeds it, so it stays and becomes a
                // source for the refill. Sunlight going straight down is the exception: a full beam
                // hands on its full value, so an equally bright cell below is still one we fed.
                var fed = level < node.Level
                       || (channel == ChannelSky && n.Y < 0
                           && node.Level == LightValue.Max && level == LightValue.Max);

                if (fed)
                {
                    SetChannel(world, nx, ny, nz, channel, 0);
                    _removals.Enqueue(new Removal(nx, ny, nz, level));
                }
                else
                {
                    _additions[channel].Enqueue(new Node(nx, ny, nz));
                }
            }
        }
    }

    /// <summary>What one step of light costs, given what it is stepping into.</summary>
    private static int Attenuate(int level, int loss, int channel, bool down) =>
        channel == ChannelSky && down && level == LightValue.Max && loss == 0
            ? LightValue.Max
            : level - 1 - loss;

    private static int Channel(ushort packed, int channel) => (packed >> (channel * 4)) & 0xF;

    /// <summary>Chunk holding a world cell, remembering the last one asked for.</summary>
    /// <remarks>
    /// Reset at every public entry point rather than held across calls: streaming drops chunks
    /// between them, and a cached reference to a forgotten chunk would write light into memory
    /// nothing reads and read light from a world that has moved on.
    /// </remarks>
    private Chunk? ChunkAt(VoxelWorld world, int wx, int wy, int wz)
    {
        var pos = ChunkPos.FromWorld(wx, wy, wz);
        if (pos == _cachedPos) return _cachedChunk;

        LastChunkMisses++;
        _cachedPos = pos;
        _cachedChunk = world.TryGetChunk(pos, out var chunk) ? chunk : null;
        return _cachedChunk;
    }

    /// <summary>
    /// Gives back the queue capacity a bulk pass needed, so an interactive edit does not inherit it.
    /// </summary>
    /// <remarks>
    /// <para>Lighting a region seeds sunlight for every column in it, which for a 3x3 region is
    /// over a million nodes and grows the sky queue to some tens of megabytes. <see cref="Queue{T}"/>
    /// never gives that back. The next edit then enqueues its four hundred nodes into a buffer whose
    /// pages have not been touched since, and pays first-touch costs on all of them — deterministic,
    /// invisible to any counter of cells or neighbours, and proportional to how much sunlight the
    /// region needed rather than to anything the edit did.</para>
    /// <para>That is exactly the shape of the bug this was written for: one edit reporting 2.5 ms
    /// for 458 cells while another seed did 1,435 cells in 0.08 ms with three times the neighbour
    /// tests. The work counters ruled out the flood; nothing about the flood was ever wrong.</para>
    /// </remarks>
    private void ReleaseBulkCapacity()
    {
        for (var c = 0; c < ChannelCount; c++) _additions[c].TrimExcess();
        _removals.TrimExcess();
    }

    private void ResetChunkCache()
    {
        _cachedPos = new ChunkPos(int.MinValue, int.MinValue, int.MinValue);
        _cachedChunk = null;
    }

    private ushort BlockAt(VoxelWorld world, int wx, int wy, int wz)
    {
        var chunk = ChunkAt(world, wx, wy, wz);
        return chunk is null
            ? (ushort)0
            : chunk.Raw[Chunk.Index(wx & Chunk.SizeMask, wy & Chunk.SizeMask, wz & Chunk.SizeMask)];
    }

    private ushort GetLight(VoxelWorld world, int wx, int wy, int wz)
    {
        if (wy < 0 || wy >= TerrainGenerator.WorldHeight) return 0;

        var chunk = ChunkAt(world, wx, wy, wz);
        return chunk?.GetLight(wx & Chunk.SizeMask, wy & Chunk.SizeMask, wz & Chunk.SizeMask) ?? 0;
    }

    private void SetChannel(VoxelWorld world, int wx, int wy, int wz, int channel, int value)
    {
        var chunk = ChunkAt(world, wx, wy, wz);
        if (chunk is null) return;

        var lx = wx & Chunk.SizeMask;
        var ly = wy & Chunk.SizeMask;
        var lz = wz & Chunk.SizeMask;

        var shift = channel * 4;
        var current = chunk.GetLight(lx, ly, lz);
        var updated = (ushort)((current & ~(0xF << shift)) | ((value & 0xF) << shift));

        if (chunk.SetLight(lx, ly, lz, updated)) chunk.Dirty = true;
    }
}
