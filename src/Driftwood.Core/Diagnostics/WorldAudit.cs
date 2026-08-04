using System.Diagnostics;
using System.Text;
using Driftwood.Core.Blocks;
using Driftwood.Core.Gen;
using Driftwood.Core.Meshing;
using Driftwood.Core.World;

namespace Driftwood.Core.Diagnostics;

/// <summary>
/// Generates and meshes a world headlessly, then reports what came out.
/// </summary>
/// <remarks>
/// This exists so the generator and mesher can be checked without a window. A screenshot tells
/// you the world looked fine from one angle; a block histogram tells you whether ore actually
/// spawned, whether caves opened, and whether the sea filled to the right level across the whole
/// volume. It is also the regression harness: a seed plus this report is a receipt that survives
/// into later phases.
/// </remarks>
public static class WorldAudit
{
    public sealed record Result(string Report, bool Passed);

    public static Result Run(WorldSeed seed, int chunksAcross)
    {
        var sb = new StringBuilder();
        var registry = new BlockRegistry();
        var ids = StarterBlocks.Register(registry);
        registry.Seal();

        var generator = new TerrainGenerator(seed, ids);
        var world = new VoxelWorld(registry);

        var half = chunksAcross / 2;
        var chunksTall = TerrainGenerator.WorldHeight / Chunk.Size;

        var positions = new List<ChunkPos>(chunksAcross * chunksAcross * chunksTall);
        for (var cy = 0; cy < chunksTall; cy++)
        for (var cz = -half; cz < chunksAcross - half; cz++)
        for (var cx = -half; cx < chunksAcross - half; cx++)
            positions.Add(new ChunkPos(cx, cy, cz));

        var chunks = new Chunk[positions.Count];
        for (var i = 0; i < positions.Count; i++) chunks[i] = world.GetOrCreateChunk(positions[i]);

        var genWatch = Stopwatch.StartNew();
        Parallel.For(0, chunks.Length, i => generator.GenerateChunk(chunks[i]));
        genWatch.Stop();

        var decorWatch = Stopwatch.StartNew();
        var minBlock = -half * Chunk.Size;
        var maxBlock = (chunksAcross - half) * Chunk.Size - 1;
        generator.DecorateRegion(world, minBlock, minBlock, maxBlock, maxBlock);
        decorWatch.Stop();

        var meshWatch = Stopwatch.StartNew();
        var meshes = new ChunkMeshData?[positions.Count];
        Parallel.For(
            0,
            positions.Count,
            () => new ChunkMesher(registry),
            (i, _, mesher) =>
            {
                meshes[i] = mesher.Build(world, positions[i]);
                return mesher;
            },
            _ => { });
        meshWatch.Stop();

        // Block census across the whole generated volume.
        var counts = new long[registry.Count];
        var minY = new int[registry.Count];
        var maxY = new int[registry.Count];
        Array.Fill(minY, int.MaxValue);
        Array.Fill(maxY, int.MinValue);

        foreach (var chunk in chunks)
        {
            var (_, oy, _) = chunk.Position.Origin;
            var raw = chunk.Raw;
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            for (var x = 0; x < Chunk.Size; x++)
            {
                var id = raw[Chunk.Index(x, y, z)];
                counts[id]++;
                var wy = oy + y;
                if (wy < minY[id]) minY[id] = wy;
                if (wy > maxY[id]) maxY[id] = wy;
            }
        }

        long totalBlocks = 0;
        foreach (var c in counts) totalBlocks += c;

        var verts = 0L;
        var tris = 0L;
        var meshedChunks = 0;
        foreach (var m in meshes)
        {
            if (m is null) continue;
            meshedChunks++;
            verts += m.VertexCount;
            tris += m.TriangleCount;
        }

        var extent = chunksAcross * Chunk.Size;

        // Relief measured directly off the heightmap. Inferring it from the y-range of grass and
        // stone conflates "how tall is the terrain" with "where does each material sit", and the
        // two answers diverge exactly when the shaping curve is wrong.
        var surfaceMin = int.MaxValue;
        var surfaceMax = int.MinValue;
        long surfaceSum = 0;
        var samples = 0;
        var aboveSea = 0;
        for (var wz = minBlock; wz <= maxBlock; wz += 4)
        for (var wx = minBlock; wx <= maxBlock; wx += 4)
        {
            var h = generator.SurfaceHeight(wx, wz);
            if (h < surfaceMin) surfaceMin = h;
            if (h > surfaceMax) surfaceMax = h;
            surfaceSum += h;
            samples++;
            if (h > TerrainGenerator.SeaLevel) aboveSea++;
        }

        sb.AppendLine($"seed          {seed}");
        sb.AppendLine($"volume        {extent} x {TerrainGenerator.WorldHeight} x {extent} blocks ({totalBlocks:N0} total)");
        sb.AppendLine($"chunks        {positions.Count:N0} generated, {meshedChunks:N0} with geometry");
        sb.AppendLine();
        sb.AppendLine($"generate      {genWatch.ElapsedMilliseconds,6} ms   ({positions.Count / Math.Max(genWatch.Elapsed.TotalSeconds, 0.001):N0} chunks/s)");
        sb.AppendLine($"decorate      {decorWatch.ElapsedMilliseconds,6} ms");
        sb.AppendLine($"mesh          {meshWatch.ElapsedMilliseconds,6} ms   ({positions.Count / Math.Max(meshWatch.Elapsed.TotalSeconds, 0.001):N0} chunks/s)");
        sb.AppendLine();
        sb.AppendLine($"geometry      {verts:N0} verts, {tris:N0} tris, {verts * ChunkVertex.SizeInBytes / (1024.0 * 1024.0):F1} MiB vertex data");
        sb.AppendLine();
        sb.AppendLine($"relief        surface y {surfaceMin}..{surfaceMax} (span {surfaceMax - surfaceMin}), mean {surfaceSum / (double)samples:F1}");
        sb.AppendLine($"land          {aboveSea * 100.0 / samples:F1}% of columns above sea level {TerrainGenerator.SeaLevel}");
        sb.AppendLine();
        sb.AppendLine("block census");

        for (ushort id = 0; id < registry.Count; id++)
        {
            if (counts[id] == 0)
            {
                sb.AppendLine($"  {registry[id].Name,-12} {"absent",14}");
                continue;
            }

            var pct = counts[id] * 100.0 / totalBlocks;
            sb.AppendLine(
                $"  {registry[id].Name,-12} {counts[id],14:N0}  {pct,6:F2}%   y {minY[id]}..{maxY[id]}");
        }

        // Sanity gates. Each one has caught a real class of generator bug: an empty world, a
        // world with no sky, ore that never rolled, a sea that never filled, trees that all
        // failed their surface test.
        sb.AppendLine();
        sb.AppendLine("checks");

        var passed = true;
        void Check(string label, bool ok, string detail)
        {
            passed &= ok;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {label,-28} {detail}");
        }

        Check("world is not empty", counts[ids.Stone.Value] > 0, $"stone {counts[ids.Stone.Value]:N0}");
        Check("world is not solid", counts[0] > totalBlocks / 10, $"air {counts[0] * 100.0 / totalBlocks:F1}%");
        Check("surface has grass", counts[ids.Grass.Value] > 0, $"grass {counts[ids.Grass.Value]:N0}");
        Check("sea filled", counts[ids.Water.Value] > 0, $"water {counts[ids.Water.Value]:N0}");
        Check("coal spawned", counts[ids.CoalOre.Value] > 0, $"coal {counts[ids.CoalOre.Value]:N0}");
        Check("iron spawned", counts[ids.IronOre.Value] > 0, $"iron {counts[ids.IronOre.Value]:N0}");
        Check("iron is deep", maxY[ids.IronOre.Value] <= 58, $"max y {maxY[ids.IronOre.Value]}");
        Check("trees planted", counts[ids.Log.Value] > 0, $"log {counts[ids.Log.Value]:N0}, leaves {counts[ids.Leaves.Value]:N0}");
        Check("bedrock floor", counts[ids.Bedrock.Value] > 0 && maxY[ids.Bedrock.Value] == 0, $"y {minY[ids.Bedrock.Value]}..{maxY[ids.Bedrock.Value]}");
        Check("caves opened", minY[0] <= 2, $"lowest air at y {minY[0]}");
        Check("geometry produced", tris > 0, $"{tris:N0} tris");

        // Relief and mix gates. A world can pass every "does this block exist" check above and
        // still be a featureless plain, or be 98% ocean, or have ore so rare it never gates
        // anything. These are the checks that notice.
        Check("terrain has relief", surfaceMax - surfaceMin >= 45, $"span {surfaceMax - surfaceMin} blocks");
        Check("both land and sea", aboveSea > samples / 10 && aboveSea < samples * 9 / 10, $"{aboveSea * 100.0 / samples:F1}% land");
        // Ore gets a band, not a floor. Too little and mining never gates progression; too much
        // and it stops being a reward. A floor-only check passes a world where one stone block
        // in fifty is coal, which is how the first calibration pass slipped through.
        var stone = (double)counts[ids.Stone.Value];
        var coalPct = counts[ids.CoalOre.Value] * 100.0 / stone;
        var ironPct = counts[ids.IronOre.Value] * 100.0 / stone;
        Check("coal rate in band", coalPct is > 0.30 and < 1.50, $"{coalPct:F3}% of stone (want 0.30-1.50)");
        Check("iron rate in band", ironPct is > 0.15 and < 0.80, $"{ironPct:F3}% of stone (want 0.15-0.80)");
        Check("coal beats iron", counts[ids.CoalOre.Value] > counts[ids.IronOre.Value], $"{counts[ids.CoalOre.Value]:N0} vs {counts[ids.IronOre.Value]:N0}");

        return new Result(sb.ToString(), passed);
    }
}
