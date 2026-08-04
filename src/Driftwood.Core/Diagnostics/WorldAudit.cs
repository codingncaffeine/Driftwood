using System.Diagnostics;
using System.Numerics;
using System.Text;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Gen;
using Driftwood.Core.Lighting;
using Driftwood.Core.Meshing;
using Driftwood.Core.Physics;
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
        Parallel.For(
            0,
            positions.Count,
            () => new ChunkMesher(registry, tinter),
            (i, _, mesher) =>
            {
                meshes[i] = mesher.Build(world, positions[i]);
                quadCounts[i] = mesher.LastQuadCount;
                coveredFaces[i] = mesher.LastCoveredFaces;
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

        long totalQuads = 0, totalCovered = 0, totalNaive = 0;
        var mismatchedChunks = 0;
        for (var i = 0; i < positions.Count; i++)
        {
            totalQuads += quadCounts[i];
            totalCovered += coveredFaces[i];
            totalNaive += naiveFaces[i];
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
        sb.AppendLine($"merging       {totalNaive:N0} visible faces -> {totalQuads:N0} quads ({totalNaive / (double)Math.Max(totalQuads, 1):F2}x fewer)");
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
        // A material nobody can find is a material that does not exist, and this is the check that
        // says so. It was written after the world turned out to hold two ores and no stone variants
        // at all — every block registered and textured, half of them nowhere in the ground. Anything
        // that is only ever built rather than dug is named here on purpose, so adding a block and
        // forgetting to place it fails rather than quietly joining the list.
        string[] craftOnly = ["air", "driftoak_planks"];
        var missing = new List<string>();
        for (ushort id = 0; id < registry.Count; id++)
        {
            var name = registry[id].Name;
            if (counts[id] > 0 || Array.IndexOf(craftOnly, name) >= 0) continue;
            missing.Add(name);
        }

        Check("every material is in the world", missing.Count == 0,
            missing.Count == 0
                ? $"{registry.Count - craftOnly.Length} of {registry.Count} blocks generate; {string.Join(", ", craftOnly[1..])} are built, not dug"
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
        var snowfall = SampleSurface(world, minBlock, maxBlock, ids.Snow.Value, ids.Grass.Value);
        var snowPct = snowfall.Total > 0 ? snowfall.Snow * 100.0 / snowfall.Total : 0;
        var lift = snowfall.MeanSnowY - snowfall.MeanGrassY;

        Check(
            "snow lies high and cold",
            // The floor is barely a floor on purpose: "snow exists at all" is already owned by the
            // material census above, and a warm seed whose only snow is on its highest peaks is
            // right rather than broken — seed 'stonebreak' comes out at 0.8% and should.
            snowPct is > 0.1 and < 60.0 && lift > 4.0 && maxY[ids.Snow.Value] >= maxY[ids.Grass.Value],
            $"{snowPct:F1}% of open ground, mean y {snowfall.MeanSnowY:F1} against grass at "
            + $"{snowfall.MeanGrassY:F1} (want at least 4 higher), tops out at y {maxY[ids.Snow.Value]}");

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

        // Warm the code paths on a throwaway copy first. The removal half only runs when there is
        // light to take away, so its first execution lands on whichever edit happens to darken
        // something — and that edit then wears the JIT cost as if it were propagation. Measured
        // cold, one edit reported 2.45 ms over 440 cells: five microseconds a cell, which is not
        // a plausible price for six array reads.
        {
            var warm = BuildRegion(seed, registry, ids, oceanCoverage, radius);
            var warmEngine = new LightEngine(registry);
            warmEngine.LightAll(warm);
            foreach (var (x, y, z, block, _) in edits)
            {
                warm.SetBlock(x, y, z, block);
                warmEngine.UpdateBlock(warm, x, y, z);
            }
        }

        worstMs = 0;
        var worstWhat = "none";
        var worstCells = 0L;
        long totalCells = 0;
        var watch = new Stopwatch();

        foreach (var (x, y, z, block, what) in edits)
        {
            world.SetBlock(x, y, z, block);

            watch.Restart();
            engine.UpdateBlock(world, x, y, z);
            watch.Stop();

            totalCells += engine.LastCellsVisited;

            var ms = watch.Elapsed.TotalMilliseconds;
            if (ms > worstMs)
            {
                worstMs = ms;
                worstWhat = what;
                worstCells = engine.LastCellsVisited;
            }

            if (Matches(world, seed, registry, ids, oceanCoverage, radius, edits, what, out detail)) continue;
            return false;
        }

        // Naming the worst edit and its cell count, not just its time: "slow" and "large" are
        // different problems and the fix for one does nothing for the other.
        detail = $"{edits.Count} edits over {totalCells:N0} cells, worst '{worstWhat}' "
               + $"at {worstMs:F2} ms over {worstCells:N0} cells";
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
    private static (int Snow, int Total, double MeanSnowY, double MeanGrassY) SampleSurface(
        VoxelWorld world, int minBlock, int maxBlock, ushort snow, ushort grass)
    {
        long snowSum = 0, grassSum = 0;
        int snowCount = 0, grassCount = 0;

        for (var z = minBlock; z <= maxBlock; z += 4)
        for (var x = minBlock; x <= maxBlock; x += 4)
        {
            for (var y = TerrainGenerator.WorldHeight - 1; y >= 0; y--)
            {
                var id = world.GetBlock(x, y, z).Value;
                if (id == 0) continue;

                if (id == snow) { snowSum += y; snowCount++; }
                else if (id == grass) { grassSum += y; grassCount++; }

                break;
            }
        }

        return (
            snowCount,
            snowCount + grassCount,
            snowCount > 0 ? snowSum / (double)snowCount : 0,
            grassCount > 0 ? grassSum / (double)grassCount : 0);
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
