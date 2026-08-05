using System.Diagnostics;
using System.Numerics;
using System.Text;
using Driftwood.Core.Audio;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Gen;
using Driftwood.Core.Lighting;
using Driftwood.Core.Meshing;
using Driftwood.Core.Particles;
using Driftwood.Core.Physics;
using Driftwood.Core.Sky;
using Driftwood.Core.Spatial;
using Driftwood.Core.Textures;
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

    public static Result Run(WorldSeed seed, int chunksAcross, float oceanCoverage = TerrainGenerator.DefaultOceanCoverage)
    {
        var sb = new StringBuilder();
        var registry = new BlockRegistry();
        var ids = StarterBlocks.Register(registry);
        registry.Seal();

        var generator = new TerrainGenerator(seed, ids, oceanCoverage);
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

        var minBlock = -half * Chunk.Size;
        var maxBlock = (chunksAcross - half) * Chunk.Size - 1;

        // Decoration is per-chunk and order-independent now, so it parallelises like generation.
        var decorWatch = Stopwatch.StartNew();
        Parallel.For(0, chunks.Length, i => generator.DecorateChunk(chunks[i]));
        decorWatch.Stop();

        // Lighting runs before meshing because the mesh bakes light into its vertices; meshing
        // first would measure and check geometry lit by nothing.
        var lightWatch = Stopwatch.StartNew();
        var lightEngine = new LightEngine(registry);
        lightEngine.LightAll(world);
        lightWatch.Stop();

        // Meshed with a tinter, because that is what the game ships. An audit that measures an
        // untinted mesh is measuring a build nobody runs — and it will report the same geometry
        // however much the tint path costs, which is exactly the sort of quiet blindness these
        // reports exist to prevent.
        var tinter = new BlockTinter(new ClimateField(seed));

        var meshWatch = Stopwatch.StartNew();
        var meshes = new ChunkMeshData?[positions.Count];
        var quadCounts = new int[positions.Count];
        var coveredFaces = new int[positions.Count];
        var modelQuads = new int[positions.Count];
        Parallel.For(
            0,
            positions.Count,
            () => new ChunkMesher(registry, tinter),
            (i, _, mesher) =>
            {
                meshes[i] = mesher.Build(world, positions[i]);
                quadCounts[i] = mesher.LastQuadCount;
                coveredFaces[i] = mesher.LastCoveredFaces;
                modelQuads[i] = mesher.LastModelQuads;
                return mesher;
            },
            _ => { });
        meshWatch.Stop();

        // Independent count of visible unit faces, taken one block at a time without any merging.
        // Greedy meshing is easy to get subtly wrong — a run that stops one short leaves a seam,
        // one that runs long double-covers — and neither shows up in a block census or a vertex
        // total. Summing width by height over the merged quads has to land on exactly this number.
        var naiveFaces = new int[positions.Count];
        Parallel.For(
            0,
            positions.Count,
            () => new ChunkMesher(registry),
            (i, _, mesher) =>
            {
                naiveFaces[i] = mesher.CountVisibleFaces(world, positions[i]);
                return mesher;
            },
            _ => { });

        long totalQuads = 0, totalCovered = 0, totalNaive = 0, totalModelQuads = 0;
        var mismatchedChunks = 0;
        for (var i = 0; i < positions.Count; i++)
        {
            totalQuads += quadCounts[i];
            totalCovered += coveredFaces[i];
            totalNaive += naiveFaces[i];
            totalModelQuads += modelQuads[i];
            if (coveredFaces[i] != naiveFaces[i]) mismatchedChunks++;
        }

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
        var (surfaceMin, surfaceMax, surfaceMean, aboveSeaPct) = SampleRelief(generator, minBlock, maxBlock, 4);

        // The generator gets judged over a fixed span, not over whatever box --chunks asked for.
        // Continent wavelength is 640 blocks, so a 384-block window can sit entirely inside one
        // continent's flat interior and read as broken terrain when the generator is fine. Those
        // are different questions and only the wide sample answers "can this seed make mountains".
        const int ReliefProbeSpan = 4096;
        var (probeMin, probeMax, _, probeLandPct) =
            SampleRelief(generator, -ReliefProbeSpan / 2, ReliefProbeSpan / 2, 16);

        sb.AppendLine($"seed          {seed}");
        sb.AppendLine($"volume        {extent} x {TerrainGenerator.WorldHeight} x {extent} blocks ({totalBlocks:N0} total)");
        sb.AppendLine($"chunks        {positions.Count:N0} generated, {meshedChunks:N0} with geometry");
        sb.AppendLine();
        sb.AppendLine($"generate      {genWatch.ElapsedMilliseconds,6} ms   ({positions.Count / Math.Max(genWatch.Elapsed.TotalSeconds, 0.001):N0} chunks/s)");
        sb.AppendLine($"decorate      {decorWatch.ElapsedMilliseconds,6} ms");
        sb.AppendLine($"light         {lightWatch.ElapsedMilliseconds,6} ms   ({lightEngine.LastCellsVisited:N0} cells flooded)");
        sb.AppendLine($"mesh          {meshWatch.ElapsedMilliseconds,6} ms   ({positions.Count / Math.Max(meshWatch.Elapsed.TotalSeconds, 0.001):N0} chunks/s)");
        sb.AppendLine();
        sb.AppendLine($"geometry      {verts:N0} verts, {tris:N0} tris, {verts * ChunkVertex.SizeInBytes / (1024.0 * 1024.0):F1} MiB vertex data");
        sb.AppendLine($"merging       {totalNaive:N0} visible cube faces -> {totalQuads - totalModelQuads:N0} quads "
                    + $"({totalNaive / (double)Math.Max(totalQuads - totalModelQuads, 1):F2}x fewer)");
        sb.AppendLine($"shapes        {totalModelQuads:N0} quads from block models, unmerged "
                    + $"({totalModelQuads * 100.0 / Math.Max(totalQuads, 1):F1}% of all quads)");
        sb.AppendLine();
        sb.AppendLine($"relief        surface y {surfaceMin}..{surfaceMax} (span {surfaceMax - surfaceMin}), mean {surfaceMean:F1}   [this world]");
        sb.AppendLine($"              surface y {probeMin}..{probeMax} (span {probeMax - probeMin})              [seed over {ReliefProbeSpan} blocks]");
        sb.AppendLine($"land          {aboveSeaPct:F1}% of columns above sea level {TerrainGenerator.SeaLevel}");
        sb.AppendLine($"ocean         {100 - probeLandPct:F1}% measured, {generator.OceanCoverage * 100:F0}% requested");

        var trees = SurveyTrees(world, chunks, ids);
        sb.AppendLine($"trees         {trees.Count:N0} trunks, {trees.MinTrunk}..{trees.MaxTrunk} logs "
                    + $"(mean {trees.MeanTrunk:F1}), crown reaches {trees.MinCrown}..{trees.MaxCrown} above ground");

        var canopy = SurveyCanopy(world, chunks, ids);
        sb.AppendLine($"canopy        {canopy.Clusters:N0} connected leaf masses, mean {canopy.MeanSize:F0} blocks, "
                    + $"largest {canopy.Largest:N0}");

        var light = SurveyLight(world, chunks, registry);
        sb.AppendLine($"sunlight      {light.SkyFullPct:F1}% of open air at full strength, "
                    + $"{light.SkyDarkPct:F1}% of open air in shadow");
        sb.AppendLine($"block light   {light.BlockLitCells:N0} cells reached, "
                    + $"peak {light.BlockPeak}, {light.ColouredCells:N0} of them off-white");
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
        Check("terrain has relief", probeMax - probeMin >= 55, $"span {probeMax - probeMin} blocks over {ReliefProbeSpan}");

        // Ocean coverage is calibrated, not emergent, so it gets held to the number it was asked
        // for. The tolerance covers the probe sampling a different grid than the calibration did.
        var oceanMeasured = (100 - probeLandPct) / 100.0;
        Check(
            "ocean hits its target",
            Math.Abs(oceanMeasured - generator.OceanCoverage) < 0.06,
            $"{oceanMeasured * 100:F1}% vs {generator.OceanCoverage * 100:F0}% requested");

        // Face winding is invisible to a block census: geometry exists, the wrong side of it is
        // drawn. Shipped inverted on the horizontal faces once already, which read as see-through
        // ground.
        var windingFaults = Faces.ValidateWinding();
        Check("face winding is outward", windingFaults.Count == 0,
            windingFaults.Count == 0 ? "all 6 faces" : string.Join("; ", windingFaults));

        var neighbourIndependent = ChunksAreNeighbourIndependent(seed, registry, ids, oceanCoverage, out var neighbourDetail);
        Check("chunk ignores neighbours", neighbourIndependent, neighbourDetail);

        var streamingMatches = StreamingMatchesBatch(seed, registry, ids, oceanCoverage, out var streamDetail);
        Check("streamed == batch meshes", streamingMatches, streamDetail);

        var frustumFaults = Frustum.SelfTest();
        Check("frustum culls correctly", frustumFaults.Count == 0,
            frustumFaults.Count == 0
                ? "24 yaws + straight down"
                : $"{frustumFaults.Count} faults: {frustumFaults[0]}");

        Check(
            "merged area == naive area",
            mismatchedChunks == 0 && totalCovered == totalNaive,
            mismatchedChunks == 0
                ? $"{totalCovered:N0} faces across {positions.Count:N0} chunks"
                : $"{mismatchedChunks} chunks differ, {totalCovered:N0} merged vs {totalNaive:N0} naive");
        // Shapes. Everything below is invisible to a block census: a model can be wound inside out,
        // read the wrong corner of its texture, or never reach the mesh at all, and the world still
        // generates exactly the same blocks in exactly the same places.
        var modelQuadTotal = 0;
        foreach (var type in registry.All) modelQuadTotal += type.Model.Quads.Length;

        var modelFaults = BlockModel.Validate(registry.All);
        Check("block models are sound", modelFaults.Count == 0,
            modelFaults.Count == 0
                ? $"{modelQuadTotal} quads across {registry.Count} shapes"
                : $"{modelFaults.Count} faults: {modelFaults[0]}");

        var vertexFaults = VertexPackingSelfTest();
        Check("vertex fields do not collide", vertexFaults.Count == 0,
            vertexFaults.Count == 0
                ? $"{ChunkVertex.SizeInBytes} bytes, 1/{ChunkVertex.PositionScale} block, {ChunkVertex.MaxLayer + 1} layers"
                : $"{vertexFaults.Count} faults: {vertexFaults[0]}");

        Check("texture layers fit the vertex", StarterBlocks.LayerCount - 1 <= ChunkVertex.MaxLayer,
            $"{StarterBlocks.LayerCount} layers, {ChunkVertex.MaxLayer + 1} addressable");

        // The overlay pass is the whole reason models came first. Its four sides must be there, they
        // must be tinted, and it must not have a top or a bottom — an overlay over the top face
        // would double the climate colour on the one face that already carries it.
        var grassModel = registry[ids.Grass].Model;
        var overlaySides = 0;
        var overlayCaps = 0;
        if (grassModel.PassCount > 1)
        {
            for (var face = 0; face < Faces.Count; face++)
            {
                if (grassModel.PassLayer(1, face) == BlockModel.NoLayer) continue;
                if (face is Faces.PosY or Faces.NegY) overlayCaps++;
                else if (grassModel.PassTinted(1, face)) overlaySides++;
            }
        }

        Check(
            "grass wears its overlay",
            grassModel.IsFullCube && grassModel.PassCount == 2 && overlaySides == 4 && overlayCaps == 0,
            $"{grassModel.PassCount} passes, {overlaySides} tinted sides, {overlayCaps} caps");

        // A crossed-plane plant that never got rescaled sits inside the middle 64% of its cell and
        // reads as a small card rather than as something growing out of the ground. Measuring how
        // far the planes reach across the block is what tells the two apart; counting quads does not.
        var tuft = registry[ids.Meadowgrass].Model;
        var tuftLow = float.MaxValue;
        var tuftHigh = float.MinValue;
        foreach (var quad in tuft.Quads)
        foreach (var corner in quad.Corners)
        {
            tuftLow = MathF.Min(tuftLow, MathF.Min(corner.Position.X, corner.Position.Z));
            tuftHigh = MathF.Max(tuftHigh, MathF.Max(corner.Position.X, corner.Position.Z));
        }

        Check(
            "tufts fill their cell",
            !tuft.IsFullCube && tuft.Quads.Length == 4 && tuftLow < 0.10f && tuftHigh > 0.90f,
            $"{tuft.Quads.Length} quads spanning {tuftLow:F2}..{tuftHigh:F2} of the block");

        Check("shapes reach the mesh", totalModelQuads > 0,
            $"{totalModelQuads:N0} unmerged quads from {counts[ids.Meadowgrass.Value]:N0} shaped blocks");

        var volumeFaults = ShapeVolumeSelfTest(registry);
        Check("shapes fill the space they claim", volumeFaults.Count == 0,
            volumeFaults.Count == 0
                ? "slab half a cell, stairs three quarters, a dusting three sixteenths, a slender torch, "
                  + "and six outlines wrapping the shape rather than the cell"
                : $"{volumeFaults.Count} faults: {volumeFaults[0]}");

        var vitalsFaults = VitalsSelfTest(registry, ids);
        Check("falls and water cost health", vitalsFaults.Count == 0,
            vitalsFaults.Count == 0
                ? $"{PlayerVitals.SafeFall:F0} blocks free then a half-heart each, {PlayerVitals.MaxBreath / 60}s of breath, rest heals"
                : $"{vitalsFaults.Count} faults: {vitalsFaults[0]}");

        var soundFaults = SoundSelfTest(registry, out var soundDetail);
        Check("every material has a sound", soundFaults.Count == 0, soundFaults.Count == 0
            ? soundDetail
            : $"{soundFaults.Count} faults: {soundFaults[0]}");

        var particleFaults = ParticleSelfTest(registry, ids);
        Check("debris falls and settles", particleFaults.Count == 0,
            particleFaults.Count == 0
                ? "a burst lands on the floor, empties the pool, uses every crop, and allocates nothing"
                : $"{particleFaults.Count} faults: {particleFaults[0]}");

        var skyFaults = SkySelfTest();
        Check("the sun crosses the sky", skyFaults.Count == 0,
            skyFaults.Count == 0
                ? "east at dawn, overhead at noon, west at dusk, 1,440 minutes without a step"
                : $"{skyFaults.Count} faults: {skyFaults[0]}");

        // Cover is calibrated per sheet, so it gets held to the number it was asked for rather than
        // to a band wide enough to swallow the seed-to-seed swing it used to have.
        var cloudField = new CloudField(seed);
        var cloudFaults = CloudSelfTest(seed);
        Check("clouds cover part of the sky", cloudFaults.Count == 0 && cloudField.Coverage is > 0.36f and < 0.40f,
            cloudFaults.Count == 0
                ? $"{cloudField.Coverage * 100:F1}% cover (want 36-40), {cloudField.Build().QuadCount:N0} quads, "
                  + "every wall between a cloud and a gap, both seams wrapping"
                : $"{cloudFaults.Count} faults: {cloudFaults[0]}");

        var placementFaults = PlacementSelfTest(registry);
        Check("placement picks the right form", placementFaults.Count == 0,
            placementFaults.Count == 0
                ? $"{StarterBlocks.Hand(registry).Length} things in hand, every face, height and heading"
                : $"{placementFaults.Count} faults: {placementFaults[0]}");

        // A material nobody can find is a material that does not exist, and this is the check that
        // says so. It was written after the world turned out to hold two ores and no stone variants
        // at all — every block registered and textured, half of them nowhere in the ground. Anything
        // that is only ever built rather than dug is named here on purpose, so adding a block and
        // forgetting to place it fails rather than quietly joining the list.
        var missing = new List<string>();
        var crafted = 0;
        for (ushort id = 1; id < registry.Count; id++)
        {
            if (registry[id].Crafted) { crafted++; continue; }
            if (counts[id] > 0) continue;
            missing.Add(registry[id].Name);
        }

        Check("every material is in the world", missing.Count == 0,
            missing.Count == 0
                ? $"{registry.Count - crafted - 1} of {registry.Count - 1} blocks generate; {crafted} are built, not dug"
                : $"never generated: {string.Join(", ", missing)}");

        // Ore gets a band, not a floor. Too little and mining never gates progression; too much
        // and it stops being a reward. A floor-only check passes a world where one stone block
        // in fifty is coal, which is how the first calibration pass slipped through.
        //
        // Measured against all rock rather than against stone alone. Once ore could form in
        // deepstone and in the three intrusions, "percent of stone" was counting a numerator the
        // denominator no longer covered — the classic way a rate drifts without anything looking
        // wrong. See the block census above for the split.
        double rock = 0;
        foreach (var block in ids.Rock) rock += counts[block.Value];

        double Rate(BlockId ore) => counts[ore.Value] * 100.0 / rock;

        var coalPct = Rate(ids.CoalOre);
        var copperPct = Rate(ids.CopperOre);
        var ironPct = Rate(ids.IronOre);
        var goldPct = Rate(ids.GoldOre);
        var azuritePct = Rate(ids.AzuriteOre);
        var stormglassPct = Rate(ids.StormglassOre);

        Check("coal rate in band", coalPct is > 0.30 and < 1.20, $"{coalPct:F3}% of rock (want 0.30-1.20)");
        Check("copper rate in band", copperPct is > 0.20 and < 0.90, $"{copperPct:F3}% of rock (want 0.20-0.90)");
        Check("iron rate in band", ironPct is > 0.15 and < 0.70, $"{ironPct:F3}% of rock (want 0.15-0.70)");
        Check("gold rate in band", goldPct is > 0.04 and < 0.30, $"{goldPct:F3}% of rock (want 0.04-0.30)");
        Check("azurite rate in band", azuritePct is > 0.03 and < 0.28, $"{azuritePct:F3}% of rock (want 0.03-0.28)");
        Check("stormglass rate in band", stormglassPct is > 0.015 and < 0.12, $"{stormglassPct:F3}% of rock (want 0.015-0.12)");

        // The ladder, which is the check the individual bands cannot make. Every tier could sit
        // inside its own band and still come out in the wrong order, and the order is the whole
        // point: what makes stormglass worth going deep for is that it is rarer than everything
        // above it, not that it is rare in absolute terms.
        var deepest = Math.Min(goldPct, azuritePct);
        var ladder = coalPct > copperPct && copperPct > ironPct
                  && ironPct > Math.Max(goldPct, azuritePct) && deepest > stormglassPct;

        Check("the ore ladder holds", ladder,
            $"coal {coalPct:F2} > copper {copperPct:F2} > iron {ironPct:F2} > gold {goldPct:F2}/azurite {azuritePct:F2} > stormglass {stormglassPct:F3}");

        // Rock variety. One uniform grey underground is the failure this catches, and it looks
        // exactly like a working world in every other check here.
        var deepPct = counts[ids.Deepstone.Value] * 100.0 / rock;
        Check("deepstone owns the depths", deepPct is > 10.0 and < 50.0 && maxY[ids.Deepstone.Value] < 30,
            $"{deepPct:F1}% of rock (want 10-50), top at y {maxY[ids.Deepstone.Value]} (want under 30)");

        var intrusions = new[] { ids.Coralstone, ids.Driftstone, ids.Saltstone }
            .Select(b => (Name: registry[b].Name, Pct: Rate(b))).ToArray();

        Check("intrusions break up the rock", Array.TrueForAll(intrusions, i => i.Pct is > 1.0 and < 8.0),
            string.Join(", ", intrusions.Select(i => $"{i.Name} {i.Pct:F2}%")) + " of rock (want 1-8 each)");

        // Snow is gated on where it lies, not on how much of it there is.
        //
        // Coverage looked like the obvious measure and is nearly useless here: climate runs on a
        // 1,400-block wavelength and the audit samples a few hundred, so a run legitimately lands
        // inside one cold region or one warm one. Measured across five seeds the same constant gave
        // between 4% and 38%, and every band tight enough to be worth having failed a seed that was
        // simply cold. What does hold on every seed is the property that actually matters: snow
        // sits above grass. A snow line driven by climate alone with no altitude term fails it flat,
        // and that was a real bug — one seed came out with no snow anywhere at all.
        var snowfall = SampleSurface(
            world, minBlock, maxBlock, ids.Snow.Value, ids.Grass.Value, registry.BuildOpacityTable());
        var snowPct = snowfall.Total > 0 ? snowfall.Snow * 100.0 / snowfall.Total : 0;
        var lift = snowfall.MeanSnowY - snowfall.MeanGrassY;

        Check(
            "snow lies high and cold",
            // The floor is barely a floor on purpose: "snow exists at all" is already owned by the
            // material census above, and a warm seed whose only snow is on its highest peaks is
            // right rather than broken — seed 'stonebreak' comes out at 0.8% and should.
            //
            // Both thresholds are measured, not chosen. Taking the altitude term out of the snow
            // line and reading all five seeds gives lifts of -4.6, 0.8 and 4.8 where snow survives
            // at all, against 3.2 to 19.5 with it in; and 0%, 0%, 26% and 23% of high ground white,
            // against 73% to 100%. Two blocks and sixty percent both sit between the two
            // populations with room on either side. Four blocks did not: it failed seed
            // 'saltmarsh', which is simply low and warm, for being correct.
            //
            // Seed 'tidefall' passes both clauses with the altitude term removed and is a blind
            // spot for this check — its cold region happens to sit on its high ground already. The
            // other four are what give this teeth.
            snowPct is > 0.1 and < 60.0
                && lift > 2.0
                && snowfall.HighSnowPct > 60.0
                && maxY[ids.Snow.Value] >= maxY[ids.Grass.Value],
            $"{snowPct:F1}% of open ground, mean y {snowfall.MeanSnowY:F1} against grass at "
            + $"{snowfall.MeanGrassY:F1} (want at least 2 higher), {snowfall.HighSnowPct:F0}% of ground "
            + $"above y {snowfall.HighFrom} is snow (want over 60), tops out at y {maxY[ids.Snow.Value]}");

        var clayPct = counts[ids.Clay.Value] * 100.0 / Math.Max(1.0, counts[ids.Sand.Value]);
        Check(
            "clay sits in the shallows",
            clayPct is > 1.0 and < 15.0 && maxY[ids.Clay.Value] <= TerrainGenerator.SeaLevel + 2,
            $"{clayPct:F1}% of shore (want 1-15), highest at y {maxY[ids.Clay.Value]}");

        Check(
            "sandstone lies under the sand",
            counts[ids.Sandstone.Value] > 0 && maxY[ids.Sandstone.Value] < maxY[ids.Sand.Value],
            $"{counts[ids.Sandstone.Value]:N0} blocks, top at y {maxY[ids.Sandstone.Value]} under sand at y {maxY[ids.Sand.Value]}");

        // Tree size gets a band like everything else calibrated. "Trees planted" above only proves
        // logs exist; a forest of four-block stumps passes it without complaint, and the difference
        // between a wood and a shrubbery is invisible in a block census. Reported after a player
        // said the trees looked short — which they were, at the very bottom of the range.
        Check("trees are tree-sized", trees.MeanTrunk is > 5.5 and < 8.5,
            $"mean trunk {trees.MeanTrunk:F1} logs over {trees.Count:N0} trees (want 5.5-8.5)");
        Check("tree heights vary", trees.MaxTrunk - trees.MinTrunk >= 3,
            $"{trees.MinTrunk}..{trees.MaxTrunk} logs");

        // Canopies have to join up. One crown is somewhere near sixty leaves, so a mean cluster
        // still in that neighbourhood means every tree is standing on its own no matter how many
        // of them there are — the orchard look. Banded above as well: a mean in the tens of
        // thousands would mean the whole world is under one unbroken ceiling of foliage.
        // Tufts against the ground they grow on rather than against the whole volume, which is the
        // comparison a player makes standing in a field. Banded both ways: a floor alone passes a
        // world with one tuft in it, and a ceiling alone passes a lawn with no bare ground at all.
        var meadowPct = counts[ids.Meadowgrass.Value] * 100.0 / Math.Max(counts[ids.Grass.Value], 1);
        Check("meadowgrass rate in band", meadowPct is > 8.0 and < 45.0,
            $"{meadowPct:F1}% of grass columns (want 8-45)");

        long flowers = 0;
        foreach (var id in ids.Flowers) flowers += counts[id.Value];

        long cover = 0;
        foreach (var id in ids.GroundCover) cover += counts[id.Value];

        // Flowers are the exception in a meadow, not the meadow. A share rather than a count,
        // because the count follows how much open ground the sampled window happened to contain and
        // the share does not — and banded at both ends, since a field that is more bloom than grass
        // is as wrong as one with none.
        var flowerPct = flowers * 100.0 / Math.Max(cover, 1);
        Check(
            "meadows carry flowers",
            flowers > 0 && flowerPct is > 2.0 and < 25.0
                && counts[ids.Seaflax.Value] > 0 && counts[ids.Marshlily.Value] > 0,
            $"{flowers:N0} blooms, {flowerPct:F1}% of ground cover (want 2-25), "
            + $"{counts[ids.Seaflax.Value]:N0} seaflax and {counts[ids.Marshlily.Value]:N0} marshlily");

        // The dusting has to be a fringe on the snowfield rather than a second one. Measured as its
        // share of all the white ground: with no band the edge either vanishes (a snow line drawn
        // as a line, which is what this replaced) or swallows the meadows below it, and neither
        // shows up in any count of snow itself.
        //
        // The ceiling is set where it can be. Correct worlds read 12.4 to 23.9 across the five test
        // seeds; widening the band five times over gives 43.0 and is caught. Doubling it gives 31.4
        // on one seed and 22.6 on another — below what a third seed reads when it is right — so a
        // doubling is not separable by any single number, and this check does not claim to catch
        // one. It catches the band being turned off, or opened wide enough to be a second biome.
        var whiteGround = counts[ids.Snow.Value] + counts[ids.SnowLayer.Value];
        var dustPct = counts[ids.SnowLayer.Value] * 100.0 / Math.Max(whiteGround, 1);
        Check(
            "snow fades at its edge",
            dustPct is > 8.0 and < 33.0 && maxY[ids.SnowLayer.Value] < maxY[ids.Snow.Value],
            $"{dustPct:F1}% of white ground is a dusting (want 8-33), "
            + $"topping out at y {maxY[ids.SnowLayer.Value]} under snow at y {maxY[ids.Snow.Value]}");

        Check("canopies merge", canopy.MeanSize is > 90 and < 40_000,
            $"mean leaf mass {canopy.MeanSize:F0} blocks across {canopy.Clusters:N0} (want 90-40,000)");

        // Lighting gates. Every one of these is a band or a two-sided comparison, because the two
        // ways lighting fails are opposite and each looks fine to the other's check: light that
        // never propagates leaves a black world, light that ignores blocks leaves a world with no
        // shadows at all. A floor on brightness passes the second; a ceiling passes the first.
        Check("sun reaches open air", light.SkyFullPct is > 30.0 and < 94.0,
            $"{light.SkyFullPct:F1}% of open air at full sun (want 30-94)");
        Check("shadow exists", light.SkyDarkPct is > 3.0 and < 65.0,
            $"{light.SkyDarkPct:F1}% of open air in full shadow (want 3-65)");
        Check("rock stays dark", light.LitSolids == 0,
            light.LitSolids == 0 ? "no light inside opaque non-emitters" : $"{light.LitSolids:N0} lit solid cells");
        Check("canopy casts shade", light.MaxSkyUnderLeaves < LightValue.Max,
            $"brightest cell under a leaf is {light.MaxSkyUnderLeaves} (must be under {LightValue.Max})");

        // The brightest open cell next to a full-strength emitter is one level down from it, since
        // the emitter itself is solid and the first step out costs a level. Banded rather than
        // pinned to 14 so a dimmer source, or one that is not a full cube, does not read as a fault.
        Check("emitters light the dark", light.BlockLitCells > 0 && light.BlockPeak is >= 12 and <= LightValue.Max,
            $"{light.BlockLitCells:N0} cells reached, peak {light.BlockPeak} (want 12-{LightValue.Max})");
        Check("block light is coloured", light.ColouredCells > 0,
            $"{light.ColouredCells:N0} cells where the channels differ");

        var emberPct = Rate(ids.Emberstone);
        Check("emberstone rate in band", emberPct is > 0.05 and < 0.40,
            $"{emberPct:F3}% of rock (want 0.05-0.40)");

        var lightingConverges = LightingIsOrderIndependent(seed, registry, ids, oceanCoverage, out var lightDetail);
        Check("light ignores load order", lightingConverges, lightDetail);

        // Climate colour has to actually vary, and the two colormaps have to disagree. A tint path
        // wired to a constant field is indistinguishable from no tint path at all, and it would
        // pass every check that only asks whether tinting happened.
        var tintShades = new HashSet<int>();
        var tintDiffers = 0;
        for (var wz = -6000; wz <= 6000; wz += 250)
        for (var wx = -6000; wx <= 6000; wx += 250)
        {
            var grassTint = tinter.Quantised(TintSource.Grass, wx, 70, wz);
            var foliageTint = tinter.Quantised(TintSource.Foliage, wx, 70, wz);
            tintShades.Add(grassTint);
            if (grassTint != foliageTint) tintDiffers++;
        }

        Check("climate colours the world", tintShades.Count is > 8 and < 2000,
            $"{tintShades.Count} distinct grass shades over 12,000 blocks (want 8-2,000)");
        Check("foliage differs from grass", tintDiffers > 2000,
            $"{tintDiffers:N0} of 2,401 samples where the two colormaps disagree");

        var textureFaults = TextureSelfTest();
        Check("block textures are drawn", textureFaults.Count == 0,
            textureFaults.Count == 0
                ? $"{BlockTextureSet.Layers.Length} layers, all painted, cutouts have holes"
                : $"{textureFaults.Count} faults: {string.Join("; ", textureFaults)}");

        var rayFaults = RaycastSelfTest(registry, ids);
        Check("raycast hits the right face", rayFaults.Count == 0,
            rayFaults.Count == 0
                ? "6 axis directions, a diagonal, a miss, and a ray starting inside a block"
                : $"{rayFaults.Count} faults: {string.Join("; ", rayFaults)}");

        var physicsFaults = PhysicsSelfTest(seed, registry, ids, oceanCoverage);
        Check("player physics holds", physicsFaults.Count == 0,
            physicsFaults.Count == 0
                ? "falls, lands, walks, jumps, is stopped by walls, does not sneak off ledges"
                : $"{physicsFaults.Count} faults: {string.Join("; ", physicsFaults)}");

        // The model is drawn from a table of measurements and a UV net worked out on paper. None of
        // it throws when it is wrong: a reversed winding is an invisible limb, a transposed patch is
        // an elbow wearing a kneecap, and both draw perfectly happily.
        var modelWinding = PlayerModel.ValidateWinding();
        Check("model winds outward", modelWinding.Count == 0,
            modelWinding.Count == 0
                ? "12 boxes x 6 faces, classic and slim, modern and legacy"
                : $"{modelWinding.Count} faults: {modelWinding[0]}");

        var netFaults = PlayerModel.ValidateNet();
        Check("skin net is well formed", netFaults.Count == 0,
            netFaults.Count == 0
                ? "every patch on the sheet, right size, none reused"
                : $"{netFaults.Count} faults: {netFaults[0]}");

        var mirrorFaults = PlayerModel.ValidateMirror();
        Check("left limbs mirror", mirrorFaults.Count == 0,
            mirrorFaults.Count == 0
                ? "u runs opposite ways on the two arms, both layouts"
                : $"{mirrorFaults.Count} faults: {mirrorFaults[0]}");

        var (modelTall, modelWide, jointFaults) = MeasureModel();
        Check(
            "model is one piece",
            MathF.Abs(modelTall - PlayerBody.Height) < 1e-3f && modelWide is > 0.6f and < 1.2f && jointFaults.Count == 0,
            jointFaults.Count > 0
                ? $"{jointFaults.Count} faults: {jointFaults[0]}"
                : $"{modelTall:F3} blocks tall (body is {PlayerBody.Height:F2}), {modelWide:F2} across the shoulders, joints meet");

        var defaultSkin = PlayerSkin.Paint(ArmStyle.Classic);
        var skinFaults = PlayerSkin.Validate(defaultSkin);
        Check("default skin covers it", skinFaults.Count == 0,
            skinFaults.Count == 0
                ? $"{defaultSkin.Size}x{defaultSkin.Size}, all 36 base patches painted"
                : $"{skinFaults.Count} faults: {skinFaults[0]}");

        var animationFaults = AnimationSelfTest();
        Check("swing and stride hold", animationFaults.Count == 0,
            animationFaults.Count == 0
                ? "swing completes, repeats while held, returns to rest; stride scales with speed"
                : $"{animationFaults.Count} faults: {string.Join("; ", animationFaults)}");

        var boomFaults = CameraBoom.SelfTest(registry, ids.Stone);
        Check("camera boom clears walls", boomFaults.Count == 0,
            boomFaults.Count == 0
                ? "full length in the open, gives way to a wall, never ends up inside one"
                : $"{boomFaults.Count} faults: {string.Join("; ", boomFaults)}");

        var miningFaults = MiningSelfTest(registry, ids);
        Check("mining takes the right work", miningFaults.Count == 0,
            miningFaults.Count == 0
                ? $"soft ground {MiningRules.SecondsToBreak(registry[ids.Dirt]):F2}s, "
                  + $"stone {MiningRules.SecondsToBreak(registry[ids.Stone]):F2}s, "
                  + $"ore {MiningRules.SecondsToBreak(registry[ids.IronOre]):F2}s, bedrock never"
                : $"{miningFaults.Count} faults: {string.Join("; ", miningFaults)}");

        var crackFaults = CrackSelfTest();
        Check("cracks deepen without healing", crackFaults.Count == 0,
            crackFaults.Count == 0
                ? $"{MiningRules.Stages} stages, each a superset of the last"
                : $"{crackFaults.Count} faults: {string.Join("; ", crackFaults)}");

        var relightMatches = RelightMatchesFullPass(
            seed, registry, ids, oceanCoverage, out var relightDetail, out var worstRelightMs);
        Check("edits relight exactly", relightMatches, relightDetail);

        // The ceiling is on the light thread, not on a frame: nothing here blocks drawing. What it
        // bounds is how long after a swing the world looks right, and a tenth of a frame is far
        // below noticing.
        Check("relight is interactive", worstRelightMs < 2.0, $"worst single edit {worstRelightMs:F2} ms (want under 2)");

        return new Result(sb.ToString(), passed);
    }

    /// <summary>
    /// Lights the same blocks twice — once as a whole region, once column by column in a scattered
    /// order — and compares every cell.
    /// </summary>
    /// <remarks>
    /// This is the question streaming asks of lighting, and it is not the same question meshing
    /// asks. A mesh is built from blocks that are already final; light is built from light, so a
    /// column lit before its neighbour existed has to end up identical to one lit after. The failure
    /// it guards against is a seam of darkness frozen along a chunk boundary the player walked
    /// across in the wrong direction — which never reproduces from a fresh load, because a fresh
    /// load lights everything at once.
    /// <para>The column order is deliberately scattered rather than reversed. A reversed sweep is
    /// still a sweep, and a bug that only needs "some neighbour already lit" survives it.</para>
    /// </remarks>
    private static bool LightingIsOrderIndependent(
        WorldSeed seed, BlockRegistry registry, StarterBlocks.Ids ids, float oceanCoverage, out string detail)
    {
        const int radius = 2;   // 5x5 columns is enough for interior cells to have full neighbours

        var order = new List<(int X, int Z)>();
        for (var cz = -radius; cz <= radius; cz++)
        for (var cx = -radius; cx <= radius; cx++)
            order.Add((cx, cz));

        var batch = BuildRegion(seed, registry, ids, oceanCoverage, radius);
        new LightEngine(registry).LightAll(batch);

        var incremental = BuildRegion(seed, registry, ids, oceanCoverage, radius);
        var engine = new LightEngine(registry);

        // Every third column, wrapping — hits the whole set without ever lighting two neighbours
        // back to back.
        for (var i = 0; i < order.Count; i++)
        {
            var (cx, cz) = order[i * 3 % order.Count];
            engine.LightColumn(incremental, cx, cz);
        }

        var chunksTall = TerrainGenerator.WorldHeight / Chunk.Size;
        var compared = 0;

        for (var cz = -radius; cz <= radius; cz++)
        for (var cx = -radius; cx <= radius; cx++)
        for (var cy = 0; cy < chunksTall; cy++)
        {
            var pos = new ChunkPos(cx, cy, cz);
            if (!batch.TryGetChunk(pos, out var a) || !incremental.TryGetChunk(pos, out var b)) continue;

            var la = a.RawLight;
            var lb = b.RawLight;
            for (var i = 0; i < la.Length; i++)
            {
                compared++;
                if (la[i] == lb[i]) continue;

                var x = i & Chunk.SizeMask;
                var z = (i >> Chunk.SizeLog2) & Chunk.SizeMask;
                var y = i >> (Chunk.SizeLog2 * 2);
                detail = $"chunk {pos} local ({x},{y},{z}): batch sky {LightValue.Sky(la[i])} "
                       + $"rgb {LightValue.Red(la[i])},{LightValue.Green(la[i])},{LightValue.Blue(la[i])} vs "
                       + $"streamed sky {LightValue.Sky(lb[i])} "
                       + $"rgb {LightValue.Red(lb[i])},{LightValue.Green(lb[i])},{LightValue.Blue(lb[i])}";
                return false;
            }
        }

        detail = $"{compared:N0} cells identical whole-region and column by column";
        return true;
    }

    /// <summary>
    /// Makes a series of block edits, relights each one incrementally, and compares the result with
    /// lighting the whole modified region from scratch.
    /// </summary>
    /// <remarks>
    /// <para>The edits are chosen to exercise both directions, because they are different
    /// algorithms and only one of them is easy. Digging a shaft down from the surface is light
    /// <em>arriving</em>: a flood into cells that were dark. Sealing that shaft, or walling off a
    /// glowing block, is light <em>leaving</em> — and a flood cannot do that at all, since every
    /// cell around the hole still holds a value that would happily fill it straight back in.</para>
    /// <para>Comparing against a from-scratch pass rather than against expected numbers is what
    /// makes this worth having. Nobody can write down what sunlight in a cave should be; but the
    /// two ways of computing it have to agree, and when they do not, the from-scratch answer is
    /// the one to trust.</para>
    /// </remarks>
    private static bool RelightMatchesFullPass(
        WorldSeed seed, BlockRegistry registry, StarterBlocks.Ids ids, float oceanCoverage,
        out string detail, out double worstMs)
    {
        const int radius = 1;

        var generator = new TerrainGenerator(seed, ids, oceanCoverage);
        var world = BuildRegion(seed, registry, ids, oceanCoverage, radius);
        var engine = new LightEngine(registry);
        engine.LightAll(world);

        var surface = generator.SurfaceHeight(4, 4);

        // Dig a shaft, seal it again, drop a light down a hole, then bury it.
        var edits = new List<(int X, int Y, int Z, BlockId Block, string What)>();
        for (var d = 0; d < 8; d++) edits.Add((4, surface - d, 4, BlockId.Air, $"dig {d + 1} down"));
        for (var d = 7; d >= 0; d--) edits.Add((4, surface - d, 4, ids.Stone, $"backfill {8 - d}"));
        edits.Add((-6, surface - 4, 6, BlockId.Air, "open a pocket"));
        edits.Add((-6, surface - 4, 6, ids.Emberstone, "light the pocket"));
        edits.Add((-6, surface - 3, 6, ids.Stone, "cap the pocket"));
        edits.Add((-6, surface - 4, 6, ids.Stone, "bury the light"));

        worstMs = 0;
        var worstWhat = "none";
        var worstCells = 0L;
        var worstBreakdown = "nothing measured";
        long totalCells = 0;

        // Timing runs on its own world, and only after that world has been edited once already.
        //
        // Both halves of that were bought with a wrong answer. Timing used to share a loop with the
        // verification, which generates and lights a whole region per edit; the watch was stopped
        // first, which looked like enough. And a warm-up pass existed, but it ran on a *different*
        // world with a *different* engine, so it warmed the JIT and nothing else.
        //
        // What was actually being measured was warm-up. The proof is direct and does not depend on
        // knowing which warm-up: the identical edit, on the same world and engine, visiting the same
        // 458 cells with the same 2,736 neighbour tests and 800 chunk lookups, costs 2.48 ms the
        // first time and 0.074 ms the second, with no collection inside either window. Thirty times
        // apart for byte-identical work rules out the algorithm, which is what mattered — whether
        // the remaining cost is tiered JIT promoting the flood to optimised code or first touch of
        // freshly allocated chunk memory was not worth separating, since the fix is the same and
        // neither is a number a player can ever meet. In a running game chunks are long-lived and a
        // swing lands on the second case.
        {
            var timed = BuildRegion(seed, registry, ids, oceanCoverage, radius);
            var timedEngine = new LightEngine(registry);
            timedEngine.LightAll(timed);

            // Run the whole sequence, put every block back the way it was, and relight. What
            // survives is a world whose pages are committed and whose light is where it started.
            var before = new List<BlockId>(edits.Count);
            foreach (var (x, y, z, block, _) in edits)
            {
                before.Add(timed.GetBlock(x, y, z));
                timed.SetBlock(x, y, z, block);
                timedEngine.UpdateBlock(timed, x, y, z);
            }

            for (var i = edits.Count - 1; i >= 0; i--)
            {
                var (x, y, z, _, _) = edits[i];
                timed.SetBlock(x, y, z, before[i]);
                timedEngine.UpdateBlock(timed, x, y, z);
            }

            var watch = new Stopwatch();

            foreach (var (x, y, z, block, what) in edits)
            {
                timed.SetBlock(x, y, z, block);

                watch.Restart();
                timedEngine.UpdateBlock(timed, x, y, z);
                watch.Stop();

                totalCells += timedEngine.LastCellsVisited;

                var ms = watch.Elapsed.TotalMilliseconds;
                if (ms <= worstMs) continue;

                worstMs = ms;
                worstWhat = what;
                worstCells = timedEngine.LastCellsVisited;
                worstBreakdown = $"{timedEngine.LastUnfillPasses} unfill passes taking "
                               + $"{timedEngine.LastUnfillMs:F2} ms, "
                               + $"{timedEngine.LastRemovalCells:N0} torn out in {timedEngine.LastRemovalMs:F2} ms, "
                               + $"{timedEngine.LastFillCells:N0} refilled in {timedEngine.LastFillMs:F2} ms, "
                               + $"{timedEngine.LastNeighbourTests:N0} neighbour tests, "
                               + $"{timedEngine.LastChunkMisses:N0} chunk lookups";
            }
        }

        foreach (var (x, y, z, block, what) in edits)
        {
            world.SetBlock(x, y, z, block);
            engine.UpdateBlock(world, x, y, z);

            if (Matches(world, seed, registry, ids, oceanCoverage, radius, edits, what, out detail)) continue;
            return false;
        }

        // Naming the worst edit and its cell count, not just its time: "slow" and "large" are
        // different problems and the fix for one does nothing for the other.
        detail = $"{edits.Count} edits over {totalCells:N0} cells, worst '{worstWhat}' "
               + $"at {worstMs:F2} ms over {worstCells:N0} cells ({worstBreakdown})";
        return true;

        static bool Matches(
            VoxelWorld edited, WorldSeed seed, BlockRegistry registry, StarterBlocks.Ids ids,
            float oceanCoverage, int radius, List<(int X, int Y, int Z, BlockId Block, string What)> _,
            string what, out string detail)
        {
            // Reference: the same blocks, lit from nothing.
            var reference = new VoxelWorld(registry);
            foreach (var chunk in edited.Chunks)
            {
                var copy = reference.GetOrCreateChunk(chunk.Position);
                Array.Copy(chunk.Raw, copy.Raw, chunk.Raw.Length);
                copy.RecountSolid();
            }

            new LightEngine(registry).LightAll(reference);

            foreach (var chunk in edited.Chunks)
            {
                if (!reference.TryGetChunk(chunk.Position, out var other)) continue;

                var a = chunk.RawLight;
                var b = other.RawLight;
                for (var i = 0; i < a.Length; i++)
                {
                    if (a[i] == b[i]) continue;

                    var x = i & Chunk.SizeMask;
                    var y = i >> (Chunk.SizeLog2 * 2);
                    var z = (i >> Chunk.SizeLog2) & Chunk.SizeMask;
                    detail = $"after '{what}', chunk {chunk.Position} local ({x},{y},{z}): "
                           + $"relit sky {LightValue.Sky(a[i])} rgb {LightValue.Red(a[i])},"
                           + $"{LightValue.Green(a[i])},{LightValue.Blue(a[i])} vs "
                           + $"full sky {LightValue.Sky(b[i])} rgb {LightValue.Red(b[i])},"
                           + $"{LightValue.Green(b[i])},{LightValue.Blue(b[i])}";
                    return false;
                }
            }

            detail = string.Empty;
            return true;
        }
    }

    private static VoxelWorld BuildRegion(
        WorldSeed seed, BlockRegistry registry, StarterBlocks.Ids ids, float oceanCoverage, int radius)
    {
        var world = new VoxelWorld(registry);
        var generator = new TerrainGenerator(seed, ids, oceanCoverage);
        var chunksTall = TerrainGenerator.WorldHeight / Chunk.Size;

        for (var cz = -radius; cz <= radius; cz++)
        for (var cx = -radius; cx <= radius; cx++)
        for (var cy = 0; cy < chunksTall; cy++)
        {
            var chunk = world.GetOrCreateChunk(new ChunkPos(cx, cy, cz));
            generator.GenerateChunk(chunk);
            generator.DecorateChunk(chunk);
        }

        return world;
    }

    /// <summary>
    /// Packs a vertex with a distinct value in every field and reads all of them back.
    /// </summary>
    /// <remarks>
    /// <para>The whole vertex is bit fields sharing three words, and the shader unpacks them by
    /// shift and mask on the other side of a language boundary where nothing checks anything. Two
    /// fields that overlap by a bit do not fail to compile, do not fail to draw, and do not fail
    /// any geometry check — they produce a world where occasionally a face is the wrong texture, or
    /// a corner is the wrong brightness, at whatever coordinate happens to set the shared bit.</para>
    /// <para>Every field carries a different value on purpose, and each one is near the top of its
    /// range. A test that packs zeros everywhere passes whatever the layout is.</para>
    /// </remarks>
    private static List<string> VertexPackingSelfTest()
    {
        var faults = new List<string>();

        static uint Field(uint word, int shift, int bits) => (word >> shift) & ((1u << bits) - 1);

        void Expect(string what, long actual, long want)
        {
            if (actual != want) faults.Add($"{what} read back {actual}, expected {want}");
        }

        // A merged cube face: whole-block coordinates, coordinates derived in the shader.
        var cube = ChunkVertex.Cube(31, 5, 17, Faces.PosZ, 2, 3, ChunkVertex.MaxLayer, 0xBEEF, 63);
        Expect("cube x", Field(cube.Packed0, 0, 12), 31 * ChunkVertex.PositionScale + ChunkVertex.PositionBias);
        Expect("cube y", Field(cube.Packed0, 12, 12), 5 * ChunkVertex.PositionScale + ChunkVertex.PositionBias);
        Expect("cube z", Field(cube.Packed1, 0, 12), 17 * ChunkVertex.PositionScale + ChunkVertex.PositionBias);
        Expect("cube face", Field(cube.Packed0, 24, 3), Faces.PosZ);
        Expect("cube occlusion", Field(cube.Packed0, 27, 2), 2);
        Expect("cube pass", Field(cube.Packed0, 29, 2), 3);
        Expect("cube layer", Field(cube.Packed1, 12, 12), ChunkVertex.MaxLayer);
        Expect("cube tint", Field(cube.Packed1, 24, 6), 63);
        Expect("cube uv mode", Field(cube.Packed1, 30, 1), 0);
        Expect("cube light", Field(cube.Packed2, 0, 16), 0xBEEF);

        // A model quad: fractional coordinates, texture coordinates carried along.
        var model = ChunkVertex.Model(0.05f, 32f, -0.5f, ChunkVertex.UnshadedFace, 3, 1, 0x1234, 7, 0.25f, 1f);
        Expect("model x", Field(model.Packed0, 0, 12), ChunkVertex.Quantise(0.05f));
        Expect("model y", Field(model.Packed0, 12, 12), ChunkVertex.Quantise(32f));
        Expect("model z", Field(model.Packed1, 0, 12), ChunkVertex.Quantise(-0.5f));
        Expect("model face", Field(model.Packed0, 24, 3), ChunkVertex.UnshadedFace);
        Expect("model occlusion", Field(model.Packed0, 27, 2), 3);
        Expect("model pass", Field(model.Packed0, 29, 2), 0);
        Expect("model layer", Field(model.Packed1, 12, 12), 1);
        Expect("model tint", Field(model.Packed1, 24, 6), 7);
        Expect("model uv mode", Field(model.Packed1, 30, 1), 1);
        Expect("model u", Field(model.Packed2, 16, 6), (long)(0.25f * ChunkVertex.UvScale));
        Expect("model v", Field(model.Packed2, 22, 6), ChunkVertex.UvScale);
        Expect("model light", Field(model.Packed2, 0, 16), 0x1234);

        // The extremes the format promises: a block below the chunk, and a model reaching a block
        // past its far corner.
        Expect("a block below the chunk", ChunkVertex.Quantise(-1f), 0);
        Expect("a block past the far corner", ChunkVertex.Quantise(Chunk.Size + 1),
            (Chunk.Size + 1) * ChunkVertex.PositionScale + ChunkVertex.PositionBias);

        if (ChunkVertex.Quantise(Chunk.Size + 1) > 0xFFF)
            faults.Add("the position field cannot address a model reaching past the chunk");

        return faults;
    }

    /// <summary>
    /// Drops the body from a ladder of heights, holds its head under water, and reads the bar.
    /// </summary>
    /// <remarks>
    /// <para>Every number here is one a player finds out by dying to it. How far a fall has to be
    /// before it costs anything decides whether ordinary climbing is punished; how long a breath
    /// lasts decides whether a lake is a shortcut or a trap. Neither can be tuned by eye and both
    /// are wrong quietly.</para>
    /// <para>The ladder matters more than any one rung. A single "a ten-block fall hurts" passes a
    /// build where every fall costs the same, and a build where the free height is zero, and one
    /// where damage is a constant. Walking heights either side of the free limit and checking the
    /// cost rises with the height is what tells those apart.</para>
    /// </remarks>
    private static List<string> VitalsSelfTest(BlockRegistry registry, StarterBlocks.Ids ids)
    {
        var faults = new List<string>();
        const float Step = 1f / 60f;

        // Ground up to y=10, so the surface is y=11.
        var world = new VoxelWorld(registry);
        for (var z = -3; z <= 3; z++)
        for (var x = -3; x <= 3; x++)
        for (var y = 0; y <= 10; y++)
            world.SetBlock(x, y, z, ids.Stone);

        (float Height, int Want)[] ladder =
        [
            (1f, 0),
            (3f, 0),
            (5f, 2),
            (9f, 6),
            (14f, 11),
        ];

        var lastCost = -1;
        foreach (var (height, want) in ladder)
        {
            var body = new PlayerBody(registry);
            var vitals = new PlayerVitals(registry);
            body.Teleport(new Vector3(0.5f, 11f + height, 0.5f));

            for (var i = 0; i < 600 && (!body.OnGround || i < 4); i++)
            {
                body.Step(world, Step, Vector3.Zero, false, false, false);
                vitals.Update(world, body, Step);
            }

            var cost = PlayerVitals.MaxHealth - vitals.Health;
            if (Math.Abs(cost - want) > 1)
                faults.Add($"a {height:F0} block fall cost {cost} half-hearts, expected about {want}");
            if (cost < lastCost) faults.Add($"a {height:F0} block fall cost less than a shorter one");
            lastCost = cost;
        }

        // Deep enough that the head is under and the feet are on the floor.
        var pool = new VoxelWorld(registry);
        for (var z = -3; z <= 3; z++)
        for (var x = -3; x <= 3; x++)
        {
            for (var y = 0; y <= 10; y++) pool.SetBlock(x, y, z, ids.Stone);
            for (var y = 11; y <= 16; y++) pool.SetBlock(x, y, z, ids.Water);
        }

        var swimmer = new PlayerBody(registry);
        var lungs = new PlayerVitals(registry);
        swimmer.Teleport(new Vector3(0.5f, 11f, 0.5f));

        // Ten seconds: long enough for the breath to run out and the drowning to start, short
        // enough to surface alive. A dead body has no vitals to advance, so drowning one outright
        // would leave nothing to measure the recovery with.
        var toEmpty = -1;
        var firstHurt = -1;
        for (var i = 0; i < 60 * 10; i++)
        {
            swimmer.Step(pool, Step, Vector3.Zero, false, false, false);
            lungs.Update(pool, swimmer, Step);

            if (toEmpty < 0 && lungs.Breath == 0) toEmpty = i;
            if (firstHurt < 0 && lungs.Health < PlayerVitals.MaxHealth) firstHurt = i;
        }

        if (!lungs.Submerged) faults.Add("a body standing in six blocks of water is not submerged");
        if (toEmpty < 60 * 3 || toEmpty > 60 * 8)
            faults.Add($"breath ran out after {toEmpty / 60f:F1}s, wanted 3 to 8");
        if (firstHurt <= toEmpty) faults.Add("drowning started before the breath was gone");
        if (lungs.Health >= PlayerVitals.MaxHealth) faults.Add("ten seconds under water cost nothing");
        if (!lungs.Alive) faults.Add("ten seconds under water was fatal, which is far too fast");

        // And surfacing gives it back faster than it went.
        var refilled = -1;
        for (var i = 0; i < 60 * 20; i++)
        {
            swimmer.Teleport(new Vector3(0.5f, 40f, 0.5f));
            lungs.Update(world, swimmer, Step);
            if (lungs.Breath < PlayerVitals.MaxBreath) continue;
            refilled = i;
            break;
        }

        if (refilled < 0) faults.Add("breath never came back out of the water");
        else if (refilled >= toEmpty) faults.Add($"breath took {refilled / 60f:F1}s to return, longer than it lasted");

        // Rest heals, and not before it should.
        var rester = new PlayerBody(registry);
        var healing = new PlayerVitals(registry);
        rester.Teleport(new Vector3(0.5f, 11f, 0.5f));
        healing.Hurt(6);

        var atStart = healing.Health;
        for (var i = 0; i < 60 * 3; i++) healing.Update(world, rester, Step);
        if (healing.Health != atStart) faults.Add("health started coming back within three seconds of being hurt");

        for (var i = 0; i < 60 * 30; i++) healing.Update(world, rester, Step);
        if (healing.Health != PlayerVitals.MaxHealth)
            faults.Add($"half a minute of rest left health at {healing.Health} of {PlayerVitals.MaxHealth}");

        return faults;
    }

    /// <summary>
    /// Resolves every sound the block table names, without opening a speaker.
    /// </summary>
    /// <remarks>
    /// <para>A sound table pointing at a file nobody shipped is silent in exactly the way a working
    /// game is silent. Nothing on screen changes, nothing throws, and the only way anybody finds
    /// out is by noticing that grass has stopped making a noise — which is not something a person
    /// reliably notices at all. So the gate is here, where a release cannot get past it.</para>
    /// <para>Decoding is the point, not merely finding the file. A truncated WAV, a format this
    /// reader does not handle, or a file that decodes to digital silence all pass a
    /// does-it-exist test and all of them are the fault this is looking for.</para>
    /// </remarks>
    private static List<string> SoundSelfTest(BlockRegistry registry, out string detail)
    {
        var faults = new List<string>();
        var root = SoundLibrary.FindRoot();
        var library = new SoundLibrary(root);

        detail = $"{library.Count} clips in {Path.GetFileName(root)}";

        if (library.Count == 0)
        {
            faults.Add($"no sounds found under {root}");
            return faults;
        }

        var named = 0;
        var quietest = 1f;
        foreach (var name in MaterialSounds.AllNames())
        {
            named++;
            var clip = library.Load(name);
            if (clip is null)
            {
                faults.Add($"'{name}' is named by the block table and is not in {Path.GetFileName(root)}");
                continue;
            }

            var peak = clip.Peak;
            quietest = MathF.Min(quietest, peak);
            if (peak < 0.02f) faults.Add($"'{name}' decodes to near silence (peak {peak:F3})");
            if (clip.Seconds > 8f) faults.Add($"'{name}' runs {clip.Seconds:F1}s, which is a loop not a one-shot");
        }

        foreach (var material in MaterialSounds.Materials)
        foreach (var which in Enum.GetValues<SoundEvent>())
        {
            if (MaterialSounds.For(material, which).Count > 0) continue;
            faults.Add($"{material} has nothing for {which}");
        }

        // And every registered block has to land on a material that is actually in the table.
        for (ushort id = 1; id < registry.Count; id++)
        {
            if (MaterialSounds.For(registry[id].Sounds, SoundEvent.Break).Count > 0) continue;
            faults.Add($"block '{registry[id].Name}' resolves to no break sound");
        }

        foreach (var fault in library.Faults) faults.Add(fault);

        detail = $"{named} clips over {registry.Count - 1} blocks, quietest peak {quietest:F2}";
        return faults;
    }

    /// <summary>
    /// Bursts a block over a headless floor and watches where the debris goes.
    /// </summary>
    /// <remarks>
    /// <para>What a particle system gets wrong is never visible in one screenshot. A burst that
    /// falls through the floor looks like a burst for the first two frames; a pool that leaks looks
    /// perfect until an hour into play; a spawn that allocates never looks like anything at all and
    /// turns up as a stutter somebody blames on the streamer. All three are numbers.</para>
    /// <para>The allocation measurement is taken after a warm-up on purpose. The first call through
    /// any path pulls in whatever the runtime needed to get there, and counting that would make the
    /// check a test of the just-in-time compiler rather than of the loop.</para>
    /// </remarks>
    private static List<string> ParticleSelfTest(BlockRegistry registry, StarterBlocks.Ids ids)
    {
        var faults = new List<string>();

        // Solid ground up to y=10, so the surface a chip can rest on is y=11.
        const float Floor = 11f;
        var world = new VoxelWorld(registry);
        for (var z = -4; z <= 4; z++)
        for (var x = -4; x <= 4; x++)
        for (var y = 0; y <= 10; y++)
            world.SetBlock(x, y, z, ids.Stone);

        var stone = registry[ids.Stone];
        var particles = new ParticleSystem(registry, 0x51ED2701);

        particles.Burst(stone, 0, 12, 0);
        var spawned = particles.Count;
        if (spawned is < 16 or > 48) faults.Add($"a burst spawned {spawned} particles, wanted 16 to 48");

        // Four seconds is comfortably past the longest life a burst hands out.
        var lowest = float.MaxValue;
        var crops = new HashSet<int>();
        for (var step = 0; step < 240; step++)
        {
            particles.Update(world, 1f / 60f);
            foreach (var p in particles.Live)
            {
                lowest = MathF.Min(lowest, p.Position.Y);
                crops.Add(p.CropX * ParticleSystem.CropsPerAxis + p.CropY);
                if (p.Layer != stone.Model.ParticleLayer)
                    faults.Add($"a chip of stone is wearing layer {p.Layer}, not {stone.Model.ParticleLayer}");
            }
        }

        if (particles.Count != 0) faults.Add($"{particles.Count} particles outlived their life");
        if (lowest < Floor - 0.001f) faults.Add($"a particle reached y {lowest:F2}, under the floor at {Floor:F0}");

        // Sixteen crops of the tile, and a burst big enough to want most of them. All one crop
        // means every chip of every block in the world is the same four pixels.
        var wide = new ParticleSystem(registry, 0x2F6E13A9);
        for (var i = 0; i < 30; i++) wide.Burst(stone, 0, 12, 0);
        var seen = new HashSet<int>();
        foreach (var p in wide.Live) seen.Add(p.CropX * ParticleSystem.CropsPerAxis + p.CropY);
        if (seen.Count < ParticleSystem.CropsPerAxis * ParticleSystem.CropsPerAxis)
            faults.Add($"{seen.Count} of {ParticleSystem.CropsPerAxis * ParticleSystem.CropsPerAxis} tile crops ever used");

        // Chips come off the face that was struck, moving away from it.
        var chips = new ParticleSystem(registry, 0x7A31C5D3);
        chips.Chip(stone, 0, 10, 0, Faces.PosY, 12);
        foreach (var p in chips.Live)
        {
            if (p.Position.Y < 11f) faults.Add($"a chip off the top face started at y {p.Position.Y:F2}, inside the block");
            if (p.Velocity.Y <= 0f) faults.Add($"a chip off the top face is heading down at {p.Velocity.Y:F2}");
        }

        // The pool refuses rather than grows.
        var flooded = new ParticleSystem(registry, 0x1234567);
        for (var i = 0; i < ParticleSystem.Capacity; i++) flooded.Burst(stone, 0, 12, 0, 4);
        if (flooded.Count != ParticleSystem.Capacity)
            faults.Add($"the pool holds {flooded.Count}, not the {ParticleSystem.Capacity} it claims");
        if (flooded.Refused == 0) faults.Add("the pool never refused a spawn, so it grew instead");

        // And the steady state allocates nothing.
        var steady = new ParticleSystem(registry, 0xABCDEF);
        steady.Burst(stone, 0, 12, 0);
        for (var step = 0; step < 60; step++) steady.Update(world, 1f / 60f);

        steady.Burst(stone, 0, 12, 0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var step = 0; step < 30; step++) steady.Update(world, 1f / 60f);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        if (allocated > 0) faults.Add($"updating allocated {allocated} bytes over 30 frames");

        return faults;
    }

    /// <summary>
    /// Walks the whole day a minute at a time and checks the sky against what a sky has to do.
    /// </summary>
    /// <remarks>
    /// <para>Everything about a sky is a judgement about colour, and a judgement about colour gets
    /// made by eye at one moment of one day. The hours nobody happened to look at are where a ramp
    /// runs backwards, a colour goes negative, or the sun sets in the east — none of which any
    /// other check here can see, because none of them changes a single block.</para>
    /// <para>The wrap gets its own test. Midnight is where the clock's arithmetic folds over, and a
    /// discontinuity there is a visible flash once every twenty minutes of play — the kind of fault
    /// that gets reported as "it flickered" and never reproduced.</para>
    /// </remarks>
    private static List<string> SkySelfTest()
    {
        var faults = new List<string>();

        // Where the sun has to be at the four corners of the day. East at dawn, overhead at noon,
        // west at dusk, under the world at midnight.
        (float Time, string Name, float X, float Y)[] corners =
        [
            (0.25f, "sunrise", 1f, 0f),
            (0.50f, "noon", 0f, 1f),
            (0.75f, "sunset", -1f, 0f),
            (0.00f, "midnight", 0f, -1f),
        ];

        foreach (var (time, name, wantX, wantY) in corners)
        {
            var sun = SkyClock.SunAt(time);
            if (MathF.Abs(sun.X - wantX) > 0.02f)
                faults.Add($"at {name} the sun is {sun.X:F2} east, expected {wantX:F0}");

            // The arc leans, so the vertical corners are near rather than exactly one.
            if (MathF.Abs(sun.Y) > 0.02f && wantY == 0f)
                faults.Add($"at {name} the sun is {sun.Y:F2} above the horizon, expected level");
            if (wantY != 0f && MathF.Sign(sun.Y) != MathF.Sign(wantY))
                faults.Add($"at {name} the sun is {sun.Y:F2} above the horizon, expected the other side");
            if (wantY != 0f && MathF.Abs(sun.Y) < 0.85f)
                faults.Add($"at {name} the sun only reaches {sun.Y:F2}, which is not overhead enough");
        }

        var noon = SkyClock.At(0.5f);
        var midnight = SkyClock.At(0f);

        if (Luminance(noon.SkyAmbient) < Luminance(midnight.SkyAmbient) * 3f)
            faults.Add($"noon ambient {Luminance(noon.SkyAmbient):F3} is not clear of midnight's {Luminance(midnight.SkyAmbient):F3}");
        if (Luminance(noon.SunColor) < 0.3f) faults.Add($"the noon sun gives {Luminance(noon.SunColor):F3}");
        if (Luminance(midnight.SunColor) > 0.02f) faults.Add($"the midnight sun still gives {Luminance(midnight.SunColor):F3}");
        if (noon.StarFade > 0.001f) faults.Add($"stars are {noon.StarFade:F2} visible at noon");
        if (midnight.StarFade < 0.95f) faults.Add($"stars are only {midnight.StarFade:F2} visible at midnight");

        // A minute at a time through the whole day. Nothing may be negative, the sun's direction
        // must stay a unit vector, and no step may jump — a ramp with a break in it is a flash.
        var previous = SkyClock.At(0f);
        var worstJump = 0f;
        var worstAt = 0f;

        for (var minute = 1; minute <= 1440; minute++)
        {
            var time = minute / 1440f;
            var state = SkyClock.At(time);

            if (MathF.Abs(state.SunDirection.Length() - 1f) > 1e-3f)
                faults.Add($"at {minute / 60}:{minute % 60:00} the sun's direction is {state.SunDirection.Length():F3} long");

            foreach (var (colour, what) in (( Vector3, string )[])
                [(state.Zenith, "zenith"), (state.Horizon, "horizon"), (state.SunColor, "sun"),
                 (state.SkyAmbient, "sky ambient"), (state.GroundAmbient, "ground ambient")])
            {
                if (colour.X >= 0f && colour.Y >= 0f && colour.Z >= 0f) continue;
                faults.Add($"at {minute / 60}:{minute % 60:00} the {what} is negative");
            }

            var jump = Vector3.Distance(state.Zenith, previous.Zenith)
                     + Vector3.Distance(state.Horizon, previous.Horizon)
                     + Vector3.Distance(state.SunDirection, previous.SunDirection);

            if (jump > worstJump) { worstJump = jump; worstAt = time; }
            previous = state;
        }

        // A minute of a twenty-minute day is a twentieth of the whole cycle's motion; anything
        // past a tenth of a unit in one minute is a step rather than a ramp. The loop above ends
        // on minute 1440, which is midnight again, so the wrap is inside the same measurement.
        if (worstJump > 0.10f)
            faults.Add($"the sky jumps {worstJump:F3} in one minute at {worstAt * 24f:F1}h");

        return faults;

        static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
    }

    /// <summary>
    /// Reads the cloud sheet's geometry back and asks what the bitmap says about each face.
    /// </summary>
    /// <remarks>
    /// The side test is the one worth having. Emitting a wall wherever cloud meets sky is one
    /// expression, and checking it by writing that expression again proves only that it matches
    /// itself. Taking each wall the mesh actually produced, finding which two cells it stands
    /// between, and insisting exactly one of them holds cloud is a different question — and it is
    /// the question that catches a neighbour lookup that is off by one, or one that does not wrap.
    /// </remarks>
    private static List<string> CloudSelfTest(WorldSeed seed)
    {
        var faults = new List<string>();
        var field = new CloudField(seed);
        var mesh = field.Build();

        if (mesh.TopQuads != field.CloudCells * 2)
            faults.Add($"{mesh.TopQuads} caps over {field.CloudCells:N0} cells, expected {field.CloudCells * 2:N0}");

        var walls = 0;
        int wallsAtNear = 0, wallsAtFar = 0;
        for (var quad = 0; quad < mesh.QuadCount; quad++)
        {
            var centre = Vector3.Zero;
            for (var i = 0; i < 4; i++)
            {
                var v = mesh.Vertices[quad * 4 + i];
                centre += new Vector3(v.X, v.Y, v.Z);
            }
            centre /= 4f;

            // A cap's centre is on the top or the bottom; a wall's is half way up.
            if (MathF.Abs(centre.Y - CloudField.Thickness * 0.5f) > 0.01f) continue;
            walls++;

            // The wall lies on a cell boundary in exactly one axis, between two cells.
            var onX = MathF.Abs(centre.X / CloudField.CellBlocks - MathF.Round(centre.X / CloudField.CellBlocks)) < 0.01f;
            var x = (int)MathF.Floor(centre.X / CloudField.CellBlocks);
            var z = (int)MathF.Floor(centre.Z / CloudField.CellBlocks);

            var near = field[x, z];
            var far = onX ? field[x - 1, z] : field[x, z - 1];

            if (near == far)
                faults.Add($"a wall at ({centre.X:F0},{centre.Z:F0}) stands between two cells that agree");

            if (onX && centre.X < 0.01f) wallsAtNear++;
            if (onX && centre.X > CloudField.Period - 0.01f) wallsAtFar++;
        }

        if (walls != mesh.SideQuads)
            faults.Add($"{walls} walls found in the geometry, {mesh.SideQuads} reported");

        if (walls == 0) faults.Add("the sheet has no edges at all");

        // The seam, counted from the bitmap's two outermost columns. This is the property that
        // makes a finite sheet endless: a copy drawn beside this one must not show a wall down the
        // join where both sides are cloud, and must show one where they differ. A neighbour lookup
        // that clamps instead of wrapping passes every other test here and puts a wall across the
        // whole sky every 1,536 blocks.
        int expectNear = 0, expectFar = 0;
        for (var z = 0; z < CloudField.Size; z++)
        {
            var first = field[0, z];
            var last = field[CloudField.Size - 1, z];
            if (first && !last) expectNear++;
            if (last && !first) expectFar++;
        }

        if (wallsAtNear != expectNear || wallsAtFar != expectFar)
            faults.Add($"the seam carries {wallsAtNear}+{wallsAtFar} walls, the bitmap wants {expectNear}+{expectFar}");

        // And the wrap itself, unconditionally. How many walls the seam happens to want depends on
        // whether this seed's two edge columns agree, so the count above can be right by luck; the
        // sheet reading one block off its own far edge cannot be.
        for (var i = 0; i < CloudField.Size; i++)
        {
            if (field[-1, i] != field[CloudField.Size - 1, i]) faults.Add($"column -1 is not column {CloudField.Size - 1} at row {i}");
            if (field[CloudField.Size, i] != field[0, i]) faults.Add($"column {CloudField.Size} is not column 0 at row {i}");
            if (field[i, -1] != field[i, CloudField.Size - 1]) faults.Add($"row -1 is not row {CloudField.Size - 1} at column {i}");
            if (field[i, CloudField.Size] != field[i, 0]) faults.Add($"row {CloudField.Size} is not row 0 at column {i}");
        }

        return faults;
    }

    /// <summary>
    /// Measures how much of its cell each shape actually fills.
    /// </summary>
    /// <remarks>
    /// A shape that is subtly the wrong size is invisible to everything else here: the models
    /// validate, the quads wind outward, the mesh emits them. It reads on screen as a slab that is
    /// a little too tall or a step you cannot quite walk up, which is the sort of thing that gets
    /// noticed months later and blamed on the physics.
    /// </remarks>
    private static List<string> ShapeVolumeSelfTest(BlockRegistry registry)
    {
        var faults = new List<string>();

        (string Name, float Want)[] cases =
        [
            ("driftoak_slab_lower", 0.5f),
            ("driftoak_slab_upper", 0.5f),
            ("stone_slab_lower", 0.5f),
            ("driftoak_stairs_east_lower", 0.75f),
            ("driftoak_stairs_north_upper", 0.75f),
            ("snow_layer", 3f / 16f),
        ];

        foreach (var (name, want) in cases)
        {
            var got = BoxVolume(registry.ByName(name).Model);
            if (MathF.Abs(got - want) > 0.001f)
                faults.Add($"{name} fills {got:F3} of its cell, expected {want:F3}");
        }

        // The torch is not a box count — its planes overlap and its post is inside them — so it gets
        // the one property that matters instead: it must be slender enough to read as a stick.
        var torch = registry.ByName("torch").Model;
        var widest = 0f;
        foreach (var element in torch.Elements)
        {
            var span = MathF.Min(element.To.X - element.From.X, element.To.Z - element.From.Z);
            widest = MathF.Max(widest, span);
        }

        if (widest > 4f) faults.Add($"the torch is {widest / 16f:F2} of a block thick, which is a post not a torch");

        // The box the selection outline and the cracking overlay wrap around. Nothing else notices
        // when it quietly goes back to being a unit cube — the shape still draws, the block still
        // breaks — and a full-cube outline around a slab is the tell that the game thinks in cubes.
        (string Name, Vector3 Min, Vector3 Max)[] outlines =
        [
            ("stone", Vector3.Zero, Vector3.One),
            ("driftoak_slab_lower", Vector3.Zero, new Vector3(1f, 0.5f, 1f)),
            ("driftoak_slab_upper", new Vector3(0f, 0.5f, 0f), Vector3.One),
            ("snow_layer", Vector3.Zero, new Vector3(1f, 3f / 16f, 1f)),
            ("torch", new Vector3(7f, 0f, 7f) / 16f, new Vector3(9f, 10f, 9f) / 16f),
            ("meadowgrass", new Vector3(0.05f, 0f, 0.05f), new Vector3(0.95f, 1f, 0.95f)),
        ];

        foreach (var (name, wantMin, wantMax) in outlines)
        {
            var (min, max) = registry.ByName(name).Model.Outline;
            if (Vector3.Distance(min, wantMin) > 0.01f || Vector3.Distance(max, wantMax) > 0.01f)
                faults.Add(
                    $"{name} outlines ({min.X:F2},{min.Y:F2},{min.Z:F2})-({max.X:F2},{max.Y:F2},{max.Z:F2}), "
                    + $"expected ({wantMin.X:F2},{wantMin.Y:F2},{wantMin.Z:F2})-({wantMax.X:F2},{wantMax.Y:F2},{wantMax.Z:F2})");
        }

        return faults;
    }

    /// <summary>Total volume of a model's boxes, in cells. Assumes they do not overlap.</summary>
    private static float BoxVolume(BlockModel model)
    {
        var total = 0f;
        foreach (var element in model.Elements)
        {
            var size = element.To - element.From;
            total += size.X * size.Y * size.Z;
        }
        return total / (16f * 16f * 16f);
    }

    /// <summary>
    /// Puts every hand entry down against every face, at every height, facing every way.
    /// </summary>
    /// <remarks>
    /// <para>The check reads the shape back rather than the id. Comparing against a table of
    /// expected block names would only prove the table matches itself; asking where the raised half
    /// of the stair actually ended up is what catches a facing that is a quarter turn out, which is
    /// the single most likely thing to be wrong here and looks exactly like a modelling bug from
    /// inside the game.</para>
    /// <para>Heights are chosen either side of the boundary rather than at round numbers, because
    /// "the half the ray landed in" is a comparison against one half and both sides of it have to
    /// be walked.</para>
    /// </remarks>
    private static List<string> PlacementSelfTest(BlockRegistry registry)
    {
        var faults = new List<string>();
        var hand = StarterBlocks.Hand(registry);

        (Vector3 Look, int Facing)[] looks =
        [
            (new Vector3(1f, 0f, 0f), Faces.PosX),
            (new Vector3(-1f, 0f, 0f), Faces.NegX),
            (new Vector3(0f, 0f, 1f), Faces.PosZ),
            (new Vector3(0f, 0f, -1f), Faces.NegZ),
            (new Vector3(0.9f, -0.6f, 0.2f), Faces.PosX),      // looking down and mostly east
            (new Vector3(0.2f, 0.5f, -0.9f), Faces.NegZ),      // looking up and mostly north
        ];

        float[] heights = [0f, 0.25f, 0.5f, 0.501f, 0.75f, 1f];
        int[] hitFaces = [Faces.PosY, Faces.NegY, Faces.PosX, Faces.NegZ];

        foreach (var entry in hand)
        foreach (var face in hitFaces)
        foreach (var height in heights)
        foreach (var (look, wantFacing) in looks)
        {
            var where = $"{entry.Label} on face {face} at height {height:F3} looking {wantFacing}";
            var placed = entry.TryResolve(face, height, look, out var id);

            if (entry.Kind == PlacementKind.Standing)
            {
                if (placed != (face == Faces.PosY))
                    faults.Add($"{where}: {(placed ? "stood on nothing" : "refused a floor")}");
                continue;
            }

            if (!placed)
            {
                faults.Add($"{where}: refused to place at all");
                continue;
            }

            if (entry.Kind == PlacementKind.Plain) continue;

            var model = registry[id].Model;
            var wantUpper = height > 0.5f;
            var gotUpper = SitsInUpperHalf(model);

            if (gotUpper != wantUpper)
                faults.Add($"{where}: landed in the {(gotUpper ? "upper" : "lower")} half, wanted the other");

            if (entry.Kind != PlacementKind.Stairs) continue;

            var gotFacing = StepFacing(model);
            if (gotFacing != wantFacing)
                faults.Add($"{where}: step ended up facing {gotFacing}");
        }

        return faults;
    }

    /// <summary>Whether the box covering the whole footprint sits in the cell's upper half.</summary>
    private static bool SitsInUpperHalf(BlockModel model)
    {
        foreach (var element in model.Elements)
        {
            if (element.To.X - element.From.X < 15.9f) continue;
            if (element.To.Z - element.From.Z < 15.9f) continue;
            return element.From.Y > 7.9f;
        }

        return false;
    }

    /// <summary>Which side of the cell a stair's raised step sits on, or -1 when nothing does.</summary>
    private static int StepFacing(BlockModel model)
    {
        foreach (var element in model.Elements)
        {
            if (element.To.X - element.From.X < 15.9f)
                return element.From.X > 0.1f ? Faces.PosX : Faces.NegX;
            if (element.To.Z - element.From.Z < 15.9f)
                return element.From.Z > 0.1f ? Faces.PosZ : Faces.NegZ;
        }

        return -1;
    }

    /// <summary>
    /// Builds every block tile and checks that each one was actually painted.
    /// </summary>
    /// <remarks>
    /// The magenta test is the one that earns its place. An unhandled layer falls through to a
    /// loud placeholder, which is obvious the moment anyone looks at that block — and completely
    /// invisible if the block happens to be one that only spawns underground, or one nobody thought
    /// to fly past. A layer added without art is exactly the kind of thing that ships.
    /// <para>Cutout layers are checked for holes for the opposite reason: leaves with no
    /// transparency render as a solid green cube, which looks deliberate.</para>
    /// </remarks>
    private static List<string> TextureSelfTest()
    {
        var faults = new List<string>();
        var built = BlockTextureSet.Build(packPath: null);

        for (var layer = 0; layer < built.Tiles.Length; layer++)
        {
            var name = BlockTextureSet.Layers[layer].Name;
            var tile = built.Tiles[layer];

            if (tile.Length != built.Size * built.Size * 4)
            {
                faults.Add($"{name} is {tile.Length} bytes, expected {built.Size * built.Size * 4}");
                continue;
            }

            var magenta = 0;
            var transparent = 0;
            var distinct = new HashSet<int>();

            for (var i = 0; i < tile.Length; i += 4)
            {
                if (tile[i] == 255 && tile[i + 1] == 0 && tile[i + 2] == 255) magenta++;
                if (tile[i + 3] < 128) transparent++;
                distinct.Add((tile[i] << 16) | (tile[i + 1] << 8) | tile[i + 2]);
            }

            var pixels = tile.Length / 4;
            if (magenta > pixels / 2) faults.Add($"{name} is the missing-texture placeholder");
            else if (distinct.Count < 4) faults.Add($"{name} has only {distinct.Count} colours — flat, not drawn");

            var cutout = BlockTextureSet.Layers[layer].Cutout;
            if (cutout && transparent == 0) faults.Add($"{name} is marked cutout but has no holes");
            if (!cutout && transparent > 0) faults.Add($"{name} is opaque but has {transparent} clear pixels");
        }

        return faults;
    }

    /// <summary>
    /// Fires rays at a single block from every side and checks which face each one reports.
    /// </summary>
    /// <remarks>
    /// The face is the half of the answer that is easy to get subtly wrong and hard to notice.
    /// A hit position alone looks correct in every screenshot; the face is what decides where a
    /// placed block goes, and an off-by-one there puts it inside the block you were aiming at
    /// — which reads as "placing sometimes does nothing" rather than as a maths error.
    /// </remarks>
    private static List<string> RaycastSelfTest(BlockRegistry registry, StarterBlocks.Ids ids)
    {
        var faults = new List<string>();
        var stops = registry.BuildSolidTable();

        var world = new VoxelWorld(registry);
        world.SetBlock(0, 0, 0, ids.Stone);

        // Fire at the block from each side; the face reported must be the one facing the shooter.
        (Vector3 From, Vector3 Dir, int Face, string Name)[] cases =
        [
            (new Vector3(5.5f, 0.5f, 0.5f), -Vector3.UnitX, Faces.PosX, "from +X"),
            (new Vector3(-5.5f, 0.5f, 0.5f), Vector3.UnitX, Faces.NegX, "from -X"),
            (new Vector3(0.5f, 5.5f, 0.5f), -Vector3.UnitY, Faces.PosY, "from +Y"),
            (new Vector3(0.5f, -5.5f, 0.5f), Vector3.UnitY, Faces.NegY, "from -Y"),
            (new Vector3(0.5f, 0.5f, 5.5f), -Vector3.UnitZ, Faces.PosZ, "from +Z"),
            (new Vector3(0.5f, 0.5f, -5.5f), Vector3.UnitZ, Faces.NegZ, "from -Z"),
        ];

        foreach (var (from, dir, face, name) in cases)
        {
            if (!BlockRay.TryCast(world, stops, from, dir, 20f, out var hit))
            {
                faults.Add($"{name} missed");
                continue;
            }

            if (hit.X != 0 || hit.Y != 0 || hit.Z != 0)
                faults.Add($"{name} hit ({hit.X},{hit.Y},{hit.Z}), expected the origin block");
            else if (hit.Face != face)
                faults.Add($"{name} reported face {hit.Face}, expected {face}");
            else if (world.GetBlock(hit.Adjacent.X, hit.Adjacent.Y, hit.Adjacent.Z) != BlockId.Air)
                faults.Add($"{name} placement cell is not empty");
        }

        // A diagonal must still land on the block rather than slipping past its corner.
        if (!BlockRay.TryCast(world, stops, new Vector3(4.5f, 4.5f, 4.5f), new Vector3(-1, -1, -1), 20f, out _))
            faults.Add("diagonal ray slipped past the corner");

        // A ray pointed at nothing must report a miss, not the last cell it walked through.
        if (BlockRay.TryCast(world, stops, new Vector3(0.5f, 0.5f, 5.5f), Vector3.UnitZ, 20f, out _))
            faults.Add("ray fired away from the block still reported a hit");

        // Range is a range: just past it is a miss, just inside it is a hit.
        if (BlockRay.TryCast(world, stops, new Vector3(20.5f, 0.5f, 0.5f), -Vector3.UnitX, 5f, out _))
            faults.Add("hit a block 20 blocks away on a 5-block reach");

        // Standing inside a block targets that block.
        if (!BlockRay.TryCast(world, stops, new Vector3(0.5f, 0.5f, 0.5f), Vector3.UnitX, 5f, out var inside)
            || inside.X != 0 || inside.Y != 0 || inside.Z != 0)
            faults.Add("a ray starting inside a block did not target it");

        return faults;
    }

    /// <summary>
    /// Drops, walks, jumps and crouches a body in a purpose-built room and checks what happened.
    /// </summary>
    /// <remarks>
    /// <para>Built as a flat floor with a wall and a ledge rather than run on generated terrain,
    /// because every one of these questions has an exact answer only when the geometry is known.
    /// "Did it land on the ground" over real terrain is a question about the terrain.</para>
    /// <para>Each case is one that goes wrong quietly. A body that falls through the world at high
    /// speed looks fine at walking pace. A body that never reports standing on ground can still be
    /// walked around — it just never jumps again. And a step-up that works while airborne is a
    /// wall-climbing exploit that will not show up until somebody tries it.</para>
    /// </remarks>
    private static List<string> PhysicsSelfTest(
        WorldSeed seed, BlockRegistry registry, StarterBlocks.Ids ids, float oceanCoverage)
    {
        var faults = new List<string>();

        const int floorY = 40;
        var world = new VoxelWorld(registry);

        // A 32x32 floor at y=40, a wall along x=8, and a ledge: the floor stops at z=12.
        for (var z = -4; z < 20; z++)
        for (var x = -4; x < 20; x++)
        {
            if (z > 12) continue;
            world.SetBlock(x, floorY, z, ids.Stone);
            world.SetBlock(x, floorY - 1, z, ids.Stone);
        }

        for (var y = 1; y <= 4; y++)
        for (var z = -4; z < 20; z++)
            world.SetBlock(8, floorY + y, z, ids.Stone);

        // A single block to step onto, and a two-block wall that must not be climbable.
        world.SetBlock(3, floorY + 1, 3, ids.Stone);

        var top = floorY + 1f;   // a body standing on the floor has its feet here
        const float dt = 1f / 60f;

        // Falls and lands.
        var body = new PlayerBody(registry);
        body.Teleport(new Vector3(1.5f, floorY + 25f, 1.5f));
        for (var i = 0; i < 300; i++) body.Step(world, dt, Vector3.Zero, false, false, false);

        if (!body.OnGround) faults.Add("body never landed after a 25-block drop");
        if (MathF.Abs(body.Position.Y - top) > 0.01f)
            faults.Add($"landed at y {body.Position.Y:F3}, expected {top:F3}");

        // Terminal velocity must not tunnel it through a two-block floor.
        var faller = new PlayerBody(registry);
        faller.Teleport(new Vector3(1.5f, floorY + 120f, 1.5f));
        for (var i = 0; i < 600; i++) faller.Step(world, dt, Vector3.Zero, false, false, false);
        if (faller.Position.Y < floorY - 2f)
            faults.Add($"fell through the floor from 120 blocks up, ended at y {faller.Position.Y:F1}");

        // Walks into a wall and stops short of it. The wall face is at x=8, the body is 0.6 wide.
        var walker = new PlayerBody(registry);
        walker.Teleport(new Vector3(1.5f, top, 1.5f));
        for (var i = 0; i < 240; i++)
            walker.Step(world, dt, new Vector3(1f, 0f, 0f), false, false, false);

        if (walker.Position.X > 8f - PlayerBody.Width * 0.5f + 0.01f)
            faults.Add($"walked into the wall at x {walker.Position.X:F3}");
        if (walker.Position.X < 6.5f)
            faults.Add($"stopped {8f - walker.Position.X:F2} blocks short of the wall");

        // Cannot climb the wall by jumping into it repeatedly. Measured after letting it settle,
        // not at whatever point in a jump arc the loop happened to stop — the first version of this
        // test caught the body mid-hop and reported a wall climb that was just a jump.
        var climber = new PlayerBody(registry);
        climber.Teleport(new Vector3(6f, top, 1.5f));
        for (var i = 0; i < 600; i++)
            climber.Step(world, dt, new Vector3(1f, 0f, 0f), jump: true, sneak: false, sprint: false);
        for (var i = 0; i < 120; i++)
            climber.Step(world, dt, Vector3.Zero, jump: false, sneak: false, sprint: false);
        if (climber.Position.Y > top + 0.01f)
            faults.Add($"climbed the wall, settled at y {climber.Position.Y:F2} above {top:F2}");

        // A jump clears one block and not two. One settling frame first: a body that has just been
        // placed does not yet know it is standing on anything, and refusing to jump in mid-air is
        // the correct answer to a question the test meant to ask differently.
        var jumper = new PlayerBody(registry);
        jumper.Teleport(new Vector3(1.5f, top, 1.5f));
        jumper.Step(world, dt, Vector3.Zero, jump: false, sneak: false, sprint: false);
        jumper.Step(world, dt, Vector3.Zero, jump: true, sneak: false, sprint: false);
        var peak = jumper.Position.Y;
        for (var i = 0; i < 120; i++)
        {
            jumper.Step(world, dt, Vector3.Zero, false, false, false);
            if (jumper.Position.Y > peak) peak = jumper.Position.Y;
        }
        var jumpHeight = peak - top;
        if (jumpHeight is < 1.0f or >= 2.0f)
            faults.Add($"jump reaches {jumpHeight:F2} blocks (want 1.0 to just under 2.0)");

        // Crouching at the ledge stops the body walking off it; not crouching does not.
        var sneaker = new PlayerBody(registry);
        sneaker.Teleport(new Vector3(1.5f, top, 11f));
        for (var i = 0; i < 240; i++)
            sneaker.Step(world, dt, new Vector3(0f, 0f, 1f), jump: false, sneak: true, sprint: false);
        if (sneaker.Position.Y < top - 0.01f)
            faults.Add($"crouched off the ledge, fell to y {sneaker.Position.Y:F2}");

        var stroller = new PlayerBody(registry);
        stroller.Teleport(new Vector3(1.5f, top, 11f));
        for (var i = 0; i < 240; i++)
            stroller.Step(world, dt, new Vector3(0f, 0f, 1f), jump: false, sneak: false, sprint: false);
        if (stroller.Position.Y >= top - 0.01f)
            faults.Add("walked off the ledge and did not fall — the crouch test proves nothing");

        return faults;
    }

    private readonly record struct CanopySurvey(int Clusters, double MeanSize, int Largest);

    /// <summary>
    /// Flood-fills the leaf blocks into connected clusters and reports how big they get.
    /// </summary>
    /// <remarks>
    /// This is the measurement for "do the trees grow into one another". Crown radius and tree
    /// spacing are both inputs to that and neither one answers it: widen the crowns and tighten the
    /// grid all you like, the question is whether the foliage actually joins up. A world of isolated
    /// trees has every cluster the size of one crown, however many trees there are. A wood has a few
    /// enormous ones with trunks running up through them.
    /// </remarks>
    private static CanopySurvey SurveyCanopy(VoxelWorld world, Chunk[] chunks, StarterBlocks.Ids ids)
    {
        var visited = new Dictionary<ChunkPos, bool[]>();
        var stack = new Stack<(int X, int Y, int Z)>();

        var clusters = 0;
        var largest = 0;
        long total = 0;

        bool TryClaim(int wx, int wy, int wz)
        {
            if (wy < 0 || wy >= TerrainGenerator.WorldHeight) return false;
            if (world.GetBlock(wx, wy, wz) != ids.Leaves) return false;

            var pos = ChunkPos.FromWorld(wx, wy, wz);
            if (!world.TryGetChunk(pos, out _)) return false;

            if (!visited.TryGetValue(pos, out var flags))
            {
                flags = new bool[Chunk.Volume];
                visited[pos] = flags;
            }

            var i = Chunk.Index(wx & Chunk.SizeMask, wy & Chunk.SizeMask, wz & Chunk.SizeMask);
            if (flags[i]) return false;
            flags[i] = true;
            return true;
        }

        foreach (var chunk in chunks)
        {
            var (ox, oy, oz) = chunk.Position.Origin;
            var raw = chunk.Raw;

            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            for (var x = 0; x < Chunk.Size; x++)
            {
                if (raw[Chunk.Index(x, y, z)] != ids.Leaves.Value) continue;
                if (!TryClaim(ox + x, oy + y, oz + z)) continue;

                var size = 0;
                stack.Push((ox + x, oy + y, oz + z));

                while (stack.Count > 0)
                {
                    var (cx, cy, cz) = stack.Pop();
                    size++;

                    for (var face = 0; face < Faces.Count; face++)
                    {
                        var n = Faces.Normals[face];
                        if (TryClaim(cx + n.X, cy + n.Y, cz + n.Z))
                            stack.Push((cx + n.X, cy + n.Y, cz + n.Z));
                    }
                }

                clusters++;
                total += size;
                if (size > largest) largest = size;
            }
        }

        return clusters == 0
            ? new CanopySurvey(0, 0, 0)
            : new CanopySurvey(clusters, total / (double)clusters, largest);
    }

    private readonly record struct LightSurvey(
        long OpenCells,
        double SkyFullPct,
        double SkyDarkPct,
        long BlockLitCells,
        long ColouredCells,
        int BlockPeak,
        long LitSolids,
        int MaxSkyUnderLeaves);

    /// <summary>
    /// Counts what the lighting pass actually produced, over every cell light is allowed to exist in.
    /// </summary>
    /// <remarks>
    /// Deliberately reported as shares of open air rather than of the whole volume. Most of a world
    /// is stone, and stone is always dark, so any fraction taken over everything is dominated by the
    /// part of the answer nobody was asking about — and it barely moves when the lighting is
    /// completely broken.
    /// </remarks>
    private static LightSurvey SurveyLight(VoxelWorld world, Chunk[] chunks, BlockRegistry registry)
    {
        var opaque = registry.BuildOpacityTable();
        var emission = registry.BuildLightEmissionTable();

        long open = 0, skyFull = 0, skyDark = 0, blockLit = 0, coloured = 0, litSolids = 0;
        var peak = 0;
        var underLeaves = 0;

        var leaves = registry.ByName("driftoak_leaves").Id.Value;

        foreach (var chunk in chunks)
        {
            var raw = chunk.Raw;
            var light = chunk.RawLight;
            var (ox, oy, oz) = chunk.Position.Origin;

            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            for (var x = 0; x < Chunk.Size; x++)
            {
                var i = Chunk.Index(x, y, z);
                var packed = light[i];

                if (opaque[raw[i]])
                {
                    // Light inside solid rock means the attenuation table is not being consulted.
                    // An emitter is the exception: it holds its own emission, which is where the
                    // flood starts from. Counting those was the first thing this check caught, and
                    // the count matched the emberstone census exactly — the check was wrong, not
                    // the engine.
                    if (packed != 0 && emission[raw[i]] == 0) litSolids++;
                    continue;
                }

                open++;

                var sky = LightValue.Sky(packed);
                if (sky >= LightValue.Max) skyFull++;
                else if (sky == 0) skyDark++;

                var r = LightValue.Red(packed);
                var g = LightValue.Green(packed);
                var b = LightValue.Blue(packed);
                var block = Math.Max(r, Math.Max(g, b));
                if (block > 0)
                {
                    blockLit++;
                    if (r != g || g != b) coloured++;
                    if (block > peak) peak = block;
                }

                if (sky > underLeaves && world.GetBlock(ox + x, oy + y + 1, oz + z).Value == leaves)
                    underLeaves = sky;
            }
        }

        if (open == 0) return new LightSurvey(0, 0, 0, 0, 0, 0, litSolids, 0);

        return new LightSurvey(
            open,
            skyFull * 100.0 / open,
            skyDark * 100.0 / open,
            blockLit,
            coloured,
            peak,
            litSolids,
            underLeaves);
    }

    private readonly record struct TreeSurvey(
        int Count, int MinTrunk, int MaxTrunk, double MeanTrunk, int MinCrown, int MaxCrown);

    /// <summary>
    /// Finds every trunk in the volume and measures how tall it stands.
    /// </summary>
    /// <remarks>
    /// <para>A trunk is a vertical run of logs standing on the ground — the block under it is
    /// terrain, not air and not another log. That test is doing real work: a tree also grows
    /// branches, which are logs with nothing beneath them, and a flare of logs at its foot. Counting
    /// every log run as a trunk reported eight thousand trees averaging 2.3 logs in a world whose
    /// shortest tree is five, because it was measuring limbs.</para>
    /// <para>Runs shorter than three are dropped as flare. No real trunk is that short — the
    /// generator's minimum is five — so this discards nothing the measurement is about.</para>
    /// <para>The crown is measured separately by walking up through the leaves directly above,
    /// because the two can disagree: a tall trunk under a thin canopy and a short one under a fat
    /// one are nothing alike from the ground and are the same number of logs.</para>
    /// <para>Trunks touching the top of the sampled volume are skipped rather than counted short —
    /// an edge-clipped tree would drag the mean down and read as a generator fault.</para>
    /// </remarks>
    private static TreeSurvey SurveyTrees(VoxelWorld world, Chunk[] chunks, StarterBlocks.Ids ids)
    {
        const int MinTrunkLogs = 3;

        static bool IsGround(BlockId id, StarterBlocks.Ids ids) =>
            id == ids.Grass || id == ids.Dirt || id == ids.Sand
            || id == ids.Stone || id == ids.Gravel;

        // Find the columns worth walking first. Scanning every column of the volume through
        // world-space reads would be twenty million dictionary lookups to find a few hundred trees.
        var columns = new HashSet<(int X, int Z)>();
        foreach (var chunk in chunks)
        {
            var (ox, _, oz) = chunk.Position.Origin;
            var raw = chunk.Raw;
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            for (var x = 0; x < Chunk.Size; x++)
            {
                if (raw[Chunk.Index(x, y, z)] == ids.Log.Value) columns.Add((ox + x, oz + z));
            }
        }

        var count = 0;
        var min = int.MaxValue;
        var max = int.MinValue;
        long sum = 0;
        var minCrown = int.MaxValue;
        var maxCrown = int.MinValue;

        var top = TerrainGenerator.WorldHeight - 1;

        foreach (var (wx, wz) in columns)
        {
            for (var wy = 1; wy < top; wy++)
            {
                if (world.GetBlock(wx, wy, wz) != ids.Log) continue;
                if (world.GetBlock(wx, wy - 1, wz) == ids.Log) continue;   // mid-trunk

                var trunk = 0;
                var y = wy;
                while (y <= top && world.GetBlock(wx, y, wz) == ids.Log) { trunk++; y++; }

                if (y > top) break;   // clipped by the top of the volume, not a real measurement

                // A branch has air under it and a flare is one block tall. Neither is a trunk.
                if (!IsGround(world.GetBlock(wx, wy - 1, wz), ids) || trunk < MinTrunkLogs)
                {
                    wy = y;
                    continue;
                }

                var crown = trunk;
                while (y <= top && world.GetBlock(wx, y, wz) == ids.Leaves) { crown++; y++; }

                count++;
                sum += trunk;
                if (trunk < min) min = trunk;
                if (trunk > max) max = trunk;
                if (crown < minCrown) minCrown = crown;
                if (crown > maxCrown) maxCrown = crown;

                wy = y;   // nothing else in this column belongs to the same tree
            }
        }

        if (count == 0) return new TreeSurvey(0, 0, 0, 0, 0, 0);
        return new TreeSurvey(count, min, max, sum / (double)count, minCrown, maxCrown);
    }

    /// <summary>
    /// Runs the streamer to quiescence and checks every mesh it produced against the mesh the
    /// batch path builds for the same chunk.
    /// </summary>
    /// <remarks>
    /// The failure this exists for: meshing a chunk before its neighbours have generated. Absent
    /// neighbours read as air, so the chunk grows a wall of faces along the seam — geometry that is
    /// perfectly valid, passes every other check, and disappears the moment the neighbour arrives.
    /// <para>The test compares vertex counts against a fully-generated reference and reports any
    /// difference, without assuming a direction. It would be natural to expect an early mesh to
    /// hold strictly more geometry, since it emits faces the finished world hides — but greedy
    /// merging is not monotonic in face count. A flat wall against absent-neighbour air merges into
    /// a handful of large quads, while the correct broken surface needs more. Verified: forcing the
    /// streamer to mesh without waiting reports 4788 verts against 4792.</para>
    /// </remarks>
    private static bool StreamingMatchesBatch(
        WorldSeed seed, BlockRegistry registry, StarterBlocks.Ids ids, float oceanCoverage, out string detail)
    {
        const int radius = 3;
        const int settleTimeoutMs = 30_000;

        var produced = new Dictionary<ChunkPos, (int Verts, int Indices)>();

        using (var streamer = new WorldStreamer(registry, new TerrainGenerator(seed, ids, oceanCoverage), radius))
        {
            streamer.Update(Vector3.Zero);

            var watch = Stopwatch.StartNew();
            var idleSweeps = 0;
            while (watch.ElapsedMilliseconds < settleTimeoutMs)
            {
                streamer.PromoteReadyChunks();

                var drained = false;
                while (streamer.TryDequeueMesh(out var data))
                {
                    produced[data.Position] = (data.VertexCount, data.IndexCount);
                    drained = true;
                }

                var busy = streamer.PendingGenerate > 0 || streamer.PendingMesh > 0;
                if (busy || drained)
                {
                    idleSweeps = 0;
                    Thread.Sleep(2);
                    continue;
                }

                // Quiet for several consecutive sweeps means the pipeline has genuinely drained,
                // not that a worker happened to be between jobs.
                if (++idleSweeps >= 25) break;
                Thread.Sleep(2);
            }

            if (produced.Count == 0)
            {
                detail = "streamer produced no meshes";
                return false;
            }
        }

        // Reference world: everything the streamed chunks could sample, generated up front.
        var batchWorld = new VoxelWorld(registry);
        var batchGenerator = new TerrainGenerator(seed, ids, oceanCoverage);
        var chunksTall = TerrainGenerator.WorldHeight / Chunk.Size;

        var needed = new HashSet<ChunkPos>();
        foreach (var pos in produced.Keys)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var n = pos.Offset(dx, dy, dz);
            if (n.Y < 0 || n.Y >= chunksTall) continue;
            needed.Add(n);
        }

        foreach (var pos in needed)
        {
            var chunk = batchWorld.GetOrCreateChunk(pos);
            batchGenerator.GenerateChunk(chunk);
            batchGenerator.DecorateChunk(chunk);
        }

        // The reference has to be lit too, or the comparison is between meshes that baked light and
        // meshes that baked nothing, and it fails for a reason that has nothing to do with
        // streaming. Lighting the region whole is the point: it is the answer the streamed,
        // column-at-a-time version has to reproduce.
        new LightEngine(registry).LightAll(batchWorld);

        // The reference has to be tinted the same way too. Tint is part of a face's merge key, so a
        // reference built without it merges differently and the comparison fails for a reason that
        // has nothing to do with streaming — which is exactly what it did the first time.
        var mesher = new ChunkMesher(registry, new BlockTinter(new ClimateField(seed)));
        foreach (var (pos, streamed) in produced)
        {
            var reference = mesher.Build(batchWorld, pos);
            var refVerts = reference?.VertexCount ?? 0;
            var refIndices = reference?.IndexCount ?? 0;

            if (streamed.Verts == refVerts && streamed.Indices == refIndices) continue;

            detail = $"chunk {pos} differs: streamed {streamed.Verts} verts / {streamed.Indices} indices, "
                   + $"batch {refVerts} / {refIndices} — likely meshed before a neighbour generated";
            return false;
        }

        detail = $"{produced.Count} streamed meshes identical to batch";
        return true;
    }

    /// <summary>
    /// Generates a chunk alone, then generates the same chunk surrounded by its 26 neighbours, and
    /// compares the two block for block.
    /// </summary>
    /// <remarks>
    /// This is the question streaming actually asks. Chunks arrive in whatever order the player
    /// walks, and a chunk that comes out differently depending on which neighbours happen to exist
    /// produces seams that appear and disappear as you move — the worst class of bug to chase,
    /// because it never reproduces from a fresh load. Trees are the immediate case, since a canopy
    /// crosses seams, but the same property has to hold for every structure added later.
    /// </remarks>
    private static bool ChunksAreNeighbourIndependent(
        WorldSeed seed, BlockRegistry registry, StarterBlocks.Ids ids, float oceanCoverage, out string detail)
    {
        var generator = new TerrainGenerator(seed, ids, oceanCoverage);

        // Spread the samples so they land on different terrain: forest, coast, deep and high.
        ChunkPos[] targets =
        [
            new(0, 2, 0), new(3, 2, -4), new(-7, 1, 5), new(11, 2, 11), new(-13, 2, -9),
        ];

        foreach (var target in targets)
        {
            var alone = new VoxelWorld(registry);
            var solo = alone.GetOrCreateChunk(target);
            generator.GenerateChunk(solo);
            generator.DecorateChunk(solo);

            var surrounded = new VoxelWorld(registry);
            Chunk? together = null;
            for (var dy = -1; dy <= 1; dy++)
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var pos = target.Offset(dx, dy, dz);
                var chunk = surrounded.GetOrCreateChunk(pos);
                generator.GenerateChunk(chunk);
                generator.DecorateChunk(chunk);
                if (pos == target) together = chunk;
            }

            // Same chunk again, but hunting for structure origins far enough away that nothing
            // could plausibly be missed. If the production reach is wide enough this changes
            // nothing; if it is too narrow, the wide pass finds geometry the narrow one dropped.
            var wide = new VoxelWorld(registry);
            var generous = wide.GetOrCreateChunk(target);
            generator.GenerateChunk(generous);
            generator.DecorateChunk(generous, Chunk.Size * 2);

            if (!Compare(solo.Raw, together!.Raw, target, "alone", "surrounded", out detail)) return false;
            if (!Compare(solo.Raw, generous.Raw, target, "normal reach", "wide reach", out detail)) return false;
        }

        detail = $"{targets.Length} chunks stable alone, surrounded, and at 2x search reach";
        return true;

        static bool Compare(ushort[] a, ushort[] b, ChunkPos target, string nameA, string nameB, out string detail)
        {
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] == b[i]) continue;

                var x = i & Chunk.SizeMask;
                var z = (i >> Chunk.SizeLog2) & Chunk.SizeMask;
                var y = i >> (Chunk.SizeLog2 * 2);
                detail = $"chunk {target} local ({x},{y},{z}): {nameA} {a[i]}, {nameB} {b[i]}";
                return false;
            }

            detail = string.Empty;
            return true;
        }
    }

    /// <summary>
    /// Walks the heightmap over a square block range and reports its extremes, mean, and the
    /// share of columns standing above sea level.
    /// </summary>
    private static (int Min, int Max, double Mean, double AboveSeaPct) SampleRelief(
        TerrainGenerator generator, int min, int max, int step)
    {
        var lo = int.MaxValue;
        var hi = int.MinValue;
        long sum = 0;
        var samples = 0;
        var aboveSea = 0;

        for (var wz = min; wz <= max; wz += step)
        for (var wx = min; wx <= max; wx += step)
        {
            var h = generator.SurfaceHeight(wx, wz);
            if (h < lo) lo = h;
            if (h > hi) hi = h;
            sum += h;
            samples++;
            if (h > TerrainGenerator.SeaLevel) aboveSea++;
        }

        return (lo, hi, sum / (double)samples, aboveSea * 100.0 / samples);
    }

    /// <summary>
    /// Measures the model out of its own emitted geometry rather than out of its constants.
    /// </summary>
    /// <remarks>
    /// <para>The envelope alone is not enough, and finding that out is what control-testing this is
    /// for: shortening both legs by a unit leaves the model exactly as tall as it was and opens a
    /// gap at the hips that the height check waved straight through. So the parts are also asked to
    /// stack — every slice from the soles to the crown has to be inside something.</para>
    /// <para>Measured off the emitted vertices rather than off <c>UnitsTall</c>, which would only
    /// check the arithmetic against itself.</para>
    /// </remarks>
    private static (float Tall, float Wide, List<string> Faults) MeasureModel()
    {
        var vertices = new List<ModelVertex>();
        var indices = new List<uint>();
        var spans = new Dictionary<PlayerPart, (float Lo, float Hi)>();

        float lowest = float.MaxValue, highest = float.MinValue;
        float left = float.MaxValue, right = float.MinValue;

        foreach (var box in PlayerModel.Build(ArmStyle.Classic, legacy: false))
        {
            if (box.Overlay) continue;

            vertices.Clear();
            indices.Clear();
            PlayerModel.Emit(box, vertices, indices);

            float boxLow = float.MaxValue, boxHigh = float.MinValue;

            foreach (var v in vertices)
            {
                var world = v.Position + box.Pivot * PlayerModel.Unit;
                boxLow = MathF.Min(boxLow, world.Y);
                boxHigh = MathF.Max(boxHigh, world.Y);
                left = MathF.Min(left, world.X);
                right = MathF.Max(right, world.X);
            }

            spans[box.Part] = (boxLow, boxHigh);
            lowest = MathF.Min(lowest, boxLow);
            highest = MathF.Max(highest, boxHigh);
        }

        var faults = new List<string>();

        void Joint(string what, float a, float b)
        {
            if (MathF.Abs(a - b) > 1e-4f) faults.Add($"{what} ({a:F3} vs {b:F3})");
        }

        var body = spans[PlayerPart.Body];
        var head = spans[PlayerPart.Head];

        // Asked as joints between separately declared parts rather than as a single envelope. A
        // model whose right leg is a unit short is exactly as tall as it should be, and its other
        // leg still covers the slice the missing one left — so neither a height check nor a
        // "something covers every slice" check notices, which is how both earlier versions of this
        // passed a visibly broken model.
        Joint("head does not sit on the shoulders", body.Hi, head.Lo);

        foreach (var leg in (ReadOnlySpan<PlayerPart>)[PlayerPart.RightLeg, PlayerPart.LeftLeg])
        {
            Joint($"{leg} does not reach the ground", spans[leg].Lo, lowest);
            Joint($"{leg} does not reach the hip", spans[leg].Hi, body.Lo);
        }

        foreach (var arm in (ReadOnlySpan<PlayerPart>)[PlayerPart.RightArm, PlayerPart.LeftArm])
        {
            Joint($"{arm} hangs off the shoulder", spans[arm].Hi, body.Hi);
            Joint($"{arm} does not match the torso", spans[arm].Lo, body.Lo);
        }

        return (highest - lowest, right - left, faults);
    }

    /// <summary>
    /// Finds what is on top of each column and how high it is, for two surface materials.
    /// </summary>
    /// <remarks>
    /// Every fourth column, which is plenty for a mean and cheap enough to run inside the audit.
    /// The point is to compare where two materials sit rather than how much of each there is —
    /// a count is at the mercy of which climate region the sampled window happened to land in, and
    /// a mean height is not.
    /// </remarks>
    /// <param name="HighSnowPct">Share of the highest tenth of open ground that is snow.</param>
    /// <param name="HighFrom">The height that tenth starts at.</param>
    private readonly record struct SurfaceSample(
        int Snow, int Total, double MeanSnowY, double MeanGrassY, double HighSnowPct, int HighFrom);

    private static SurfaceSample SampleSurface(
        VoxelWorld world, int minBlock, int maxBlock, ushort snow, ushort grass, bool[] opaque)
    {
        long snowSum = 0, grassSum = 0;
        int snowCount = 0, grassCount = 0;
        var columns = new List<(int Y, bool Snow)>();

        for (var z = minBlock; z <= maxBlock; z += 4)
        for (var x = minBlock; x <= maxBlock; x += 4)
        {
            for (var y = TerrainGenerator.WorldHeight - 1; y >= 0; y--)
            {
                // Down to the first block that hides what is under it, not the first that is not
                // air. Anything standing on the ground — a tuft of grass, a canopy, a curtain of
                // vines — is not the ground, and stopping at it drops that whole column from both
                // materials' means. That was not hypothetical: the day tufts started growing, a
                // quarter of every meadow in the world silently left the sample and snow's share of
                // open ground jumped six points without a single snowflake moving.
                var id = world.GetBlock(x, y, z).Value;
                if (!opaque[id]) continue;

                if (id == snow) { snowSum += y; snowCount++; columns.Add((y, true)); }
                else if (id == grass) { grassSum += y; grassCount++; columns.Add((y, false)); }

                break;
            }
        }

        // What the highest ground is made of. The mean lift says snow sits above grass on average,
        // which a seed can satisfy by accident wherever climate happens to correlate with altitude —
        // and one of the five test seeds does exactly that, passing the lift test with the altitude
        // term taken out entirely. "The top of a mountain is white" is the property the feature
        // actually promises, and it has no such loophole.
        columns.Sort((a, b) => a.Y.CompareTo(b.Y));
        var highFrom = columns.Count > 0 ? columns[columns.Count * 9 / 10].Y : 0;

        int high = 0, highSnow = 0;
        foreach (var column in columns)
        {
            if (column.Y < highFrom) continue;
            high++;
            if (column.Snow) highSnow++;
        }

        return new SurfaceSample(
            snowCount,
            snowCount + grassCount,
            snowCount > 0 ? snowSum / (double)snowCount : 0,
            grassCount > 0 ? grassSum / (double)grassCount : 0,
            high > 0 ? highSnow * 100.0 / high : 0,
            highFrom);
    }

    /// <summary>
    /// Holds the button down on one block at a time and times how long each takes to give way.
    /// </summary>
    /// <remarks>
    /// <para>The check that matters is the <em>spread</em>, not any one number. A build where every
    /// block takes the same work passes "dirt breaks in about a second" and every other absolute
    /// gate here, and is exactly the thing this replaced — a world where punching stone and
    /// punching leaves feel identical. So the materials are asked to come out in order and to differ
    /// by an order of magnitude end to end.</para>
    /// <para>Bedrock gets its own case because "unbreakable" is not "very slow": it must still be
    /// there after a minute of holding, and it must never show a crack.</para>
    /// </remarks>
    private static List<string> MiningSelfTest(BlockRegistry registry, StarterBlocks.Ids ids)
    {
        var faults = new List<string>();
        const float dt = 1f / 60f;
        var cell = (0, 64, 0);

        float Break(BlockType type, int maxFrames = 7200)
        {
            var mining = new PlayerMining();
            for (var frame = 1; frame <= maxFrames; frame++)
                if (mining.Update(dt, type, cell, mining: true)) return frame * dt;

            return -1f;
        }

        var dirt = registry[ids.Dirt];
        var stone = registry[ids.Stone];
        var iron = registry[ids.IronOre];
        var leaves = registry[ids.Leaves];

        foreach (var type in (ReadOnlySpan<BlockType>)[leaves, dirt, stone, iron])
        {
            var measured = Break(type);
            var wanted = MiningRules.SecondsToBreak(type);

            if (measured < 0f) faults.Add($"{type.Name} never broke");
            else if (MathF.Abs(measured - wanted) > dt * 2f)
                faults.Add($"{type.Name} took {measured:F2}s, the rule says {wanted:F2}s");
        }

        var leafTime = Break(leaves);
        var dirtTime = Break(dirt);
        var stoneTime = Break(stone);
        var ironTime = Break(iron);

        if (!(leafTime < dirtTime && dirtTime < stoneTime && stoneTime < ironTime))
            faults.Add($"materials out of order: leaves {leafTime:F2}, dirt {dirtTime:F2}, stone {stoneTime:F2}, ore {ironTime:F2}");

        if (ironTime / leafTime < 10f)
            faults.Add($"hardest is only {ironTime / leafTime:F1}x the softest — materials barely differ");

        // Unbreakable means unbreakable, and shows nothing while being hit.
        var bedrockMining = new PlayerMining();
        for (var frame = 0; frame < 3600; frame++)
        {
            if (!bedrockMining.Update(dt, registry[ids.Bedrock], cell, mining: true)) continue;
            faults.Add($"bedrock broke after {frame * dt:F1}s");
            break;
        }

        if (bedrockMining.Stage >= 0) faults.Add("bedrock showed cracking");

        // Cracking has to climb the whole way and never go backwards, or the overlay flickers
        // between stages instead of deepening.
        var staged = new PlayerMining();
        var highest = -1;
        var slipped = false;
        for (var frame = 0; frame < 7200; frame++)
        {
            if (staged.Update(dt, stone, cell, mining: true)) break;

            var stage = staged.Stage;
            if (stage < highest) slipped = true;
            if (stage > highest) highest = stage;
        }

        if (slipped) faults.Add("cracking went backwards mid-block");
        if (highest != MiningRules.Stages - 1)
            faults.Add($"cracking reached stage {highest}, wanted {MiningRules.Stages - 1}");

        // Looking away and coming back starts the block again. Without this a player can chip at
        // ten blocks in rotation and have them all fall at once.
        var moved = new PlayerMining();
        for (var frame = 0; frame < 20; frame++) moved.Update(dt, stone, cell, mining: true);
        var earned = moved.Progress;
        moved.Update(dt, stone, (5, 64, 0), mining: true);
        if (moved.Progress >= earned)
            faults.Add($"moving the crosshair kept {moved.Progress:F2} of {earned:F2} progress");

        // And so does letting go.
        var released = new PlayerMining();
        for (var frame = 0; frame < 20; frame++) released.Update(dt, stone, cell, mining: true);
        released.Update(dt, stone, cell, mining: false);
        if (released.Progress > 0f) faults.Add($"letting go kept {released.Progress:F2} progress");

        return faults;
    }

    /// <summary>
    /// Checks the cracking stages grow into each other rather than being ten unrelated pictures.
    /// </summary>
    /// <remarks>
    /// Nesting is the property that cannot be eyeballed: ten stages that each look like cracking
    /// will still read as a block healing and re-breaking ten times if the later ones are not
    /// supersets of the earlier. Counting pixels only proves the totals rise, so every stage is
    /// compared against its predecessor pixel by pixel.
    /// </remarks>
    private static List<string> CrackSelfTest()
    {
        var faults = new List<string>();
        var stages = TileGen.Cracks(2001, MiningRules.Stages);
        var pixels = TileGen.Size * TileGen.Size;

        static bool Broken(byte[] tile, int i) => tile[i * 4 + 3] >= 128;

        var counts = new int[stages.Length];
        for (var s = 0; s < stages.Length; s++)
        for (var i = 0; i < pixels; i++)
            if (Broken(stages[s], i)) counts[s]++;

        if (counts[0] == 0) faults.Add("the first stage shows nothing");

        if (counts[^1] >= pixels * 9 / 10)
            faults.Add($"the last stage covers {counts[^1] * 100 / pixels}% of the face — it should be cracked, not painted over");

        if (counts[^1] <= counts[0] * 2)
            faults.Add($"cracking barely grows: {counts[0]} pixels to {counts[^1]}");

        for (var s = 1; s < stages.Length; s++)
        {
            for (var i = 0; i < pixels; i++)
            {
                if (!Broken(stages[s - 1], i) || Broken(stages[s], i)) continue;
                faults.Add($"stage {s} healed a fracture stage {s - 1} had");
                break;
            }
        }

        return faults;
    }

    /// <summary>
    /// Steps the animator at a fixed rate and measures what came out.
    /// </summary>
    /// <remarks>
    /// <para>Every check here is two-sided, because each of these has an opposite failure that the
    /// obvious one-sided version waves through. An arm that never moves passes "the swing finishes";
    /// an arm that never stops passes "the swing reaches the top". A stride with a floor on its
    /// period passes a model standing still.</para>
    /// <para>The stride period is counted from the pose the renderer would actually draw — zero
    /// crossings of the leg's angle — rather than read back off the phase accumulator, which would
    /// only prove the accumulator agrees with itself.</para>
    /// </remarks>
    private static List<string> AnimationSelfTest()
    {
        var faults = new List<string>();
        const float dt = 1f / 60f;
        var still = new Vector3(0f, 64f, 0f);

        // One swing, button released immediately: it has to run its course and stop on its own.
        var single = new PlayerAnimator();
        single.Strike();
        var struck = single.TakeStrikes();
        var peakLift = 0f;

        for (var i = 0; i < 60; i++)
        {
            single.Update(dt, still, 0f, PlayerBody.WalkSpeed, false, holding: false);
            peakLift = MathF.Max(peakLift, MathF.Abs(single.Pose(0f, 0f).RightArm.Pitch));
            struck += single.TakeStrikes();
        }

        if (struck != 1) faults.Add($"one click produced {struck} strikes");
        if (single.Swinging) faults.Add("swing never ended");
        if (peakLift < 1.2f) faults.Add($"arm only reached {peakLift:F2} rad, wanted past 1.2");

        var settled = MathF.Abs(single.Pose(0f, 0f).RightArm.Pitch);
        if (settled > 0.2f) faults.Add($"arm settled at {settled:F2} rad instead of near rest");

        // Held down: strikes should arrive at the swing's own cadence, not at the frame rate.
        var held = new PlayerAnimator();
        held.Strike();
        var repeats = held.TakeStrikes();
        for (var i = 0; i < 60; i++)   // one second
        {
            held.Update(dt, still, 0f, PlayerBody.WalkSpeed, false, holding: true);
            repeats += held.TakeStrikes();
        }

        var wanted = (int)(1f / PlayerAnimator.SwingSeconds) + 1;
        if (Math.Abs(repeats - wanted) > 1)
            faults.Add($"a second of holding gave {repeats} strikes, wanted about {wanted}");

        // Standing still: the legs must be still too. This is the control for the stride check
        // below — a walk cycle driven by a clock rather than by distance passes that one and fails
        // this one, and it is the failure a player would see as skating feet.
        var idle = new PlayerAnimator();
        for (var i = 0; i < 60; i++) idle.Update(dt, still, 0f, PlayerBody.WalkSpeed, false, false);
        var idleSwing = MathF.Abs(idle.Pose(0f, 0f).RightLeg.Pitch);
        if (idleSwing > 0.02f) faults.Add($"legs swing {idleSwing:F3} rad while standing still");

        // Walking in a straight line: count the leg's zero crossings to get the stride period.
        var peakStride = 0f;

        float MeasurePeriod(float speed)
        {
            const float seconds = 6f;
            const float settle = 0.5f;

            var walker = new PlayerAnimator();
            var at = still;
            var wasPositive = false;
            var counting = false;
            var crossings = 0;

            for (var i = 0; i < seconds / dt; i++)
            {
                at.X += speed * dt;
                walker.Update(dt, at, 0f, PlayerBody.WalkSpeed, false, false);

                var pitch = walker.Pose(0f, 0f).RightLeg.Pitch;
                if (speed >= PlayerBody.WalkSpeed) peakStride = MathF.Max(peakStride, MathF.Abs(pitch));

                var positive = pitch > 0f;
                if (i * dt > settle)
                {
                    if (counting && positive != wasPositive) crossings++;
                    counting = true;
                }

                wasPositive = positive;
            }

            return crossings < 2 ? -1f : 2f * (seconds - settle) / crossings;
        }

        var walkPeriod = MeasurePeriod(PlayerBody.WalkSpeed);
        var sneakPeriod = MeasurePeriod(PlayerBody.SneakSpeed);

        if (peakStride < 0.5f) faults.Add($"walking legs only reached {peakStride:F2} rad");

        if (walkPeriod < 0f || sneakPeriod < 0f)
        {
            faults.Add("legs never crossed zero over six seconds of walking");
        }
        else
        {
            if (walkPeriod is not (> 0.45f and < 0.85f))
                faults.Add($"stride period {walkPeriod:F2}s at walking pace, wanted 0.45-0.85");

            // The one that actually discriminates. A stride driven by a clock rather than by
            // distance gives the same period at every speed, and the absolute check above cannot
            // tell the difference — it passed a clock-driven walk cycle whose period happened to
            // land inside the band. Creeping at a third of walking pace has to take three times
            // as long per stride, or the feet are skating.
            var ratio = sneakPeriod / walkPeriod;
            var expected = PlayerBody.WalkSpeed / PlayerBody.SneakSpeed;
            if (MathF.Abs(ratio - expected) > expected * 0.15f)
                faults.Add($"stride slowed by {ratio:F2}x when creeping, wanted {expected:F2}x — phase is not distance-driven");
        }

        // Turning on the spot: the head leads and the shoulders only follow once it runs out of
        // neck. A body welded to the camera passes nothing here; one that never turns fails the
        // second half.
        var turner = new PlayerAnimator();
        turner.Reset(0f);
        for (var i = 0; i < 120; i++) turner.Update(dt, still, 90f, PlayerBody.WalkSpeed, false, false);

        var relative = MathF.Abs(PlayerAnimator.Wrap(90f - turner.BodyYawDegrees));
        if (relative < 1f) faults.Add("body followed the head exactly instead of lagging it");
        if (relative > 61f) faults.Add($"head twisted {relative:F0} degrees off the body");

        // And walking off in that direction brings the shoulders round the rest of the way.
        var walkTurn = still;
        for (var i = 0; i < 120; i++)
        {
            walkTurn.Z += PlayerBody.WalkSpeed * dt;
            turner.Update(dt, walkTurn, 90f, PlayerBody.WalkSpeed, false, false);
        }

        if (MathF.Abs(PlayerAnimator.Wrap(90f - turner.BodyYawDegrees)) > 2f)
            faults.Add($"walking left the body at {turner.BodyYawDegrees:F0} instead of 90");

        return faults;
    }
}
