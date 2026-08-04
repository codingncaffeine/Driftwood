using Driftwood.Core.Blocks;
using Driftwood.Core.Gen;
using Driftwood.Core.World;

namespace Driftwood.Core.Lighting;

/// <summary>
/// Fills the world's light arrays: sunlight down from the sky, coloured light out from emitters,
/// both carried onward by a breadth-first flood.
/// </summary>
/// <remarks>
/// <para>Two seeds, one flood. Sunlight is seeded by walking each column down from the top of the
/// world, which is exact and costs one pass; the flood then only has to carry it sideways into
/// whatever the column pass left dark — under overhangs, into cave mouths, through doorways. Block
/// light has no column structure at all and is seeded straight from its emitters.</para>
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
    private readonly byte[] _attenuation;
    private readonly ushort[] _emission;

    private readonly Queue<Node> _skyQueue = new();
    private readonly Queue<Node> _blockQueue = new();

    private readonly record struct Node(int X, int Y, int Z);

    public LightEngine(BlockRegistry registry)
    {
        _attenuation = registry.BuildLightAttenuationTable();
        _emission = registry.BuildLightEmissionTable();
    }

    /// <summary>Cells taken off a queue by the last flood, for reporting propagation cost.</summary>
    public long LastCellsVisited { get; private set; }

    /// <summary>
    /// Lights every column in the world at once. Used by the audit and by anything that has the
    /// whole region in hand up front.
    /// </summary>
    public void LightAll(VoxelWorld world)
    {
        var columns = new HashSet<(int X, int Z)>();
        foreach (var chunk in world.Chunks) columns.Add((chunk.Position.X, chunk.Position.Z));

        foreach (var (x, z) in columns) SeedColumn(world, x, z);
        foreach (var chunk in world.Chunks) SeedEmitters(world, chunk);

        Flood(world);

        foreach (var chunk in world.Chunks) chunk.Lit = true;
    }

    /// <summary>
    /// Seeds and floods one column of chunks. The streamer calls this once every chunk in the
    /// column has generated and its neighbours are in place.
    /// </summary>
    public void LightColumn(VoxelWorld world, int cx, int cz)
    {
        SeedColumn(world, cx, cz);

        // Light already standing in the neighbours has to be re-offered to the new column, or a
        // cave mouth one block over the seam stays black. The column's own seeding cannot find it:
        // those cells were drained from the queue when their column was lit and this one did not
        // exist yet.
        EnqueueShell(world, cx, cz);

        var chunksTall = TerrainGenerator.WorldHeight / Chunk.Size;
        for (var cy = 0; cy < chunksTall; cy++)
            if (world.TryGetChunk(new ChunkPos(cx, cy, cz), out var chunk)) SeedEmitters(world, chunk);

        Flood(world);

        for (var cy = 0; cy < chunksTall; cy++)
            if (world.TryGetChunk(new ChunkPos(cx, cy, cz), out var chunk)) chunk.Lit = true;
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
                var loss = _attenuation[world.GetBlock(wx, wy, wz).Value];
                if (loss >= LightValue.Max) break;   // opaque: everything below stays dark

                // Deliberately the same arithmetic the flood uses one step downward, not a second
                // rule that happens to agree. Two formulas for the same physical step is how a
                // seeded column and a flooded one end up differing by a level along a seam.
                level = level == LightValue.Max && loss == 0
                    ? LightValue.Max
                    : level - 1 - loss;

                if (level <= 0) break;

                SetSky(world, wx, wy, wz, level);
                _skyQueue.Enqueue(new Node(wx, wy, wz));
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
        {
            for (var i = 0; i < Chunk.Size; i++)
            {
                Offer(ox + i, wy, oz - 1);
                Offer(ox + i, wy, oz + Chunk.Size);
                Offer(ox - 1, wy, oz + i);
                Offer(ox + Chunk.Size, wy, oz + i);
            }
        }

        void Offer(int wx, int wy, int wz)
        {
            var light = GetLight(world, wx, wy, wz);
            if (light == 0) return;

            if (LightValue.Sky(light) > 0) _skyQueue.Enqueue(new Node(wx, wy, wz));
            if (LightValue.BlockPeak(light) > 0) _blockQueue.Enqueue(new Node(wx, wy, wz));
        }
    }

    private void SeedEmitters(VoxelWorld world, Chunk chunk)
    {
        var (ox, oy, oz) = chunk.Position.Origin;
        var raw = chunk.Raw;
        var light = chunk.RawLight;

        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        for (var x = 0; x < Chunk.Size; x++)
        {
            var i = Chunk.Index(x, y, z);
            var emission = _emission[raw[i]];
            if (emission == 0) continue;

            light[i] = (ushort)(LightValue.Sky(light[i])
                     | LightValue.MaxBlock((ushort)(light[i] & LightValue.BlockMask), emission));
            chunk.Dirty = true;

            _blockQueue.Enqueue(new Node(ox + x, oy + y, oz + z));
        }
    }

    /// <summary>Drains both queues, spreading whatever is brighter into whatever is darker.</summary>
    private void Flood(VoxelWorld world)
    {
        LastCellsVisited = 0;

        while (_skyQueue.Count > 0)
        {
            LastCellsVisited++;
            SpreadSky(world, _skyQueue.Dequeue());
        }

        while (_blockQueue.Count > 0)
        {
            LastCellsVisited++;
            SpreadBlock(world, _blockQueue.Dequeue());
        }
    }

    private void SpreadSky(VoxelWorld world, Node node)
    {
        var level = LightValue.Sky(GetLight(world, node.X, node.Y, node.Z));
        if (level <= 0) return;

        for (var face = 0; face < Faces.Count; face++)
        {
            var n = Faces.Normals[face];
            var nx = node.X + n.X;
            var ny = node.Y + n.Y;
            var nz = node.Z + n.Z;
            if (ny < 0 || ny >= TerrainGenerator.WorldHeight) continue;

            var loss = _attenuation[world.GetBlock(nx, ny, nz).Value];
            if (loss >= LightValue.Max) continue;

            // Sunlight falling straight down through clear air keeps its full value; every other
            // direction, and any dimming block, costs the usual level.
            var target = n.Y < 0 && level == LightValue.Max && loss == 0
                ? LightValue.Max
                : level - 1 - loss;

            if (target <= 0) continue;
            if (LightValue.Sky(GetLight(world, nx, ny, nz)) >= target) continue;

            SetSky(world, nx, ny, nz, target);
            _skyQueue.Enqueue(new Node(nx, ny, nz));
        }
    }

    private void SpreadBlock(VoxelWorld world, Node node)
    {
        var here = GetLight(world, node.X, node.Y, node.Z);
        var r = LightValue.Red(here);
        var g = LightValue.Green(here);
        var b = LightValue.Blue(here);
        if (r <= 0 && g <= 0 && b <= 0) return;

        for (var face = 0; face < Faces.Count; face++)
        {
            var n = Faces.Normals[face];
            var nx = node.X + n.X;
            var ny = node.Y + n.Y;
            var nz = node.Z + n.Z;
            if (ny < 0 || ny >= TerrainGenerator.WorldHeight) continue;

            var loss = _attenuation[world.GetBlock(nx, ny, nz).Value];
            if (loss >= LightValue.Max) continue;

            var step = 1 + loss;
            var tr = Math.Max(r - step, 0);
            var tg = Math.Max(g - step, 0);
            var tb = Math.Max(b - step, 0);
            if (tr == 0 && tg == 0 && tb == 0) continue;

            var current = GetLight(world, nx, ny, nz);
            var updated = LightValue.Pack(
                LightValue.Sky(current),
                Math.Max(LightValue.Red(current), tr),
                Math.Max(LightValue.Green(current), tg),
                Math.Max(LightValue.Blue(current), tb));

            if (updated == current) continue;

            SetLight(world, nx, ny, nz, updated);
            _blockQueue.Enqueue(new Node(nx, ny, nz));
        }
    }

    private static ushort GetLight(VoxelWorld world, int wx, int wy, int wz)
    {
        if (wy < 0 || wy >= TerrainGenerator.WorldHeight) return 0;

        var pos = ChunkPos.FromWorld(wx, wy, wz);
        return world.TryGetChunk(pos, out var chunk)
            ? chunk.GetLight(wx & Chunk.SizeMask, wy & Chunk.SizeMask, wz & Chunk.SizeMask)
            : (ushort)0;
    }

    private static void SetLight(VoxelWorld world, int wx, int wy, int wz, ushort value)
    {
        var pos = ChunkPos.FromWorld(wx, wy, wz);
        if (!world.TryGetChunk(pos, out var chunk)) return;

        if (chunk.SetLight(wx & Chunk.SizeMask, wy & Chunk.SizeMask, wz & Chunk.SizeMask, value))
            chunk.Dirty = true;
    }

    private static void SetSky(VoxelWorld world, int wx, int wy, int wz, int sky)
    {
        var pos = ChunkPos.FromWorld(wx, wy, wz);
        if (!world.TryGetChunk(pos, out var chunk)) return;

        var lx = wx & Chunk.SizeMask;
        var ly = wy & Chunk.SizeMask;
        var lz = wz & Chunk.SizeMask;

        var updated = LightValue.WithSky(chunk.GetLight(lx, ly, lz), sky);
        if (chunk.SetLight(lx, ly, lz, updated)) chunk.Dirty = true;
    }
}
