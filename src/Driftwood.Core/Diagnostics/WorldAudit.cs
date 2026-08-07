using System.Diagnostics;
using System.Numerics;
using System.Text;
using Driftwood.Core.Audio;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Gen;
using Driftwood.Core.Items;
using Driftwood.Core.Lighting;
using Driftwood.Core.Meshing;
using Driftwood.Core.Particles;
using Driftwood.Core.Physics;
using Driftwood.Core.Saves;
using Driftwood.Core.Settings;
using Driftwood.Core.Sky;
using Driftwood.Core.Spatial;
using Driftwood.Core.Textures;
using Driftwood.Core.Ui;
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

        // The item layer, exactly as the game builds it. Everything below that asks what a block
        // leaves, how long it takes with a tool, or whether a recipe is reachable reads these two
        // rather than a table written for the audit — a check against a second construction of the
        // same tables is a check that both were written the same way, not that either is right.
        var items = StarterItems.Register(registry);
        var drops = StarterItems.Drops(registry, items);
        var creatureDrops = StarterItems.Creatures(items);

        var generator = new TerrainGenerator(seed, ids, oceanCoverage);
        var world = new VoxelWorld(registry);

        var half = chunksAcross / 2;

        var positions = new List<ChunkPos>(chunksAcross * chunksAcross * TerrainGenerator.ChunksTall);
        for (var cy = TerrainGenerator.ChunkBottom; cy < TerrainGenerator.ChunkTop; cy++)
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

        // ⛔ Rock BY HEIGHT, because "percent of rock" needs a denominator that covers the same
        // cells as its numerator. An ore that only forms between y 4 and y 58 measured against every
        // rock in a 384-tall world is not a rate, it is a rate divided by how deep the world happens
        // to be — and the day the world got three times deeper, six perfectly correct ore bands went
        // red at once and none of them had moved a block.
        // Rock, and everything that replaced rock — an ore or a gravel pocket occupies a cell the
        // vein could have been rolled into, so it belongs in the denominator as much as the stone
        // beside it does.
        var isRock = new bool[registry.Count];
        foreach (var block in ids.Rock) isRock[block.Value] = true;
        foreach (var block in ids.Ores) isRock[block.Value] = true;
        isRock[ids.Gravel.Value] = true;

        var rockByY = new long[TerrainGenerator.WorldHeight];

        // The same census split at y 0, because the world has two undergrounds now and one number
        // describes neither. Deepstone is a minority intrusion above the line and the whole of the
        // rock below it; a single share reads as "correct" for a world that is either.
        var shallowCounts = new long[registry.Count];
        var deepCounts = new long[registry.Count];

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

                if (isRock[id]) rockByY[wy - TerrainGenerator.WorldBottom]++;
                if (wy >= TerrainGenerator.DeepFloor) shallowCounts[id]++; else deepCounts[id]++;
            }
        }

        long RockBetween(int low, int high)
        {
            long total = 0;
            var from = Math.Max(low, TerrainGenerator.WorldBottom);
            var to = Math.Min(high, TerrainGenerator.WorldTop - 1);
            for (var y = from; y <= to; y++) total += rockByY[y - TerrainGenerator.WorldBottom];
            return total;
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
        Check("bedrock floor",
            counts[ids.Bedrock.Value] > 0 && maxY[ids.Bedrock.Value] == TerrainGenerator.WorldBottom,
            $"y {minY[ids.Bedrock.Value]}..{maxY[ids.Bedrock.Value]} (want {TerrainGenerator.WorldBottom})");

        // ⛳ Air in the EMBERDEEP, not near y 2. The old floor is 256 blocks up now, so a check that
        // still asked about y 2 would pass on a world with no deep in it at all — and it cannot ask
        // about the very bottom either, because the very bottom is molten and air down there would
        // mean the core had holes in it.
        Check("caves opened", minY[0] <= TerrainGenerator.EmberdeepTop,
            $"lowest air at y {minY[0]} (want at or under {TerrainGenerator.EmberdeepTop})");

        // The molten floor, which is the reason to go down at all. Both halves: it has to reach the
        // bedrock, or there is no core — and it must not reach the ordinary underground, or a player
        // digging a straight shaft from their front door lands in it.
        var lavaLow = counts[ids.Lava.Value] > 0 ? minY[ids.Lava.Value] : 0;
        var lavaHigh = counts[ids.Lava.Value] > 0 ? maxY[ids.Lava.Value] : 0;

        Check("the world has a molten floor",
            counts[ids.Lava.Value] > 0
            && lavaLow <= TerrainGenerator.WorldBottom + 30
            && lavaHigh < TerrainGenerator.HollowsTop,
            $"lava {counts[ids.Lava.Value]:N0} blocks, y {lavaLow}..{lavaHigh} "
            + $"(want down to {TerrainGenerator.WorldBottom + 30} and no higher than {TerrainGenerator.HollowsTop})");
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

        var verticalOk = VerticalStreamingPays(seed, registry, ids, oceanCoverage, out var verticalDetail);
        Check("the deep costs less than the surface", verticalOk, verticalDetail);

        var fluidFaults = FluidFaults(registry, ids, out var fluidDetail);
        Check("fluid flows, settles, and drains", fluidFaults.Count == 0,
            fluidFaults.Count == 0 ? fluidDetail : string.Join("; ", fluidFaults));

        var spawnFaults = SpawnBandFaults(out var spawnDetail);
        Check("where a thing lives takes two questions", spawnFaults.Count == 0,
            spawnFaults.Count == 0 ? spawnDetail : string.Join("; ", spawnFaults));

        var shelfFaults = PackShelfFaults(out var shelfDetail);
        Check("a pack can be put on the shelf and named", shelfFaults.Count == 0,
            shelfFaults.Count == 0 ? shelfDetail : string.Join("; ", shelfFaults));

        var tierFaults = ToolTierColourFaults(out var tierDetail);
        Check("a tool is the colour of what it is made of", tierFaults.Count == 0,
            tierFaults.Count == 0 ? tierDetail : string.Join("; ", tierFaults));

        var fireFaults = FireFaults(registry, ids, out var fireDetail);
        Check("things that burn put fire and smoke in the air", fireFaults.Count == 0,
            fireFaults.Count == 0 ? fireDetail : string.Join("; ", fireFaults));

        var passFaults = TranslucentPassFaults(registry, ids, out var passDetail);
        Check("water is meshed into a pass of its own", passFaults.Count == 0,
            passFaults.Count == 0 ? passDetail : string.Join("; ", passFaults));

        var shoreFaults = ShoreFaults(seed, registry, ids, oceanCoverage, out var shoreDetail);
        Check("breaking a block beside the sea fills it", shoreFaults.Count == 0,
            shoreFaults.Count == 0 ? shoreDetail : string.Join("; ", shoreFaults));

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

        var itemFaults = ItemSelfTest(registry, items, drops, ids);
        Check("what breaks can be picked up", itemFaults.Count == 0,
            itemFaults.Count == 0
                ? $"drops land, merge and collect; {Inventory.Slots} slots of {ItemStack.MaxCount} lose nothing"
                : $"{itemFaults.Count} faults: {itemFaults[0]}");

        var catalogueFaults = ItemCatalogueSelfTest(registry, items, drops);
        Check("every item is a thing in the world", catalogueFaults.Count == 0,
            catalogueFaults.Count == 0
                ? $"{items.Count - 1} items over {registry.Count} blocks, "
                  + $"{drops.BlocksLeavingNothing} of them leaving nothing, every icon painted"
                : $"{catalogueFaults.Count} faults: {catalogueFaults[0]}");

        var book = StarterRecipes.Build(items);

        var recipeFaults = RecipeSelfTest(registry, items, book);
        Check("recipes match what they are made of", recipeFaults.Count == 0,
            recipeFaults.Count == 0
                ? $"{book.Recipes.Count} recipes and {book.Smelting.Count} smelts, each laid back "
                  + "into a grid and found again, none duplicated, none bigger than a bench"
                : $"{recipeFaults.Count} faults: {recipeFaults[0]}");

        // ⛔ What a square SAYS, which is a different claim from where it is drawn. The ui-check can
        // see that a box appeared; only this can see that it named the right thing, and it is the
        // half that goes wrong silently — a tooltip saying "pocket" over every square looks exactly
        // like one that works from any screenshot.
        // ⛔ And which world a launch opens, which was wrong and invisible: --play saves on quit and
        // with no --world fell through to the same name a double-click opens, so every timing run
        // loaded somebody's world, played in it and wrote it back.
        var namingFaults = WorldSave.ValidateNaming();
        Check("an instrument never opens somebody's own world", namingFaults.Count == 0,
            namingFaults.Count == 0
                ? $"a bare launch opens '{WorldSave.DefaultWorld}', a seed names its own, and every "
                  + $"instrument works in '{WorldSave.TestWorld}' unless a name is typed"
                : $"{namingFaults.Count} faults: {namingFaults[0]}");

        var tipFaults = Tooltip.Validate(items, book);
        Check("hovering a thing says what it is", tipFaults.Count == 0,
            tipFaults.Count == 0
                ? "every special square names what it is for, the pockets stay quiet when empty, a "
                  + "tool gives its tier and its wear, and a recipe says what it costs"
                : $"{tipFaults.Count} faults: {tipFaults[0]}");

        var reach = ReachabilitySelfTest(registry, items, drops, creatureDrops, book, counts, out var reachDetail);
        Check("everything is reachable from bare hands", reach.Count == 0,
            reach.Count == 0 ? reachDetail : $"{reach.Count} faults: {reach[0]}");

        var unlockFaults = UnlockSelfTest(registry, items, book, out var unlockDetail);
        Check("a new recipe announces itself once", unlockFaults.Count == 0,
            unlockFaults.Count == 0 ? unlockDetail : $"{unlockFaults.Count} faults: {unlockFaults[0]}");

        var settingsFaults = SettingsSelfTest(out var settingsDetail);
        Check("settings survive a round trip", settingsFaults.Count == 0,
            settingsFaults.Count == 0 ? settingsDetail : $"{settingsFaults.Count} faults: {settingsFaults[0]}");

        var fontFaults = FontSelfTest(out var fontDetail);
        Check("every letter is drawn and distinct", fontFaults.Count == 0,
            fontFaults.Count == 0 ? fontDetail : $"{fontFaults.Count} faults: {fontFaults[0]}");

        var joinFaults = ConnectionSelfTest(registry, out var joinDetail);
        Check("things that join up find their neighbours", joinFaults.Count == 0,
            joinFaults.Count == 0 ? joinDetail : $"{joinFaults.Count} faults: {joinFaults[0]}");

        var supportFaults = SupportSelfTest(registry, items, drops, out var supportDetail);
        Check("what is held up comes down with its wall", supportFaults.Count == 0,
            supportFaults.Count == 0 ? supportDetail : $"{supportFaults.Count} faults: {supportFaults[0]}");

        var lampFaults = CraftedLightSelfTest(registry, out var lampDetail);
        Check("light can be built, and blocked", lampFaults.Count == 0,
            lampFaults.Count == 0 ? lampDetail : $"{lampFaults.Count} faults: {lampFaults[0]}");

        var saveFaults = SaveSelfTest(registry, items, book, out var saveDetail);
        Check("a world survives being written down", saveFaults.Count == 0,
            saveFaults.Count == 0 ? saveDetail : $"{saveFaults.Count} faults: {saveFaults[0]}");

        var loadFaults = LoadIntoStreamerSelfTest(
            seed, registry, items, book, ids, oceanCoverage, out var loadDetail);
        Check("a loaded world is still the world the generator makes", loadFaults.Count == 0,
            loadFaults.Count == 0 ? loadDetail : $"{loadFaults.Count} faults: {loadFaults[0]}");

        var backupFaults = BackupSelfTest(registry, items, book, out var backupDetail);
        Check("the last few states of a world are kept beside it", backupFaults.Count == 0,
            backupFaults.Count == 0 ? backupDetail : $"{backupFaults.Count} faults: {backupFaults[0]}");

        var chestFaults = ChestSelfTest(items, out var chestDetail);
        Check("a chest keeps what it is given", chestFaults.Count == 0,
            chestFaults.Count == 0 ? chestDetail : $"{chestFaults.Count} faults: {chestFaults[0]}");

        var furnaceFaults = FurnaceSelfTest(items, book, out var furnaceDetail);
        Check("a furnace burns only when it has work", furnaceFaults.Count == 0,
            furnaceFaults.Count == 0 ? furnaceDetail : $"{furnaceFaults.Count} faults: {furnaceFaults[0]}");

        var dialectFaults = PackDialectSelfTest(out var dialectDetail);
        Check("a pack of either kind is read as itself", dialectFaults.Count == 0,
            dialectFaults.Count == 0 ? dialectDetail : $"{dialectFaults.Count} faults: {dialectFaults[0]}");

        var layoutFaults = ScreenLayoutSelfTest(out var layoutDetail);
        Check("a click lands on the square it is over", layoutFaults.Count == 0,
            layoutFaults.Count == 0 ? layoutDetail : $"{layoutFaults.Count} faults: {layoutFaults[0]}");

        var pocketFaults = PocketsSelfTest(items, book, out var pocketDetail);
        Check("moving things about never loses one", pocketFaults.Count == 0,
            pocketFaults.Count == 0 ? pocketDetail : $"{pocketFaults.Count} faults: {pocketFaults[0]}");

        var vitalsFaults = VitalsSelfTest(registry, ids, out var vitalsDetail);
        Check("falls, water and lava cost health", vitalsFaults.Count == 0,
            vitalsFaults.Count == 0
                ? $"{PlayerVitals.SafeFall:F0} blocks free then a half-heart each, "
                  + $"{PlayerVitals.MaxBreath / 60}s of breath, rest heals; {vitalsDetail}"
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

        var placementFaults = PlacementSelfTest(registry, items);
        Check("placement picks the right form", placementFaults.Count == 0,
            placementFaults.Count == 0
                ? $"{Placeables(items)} things that can be put down, every face, height and heading"
                : $"{placementFaults.Count} faults: {placementFaults[0]}");

        // A material nobody can find is a material that does not exist, and this is the check that
        // says so. It was written after the world turned out to hold two ores and no stone variants
        // at all — every block registered and textured, half of them nowhere in the ground. Anything
        // that is only ever built rather than dug is named here on purpose, so adding a block and
        // forgetting to place it fails rather than quietly joining the list.
        var missing = new List<string>();
        var crafted = 0;
        var derived = 0;
        for (ushort id = 1; id < registry.Count; id++)
        {
            if (registry[id].Crafted) { crafted++; continue; }

            // A flowing fluid level exists only while something is flowing. It is neither dug nor
            // built, so a census of terrain will not find it and is not the right thing to ask.
            if (registry[id].Derived) { derived++; continue; }

            if (counts[id] > 0) continue;
            missing.Add(registry[id].Name);
        }

        Check("every material is in the world", missing.Count == 0,
            missing.Count == 0
                ? $"{registry.Count - crafted - derived - 1} of {registry.Count - 1} blocks generate; "
                  + $"{crafted} are built, {derived} only ever flow"
                : $"never generated: {string.Join(", ", missing)}");

        // Ore gets a band, not a floor. Too little and mining never gates progression; too much
        // and it stops being a reward. A floor-only check passes a world where one stone block
        // in fifty is coal, which is how the first calibration pass slipped through.
        //
        // ⛔ EACH ORE AGAINST THE ROCK IN ITS OWN BAND, and the denominator is the whole check.
        // "Percent of all rock" was measuring a rate divided by how deep the world happens to be:
        // the day it went from 128 cells tall to 384, six correct ore bands went red at once and not
        // one vein had moved. The bands come from the generator's own table rather than from numbers
        // restated here, so a band that changes in one place cannot go on passing in the other.
        var bands = TerrainGenerator.OreBands(ids);
        var rates = new Dictionary<string, double>();

        foreach (var band in bands)
        {
            var denominator = RockBetween(band.Low, band.High);
            var pct = denominator == 0 ? 0 : counts[band.Ore.Value] * 100.0 / denominator;
            rates[band.Name] = pct;
        }

        // ⛳ The observed depths must lie inside the declared ones, or the table above is describing
        // a generator that no longer exists — and a table that claims a wider band than the code
        // produces makes every denominator too big and every rate quietly too low.
        var strayed = bands
            .Where(b => counts[b.Ore.Value] > 0
                     && (minY[b.Ore.Value] < b.Low || maxY[b.Ore.Value] > b.High))
            .Select(b => $"{b.Name} found at y {minY[b.Ore.Value]}..{maxY[b.Ore.Value]}, declared {b.Low}..{b.High}")
            .ToArray();

        Check("ore bands are what the generator says", strayed.Length == 0,
            strayed.Length == 0
                ? $"{bands.Length} ores, every one inside its declared depths"
                : string.Join("; ", strayed));

        var coalPct = rates["coal"];
        var copperPct = rates["copper"];
        var ironPct = rates["iron"];
        var goldPct = rates["gold"];
        var azuritePct = rates["azurite"];
        var stormglassPct = rates["stormglass"];

        Check("coal rate in band", coalPct is > 0.30 and < 1.60, $"{coalPct:F3}% of rock in band (want 0.30-1.60)");
        Check("copper rate in band", copperPct is > 0.20 and < 1.20, $"{copperPct:F3}% of rock in band (want 0.20-1.20)");
        Check("iron rate in band", ironPct is > 0.15 and < 0.90, $"{ironPct:F3}% of rock in band (want 0.15-0.90)");
        Check("gold rate in band", goldPct is > 0.04 and < 0.40, $"{goldPct:F3}% of rock in band (want 0.04-0.40)");
        Check("azurite rate in band", azuritePct is > 0.03 and < 0.38, $"{azuritePct:F3}% of rock in band (want 0.03-0.38)");
        Check("stormglass rate in band", stormglassPct is > 0.015 and < 0.20, $"{stormglassPct:F3}% of rock in band (want 0.015-0.20)");

        // ⛳ HOW IT ARRIVES, not just how much of it there is — and a rate cannot answer this at all.
        // "One block every two hundred and fifty" and "a vein of eight every two thousand" are the
        // same percentage and are nothing alike to play: the first is a trickle nobody notices and
        // the second is the moment a tunnel was dug for. Every rate band above passes either way.
        var veins = VeinSizes(chunks, registry, ids);
        var veinFaults = new List<string>();

        foreach (var band in bands)
        {
            if (!veins.TryGetValue(band.Ore.Value, out var size) || size.Count == 0) continue;

            // ⚠ WIDE, AND ONE END OF IT IS KNOWINGLY GENEROUS. A seam of two is speckle nobody
            // notices digging past; a seam of fifty is a room. Both extremes are what this catches.
            //
            // ⛔ Coal measures 35 with a tail to 650 and it is ABOVE the genre, which runs a handful
            // to a dozen. That is not a bug in this check — it is what noise thresholding produces,
            // and TerrainGenerator.OreAt has said so since P0: "Noise thresholding gives blobs but
            // only indirect control over how much ore exists. P4 replaces this with explicit vein
            // placement." It never did. The band is set to hold the current shape as a REGRESSION
            // gate rather than to bless it, because changing it moves every vein in every existing
            // world and that is the user's call, not this file's.
            if (size.Mean is < 2.0 or > 45.0)
                veinFaults.Add($"{band.Name} comes in veins of {size.Mean:F1}");
        }

        // ⚠ The whole table either way. A fault that names only the ore that broke leaves the reader
        // guessing whether it is an outlier or the shape of the whole set.
        var veinReport = string.Join(", ", bands
            .Where(b => veins.ContainsKey(b.Ore.Value))
            .Select(b => $"{b.Name} {veins[b.Ore.Value].Mean:F1}/{veins[b.Ore.Value].Largest}"));

        Check("ore comes in seams, not speckle", veinFaults.Count == 0,
            (veinFaults.Count == 0 ? "" : string.Join("; ", veinFaults) + " — ")
            + veinReport + " mean/largest blocks a vein (want a mean of 2-45)");

        // The ladder, which is the check the individual bands cannot make. Every tier could sit
        // inside its own band and still come out in the wrong order, and the order is the whole
        // point: what makes stormglass worth going deep for is that it is rarer than everything
        // above it, not that it is rare in absolute terms.
        var deepest = Math.Min(goldPct, azuritePct);
        var ladder = coalPct > copperPct && copperPct > ironPct
                  && ironPct > Math.Max(goldPct, azuritePct) && deepest > stormglassPct;

        Check("the ore ladder holds", ladder,
            $"coal {coalPct:F2} > copper {copperPct:F2} > iron {ironPct:F2} > gold {goldPct:F2}/azurite {azuritePct:F2} > stormglass {stormglassPct:F3}");

        // Rock variety, asked twice because the world now has two undergrounds and one number
        // cannot describe both. Above y 0 the failure is a uniform grey cavern nobody can navigate;
        // below it the failure is the opposite — a deep that is not made of anything in particular.
        double shallowRock = RockBetween(TerrainGenerator.DeepFloor, TerrainGenerator.WorldTop - 1);
        double deepRock = RockBetween(TerrainGenerator.WorldBottom, TerrainGenerator.DeepFloor - 1);

        double Shallow(BlockId block) => shallowRock == 0 ? 0 : shallowCounts[block.Value] * 100.0 / shallowRock;

        var deepPct = deepRock == 0 ? 0 : deepCounts[ids.Deepstone.Value] * 100.0 / deepRock;
        Check("the deep is deepstone", deepPct > 80.0,
            $"{deepPct:F1}% of rock below y {TerrainGenerator.DeepFloor} (want over 80)");

        var shallowDeepPct = Shallow(ids.Deepstone);
        Check("deepstone owns the depths", shallowDeepPct is > 3.0 and < 50.0 && maxY[ids.Deepstone.Value] < 30,
            $"{shallowDeepPct:F1}% of rock above y 0 (want 3-50), top at y {maxY[ids.Deepstone.Value]} (want under 30)");

        var intrusions = new[] { ids.Coralstone, ids.Driftstone, ids.Saltstone }
            .Select(b => (Name: registry[b].Name, Pct: Shallow(b))).ToArray();

        Check("intrusions break up the rock", Array.TrueForAll(intrusions, i => i.Pct is > 1.0 and < 12.0),
            string.Join(", ", intrusions.Select(i => $"{i.Name} {i.Pct:F2}%")) + " of rock above y 0 (want 1-12 each)");

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
        // ⛔ EVERY KIND, NOT TWO OF THEM. Naming the two that existed when this was written would
        // have gone on passing after the dye tree added another two — and a colour whose flower
        // never grows is a whole branch of the recipe tree nobody can reach, which reads from a
        // console exactly like a world that works. The four are asked for by name, off ids.Flowers,
        // so a fifth is a row in that array and not an edit here.
        var barren = ids.Flowers.Where(f => counts[f.Value] == 0).ToList();
        var bloomCounts = string.Join(
            ", ", ids.Flowers.Select(f => $"{counts[f.Value]:N0} {registry[f].Name}"));

        Check(
            "meadows carry flowers",
            flowers > 0 && flowerPct is > 2.0 and < 25.0 && barren.Count == 0,
            $"{flowers:N0} blooms, {flowerPct:F1}% of ground cover (want 2-25), {bloomCounts}");

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

        var emberPct = rates["emberstone"];
        Check("emberstone rate in band", emberPct is > 0.05 and < 0.60,
            $"{emberPct:F3}% of rock in band (want 0.05-0.60)");

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

        var swimFaults = SwimSelfTest(registry, ids, out var swimDetail);
        Check("a body swims rather than drowning where it lands", swimFaults.Count == 0,
            swimFaults.Count == 0 ? swimDetail : string.Join("; ", swimFaults));

        var shapeFaults = ShapeCollisionSelfTest(registry, items, ids, out var shapeDetail);
        Check("a body collides with the shape, not the cell", shapeFaults.Count == 0,
            shapeFaults.Count == 0
                ? shapeDetail
                : $"{shapeFaults.Count} faults: {string.Join("; ", shapeFaults)}");

        var toolShapeFaults = ToolShapeAudit(out var toolShapeDetail);
        Check("the four tools are four different drawings", toolShapeFaults.Count == 0,
            toolShapeFaults.Count == 0
                ? toolShapeDetail
                : $"{toolShapeFaults.Count} faults: {string.Join("; ", toolShapeFaults)}");

        var iconFaults = IconStyleAudit(items, out var iconDetail);
        Check("every item is drawn the way it should be", iconFaults.Count == 0,
            iconFaults.Count == 0
                ? iconDetail
                : $"{iconFaults.Count} faults: {string.Join("; ", iconFaults)}");

        var typingFaults = TextFieldSelfTest(out var typingDetail);
        Check("a typed line takes what it should and refuses the rest", typingFaults.Count == 0,
            typingFaults.Count == 0
                ? typingDetail
                : $"{typingFaults.Count} faults: {string.Join("; ", typingFaults)}");

        var climbFaults = ClimbSelfTest(registry, out var climbDetail);
        Check("a ladder is climbed, not walked past", climbFaults.Count == 0,
            climbFaults.Count == 0 ? climbDetail : $"{climbFaults.Count} faults: {climbFaults[0]}");

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

        // ⛔ What the user reported, and nothing here could have caught either half of it: a held
        // tool drawn as a cube wearing its picture on all six faces — so it read as two tools two
        // thirds of a block apart, pointed the wrong way — and in third person not drawn at all.
        var spriteFaults = ValidateSprites();
        Check("a flat item is a solid, not a pair of pictures", spriteFaults.Count == 0,
            spriteFaults.Count == 0
                ? $"{TileGen.ToolShapes.Length + 1} silhouettes walked, walls counted against edges"
                : $"{spriteFaults.Count} faults: {spriteFaults[0]}");

        // ⛳ The creature skeletons come off the user's own install and cannot be checked here — but
        // the READER can, against one sample of each format it has already been wrong about.
        var geometryFaults = BedrockGeometry.Validate();
        Check("a creature skeleton reads back the way it was written", geometryFaults.Count == 0,
            geometryFaults.Count == 0
                ? "both formats, inheritance across files, bind pose kept, anything else ignored quietly"
                : $"{geometryFaults.Count} faults: {geometryFaults[0]}");

        // ⛔ And then it has to stand up. None of what this asks would throw, come back empty or show
        // in a count of bones — a skeleton meshed wrong is an animal in a heap, which reads as a
        // rendering problem long after it stopped being one.
        var meshFaults = CreatureMesh.Validate();
        Check("a creature stands up from its skeleton", meshFaults.Count == 0,
            meshFaults.Count == 0
                ? "torso on its legs and behind its head, bind pose not carried down, a ring facing one way"
                : $"{meshFaults.Count} faults: {meshFaults[0]}");

        // ⛔ And it has to be the right skeleton. An install carries the same creature modelled
        // several times over, and the bare name is the oldest of them — a flat list of bones from
        // before there were skeletons, which nothing in the file can pose.
        // ⛔ And then it has to stand somewhere. A herd that spawns inside a hill, sinks through the
        // floor or walks through walls looks from any console exactly like one that works.
        // ⛔ And then it has to be able to be hit. Damage, flight and death are three claims that all
        // read as "nothing happened" from a console, so every one of them is paired with a control:
        // a swing turned away must miss, a swing that reaches too little must miss, a second blow
        // inside the cooldown must not land, and a death must be reported exactly once.
        var herdFaults = CreatureHerd.Validate();
        Check("a herd stands, walks, falls, and can be fought", herdFaults.Count == 0,
            herdFaults.Count == 0
                ? "6 on a plain, none through a wall in 15s; a dug-out pillar drops one 20 blocks in "
                  + "1.2s and hurts it; a blow lands, is refused inside its cooldown, turns it away, "
                  + "and the last one kills it once"
                : $"{herdFaults.Count} faults: {herdFaults[0]}");

        // ⛔ The creatures that SHIP. Everything above tests what can be read off somebody else's
        // disk; this tests what is in the box, which is the only art most players will ever see.
        var ourCreatures = StarterCreatures.Validate();
        Check("our own creatures stand up", ourCreatures.Count == 0,
            ourCreatures.Count == 0
                ? $"{StarterCreatures.All.Count} models, nets sound, assembled, feet on the ground"
                : $"{ourCreatures.Count} faults: {ourCreatures[0]}");

        // And what they leave, which is the half the reachability walk cannot check on its own:
        // wool comes off a dead sheep as well as a live one, so a shear gate that had been forgotten
        // entirely would still leave wool reachable. Asked directly, with three things in hand.
        var creatureDropFaults = CreatureDrops.Validate(items, creatureDrops);
        Check("an animal gives up what it should, and only how it should", creatureDropFaults.Count == 0,
            creatureDropFaults.Count == 0
                ? string.Join("; ", creatureDrops.Describe())
                : $"{creatureDropFaults.Count} faults: {creatureDropFaults[0]}");

        // ⛳ The 2012 grid, which is a table of cell numbers and therefore a table that can be wrong
        // in the one way nothing notices: two layers reading one cell imports cleanly, paints every
        // tile and puts the wrong picture on one block.
        var atlasFaults = PackAtlas.Validate();
        Check("the 2012 atlas table reads one cell each", atlasFaults.Count == 0,
            atlasFaults.Count == 0
                ? $"{PackAtlas.Mapped} of {StarterBlocks.LayerCount} layers can come off a "
                  + $"{TexturePack.AtlasCells}x{TexturePack.AtlasCells} grid, no cell twice"
                : $"{atlasFaults.Count} faults: {atlasFaults[0]}");

        // ⛳ Armour, which is where the leather those animals drop finally goes. The table and the
        // registered items are two statements of the same twenty numbers and nothing else in the
        // game would notice them disagreeing — a helmet worth the chestplate's points looks exactly
        // like working armour from every screen there is.
        var armourFaults = Armour.Validate(items);
        Check("armour turns aside what the table says", armourFaults.Count == 0,
            armourFaults.Count == 0
                ? $"{Armour.Materials.Length} materials x {Armour.Pieces.Length} pieces and "
                  + $"{Armour.Shields.Length} shields, best set {Armour.MaxPoints} points turning a "
                  + $"blow of 10 into {Armour.Survive(10, Armour.MaxPoints)}, "
                  + $"{Armour.Survive(10, Armour.MaxPoints, Armour.Shields[^1].Share)} behind the best "
                  + $"shield, and {Armour.Survive(10, 0)} in a shirt"
                : $"{armourFaults.Count} faults: {armourFaults[0]}");

        // And the other half of it: the plates are geometry hung off the body's own joints, wearing
        // sheets painted in code. ⛔ Neither half can be seen from the other — a suit whose numbers
        // are right and whose boots are painted up the whole leg is a player in iron trousers.
        var plateFaults = ArmourModel.Validate();
        Check("worn armour stands off the body", plateFaults.Count == 0,
            plateFaults.Count == 0
                ? $"{ArmourModel.Build().Length} plates over 4 slots, outer at {ArmourModel.Outer} "
                  + $"and leggings under it at {ArmourModel.Inner}, every net on the sheet, wound outward"
                : $"{plateFaults.Count} faults: {plateFaults[0]}");

        var armourArtFaults = ArmourArt.Validate();
        Check("armour is painted where it is worn", armourArtFaults.Count == 0,
            armourArtFaults.Count == 0
                ? $"{Armour.Materials.Length} materials x 2 sheets at {ArmourArt.Width}x{ArmourArt.Height}, "
                  + "boots at the foot and not the knee, leggings at the knee and not the foot"
                : $"{armourArtFaults.Count} faults: {armourArtFaults[0]}");

        var artFaults = CreatureArt.Validate();
        Check("our own creatures are painted", artFaults.Count == 0,
            artFaults.Count == 0
                ? "every patch its net names, a face on the front of each head, no two the same colour"
                : $"{artFaults.Count} faults: {artFaults[0]}");

        var matchFaults = CreatureSet.Validate();
        Check("a creature wears the skeleton its art was painted for", matchFaults.Count == 0,
            matchFaults.Count == 0
                ? "two sheets pick two skeletons, jointed over flat, v2 over v1.8, never a namesake"
                : $"{matchFaults.Count} faults: {matchFaults[0]}");

        var mipFaults = MipChain.Validate();
        Check("a cut-out keeps its colour as it shrinks", mipFaults.Count == 0,
            mipFaults.Count == 0
                ? "weighted halving is brighter than flat on foliage and identical on rock"
                : $"{mipFaults.Count} faults: {mipFaults[0]}");

        var gripFaults = HeldGrip.Validate();
        Check("what is held stays in the fist", gripFaults.Count == 0,
            gripFaults.Count == 0
                ? "a tool and a block, classic and slim, at four points through a swing"
                : $"{gripFaults.Count} faults: {gripFaults[0]}");

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

        var miningFaults = MiningSelfTest(registry, items, ids);
        var woodPick = items.ByName("wood_pickaxe");
        Check("mining takes the right work", miningFaults.Count == 0,
            miningFaults.Count == 0
                ? $"soft ground {MiningRules.SecondsToBreak(registry[ids.Dirt], null):F2}s, "
                  + $"stone {MiningRules.SecondsToBreak(registry[ids.Stone], null):F2}s by hand and "
                  + $"{MiningRules.SecondsToBreak(registry[ids.Stone], woodPick):F2}s with the first pickaxe, "
                  + "bedrock never. The ladder, each ore with the cheapest pickaxe that takes it: "
                  + string.Join(", ", new[]
                    {
                        ("coal_ore", "wood_pickaxe"), ("iron_ore", "stone_pickaxe"),
                        ("gold_ore", "copper_pickaxe"), ("stormglass_ore", "iron_pickaxe"),
                        ("diamond_ore", "stormglass_pickaxe"),
                    }.Select(pair =>
                        $"{pair.Item1.Replace("_ore", "")} "
                        + $"{MiningRules.SecondsToBreak(registry.ByName(pair.Item1), items.ByName(pair.Item2)):F2}s"))
                  + $"; one rung under is {MiningRules.SecondsToBreak(registry.ByName("diamond_ore"), items.ByName("iron_pickaxe")):F0}s "
                  + "and two is refused"
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

        var compared = 0;

        for (var cz = -radius; cz <= radius; cz++)
        for (var cx = -radius; cx <= radius; cx++)
        for (var cy = TerrainGenerator.ChunkBottom; cy < TerrainGenerator.ChunkTop; cy++)
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

        for (var cz = -radius; cz <= radius; cz++)
        for (var cx = -radius; cx <= radius; cx++)
        for (var cy = TerrainGenerator.ChunkBottom; cy < TerrainGenerator.ChunkTop; cy++)
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
    /// Breaks one of everything, drops it on a floor, and walks a collector into the pile.
    /// </summary>
    /// <remarks>
    /// <para>The one thing an inventory must never do is lose something. Everything else here is a
    /// nicety by comparison: a stack that falls through the floor is visible, a pool that leaks
    /// shows up in a frame time, but a pickup that quietly swallows what would not fit looks
    /// exactly like a pickup that worked and is only ever noticed as "I'm sure I mined more than
    /// that".</para>
    /// <para>The merge is checked because without it one felled tree is forty entities, each with
    /// its own physics and its own draw call, and the frame rate says so long before anybody counts
    /// them.</para>
    /// </remarks>
    private static List<string> ItemSelfTest(
        BlockRegistry registry, ItemRegistry items, BlockDrops drops, StarterBlocks.Ids ids)
    {
        var faults = new List<string>();
        const float Step = 1f / 60f;

        var stone = items.ByName("rubble").Id;      // what stone actually leaves
        var log = items.ByName("driftoak_log").Id;
        var dirt = items.ByName("dirt").Id;
        var sand = items.ByName("sand").Id;

        // Every block leaves a sensible number of something, or an honest nothing.
        var leaveNothing = 0;
        for (ushort id = 1; id < registry.Count; id++)
        {
            var left = drops.Of(new BlockId(id));
            if (left.IsEmpty) { leaveNothing++; continue; }
            if (left.Count >= 1) continue;
            faults.Add($"'{registry[id].Name}' drops {left.Count} of something");
        }

        if (leaveNothing == 0) faults.Add("every block in the world leaves something, including the foliage");
        if (leaveNothing > registry.Count / 2) faults.Add($"{leaveNothing} of {registry.Count} blocks leave nothing");

        // The tier gate, both ways round. A bare hand breaks stone and keeps none of it; the first
        // wooden pickaxe keeps it. Checking only the second half passes a build with no gate at all.
        var bareStone = drops.Harvest(registry[ids.Stone], null);
        var pickStone = drops.Harvest(registry[ids.Stone], items.ByName("wood_pickaxe"));
        var shovelStone = drops.Harvest(registry[ids.Stone], items.ByName("iron_shovel"));
        var pickDirt = drops.Harvest(registry[ids.Dirt], null);

        if (!bareStone.IsEmpty) faults.Add("stone taken bare-handed still left something");
        if (pickStone.IsEmpty) faults.Add("stone taken with a wooden pickaxe left nothing");
        if (!shovelStone.IsEmpty) faults.Add("stone taken with a shovel left something, so the class is not read");
        if (pickDirt.IsEmpty) faults.Add("dirt taken bare-handed left nothing, so ordinary digging is gated");

        // ⛳ AND THE LADDER, WHICH IS A LADDER OF TIME NOW RATHER THAN OF DROPS. The rule the user
        // asked for: one rung under still brings the ore up, it just takes far longer; TWO rungs
        // under will not move the rock at all. Both halves are asserted and the middle one is what
        // the old check got wrong — it insisted a copper pickaxe left no stormglass, which is the
        // behaviour that was replaced.
        var deepGem = registry.ByName("stormglass_ore");

        if (drops.Harvest(deepGem, items.ByName("iron_pickaxe")).IsEmpty)
            faults.Add("an iron pickaxe left no stormglass");

        if (drops.Harvest(deepGem, items.ByName("copper_pickaxe")).IsEmpty)
            faults.Add("a copper pickaxe left no stormglass — one rung under is meant to be slow, not empty");

        if (!drops.Harvest(deepGem, items.ByName("stone_pickaxe")).IsEmpty)
            faults.Add("a stone pickaxe brought up stormglass, which is two rungs above it");

        // ⛔ And the refusal itself, which is not a drop rule and would not show up in one. Two rungs
        // under has to be infinite rather than merely long: a very long wait is a player standing
        // there wearing a pickaxe out on a rock that will never give.
        if (!MiningRules.TooHard(deepGem, items.ByName("stone_pickaxe")))
            faults.Add("a stone pickaxe is allowed to work at stormglass, two rungs above it");
        if (MiningRules.TooHard(deepGem, items.ByName("copper_pickaxe")))
            faults.Add("a copper pickaxe is refused at stormglass, which is only one rung above it");

        var world = new VoxelWorld(registry);
        for (var z = -4; z <= 4; z++)
        for (var x = -4; x <= 4; x++)
        for (var y = 0; y <= 10; y++)
            world.SetBlock(x, y, z, ids.Stone);

        // A stack thrown out of a cell has to land on the floor rather than through it.
        var ground = new DroppedItems(registry, items, 0x515A6E);
        ground.Drop(new ItemStack(stone, 1), new Vector3(0.5f, 12.5f, 0.5f));
        for (var i = 0; i < 240; i++) ground.Update(world, Step, null, null);

        if (ground.Count != 1) faults.Add($"{ground.Count} stacks on the ground after dropping one");
        else if (ground.Live[0].Position.Y < 11f - 0.01f)
            faults.Add($"a dropped stack reached y {ground.Live[0].Position.Y:F2}, under the floor at 11");

        // Stacks lying together become one.
        var heap = new DroppedItems(registry, items, 0x4D3267);
        for (var i = 0; i < 12; i++) heap.Drop(new ItemStack(log, 1), new Vector3(0.5f, 11.6f, 0.5f), scatter: 0.05f);
        for (var i = 0; i < 240; i++) heap.Update(world, Step, null, null);

        if (heap.Count >= 12) faults.Add($"{heap.Count} stacks of 12 dropped together never merged");
        var carried = 0;
        foreach (var item in heap.Live) carried += item.Stack.Count;
        if (carried != 12) faults.Add($"merging 12 logs left {carried}");

        // And a collector standing in them ends up with all of it.
        var pockets = new Inventory(items);
        var walker = new Vector3(0.5f, 11.9f, 0.5f);
        for (var i = 0; i < 600 && heap.Count > 0; i++) heap.Update(world, Step, walker, pockets);

        if (heap.Count != 0) faults.Add($"{heap.Count} stacks were left on the ground after standing in them");
        if (pockets.CountOf(log) != 12)
            faults.Add($"walking into 12 logs collected {pockets.CountOf(log)}");

        // A full inventory refuses rather than losing the overflow.
        var full = new Inventory(items);
        for (var i = 0; i < Inventory.Slots; i++) full.Add(new ItemStack(stone, ItemStack.MaxCount));

        var over = full.Add(new ItemStack(dirt, 5));
        if (over.Count != 5) faults.Add($"a full inventory swallowed {5 - over.Count} of 5 it had no room for");
        if (full.CountOf(stone) != Inventory.Slots * ItemStack.MaxCount)
            faults.Add($"a full inventory holds {full.CountOf(stone)}, not {Inventory.Slots * ItemStack.MaxCount}");

        // Partial stacks fill before empty slots open.
        var tidy = new Inventory(items);
        tidy.Add(new ItemStack(sand, 10));
        tidy.Add(new ItemStack(sand, 10));

        if (tidy.Used != 1) faults.Add($"twenty sand went into {tidy.Used} slots instead of one");
        if (tidy.CountOf(sand) != 20) faults.Add($"twenty sand came to {tidy.CountOf(sand)}");

        // A thing that wears out takes a slot to itself however many are picked up.
        var toolbelt = new Inventory(items);
        var axe = items.ByName("stone_axe").Id;
        toolbelt.Add(new ItemStack(axe, 1));
        toolbelt.Add(new ItemStack(axe, 1));
        if (toolbelt.Used != 2) faults.Add($"two axes went into {toolbelt.Used} slots, so wear has nowhere to live");

        // And wearing it through empties the slot rather than leaving a broken one in it.
        var wearing = new Inventory(items);
        wearing.Add(new ItemStack(axe, 1));
        var life = items[wearing.Held.Item].Durability;
        var broke = false;
        for (var i = 0; i < life + 4 && !broke; i++) broke = wearing.WearHeld();

        if (!broke) faults.Add($"an axe survived {life + 4} uses of a claimed {life}");
        else if (!wearing.Held.IsEmpty) faults.Add("a tool that broke left something in the slot");

        // Placing spends exactly one.
        var spender = new Inventory(items);
        spender.Add(new ItemStack(dirt, 3));
        spender.SpendHeld();
        if (spender.CountOf(dirt) != 2) faults.Add($"placing one of three left {spender.CountOf(dirt)}");
        spender.SpendHeld();
        spender.SpendHeld();
        if (!spender.Held.IsEmpty) faults.Add("spending the last one left something in hand");

        // Paying for a craft takes from the smallest stacks and takes exactly what it asked for.
        var purse = new Inventory(items);
        purse.Add(new ItemStack(dirt, 40));
        purse.Add(new ItemStack(sand, 3));
        purse.Add(new ItemStack(dirt, 5));

        if (purse.Take(dirt, 7) != 7) faults.Add("taking seven of forty-five got a different number");
        if (purse.CountOf(dirt) != 38) faults.Add($"taking seven of forty-five left {purse.CountOf(dirt)}");
        if (purse.Take(sand, 9) != 3) faults.Add("taking nine of three claimed more than there was");
        if (purse.CountOf(sand) != 0) faults.Add("taking more than there was left some behind");

        // The moves a screen makes with a cursor. Every one of them can lose something, and losing
        // something is the only bug in an inventory nobody forgives — so each is checked by
        // counting what went in against what came out rather than by looking at where it landed.
        var bench = new Inventory(items);
        bench.Add(new ItemStack(dirt, 20));

        var lifted = bench.TakeAll(0);
        if (lifted.Count != 20) faults.Add($"lifting a slot of 20 got {lifted.Count}");
        if (bench.CountOf(dirt) != 0) faults.Add("lifting a whole slot left something in it");

        var back = bench.PutInto(20, lifted);
        if (!back.IsEmpty) faults.Add($"putting 20 into an empty backpack slot returned {back.Count}");
        if (bench.CountOf(dirt) != 20) faults.Add($"20 into an empty slot came to {bench.CountOf(dirt)}");
        if (Inventory.InHotbar(20)) faults.Add("slot 20 counts as the bar, which it is not");

        // Half, rounded up, so splitting one gives the one.
        var half = bench.TakeHalf(20);
        if (half.Count != 10 || bench.CountOf(dirt) != 10)
            faults.Add($"splitting 20 gave {half.Count} and left {bench.CountOf(dirt)}");

        var single = new Inventory(items);
        single.Add(new ItemStack(sand, 1));
        if (single.TakeHalf(0).Count != 1) faults.Add("splitting a single left the player holding nothing");

        // Different things swap rather than one eating the other.
        var swapper = new Inventory(items);
        swapper.Add(new ItemStack(dirt, 5));
        var displaced = swapper.PutInto(0, new ItemStack(sand, 3));
        if (displaced.Item.Value != dirt.Value || displaced.Count != 5)
            faults.Add("dropping sand on dirt did not hand the dirt back");
        if (swapper.CountOf(sand) != 3) faults.Add("the sand did not land");

        // A full slot refuses the overflow instead of swallowing it.
        var brimming = new Inventory(items);
        brimming.Add(new ItemStack(stone, ItemStack.MaxCount));
        var refused = brimming.PutInto(0, new ItemStack(stone, 10));
        if (refused.Count != 10) faults.Add($"a full slot swallowed {10 - refused.Count} of 10");

        // Shift-click moves between the bar and the backpack and never off the end of the world.
        var sweeper = new Inventory(items);
        sweeper.Add(new ItemStack(stone, 30));
        var whereFrom = sweeper.CountOf(stone);
        if (!sweeper.Sweep(0)) faults.Add("sweeping a full bar slot moved nothing");
        if (sweeper.CountOf(stone) != whereFrom) faults.Add("sweeping lost some of the stack");
        if (sweeper[0].Count != 0) faults.Add("sweeping left the slot it came from occupied");

        var landed = 0;
        for (var i = Inventory.HotbarSlots; i < Inventory.Slots; i++) landed += sweeper[i].Count;
        if (landed != 30) faults.Add($"sweeping the bar put {landed} of 30 in the backpack");

        // And back again.
        var home = 0;
        for (var i = Inventory.HotbarSlots; i < Inventory.Slots; i++)
            if (!sweeper[i].IsEmpty) { sweeper.Sweep(i); break; }
        for (var i = 0; i < Inventory.HotbarSlots; i++) home += sweeper[i].Count;
        if (home == 0) faults.Add("sweeping out of the backpack put nothing back in the bar");

        // A backpack with nowhere to go keeps what it has rather than dropping it.
        var stuffed = new Inventory(items);
        for (var i = Inventory.HotbarSlots; i < Inventory.Slots; i++)
            stuffed.PutInto(i, new ItemStack(stone, ItemStack.MaxCount));
        stuffed.PutInto(0, new ItemStack(sand, 4));

        var beforeSweep = stuffed.CountOf(sand);
        stuffed.Sweep(0);
        if (stuffed.CountOf(sand) != beforeSweep)
            faults.Add("sweeping into a full backpack lost the stack");

        // The bar is the front of the same array, so a pickup fills it before the backpack.
        var reach = new Inventory(items);
        reach.Add(new ItemStack(dirt, 1));
        if (reach[0].IsEmpty) faults.Add("a pickup went into the backpack while the bar was empty");

        // The ground refuses past its end rather than growing.
        var flooded = new DroppedItems(registry, items, 0x7E11);
        for (var i = 0; i < DroppedItems.Capacity + 40; i++)
            flooded.Drop(new ItemStack(stone, 1), new Vector3(i * 4f, 40f, 0f));

        if (flooded.Count != DroppedItems.Capacity)
            faults.Add($"the ground holds {flooded.Count}, not the {DroppedItems.Capacity} it claims");
        if (flooded.Refused == 0) faults.Add("the ground never refused a drop, so it grew instead");

        return faults;
    }

    /// <summary>
    /// Lays every recipe back into a grid and asks the book what it makes.
    /// </summary>
    /// <remarks>
    /// <para>The round trip is the check. A recipe table and a matcher are two descriptions of the
    /// same rule written in different shapes, and nothing about either says whether they agree —
    /// a recipe with its rows the wrong way up, a matcher that trims the grid wrong, an off-by-one
    /// in the mirror all leave a table full of recipes and a game where nothing can be made.</para>
    /// <para>It also catches duplicates for free, and better than comparing signatures does: if two
    /// recipes match the same arrangement, the second one laid out comes back as the first, and the
    /// failure names both.</para>
    /// </remarks>
    private static List<string> RecipeSelfTest(
        BlockRegistry registry, ItemRegistry items, RecipeBook book)
    {
        var faults = new List<string>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var grid = new ItemStack[9];

        foreach (var recipe in book.Recipes)
        {
            if (recipe.Width > 3 || recipe.Height > 3)
            {
                faults.Add($"'{recipe.Name}' is {recipe.Width}x{recipe.Height}, past the biggest bench");
                continue;
            }

            if (recipe.SlotsUsed == 0) faults.Add($"'{recipe.Name}' costs nothing");
            if (recipe.Result.IsEmpty) faults.Add($"'{recipe.Name}' makes nothing");

            // ⚠ Two recipes matching one arrangement is a fault at a GRID station and the whole
            // design at a choosing one — a stonecutter offering a rock's slab, its stair and its
            // worked form is three recipes with one signature on purpose. So the check is scoped
            // per station rather than over the book, which is also what stops a stonecutter recipe
            // colliding with the bench recipe it was moved off.
            var signature = $"{recipe.Station}:{recipe.Signature()}";
            if (CraftStations.IsGrid(recipe.Station))
            {
                if (seen.TryGetValue(signature, out var other))
                    faults.Add($"'{recipe.Name}' is laid out exactly like '{other}'");
                else
                    seen[signature] = recipe.Name;
            }

            // A choosing station has no arrangement to round-trip. What has to be true instead is
            // that it takes exactly one thing and that asking the station for that thing offers it.
            if (CraftStations.Chooses(recipe.Station))
            {
                if (recipe.SlotsUsed != 1)
                {
                    faults.Add($"'{recipe.Name}' is worked at a {recipe.Station} and takes {recipe.SlotsUsed} things");
                    continue;
                }

                var only = recipe.Ingredients.First().Members[0];
                if (!book.Offers(recipe.Station, only).Contains(recipe))
                    faults.Add($"a {recipe.Station} was not offered '{recipe.Name}' for the thing it is made of");

                // ⛔ And that it is refused everywhere else. Without this the gate is a field
                // nobody reads and every recipe still matches wherever it was laid.
                Array.Clear(grid);
                grid[0] = new ItemStack(only, 1);
                if (book.TryMatch(grid, 3, 3, CraftStation.Bench, out var leaked))
                    faults.Add($"'{recipe.Name}' is worked at a {recipe.Station} and a bench made '{leaked!.Name}'");

                continue;
            }

            // Laid into the corner of a full bench, so the trim has something to do. A recipe that
            // only matches when it fills the grid exactly is a recipe a player has to guess at.
            Array.Clear(grid);
            for (var y = 0; y < recipe.Height; y++)
            for (var x = 0; x < recipe.Width; x++)
            {
                if (recipe.At(x, y) is not { } want) continue;
                grid[y * 3 + x] = new ItemStack(want.Members[0], 1);
            }

            if (!book.TryMatch(grid, 3, 3, CraftStation.Bench, out var made))
            {
                faults.Add($"'{recipe.Name}' laid out in a bench matched nothing");
                continue;
            }

            if (made != recipe)
                faults.Add($"'{recipe.Name}' laid out in a bench came back as '{made!.Name}'");

            // And the same arrangement in the two slots a player carries, which must work for the
            // small ones worked in the hand and must not for anything else.
            if (recipe.TooBigForHands || recipe.Station != CraftStation.Hand) continue;

            Array.Clear(grid);
            for (var y = 0; y < recipe.Height; y++)
            for (var x = 0; x < recipe.Width; x++)
            {
                if (recipe.At(x, y) is not { } want) continue;
                grid[y * 2 + x] = new ItemStack(want.Members[0], 1);
            }

            if (!book.TryMatch(grid.AsSpan(0, 4), 2, 2, CraftStation.Hand, out _))
                faults.Add($"'{recipe.Name}' fits in two by two and did not match there");
        }

        // A bench is wanted for one of two reasons and both have to be readable off the recipe: it
        // does not fit in two hands, or it is worked at one whatever its size.
        foreach (var recipe in book.Recipes)
        {
            var big = recipe.Width > 2 || recipe.Height > 2;
            var wants = big || recipe.Station == CraftStation.Bench;
            if (recipe.NeedsBench == wants) continue;
            faults.Add($"'{recipe.Name}' is {recipe.Width}x{recipe.Height} at a {recipe.Station} and "
                     + $"{(recipe.NeedsBench ? "wants" : "does not want")} a bench");
        }

        // ⚠ And that the gate is doing something at all. Every recipe worked in the hand would pass
        // every check above on a build where Station was never read, so the counts are named: how
        // many are made in bare hands, and how many stations have anything to work.
        var byStation = new Dictionary<CraftStation, int>();
        foreach (var recipe in book.Recipes)
            byStation[recipe.Station] = byStation.GetValueOrDefault(recipe.Station) + 1;

        var inHand = 0;
        foreach (var recipe in book.Recipes)
            if (recipe.WorkedAt(CraftStation.Hand, 2)) inHand++;

        if (byStation.Count < 2)
            faults.Add("every recipe in the book is worked at the same place, so the station gate is not used");
        if (inHand == 0) faults.Add("nothing at all can be made in bare hands, so a new world cannot start");
        if (inHand > 12)
            faults.Add($"{inHand} recipes are made in bare hands, which is most of a game before anything is built");

        // A grid holding something no recipe mentions has to make nothing. Without this the whole
        // round trip above would pass a matcher that says yes to everything.
        var mentioned = new HashSet<ushort>();
        foreach (var recipe in book.Recipes)
        foreach (var slot in recipe.Ingredients)
        foreach (var member in slot.Members) mentioned.Add(member.Value);

        var unused = ItemId.None;
        foreach (var item in items.All)
            if (!item.Id.IsNone && !mentioned.Contains(item.Id.Value)) { unused = item.Id; break; }

        if (unused.IsNone)
        {
            faults.Add("every item in the game is a recipe ingredient, so the negative control cannot run");
        }
        else
        {
            Array.Clear(grid);
            grid[4] = new ItemStack(unused, 1);
            if (book.TryMatch(grid, 3, 3, CraftStation.Bench, out var spurious))
                faults.Add($"one {items[unused].Name} in a bench made '{spurious!.Name}'");

            // And a scattering of the right things in the wrong arrangement makes nothing either.
            Array.Clear(grid);
            grid[0] = items.Stack("stick");
            grid[8] = items.Stack("driftoak_planks");
            if (book.TryMatch(grid, 3, 3, CraftStation.Bench, out var crooked))
                faults.Add($"a stick and a plank in opposite corners made '{crooked!.Name}'");
        }

        // Matching is not paying. Every recipe is now bought out of a real inventory holding exactly
        // its cost and nothing else, which is what catches a payment that takes from the wrong slot,
        // takes twice, or takes nothing — none of which the round trip above can see.
        foreach (var recipe in book.Recipes)
        {
            var purse = new Inventory(items);
            var owed = new Dictionary<ushort, int>();

            foreach (var slot in recipe.Ingredients)
            {
                var pick = slot.Members[0];
                owed[pick.Value] = owed.GetValueOrDefault(pick.Value) + 1;
            }

            foreach (var (id, count) in owed) purse.Add(new ItemStack(new ItemId(id), count));

            if (!book.CanPay(purse, recipe))
            {
                faults.Add($"'{recipe.Name}' could not be paid for out of exactly its own ingredients");
                continue;
            }

            if (!book.Craft(purse, recipe, out var made))
            {
                faults.Add($"'{recipe.Name}' matched and then refused to be made");
                continue;
            }

            if (made != recipe.Result)
                faults.Add($"'{recipe.Name}' made {made.Count} of item {made.Item.Value}, not its own result");

            foreach (var (id, count) in owed)
            {
                var left = purse.CountOf(new ItemId(id));
                if (left == 0) continue;
                faults.Add($"'{recipe.Name}' left {left} of {count} {items[new ItemId(id)].Name} unspent");
            }

            // And one short is refused outright rather than half-paid.
            var short1 = new Inventory(items);
            foreach (var (id, count) in owed)
                if (count > 1) short1.Add(new ItemStack(new ItemId(id), count - 1));

            if (!book.CanPay(short1, recipe)) continue;
            faults.Add($"'{recipe.Name}' claimed to be payable one ingredient short");
        }

        _ = registry;
        return faults;
    }

    /// <summary>
    /// Starts a player in this world with nothing and works out what they could eventually hold.
    /// </summary>
    /// <remarks>
    /// <para>The one check the whole content phase rests on. Every other question about a recipe
    /// set — is it balanced, is it interesting, does it read — is a matter of taste; whether the
    /// last item in it can be obtained at all is not, and it is invisible. A tier written one rung
    /// too high, an ore that generates nowhere, a smelt whose only fuel is behind the thing it
    /// smelts: each of those leaves a game that looks complete and has a dead end in it.</para>
    /// <para>It seeds from the <em>census of this world</em>, not from the block table. A block that
    /// is registered and never generated cannot start anybody off, and the difference between those
    /// two is exactly the mistake worth catching.</para>
    /// <para>The walk is a fixed point over four ways to gain something: dig it with what you have,
    /// craft it, smelt it, or gain a tool that unlocks more digging. They feed each other — the
    /// first pickaxe is crafted from dug wood and unlocks the rock the second is made of — so it
    /// runs to exhaustion rather than in passes.</para>
    /// </remarks>
    /// <summary>What has to be standing before a station's recipes can be worked, or none.</summary>
    /// <remarks>
    /// Named here rather than on <see cref="CraftStation"/> because it is a fact about this game's
    /// item set, not about the idea of a station — and it is the one place the walk and the world
    /// have to agree. A station whose block does not exist yet returns null and its recipes are
    /// treated as reachable, which is honest: nothing is gated behind something unbuildable.
    /// </remarks>
    private static ItemId? StationItem(ItemRegistry items, CraftStation station)
    {
        var name = station switch
        {
            CraftStation.Bench => "bench",
            CraftStation.Stonecutter => "stonecutter",
            CraftStation.Smithing => "smithing_table",
            CraftStation.Loom => "loom",
            _ => null,
        };

        if (name is null) return null;
        return items.TryByName(name, out var type) ? type.Id : null;
    }

    private static List<string> ReachabilitySelfTest(
        BlockRegistry registry,
        ItemRegistry items,
        BlockDrops drops,
        CreatureDrops creatures,
        RecipeBook book,
        long[] counts,
        out string detail)
    {
        var faults = new List<string>();
        var have = new HashSet<ushort>();

        // The best tier of each tool class currently obtainable. Index by ToolClass.
        var reach = new int[Enum.GetValues<ToolClass>().Length];
        for (var i = 0; i < reach.Length; i++) reach[i] = -1;      // -1 = no tool of this class

        bool Held(ItemId item) => have.Contains(item.Value);

        bool Gain(ItemId item)
        {
            if (item.IsNone || !have.Add(item.Value)) return false;

            var type = items[item];
            if (type.IsTool && type.Tier > reach[(int)type.Tool]) reach[(int)type.Tool] = type.Tier;
            return true;
        }

        var rounds = 0;
        bool changed;
        do
        {
            changed = false;
            rounds++;

            // Dig. A block has to be somewhere in this world, and the hand or tool has to reach its
            // tier — which is why gaining a pickaxe opens this loop up again.
            for (ushort id = 1; id < registry.Count; id++)
            {
                if (counts[id] == 0) continue;

                var block = registry[id];
                if (block.Unbreakable) continue;
                if (block.HarvestTier > 0 && reach[(int)block.HarvestClass] < block.HarvestTier) continue;

                var left = drops.Of(block.Id);
                if (!left.IsEmpty) changed |= Gain(left.Item);
            }

            // Hunt. ⛔ A creature is a source exactly as a block is, and the gate has the same two
            // halves: it has to actually be in the world, and whatever takes the drop off it has to
            // be in hand. The world half is "do we ship a model for it" — a kind that only appears on
            // a machine with somebody else's install is not something a walk may assume, which is why
            // the hostiles' bone and string stay unreachable until they are placed rather than
            // quietly making every recipe that needs them look fine.
            foreach (var (kind, item, tool, most) in creatures.Walk())
            {
                if (most <= 0 || StarterCreatures.ByName(kind) is null) continue;
                if (tool != ToolClass.None && reach[(int)tool] < 0) continue;
                changed |= Gain(item);
            }

            // ⛳ Draw. The fifth source, after dig, hunt, craft and smelt, and it has the same two
            // halves every other one has: the fluid has to actually exist in this world, and the
            // thing that carries it has to be in hand. A bucket of water is not a recipe and not a
            // drop; without this the walk calls it unobtainable, and the day something is crafted
            // FROM one it would call that unobtainable too and be right for the wrong reason.
            foreach (var (source, filled) in new[]
                     {
                         ("water", "water_bucket"),
                         ("lava", "lava_bucket"),
                     })
            {
                if (counts[registry.ByName(source).Id.Value] == 0) continue;
                if (!Held(items.ByName("bucket").Id)) continue;
                changed |= Gain(items.ByName(filled).Id);
            }

            // Craft. ⚠ A recipe worked at a station cannot be made until the station itself has been
            // reached — which is the whole reason the gate exists, and without this line the walk
            // would report every worked stone in the game as obtainable by a player who has never
            // built a stonecutter. The station is an item like any other, so it is simply asked for.
            foreach (var recipe in book.Recipes)
            {
                if (StationItem(items, recipe.Station) is { } needed && !Held(needed)) continue;

                var payable = true;
                foreach (var slot in recipe.Ingredients)
                {
                    var any = false;
                    foreach (var member in slot.Members) any |= Held(member);
                    if (any) continue;
                    payable = false;
                    break;
                }

                if (payable) changed |= Gain(recipe.Result.Item);
            }

            // Smelt, which costs a second thing: something to burn.
            var anyFuel = false;
            foreach (var type in items.All) anyFuel |= type.IsFuel && Held(type.Id);

            if (!anyFuel) continue;

            foreach (var recipe in book.Smelting)
            {
                var any = false;
                foreach (var member in recipe.Input.Members) any |= Held(member);
                if (any) changed |= Gain(recipe.Result.Item);
            }
        }
        while (changed && rounds < 64);

        if (rounds >= 64) faults.Add("the reachability walk never settled, so something is cyclic");

        var unreachable = new List<string>();
        foreach (var type in items.All)
        {
            if (type.Id.IsNone || Held(type.Id)) continue;
            unreachable.Add(type.Name);
        }

        foreach (var name in unreachable)
            faults.Add($"'{name}' cannot be obtained by anyone starting with nothing");

        // A recipe nobody can pay for is a row in a table that never runs. Reported separately from
        // an unreachable item because the cause is different — the result may well be reachable
        // another way, and the dead ingredient is the thing to look at.
        foreach (var recipe in book.Recipes)
        foreach (var slot in recipe.Ingredients)
        {
            var any = false;
            foreach (var member in slot.Members) any |= Held(member);
            if (!any) faults.Add($"'{recipe.Name}' needs {slot.Name}, which nobody can get");
        }

        foreach (var recipe in book.Smelting)
        {
            var any = false;
            foreach (var member in recipe.Input.Members) any |= Held(member);
            if (!any) faults.Add($"'{recipe.Name}' takes {recipe.Input.Name}, which nobody can get");
        }

        // The positive control. A walk that starts with everything would report everything reachable
        // whatever the recipes said, so the seed is checked for being a seed: bare hands must open
        // some of the world and must not open all of it.
        var byHand = 0;
        var gated = 0;
        for (ushort id = 1; id < registry.Count; id++)
        {
            if (counts[id] == 0 || registry[id].Unbreakable) continue;
            if (registry[id].HarvestTier > 0) gated++; else byHand++;
        }

        if (byHand == 0) faults.Add("bare hands can dig nothing that generates, so nobody could start");
        if (gated == 0) faults.Add("nothing that generates is behind a tool, so the ladder is not gating anything");

        var toolSteps = 0;
        foreach (var tier in reach) if (tier > 0) toolSteps++;

        detail = $"{have.Count} of {items.Count - 1} items in {rounds} rounds from {byHand} blocks "
               + $"a hand can take, past {gated} it cannot, over {toolSteps} tool classes";

        return faults;
    }

    /// <summary>
    /// Feeds a furnace, starves it, and holds a full one under a burning fire.
    /// </summary>
    /// <remarks>
    /// <para>Every fault worth catching here is one that only shows up over time and looks like
    /// nothing while it is happening. A furnace that burns fuel with an empty top loses a player's
    /// coal in a way they will blame on the coal; one that never spends fuel is a free furnace; one
    /// that smelts into a full output slot destroys what it made and reports nothing.</para>
    /// <para>The starving case is the positive control for the burning case, and it is the reason
    /// both are here: a build that never lights at all would pass "it does not burn when idle" on
    /// its own.</para>
    /// </remarks>
    /// <summary>
    /// That a pack of either layout is recognised, and that every layer knows where the other one
    /// keeps it.
    /// </summary>
    /// <remarks>
    /// <para>Two packs of the same game are two different formats, and a <c>.mcpack</c> opening as a
    /// zip is the easy quarter of it: no <c>assets/</c>, no namespace, folders in the plural, and a
    /// third of the files under names that never went through the 2018 rename. All of that is one
    /// translation table, and a translation table with a hole in it fails by <em>silently keeping
    /// our own art</em> — the exact shape of failure a texture importer cannot afford, because it is
    /// indistinguishable from a pack that simply did not ship that texture.</para>
    /// <para>So both halves are checked: the table is walked for every layer, and a pack of each
    /// kind is built on disk and opened. The on-disk half turns on <see cref="TexturePack.Faults"/>
    /// against <see cref="TexturePack.Missing"/> — a file written as junk bytes is <em>found and
    /// unreadable</em> if the path resolved and <em>missing</em> if it did not, which is what tells
    /// "we looked in the right place" from "we looked in the wrong one" without needing an encoder.
    /// </para>
    /// </remarks>
    private static List<string> PackDialectSelfTest(out string detail)
    {
        var faults = new List<string>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var translated = 0;
        var renamed = 0;

        foreach (var layer in BlockTextureSet.Layers)
        {
            if (layer.PackPath.Length == 0) continue;

            var candidates = PackLayouts.Legacy(layer.PackPath).ToList();

            if (candidates.Count == 0)
            {
                faults.Add($"'{layer.Name}' ({layer.PackPath}) has nowhere to look on an old pack");
                continue;
            }

            translated++;

            // Counted on the FILE, not the path. Every path differs — the folder is plural over
            // there — so "how many changed" was 75 of 75 and said nothing about whether the rename
            // table does anything at all.
            if (!Path.GetFileName(candidates[0]).Equals(
                    Path.GetFileName(layer.PackPath), StringComparison.OrdinalIgnoreCase))
                renamed++;

            foreach (var other in candidates)
            {
                if (other.Contains("//") || other.StartsWith('/') || !other.EndsWith(".png"))
                    faults.Add($"'{layer.Name}' would look at a malformed path '{other}'");

                if (!other.StartsWith("textures/", StringComparison.Ordinal))
                    faults.Add($"'{layer.Name}' would look outside textures/, at '{other}'");
            }

            // Two of our layers landing on one of their files would draw both with one picture, and
            // nothing downstream would ever mention it. Compared on the first candidate, which is
            // the one that decides the answer when both exist.
            if (seen.TryGetValue(candidates[0], out var already))
                faults.Add($"'{layer.Name}' and '{already}' both look at '{candidates[0]}'");
            else
                seen[candidates[0]] = layer.Name;
        }

        // The renames that were actually read out of a real Bedrock pack, written out so gutting the
        // table fails here. Everything above this is satisfied by a table with nothing in it: the
        // folder alone changes on every path, nothing collides, and stone is called stone on both
        // sides. A rename table is only worth having if these six are in it.
        (string Java, string Bedrock)[] measured =
        [
            ("textures/block/oak_log.png", "textures/blocks/log_oak.png"),
            ("textures/block/oak_planks.png", "textures/blocks/planks_oak.png"),
            ("textures/block/oak_leaves.png", "textures/blocks/leaves_oak.png"),
            ("textures/block/grass_block_top.png", "textures/blocks/grass_top.png"),
            ("textures/block/granite.png", "textures/blocks/stone_granite.png"),
            ("textures/block/torch.png", "textures/blocks/torch_on.png"),
            ("textures/item/stick.png", "textures/items/stick.png"),
        ];

        foreach (var (java, bedrock) in measured)
        {
            if (!PackLayouts.Legacy(java).Contains(bedrock, StringComparer.OrdinalIgnoreCase))
                faults.Add(
                    $"'{java}' should look at '{bedrock}' and looks at "
                    + $"'{string.Join(", ", PackLayouts.Legacy(java))}'");
        }

        // And a pack of each kind, on disk, opened for real.
        var root = Path.Combine(Path.GetTempPath(), $"driftwood-dialect-{Environment.ProcessId}");

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bedrock", "textures", "blocks"));
            File.WriteAllText(Path.Combine(root, "bedrock", "manifest.json"), "{\"format_version\":2}");
            File.WriteAllBytes(Path.Combine(root, "bedrock", "textures", "blocks", "stone.png"), [1, 2, 3, 4]);

            Directory.CreateDirectory(Path.Combine(root, "java", "assets", "minecraft", "textures", "block"));
            File.WriteAllText(Path.Combine(root, "java", "pack.mcmeta"), "{\"pack\":{\"pack_format\":34}}");
            File.WriteAllBytes(
                Path.Combine(root, "java", "assets", "minecraft", "textures", "block", "stone.png"), [1, 2, 3, 4]);

            // The third one, and the reason this is a candidate list rather than a translation:
            // pre-flattening Java is the Java shape with the other layout's folder and names.
            Directory.CreateDirectory(Path.Combine(root, "legacy", "assets", "minecraft", "textures", "blocks"));
            File.WriteAllText(Path.Combine(root, "legacy", "pack.mcmeta"), "{\"pack\":{\"pack_format\":3}}");
            File.WriteAllBytes(
                Path.Combine(root, "legacy", "assets", "minecraft", "textures", "blocks", "log_oak.png"),
                [1, 2, 3, 4]);

            Check(Path.Combine(root, "bedrock"), PackDialect.Bedrock, "textures/block/stone.png", "textures/blocks/stone.png");
            Check(Path.Combine(root, "java"), PackDialect.Java, "textures/block/stone.png", "assets/minecraft/textures/block/stone.png");
            Check(Path.Combine(root, "legacy"), PackDialect.JavaLegacy, "textures/block/oak_log.png", "assets/minecraft/textures/blocks/log_oak.png");
        }
        catch (IOException ex)
        {
            faults.Add($"could not build a pack to open: {ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }

        void Check(string path, PackDialect want, string ask, string where)
        {
            using var pack = TexturePack.Open(path);

            if (pack is null)
            {
                faults.Add($"a {want} pack on disk would not open at all");
                return;
            }

            if (pack.Dialect != want) faults.Add($"a {want} pack was read as {pack.Dialect}");

            // Asked for by its modern name whatever the layout, which is the whole point: one
            // column of paths, and one place that knows there is more than one shelf to look on.
            pack.TryLoadTile(ask, 16, out var from);

            if (pack.Faults.Count == 0)
                faults.Add($"a {want} pack was asked for '{ask}' and never reached {where}");
            else if (!string.Equals(from, where, StringComparison.OrdinalIgnoreCase))
                faults.Add($"a {want} pack answered '{ask}' from '{from}' rather than '{where}'");

            // The control. A name nothing could resolve must come back MISSING rather than as a
            // fault — otherwise "found and unreadable" is not actually telling us anything.
            var before = pack.Missing;
            pack.TryLoadTile("textures/block/nothing_is_called_this.png", 16);

            if (pack.Missing == before)
                faults.Add($"a {want} pack reported a texture that cannot exist as something it had");
        }

        detail = $"{translated} layers translate and {renamed} are called something else over there, "
            + $"none colliding, {measured.Length} of them pinned to what a real pack ships; "
            + "a pack of each layout built on disk, recognised, and read from the right folder";

        return faults;
    }

    /// <summary>
    /// That the panel's squares are where the pack's grid puts them, and that a click on one lands
    /// back on the same one.
    /// </summary>
    /// <remarks>
    /// <para><b>The round trip is the point.</b> A screen laid out from one set of numbers and hit
    /// tested from another drifts the first time either is edited, and the symptom is a click
    /// landing on the square next to the picture it was aimed at — which a player finds in seconds
    /// and which nothing in a build log ever mentions. Here every square's own middle is fed back
    /// through the same <see cref="ScreenLayout.At"/> the pointer uses, and it has to come back with
    /// the same role and the same index.</para>
    /// <para>Run at three zooms and three window shapes, because the arithmetic that could be wrong
    /// is the scaling: a layout correct at one unit per panel pixel and wrong at two is a layout
    /// that works on the machine it was written on.</para>
    /// <para>The absolute positions are checked as well as the round trip, against the numbers
    /// measured out of a real pack. A grid that is self-consistent and in the wrong place passes
    /// every round trip there is.</para>
    /// </remarks>
    private static List<string> ScreenLayoutSelfTest(out string detail)
    {
        var faults = new List<string>();
        var layout = new ScreenLayout();
        var tested = 0;
        var zooms = new List<float>();

        // Three window shapes: a short one that can only afford the smallest panel, the default,
        // and a tall one. The zoom each lands on is reported, because a check that silently ran
        // three times at the same zoom is a check that ran once.
        foreach (var (w, h) in new[] { (640f, 360f), (800f, 450f), (960f, 540f) })
        foreach (var (kind, cells) in new[]
                 {
                     (PanelKind.Player, 2), (PanelKind.Bench, 3), (PanelKind.Furnace, 0),
                 })
        {
            layout.BuildPanel(kind, cells, w, h);
            if (!zooms.Contains(layout.Zoom)) zooms.Add(layout.Zoom);

            if (layout.Zoom != MathF.Round(layout.Zoom) || layout.Zoom < 1f)
                faults.Add($"{kind} at {w}x{h} came out at zoom {layout.Zoom}, which is not a whole number");

            // On screen, and not off the bottom of it. A panel laid out past the edge draws
            // perfectly and is invisible, which is the one failure this file exists to catch.
            if (layout.OriginX < 0f || layout.OriginY < 0f
                || layout.X(ScreenLayout.PanelWidth) > w || layout.Y(ScreenLayout.PanelHeight) > h)
                faults.Add(
                    $"{kind} at {w}x{h} spans {layout.OriginX:F0},{layout.OriginY:F0} to "
                    + $"{layout.X(ScreenLayout.PanelWidth):F0},{layout.Y(ScreenLayout.PanelHeight):F0}");

            var seen = new HashSet<(SlotRole, int)>();

            foreach (var zone in layout.Zones)
            {
                if (zone.Kind != ZoneKind.Slot) continue;
                tested++;

                if (!seen.Add((zone.Role, zone.Index)))
                    faults.Add($"{kind} has two {zone.Role} squares numbered {zone.Index}");

                // Sixteen written out, NOT compared against the constant the layout used. Comparing
                // a value against the constant it was computed from is a sentence that restates
                // itself, and this check passed a build with the squares grown to the full pitch
                // until the control run said so.
                if (MathF.Abs(zone.W / layout.Zoom - 16f) > 0.01f)
                    faults.Add(
                        $"{kind} {zone.Role} {zone.Index} is {zone.W / layout.Zoom:F1} panel pixels across, not 16");

                // The middle of the square, back through the pointer's own lookup.
                var hit = layout.At(zone.CentreX, zone.CentreY);
                if (hit is not { } landed)
                {
                    faults.Add($"{kind} {zone.Role} {zone.Index}: its own middle hit nothing");
                    continue;
                }

                if (landed.Role != zone.Role || landed.Index != zone.Index)
                    faults.Add(
                        $"{kind} {zone.Role} {zone.Index}: its own middle hit {landed.Role} {landed.Index}");

                // The gap beside it must answer NOTHING — not "not this square", nothing at all.
                // Sixteen on an eighteen pitch leaves two panel pixels between neighbours, and that
                // gap is load-bearing: it is what a click on the panel between two squares lands on.
                // Probed at its exact middle, one panel pixel out from the edge. Written as "hit
                // nothing" rather than "did not hit me" because the weaker form is satisfied by
                // the neighbour swallowing it, which is precisely the fault.
                var gap = layout.At(zone.X - layout.Zoom, zone.CentreY);
                if (gap is { } spill)
                    faults.Add(
                        $"{kind} {zone.Role} {zone.Index}: the gap beside it answers "
                        + $"{spill.Role} {spill.Index}, so there is no gap");
            }

            // Every panel carries the player's own pockets, all thirty six of them.
            for (var slot = 0; slot < Inventory.Slots; slot++)
                if (layout.Find(SlotRole.Pocket, slot) is null)
                    faults.Add($"{kind} has no square for pocket {slot}");

            var wanted = kind switch
            {
                PanelKind.Player => Inventory.Slots + 4 + 1 + 5,       // pockets, worn, offhand, 2x2 and result
                PanelKind.Bench => Inventory.Slots + 9 + 1,
                _ => Inventory.Slots + 3,
            };

            var squares = 0;
            foreach (var zone in layout.Zones) if (zone.Kind == ZoneKind.Slot) squares++;
            if (squares != wanted) faults.Add($"{kind} laid out {squares} squares, not {wanted}");
        }

        // The absolute grid, against the three sheets it was measured from. Self-consistency is not
        // enough: a panel that agrees with itself and sits four pixels left of where every pack
        // paints it would skin wrong the day one is loaded, and nothing else here would notice.
        layout.BuildPanel(PanelKind.Player, 2, 800f, 450f);
        var z = layout.Zoom;

        Where(SlotRole.Pocket, 0, 8, 142, "the bar");
        Where(SlotRole.Pocket, Inventory.HotbarSlots, 8, 84, "the backpack");
        Where(SlotRole.Equip, 0, 8, 8, "the helmet slot");
        Where(SlotRole.Equip, (int)EquipSlot.Offhand, 77, 62, "the other hand");
        Where(SlotRole.Craft, 0, 98, 18, "the two-by-two");
        Where(SlotRole.Result, 0, 154, 28, "what the hands make");

        layout.BuildPanel(PanelKind.Bench, 3, 800f, 450f);
        Where(SlotRole.Craft, 0, 30, 17, "the three-by-three");
        Where(SlotRole.Result, 0, 124, 35, "what a bench makes");

        layout.BuildPanel(PanelKind.Furnace, 0, 800f, 450f);
        Where(SlotRole.Smelting, 0, 56, 17, "what a furnace works on");
        Where(SlotRole.Fuel, 0, 56, 53, "what a furnace burns");
        Where(SlotRole.Smelted, 0, 116, 35, "what a furnace has finished");

        void Where(SlotRole role, int index, int panelX, int panelY, string what)
        {
            if (layout.Find(role, index) is not { } zone)
            {
                faults.Add($"{what} has no square at all");
                return;
            }

            var wantX = layout.X(panelX);
            var wantY = layout.Y(panelY);
            if (MathF.Abs(zone.X - wantX) > 0.01f || MathF.Abs(zone.Y - wantY) > 0.01f)
                faults.Add(
                    $"{what} is at panel pixel "
                    + $"{(zone.X - layout.OriginX) / layout.Zoom:F1},{(zone.Y - layout.OriginY) / layout.Zoom:F1}, "
                    + $"the pack's sheet puts it at {panelX},{panelY}");
        }

        // And with the book folded out beside it. It hangs to the LEFT, so nothing of it may reach
        // the panel — a book drawn over the pockets is a book covering the thing it exists to fill,
        // and because a recipe zone is added after the squares it would win the hit test as well,
        // silently taking clicks meant for a slot.
        var overlapping = 0;
        var recipes = 0;

        foreach (var (kind, cells) in new[] { (PanelKind.Player, 2), (PanelKind.Bench, 3) })
        {
            layout.BuildPanel(kind, cells, 800f, 450f, bookOut: true, bookPage: 0, bookCount: 40);

            foreach (var zone in layout.Zones)
            {
                if (zone.Kind != ZoneKind.Recipe) continue;
                recipes++;
                if (zone.X + zone.W > layout.OriginX) overlapping++;
            }

            if (layout.BookX + ScreenLayout.BookWidth * layout.Zoom > layout.OriginX)
                faults.Add($"{kind}'s book runs into the panel by "
                    + $"{layout.BookX + ScreenLayout.BookWidth * layout.Zoom - layout.OriginX:F0} units");

            if (layout.BookX < 0f) faults.Add($"{kind}'s book starts {-layout.BookX:F0} units off the left of the screen");
        }

        if (overlapping > 0) faults.Add($"{overlapping} of the book's recipes are laid over the panel");
        if (recipes == 0) faults.Add("the book laid out no recipes at all");

        _ = z;
        detail = $"{tested} squares over 3 panels x 3 window shapes at zooms {string.Join("/", zooms)}, "
            + $"each hit-tested from its own middle and placed against the pack's own grid; "
            + $"{recipes} book entries all clear of it";

        return faults;
    }

    /// <summary>
    /// That picking things up and putting them down never creates or destroys anything.
    /// </summary>
    /// <remarks>
    /// <para>Everything a pointer does to an inventory is a move, and the only thing worth insisting
    /// on is that the total is the same afterwards. Counted over the pockets, the grid and the
    /// cursor together, because the whole class of bug here is a stack that ends up in none of the
    /// three — and a cursor is genuinely nowhere, which is what makes it the easy place to lose one.
    /// </para>
    /// <para>Walked rather than reasoned about: a fixed sequence of the six gestures, then a craft,
    /// then the close, with the total taken before and after each one.</para>
    /// </remarks>
    private static List<string> PocketsSelfTest(ItemRegistry items, RecipeBook book, out string detail)
    {
        var faults = new List<string>();
        var pockets = new Inventory(items);
        var grid = new CraftingGrid(book, items, 2, 2, CraftStation.Hand);
        var carried = ItemStack.Empty;

        var log = items.ByName("driftoak_log").Id;
        var planks = items.ByName("driftoak_planks").Id;

        pockets.Add(new ItemStack(log, 17));
        pockets.Add(new ItemStack(planks, 5));

        var start = Total();
        var steps = 0;

        Step("lift a whole slot", () => carried = pockets.TakeAll(0));
        Step("drop it in the grid", () => carried = grid.Put(0, carried));
        Step("take half of it back", () => carried = grid.TakeHalf(0));
        Step("lay one down", () => carried = pockets.PutOne(20, carried));
        Step("put the rest away", () => carried = pockets.Add(carried));
        Step("send a slot across", () => pockets.Sweep(0));

        // A log in the grid makes planks. Taking the result spends the log and hands over four
        // planks, so the raw count goes UP — which is the one step where a plain conservation check
        // would be wrong, and the reason this section counts each thing separately.
        foreach (var i in Enumerable.Range(0, grid.Cells)) _ = grid.TakeAll(i);
        carried = grid.Put(0, new ItemStack(log, 3));
        if (!carried.IsEmpty) faults.Add("a square would not take three logs");

        var made = grid.TakeResult();
        if (made.IsEmpty) faults.Add("a log in the two-by-two made nothing");
        else if (made.Item.Value != planks.Value)
            faults.Add($"a log in the two-by-two made {items[made.Item].Name}, not planks");

        if (grid[0].Count != 2) faults.Add($"one craft left {grid[0].Count} of 3 logs, not 2");

        // Then the case a one-square recipe cannot test: four squares, and one off EACH of them.
        // A grid that spent only the first square would pass everything above and quietly make
        // benches out of a single plank.
        foreach (var i in Enumerable.Range(0, grid.Cells)) _ = grid.TakeAll(i);
        for (var i = 0; i < 4; i++) _ = grid.Put(i, new ItemStack(planks, 2));

        var bench = grid.TakeResult();
        if (bench.IsEmpty) faults.Add("four planks in the two-by-two made nothing");

        for (var i = 0; i < 4; i++)
            if (grid[i].Count != 1)
                faults.Add($"a four-square craft left {grid[i].Count} of 2 planks in square {i}, not 1");

        // And the close: whatever is on the cursor and whatever is in the grid comes back.
        carried = made;
        var beforeClose = Total();
        var spilled = grid.Empty(pockets);
        var left = pockets.Add(carried);
        carried = ItemStack.Empty;

        var afterClose = Total() + spilled.Sum(s => s.Count) + left.Count;
        if (afterClose != beforeClose)
            faults.Add($"closing turned {beforeClose} things into {afterClose}");

        if (!grid.IsEmpty) faults.Add("the grid still had something in it after being emptied");

        detail = $"{steps} gestures over {start} things, none created or lost; "
            + "one craft spends one of each square and the close hands everything back";

        return faults;

        int Total()
        {
            var total = carried.Count;
            foreach (var slot in pockets.All) total += slot.Count;
            for (var i = 0; i < grid.Cells; i++) total += grid[i].Count;
            return total;
        }

        void Step(string what, Action gesture)
        {
            var before = Total();
            gesture();
            steps++;

            var after = Total();
            if (after != before) faults.Add($"'{what}' turned {before} things into {after}");
        }
    }

    private static List<string> FurnaceSelfTest(ItemRegistry items, RecipeBook book, out string detail)
    {
        var faults = new List<string>();
        const float Step = 1f / 20f;
        var relit = new List<(int, int, int, bool)>();

        var iron = items.ByName("raw_iron").Id;
        var ingot = items.ByName("iron_ingot").Id;
        var planks = items.ByName("driftoak_planks").Id;
        var smelt = book.SmeltFor(iron);

        if (smelt is null)
        {
            detail = "nothing smelts raw iron";
            faults.Add("raw iron has no smelting recipe, so nothing below could run");
            return faults;
        }

        // Fed and fuelled: it lights, it produces, and it takes about as long as it says.
        //
        // Four ore rather than a rounder number, deliberately. A plank is fifteen seconds and a
        // smelt is ten, so three ore is exactly two planks — a boundary where an off-by-one frame
        // and a correct run cost the same, and where a check written as "smelts per plank" agrees
        // with a furnace that throws the remainder of every plank away. Four is forty seconds of
        // work against fifteen-second planks and only comes to three if the leftover carries over.
        const int Ore = 4;
        var bank = new FurnaceBank(items, book);
        var working = bank.Open(0, 0, 0);
        working.Input = new ItemStack(iron, Ore);
        working.Fuel = new ItemStack(planks, 8);

        var litAfter = -1f;
        var madeAfter = -1f;
        var doneAfter = -1f;
        for (var frame = 1; frame <= 4000; frame++)
        {
            bank.Update(Step, relit);
            if (litAfter < 0f && working.Lit) litAfter = frame * Step;
            if (madeAfter < 0f && !working.Output.IsEmpty) madeAfter = frame * Step;
            if (!working.Input.IsEmpty || working.Output.Count < Ore) continue;
            doneAfter = frame * Step;
            break;
        }

        if (litAfter < 0f) faults.Add("a fed and fuelled furnace never lit");
        if (madeAfter < 0f) faults.Add("a fed and fuelled furnace made nothing");
        else if (MathF.Abs(madeAfter - smelt.Seconds) > Step * 3f)
            faults.Add($"the first ingot took {madeAfter:F2}s, the recipe says {smelt.Seconds:F2}s");

        // A frame of slack per smelt, plus a couple. Progress is a float summed a twentieth of a
        // second at a time, so ten seconds of it lands a hair under ten and each item costs one
        // extra tick — real, harmless, and about a quarter of a percent. A tolerance tight enough to
        // fail that would only ever be reporting the addition.
        var work = Ore * smelt.Seconds;
        if (doneAfter < 0f) faults.Add($"{Ore} ore never finished");
        else if (MathF.Abs(doneAfter - work) > Step * (Ore + 2))
            faults.Add($"{Ore} smelts took {doneAfter:F2}s, the recipe says {work:F2}s");

        if (working.Output.Item.Value != ingot.Value || working.Output.Count != Ore)
            faults.Add($"{Ore} raw iron came to {working.Output.Count} of {items[working.Output.Item].Name}");

        // The property, not the arithmetic of one path through it: fuel is spent to cover the work
        // and what is left of a piece is still there for the next smelt.
        var burnt = 8 - working.Fuel.Count;
        var perFuel = items[planks].BurnSeconds;
        var wanted = (int)MathF.Ceiling(work / perFuel);
        if (burnt != wanted)
            faults.Add(
                $"{Ore} smelts is {work:F0}s of work and burnt {burnt} planks of {perFuel:F0}s, "
                + $"which is {wanted} if the leftover carries over");

        // Fuel and nothing to do: it must not light, and must not lose a single plank. This is the
        // one a player notices a long time after it happened.
        var idle = new FurnaceBank(items, book);
        var waiting = idle.Open(0, 0, 0);
        waiting.Fuel = new ItemStack(planks, 4);
        for (var frame = 0; frame < 2000; frame++) idle.Update(Step, relit);

        if (waiting.Lit) faults.Add("an empty furnace lit itself");
        if (waiting.Fuel.Count != 4) faults.Add($"an empty furnace burnt {4 - waiting.Fuel.Count} planks doing nothing");

        // Ore and no fuel: no progress at all, rather than slow progress.
        var cold = new FurnaceBank(items, book);
        var starved = cold.Open(0, 0, 0);
        starved.Input = new ItemStack(iron, 4);
        for (var frame = 0; frame < 2000; frame++) cold.Update(Step, relit);

        if (starved.Lit) faults.Add("a furnace with no fuel lit anyway");
        if (!starved.Output.IsEmpty) faults.Add("a furnace with no fuel smelted something");
        if (starved.Progress > 0f) faults.Add($"a furnace with no fuel got {starved.Progress:F2}s through a smelt");

        // A full output stops the work rather than swallowing it.
        var full = new FurnaceBank(items, book);
        var blocked = full.Open(0, 0, 0);
        blocked.Input = new ItemStack(iron, 8);
        blocked.Fuel = new ItemStack(planks, 8);
        blocked.Output = new ItemStack(ingot, ItemStack.MaxCount);
        for (var frame = 0; frame < 2000; frame++) full.Update(Step, relit);

        if (blocked.Output.Count != ItemStack.MaxCount)
            faults.Add($"a full furnace output reached {blocked.Output.Count}");
        if (blocked.Input.Count != 8) faults.Add($"a blocked furnace still ate {8 - blocked.Input.Count} ore");
        if (blocked.Fuel.Count != 8) faults.Add($"a blocked furnace still burnt {8 - blocked.Fuel.Count} planks");

        // Lighting and going out are both reported, so the block that is drawn can follow.
        var watched = new FurnaceBank(items, book);
        var brief = watched.Open(4, 5, 6);
        brief.Input = new ItemStack(iron, 1);
        brief.Fuel = new ItemStack(planks, 1);

        var lights = 0;
        var outs = 0;
        for (var frame = 0; frame < 2000; frame++)
        {
            watched.Update(Step, relit);
            foreach (var (x, y, z, on) in relit)
            {
                if ((x, y, z) != (4, 5, 6)) faults.Add($"a change was reported at {x},{y},{z}");
                if (on) lights++; else outs++;
            }
        }

        if (lights != 1) faults.Add($"one plank and one ore lit the fire {lights} times");
        if (outs != 1) faults.Add($"one plank and one ore put the fire out {outs} times");

        // And breaking one hands back everything that was inside it.
        var carried = 0;
        foreach (var stack in watched.Remove(4, 5, 6)) carried += stack.Count;
        if (carried != 1) faults.Add($"breaking a furnace holding one ingot handed back {carried} things");
        if (watched.Count != 0) faults.Add("a broken furnace is still in the world");

        // ⛳ THE BLAST FURNACE, AND BOTH HALVES OF WHAT IT IS FOR. It takes ore and nothing else, and
        // it takes it in half the time. Run in the same bank as the pair above with only the kind
        // changed, so the two numbers are comparable — a separate world would be comparing setups.
        //
        // ⚠ The refusal has to be checked against something the plain furnace DOES take, or "it made
        // nothing" is true of a station that does nothing at all.
        float SmeltRun(ItemId input, int count, FurnaceKind kind)
        {
            var bench = new FurnaceBank(items, book);
            var one = bench.Open(0, 0, 0);
            one.Input = new ItemStack(input, count);
            one.Fuel = new ItemStack(planks, 16);

            for (var frame = 1; frame <= 4000; frame++)
            {
                bench.Update(Step, relit, (_, _, _) => kind);
                if (one.Input.IsEmpty && one.Output.Count >= count) return frame * Step;
            }

            return -1f;
        }

        var plain = SmeltRun(iron, Ore, FurnaceKind.Furnace);
        var blast = SmeltRun(iron, Ore, FurnaceKind.Blast);

        if (plain < 0f || blast < 0f) faults.Add("a smelter never finished four ore");
        else if (blast > plain * 0.6f)
            faults.Add(
                $"a blast furnace took {blast:F2}s over four ore where a furnace took {plain:F2}s — "
                + "it is meant to be half, and half is the only reason to build one");

        var sand = items.ByName("sand").Id;
        if (book.SmeltFor(sand, FurnaceKind.Furnace) is null)
            faults.Add("a furnace will not melt sand, so refusing it below proves nothing");
        if (book.SmeltFor(sand, FurnaceKind.Blast) is not null)
            faults.Add("a blast furnace took sand, which is not ore");

        var refused = SmeltRun(sand, 1, FurnaceKind.Blast);
        if (refused >= 0f) faults.Add("a blast furnace with sand in it smelted it anyway");

        // ⛳ AND THE SMOKER, which is the same claim on a different axis — and the pair with the
        // blast furnace is what makes either mean anything. ⛔ The old test for "will this smelter
        // take this job" was `kind != Blast || work == Ore`, which is TRUE OF A SMOKER FOR EVERY JOB
        // IN THE GAME: it would have been a plain furnace that happened to be twice as fast at
        // melting sand, and every check above would have passed it. So each specialised kind is
        // asked for the thing it takes AND for the thing the other one takes.
        var meat = items.ByName("raw_beef").Id;

        if (book.SmeltFor(meat, FurnaceKind.Smoker) is null)
            faults.Add("a smoker will not cook meat, which is the only thing it is for");
        if (book.SmeltFor(meat, FurnaceKind.Blast) is not null)
            faults.Add("a blast furnace cooked a steak");
        if (book.SmeltFor(iron, FurnaceKind.Smoker) is not null)
            faults.Add("a smoker reduced an ore, so it is a furnace with a different picture");
        if (book.SmeltFor(sand, FurnaceKind.Smoker) is not null)
            faults.Add("a smoker melted sand");
        if (book.SmeltFor(meat, FurnaceKind.Furnace) is null)
            faults.Add("a plain furnace will not cook meat, so refusing it above proves nothing");

        var cooked = SmeltRun(meat, 4, FurnaceKind.Smoker);
        var overFire = SmeltRun(meat, 4, FurnaceKind.Furnace);

        if (cooked < 0f || overFire < 0f) faults.Add("a smelter never finished four steaks");
        else if (cooked > overFire * 0.6f)
            faults.Add($"a smoker took {cooked:F2}s over four steaks where a furnace took "
                     + $"{overFire:F2}s — it is meant to be half");

        detail = $"{Ore} ore into {Ore} ingots in {doneAfter:F0}s on {burnt} planks of {perFuel:F0}s; "
               + "idle burns nothing, unfuelled makes no progress, a full one stops, and the flame is "
               + $"reported both ways. A blast furnace does the same four in {blast:F0}s against "
               + $"{plain:F0}s and will not touch sand; a smoker does four steaks in {cooked:F0}s "
               + $"against {overFire:F0}s and will not touch ore";

        return faults;
    }

    /// <summary>
    /// Builds a run of fence in a headless world and checks every piece settles on the right shape.
    /// </summary>
    /// <remarks>
    /// <para>The load-bearing check is the last one: that <em>one ring is enough</em>. The whole
    /// pass rests on the claim that a variant swap can never make a seventh cell want to move, and
    /// that claim is an argument about the block set rather than about the code — the day somebody
    /// adds a family whose variants differ in what they connect to, the argument stops holding and
    /// the pass starts silently leaving pieces on the wrong shape. So it is measured: the whole
    /// world is swept after every edit, and anything outside the ring that still wants to change is
    /// a fault.</para>
    /// <para>The shapes are read back off the model rather than compared against a table of names.
    /// A mask that is registered against the wrong geometry passes every check written in terms of
    /// ids and shows up as a fence with an arm reaching into open air.</para>
    /// </remarks>
    private static List<string> ConnectionSelfTest(BlockRegistry registry, out string detail)
    {
        var faults = new List<string>();
        var table = StarterBlocks.Connections(registry);
        var world = new VoxelWorld(registry);

        var fence = StarterBlocks.Connected(registry, "driftoak_fence");
        var pane = StarterBlocks.Connected(registry, "glass_pane");
        var stone = registry.ByName("stone").Id;
        var glass = registry.ByName("glass").Id;

        // Every variant's geometry has to match the mask it is filed under. An arm is a box that
        // reaches the edge of the cell, so counting how far the model spans on each side says what
        // it is actually joined to without trusting the name it was registered with.
        for (var mask = 0; mask < ConnectionFamily.Masks; mask++)
        {
            var model = registry[fence[mask]].Model;
            var drawn = 0;

            for (var i = 0; i < Placeable.Facings.Length; i++)
            {
                if (!ReachesEdge(model, Placeable.Facings[i])) continue;
                drawn |= 1 << i;
            }

            if (drawn != mask)
                faults.Add($"fence variant {mask} draws arms {drawn}, so its shape and its id disagree");
        }

        // A lone post joins nothing.
        Rewire(world, table, Place(world, fence[0], 0, 64, 0));
        if (world.GetBlock(0, 64, 0) != fence[0])
            faults.Add("a fence on its own picked up an arm from nowhere");

        // Put one beside it and both must reach for the other. Checking only the new one passes a
        // pass that never looks backwards, which is the whole point of doing this on an edit.
        Rewire(world, table, Place(world, fence[0], 1, 64, 0));

        var west = table.MaskOf(world.GetBlock(0, 64, 0));
        var east = table.MaskOf(world.GetBlock(1, 64, 0));
        var toPosX = 1 << Array.IndexOf(Placeable.Facings, Faces.PosX);
        var toNegX = 1 << Array.IndexOf(Placeable.Facings, Faces.NegX);

        if (west != toPosX) faults.Add($"the first fence wears {west}, not the {toPosX} that reaches its neighbour");
        if (east != toNegX) faults.Add($"the second fence wears {east}, not the {toNegX} that reaches back");

        // A solid block is something to join on to as well.
        Rewire(world, table, Place(world, stone, 2, 64, 0));
        if (table.MaskOf(world.GetBlock(1, 64, 0)) != (toPosX | toNegX))
            faults.Add("a fence between another fence and a wall of stone did not reach both");

        // And taking it away lets go again. A pass that only ever adds arms leaves a fence pointing
        // at a hole, which is the failure that looks most like a rendering bug.
        Rewire(world, table, Place(world, BlockId.Air, 2, 64, 0));
        if (table.MaskOf(world.GetBlock(1, 64, 0)) != toNegX)
            faults.Add("a fence kept its arm after what it was joined to was broken");

        // Families do not join each other. A pane reaching for a fence is two features sharing one
        // mask table, and it looks correct until they are next to each other.
        Rewire(world, table, Place(world, pane[0], 2, 64, 0));
        if (table.MaskOf(world.GetBlock(2, 64, 0)) != 0)
            faults.Add("a pane joined on to a fence");

        // But a pane does join glass, which is not opaque — testing opacity rather than fullness is
        // what would leave every window with a gap in it.
        Rewire(world, table, Place(world, glass, 3, 64, 0));
        if (table.MaskOf(world.GetBlock(2, 64, 0)) == 0)
            faults.Add("a pane did not join the glass beside it");

        // One ring is enough. Sweep the whole world after each edit of a fresh cross and fail if
        // anything anywhere still wants to move.
        var wide = new VoxelWorld(registry);
        var settled = 0;
        var stray = 0;

        foreach (var (x, z) in (( int X, int Z)[])[(0, 0), (1, 0), (-1, 0), (0, 1), (0, -1), (2, 0), (0, 2)])
        {
            Rewire(wide, table, Place(wide, fence[0], x, 64, z));

            for (var sz = -4; sz <= 4; sz++)
            for (var sx = -4; sx <= 4; sx++)
            {
                if (!table.TryRewire(wide, sx, 64, sz, out _)) { settled++; continue; }
                stray++;
                faults.Add($"after an edit at {x},{z} the fence at {sx},{sz} still wanted to change");
            }
        }

        detail = $"{table.FamilyCount} families of {ConnectionFamily.Masks}, shapes matching their masks, "
               + $"joining both ways and letting go, and {settled} cells settled with {stray} left over "
               + "one ring from an edit";

        return faults;

        static (int X, int Y, int Z) Place(VoxelWorld into, BlockId id, int x, int y, int z)
        {
            into.SetBlock(x, y, z, id);
            return (x, y, z);
        }

        // The streamer's own ring, run against a bare world so the rule is checked rather than the
        // streaming around it.
        static void Rewire(VoxelWorld into, ConnectionTable table, (int X, int Y, int Z) at)
        {
            Fix(at.X, at.Y, at.Z);
            for (var face = 0; face < Faces.Count; face++)
            {
                var (dx, dy, dz) = Faces.Normals[face];
                Fix(at.X + dx, at.Y + dy, at.Z + dz);
            }

            void Fix(int x, int y, int z)
            {
                if (table.TryRewire(into, x, y, z, out var become)) into.SetBlock(x, y, z, become);
            }
        }
    }

    /// <summary>
    /// Builds a world, writes it, reads it back, and checks nothing changed on the way.
    /// </summary>
    /// <remarks>
    /// <para>A round trip is the obvious check and on its own it is not enough — it passes on a
    /// format that stores raw ids, which is exactly the format that quietly ruins a world the next
    /// time a block is added. So the two failures that actually cost somebody their save are
    /// checked separately, and neither is visible from a round trip:</para>
    /// <para>⛔ <b>That the palette is doing something.</b> The same save is resolved against a
    /// deliberately <em>shifted</em> registry — every id moved, as inserting one block does to every
    /// id after it — and the names have to come back pointing at the right blocks. A format keyed on
    /// ids passes every round trip ever run and fails this one.</para>
    /// <para>⛔ <b>That a name this build no longer has is reported and skipped</b>, rather than
    /// resolving to whatever now occupies that number. That is the difference between a player
    /// noticing a missing block and a player finding their wall turned into glass.</para>
    /// </remarks>
    private static List<string> SaveSelfTest(
        BlockRegistry registry, ItemRegistry items, RecipeBook book, out string detail)
    {
        var faults = new List<string>();

        var world = new VoxelWorld(registry);
        var furnaces = new FurnaceBank(items, book);
        var chests = new ChestBank(items);
        var pockets = new Inventory(items);
        var worn = new Equipment(items);
        var vitals = new PlayerVitals(registry);
        var unlocks = new RecipeUnlocks();

        var stone = registry.ByName("stone").Id;
        var bench = registry.ByName("bench").Id;
        var torch = registry.ByName("torch").Id;

        // A world somebody has built in: a floor, a bench on it, a torch beside it.
        for (var x = 0; x < 4; x++)
        for (var z = 0; z < 4; z++)
            world.SetBlock(x, 64, z, stone);

        world.SetBlock(1, 65, 1, bench);
        world.SetBlock(2, 65, 1, torch);

        if (!world.Changed) faults.Add("a world that has been built in does not think anything changed");

        var furnace = furnaces.Open(3, 65, 3);
        furnace.Input = items.Stack("raw_iron", 5);
        furnace.Fuel = items.Stack("coal", 2);
        furnace.Output = items.Stack("iron_ingot", 1);
        furnace.BurnLeft = 42.5f;
        furnace.Progress = 3.25f;

        var chest = chests.Open(0, 65, 0);
        chest.Contents[0] = items.Stack("driftoak_planks", 37);
        chest.Contents[26] = items.Stack("stormglass", 2);

        pockets.Add(items.Stack("driftoak_log", 12));
        pockets.Add(new ItemStack(items.ByName("wood_pickaxe").Id, 1, 17));
        pockets.Select(3);
        worn.Restore(EquipSlot.Offhand, items.Stack("torch", 8));
        vitals.Restore(13, 220);
        unlocks.Reload(["sticks", "bench"]);

        var state = new WorldState(
            "driftwood", items, world, furnaces, chests, pockets, worn, vitals, unlocks)
        {
            Position = new Vector3(1.5f, 65f, 2.5f),
            Yaw = 47f,
            Pitch = -12f,
            Played = 3725.5,
            DayTime = 0.375f,
        };

        var path = Path.Combine(Path.GetTempPath(), $"driftwood-audit-{Environment.ProcessId}.dws");

        try
        {
            // ⚠ Written through the real writer to the real disk, because "it round-trips through a
            // MemoryStream" says nothing about the atomic move, and that move is the part that keeps
            // somebody's world when a save is interrupted.
            if (WorldSave.Write(Path.GetFileNameWithoutExtension(path), state) is { } wrote)
                faults.Add($"writing a world failed: {wrote}");

            var written = WorldSave.PathFor(Path.GetFileNameWithoutExtension(path));

            if (world.Changed) faults.Add("a world still thinks it has unsaved changes after being saved");

            if (!WorldSave.TryReadHeader(written, out var header))
            {
                faults.Add("a world that was just written has no readable header");
                detail = "the save could not be read back";
                return faults;
            }

            if (header.Seed != "driftwood") faults.Add($"the header came back with seed '{header.Seed}'");
            if (Math.Abs(header.Played - 3725.5) > 0.01) faults.Add($"played time came back as {header.Played}");
            if (header.Edits != world.Edits.Count)
                faults.Add($"the header says {header.Edits} edits where the world had {world.Edits.Count}");

            // Back into a session that knows nothing.
            var back = new VoxelWorld(registry);
            var backFurnaces = new FurnaceBank(items, book);
            var backChests = new ChestBank(items);
            var backPockets = new Inventory(items);
            var backWorn = new Equipment(items);
            var backVitals = new PlayerVitals(registry);
            var backUnlocks = new RecipeUnlocks();

            var into = new WorldState(
                "", items, back, backFurnaces, backChests, backPockets, backWorn, backVitals, backUnlocks);

            var missing = new List<string>();
            if (WorldSave.Read(written, registry, items, into, missing) is { } read)
                faults.Add($"reading a world failed: {read}");

            if (missing.Count > 0)
                faults.Add($"a world written by this build reported {missing.Count} names it does not have: {missing[0]}");

            // ⚠ Asked of the record rather than of the blocks, and the distinction is the whole
            // reason the load fault went unseen for a commit. A world read back holds its edits as a
            // list waiting for the chunks they belong to — nothing writes a block into a chunk the
            // generator has not made yet, because generation would only overwrite it. This world has
            // no generator behind it at all, so nothing will ever arrive to take delivery of them,
            // and asking GetBlock here would be asking a question this check is not the one to ask.
            // "The edits reach the world" is the load check's job and it runs a real streamer.
            if (!back.Edits.TryGetValue((1, 65, 1), out var backBench) || backBench != bench)
                faults.Add("the bench did not come back");
            if (!back.Edits.TryGetValue((2, 65, 1), out var backTorch) || backTorch != torch)
                faults.Add("the torch did not come back");
            if (!back.Edits.TryGetValue((0, 64, 0), out var backFloor) || backFloor != stone)
                faults.Add("the floor did not come back");
            if (back.Edits.Count != world.Edits.Count)
                faults.Add($"{back.Edits.Count} edits came back of {world.Edits.Count}");
            if (back.PendingEdits != world.Edits.Count)
                faults.Add($"{back.PendingEdits} edits are waiting for a chunk of {world.Edits.Count}");

            if (back.Changed) faults.Add("a world is marked changed the instant it is loaded, so every load autosaves");

            if (!backFurnaces.TryGet(3, 65, 3, out var backFurnace))
            {
                faults.Add("the furnace did not come back");
            }
            else
            {
                if (backFurnace.Input.Count != 5) faults.Add($"the furnace came back holding {backFurnace.Input.Count} ore");
                if (Math.Abs(backFurnace.BurnLeft - 42.5f) > 0.01f)
                    faults.Add($"the furnace came back with {backFurnace.BurnLeft:F2}s of burn, not 42.50");
                if (Math.Abs(backFurnace.Progress - 3.25f) > 0.01f)
                    faults.Add($"the furnace lost its progress: {backFurnace.Progress:F2}");
            }

            if (!backChests.TryGet(0, 65, 0, out var backChest))
            {
                faults.Add("the chest did not come back");
            }
            else
            {
                if (backChest.Contents[0].Count != 37)
                    faults.Add($"the chest's first slot came back with {backChest.Contents[0].Count}");
                if (backChest.Contents[26].IsEmpty) faults.Add("the chest's last slot came back empty");
            }

            if (backPockets.Selected != 3) faults.Add($"the held slot came back as {backPockets.Selected}");
            if (backPockets.CountOf(items.ByName("driftoak_log").Id) != 12)
                faults.Add("the logs did not come back");

            var pick = ItemStack.Empty;
            foreach (var slot in backPockets.All)
                if (!slot.IsEmpty && items[slot.Item].Name == "wood_pickaxe") pick = slot;

            if (pick.IsEmpty) faults.Add("the pickaxe did not come back");
            else if (pick.Damage != 17) faults.Add($"the pickaxe came back with {pick.Damage} wear, not 17");

            if (backWorn.At((int)EquipSlot.Offhand).Count != 8) faults.Add("what was in the other hand did not come back");
            if (backVitals.Health != 13) faults.Add($"health came back as {backVitals.Health}");
            if (backVitals.Breath != 220) faults.Add($"breath came back as {backVitals.Breath}");
            if (backUnlocks.Announced != 2) faults.Add($"{backUnlocks.Announced} unlocks came back of 2");
            if (Math.Abs(into.Position.X - 1.5f) > 0.001f) faults.Add("the player came back somewhere else");

            // ⛔ THE PALETTE, and the only test that can tell it from a format keyed on ids. Every
            // name is resolved against a registry whose ids have all moved, which is what adding one
            // block does to every id after it.
            var shifted = new Palette();
            shifted.Add("stone");
            shifted.Add("bench");

            var shuffle = shifted.Resolve(n => registry.ByName(n).Id.Value + 1000, []);
            if (shuffle[0] != stone.Value + 1000 || shuffle[1] != bench.Value + 1000)
                faults.Add("a palette resolved against moved ids did not follow them, so names are not being used");

            // ⛔ And a name this build has never heard of is reported and left alone, rather than
            // resolving to whatever now occupies that number.
            var gone = new Palette();
            gone.Add("stone");
            gone.Add("a_block_that_was_removed");

            var lost = new List<string>();
            var partial = gone.Resolve(n => registry.TryByName(n, out var b) ? b.Id.Value : -1, lost);

            if (lost.Count != 1) faults.Add($"{lost.Count} names were reported missing where one was");
            if (partial[1] >= 0) faults.Add("a block this build does not have resolved to one that it does");
            if (partial[0] != stone.Value) faults.Add("a missing name took a real one down with it");

            detail = $"{world.Edits.Count} edits, a furnace mid-smelt, a {Chest.Slots}-slot chest, "
                   + $"{backPockets.Used} pockets and {backUnlocks.Announced} unlocks written and read back; "
                   + "names survive every id moving, and a name this build lost is reported not guessed";
        }
        finally
        {
            try { File.Delete(WorldSave.PathFor(Path.GetFileNameWithoutExtension(path))); } catch (Exception) { }
        }

        return faults;
    }

    /// <summary>
    /// Saves a world five times over and checks the four states behind the current one.
    /// </summary>
    /// <remarks>
    /// <para>The rotation is pure file shuffling with an order that is easy to get backwards, and
    /// getting it backwards has no symptom at all until somebody needs it — at which point the three
    /// copies kept against exactly that moment turn out to be three copies of the same state, or of
    /// the wrong ones.</para>
    /// <para><b>Each save is given a different number of edits</b>, so every file can be told apart
    /// by its own header without reading a block. A rotation that copies the same file into every
    /// slot, or shifts the wrong way, comes out as the wrong numbers rather than as a pass.</para>
    /// <para>Five saves against three slots, because the interesting case is the one that pushes the
    /// oldest out. Four would leave the last slot never having been overwritten.</para>
    /// </remarks>
    private static List<string> BackupSelfTest(
        BlockRegistry registry, ItemRegistry items, RecipeBook book, out string detail)
    {
        var faults = new List<string>();
        var name = $"driftwood-audit-backup-{Environment.ProcessId}";
        var stone = registry.ByName("stone").Id;

        try
        {
            var world = new VoxelWorld(registry);

            // Save n has n edits in it, which is the whole identity of that state.
            for (var save = 1; save <= 5; save++)
            {
                world.SetBlock(save, 64, 0, stone);

                var state = new WorldState(
                    "backups", items, world,
                    new FurnaceBank(items, book), new ChestBank(items),
                    new Inventory(items), new Equipment(items),
                    new PlayerVitals(registry), new RecipeUnlocks());

                if (WorldSave.Backup(name) is { } spare) faults.Add($"keeping a copy failed: {spare}");
                if (WorldSave.Write(name, state) is { } wrote) faults.Add($"save {save} failed: {wrote}");
            }

            // The current file is the fifth, and slot n is the state n saves ago.
            if (!WorldSave.TryReadHeader(WorldSave.PathFor(name), out var current))
                faults.Add("the world itself is not readable after five saves");
            else if (current.Edits != 5)
                faults.Add($"the world holds {current.Edits} edits after five saves of 1..5");

            for (var slot = 1; slot <= WorldSave.Backups; slot++)
            {
                var path = WorldSave.BackupPath(name, slot);
                var want = 5 - slot;

                if (!File.Exists(path))
                {
                    faults.Add($"slot {slot} was never written, so only {slot - 1} states are kept");
                    continue;
                }

                if (!WorldSave.TryReadHeader(path, out var kept))
                    faults.Add($"slot {slot} is not readable");
                else if (kept.Edits != want)
                    faults.Add(
                        $"slot {slot} holds the state with {kept.Edits} edits where the one "
                        + $"{slot} saves back has {want} — the rotation is not moving them along");
            }

            // ⛔ And the one a fifth save must have pushed out. Without this the check passes a
            // rotation that only ever grows, which keeps every state a world has ever been in.
            if (File.Exists(WorldSave.BackupPath(name, WorldSave.Backups + 1)))
                faults.Add(
                    $"a {WorldSave.Backups + 1}th slot exists, so nothing is ever dropped and the "
                    + "saves folder grows without limit");

            // ⚠ And the backups must not read as worlds. A list built from *.dws would show four
            // entries where somebody has one world, three of them copies they never made.
            var listed = WorldSave.List().Count(w => w.Name == name);
            if (listed != 1)
                faults.Add($"one world with {WorldSave.Backups} copies behind it lists as {listed} worlds");

            detail = $"five saves of 1..5 edits leave the world holding 5 and the {WorldSave.Backups} "
                   + "slots holding 4, 3 and 2; the oldest is dropped and none of them list as a world";
        }
        finally
        {
            try
            {
                File.Delete(WorldSave.PathFor(name));
                for (var slot = 1; slot <= WorldSave.Backups + 1; slot++)
                    File.Delete(WorldSave.BackupPath(name, slot));
            }
            catch (Exception) { }
        }

        return faults;
    }

    /// <summary>
    /// Loads a saved world into a real streamer, and checks that what comes out is the generator's
    /// world with the save's changes in it.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>The reason this exists, and the reason <see cref="SaveSelfTest"/> could not have
    /// caught what it is for.</b> That check reads a world back into a bare <see cref="VoxelWorld"/>
    /// with no generator behind it — where creating a chunk to hold an edit is exactly the right
    /// thing to do, because nothing else was ever going to make one. Put the same read in front of a
    /// streamer and it stops being right, and what happens to the edit next is the whole
    /// question.</para>
    /// <para>So this runs the real sequence: write a world, then read it into a fresh streamer's
    /// world <em>before</em> the streamer has been told where the viewer is, which is the order
    /// startup actually uses. Then it asks the world what is in it.</para>
    /// <para><b>Two cells, and the second is the point.</b> One under the spawn and one six hundred
    /// blocks away, because "somebody built away from spawn" is precisely the case a load radius
    /// makes different — the far chunk is not generated when the save is read, is not generated when
    /// the world opens, and only arrives once somebody walks to it.</para>
    /// <para><b>A bench is the marker</b> because the generator has never placed one anywhere. "The
    /// bench is there" therefore cannot be terrain agreeing with the save by coincidence, which
    /// stone or dirt could easily do.</para>
    /// </remarks>
    private static List<string> LoadIntoStreamerSelfTest(
        WorldSeed seed,
        BlockRegistry registry,
        ItemRegistry items,
        RecipeBook book,
        StarterBlocks.Ids ids,
        float oceanCoverage,
        out string detail)
    {
        const int radius = 2;
        const int settleTimeoutMs = 30_000;

        var faults = new List<string>();
        var bench = registry.ByName("bench").Id;

        // Deep enough to be inside solid ground rather than in the open air, so a chunk that came
        // back hollow is a chunk whose neighbours are obviously not.
        var near = (X: 5, Y: 40, Z: 5);
        var far = (X: 600, Y: 40, Z: 600);
        var untouched = (X: 7, Y: 2, Z: 7);

        var name = $"driftwood-audit-load-{Environment.ProcessId}";
        var path = WorldSave.PathFor(name);

        try
        {
            var built = new VoxelWorld(registry);
            built.SetBlock(near.X, near.Y, near.Z, bench);
            built.SetBlock(far.X, far.Y, far.Z, bench);

            var saved = new WorldState(
                seed.ToString(), items, built,
                new FurnaceBank(items, book), new ChestBank(items),
                new Inventory(items), new Equipment(items),
                new PlayerVitals(registry), new RecipeUnlocks());

            if (WorldSave.Write(name, saved) is { } wrote)
            {
                detail = $"could not write the world to load: {wrote}";
                faults.Add(detail);
                return faults;
            }

            using var streamer = new WorldStreamer(
                registry, new TerrainGenerator(seed, ids, oceanCoverage), radius);

            // ⛳ Before Update, which is the order startup uses: the save is read into a world whose
            // streamer has not generated a single chunk yet.
            var into = new WorldState(
                "", items, streamer.World,
                new FurnaceBank(items, book), new ChestBank(items),
                new Inventory(items), new Equipment(items),
                new PlayerVitals(registry), new RecipeUnlocks());

            var missing = new List<string>();
            if (WorldSave.Read(path, registry, items, into, missing) is { } read)
            {
                detail = $"could not read the world back: {read}";
                faults.Add(detail);
                return faults;
            }

            Settle(streamer, new Vector3(0.5f, 0f, 0.5f));

            // The positive control. Everything below is a claim about a cell in a chunk the streamer
            // was supposed to have generated, and all of it passes vacuously in a world where
            // generation never ran at all.
            //
            // ⛔ A COLUMN, NOT A CELL, AND THE CELL WAS A CHECK THAT LIED. It probed (7,2,7) and
            // called air there "nothing generated" — but two of the five test seeds have a cave
            // running through exactly that cell, so a perfectly generated world failed its own
            // positive control on saltmarsh and on 9911. The claim was never about one cell; it is
            // that the chunk has terrain in it, and a chunk of terrain is not something a cave can
            // empty. Counted over the whole column, with a floor well under what any real one holds —
            // measured at 53 to 66 across the five test seeds, and gated at 8.
            var column = 0;
            for (var y = TerrainGenerator.WorldBottom; y < TerrainGenerator.WorldTop; y++)
                if (streamer.World.GetBlock(untouched.X, y, untouched.Z) != BlockId.Air) column++;

            if (column < 8)
                faults.Add(
                    $"nothing generated at all — the column at ({untouched.X},{untouched.Z}) holds "
                    + $"{column} solid cells, so every check below is measuring an empty world");

            Judge("under the spawn", near, streamer);

            // Walking to it is the only way the far chunk ever generates, and it is where a load
            // that replays edits into ungenerated chunks does its damage.
            Settle(streamer, new Vector3(far.X + 0.5f, 0f, far.Z + 0.5f));
            Judge("six hundred blocks out", far, streamer);

            detail = faults.Count > 0
                ? faults[0]
                : $"a bench saved at spawn and one at {far.X},{far.Z} both come back, "
                  + "each in a chunk of full terrain, after the streamer generated over them";
        }
        finally
        {
            try { File.Delete(path); } catch (Exception) { }
        }

        return faults;

        void Judge(string where, (int X, int Y, int Z) cell, WorldStreamer streamer)
        {
            var chunkAt = ChunkPos.FromWorld(cell.X, cell.Y, cell.Z);

            if (!streamer.World.TryGetChunk(chunkAt, out var chunk))
            {
                faults.Add($"the chunk {where} is not loaded at all after settling on it");
                return;
            }

            var found = streamer.World.GetBlock(cell.X, cell.Y, cell.Z);
            if (found != bench)
                faults.Add(
                    $"the bench {where} came back as '{registry[found.Value].Name}' — "
                    + "a saved edit did not survive the chunk being generated");

            // ⛔ The other half, and the one that says which fault it is. A chunk holding only the
            // restored block is a chunk generation never filled: a hollow 32-cube punched into the
            // world wherever somebody had built. Solid ground this far down is very nearly the whole
            // volume, so the two outcomes are nowhere near each other.
            if (chunk.SolidCount < 1000)
                faults.Add(
                    $"the chunk {where} holds {chunk.SolidCount} blocks of a possible {Chunk.Volume} — "
                    + "loading punched a hollow chunk into the world instead of filling it");
        }

        static void Settle(WorldStreamer streamer, Vector3 viewer)
        {
            streamer.Update(viewer);

            var watch = Stopwatch.StartNew();
            var idleSweeps = 0;

            while (watch.ElapsedMilliseconds < settleTimeoutMs)
            {
                streamer.PromoteReadyChunks();

                var drained = false;
                while (streamer.TryDequeueMesh(out _)) drained = true;
                while (streamer.TryDequeueDropped(out _)) { }

                var busy = streamer.PendingGenerate > 0
                        || streamer.PendingLight > 0
                        || streamer.PendingMesh > 0;

                if (busy || drained)
                {
                    idleSweeps = 0;
                    Thread.Sleep(2);
                    continue;
                }

                if (++idleSweeps >= 25) break;
                Thread.Sleep(2);
            }
        }
    }

    /// <summary>
    /// Fills a chest, overfills it, empties it, and counts what comes out.
    /// </summary>
    /// <remarks>
    /// <para>One rule, asked four ways: nothing is created and nothing is lost. A chest that
    /// silently swallowed the stack that would not fit would look exactly like one that fitted it,
    /// and the only way anybody would find out is by counting — which is what this does, in items
    /// rather than in stacks, because a merge that drops a remainder loses items and not slots.</para>
    /// <para>The overfill is deliberately not a round number of stacks. Twenty-eight stacks of a
    /// thing that caps at sixty-four into twenty-seven slots leaves exactly one over, and a check
    /// that put in twenty-seven would agree with a chest that quietly discarded anything past the
    /// last slot.</para>
    /// </remarks>
    private static List<string> ChestSelfTest(ItemRegistry items, out string detail)
    {
        var faults = new List<string>();
        var bank = new ChestBank(items);
        var chest = bank.Open(0, 64, 0);

        if (!chest.IsEmpty) faults.Add("a chest nobody has touched already holds something");
        if (bank.Open(0, 64, 0) != chest) faults.Add("opening the same chest twice made two of them");

        var plank = items.ByName("driftoak_planks").Id;
        var cap = items[plank].MaxStack;

        // Merging before filling: twenty into a slot that already holds twenty leaves one stack.
        chest.Contents[0] = new ItemStack(plank, 20);
        var over = bank.Add(chest, new ItemStack(plank, 20));

        if (!over.IsEmpty) faults.Add($"topping up a part-full slot left {over.Count} over");
        if (chest.Contents[0].Count != 40)
            faults.Add($"twenty onto twenty came to {chest.Contents[0].Count}");
        if (chest.Used != 1) faults.Add($"topping up a slot used {chest.Used} of them");

        // And then past the end of it. Twenty-eight full stacks into twenty-seven slots, so the
        // remainder is a whole stack and cannot be confused with rounding.
        var full = new ChestBank(items);
        var packed = full.Open(0, 64, 0);
        var given = 0;
        var refused = 0;

        for (var i = 0; i < Chest.Slots + 1; i++)
        {
            given += cap;
            refused += full.Add(packed, new ItemStack(plank, cap)).Count;
        }

        var held = 0;
        foreach (var stack in packed.Contents) held += stack.Count;

        if (packed.Used != Chest.Slots) faults.Add($"a chest filled past the end used {packed.Used} slots");
        if (held + refused != given)
            faults.Add($"{given} planks went in, {held} are in the chest and {refused} came back — "
                     + $"{given - held - refused} are nowhere");
        if (refused != cap)
            faults.Add($"a chest with {Chest.Slots} slots took {held} of {given} and refused {refused}");

        // Breaking it hands back everything and takes the chest out of the world with it.
        var out1 = 0;
        var stacks = 0;
        foreach (var stack in full.Remove(0, 64, 0)) { out1 += stack.Count; stacks++; }

        if (out1 != held) faults.Add($"breaking a chest holding {held} gave back {out1}");
        if (stacks != Chest.Slots) faults.Add($"breaking a full chest gave back {stacks} stacks");
        if (full.Count != 0) faults.Add("a broken chest is still in the world");
        if (full.Remove(0, 64, 0).Any()) faults.Add("breaking the same chest twice gave its contents twice");

        detail = $"{Chest.Slots} slots; topping up merges, {given} in gives {held} held and {refused} "
               + $"back with none lost, and breaking it returns all {stacks} stacks once";

        return faults;
    }

    /// <summary>
    /// Builds a shaft with a ladder up one wall and checks what it takes to go up it.
    /// </summary>
    /// <remarks>
    /// <para>Four claims, and the third is the one worth having. That pressing into a ladder takes
    /// you up it. That letting go turns a fall into a slide rather than a drop. ⚠ <b>That walking
    /// past one, pressing away from it, does not stick you to it</b> — a ladder you cling to by
    /// being near it catches everybody who builds a corridor beside one, and every other test here
    /// would pass a build that did exactly that. And that a climb is not a shortcut: it has to be
    /// slower than walking, or every staircase in the game is a waste of stone.</para>
    /// <para>Heights are measured over a fixed number of steps rather than compared against the
    /// constant, so a climb that is somehow instantaneous fails rather than agreeing with itself.
    /// </para>
    /// </remarks>
    private static List<string> ClimbSelfTest(BlockRegistry registry, out string detail)
    {
        var faults = new List<string>();
        const float Step = 1f / 60f;
        const int Steps = 60;

        var world = new VoxelWorld(registry);
        var stone = registry.ByName("stone").Id;
        var ladder = registry.ByName("ladder_east").Id;

        // ⚠ A shaft, not an open wall, and that is what makes the control below mean anything. A
        // body pressing away from a ladder in the open simply walks out of the cell within a frame
        // or two and then falls for honest reasons — so "it did not end up higher" passes whether
        // the rule is there or not. Walled in, it stays on the ladder and the only thing that can
        // lift it is the rule being wrong.
        for (var y = 59; y < 76; y++)
        {
            world.SetBlock(0, y, 0, stone);      // the wall the ladder is fixed to
            world.SetBlock(2, y, 0, stone);
            world.SetBlock(1, y, -1, stone);
            world.SetBlock(1, y, 1, stone);
            if (y >= 60) world.SetBlock(1, y, 0, ladder);
        }

        for (var z = -2; z <= 2; z++)
        for (var x = -2; x <= 4; x++)
            world.SetBlock(x, 59, z, stone);

        // The ladder faces +x, so the wall it is fixed to is on its -x side and that is the way a
        // player has to press to hold on. Taken from the block rather than written down twice.
        var (wx, wy, wz) = Faces.Normals[registry[ladder].SupportFace];
        var into = new Vector3(wx, wy, wz);

        // Up.
        var body = new PlayerBody(registry);
        body.Teleport(new Vector3(1.5f, 60f, 0.5f));
        for (var i = 0; i < Steps; i++) body.Step(world, Step, into, false, false, false);

        var climbed = body.Position.Y - 60f;
        if (!body.OnLadder) faults.Add("pressing into a ladder did not put the body on it");
        if (climbed < 1f) faults.Add($"a second of climbing went up {climbed:F2} blocks");

        // ⚠ Slower than walking, or a ladder is a shortcut and every staircase is a waste of stone.
        // Both sides are walked rather than compared: the two speeds are compile-time constants and
        // asking whether one is less than the other is a sentence the compiler can answer without
        // running anything, which is the definition of a check that restates its own inputs.
        var flat = new VoxelWorld(registry);
        for (var z = -2; z <= 2; z++)
        for (var x = -2; x <= 40; x++)
            flat.SetBlock(x, 59, z, stone);

        var walker = new PlayerBody(registry);
        walker.Teleport(new Vector3(0.5f, 60f, 0.5f));
        for (var i = 0; i < Steps; i++)
            walker.Step(flat, Step, new Vector3(1f, 0f, 0f), false, false, false);

        var walked = walker.Position.X - 0.5f;
        if (walked <= 0.5f) faults.Add($"a second of walking went {walked:F2} blocks, so there is nothing to compare");
        if (climbed >= walked)
            faults.Add($"a second of climbing went up {climbed:F2} where a second of walking went {walked:F2} — "
                     + "a ladder is quicker than the ground, so nobody will ever build stairs");

        // Letting go: still on it, coming down at the slide rate rather than falling.
        var high = body.Position.Y;
        for (var i = 0; i < Steps; i++) body.Step(world, Step, Vector3.Zero, false, false, false);

        var slid = high - body.Position.Y;
        if (slid <= 0.2f) faults.Add($"letting go of a ladder moved the body {slid:F2} blocks");
        if (slid > PlayerBody.SlideSpeed + 0.5f)
            faults.Add($"letting go of a ladder dropped {slid:F2} blocks in a second, which is a fall");

        // Crouching holds. Measured from a height it could fall from, so a body already on the
        // floor cannot pass this by having nowhere to go.
        body.Teleport(new Vector3(1.5f, 68f, 0.5f));
        body.Step(world, Step, into, false, false, false);
        var held = body.Position.Y;
        for (var i = 0; i < Steps; i++) body.Step(world, Step, Vector3.Zero, false, true, false);

        if (MathF.Abs(body.Position.Y - held) > 0.2f)
            faults.Add($"crouching on a ladder moved {MathF.Abs(body.Position.Y - held):F2} blocks");

        // ⚠ The control, and the assertion the other three would all pass without. Pressing across
        // a ladder — neither into it nor away from it — must not climb. A ladder that holds anybody
        // standing in its cell catches every corridor built alongside one, and every test above
        // this line is satisfied by exactly that build.
        var across = new PlayerBody(registry);
        across.Teleport(new Vector3(1.5f, 68f, 0.5f));
        for (var i = 0; i < Steps; i++)
            across.Step(world, Step, new Vector3(0f, 0f, 1f), false, false, false);

        if (across.Position.Y >= 68f)
            faults.Add($"pressing across a ladder still climbed it, from 68.00 to {across.Position.Y:F2}");

        detail = $"pressing in climbs {climbed:F2} blocks in the second a walk covers {walked:F2}, "
               + $"letting go slides {slid:F2}, crouching holds, and pressing across a shaft it "
               + $"cannot leave falls to {across.Position.Y:F2}";

        return faults;
    }

    /// <summary>
    /// Checks that the light a player can build gets brighter, and that smokeglass stops it.
    /// </summary>
    /// <remarks>
    /// <para>⚠ <b>The ladder is not a ladder of range, and saying it was is what this check first
    /// got wrong.</b> A torch reaches fourteen and everything above it reaches fifteen, which is the
    /// ceiling — so "each brighter than the last" is a claim only the first rung can satisfy and the
    /// rest would pass it by being equal. What actually distinguishes them is <em>colour</em>: a
    /// torch and a fire are warm, a lantern is warm and much whiter, and the lamp is the one cold
    /// light there is. So both are asserted, and separately — everything outruns a torch, and the
    /// lamp is not a lantern with a different name on it.</para>
    /// <para>The second is the one worth having. Smokeglass is the only block in the set that is
    /// seen through and not passed through, which is possible only because opacity and attenuation
    /// are separate fields — and the way that breaks is somebody deciding they are the same thing,
    /// after which smokeglass is either an ordinary window or a solid grey cube. Both halves are
    /// asserted, so either collapse fails by name.</para>
    /// <para>Toggling is checked as a round trip and as a difference. A pair that swaps back to
    /// itself would pass "there and back again" while doing nothing at all, so the lit form also has
    /// to actually give off light the unlit one does not.</para>
    /// </remarks>
    private static List<string> CraftedLightSelfTest(BlockRegistry registry, out string detail)
    {
        var faults = new List<string>();

        // How far a source reaches: one level lost per step, so the peak channel is the range.
        static int Reach(BlockType type) => LightValue.BlockPeak(type.LightEmission);

        var torch = registry.ByName("torch");
        var lantern = registry.ByName("lantern");
        var lamp = registry.ByName("stormglass_lamp");
        var fire = registry.ByName("campfire_x_lit");
        var glass = registry.ByName("glass");
        var smoke = registry.ByName("smokeglass");

        if (Reach(torch) == 0) faults.Add("a torch gives off no light at all");
        if (Reach(lantern) <= Reach(torch))
            faults.Add($"a lantern reaches {Reach(lantern)} against a torch's {Reach(torch)}, "
                     + "so there is no reason to make one");
        if (Reach(lamp) <= Reach(torch))
            faults.Add($"a stormglass lamp reaches {Reach(lamp)}, no further than a torch");
        if (Reach(fire) == 0) faults.Add("a lit campfire gives off no light");

        // Warm against cold, which is the difference the range cannot express because everything
        // above a torch is already at the ceiling. A lamp that came out warm is a lantern with
        // another name, and every cave in the game would be lit one colour.
        static int Warmth(BlockType type) =>
            LightValue.Red(type.LightEmission) - LightValue.Blue(type.LightEmission);

        foreach (var warm in (BlockType[])[torch, fire, lantern])
            if (Warmth(warm) <= 0)
                faults.Add($"{warm.Name} burns as cold as it does warm, so nothing in the game is firelight");

        if (Warmth(lamp) >= 0)
            faults.Add($"a stormglass lamp is {Warmth(lamp)} warmer than it is cold, "
                     + "so it lights a room the same colour a torch does");

        // A wall torch is the same light as the one it was made from, whichever wall it is on.
        for (var i = 0; i < Placeable.Facings.Length; i++)
        {
            var wall = registry.ByName($"torch_wall_{FacingName(Placeable.Facings[i])}");
            if (wall.LightEmission != torch.LightEmission)
                faults.Add($"{wall.Name} burns differently from the torch it is");
        }

        // Glass and smokeglass differ in exactly one thing, and it is not opacity.
        if (glass.Opaque || smoke.Opaque)
            faults.Add("glass or smokeglass hides what is behind it, so neither is a window");
        if (!smoke.Solid) faults.Add("smokeglass is not solid, so it is not a block anybody can build with");
        if (glass.LightAttenuation != 0)
            faults.Add($"plain glass dims light by {glass.LightAttenuation}, so a glasshouse is dark");
        if (smoke.LightAttenuation < LightValue.Max)
            faults.Add($"smokeglass dims light by only {smoke.LightAttenuation} of {LightValue.Max}, "
                     + "so it is an ordinary window with a dark texture");

        // And the table the propagator actually reads has to agree, which is a different claim from
        // what the block says about itself: the two are joined by one Math.Clamp and a ternary.
        var dimming = registry.BuildLightAttenuationTable();
        if (dimming[smoke.Id.Value] < LightValue.Max)
            faults.Add($"the propagator dims smokeglass by {dimming[smoke.Id.Value]}, not {LightValue.Max}");
        if (dimming[glass.Id.Value] != 0)
            faults.Add($"the propagator dims plain glass by {dimming[glass.Id.Value]}");

        // Every toggle pairs both ways, and the two states are actually different.
        var toggles = 0;
        var pairs = new Dictionary<ushort, BlockId>();
        foreach (var (from, to) in StarterBlocks.Toggles(registry)) pairs[from.Value] = to;

        foreach (var (from, to) in pairs)
        {
            toggles++;

            if (!pairs.TryGetValue(to.Value, out var back) || back.Value != from)
                faults.Add($"{registry[from].Name} toggles to {registry[to].Name} and not back again");

            // Using something has to change something. A fire changes what it gives off; a door
            // changes where it is and whether it stops anybody. Asking only about the model would
            // be vacuous — the two states are always two objects — so the three real differences
            // are named, and a pair that shares all of them is a switch with nothing on the end.
            var one = registry[from];
            var other = registry[to];
            var moved = one.Model.Outline != other.Model.Outline;

            if (one.LightEmission == other.LightEmission && one.Solid == other.Solid && !moved)
                faults.Add($"{one.Name} and {other.Name} burn the same, stop the same and stand in the "
                         + "same place, so using it changes nothing anybody can see");
        }

        if (toggles == 0) faults.Add("no block has a second state, so nothing here was checked");

        detail = $"torch {Reach(torch)} warm {Warmth(torch):+#;-#;0}, fire {Reach(fire)} "
               + $"{Warmth(fire):+#;-#;0}, lantern {Reach(lantern)} {Warmth(lantern):+#;-#;0}, "
               + $"lamp {Reach(lamp)} {Warmth(lamp):+#;-#;0}; glass dims 0 and smokeglass "
               + $"{LightValue.Max} while neither is opaque; {toggles} states swap both ways";

        return faults;
    }

    /// <summary>The name a facing is registered under, in <see cref="Placeable.Facings"/> terms.</summary>
    private static string FacingName(int face) => face switch
    {
        Faces.PosX => "east",
        Faces.NegX => "west",
        Faces.PosZ => "south",
        _ => "north",
    };

    /// <summary>
    /// Fixes things to walls, floors and ceilings, then takes each away and checks what falls.
    /// </summary>
    /// <remarks>
    /// <para>The pass has to answer three separate claims and each is asked on its own. That what
    /// needs holding up says so — a table where nothing declares a support face would make every
    /// other assertion here vacuously true, so the count is checked first. That taking the support
    /// away brings the thing down and leaves the right item. And that a support still there leaves
    /// it alone, which is the control: a pass that simply cleared its neighbours would satisfy every
    /// "did it fall" test ever written.</para>
    /// <para>The cascade is the reason the pass has a queue instead of a ring, so it is measured
    /// rather than argued: a stack four deep is built out of things that hold each other, the block
    /// under all of it is removed, and every one of the four has to come down from that single edit.
    /// A ring would take the bottom one and leave three hanging.</para>
    /// </remarks>
    private static List<string> SupportSelfTest(
        BlockRegistry registry, ItemRegistry items, BlockDrops drops, out string detail)
    {
        var faults = new List<string>();
        var table = new SupportTable(registry);
        var fell = new List<(int X, int Y, int Z, BlockId Was)>();

        // Nothing needs holding up: every claim below would pass on an empty table.
        if (table.Supported == 0)
            faults.Add("no block in the whole registry says what holds it up, so this checks nothing");

        var stone = registry.ByName("stone").Id;
        var pane = StarterBlocks.Connected(registry, "glass_pane")[0];
        var torch = registry.ByName("torch").Id;
        var wallTorch = registry.ByName("torch_wall_east").Id;
        var lantern = registry.ByName("lantern_hanging").Id;

        // A wall with a torch on its east face, and a torch standing on top of it.
        var world = new VoxelWorld(registry);
        world.SetBlock(0, 64, 0, stone);
        world.SetBlock(1, 64, 0, wallTorch);
        world.SetBlock(0, 65, 0, torch);

        if (!table.Holds(world, 1, 64, 0)) faults.Add("a torch on a wall was not being held by it");
        if (!table.Holds(world, 0, 65, 0)) faults.Add("a torch on a floor was not being held by it");

        // The control: an edit two cells away must move nothing at all.
        fell.Clear();
        world.SetBlock(4, 64, 0, stone);
        if (table.Shed(world, 4, 64, 0, fell) != 0)
            faults.Add($"putting a block down two cells away brought {fell.Count} things off a wall it never touched");

        // Now take the wall out from under both of them.
        fell.Clear();
        world.SetBlock(0, 64, 0, BlockId.Air);
        table.Shed(world, 0, 64, 0, fell);

        if (!world.GetBlock(1, 64, 0).IsAir) faults.Add("a wall torch stayed in the air after its wall was mined");
        if (!world.GetBlock(0, 65, 0).IsAir) faults.Add("a standing torch stayed in the air after its floor was mined");
        if (fell.Count != 2) faults.Add($"{fell.Count} things came down where two were held up");

        // And what came down has to become something. A torch that falls and leaves nothing is a
        // torch a player has lost to a rule nobody told them about.
        foreach (var (_, _, _, was) in fell)
        {
            if (drops.Of(was).IsEmpty)
                faults.Add($"{registry[was].Name} came down and left nothing on the floor");
            else if (items[drops.Of(was).Item].Name != "torch")
                faults.Add($"{registry[was].Name} came down and left {drops.Describe(was)} rather than a torch");
        }

        // A pane is solid and is not a whole face, so nothing fixes itself to one — the difference
        // between "solid" and "something to hang off" is the whole reason the two tests are separate.
        var thin = new VoxelWorld(registry);
        thin.SetBlock(0, 64, 0, pane);
        thin.SetBlock(1, 64, 0, wallTorch);
        if (table.Holds(thin, 1, 64, 0))
            faults.Add("a torch hung off a pane of glass, which has no face to hold it");

        // But a pane will hold something standing on it, which is what a foot rests on.
        thin.SetBlock(0, 65, 0, torch);
        if (!table.Holds(thin, 0, 65, 0))
            faults.Add("a torch would not stand on a pane of glass, which is something to stand on");

        // A ceiling holds what hangs from it, and stops doing so when it goes.
        var roof = new VoxelWorld(registry);
        roof.SetBlock(0, 66, 0, stone);
        roof.SetBlock(0, 65, 0, lantern);
        if (!table.Holds(roof, 0, 65, 0)) faults.Add("a hanging lantern was not being held by the ceiling");

        fell.Clear();
        roof.SetBlock(0, 66, 0, BlockId.Air);
        table.Shed(roof, 0, 66, 0, fell);
        if (!roof.GetBlock(0, 65, 0).IsAir) faults.Add("a lantern kept hanging from a ceiling that was gone");

        // The cascade, and the reason there is a queue. Four torches each standing on the one below
        // — which is what a door is, and what a ring pass would get wrong by three.
        var stack = new VoxelWorld(registry);
        stack.SetBlock(0, 63, 0, stone);
        for (var y = 64; y < 68; y++) stack.SetBlock(0, y, 0, torch);

        fell.Clear();
        stack.SetBlock(0, 63, 0, BlockId.Air);
        var cascaded = table.Shed(stack, 0, 63, 0, fell);

        if (cascaded != 4)
            faults.Add($"a stack four deep lost {cascaded} of its four when the block under it went — "
                     + "the pass is not following what was leaning on what");

        // A door is two cells and one door, and it has to come apart that way from either end.
        // Three edits, because the three are three different paths through the pass: the top, the
        // bottom, and the floor under both.
        var doors = StarterBlocks.Doors(registry);
        var lower = doors[0];
        var upper = doors[1];

        if (registry[lower].PartnerFace != Faces.PosY || registry[upper].PartnerFace != Faces.NegY)
            faults.Add("a door's two halves do not name each other, so nothing below tests anything");

        foreach (var (struck, what) in ((int Y, string What)[])[(66, "its top"), (65, "its bottom"), (64, "the floor")])
        {
            var frame = new VoxelWorld(registry);
            frame.SetBlock(0, 64, 0, stone);
            frame.SetBlock(0, 65, 0, lower);
            frame.SetBlock(0, 66, 0, upper);

            if (!table.Holds(frame, 0, 65, 0) || !table.Holds(frame, 0, 66, 0))
                faults.Add("a whole door standing on stone was not being held up");

            fell.Clear();
            frame.SetBlock(0, struck, 0, BlockId.Air);
            table.Shed(frame, 0, struck, 0, fell);

            if (!frame.GetBlock(0, 65, 0).IsAir || !frame.GetBlock(0, 66, 0).IsAir)
                faults.Add($"breaking {what} left half a door standing");
        }

        detail = $"{table.Supported} of {registry.Count} blocks say what holds them, on walls, floors "
               + $"and ceilings; a pane holds a foot and not a fixing, {cascaded} of a stack of 4 "
               + "cascade off one edit, a door comes apart whole from either end, and an edit two "
               + "cells away moves nothing";

        return faults;
    }

    /// <summary>Whether any part of a model reaches the cell wall on one side.</summary>
    private static bool ReachesEdge(BlockModel model, int face)
    {
        const float Edge = 0.999f;

        foreach (var element in model.Elements)
        {
            var from = element.From / 16f;
            var to = element.To / 16f;

            var reaches = face switch
            {
                Faces.PosX => to.X >= Edge,
                Faces.NegX => from.X <= 1f - Edge,
                Faces.PosZ => to.Z >= Edge,
                _ => from.Z <= 1f - Edge,
            };

            if (reaches) return true;
        }

        return false;
    }

    /// <summary>
    /// Reads the whole font back: right characters, all inked, none of them the same as another.
    /// </summary>
    /// <remarks>
    /// <para>A hand-drawn table of ninety-five entries fails in three ways that all look like
    /// working software. One glyph dropped shifts every letter after it and the game spells
    /// nonsense — caught by asking each row which character it claims to be. One glyph left blank
    /// puts a hole in a word — caught by counting ink. And one glyph pasted from the row above puts
    /// the wrong letter in every word containing it, which is the only one of the three a human
    /// reading the table would ever miss, so the check compares every pair.</para>
    /// <para>The pair comparison is the point of the whole check. <c>O</c> and <c>0</c> are
    /// deliberately different, and a font where they are not is a font that cannot show a seed.</para>
    /// </remarks>
    private static List<string> FontSelfTest(out string detail)
    {
        var faults = new List<string>();
        var tiles = TileGen.Font();
        var advance = TileGen.FontAdvance();

        if (tiles.Length != TileGen.GlyphCount)
            faults.Add($"the font has {tiles.Length} glyphs, not the {TileGen.GlyphCount} it claims");

        var inked = 0;
        var widest = 0;
        var narrowest = int.MaxValue;

        for (var i = 0; i < tiles.Length; i++)
        {
            var wanted = (char)(TileGen.FirstGlyph + i);
            if (TileGen.GlyphChar(i) != wanted)
                faults.Add($"glyph {i} says it is '{TileGen.GlyphChar(i)}' where '{wanted}' belongs");

            var ink = 0;
            for (var p = 3; p < tiles[i].Length; p += 4) if (tiles[i][p] > 0) ink++;

            if (wanted == ' ')
            {
                if (ink > 0) faults.Add($"the space has {ink} lit pixels in it");
            }
            else if (ink == 0)
            {
                faults.Add($"'{wanted}' is blank");
            }
            else
            {
                inked++;
            }

            if (advance[i] is < 4 or > 14)
                faults.Add($"'{wanted}' advances {advance[i]} pixels, which is outside 4 to 14");

            widest = Math.Max(widest, advance[i]);
            narrowest = Math.Min(narrowest, advance[i]);
        }

        // Every pair. Four and a half thousand comparisons of a hundred-odd bytes is nothing, and it
        // is the only thing that catches a row pasted from the one above it.
        var twins = 0;
        for (var a = 0; a < tiles.Length; a++)
        for (var b = a + 1; b < tiles.Length; b++)
        {
            if (!tiles[a].AsSpan().SequenceEqual(tiles[b])) continue;
            twins++;
            if (twins <= 3)
                faults.Add($"'{TileGen.GlyphChar(a)}' and '{TileGen.GlyphChar(b)}' are drawn identically");
        }

        detail = $"{tiles.Length} glyphs, {inked} with ink, none alike, "
               + $"advancing {narrowest} to {widest} pixels";

        return faults;
    }

    /// <summary>
    /// Writes settings out, reads them back, and checks nothing changed on the way.
    /// </summary>
    /// <remarks>
    /// <para>Every fault here is one a player meets after they have already stopped looking. A
    /// binding that does not survive a save is noticed on the next launch, when there is nothing on
    /// screen connecting it to what they did; a value silently clamped to a default is noticed
    /// never. So the round trip is checked against values that are all deliberately <em>not</em> the
    /// defaults — a writer that writes nothing at all passes a round trip of default values.</para>
    /// <para>Nothing touches the disk. The file's text is the thing being checked and it is
    /// generated in memory, so this runs on a machine with no home directory and cannot leave
    /// somebody's real settings behind it.</para>
    /// </remarks>
    private static List<string> SettingsSelfTest(out string detail)
    {
        var faults = new List<string>();

        var defaults = new GameSettings();
        foreach (var fault in defaults.Keys.Faults()) faults.Add($"the shipped keys: {fault}");

        // Every value moved off its default, so a writer that emits nothing cannot pass.
        var written = new GameSettings
        {
            ViewDistance = 14,
            FieldOfView = 96,
            Fullscreen = true,
            VSync = true,
            Volume = 43,
            Mute = true,
            MouseSensitivity = 175,
            Keys = Bindings.Defaults(),
        };

        written.Keys.Bind(GameAction.Jump, "Y");
        written.Keys.Bind(GameAction.MoveForward, "I");

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "driftwood-settings-check.txt");
        var text = written.Write();

        GameSettings read;
        try
        {
            File.WriteAllText(path, text);
            read = GameSettings.Load(path);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }

        if (read.ViewDistance != 14) faults.Add($"view distance came back {read.ViewDistance}, not 14");
        if (read.FieldOfView != 96) faults.Add($"field of view came back {read.FieldOfView}, not 96");
        if (!read.Fullscreen) faults.Add("fullscreen came back off");
        if (!read.VSync) faults.Add("vsync came back off");
        if (read.Volume != 43) faults.Add($"volume came back {read.Volume}, not 43");
        if (!read.Mute) faults.Add("mute came back off");
        if (read.MouseSensitivity != 175) faults.Add($"sensitivity came back {read.MouseSensitivity}, not 175");

        foreach (var action in GameActions.All)
        {
            if (read.Keys.Primary(action) == written.Keys.Primary(action)
                && read.Keys.Secondary(action) == written.Keys.Secondary(action)) continue;

            faults.Add(
                $"'{GameActions.Label(action)}' went out as '{written.Keys.Describe(action)}' "
                + $"and came back as '{read.Keys.Describe(action)}'");
        }

        foreach (var fault in read.Keys.Faults()) faults.Add($"after a round trip: {fault}");

        // Binding steals rather than refusing, so the key it took has to actually be gone.
        var stealing = Bindings.Defaults();
        var had = stealing.Primary(GameAction.Jump);
        stealing.Bind(GameAction.Sneak, had);

        if (stealing.Primary(GameAction.Jump) == had)
            faults.Add($"binding '{had}' to sneak left it on jump as well");
        if (stealing.ActionFor(had) != GameAction.Sneak)
            faults.Add($"'{had}' does not run sneak after being bound to it");

        // A file from an older build names actions that no longer exist. The first line that IS
        // recognised throws away the defaults, so without filling the gaps afterwards a renamed
        // action ends up with no key on it and nothing on screen saying so — a player who upgrades
        // simply cannot open their inventory. This is the check that a rename stays survivable.
        var stale = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "driftwood-settings-stale.txt");
        try
        {
            File.WriteAllText(
                stale,
                "bind.moveforward=Up\nbind.moveforward.2=W\nbind.openscreen=E\nbind.releasemouse=Escape\n");

            var upgraded = GameSettings.Load(stale);
            foreach (var fault in upgraded.Keys.Faults())
                faults.Add($"after loading a file from an older build: {fault}");

            if (upgraded.Keys.Primary(GameAction.MoveForward) != "Up")
                faults.Add("a binding the old file DID name was lost on the way through");
        }
        finally
        {
            try { File.Delete(stale); } catch (IOException) { }
        }

        // A file that says nothing about keys keeps the shipped ones rather than ending up with none.
        var bare = GameSettings.Load(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "driftwood-settings-absent.txt"));
        if (bare.Keys.Faults().Count > 0)
            faults.Add("a missing settings file left the game with no keys on it");

        detail = $"{GameActions.All.Length} actions and 7 settings out and back unchanged, "
               + "a rebind takes the key off whatever had it, a missing file keeps the shipped keys";

        return faults;
    }

    /// <summary>
    /// Puts things in a player's pockets and checks the game notices exactly once.
    /// </summary>
    /// <remarks>
    /// <para>Three things can go wrong and only one of them is visible in a running game. Saying
    /// nothing when something opens up is noticed, eventually. Saying it again every time the
    /// player picks up another log is noticed immediately and is infuriating. And doing the work on
    /// a frame where nothing changed is noticed never, which is why the version gate is checked
    /// here rather than trusted.</para>
    /// <para>Priming is checked too. A world that started with a full inventory would otherwise
    /// announce forty recipes at once, which is every one of them and therefore none.</para>
    /// </remarks>
    private static List<string> UnlockSelfTest(
        BlockRegistry registry, ItemRegistry items, RecipeBook book, out string detail)
    {
        var faults = new List<string>();
        var found = new List<Recipe>();

        var pockets = new Inventory(items);
        var watch = new RecipeUnlocks();

        // Nothing in hand, nothing to say.
        watch.Poll(book, pockets, found);
        if (found.Count != 0) faults.Add($"an empty inventory announced {found.Count} recipes");

        // A log opens planks, and nothing else.
        pockets.Add(items.Stack("driftoak_log", 1));
        watch.Poll(book, pockets, found);

        if (found.Count == 0) faults.Add("picking up a log announced nothing, and it makes planks");
        else if (found.Count > 2) faults.Add($"one log announced {found.Count} recipes at once");

        var announcedPlanks = false;
        foreach (var recipe in found) announcedPlanks |= recipe.Result.Item.Value == items.ByName("driftoak_planks").Id.Value;
        if (!announcedPlanks) faults.Add("picking up a log did not announce planks");

        // A second log says nothing, because planks were already possible.
        pockets.Add(items.Stack("driftoak_log", 1));
        watch.Poll(book, pockets, found);
        if (found.Count != 0) faults.Add($"a second log announced {found.Count} recipes again");

        // And a frame where nothing moved does not even look.
        if (watch.Poll(book, pockets, found)) faults.Add("an unchanged inventory was searched anyway");

        // Once said, never said again — even after the ingredients go and come back.
        var before = watch.Announced;
        pockets.Clear();
        watch.Poll(book, pockets, found);
        pockets.Add(items.Stack("driftoak_log", 4));
        watch.Poll(book, pockets, found);

        if (found.Count != 0) faults.Add($"losing and regaining logs announced {found.Count} recipes over again");
        if (watch.Announced != before) faults.Add("the set of things already said changed while nothing new happened");

        // Priming a full inventory says nothing at all, and then a genuinely new thing still does.
        var rich = new Inventory(items);
        rich.Add(items.Stack("driftoak_planks", 64));
        rich.Add(items.Stack("stick", 64));

        var quiet = new RecipeUnlocks();
        quiet.Prime(book, rich);
        quiet.Poll(book, rich, found);
        if (found.Count != 0) faults.Add($"a primed inventory still announced {found.Count} recipes");

        var primed = quiet.Announced;
        if (primed == 0) faults.Add("priming a bench-worth of planks and sticks marked nothing as known");

        rich.Add(items.Stack("rubble", 8));
        quiet.Poll(book, rich, found);
        if (found.Count == 0) faults.Add("a primed watcher went quiet for good and missed the stone tools");

        // ⛳ AND THE TWO HALVES OF "ONCE PER WORLD", WHICH ARE DIFFERENT CLAIMS AND BOTH MATTER.
        //
        // The first is that an achievement outlives the process: a watcher that starts fresh every
        // launch passes every check above and still tells a returning player about planks for the
        // tenth time, which is how somebody learns to stop reading the corner.
        //
        // The second is that it does NOT outlive the world. This was a file beside the settings
        // while there was one world and no saves, which made "ever" and "this world" the same
        // sentence. They are not the same sentence any more, and a returning player and a brand new
        // world must get opposite answers out of the same code — so both are asked here, from the
        // same starting point, and a build that got either one right by doing nothing fails the
        // other.
        var name = $"driftwood-audit-unlocks-{Environment.ProcessId}";

        try
        {
            var first = new RecipeUnlocks();
            var pocket = new Inventory(items);
            pocket.Add(items.Stack("driftoak_log", 1));
            first.Poll(book, pocket, found);

            if (found.Count == 0) faults.Add("nothing to keep: a log announced nothing");
            if (!first.Dirty) faults.Add("something was announced and the record did not think it had changed");

            var kept = new WorldState(
                "unlocks", items, new VoxelWorld(registry),
                new FurnaceBank(items, book), new ChestBank(items),
                new Inventory(items), new Equipment(items), new PlayerVitals(registry), first);

            if (WorldSave.Write(name, kept) is { } wrote)
                faults.Add($"a world carrying what has been said would not write: {wrote}");

            // The same world again.
            var sameWorld = new RecipeUnlocks();
            var back = new WorldState(
                "", items, new VoxelWorld(registry),
                new FurnaceBank(items, book), new ChestBank(items),
                new Inventory(items), new Equipment(items), new PlayerVitals(registry), sameWorld);

            if (WorldSave.Read(WorldSave.PathFor(name), registry, items, back, []) is { } read)
                faults.Add($"a world carrying what has been said would not read: {read}");

            if (sameWorld.Announced != first.Announced)
                faults.Add($"{first.Announced} things were said and {sameWorld.Announced} came back with the world");

            var returning = new Inventory(items);
            returning.Add(items.Stack("driftoak_log", 1));
            sameWorld.Poll(book, returning, found);
            if (found.Count != 0)
                faults.Add($"reloading a world told the player about {found.Count} recipes all over again");

            // ⚠ A DIFFERENT world, from the same log, and it has to say them all again. Nothing was
            // loaded into this one, which is exactly what starting a new game does.
            var newWorld = new RecipeUnlocks();
            var elsewhere = new Inventory(items);
            elsewhere.Add(items.Stack("driftoak_log", 1));
            newWorld.Poll(book, elsewhere, found);

            if (found.Count == 0)
                faults.Add(
                    "a brand new world said nothing, so what has been announced is being remembered "
                    + "per installation rather than per world");

            // And forgetting puts a world back to a new one.
            sameWorld.Forget();
            var third = new Inventory(items);
            third.Add(items.Stack("driftoak_log", 1));
            sameWorld.Poll(book, third, found);
            if (found.Count == 0) faults.Add("forgetting what had been said announced nothing afterwards");
        }
        finally
        {
            try { File.Delete(WorldSave.PathFor(name)); } catch (IOException) { }
        }

        detail = $"an empty bag says nothing, a log says planks once, a second log says nothing, "
               + $"an unchanged bag is not searched, priming marks {primed} known without a word, "
               + "a reloaded world is not told twice, and a new one is told everything again";

        return faults;
    }

    /// <summary>How many distinct things a player can put down.</summary>
    private static int Placeables(ItemRegistry items)
    {
        var count = 0;
        foreach (var item in items.All) if (item.Places is not null) count++;
        return count;
    }

    /// <summary>
    /// Walks the whole item catalogue against the whole block table and checks they agree.
    /// </summary>
    /// <remarks>
    /// <para>Two registries built one after the other is two chances to disagree, and none of the
    /// disagreements are visible: an item whose icon layer does not exist draws magenta somewhere
    /// nobody is looking, a block that leaves an item nothing places is a one-way trip, and a
    /// placeable whose variants belong to two different items is a stair that turns into a slab
    /// when you pick it up.</para>
    /// <para>The count of things that leave nothing is checked as a band rather than a floor. Zero
    /// means the foliage rules were dropped; most of the table means the defaults stopped working
    /// and every block silently became unobtainable.</para>
    /// </remarks>
    private static List<string> ItemCatalogueSelfTest(
        BlockRegistry registry, ItemRegistry items, BlockDrops drops)
    {
        var faults = new List<string>();

        for (ushort id = 1; id < items.Count; id++)
        {
            var item = items[id];

            if (item.IconLayer >= StarterBlocks.LayerCount)
                faults.Add($"'{item.Name}' wears layer {item.IconLayer}, past the {StarterBlocks.LayerCount} that exist");

            if (item.MaxStack is < 1 or > ItemStack.MaxCount)
                faults.Add($"'{item.Name}' stacks to {item.MaxStack}");

            if (item.IsTool && item.Durability <= 0)
                faults.Add($"tool '{item.Name}' never wears out");

            if (item.Places is { } places && places.Variants.Length == 0)
                faults.Add($"'{item.Name}' places nothing");

            // A thing drawn as a cube has to have a cube to be drawn as.
            if (item.DrawsAsBlock && item.PlainBlock.IsAir)
                faults.Add($"'{item.Name}' draws as a cube and puts no block down");
        }

        // Every block that leaves something leaves something real, and every placeable block is
        // reachable back through what it leaves.
        for (ushort id = 1; id < registry.Count; id++)
        {
            var block = registry[id];
            var left = drops.Of(block.Id);
            if (left.IsEmpty) continue;

            if (left.Item.Value >= items.Count)
            {
                faults.Add($"'{block.Name}' leaves item {left.Item.Value}, past the {items.Count} that exist");
                continue;
            }

            if (block.Unbreakable)
                faults.Add($"'{block.Name}' can never be broken and still leaves {items[left.Item].Name}");
        }

        // Nothing places a block that nothing leaves — a one-way trip out of the inventory.
        foreach (var item in items.All)
        {
            if (item.Places is not { } places) continue;

            foreach (var variant in places.Variants)
            {
                var back = drops.Of(variant);
                if (!back.IsEmpty) continue;

                // Half of a two-cell block is allowed to leave nothing, and has to: one door out
                // of the pockets must not come back as two. The exemption is not a free pass —
                // the other half is asked, so a door where BOTH halves leave nothing still fails,
                // which is the failure that would actually cost a player their door.
                var block = registry[variant];
                if (block.PartnerFace >= 0)
                {
                    var whole = false;
                    foreach (var other in places.Variants)
                    {
                        if (registry[other].PartnerFace != Placeable.Opposite(block.PartnerFace)) continue;
                        if (drops.Of(other).IsEmpty) continue;
                        whole = true;
                        break;
                    }

                    if (whole) continue;
                    faults.Add($"'{item.Name}' puts down '{block.Name}' and no half of it leaves anything");
                    continue;
                }

                faults.Add($"'{item.Name}' puts down '{block.Name}', which leaves nothing");
            }
        }

        // ⛔ A block in a slot has to be recognisable AS that block, and this is the check written
        // from a user's own report: "I couldn't even tell that was a bench in the crafting window."
        // A bench's icon was its top tile — scored planks — which at sixteen pixels beside plain
        // planks is plain planks. The slot draws three faces now, so what has to be true is that
        // the shape a slot would draw from is actually there, and that a block with three DIFFERENT
        // faces really does offer three different textures to draw.
        var cubes = 0;
        var manyFaced = 0;

        foreach (var item in items.All)
        {
            if (!item.DrawsAsBlock || item.Places is null) continue;

            if (item.IconModel is null)
            {
                faults.Add($"'{item.Name}' draws as a cube and has no shape to draw");
                continue;
            }

            if (!item.IconModel.IsFullCube) continue;
            cubes++;

            var top = item.IconModel.PassLayer(0, Faces.PosY);
            var front = item.IconModel.PassLayer(0, Faces.PosZ);
            var side = item.IconModel.PassLayer(0, Faces.PosX);
            if (top != front && front != side && top != side) manyFaced++;
        }

        if (cubes == 0) faults.Add("no item draws as a cube, so nothing in a slot is drawn as a block");
        if (manyFaced == 0)
            faults.Add("every block that draws as a cube wears one texture on all three visible faces, "
                     + "so a bench and a plank are the same picture");

        var bench = items.ByName("bench");
        if (bench.IconModel is not { IsFullCube: true })
            faults.Add("a bench does not draw as a cube, which is the one that was reported unreadable");
        else if (bench.IconModel.PassLayer(0, Faces.PosZ) == bench.IconModel.PassLayer(0, Faces.PosX))
            faults.Add("a bench's front and side are the same texture, so it still reads as a crate");

        var nothing = drops.BlocksLeavingNothing;
        if (nothing == 0) faults.Add("nothing in the world leaves nothing, so the foliage rules are gone");
        if (nothing > registry.Count / 2)
            faults.Add($"{nothing} of {registry.Count} blocks leave nothing, so the defaults are not running");

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
    private static List<string> VitalsSelfTest(
        BlockRegistry registry, StarterBlocks.Ids ids, out string detail)
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

        // ── Lava burns, and it goes on burning after you get out ────────────────────────────────
        //
        // ⛔ THE CONTROL IS THE SAME BODY IN WATER, and it is not optional: every one of these
        // assertions is also satisfied by "standing still costs health", by "the world hurts you",
        // and by a build where the drowning path fires on any fluid. The two runs differ in one
        // block and nothing else.
        var (lavaHurt, lavaBurnedAfter, lavaDrowned) = Bathe(registry, ids, ids.Lava);
        var (waterHurt, waterBurnedAfter, _) = Bathe(registry, ids, ids.Water);

        if (lavaHurt <= 0) faults.Add("two seconds in lava cost nothing");
        if (waterHurt != 0) faults.Add($"two seconds in water cost {waterHurt}, so the burn check is measuring something else");
        if (!lavaBurnedAfter) faults.Add("stepping out of lava put the fire out immediately");
        if (waterBurnedAfter) faults.Add("stepping out of water left the body alight");

        // ⛔ AND IT MUST NOT DROWN. The test for what a head cannot breathe in used to be derived
        // from three other flags — not solid, not opaque, dims light — which lava satisfies exactly.
        // A build with that derivation passes every check above and quietly has the player holding
        // their breath in molten rock.
        if (lavaDrowned) faults.Add("a head in lava is treated as being under water");

        detail = $"two seconds in lava costs {lavaHurt} half-hearts and goes on burning after, "
               + $"the same two in water costs {waterHurt} and does not";

        return faults;
    }

    /// <summary>
    /// Drops a body into a lake and asks whether it can get out of it.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Every claim here is also satisfied by "nothing happens", so every one has its dry twin.</b>
    /// "It did not take fall damage" passes on a build with no fall damage; "it rose" passes on a
    /// build where gravity is off; "it sank slowly" passes on one where nothing moves at all. The
    /// same drop onto stone, and the same body in air, are what give the numbers meaning.
    /// </remarks>
    private static List<string> SwimSelfTest(
        BlockRegistry registry, StarterBlocks.Ids ids, out string detail)
    {
        const float Step = 1f / 60f;

        var faults = new List<string>();

        var world = new VoxelWorld(registry);
        for (var cy = 0; cy <= 1; cy++)
            world.GetOrCreateChunk(new ChunkPos(0, cy, 0));

        // A pool eight deep with a stone shelf beside it, both under thirty blocks of air.
        for (var z = 0; z <= 20; z++)
        for (var x = 0; x <= 20; x++)
        {
            Put(world, x, 0, z, ids.Stone);
            for (var y = 1; y <= 8; y++) Put(world, x, y, z, x < 12 ? ids.Water : ids.Stone);
        }

        var wet = new PlayerBody(registry);
        var wetVitals = new PlayerVitals(registry);
        wet.Teleport(new Vector3(5.5f, 40f, 5.5f));

        var dry = new PlayerBody(registry);
        var dryVitals = new PlayerVitals(registry);
        dry.Teleport(new Vector3(16.5f, 40f, 16.5f));

        for (var i = 0; i < 60 * 8; i++)
        {
            wet.Step(world, Step, Vector3.Zero, false, false, false);
            wetVitals.Update(world, wet, Step);
            dry.Step(world, Step, Vector3.Zero, false, false, false);
            dryVitals.Update(world, dry, Step);
        }

        var wetHurt = PlayerVitals.MaxHealth - wetVitals.Health;
        var dryHurt = PlayerVitals.MaxHealth - dryVitals.Health;

        // ⛳ THE CONTROL. The dry body fell exactly as far onto stone and has to be hurt by it, or
        // "the water broke the fall" is measuring a build with no fall damage in it.
        if (dryHurt <= 0)
            faults.Add("a thirty-block drop onto stone cost nothing, so the water is proving nothing");

        // The wet one will have drowned by eight seconds — breath is five — so the fall is judged on
        // the first second, before the lungs come into it.
        var early = new PlayerBody(registry);
        var earlyVitals = new PlayerVitals(registry);
        early.Teleport(new Vector3(5.5f, 40f, 5.5f));

        for (var i = 0; i < 60 * 4; i++)
        {
            early.Step(world, Step, Vector3.Zero, false, false, false);
            earlyVitals.Update(world, early, Step);
        }

        if (earlyVitals.Health != PlayerVitals.MaxHealth)
            faults.Add($"a thirty-block drop into eight blocks of water cost {PlayerVitals.MaxHealth - earlyVitals.Health}");

        if (!early.InWater) faults.Add("the body that fell in the lake does not think it is in water");

        // It sinks with nothing pressed, and not at a stone's rate.
        var sankFrom = early.Position.Y;
        for (var i = 0; i < 60; i++) early.Step(world, Step, Vector3.Zero, false, false, false);
        var sank = sankFrom - early.Position.Y;

        // And a stroke beats the sink, or there is no getting out of a lake.
        var roseFrom = early.Position.Y;
        for (var i = 0; i < 60; i++) early.Step(world, Step, Vector3.Zero, true, false, false);
        var rose = early.Position.Y - roseFrom;

        if (sank >= PlayerBody.Gravity * 0.5f)
            faults.Add($"a body in water fell {sank:F1} in a second, which is not swimming");

        if (rose <= 0.5f)
            faults.Add($"a stroke lifted the body {rose:F2} in a second, so a lake cannot be climbed out of");

        detail = $"a 30-block drop into water costs 0 where the same drop onto stone costs {dryHurt}; "
               + $"it settles {sank:F1} a second and a stroke lifts it {rose:F1}";

        return faults;
    }

    /// <summary>
    /// Stands a body in one block for two seconds, walks it out, and reports what happened.
    /// </summary>
    private static (int Hurt, bool StillBurning, bool Drowned) Bathe(
        BlockRegistry registry, StarterBlocks.Ids ids, BlockId fill)
    {
        const float Step = 1f / 60f;

        var world = new VoxelWorld(registry);
        world.GetOrCreateChunk(new ChunkPos(0, 0, 0));

        for (var z = 0; z <= 6; z++)
        for (var x = 0; x <= 6; x++)
        {
            Put(world, x, 4, z, ids.Stone);
            for (var y = 5; y <= 8; y++) Put(world, x, y, z, fill);
            for (var y = 9; y <= 14; y++) Put(world, x, y, z, BlockId.Air);
        }

        // Dry land to climb out onto, three cells over.
        for (var y = 5; y <= 14; y++) Put(world, 10, y, 3, BlockId.Air);
        Put(world, 10, 4, 3, ids.Stone);

        var body = new PlayerBody(registry);
        var vitals = new PlayerVitals(registry);
        body.Teleport(new Vector3(3.5f, 5f, 3.5f));

        var drowned = false;
        for (var i = 0; i < 120; i++)
        {
            vitals.Update(world, body, Step);
            drowned |= vitals.Submerged;
        }

        var hurt = PlayerVitals.MaxHealth - vitals.Health;

        // Out, and dry. Whatever happens next is the burn rather than the bath.
        body.Teleport(new Vector3(10.5f, 5f, 3.5f));
        vitals.Update(world, body, Step);
        var burning = vitals.Burning;

        return (hurt, burning, drowned);
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

        // A leaf takes its time and does not fall straight. Both are the point of it: one that
        // drops like a chip of stone reads as a chip of stone, and one that falls in a line reads
        // as a dropped item. Measured against a chip released from the same height, because that
        // is the comparison that says "slower" rather than merely "slow".
        var open = new VoxelWorld(registry);
        var leaves = registry[ids.Leaves];

        var drifting = new ParticleSystem(registry, 0x1EAF);
        drifting.Leaf(leaves, new Vector3(0.5f, 60f, 0.5f));
        var leafFrom = drifting.Live.Length > 0 ? drifting.Live[0].Position : Vector3.Zero;
        for (var s = 0; s < 120; s++) drifting.Update(open, 1f / 60f);

        var tumbling = new ParticleSystem(registry, 0x1EAF);
        tumbling.Burst(leaves, 0, 60, 0, 1);
        var chipFrom = tumbling.Live.Length > 0 ? tumbling.Live[0].Position : Vector3.Zero;
        for (var s = 0; s < 120; s++) tumbling.Update(open, 1f / 60f);

        if (drifting.Count != 1)
        {
            faults.Add("a leaf did not survive two seconds of falling");
        }
        else
        {
            var leaf = drifting.Live[0];
            var fell = leafFrom.Y - leaf.Position.Y;
            var wandered = MathF.Sqrt(
                (leaf.Position.X - leafFrom.X) * (leaf.Position.X - leafFrom.X)
                + (leaf.Position.Z - leafFrom.Z) * (leaf.Position.Z - leafFrom.Z));

            if (fell is < 0.2f or > 4f) faults.Add($"a leaf fell {fell:F2} blocks in two seconds, wanted 0.2 to 4");
            if (wandered < 0.15f) faults.Add($"a leaf wandered {wandered:F2} blocks sideways, which is a straight line");

            if (tumbling.Count == 1 && fell >= chipFrom.Y - tumbling.Live[0].Position.Y)
                faults.Add($"a leaf fell {fell:F2} blocks, no slower than a chip did");
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
    private static List<string> PlacementSelfTest(BlockRegistry registry, ItemRegistry items)
    {
        var faults = new List<string>();

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

        // What each two-state block swings to, so a shut door can be asked where it would open.
        var swung = new Dictionary<ushort, BlockId>();
        foreach (var (from, to) in StarterBlocks.Toggles(registry)) swung[from.Value] = to;

        foreach (var item in items.All)
        {
            if (item.Places is not { } entry) continue;

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

                // Attached refuses a ceiling and takes everything else, which is the one kind whose
                // answer to "can it go here" is neither always yes nor one face.
                if (entry.Kind == PlacementKind.Attached && face == Faces.NegY)
                {
                    if (placed) faults.Add($"{where}: fixed itself to a ceiling");
                    continue;
                }

                // A wall and nowhere else. Both halves of that are checked: a ladder that took the
                // floor and one that refused a wall are the same bug from opposite sides.
                if (entry.Kind == PlacementKind.Wall)
                {
                    var onWall = Array.IndexOf(Placeable.Facings, face) >= 0;
                    if (placed != onWall)
                        faults.Add($"{where}: {(placed ? "fixed itself to a floor or a ceiling" : "refused a wall")}");
                    if (!placed) continue;

                    var says = registry[id].SupportFace;
                    if (says != Placeable.Opposite(face))
                        faults.Add($"{where}: says it is held on face {says}, wanted {Placeable.Opposite(face)}");

                    continue;
                }

                if (entry.Kind == PlacementKind.Door && face == Faces.NegY)
                {
                    if (placed) faults.Add($"{where}: hung a door from a ceiling");
                    continue;
                }

                if (!placed)
                {
                    faults.Add($"{where}: refused to place at all");
                    continue;
                }

                if (entry.Kind == PlacementKind.Plain) continue;

                var model = registry[id].Model;

                // The two kinds that hang off something. Both claims are checked, and separately:
                // that the form which came back says it is held on the side that was struck, and
                // that its shape is actually over there. The first alone would pass a variant table
                // shuffled into the wrong order as long as the labels moved with it; the second
                // alone would pass a wall torch built for the right wall and handed out for the
                // wrong one. It takes both to say a torch leans out of the wall it was put on.
                if (entry.Kind is PlacementKind.Attached or PlacementKind.Hung)
                {
                    var wall = Array.IndexOf(Placeable.Facings, face);
                    var want = entry.Kind == PlacementKind.Attached
                        ? wall >= 0 ? Placeable.Opposite(face) : Faces.NegY
                        : face == Faces.NegY ? Faces.PosY : Faces.NegY;

                    var says = registry[id].SupportFace;
                    if (says != want)
                    {
                        faults.Add($"{where}: says it is held on face {says}, wanted {want}");
                        continue;
                    }

                    var lean = LeanOf(model, want);
                    if (lean < 0.05f)
                        faults.Add($"{where}: held on face {want} but its shape sits {lean:F3} that way "
                                 + "— it is not against what is meant to be holding it");

                    continue;
                }

                // ⚠ A door's hinge cannot be read off the door. Shut, both hinges are the same box
                // on the same edge — so a variant table that hands out the wrong one is invisible
                // until somebody opens it, which is exactly when it matters. The check opens it:
                // it follows the toggle to the swung form and asks which way that leans, which is
                // the only place the hinge is a fact about the geometry rather than about a name.
                if (entry.Kind == PlacementKind.Door)
                {
                    var wantFace = Opposite(wantFacing);
                    var wantHinge = Placeable.Hinges(wantFace)[
                        wantFace is Faces.PosX or Faces.NegX
                            ? look.Z >= 0f ? 0 : 1
                            : look.X >= 0f ? 0 : 1];

                    if (registry[id].PartnerFace != Faces.PosY)
                        faults.Add($"{where}: put down a half that does not grow upward");

                    if (LeanOf(model, wantFace) < 0.05f)
                        faults.Add($"{where}: shut, its panel is not on the {wantFace} side of the cell");

                    if (!swung.TryGetValue(id.Value, out var open))
                        faults.Add($"{where}: has no open form to swing to");
                    else if (LeanOf(registry[open].Model, wantHinge) < 0.05f)
                        faults.Add($"{where}: opens away from the {wantHinge} side it is hinged on");

                    continue;
                }

                // Something lying along an axis has no halves and no front either. What has to be
                // true is that its long boxes run the way the player was looking — read off the
                // model, because "it picked variant 0" only says the table was indexed.
                if (entry.Kind == PlacementKind.Axis)
                {
                    var wantAxis = wantFacing is Faces.PosX or Faces.NegX ? 0 : 2;
                    var gotAxis = LongAxis(model);

                    if (gotAxis != wantAxis)
                        faults.Add($"{where}: lies along axis {gotAxis}, wanted {wantAxis}");

                    continue;
                }

                // A facing block has no halves. Its face has to end up looking back at whoever put
                // it down, which is the opposite cardinal to the one a stair's step points along —
                // and getting that backwards is a furnace you have to walk round to use.
                if (entry.Kind == PlacementKind.Facing)
                {
                    var front = FrontFace(model);
                    if (front != Opposite(wantFacing))
                        faults.Add($"{where}: face ended up pointing {front}, wanted {Opposite(wantFacing)}");
                    continue;
                }

                var wantUpper = height > 0.5f;
                var gotUpper = SitsInUpperHalf(model);

                if (gotUpper != wantUpper)
                    faults.Add($"{where}: landed in the {(gotUpper ? "upper" : "lower")} half, wanted the other");

                // ⚠ A shut trapdoor is a flat panel and its facing is nowhere in that shape — both
                // facings of a half are the same box. It is only visible once it swings, standing
                // on the hinge edge opposite the way it faces, so that is where the check looks.
                if (entry.Kind == PlacementKind.Trapdoor)
                {
                    if (!swung.TryGetValue(id.Value, out var flap))
                        faults.Add($"{where}: has no open form to swing to");
                    else if (LeanOf(registry[flap].Model, Opposite(wantFacing)) < 0.05f)
                        faults.Add($"{where}: swings up on the wrong edge for a facing of {wantFacing}");

                    continue;
                }

                if (entry.Kind != PlacementKind.Stairs) continue;

                var gotFacing = StepFacing(model);
                if (gotFacing != wantFacing)
                    faults.Add($"{where}: step ended up facing {gotFacing}");
            }
        }

        return faults;
    }

    /// <summary>The cardinal opposite a face, for reading a rule written the other way round.</summary>
    private static int Opposite(int face) => face switch
    {
        Faces.PosX => Faces.NegX,
        Faces.NegX => Faces.PosX,
        Faces.PosZ => Faces.NegZ,
        Faces.NegZ => Faces.PosZ,
        Faces.PosY => Faces.NegY,
        _ => Faces.PosY,
    };

    /// <summary>
    /// Which side of a facing cube wears the odd texture out — read back off the model, not the id.
    /// </summary>
    /// <remarks>
    /// Asking the model which face is different is what makes this a check rather than a
    /// restatement. Comparing the placed id against a table of expected names would pass a build
    /// where the model and the name disagree, which is exactly the failure a facing block invites.
    /// </remarks>
    private static int FrontFace(BlockModel model)
    {
        // The four sides share one layer except the front, so the odd one out is the face. Read
        // from the first element rather than only from the merged-cube tables, because a chest is a
        // facing block that is deliberately not a cube — asking the cube path alone answered -1 for
        // every chest in the world and said nothing about any of them.
        ushort LayerOn(int face) => model.IsFullCube
            ? model.PassLayer(0, face)
            : model.Elements.Count > 0 && model.Elements[0].Faces[face] is { } spec
                ? spec.Layer
                : BlockModel.NoLayer;

        for (var face = 0; face < Faces.Count; face++)
        {
            if (face is Faces.PosY or Faces.NegY) continue;

            var layer = LayerOn(face);
            if (layer == BlockModel.NoLayer) continue;

            var matches = 0;
            for (var other = 0; other < Faces.Count; other++)
            {
                if (other == face || other is Faces.PosY or Faces.NegY) continue;
                if (LayerOn(other) == layer) matches++;
            }

            if (matches == 0) return face;
        }

        return -1;
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

    /// <summary>
    /// How far toward one side of the cell a shape actually sits, from 0 (centred) to 0.5 (flush).
    /// </summary>
    /// <remarks>
    /// <para>Measured off the box a player aims at rather than off every quad, because a torch draws
    /// itself on two planes a whole cell wide and those reach every side of the cell whichever way
    /// it is leaning. The outline is the stick, so this reads where the stick is.</para>
    /// <para>A displacement rather than "does it touch that face": touching is satisfied by a shape
    /// that spans the whole cell and says nothing about which way it leans, which is the vacuous
    /// version of this test. A wall torch built for the opposite wall touches the same faces and
    /// leans the wrong way, so the sign is the part that discriminates.</para>
    /// </remarks>
    private static float LeanOf(BlockModel model, int face)
    {
        var (min, max) = model.Outline;
        var centre = (min + max) * 0.5f;

        var n = Faces.Normals[face];
        var along = n.X != 0 ? centre.X : n.Y != 0 ? centre.Y : centre.Z;
        var toward = n.X + n.Y + n.Z > 0 ? along - 0.5f : 0.5f - along;

        return toward;
    }

    /// <summary>
    /// The axis a shape's long boxes run along — 0 for x, 2 for z, -1 when none of them are long.
    /// </summary>
    /// <remarks>
    /// ⚠ Takes the <em>lowest</em> long boxes, not the first and not the majority. A campfire is two
    /// logs one way crossed by two the other, so counting them comes out even and answers nothing —
    /// which is a check that would have passed both forms of it. What lies on the ground is what a
    /// stack of timber points along, and that is a statement about the shape rather than about the
    /// order somebody happened to write its boxes in.
    /// </remarks>
    private static int LongAxis(BlockModel model)
    {
        var lowest = float.MaxValue;
        var axis = -1;

        foreach (var element in model.Elements)
        {
            var wide = element.To.X - element.From.X >= 15.9f;
            var deep = element.To.Z - element.From.Z >= 15.9f;

            // Long both ways or neither is a box with no direction in it.
            if (wide == deep) continue;
            if (element.From.Y >= lowest) continue;

            lowest = element.From.Y;
            axis = wide ? 0 : 2;
        }

        return axis;
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
    /// <summary>
    /// A handful of layers pinned by name, so the table's order and the constants cannot drift.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>WRITTEN BECAUSE THEY DID DRIFT, AND EVERY OTHER TEXTURE CHECK PASSED IT.</b>
    /// <c>BlockTextureSet.Layers</c> is indexed by the same numbers <c>StarterBlocks</c> hands out,
    /// so its order <em>is</em> the numbering — and the sixteen dyes went in at the end of the array
    /// while <c>LayerFirstDye</c> said 112 and <c>LayerFirstTool</c> said 128. The count was right,
    /// every tile was painted, every cutout had holes; what was wrong was that a pack importing a
    /// wooden pickaxe would have painted it onto white dye and its dye onto a tool. ⚠ One at each end
    /// of every run, which catches an insertion anywhere without listing a hundred and fifty rows.
    /// </remarks>
    private static readonly (ushort Layer, string Name)[] PinnedLayers =
    [
        (StarterBlocks.LayerStone, "stone"),
        (StarterBlocks.LayerBlastFrontLit, "blast_front_lit"),
        (StarterBlocks.LayerEmberbloom, "emberbloom"),
        (StarterBlocks.LayerFirstWool, "wool_white"),
        (StarterBlocks.LayerFirstIcon, "stick"),
        (StarterBlocks.LayerShears, "shears"),
        (StarterBlocks.LayerFirstMeat, "raw_beef"),
        (StarterBlocks.LayerFirstDye, "dye_white"),
        (StarterBlocks.LayerBone, "bone"),
        (StarterBlocks.LayerFirstTool, "wood_pickaxe"),
        ((ushort)(StarterBlocks.LayerFirstFluid - 1), "diamond_sword"),
        (StarterBlocks.LayerWaterFlow, "water_flow"),
        (StarterBlocks.LayerLava, "lava"),
        (StarterBlocks.LayerLavaFlow, "lava_flow"),
        (StarterBlocks.LayerBucket, "bucket"),
        (StarterBlocks.LayerLavaBucket, "lava_bucket"),
        (StarterBlocks.LayerCoalBlock, "coal_block"),
        (StarterBlocks.LayerFlame, "flame"),
        (StarterBlocks.LayerSmoke, "smoke"),

        // ⛳ THIS PIN DID ITS JOB THE DAY THE ARMOUR LANDED. It used to read "the last layer is
        // smoke", which is a true statement about a table until something is appended to it — and
        // twenty-one rows went on the end. Both ends of every run are pinned by their own constant
        // now, so "the last one" is a fact about the shield rather than about whatever is newest.
        (StarterBlocks.LayerFirstArmour, "leather_helmet"),
        ((ushort)(StarterBlocks.LayerFirstShield - 1), "diamond_boots"),
        (StarterBlocks.LayerFirstShield, "shield"),
        ((ushort)(StarterBlocks.LayerFirstShield + StarterBlocks.ShieldCount - 1), "diamond_shield"),

        // ⛳ THE "LAST LAYER" PIN HAS NOW FIRED THREE TIMES IN ONE SESSION and it is right every
        // time: it is a true statement about a table until something is appended, which is what
        // makes it the one pin that catches an append at all. The rule that came out of it is to
        // pin the OLD end by its own constant on the way past — as smoke and the shield are above —
        // so the moving claim only ever covers the newest run.
        (StarterBlocks.LayerSmokerTop, "smoker_top"),
        (StarterBlocks.LayerBarrelSide, "barrel_side"),

        // ⛳ And a fourth firing, exactly as predicted. "The last layer is diamond" was true until
        // paper went on the end of the array; diamond is pinned by its own constant on the way past
        // and the moving claim covers the newest run only.
        (StarterBlocks.LayerDiamond, "diamond"),
        ((ushort)(StarterBlocks.LayerCount - 1), "paper"),
    ];

    private static List<string> TextureSelfTest()
    {
        var faults = new List<string>();
        var built = BlockTextureSet.Build(packPath: null);

        if (BlockTextureSet.Layers.Length != StarterBlocks.LayerCount)
            faults.Add(
                $"the layer table has {BlockTextureSet.Layers.Length} rows "
                + $"against {StarterBlocks.LayerCount} layers");

        foreach (var (layer, expected) in PinnedLayers)
        {
            if (layer >= BlockTextureSet.Layers.Length) continue;

            var actual = BlockTextureSet.Layers[layer].Name;
            if (actual == expected) continue;

            faults.Add($"layer {layer} should be '{expected}' and the table calls it '{actual}'");
        }

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

            // ⛔ AN ICON HAS TO BE A DRAWING RATHER THAN A SPRAY, and this is what a real fault
            // looked like: the feather's vane was walked along one diagonal and filled along another,
            // which only ever reaches one parity class of the grid, so it rendered as a dither field.
            // Every check above passed it — it had colours, it had holes, it was not the placeholder.
            // What it did not have was a SHAPE. Counting the connected islands of ink separates the
            // two: a picture is one island and a few specks, a lattice is dozens.
            if (layer < StarterBlocks.LayerFirstIcon) continue;

            // What the band cannot catch, measured: the widest correct drawing in the set is 3 (the
            // shears, whose blades meet the handles only at the pivot), and the fault that motivated
            // this read 23. Anything between 7 and 22 passes — a drawing that fragments only mildly
            // is exactly what this is blind to, and the icon sheet is what catches those.
            var islands = InkIslands(tile, built.Size);
            if (islands > 6) faults.Add($"{name} is {islands} disconnected pieces of ink, which is a spray not a drawing");
        }

        faults.AddRange(TextureCeilingSelfTest());
        return faults;
    }

    /// <summary>
    /// Checks a requested tile size is held to what the machine will take, and said out loud.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>The ceiling used to be applied on the pack path only.</b> Which is exactly the
    /// wrong path — a player running with no pack at all is the ordinary case, and
    /// <c>--texture-size 4096</c> with no pack asked for two hundred layers of 4096², which is
    /// <b>13.7 GB</b> and a process that dies before its window opens. The pack branch was clamped
    /// because that is the branch where somebody was thinking about resolution; the two that were
    /// not are the two where nobody was.</para>
    /// <para>⛔ <b>Both arms, and the second is the one that matters.</b> "A request over the
    /// ceiling comes down" is satisfied by a build that ignores the request entirely and always
    /// returns the ceiling — which would silently pin every machine to one resolution. Asking that
    /// a request <em>under</em> the ceiling is honoured is what tells a clamp from a constant.</para>
    /// <para>⚠ And each arm checks what was <em>said</em> as well as what was built. Reducing a
    /// number a player typed on their own command line without a word is the failure this exists to
    /// prevent: the only other evidence is that the game looks softer than they asked for.</para>
    /// </remarks>
    private static List<string> TextureCeilingSelfTest()
    {
        var faults = new List<string>();

        var over = BlockTextureSet.Build(packPath: null, size: 512, ceiling: 64);
        if (over.Size != 64)
            faults.Add($"512 asked for against a ceiling of 64 built at {over.Size}");
        if (!over.Summary.Contains("asked 512", StringComparison.Ordinal))
            faults.Add($"a request cut from 512 to 64 said nothing about it: {over.Summary}");

        var under = BlockTextureSet.Build(packPath: null, size: 32, ceiling: 512);
        if (under.Size != 32)
            faults.Add($"32 asked for against a ceiling of 512 built at {under.Size}");
        if (under.Summary.Contains("asked", StringComparison.Ordinal))
            faults.Add($"a request that was honoured claimed it had been cut: {under.Summary}");

        // And the floor, which is the same argument from the other end: our own art is drawn at
        // sixteen and a tile smaller than that is a downscale of the only original there is.
        var tiny = BlockTextureSet.Build(packPath: null, size: 4, ceiling: 512);
        if (tiny.Size != TileGen.Size)
            faults.Add($"4 asked for came back at {tiny.Size} rather than {TileGen.Size}");

        return faults;
    }

    /// <summary>How many connected islands of opaque pixels a tile has.</summary>
    /// <remarks>
    /// Four-connected on purpose. Eight-connectivity joins a checkerboard into one blob — which is
    /// the exact thing this exists to catch — so a diagonal touch does not count as touching.
    /// </remarks>
    private static int InkIslands(byte[] tile, int size)
    {
        var seen = new bool[size * size];
        var stack = new Stack<int>();
        var islands = 0;

        bool Ink(int i) => tile[i * 4 + 3] >= 128;

        for (var start = 0; start < seen.Length; start++)
        {
            if (seen[start] || !Ink(start)) continue;

            islands++;
            stack.Push(start);
            seen[start] = true;

            while (stack.Count > 0)
            {
                var at = stack.Pop();
                var x = at % size;
                var y = at / size;

                for (var side = 0; side < 4; side++)
                {
                    var nx = x + (side == 0 ? -1 : side == 1 ? 1 : 0);
                    var ny = y + (side == 2 ? -1 : side == 3 ? 1 : 0);

                    if (nx < 0 || ny < 0 || nx >= size || ny >= size) continue;

                    var next = ny * size + nx;
                    if (seen[next] || !Ink(next)) continue;

                    seen[next] = true;
                    stack.Push(next);
                }
            }
        }

        return islands;
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

    /// <summary>
    /// That a slab is half a block to walk into and a doorway is a doorway.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Every claim here has a full cube beside it as the control</b>, because the thing
    /// being checked is a <em>difference</em>: collision used to read a <c>bool[]</c> keyed on the
    /// block id, so every one of these shapes behaved exactly as a cube did. A test that only
    /// measured the slab would pass the old build for half of these and read as a sensible number
    /// for the rest — "the body settled at y 41" says nothing at all unless something else settled
    /// at 40.5.</para>
    /// <para>⚠ <b>The fence is the one that is not geometry.</b> It is drawn a block high and
    /// collided with a block and a half high on purpose, so the pair is a body that gets onto a
    /// one-block step and does not get over a fence — which is a claim about the game rather than
    /// about the model, and would silently stop being true if the collision height were dropped.
    /// </para>
    /// </remarks>
    private static List<string> ShapeCollisionSelfTest(
        BlockRegistry registry, ItemRegistry catalogue, StarterBlocks.Ids ids, out string detail)
    {
        var faults = new List<string>();

        const int floorY = 40;
        const float dt = 1f / 60f;
        var top = floorY + 1f;

        var slab = registry.ByName("driftoak_slab_lower").Id;
        var shutTrapdoor = registry.ByName("trapdoor_east_lower").Id;
        var openDoorLower = registry.ByName("door_east_south_lower_open").Id;
        var openDoorUpper = registry.ByName("door_east_south_upper_open").Id;
        var shutDoorLower = registry.ByName("door_east_south_lower").Id;
        var shutDoorUpper = registry.ByName("door_east_south_upper").Id;
        var campfire = StarterBlocks.Campfires(registry, lit: false)[0];

        // Joined north and south, so a line of them has no gap to squeeze through. The mask is a set
        // over Placeable.Facings — +x, -x, +z, -z — and a fence running along z joins on the last two.
        var fence = StarterBlocks.Connected(registry, "driftoak_fence")[0b1100];

        var world = new VoxelWorld(registry);
        for (var z = -6; z < 24; z++)
        for (var x = -6; x < 24; x++)
        {
            world.SetBlock(x, floorY, z, ids.Stone);
            world.SetBlock(x, floorY - 1, z, ids.Stone);
        }

        float Settle(BlockId under, float dropFrom = 6f)
        {
            var probe = new PlayerBody(registry);
            world.SetBlock(2, floorY + 1, 2, under);
            probe.Teleport(new Vector3(2.5f, floorY + 1 + dropFrom, 2.5f));
            for (var i = 0; i < 240; i++) probe.Step(world, dt, Vector3.Zero, false, false, false);
            world.SetBlock(2, floorY + 1, 2, BlockId.Air);
            return probe.Position.Y;
        }

        // 1. A slab is half a block, and a whole block is a whole block. The pair is the check.
        var onSlab = Settle(slab);
        var onCube = Settle(ids.Stone);

        if (MathF.Abs(onSlab - (top + 0.5f)) > 0.01f)
            faults.Add($"settled at y {onSlab:F3} on a slab, expected {top + 0.5f:F3}");
        if (MathF.Abs(onCube - (top + 1f)) > 0.01f)
            faults.Add($"settled at y {onCube:F3} on a whole block, expected {top + 1f:F3} — the control is wrong");
        if (onSlab >= onCube - 0.01f)
            faults.Add($"a slab ({onSlab:F3}) is no lower to stand on than a whole block ({onCube:F3})");

        // 2. A shut trapdoor is three units of panel lying on the floor, not a block in the way.
        var onTrapdoor = Settle(shutTrapdoor);
        if (MathF.Abs(onTrapdoor - (top + 3f / 16f)) > 0.01f)
            faults.Add($"settled at y {onTrapdoor:F3} on a shut trapdoor, expected {top + 3f / 16f:F3}");

        // 3. A campfire is half a block, and there is no standing in the middle of it. Dropped down
        // the exact centre, which is where the ring of logs leaves a hole.
        var onFire = Settle(campfire);
        if (MathF.Abs(onFire - (top + 0.5f)) > 0.01f)
            faults.Add($"settled at y {onFire:F3} in the middle of a campfire, expected {top + 0.5f:F3}");

        // 4. And a dropped stack comes to rest on top of a slab, not inside it. The same table, the
        // same fault, a third copy of it — an item read a bool keyed on the block id too.
        float ItemRestsOn(BlockId under)
        {
            world.SetBlock(2, floorY + 1, 2, under);

            var thrown = new DroppedItems(registry, catalogue, 0x5EED);
            thrown.Drop(new ItemStack(catalogue.ByName("stone").Id, 1),
                new Vector3(2.5f, floorY + 5f, 2.5f), scatter: 0f);
            for (var i = 0; i < 300; i++) thrown.Update(world, dt, null, null);

            var rest = thrown.Count > 0 ? thrown.Live[0].Position.Y : float.NaN;
            world.SetBlock(2, floorY + 1, 2, BlockId.Air);
            return rest;
        }

        var itemOnSlab = ItemRestsOn(slab);
        var itemOnCube = ItemRestsOn(ids.Stone);

        if (float.IsNaN(itemOnSlab) || float.IsNaN(itemOnCube))
            faults.Add("a dropped stack vanished before it came to rest");
        else if (itemOnSlab >= itemOnCube - 0.01f)
            faults.Add(
                $"a dropped stack rests at y {itemOnSlab:F2} on a slab and {itemOnCube:F2} on a "
                + "whole block — an item is falling to the cell rather than to the shape");

        // 5. A doorway is walked through when the door is open and not when it is shut. A wall along
        // x = 8 with one cell of it a door, both halves.
        float WalkThroughDoorway(BlockId lower, BlockId upper)
        {
            for (var y = 1; y <= 3; y++)
            for (var z = -6; z < 24; z++)
                world.SetBlock(8, floorY + y, z, z == 4 ? BlockId.Air : ids.Stone);

            world.SetBlock(8, floorY + 1, 4, lower);
            world.SetBlock(8, floorY + 2, 4, upper);

            var walker = new PlayerBody(registry);
            walker.Teleport(new Vector3(5.5f, top, 4.5f));
            for (var i = 0; i < 300; i++)
                walker.Step(world, dt, new Vector3(1f, 0f, 0f), false, false, false);

            return walker.Position.X;
        }

        var throughOpen = WalkThroughDoorway(openDoorLower, openDoorUpper);
        var throughShut = WalkThroughDoorway(shutDoorLower, shutDoorUpper);

        // ⚠ Past the door's own cell, not past the wall's near face. A shut door's panel is three
        // units thick at the far side of the cell, so a body correctly stopped by one has walked
        // most of the way into the doorway — x 8.51 is a body against the panel, not through it.
        if (throughOpen <= 9f)
            faults.Add($"an open door stopped a body at x {throughOpen:F2} — it never got out of the doorway");
        if (throughShut >= 9f)
            faults.Add($"a shut door let a body reach x {throughShut:F2}, out the far side of its own cell");

        for (var y = 1; y <= 3; y++)
        for (var z = -6; z < 24; z++)
            world.SetBlock(8, floorY + y, z, BlockId.Air);

        // 5. A one-block wall can be jumped over; a fence, drawn exactly as tall, cannot.
        //
        // ⚠ The run stops the moment the body is clear rather than running for a fixed time. Left to
        // run, the one that got over walked another forty blocks and off the end of the floor, and
        // the check reported it at y -492 — which reads exactly like "the control never got up".
        float JumpAt(BlockId barrier)
        {
            for (var z = -6; z < 24; z++)
                world.SetBlock(12, floorY + 1, z, barrier);

            var hopper = new PlayerBody(registry);
            hopper.Teleport(new Vector3(9.5f, top, 4.5f));
            for (var i = 0; i < 600 && hopper.Position.X < 13f; i++)
                hopper.Step(world, dt, new Vector3(1f, 0f, 0f), jump: true, sneak: false, sprint: false);

            var reached = hopper.Position.X;

            for (var z = -6; z < 24; z++)
                world.SetBlock(12, floorY + 1, z, BlockId.Air);

            return reached;
        }

        var overStep = JumpAt(ids.Stone);
        var overFence = JumpAt(fence);

        if (overStep <= 13f)
            faults.Add(
                $"a body jumping at a one-block wall only reached x {overStep:F2} — the control "
                + "never got over, so the fence beside it proves nothing");

        // ⚠ 12.5, not 12. A fence post is four units wide in the middle of its cell, so a body
        // correctly stopped by one still walks to x 12.08 — most of the way into the fence's cell.
        if (overFence >= 12.5f)
            faults.Add($"a body got over a fence and reached x {overFence:F2}");

        detail =
            $"a slab is stood on at {onSlab - floorY:F2} where a block is {onCube - floorY:F2}, "
            + $"a shut trapdoor at {onTrapdoor - floorY:F2}, a campfire at {onFire - floorY:F2}; "
            + $"an open doorway is walked out of to x {throughOpen:F1} and a shut one holds at "
            + $"{throughShut:F1}; a one-block wall is jumped over (x {overStep:F1}) and a fence "
            + $"drawn the same height is not (x {overFence:F2}); a dropped stack rests at "
            + $"{itemOnSlab - floorY:F2} on a slab and {itemOnCube - floorY:F2} on a whole block";

        return faults;
    }

    /// <summary>
    /// That a box takes a typed line, a caret walks it, and what it refuses it really refuses.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Every refusal is paired with the same thing being accepted somewhere it should be.</b> A
    /// field that took nothing at all would pass "it refuses a slash" and every other refusal here,
    /// and would then be a seed box nobody can type in — which is a fault that looks like a keyboard
    /// problem from the front and would be hunted for in the input layer.
    /// </remarks>
    private static List<string> TextFieldSelfTest(out string detail)
    {
        var faults = new List<string>();

        var any = new TextField(8);
        foreach (var c in "hello") any.Insert(c);

        if (any.Text != "hello") faults.Add($"typing 'hello' left '{any.Text}'");
        if (any.Caret != 5) faults.Add($"the caret is at {any.Caret} after five characters");

        // Into the middle, which is the whole reason a caret exists rather than an append.
        any.Left();
        any.Left();
        any.Insert('-');
        if (any.Text != "hel-lo") faults.Add($"typing into the middle left '{any.Text}'");

        any.Backspace();
        if (any.Text != "hello") faults.Add($"backspace left '{any.Text}'");

        any.Delete();
        if (any.Text != "helo") faults.Add($"delete left '{any.Text}'");

        // Full. The ninth character has nowhere to go and must not silently replace the eighth.
        var full = new TextField(8);
        full.Insert("123456789");
        if (full.Text != "12345678") faults.Add($"a box of 8 took '{full.Text}'");

        // Neither end may run off, and neither may throw.
        var edges = new TextField(8);
        edges.Backspace();
        edges.Delete();
        edges.Left();
        if (edges.Caret != 0 || edges.Text.Length != 0) faults.Add("an empty box moved when it was pushed");

        edges.Insert('a');
        edges.Right();
        edges.Right();
        if (edges.Caret != 1) faults.Add($"the caret ran past the end to {edges.Caret}");

        edges.Home();
        if (edges.Caret != 0) faults.Add("home did not go to the start");
        edges.End();
        if (edges.Caret != 1) faults.Add("end did not go to the end");

        // ⚠ The font is 95 glyphs and one texture layer each, so a character outside it has nothing
        // to draw. Refused at the keyboard, where somebody can see it happen.
        var drawable = new TextField(16);
        drawable.Insert("a\tb\né—c");
        if (drawable.Text != "abc")
            faults.Add($"a line the font cannot draw came through as '{drawable.Text}'");

        // And the pair for it: a full stop, a dash and a space are all drawable and all wanted.
        var punctuation = new TextField(16);
        punctuation.Insert("a b-c.d");
        if (punctuation.Text != "a b-c.d")
            faults.Add($"ordinary punctuation was refused, leaving '{punctuation.Text}'");

        // A world name is a file name. Both halves: the slash goes, the rest stays.
        var name = new TextField(32, TextAllows.FileSafe);
        name.Insert("my/world:2");
        if (name.Text != "myworld2") faults.Add($"a file-safe box took '{name.Text}'");

        var digits = new TextField(8, TextAllows.Digits);
        digits.Insert("12a3");
        if (digits.Text != "123") faults.Add($"a digits box took '{digits.Text}'");

        // Setting it from outside goes through the same gate — otherwise a name loaded from a file
        // is a way past every rule above.
        var set = new TextField(6, TextAllows.FileSafe) { Text = "a/b*cdefgh" };
        if (set.Text != "abcdef") faults.Add($"setting the text outright left '{set.Text}'");
        if (set.Caret != set.Text.Length) faults.Add("setting the text left the caret somewhere else");

        detail =
            $"typing, a caret in the middle, backspace and delete; a box of 8 holds '{full.Text}'; "
            + $"the undrawable is dropped ('{drawable.Text}') where punctuation is not "
            + $"('{punctuation.Text}'); a file-safe box makes 'my/world:2' into '{name.Text}' and a "
            + $"digits box makes '12a3' into '{digits.Text}'";

        return faults;
    }

    /// <summary>
    /// Items that put a block down and are still drawn flat, on purpose, with the reason.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>A named list rather than a flag somebody remembers to set.</b> "Flat" used to be the
    /// default, so an item was drawn flat both when that was the intention and when nobody had
    /// thought about it, and the two are indistinguishable from the outside — which is exactly how
    /// the torch and the ladder came to be reported as missing from a pass they were deliberately
    /// left out of. Anything not on this list that puts a block down has to be drawn as one.
    /// </remarks>
    private static readonly (string Item, string Why)[] FlatOnPurpose =
    [
        ("torch", "a cut-out on crossed planes: a solid of it is a solid of black"),
        ("ladder", "a cut-out on one sheet, with nothing behind it to shade"),
    ];

    /// <summary>
    /// Every item, and whether it is drawn as the block it puts down or as a flat picture.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Written because the user counted them before the game did.</b> They opened the recipe
    /// book, saw a torch and a ladder sitting still among the turning blocks, and asked for an
    /// audit. Nothing anywhere could have answered that question — the property was a bool nobody
    /// listed — so this is the list, and it fails rather than merely printing: an item that puts a
    /// block down and is drawn flat is now a decision somebody has to have written down.
    /// </remarks>
    private static List<string> IconStyleAudit(ItemRegistry items, out string detail)
    {
        var faults = new List<string>();

        int asBlock = 0, flat = 0, declared = 0, noBlock = 0;
        var quiet = new List<string>();

        for (ushort id = 1; id < items.Count; id++)
        {
            var item = items[id];
            var puts = !item.PlainBlock.IsAir;

            if (item.DrawsAsBlock)
            {
                asBlock++;

                // ⚠ Drawn as a block, but with no boxes to draw. A model that is all planes leaves
                // nothing behind once they are dropped, so this falls back to a flat picture with
                // nobody saying it did — the silent half of the same problem.
                if (item.IconModel is not { Icon.Length: > 0 })
                    faults.Add($"'{item.Name}' is meant to draw as a block and its model has no solid to draw");

                continue;
            }

            flat++;
            if (!puts) { noBlock++; continue; }

            var reason = Array.FindIndex(FlatOnPurpose, f => f.Item == item.Name);
            if (reason >= 0) declared++;
            else quiet.Add(item.Name);
        }

        foreach (var name in quiet)
            faults.Add(
                $"'{name}' puts a block down and is drawn as a flat picture, and nothing says why — "
                + "either draw it as a block or put it in FlatOnPurpose with the reason");

        // Every declared exception must still be a real item, or the list rots into a set of names
        // that excuse nothing and hide the next one that goes quiet.
        foreach (var (name, _) in FlatOnPurpose)
            if (!items.TryByName(name, out _))
                faults.Add($"'{name}' is excused from drawing as a block and is not an item any more");

        detail =
            $"{asBlock} drawn as the block they put down, {declared} flat on purpose "
            + $"({string.Join(", ", FlatOnPurpose.Select(f => f.Item))}), "
            + $"{noBlock} that put no block down at all";

        return faults;
    }

    /// <summary>
    /// That the four tool silhouettes are actually four different shapes.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Written because they were not.</b> The user opened the pockets and said the axe
    /// and the shovel were the same picture. They were: slid one column across, the two differed by
    /// a <em>single character</em> — a head-shade pixel. Four tools, three shapes, and every check
    /// in the project was perfectly happy because nothing had ever been asked to compare two
    /// drawings with each other.</para>
    /// <para>⛳ <b>Compared at every offset, not in place</b>, and that is the whole of why this
    /// catches what the eye caught. Two shapes one pixel apart are the same drawing to anybody
    /// looking at a square the size of a fingernail, and a straight cell-for-cell comparison calls
    /// them different. Sliding one over the other and keeping the <em>best</em> agreement is what a
    /// person does without noticing.</para>
    /// <para>The ink, not the shading: a silhouette is what is recognised at this size, and two
    /// tools that differ only in which pixels are lit are two tools nobody can tell apart.</para>
    /// </remarks>
    private static List<string> ToolShapeAudit(out string detail)
    {
        var faults = new List<string>();
        string[] named = ["pickaxe", "axe", "shovel", "sword"];

        var shapes = TileGen.ToolShapes;
        if (shapes.Length != named.Length)
        {
            detail = "";
            faults.Add($"{shapes.Length} tool silhouettes against {named.Length} names for them");
            return faults;
        }

        // Every row of every shape has to be the tile's own width, or a drawing that looks right
        // in the source is cropped or ragged on screen.
        for (var s = 0; s < shapes.Length; s++)
        {
            if (shapes[s].Length != TileGen.Size)
                faults.Add($"the {named[s]} is {shapes[s].Length} rows where a tile is {TileGen.Size}");

            foreach (var row in shapes[s])
                if (row.Length != TileGen.Size)
                {
                    faults.Add($"the {named[s]} has a row {row.Length} wide where a tile is {TileGen.Size}");
                    break;
                }
        }

        if (faults.Count > 0)
        {
            detail = "";
            return faults;
        }

        bool Ink(int shape, int x, int y) =>
            x >= 0 && y >= 0 && x < TileGen.Size && y < TileGen.Size && shapes[shape][y][x] != '.';

        // How many cells two shapes disagree on at their best alignment, sliding one over the other.
        int Apart(int a, int b)
        {
            var best = int.MaxValue;

            for (var dy = -3; dy <= 3; dy++)
            for (var dx = -3; dx <= 3; dx++)
            {
                var differ = 0;
                for (var y = 0; y < TileGen.Size; y++)
                for (var x = 0; x < TileGen.Size; x++)
                    if (Ink(a, x, y) != Ink(b, x + dx, y + dy)) differ++;

                best = Math.Min(best, differ);
            }

            return best;
        }

        // ⚠ Measured, not picked. The pair that shipped as one drawing came to 6 cells apart at its
        // best offset; the closest pair now is well past 30. Twenty sits clear of both.
        const int Least = 20;
        var closest = int.MaxValue;
        var closestPair = "";

        for (var a = 0; a < shapes.Length; a++)
        for (var b = a + 1; b < shapes.Length; b++)
        {
            var apart = Apart(a, b);
            if (apart >= closest) continue;

            closest = apart;
            closestPair = $"{named[a]} and {named[b]}";
        }

        if (closest < Least)
            faults.Add(
                $"the {closestPair} are the same drawing — {closest} cells apart at their best "
                + $"alignment, where {Least} is the least two tools may differ by and still be told "
                + "apart in a square the size of a fingernail");

        // And that a tool uses its tile. One drawn in a corner reads as a small tool rather than a
        // tool, and every one of these used to stop three columns short of the right-hand edge.
        for (var s = 0; s < shapes.Length; s++)
        {
            int minX = TileGen.Size, maxX = -1, minY = TileGen.Size, maxY = -1;

            for (var y = 0; y < TileGen.Size; y++)
            for (var x = 0; x < TileGen.Size; x++)
            {
                if (!Ink(s, x, y)) continue;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }

            // ⚠ The span, counted inclusively. Written as a difference this refused a tool exactly
            // twelve wide — the narrowest a real pack draws one — for being eleven.
            var across = maxX - minX + 1;
            var down = maxY - minY + 1;

            const int Fills = 12;   // 75% of the tile, which is the narrowest of the pack's four
            if (across < Fills || down < Fills)
                faults.Add(
                    $"the {named[s]} covers {across} by {down} of a {TileGen.Size} tile, "
                    + $"where a real pack's narrowest fills {Fills} — it is drawn small rather than drawn");
        }

        // ⛳ AND HOW FAR THE TONE ACTUALLY TRAVELS ACROSS ONE, which is the other half of what was
        // wrong and is not in the drawings at all — it is what the generator does with them.
        //
        // ⛔ ASKED FIRST AS A COUNT OF DISTINCT COLOURS, AND THAT COULD NOT SEE IT. The flat build
        // scored TWENTY, one MORE than the shaded one, because a dither of plus-or-minus eight over
        // three tones already produces twenty different values. Counting colours measures dithering.
        // What separates a shaded tool from a flat one is how far the light travels: measured on a
        // real 16-pixel pack, the tenth to ninetieth percentile of brightness spans 59 to 73.
        var flattest = int.MaxValue;
        var flattestAt = "";

        for (var s = 0; s < shapes.Length; s++)
        {
            var tile = TileGen.IconTool(4000 + s, s, 150, 120, 90);
            var light = new List<int>();

            for (var i = 0; i < tile.Length; i += 4)
            {
                if (tile[i + 3] < 128) continue;
                light.Add((int)(0.299f * tile[i] + 0.587f * tile[i + 1] + 0.114f * tile[i + 2]));
            }

            if (light.Count < 10) continue;

            light.Sort();
            var spread = light[(int)(light.Count * 0.9f)] - light[(int)(light.Count * 0.1f)];

            if (spread >= flattest) continue;
            flattest = spread;
            flattestAt = named[s];
        }

        // ⛔ REPORTED, NOT ASSERTED ON, and that is the honest answer to a measurement that turned
        // out to say nothing. The claim being tested was "ours are flatter than a real pack's" — and
        // measured, they are not: the build before any of this travelled 57 where the pack travels
        // 59 to 73. The highlight and shadow the drawings already carried span nearly the whole of
        // it. Any bar clear of the flat build would also be clear of the pack, so there is no bar to
        // set; a check that passes both arms is worse than no check, because it looks like one.
        //
        // It stays as a NUMBER because it is worth watching — a future change that flattens these
        // would show here — but nothing may fail on it until somebody finds a value that separates
        // two builds that genuinely differ.

        detail =
            $"{shapes.Length} silhouettes, the closest pair ({closestPair}) {closest} cells apart at "
            + $"their best alignment, each filling its tile corner to corner, the flattest "
            + $"({flattestAt}) travelling {flattest} levels of light against the pack's 59 to 73";

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
            if (!TerrainGenerator.InWorld(wy)) return false;
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

        var top = TerrainGenerator.WorldTop - 1;

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
    /// <summary>
    /// Puts a block into a bare world the way generation does — no chunk creation, no edit logged.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Through the chunk, not through <see cref="VoxelWorld.SetBlock"/>.</b> Setting up a test
    /// world with SetBlock would fill the edit log before the flow ran, and the sharpest check in
    /// this whole group is that a settling river adds <em>nothing</em> to it.
    /// </remarks>
    private static void Put(VoxelWorld world, int x, int y, int z, BlockId id)
    {
        if (!world.TryGetChunk(ChunkPos.FromWorld(x, y, z), out var chunk)) return;
        chunk.Set(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask, id);
    }

    private static BlockId At(VoxelWorld world, int x, int y, int z) => world.GetBlock(x, y, z);

    /// <summary>
    /// Does what a player does: walks to the sea in a real streamed world, breaks a block beside it,
    /// and counts the ticks before the water arrives.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Written because the flow was demonstrably correct and did nothing in the game.</b>
    /// Every other fluid check drives <see cref="FluidEngine"/> directly, in a hand-built box, with a
    /// queue holding nothing but the cells the check put there. None of them touch the wiring — the
    /// streamer, the tick, the budget, or what else is in the queue by the time a player's edit
    /// arrives — and that is where the fault was.</para>
    /// <para>⛳ <b>The number that matters is TICKS, not "did it fill eventually".</b> A settle with
    /// no budget fills it every time; what the player sees is whether it fills within a moment of the
    /// block coming out, and a queue with ten thousand cells of ocean already in it will get there in
    /// its own time.</para>
    /// </remarks>
    private static List<string> ShoreFaults(
        WorldSeed seed, BlockRegistry registry, StarterBlocks.Ids ids, float oceanCoverage,
        out string detail)
    {
        const int radius = 4;
        const int budget = 256;          // the same budget the game ticks with
        const int patience = 15;         // three seconds of ticks at five a second

        var faults = new List<string>();
        var moved = new List<(int X, int Y, int Z)>();

        using var streamer = new WorldStreamer(
            registry, new TerrainGenerator(seed, ids, oceanCoverage), radius)
        {
            Fluids = new FluidEngine(registry),
        };

        // Find a shore: somewhere with sea in it, and load the world round there the way a walk does.
        var generator = new TerrainGenerator(seed, ids, oceanCoverage);
        var shore = FindShore(generator);
        if (shore is not { } spot)
        {
            // ⛔ NOT A PASS. A check that cannot find its subject and reports green is the exact
            // shape of thing this project keeps being bitten by — it looks like evidence and is the
            // absence of it. Every seed the gate runs has a quarter of its surface under water.
            detail = "no shoreline within 1,600 blocks of the origin on this seed";
            faults.Add(detail);
            return faults;
        }

        streamer.Update(new Vector3(spot.X + 0.5f, TerrainGenerator.SeaLevel + 4f, spot.Z + 0.5f));

        var watch = Stopwatch.StartNew();
        var quiet = 0;
        while (watch.ElapsedMilliseconds < 30_000)
        {
            streamer.PromoteReadyChunks();
            while (streamer.TryDequeueMesh(out _)) { }

            if (streamer.PendingGenerate > 0 || streamer.PendingLight > 0 || streamer.PendingMesh > 0)
            {
                quiet = 0;
                Thread.Sleep(2);
                continue;
            }

            if (++quiet >= 25) break;
            Thread.Sleep(2);
        }

        // ⛳ THE MEASUREMENT THE WHOLE CHECK IS FOR. Generation places fluid at rest, so a world that
        // has only been walked into should leave the flow with nothing to do. A queue that is full
        // before the player has touched anything is a queue their edit has to wait behind.
        var backlog = streamer.Fluids!.Pending;

        // ⛳ AND WHAT THAT BACKLOG ACTUALLY DOES, which is the number that means something. Cells
        // queued is a proxy — a cell can be queued and turn out to have nowhere to go. Cells CHANGED
        // is the claim: generation places the sea and the molten floor at rest, so walking into a
        // world should move almost nothing. A big number here means the generator is producing a
        // world that immediately rearranges itself, which is a different and much worse fault.
        streamer.Fluids.ResetCounters();
        streamer.Fluids.Settle(streamer.World, moved, limit: 2_000_000);
        var settledBy = streamer.Fluids.Changed;

        // The cell to break: solid, at the waterline, with sea beside it.
        var target = FindWetWall(streamer, ids, spot);
        if (target is not { } cell)
        {
            // ⛔ NOT A PASS EITHER, and this one caught itself: two of the four gate seeds reported
            // it and went green. The shore is found off the height field, which says where the water
            // LINE is and not where a wall stands beside it, so the search has to reach as far as
            // the finder's own tolerance did.
            detail = $"no solid block beside the sea within 24 of ({spot.X},{spot.Z}) after loading";
            faults.Add(detail);
            return faults;
        }

        // The control: nothing is there yet, so "it filled" cannot be measuring water already in it.
        if (!streamer.World.GetBlock(cell.X, cell.Y, cell.Z).Value.Equals(cell.Was))
            faults.Add("the cell to break changed before it was broken");

        streamer.EditBlock(cell.X, cell.Y, cell.Z, BlockId.Air);

        var table = new FluidTable(registry);
        var ticks = 0;
        while (ticks < patience)
        {
            ticks++;
            streamer.StepFluid(budget, moved);

            if (table.KindOf(streamer.World.GetBlock(cell.X, cell.Y, cell.Z).Value) == FluidKind.Water)
                break;
        }

        var filled = table.KindOf(streamer.World.GetBlock(cell.X, cell.Y, cell.Z).Value)
                   == FluidKind.Water;

        if (!filled)
            faults.Add($"three seconds after breaking a block beside the sea at "
                     + $"({cell.X},{cell.Y},{cell.Z}) it is still empty; {backlog:N0} cells were "
                     + $"already queued when the world finished loading");

        // ⛔ AND WHAT LOADING THE WORLD MOVED, which is a separate claim from whether this cell
        // filled. Generation places the sea and the molten floor AT REST — that is the whole reason
        // the deep is affordable — so walking into a world should rearrange almost nothing. A large
        // number here means the generator is producing a world that immediately reshapes itself.
        if (settledBy > 4000)
            faults.Add($"walking into a world moved {settledBy:N0} cells, so it is not generated at rest");

        detail = $"filled in {ticks} tick{(ticks == 1 ? "" : "s")} of the 5 a second; loading the "
               + $"world queued {backlog:N0} cells and moved {settledBy:N0} of them";

        return faults;

        /// <summary>Dry land within a few blocks of water, found off the height field alone.</summary>
        /// <remarks>
        /// ⚠ Spiralled outward rather than scanned in raster order, so it lands on the nearest shore
        /// to the origin — which keeps the streamed region small and the check quick.
        /// </remarks>
        static (int X, int Z)? FindShore(TerrainGenerator gen)
        {
            for (var r = 0; r <= 1600; r += 4)
            for (var dz = -r; dz <= r; dz += 4)
            for (var dx = -r; dx <= r; dx += 4)
            {
                if (r > 0 && Math.Abs(dx) != r && Math.Abs(dz) != r) continue;   // the ring only

                // Land, with the sea bed within a couple of dozen blocks of it in some direction.
                if (gen.SurfaceHeight(dx, dz) <= TerrainGenerator.SeaLevel) continue;

                for (var reach = 4; reach <= 24; reach += 4)
                for (var side = 0; side < 4; side++)
                {
                    var ox = side == 0 ? reach : side == 1 ? -reach : 0;
                    var oz = side == 2 ? reach : side == 3 ? -reach : 0;

                    if (gen.SurfaceHeight(dx + ox, dz + oz) < TerrainGenerator.SeaLevel - 2)
                        return (dx, dz);
                }
            }

            return null;
        }

        static (int X, int Y, int Z, ushort Was)? FindWetWall(
            WorldStreamer streamer, StarterBlocks.Ids ids, (int X, int Z) near)
        {
            var world = streamer.World;

            for (var dz = -24; dz <= 24; dz++)
            for (var dx = -24; dx <= 24; dx++)
            for (var y = TerrainGenerator.SeaLevel; y >= TerrainGenerator.SeaLevel - 3; y--)
            {
                int x = near.X + dx, z = near.Z + dz;

                var here = world.GetBlock(x, y, z);
                if (here.IsAir || here == ids.Water) continue;

                // Sea on at least one side of it, at the same height.
                if (world.GetBlock(x + 1, y, z) != ids.Water
                    && world.GetBlock(x - 1, y, z) != ids.Water
                    && world.GetBlock(x, y, z + 1) != ids.Water
                    && world.GetBlock(x, y, z - 1) != ids.Water) continue;

                return (x, y, z, here.Value);
            }

            return null;
        }
    }

    /// <summary>
    /// Meshes three chunks — stone, stone with water in it, and stone with lava in it — and asks
    /// which pass each one's geometry landed in.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Three worlds because one proves nothing.</b> "The water chunk has translucent
    /// geometry" is satisfied by a mesher that puts <em>everything</em> in the second pass, which
    /// would draw the whole world see-through; the stone chunk is what refuses that. And the lava
    /// chunk is the other control: lava is a fluid too and belongs in the FIRST pass, so a rule
    /// written on "is it a fluid" rather than "is it water" fails here and nowhere else.</para>
    /// <para>Whether a lake <em>looks</em> right is the user's eyes and always was. This is the half
    /// that can be settled without them: that the geometry exists, that it is separated, and that
    /// the split is where it was meant to be.</para>
    /// </remarks>
    private static List<string> TranslucentPassFaults(
        BlockRegistry registry, StarterBlocks.Ids ids, out string detail)
    {
        var faults = new List<string>();

        var dry = MeshOne(BlockId.Air);
        var wet = MeshOne(ids.Water);
        var hot = MeshOne(ids.Lava);

        if (dry is null || wet is null || hot is null)
        {
            detail = "one of the three test chunks meshed to nothing";
            faults.Add(detail);
            return faults;
        }

        if (dry.HasTranslucent)
            faults.Add($"a chunk of plain stone put {dry.IndexCount - dry.OpaqueIndexCount} indices in the water pass");

        if (!wet.HasTranslucent)
            faults.Add("a chunk with a pool in it drew no water at all in the second pass");

        if (wet.OpaqueIndexCount == 0)
            faults.Add("a chunk with a pool in it put ALL of its geometry in the water pass");

        if (hot.HasTranslucent)
            faults.Add("lava was meshed into the water pass — it is opaque and emissive and belongs in the first");

        if (hot.OpaqueIndexCount != hot.IndexCount)
            faults.Add("the lava chunk's two halves do not add up");

        detail = $"stone {dry.OpaqueIndexCount}/0 indices, a pool {wet.OpaqueIndexCount}/"
               + $"{wet.IndexCount - wet.OpaqueIndexCount}, lava {hot.OpaqueIndexCount}/0";

        return faults;

        ChunkMeshData? MeshOne(BlockId fill)
        {
            var world = new VoxelWorld(registry);
            world.GetOrCreateChunk(new ChunkPos(0, 0, 0));

            for (var z = 0; z < Chunk.Size; z++)
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < 8; y++)
                Put(world, x, y, z, ids.Stone);

            // A square hollow in the top of it, filled or left as air.
            if (!fill.IsAir)
                for (var z = 8; z < 24; z++)
                for (var x = 8; x < 24; x++)
                    Put(world, x, 7, z, fill);

            new LightEngine(registry).LightAll(world);
            return new ChunkMesher(registry).Build(world, new ChunkPos(0, 0, 0));
        }
    }

    /// <summary>
    /// The four cells that matter, put through both spawn questions.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>A field at midnight is the whole check.</b> It is the one cell where the two questions
    /// disagree, and a build with only the first one — which is what shipped until now — answers it
    /// "dark, so anything may live here" and puts cave animals in meadows. Every other row is a
    /// control: without them "the two disagree somewhere" is satisfied by two predicates that always
    /// disagree, which would be just as wrong in the other direction.
    /// </remarks>
    private static List<string> SpawnBandFaults(out string detail)
    {
        var faults = new List<string>();

        var open = LightValue.Pack(15, 0, 0, 0);        // a field, sky wide open
        var cave = LightValue.Pack(0, 0, 0, 0);         // rock all round
        var lit = LightValue.Pack(0, 14, 10, 4);        // a cave with a torch in it

        void Want(string what, bool got, bool expected)
        {
            if (got != expected) faults.Add($"{what} came back {got}, expected {expected}");
        }

        Want("a field at noon, dark", SpawnRules.Dark(open, 1f), false);
        Want("a field at noon, buried", SpawnRules.Buried(open), false);

        // ⛳ THE DISCRIMINATOR. Dark, and not a cave — one question cannot tell those apart.
        Want("a field at midnight, dark", SpawnRules.Dark(open, 0f), true);
        Want("a field at midnight, buried", SpawnRules.Buried(open), false);

        Want("a cave at noon, dark", SpawnRules.Dark(cave, 1f), true);
        Want("a cave at noon, buried", SpawnRules.Buried(cave), true);

        Want("a lit cave, dark", SpawnRules.Dark(lit, 1f), false);
        Want("a lit cave, buried", SpawnRules.Buried(lit), false);

        // ── How hard the dark pushes, which was reported as "really aggressive at night" ─────────
        //
        // ⛔ The spawner was a set point with infinite gain: every second it refilled the entire
        // deficit, so a night arrived complete instead of building and a kill was answered before
        // the body faded. These are the three numbers that turn it into a rate.
        if (SpawnRules.Pressure(0, SpawnRules.HostileCap) < 0.99f)
            faults.Add("an empty night is not certain to place anything, so the dark never starts");

        if (SpawnRules.Pressure(SpawnRules.HostileCap, SpawnRules.HostileCap) > 0f)
            faults.Add("a full night still places more, so the cap is not a cap");

        // ⛳ THE CONTROL, and it is the one that matters: pressure has to FALL. A build that always
        // spawns reads 1 at every population — which is exactly what shipped — and satisfies both
        // of the bounds above if they are written as "between 0 and 1".
        var falling = true;
        for (var n = 1; n <= SpawnRules.HostileCap; n++)
            falling &= SpawnRules.Pressure(n, SpawnRules.HostileCap)
                     < SpawnRules.Pressure(n - 1, SpawnRules.HostileCap);

        if (!falling) faults.Add("spawn pressure does not fall as the night fills");

        // One attempt may not fill the night. This is the whole of "scale it back" as a number.
        if (SpawnRules.HostileBatch >= SpawnRules.HostileCap)
            faults.Add($"one attempt may place {SpawnRules.HostileBatch} of a cap of "
                     + $"{SpawnRules.HostileCap}, which is the deficit-refill it replaced");

        // And they arrive at an irregular remove, not on a beat and not on your doorstep.
        var soonest = SpawnRules.NextAttempt(0.0);
        var latest = SpawnRules.NextAttempt(1.0);

        if (soonest < 2f) faults.Add($"attempts come as often as every {soonest:F1}s");
        if (latest <= soonest + 2f)
            faults.Add($"attempts land between {soonest:F1}s and {latest:F1}s, which is a metronome");

        if (SpawnRules.HostileMinRadius < 20f)
            faults.Add($"things appear {SpawnRules.HostileMinRadius:F0} blocks away, close enough to be an ambush");

        // And the fault this was written for: a cave animal filed as a meadow animal.
        foreach (var kind in CreatureSet.All)
        {
            if (kind.Name != "bat") continue;
            if (kind.Family != CreatureFamily.Cave)
                faults.Add($"the bat is a {kind.Family}, so it spawns in fields with the cows");
        }

        var families = new HashSet<CreatureFamily>();
        foreach (var kind in CreatureSet.All) families.Add(kind.Family);

        if (families.Count < 3)
            faults.Add($"only {families.Count} families exist, so a band check has nothing to separate");

        detail = $"a field at midnight is dark and not buried, a cave at noon is both, a torch "
               + $"clears either; {families.Count} families; the dark tries every "
               + $"{SpawnRules.NextAttempt(0.0):F0}-{SpawnRules.NextAttempt(1.0):F0}s, places at "
               + $"most {SpawnRules.HostileBatch} of {SpawnRules.HostileCap} at "
               + $"{SpawnRules.HostileMinRadius:F0} blocks, and stops pushing as it fills";

        return faults;
    }

    /// <summary>
    /// Builds a pack, a folder pack and a broken file, and puts them through the shelf.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>The half that matters is what happens to the BAD one.</b> The saves list cost this
    /// project a session by silently dropping a file it could not open, so "no worlds" and "a world I
    /// cannot open" were four identical words. Packs are far likelier to arrive broken — people
    /// download them — so a pack that will not open has to be refused at the moment of the mistake,
    /// with the reason, and never quietly accepted onto the shelf to fail one relaunch later.</para>
    /// <para>⚠ Against a real shelf in a temporary folder rather than a mocked one, because the
    /// thing being tested is largely the file system: copying, replacing, and finding by name.</para>
    /// </remarks>
    private static List<string> PackShelfFaults(out string detail)
    {
        var faults = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "driftwood-shelf-" + Guid.NewGuid().ToString("N"));
        detail = "";

        try
        {
            // A Bedrock-shaped folder pack, which is the one shape that needs no zip to be real.
            var good = Path.Combine(root, "Testpack");
            Directory.CreateDirectory(Path.Combine(good, "textures", "blocks"));
            File.WriteAllText(Path.Combine(good, "manifest.json"), "{\"format_version\":2}");
            File.WriteAllBytes(Path.Combine(good, "textures", "blocks", "stone.png"), [1, 2, 3, 4]);

            // And something that is not a pack at all, under a name the shelf accepts.
            var bad = Path.Combine(root, "Broken.mcpack");
            Directory.CreateDirectory(root);
            File.WriteAllText(bad, "this is not a zip");

            // A wrong extension, which must be refused by name rather than opened and puzzled over.
            var wrong = Path.Combine(root, "notes.txt");
            File.WriteAllText(wrong, "hello");

            var shelf = PackLibrary.Folder;
            var installed = PackLibrary.Install(good, out var why);

            if (installed is not { } entry)
            {
                faults.Add($"a sound pack was refused: {why}");
                return faults;
            }

            if (!entry.Readable) faults.Add($"a sound pack landed unreadable: {entry.Kind}");
            if (!entry.Kind.Contains("Bedrock")) faults.Add($"a Bedrock pack was called '{entry.Kind}'");

            // ⛳ Found again BY NAME, which is what the setting stores — an index would mean something
            // different the moment anything else is added, and adding is what the screen is for.
            if (PackLibrary.PathOf(entry.Name) is null)
                faults.Add($"'{entry.Name}' went on the shelf and cannot be found by name");

            var listed = PackLibrary.List();
            if (!listed.Any(p => p.Name == entry.Name))
                faults.Add($"'{entry.Name}' is on the shelf and not in the list");

            // ⛔ THE CONTROL. A shelf that takes anything passes every row above.
            if (PackLibrary.Install(bad, out var badWhy) is not null)
                faults.Add("a file that is not a pack was accepted onto the shelf");
            else if (badWhy.Length == 0)
                faults.Add("a broken pack was refused with no reason given");

            if (PackLibrary.Install(wrong, out var wrongWhy) is not null)
                faults.Add("a .txt was accepted as a texture pack");
            else if (!wrongWhy.Contains(".txt"))
                faults.Add($"a .txt was refused without saying what was wrong with it: '{wrongWhy}'");

            // Re-importing replaces rather than piling up, which is what an updated pack needs.
            PackLibrary.Install(good, out _);
            if (PackLibrary.List().Count(p => p.Name == entry.Name) != 1)
                faults.Add("importing the same pack twice left two of it on the shelf");

            if (!PackLibrary.Remove(entry.Name)) faults.Add("a pack could not be taken off the shelf");
            if (PackLibrary.PathOf(entry.Name) is not null)
                faults.Add("a pack was removed and is still found by name");

            detail = $"a {entry.Kind} pack copied to the shelf, found by name, replaced on re-import "
                   + $"and removed; a broken one and a .txt both refused with a reason ({shelf})";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            faults.Add($"could not exercise the shelf: {error.Message}");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }

        return faults;
    }

    /// <summary>
    /// Reads every tool tile back and asks whether its head is the colour of its own material.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>The head only, and that is the whole difficulty.</b> Every tool is mostly haft —
    /// the same timber at every tier — so a mean over the tile is a mean over a brown stick and says
    /// a stormglass sword is brown. The head is picked out by discarding anything close to the
    /// handle's own colour, which leaves what the tier actually paints.</para>
    /// <para>⛳ <b>Two claims, and the second is the one a player sees.</b> That each head is near its
    /// declared material, and that <em>no two tiers land on the same colour</em> — a ladder whose
    /// rungs are indistinguishable in a fist is a ladder with no feedback on it, and every tool
    /// sitting inside its own tolerance does not prevent that.</para>
    /// </remarks>
    private static List<string> ToolTierColourFaults(out string detail)
    {
        var faults = new List<string>();
        var built = BlockTextureSet.Build(packPath: null);

        // The timber every haft is drawn in, so it can be told from the head.
        var (hr, hg, hb) = (128, 94, 56);

        var seen = new List<(string Name, int R, int G, int B)>();

        for (var tier = 0; tier < StarterItems.Tiers.Length; tier++)
        {
            var name = StarterItems.Tiers[tier].Name;

            long r = 0, g = 0, b = 0, taken = 0;

            for (var head = 0; head < StarterBlocks.ToolShapeCount; head++)
            {
                var layer = StarterBlocks.LayerFirstTool + tier * StarterBlocks.ToolShapeCount + head;
                if (layer >= built.Tiles.Length) continue;

                var tile = built.Tiles[layer];
                for (var i = 0; i < tile.Length; i += 4)
                {
                    if (tile[i + 3] < 128) continue;

                    int pr = tile[i], pg = tile[i + 1], pb = tile[i + 2];

                    // Not the haft, and not the dark line drawn round everything.
                    if (Math.Abs(pr - hr) < 46 && Math.Abs(pg - hg) < 46 && Math.Abs(pb - hb) < 46) continue;
                    if (pr + pg + pb < 150) continue;

                    r += pr; g += pg; b += pb; taken++;
                }
            }

            if (taken == 0)
            {
                faults.Add($"the {name} tools have no head left once the haft is taken out");
                continue;
            }

            seen.Add((name, (int)(r / taken), (int)(g / taken), (int)(b / taken)));
        }

        // ⛳ THE CLAIM THAT MATTERS. Six rungs that a player has to tell apart at arm's length in a
        // fist, so the test is that they are far apart in colour — not that each is near a number.
        for (var i = 0; i < seen.Count; i++)
        for (var j = i + 1; j < seen.Count; j++)
        {
            var a = seen[i];
            var c = seen[j];
            var apart = Math.Abs(a.R - c.R) + Math.Abs(a.G - c.G) + Math.Abs(a.B - c.B);

            if (apart < 45)
                faults.Add($"{a.Name} and {c.Name} tools are {apart} apart in colour, which is the same tool twice");
        }

        // ⛔ AND HOW MUCH OF THE TOOL IT IS, which is the half a colour check cannot see. Every tier
        // can be perfectly and distinctly coloured and still read as "a brown stick" in a fist, if
        // what carries the colour is a chip at one end. What a player recognises at arm's length is
        // the SILHOUETTE, and the share of it the material owns is the whole of that.
        var share = new List<(string Name, int Percent)>();

        for (var head = 0; head < StarterBlocks.ToolShapeCount; head++)
        {
            var layer = StarterBlocks.LayerFirstTool + head;   // the wood tier; shapes are shared
            if (layer >= built.Tiles.Length) continue;

            var tile = built.Tiles[layer];
            int ink = 0, haft = 0;

            for (var i = 0; i < tile.Length; i += 4)
            {
                if (tile[i + 3] < 128) continue;
                ink++;

                if (Math.Abs(tile[i] - hr) < 46 && Math.Abs(tile[i + 1] - hg) < 46
                    && Math.Abs(tile[i + 2] - hb) < 46) haft++;
            }

            if (ink == 0) continue;
            share.Add((StarterItems.Heads[head].Name, 100 - haft * 100 / ink));
        }

        foreach (var (name, percent) in share)
        {
            // A third is the line. Below it the material is a detail on a stick rather than what the
            // thing is made of, and no amount of getting the colour right rescues that.
            if (percent < 33)
                faults.Add($"only {percent}% of a {name} is its own material — the rest is haft");
        }

        // ⛔ AND WHAT SURVIVES BEING HELD, which is where this actually went wrong. The tiles were
        // never the problem; the LIGHT was. Block light is coloured on purpose and a torch is
        // (14,10,5) — so a held tool multiplied by it raw comes out the colour of the torch, and
        // underground a torch is the only light there is.
        var torch = new Vector3(14f / 15f, 10f / 15f, 5f / 15f);
        var lit = HeldGrip.HandLight(torch, 0f);

        // The control is the same two materials under the same torch with NO desaturation, which is
        // exactly what shipped: whatever number this check gates on has to separate the two.
        var apartLit = 0f;
        var apartRaw = 0f;

        for (var i = 0; i < seen.Count; i++)
        for (var j = i + 1; j < seen.Count; j++)
        {
            var a = new Vector3(seen[i].R, seen[i].G, seen[i].B) / 255f;
            var c = new Vector3(seen[j].R, seen[j].G, seen[j].B) / 255f;

            apartLit = MathF.Max(apartLit, Spread(a * lit, c * lit));
            apartRaw = MathF.Max(apartRaw, Spread(a * torch, c * torch));
        }

        // Under a torch the ladder has to stay legible. The raw multiply is the thing it must beat.
        if (apartLit <= apartRaw)
            faults.Add($"held under a torch the tiers are {apartLit:F3} apart, against {apartRaw:F3} raw — "
                     + "the hand is not preserving the material");

        detail = string.Join(", ", seen.Select(s => $"{s.Name} {s.R},{s.G},{s.B}"))
               + "; material covers " + string.Join(", ", share.Select(s => $"{s.Name} {s.Percent}%"))
               + $"; under a torch they stay {apartLit:F2} apart against {apartRaw:F2} raw";

        return faults;

        static float Spread(Vector3 a, Vector3 b) =>
            MathF.Abs(a.X - b.X) + MathF.Abs(a.Y - b.Y) + MathF.Abs(a.Z - b.Z);
    }

    /// <summary>
    /// Lights a fire in a bare world and asks what it puts in the air.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Every claim here needs its unlit twin, because a particle system that emits constantly
    /// satisfies all of them.</b> "The campfire made flames" is true of a build that makes flames
    /// everywhere; "the fire is a fire" is true of one that makes a torch and a campfire the same
    /// size. What each row actually asserts is a <em>difference</em> — lit against unlit, campfire
    /// against torch, flame against smoke — and a difference cannot be faked by emitting more.
    /// </remarks>
    private static List<string> FireFaults(
        BlockRegistry registry, StarterBlocks.Ids ids, out string detail)
    {
        var faults = new List<string>();
        var fires = new Fires(registry);

        // ── The table says what burns, and what does not ─────────────────────────────────────────
        var lit = registry.ByName("campfire_x_lit");
        var cold = registry.ByName("campfire_x");
        var torch = registry.ByName("torch");
        var furnace = registry.ByName("furnace_east_lit");
        var lamp = registry.ByName("stormglass_lamp");

        if (lit.FlameScale <= 0f) faults.Add("a lit campfire has no flame");
        if (cold.FlameScale > 0f || cold.SmokeScale > 0f) faults.Add("an unlit campfire burns");
        if (torch.FlameScale <= 0f) faults.Add("a torch has no flame");

        // ⛳ THE SIZE IS THE POINT. One emitter serves everything that burns, so the only thing
        // saying a campfire is a bonfire and a torch is a wick is this pair of numbers.
        if (torch.FlameScale >= lit.FlameScale)
            faults.Add($"a torch burns {torch.FlameScale:F2} against a campfire's {lit.FlameScale:F2}");

        // A furnace shows smoke and no fire; a lamp is cold light and shows neither.
        if (furnace.SmokeScale <= 0f) faults.Add("a lit furnace gives off no smoke");
        if (furnace.FlameScale > 0f) faults.Add("a furnace shows open flame, which a closed box does not");
        if (lamp.Smoulders) faults.Add("the stormglass lamp burns, and it is a cold light");

        // ── The sweep finds what is there, and only what is there ────────────────────────────────
        var world = new VoxelWorld(registry);
        world.GetOrCreateChunk(new ChunkPos(0, 0, 0));

        for (var z = 0; z < 8; z++)
        for (var x = 0; x < 8; x++)
            Put(world, x, 4, z, ids.Stone);

        var eye = new Vector3(4.5f, 6f, 4.5f);

        // The control first: nothing burning, nothing found. Without it "it found the campfire" is
        // satisfied by a sweep that returns everything it walks past.
        fires.Sweep(world, eye);
        if (fires.Count != 0) faults.Add($"a world with nothing alight reported {fires.Count} fires");

        Put(world, 4, 5, 4, lit.Id);
        Put(world, 6, 5, 6, torch.Id);
        fires.Sweep(world, eye);

        if (fires.Count != 2)
            faults.Add($"a campfire and a torch swept as {fires.Count} fires, not 2");

        // ── What it emits, and at a RATE rather than a count ─────────────────────────────────────
        //
        // ⛔ THE SHARPEST ONE. A per-frame count makes a fire whose size is the machine's: the same
        // campfire is four times bigger on a fast computer. Half a second of wall clock has to place
        // the same number of particles whether it arrives as 15 frames or as 100.
        var slow = new ParticleSystem(registry);
        var fast = new ParticleSystem(registry);

        var slowFires = new Fires(registry);
        var fastFires = new Fires(registry);
        slowFires.Sweep(world, eye);
        fastFires.Sweep(world, eye);

        for (var i = 0; i < 15; i++)
            slowFires.Emit(slow, StarterBlocks.LayerFlame, StarterBlocks.LayerSmoke, 1f / 30f);

        for (var i = 0; i < 100; i++)
            fastFires.Emit(fast, StarterBlocks.LayerFlame, StarterBlocks.LayerSmoke, 1f / 200f);

        var slowCount = slow.Count;
        var fastCount = fast.Count;

        if (slowCount == 0) faults.Add("half a second of a lit campfire put nothing in the air");

        // Within a tenth: the leftover fraction of a particle is carried between frames, so the two
        // can differ by a couple at the ends, and by nothing like a factor.
        var drift = Math.Abs(slowCount - fastCount) / (double)Math.Max(slowCount, 1);
        if (drift > 0.1)
            faults.Add($"the same half second placed {slowCount} at 30fps and {fastCount} at 200fps");

        // ── A flame rises and narrows; smoke rises, spreads, and goes ────────────────────────────
        var pool = new ParticleSystem(registry);
        pool.Flame(new Vector3(4.5f, 5.5f, 4.5f), 1f, StarterBlocks.LayerFlame, 40);
        pool.Smoke(new Vector3(4.5f, 6.5f, 4.5f), 1f, StarterBlocks.LayerSmoke, 40);

        var flameUp = 0;
        var smokeSize = 0f;
        var smokeAt = 0;

        foreach (var p in pool.Live)
        {
            if (p.Look == ParticleLook.Flame && p.Velocity.Y > 0f) flameUp++;
            if (p.Look != ParticleLook.Smoke) continue;

            smokeSize += p.Size;
            smokeAt++;
        }

        if (flameUp < 40) faults.Add($"{40 - flameUp} of 40 flames were not rising");

        var before = smokeAt == 0 ? 0f : smokeSize / smokeAt;

        // Half a second on. ⚠ Stepped against the same bare world, so nothing is colliding: fire and
        // smoke pass through everything, which is what stops a flame born inside a campfire's own
        // collision box from being pinned there for its whole life.
        for (var i = 0; i < 30; i++) pool.Update(world, 1f / 60f);

        float after = 0f, rose = 0f;
        var stillThere = 0;

        foreach (var p in pool.Live)
        {
            if (p.Look != ParticleLook.Smoke) continue;
            after += p.Size;
            rose += p.Position.Y;
            stillThere++;
        }

        if (stillThere == 0)
        {
            faults.Add("every wisp of smoke was gone within half a second");
        }
        else
        {
            after /= stillThere;
            rose /= stillThere;

            if (after <= before) faults.Add($"smoke went from {before:F3} to {after:F3} — it does not spread");
            if (rose <= 6.5f) faults.Add($"smoke was at y {rose:F2} after half a second, so it is not rising");
        }

        // And it is gone by its own life rather than hanging about.
        for (var i = 0; i < 60 * 5; i++) pool.Update(world, 1f / 60f);
        if (pool.Count != 0) faults.Add($"{pool.Count} particles outlived five seconds of a three-second life");

        detail = $"a campfire burns at {lit.FlameScale:F2} against a torch's {torch.FlameScale:F2}, "
               + $"a furnace smokes and shows no flame, a lamp does neither; half a second placed "
               + $"{slowCount} at 30fps and {fastCount} at 200; smoke spread {before:F3} to {after:F3} "
               + $"and cleared";

        return faults;
    }

    /// <summary>How big a seam of each ore actually is, counted as connected runs of it.</summary>
    /// <param name="Count">Seams found.</param>
    /// <param name="Mean">Blocks in the average one.</param>
    /// <param name="Largest">Blocks in the biggest one.</param>
    private readonly record struct VeinSize(int Count, double Mean, int Largest);

    /// <summary>
    /// Walks every ore block in the generated volume and groups it into connected seams.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>The number a rate cannot give you, and the one a player actually feels.</b> "One
    /// block every two hundred and fifty" and "a vein of eight every two thousand" are the same
    /// percentage of the world and are nothing alike to play — the first is a trickle nobody notices
    /// digging past, the second is the reason the tunnel was dug. Every rate band in this file passes
    /// either way.</para>
    /// <para>Six-connected, not twenty-six: two ore blocks touching only at a corner are two finds a
    /// player walks between, not one seam. Iterative rather than recursive — a large vein in a
    /// hundred-million-block census would put a recursion through the stack.</para>
    /// </remarks>
    private static Dictionary<ushort, VeinSize> VeinSizes(
        Chunk[] chunks, BlockRegistry registry, StarterBlocks.Ids ids)
    {
        var wanted = new HashSet<ushort>();
        foreach (var band in TerrainGenerator.OreBands(ids)) wanted.Add(band.Ore.Value);

        // The whole volume as one map, so a seam crossing a chunk seam is one seam.
        var cells = new Dictionary<(int X, int Y, int Z), ushort>();
        foreach (var chunk in chunks)
        {
            var (ox, oy, oz) = chunk.Position.Origin;
            var raw = chunk.Raw;

            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            for (var x = 0; x < Chunk.Size; x++)
            {
                var id = raw[Chunk.Index(x, y, z)];
                if (wanted.Contains(id)) cells[(ox + x, oy + y, oz + z)] = id;
            }
        }

        var totals = new Dictionary<ushort, (int Count, long Blocks, int Largest)>();
        var seen = new HashSet<(int X, int Y, int Z)>();
        var stack = new Stack<(int X, int Y, int Z)>();

        foreach (var (start, id) in cells)
        {
            if (!seen.Add(start)) continue;

            var size = 0;
            stack.Push(start);

            while (stack.Count > 0)
            {
                var (cx, cy, cz) = stack.Pop();
                size++;

                for (var face = 0; face < Faces.Count; face++)
                {
                    var (dx, dy, dz) = Faces.Normals[face];
                    var next = (cx + dx, cy + dy, cz + dz);

                    if (!cells.TryGetValue(next, out var there) || there != id) continue;
                    if (!seen.Add(next)) continue;

                    stack.Push(next);
                }
            }

            totals.TryGetValue(id, out var run);
            totals[id] = (run.Count + 1, run.Blocks + size, Math.Max(run.Largest, size));
        }

        var sizes = new Dictionary<ushort, VeinSize>();
        foreach (var (id, run) in totals)
            sizes[id] = new VeinSize(run.Count, run.Blocks / (double)run.Count, run.Largest);

        return sizes;
    }

    /// <summary>A bare world of empty chunks, with a stone floor laid across it.</summary>
    private static VoxelWorld FluidBox(
        BlockRegistry registry, StarterBlocks.Ids ids, int floorY, int chunksLow, int chunksHigh)
    {
        var world = new VoxelWorld(registry);

        for (var cy = chunksLow; cy <= chunksHigh; cy++)
        for (var cz = -1; cz <= 1; cz++)
        for (var cx = -1; cx <= 1; cx++)
            world.GetOrCreateChunk(new ChunkPos(cx, cy, cz));

        for (var z = -20; z <= 20; z++)
        for (var x = -20; x <= 20; x++)
            Put(world, x, floorY, z, ids.Stone);

        return world;
    }

    /// <summary>
    /// Everything the flow claims, each with the control that stops it passing on a broken build.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Every one of these has a negative half, because this project has now shipped four
    /// checks that passed a broken build.</b> "The fall reached the floor" passes on a world that was
    /// already full of lava unless something asserts it was empty first; "it drained" passes on a
    /// flow that never ran; "it settled" passes on a queue that was never filled.</para>
    /// <para>⛳ It is all arithmetic, which is why it runs here rather than needing eyes. The only
    /// part of a fluid that needs looking at is whether it looks like water.</para>
    /// </remarks>
    private static List<string> FluidFaults(
        BlockRegistry registry, StarterBlocks.Ids ids, out string detail)
    {
        var faults = new List<string>();
        var changed = new List<(int X, int Y, int Z)>();
        var table = new FluidTable(registry);

        // ── A fall reaches the floor, and spreads when it lands ──────────────────────────────────
        var fell = 0;
        var reach = 0;
        {
            var world = FluidBox(registry, ids, 0, -1, 1);
            var engine = new FluidEngine(table);

            // The control: nothing is there before the flow runs. Without this, "the column is full
            // of lava" is satisfied by a world that started that way.
            for (var y = 1; y <= 20; y++)
                if (!At(world, 0, y, 0).IsAir) faults.Add("the test column was not empty to start with");

            Put(world, 0, 21, 0, ids.Lava);
            engine.Touch(0, 21, 0);
            engine.Settle(world, changed);

            if (engine.Pending != 0) faults.Add($"the flow never settled: {engine.Pending} cells still queued");

            for (var y = 1; y <= 20; y++)
                if (table.KindOf(At(world, 0, y, 0).Value) == FluidKind.Lava) fell++;

            if (fell < 20) faults.Add($"a fall from y 21 reached only {fell} of the 20 cells under it");

            // Lava decays by two a step above the Emberdeep, so a pool spreads three cells and stops.
            for (var d = 1; d <= 6; d++)
                if (table.KindOf(At(world, d, 1, 0).Value) == FluidKind.Lava) reach = d;

            if (reach != 3) faults.Add($"a landed pool of lava reached {reach} cells, not the 3 its decay allows");

            // ⛔ THE SHARPEST CHECK HERE. A river through VoxelWorld.SetBlock writes an entry per
            // cell to the save and marks the world dirty, so the autosave fires on a world nobody
            // touched. Control: route the flow back through SetBlock and this reads in the hundreds.
            if (world.Edits.Count != 0)
                faults.Add($"a settling river logged {world.Edits.Count} save edits, and should log none");

            if (world.Changed)
                faults.Add("a settling river marked the world dirty, so an autosave would fire on it");
        }

        // ── It settles the same way whatever order the cells are looked at in ────────────────────
        {
            var straight = FluidBox(registry, ids, 0, -1, 1);
            var scrambled = FluidBox(registry, ids, 0, -1, 1);

            foreach (var world in new[] { straight, scrambled })
            {
                Put(world, 0, 12, 0, ids.Water);
                Put(world, 5, 12, 5, ids.Water);

                // A lip, so the water has to find its way round something rather than falling flat.
                for (var x = -3; x <= 3; x++) Put(world, x, 1, 2, ids.Stone);
            }

            var one = new FluidEngine(table);
            one.Touch(0, 12, 0);
            one.Touch(5, 12, 5);
            one.Settle(straight, changed);

            // The same world, seeded in a deliberately silly order and stepped a few cells at a
            // time, which is what a player walking about actually produces.
            var many = new FluidEngine(table);
            for (var i = 0; i < 4000; i++)
            {
                var x = (i * 7919) % 21 - 10;
                var y = (i * 104_729) % 14;
                var z = (i * 1301) % 21 - 10;
                many.Touch(x, y, z);
            }
            many.Touch(0, 12, 0);
            many.Touch(5, 12, 5);
            while (many.Pending > 0) many.Step(scrambled, 7, changed);

            var differ = 0;
            for (var y = 0; y <= 13; y++)
            for (var z = -10; z <= 10; z++)
            for (var x = -10; x <= 10; x++)
                if (At(straight, x, y, z) != At(scrambled, x, y, z)) differ++;

            if (differ != 0)
                faults.Add($"settling in a different order gave a different world in {differ} cells");

            // The control: the two worlds must actually hold something, or "identical" is two
            // identically empty boxes.
            var held = 0;
            for (var y = 0; y <= 13; y++)
            for (var z = -10; z <= 10; z++)
            for (var x = -10; x <= 10; x++)
                if (table.KindOf(At(straight, x, y, z).Value) == FluidKind.Water) held++;

            if (held < 50) faults.Add($"the order-independence check compared two nearly empty worlds ({held} cells)");
        }

        // ── Break a block beside it and it fills the space; take the source and it drains ────────
        var filled = 0;
        var drained = 0;
        {
            var world = FluidBox(registry, ids, 0, -1, 1);
            var engine = new FluidEngine(table);

            // A wall across the channel at x = 2, with the source behind it.
            for (var y = 1; y <= 4; y++)
            for (var z = -4; z <= 4; z++)
                Put(world, 2, y, z, ids.Stone);

            Put(world, 0, 1, 0, ids.Water);
            engine.Touch(0, 1, 0);
            engine.Settle(world, changed);

            // Control: nothing has got past the wall yet. Without this the fill is measuring water
            // that was always there.
            for (var x = 3; x <= 6; x++)
                if (!At(world, x, 1, 0).IsAir) faults.Add($"water was already past the wall at x {x}");

            // Break one block of it, exactly as a player would.
            Put(world, 2, 1, 0, BlockId.Air);
            engine.Touch(2, 1, 0);
            engine.Settle(world, changed);

            for (var x = 3; x <= 8; x++)
                if (table.KindOf(At(world, x, 1, 0).Value) == FluidKind.Water) filled++;

            if (filled < 5)
                faults.Add($"breaking the wall let water reach only {filled} cells beyond it");

            // Now take the source away. Everything it was feeding has to go, which is the half of
            // this that a naive implementation gets wrong and the half the save depends on.
            var before = CountFluid(world, table, FluidKind.Water);

            Put(world, 0, 1, 0, BlockId.Air);
            engine.Touch(0, 1, 0);
            engine.Settle(world, changed);

            drained = before - CountFluid(world, table, FluidKind.Water);

            if (CountFluid(world, table, FluidKind.Water) != 0)
                faults.Add($"taking the source left {CountFluid(world, table, FluidKind.Water)} cells of water behind");

            if (before < 8)
                faults.Add($"the drain check had only {before} cells to drain, so it proves nothing");
        }

        // ── It stalls at the edge of the loaded world, and resumes when the chunk arrives ────────
        var stalledAt = 0;
        var resumedTo = 0;
        {
            // Chunks from y 0 up only: everything below y 0 is absent, not empty.
            var world = new VoxelWorld(registry);
            for (var cz = -1; cz <= 1; cz++)
            for (var cx = -1; cx <= 1; cx++)
                world.GetOrCreateChunk(new ChunkPos(cx, 0, cz));

            var engine = new FluidEngine(table);
            Put(world, 0, 20, 0, ids.Water);
            engine.Touch(0, 20, 0);
            engine.Settle(world, changed);

            // The control first: it did start. "It stopped at the seam" and "it never ran" look
            // identical from below.
            if (table.KindOf(At(world, 0, 19, 0).Value) != FluidKind.Water)
                faults.Add("the fall never started, so nothing below it means nothing");

            for (var y = 19; y >= 0; y--)
                if (table.KindOf(At(world, 0, y, 0).Value) == FluidKind.Water) stalledAt = y;

            if (stalledAt != 0)
                faults.Add($"a fall into unloaded space stopped at y {stalledAt}, not at the seam at y 0");

            // The chunk arrives. Nothing in it changed — it did not exist — so only being shown it
            // can restart the fall.
            for (var cz = -1; cz <= 1; cz++)
            for (var cx = -1; cx <= 1; cx++)
                world.GetOrCreateChunk(new ChunkPos(cx, -1, cz));

            for (var z = -6; z <= 6; z++)
            for (var x = -6; x <= 6; x++)
                Put(world, x, -20, z, ids.Stone);

            engine.TouchChunk(world, new ChunkPos(0, -1, 0));
            engine.Settle(world, changed);

            for (var y = -1; y >= -19; y--)
                if (table.KindOf(At(world, 0, y, 0).Value) == FluidKind.Water) resumedTo = y;

            if (resumedTo > -19)
                faults.Add($"the fall resumed only to y {resumedTo} after the chunk below it loaded");
        }

        // ── Lava meets water, and what it leaves depends on which lava it was ────────────────────
        //
        // ⛔ BOTH HALVES, SEPARATELY, and the second one IS the design decision. "It became coal"
        // passes on a rule that leaves the lava sitting there — which would make coal an infinite
        // tap, and coal is fuel AND black dye AND every torch in the game. Asserting the source is
        // GONE is what separates a transformation from a generator.
        var quenched = 0;
        var chilled = 0;
        {
            var world = FluidBox(registry, ids, 0, -1, 1);
            var engine = new FluidEngine(table);

            // Two sources side by side: the lava is a source, so it should be consumed.
            Put(world, 0, 1, 0, ids.Lava);
            Put(world, 1, 1, 0, ids.Water);
            engine.Touch(0, 1, 0);
            engine.Touch(1, 1, 0);
            engine.Settle(world, changed);

            var left = At(world, 0, 1, 0);
            if (left != registry.ByName("coal_block").Id)
                faults.Add($"a quenched lava source left '{registry[left].Name}', not a block of coal");
            else quenched++;

            if (table.KindOf(left.Value) == FluidKind.Lava)
                faults.Add("the lava source survived being quenched, so coal is an infinite tap");

            // ⛳ It is a real world edit and has to be in the save, unlike everything else the flow
            // does. Reversible fluid state is derived; irreversible terrain change is written down.
            if (world.Edits.Count == 0)
                faults.Add("a quenched source was not recorded, so it comes back when the world reopens");
        }
        {
            var world = FluidBox(registry, ids, 0, -1, 1);
            var engine = new FluidEngine(table);

            // A lava source three cells away, so what meets the water is its flowing tail.
            Put(world, -3, 1, 0, ids.Lava);
            Put(world, 1, 1, 0, ids.Water);
            engine.Touch(-3, 1, 0);
            engine.Touch(1, 1, 0);
            engine.Settle(world, changed);

            var rubble = registry.ByName("rubble").Id;
            for (var x = -3; x <= 1; x++) if (At(world, x, 1, 0) == rubble) chilled++;

            if (chilled == 0) faults.Add("flowing lava quenched by water left no rubble anywhere");

            // ⛳ AND THE SOURCE IS STILL THERE. That is the difference between the two rules: a
            // stone generator is a device the genre expects and rubble is worth almost nothing, so
            // it may go on making it — but the coal one must not.
            if (!table.IsSource(At(world, -3, 1, 0).Value))
                faults.Add("the lava source behind a quenched flow was consumed too");
        }

        // ── Away from each other, the two fluids do not mix ──────────────────────────────────────
        {
            var world = FluidBox(registry, ids, 0, -1, 1);
            var engine = new FluidEngine(table);

            Put(world, -9, 1, 0, ids.Water);
            Put(world, 9, 1, 0, ids.Lava);
            engine.Touch(-9, 1, 0);
            engine.Touch(9, 1, 0);
            engine.Settle(world, changed);

            var mixed = 0;
            for (var x = -12; x <= 12; x++)
            {
                var kind = table.KindOf(At(world, x, 1, 0).Value);
                var above = table.KindOf(At(world, x, 2, 0).Value);
                if (kind != FluidKind.None && above != FluidKind.None && kind != above) mixed++;
            }

            if (mixed != 0) faults.Add($"{mixed} cells hold one fluid directly under the other");
        }

        detail = $"a fall crossed {fell} cells and spread {reach}; breaking a wall let it {filled} further "
               + $"and taking the source drained {drained}; a fall stalled at the seam and resumed to "
               + $"y {resumedTo}; a quenched source became coal and a quenched flow left {chilled} rubble "
               + $"with its source intact; only the {quenched} reaction reached the save";

        return faults;
    }

    private static int CountFluid(VoxelWorld world, FluidTable table, FluidKind kind)
    {
        var count = 0;
        foreach (var chunk in world.Chunks)
        foreach (var id in chunk.Raw)
            if (table.KindOf(id) == kind) count++;

        return count;
    }

    /// <summary>
    /// Loads the world round a viewer on the grass and round one in the Emberdeep, and compares the
    /// bill.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>This is the check vertical streaming exists for, and it needs both halves or it
    /// passes a streamer that loads nothing.</b> The claim is not "fewer chunks" — a broken streamer
    /// loads zero and wins. It is that the deep costs <em>less</em> than the surface <b>and</b> that
    /// the chunk the viewer is standing in is loaded in both, which is the positive control.</para>
    /// <para>The number that matters: a full-column streamer loads the same chunks wherever the
    /// viewer stands, so the ratio it reports is exactly 1.00 — and that is what this failed at
    /// before the two rings landed. In a 384-tall world full columns would be twelve layers of every
    /// column in the ring, which is where the third comparison comes from.</para>
    /// </remarks>
    private static bool VerticalStreamingPays(
        WorldSeed seed, BlockRegistry registry, StarterBlocks.Ids ids, float oceanCoverage, out string detail)
    {
        const int radius = 6;

        var generator = new TerrainGenerator(seed, ids, oceanCoverage);
        var surfaceY = generator.SurfaceHeight(0, 0) + 2;
        var deepY = TerrainGenerator.EmberdeepTop - 8;

        var (surfaceChunks, surfaceHeld) = LoadAround(new Vector3(0.5f, surfaceY, 0.5f));
        var (deepChunks, deepHeld) = LoadAround(new Vector3(0.5f, deepY, 0.5f));

        // What the old streamer would have cost: every column in the load ring, twelve layers of it.
        var reach = radius + 3;
        var columns = 0;
        for (var dz = -reach; dz <= reach; dz++)
        for (var dx = -reach; dx <= reach; dx++)
            if (dx * dx + dz * dz <= reach * reach) columns++;

        var fullColumns = columns * TerrainGenerator.ChunksTall;
        var ratio = surfaceChunks == 0 ? 1.0 : deepChunks / (double)surfaceChunks;

        detail = $"surface {surfaceChunks:N0} chunks, deep {deepChunks:N0} ({ratio:F2}x), "
               + $"full columns would be {fullColumns:N0}; the viewer's own chunk is loaded "
               + (surfaceHeld && deepHeld ? "in both" : surfaceHeld ? "only on the surface" : "nowhere");

        return surfaceHeld && deepHeld && ratio < 0.7 && surfaceChunks < fullColumns;

        (int Count, bool Held) LoadAround(Vector3 viewer)
        {
            using var streamer = new WorldStreamer(
                registry, new TerrainGenerator(seed, ids, oceanCoverage), radius);

            streamer.Update(viewer);

            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 30_000)
            {
                streamer.PromoteReadyChunks();
                while (streamer.TryDequeueMesh(out _)) { }

                if (streamer.PendingGenerate == 0 && streamer.PendingLight == 0
                    && streamer.PendingMesh == 0) break;

                Thread.Sleep(2);
            }

            var here = ChunkPos.FromWorld(
                (int)MathF.Floor(viewer.X), (int)MathF.Floor(viewer.Y), (int)MathF.Floor(viewer.Z));

            return (streamer.LoadedChunks, streamer.World.TryGetChunk(here, out _));
        }
    }

    private static bool StreamingMatchesBatch(
        WorldSeed seed, BlockRegistry registry, StarterBlocks.Ids ids, float oceanCoverage, out string detail)
    {
        const int radius = 3;
        const int settleTimeoutMs = 30_000;

        var produced = new Dictionary<ChunkPos, (int Verts, int Indices)>();
        var loaded = new HashSet<ChunkPos>();

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

            // ⛔ EXACTLY THE SET THE STREAMER HELD, and this is the whole correctness of the check.
            // The reference used to be "the produced chunks and their neighbours", which was the same
            // set back when the streamer loaded full columns. It is not any more: a chunk at the
            // bottom of the vertical band has a neighbour under it that nothing ever asked for, and a
            // reference built with that neighbour present is lit by emitters the streamed world
            // cannot see. Measured: chunk (0,−2,0) came back 9,372 verts against 9,684, and nothing
            // whatever was wrong with the streaming.
            foreach (var chunk in streamer.World.Chunks) loaded.Add(chunk.Position);
        }

        // Reference world: the same chunks, generated up front rather than as the player walked.
        var batchWorld = new VoxelWorld(registry);
        var batchGenerator = new TerrainGenerator(seed, ids, oceanCoverage);

        foreach (var pos in loaded)
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
            for (var y = TerrainGenerator.WorldTop - 1; y >= TerrainGenerator.WorldBottom; y--)
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
    private static List<string> MiningSelfTest(
        BlockRegistry registry, ItemRegistry items, StarterBlocks.Ids ids)
    {
        var faults = new List<string>();
        const float dt = 1f / 60f;
        var cell = (0, 64, 0);

        float Break(BlockType type, ItemType? held = null, int maxFrames = 7200)
        {
            var mining = new PlayerMining();
            for (var frame = 1; frame <= maxFrames; frame++)
                if (mining.Update(dt, type, cell, mining: true, held)) return frame * dt;

            return -1f;
        }

        var dirt = registry[ids.Dirt];
        var stone = registry[ids.Stone];
        var iron = registry[ids.IronOre];
        var leaves = registry[ids.Leaves];

        // ⚠ MEASURED WITH A WOODEN PICKAXE IN HAND, and it used to be bare-handed. Two rungs under
        // is a refusal now, so iron ore taken by fist is INFINITE — which is the rule working, and
        // which made the old spread check report that the ore never broke. The spread is still the
        // thing being asked about; it just has to be asked with something that can break all four.
        var starter = items.ByName("wood_pickaxe");

        foreach (var type in (ReadOnlySpan<BlockType>)[leaves, dirt, stone, iron])
        {
            var measured = Break(type, starter);
            var wanted = MiningRules.SecondsToBreak(type, starter);

            if (measured < 0f) faults.Add($"{type.Name} never broke");
            else if (MathF.Abs(measured - wanted) > dt * 2f)
                faults.Add($"{type.Name} took {measured:F2}s, the rule says {wanted:F2}s");
        }

        var leafTime = Break(leaves, starter);
        var dirtTime = Break(dirt, starter);
        var stoneTime = Break(stone, starter);
        var ironTime = Break(iron, starter);

        if (!(leafTime < dirtTime && dirtTime < stoneTime && stoneTime < ironTime))
            faults.Add($"materials out of order: leaves {leafTime:F2}, dirt {dirtTime:F2}, stone {stoneTime:F2}, ore {ironTime:F2}");

        if (ironTime / leafTime < 10f)
            faults.Add($"hardest is only {ironTime / leafTime:F1}x the softest — materials barely differ");

        // ⛳⛳ THE ORE LADDER, WHICH IS THE THING THE USER ASKED FOR AND WHICH USED TO RUN BACKWARDS.
        // ⛔ Measured with each ore's MINIMUM VIABLE pickaxe, which is the comparison that matters
        // and the one nothing was making: every ore was Hardness 3 while tool speed ran 2, 4, 6, 8,
        // 10 — so coal took 2.25s, iron 1.13s, gold 0.75s and stormglass 0.56s. Going deeper made
        // the work QUICKER. A check on any single ore passes that build; only the sequence catches
        // it.
        var ladder = new List<(string Ore, string Pick, float Seconds)>();

        foreach (var (ore, pick) in (ReadOnlySpan<(string, string)>)
                 [
                     ("coal_ore", "wood_pickaxe"),
                     ("iron_ore", "stone_pickaxe"),
                     ("gold_ore", "copper_pickaxe"),
                     ("stormglass_ore", "iron_pickaxe"),
                     ("diamond_ore", "stormglass_pickaxe"),
                 ])
        {
            ladder.Add((ore, pick, MiningRules.SecondsToBreak(registry.ByName(ore), items.ByName(pick))));
        }

        for (var i = 1; i < ladder.Count; i++)
        {
            if (ladder[i].Seconds > ladder[i - 1].Seconds) continue;
            faults.Add($"{ladder[i].Ore} takes {ladder[i].Seconds:F2}s with its own pickaxe against "
                     + $"{ladder[i - 1].Ore}'s {ladder[i - 1].Seconds:F2}s — the ladder runs backwards");
        }

        // ⚠ And it must climb without becoming a chore, which is the other half of what was asked.
        // A ceiling as well as a floor: a top ore that took thirty seconds with the right tool would
        // pass every line above and be exactly the thing the user said they did not want.
        if (ladder[^1].Seconds > ladder[0].Seconds * 3f)
            faults.Add($"{ladder[^1].Ore} is {ladder[^1].Seconds / ladder[0].Seconds:F1}x the work of "
                     + $"{ladder[0].Ore} with the right tool — that is a chore, not a climb");

        // ⛳ AND THE PROPERTY THAT MAKES THE RULE TEACHABLE: being one rung under costs about the
        // same wherever a player meets it, because the hardness curve is matched to the speed curve.
        // If those two ever drift, one tier of the ladder quietly becomes the painful one.
        var underhand = new List<float>();

        foreach (var (ore, pick) in (ReadOnlySpan<(string, string)>)
                 [
                     ("iron_ore", "wood_pickaxe"),
                     ("gold_ore", "stone_pickaxe"),
                     ("stormglass_ore", "copper_pickaxe"),
                     ("diamond_ore", "iron_pickaxe"),
                 ])
        {
            var seconds = MiningRules.SecondsToBreak(registry.ByName(ore), items.ByName(pick));

            if (float.IsInfinity(seconds))
                faults.Add($"a {pick} is refused at {ore}, which is only one rung above it");
            else underhand.Add(seconds);
        }

        if (underhand.Count > 0 && underhand.Max() > underhand.Min() * 1.5f)
            faults.Add($"one rung under costs {underhand.Min():F1}s at one tier and "
                     + $"{underhand.Max():F1}s at another — the lesson is not the same twice");

        // ⛔ The refusal, and the CONTROL beside it. "Two rungs under is refused" is true of a build
        // that refuses everything; the pair is what says the line is where it should be.
        if (!MiningRules.TooHard(iron, null))
            faults.Add("iron ore can be broken bare-handed, two rungs under it");
        if (MiningRules.TooHard(stone, null))
            faults.Add("stone cannot be broken bare-handed, so a player with nothing cannot dig at all");

        // And a refused swing must cost nothing whatever — no progress, which is what makes it a
        // refusal rather than a very long wait somebody is wearing a pickaxe out on.
        var refused = new PlayerMining();
        for (var frame = 0; frame < 600; frame++) refused.Update(dt, iron, cell, mining: true, null);

        if (refused.Progress > 0f)
            faults.Add($"ten seconds of swinging at a refused block made {refused.Progress:P0} of progress");

        // The whole point of a tool. Every rung of the ladder has to be quicker than the one below
        // it on the block it is for, and none of them may help on a block of another class — a
        // shovel that speeds up stone is a tool table read without its class.
        var byTier = new List<(string Name, float Seconds)>();
        foreach (var tier in StarterItems.Tiers)
            byTier.Add((tier.Name, Break(stone, items.ByName($"{tier.Name}_pickaxe"))));

        var bareStone = Break(stone);
        foreach (var rung in byTier)
        {
            if (rung.Seconds < bareStone) continue;
            faults.Add($"a {rung.Name} pickaxe took {rung.Seconds:F2}s on stone, no better than the {bareStone:F2}s a hand takes");
        }

        // Ordered by speed rather than by tier, because gold is deliberately out of tier order —
        // it cuts faster than iron and reaches less far, and a check written as "each rung beats
        // the last" would fail a design decision rather than a defect.
        var fastest = byTier[0];
        var slowest = byTier[0];
        foreach (var rung in byTier)
        {
            if (rung.Seconds < fastest.Seconds) fastest = rung;
            if (rung.Seconds > slowest.Seconds) slowest = rung;
        }

        if (fastest.Name != "gold")
            faults.Add($"the quickest pickaxe is {fastest.Name}, and gold is meant to be the quick one");

        // The spread, not the order. A build that ignores the speed column entirely still puts the
        // rungs in *some* order, and the first one it happens to find is as likely to be right as
        // wrong — a control run with the divide removed passed the ordering test on a coin toss and
        // failed here. Nominally the ends are six times apart; four is the band's floor.
        if (slowest.Seconds / fastest.Seconds < 4f)
            faults.Add(
                $"the ladder spans only {slowest.Seconds / fastest.Seconds:F1}x from {slowest.Name} "
                + $"to {fastest.Name}, so the speed column is barely read");

        var shovelOnStone = Break(stone, items.ByName("iron_shovel"));
        if (MathF.Abs(shovelOnStone - bareStone) > dt * 2f)
            faults.Add($"an iron shovel took {shovelOnStone:F2}s on stone against a bare hand's {bareStone:F2}s");

        // And the penalty for the wrong thing is real: stone by hand is the genre's own long wait.
        if (bareStone < 6f)
            faults.Add($"stone by hand takes {bareStone:F2}s, too short to be worth crafting a pickaxe over");

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
    /// Extrudes every tool and a torch, and asks that what came out is a solid.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The control is built in, and it has to be.</b> A cube wearing an icon on six faces —
    /// which is what a held tool used to be — has <em>more</em> geometry than an extrusion does,
    /// draws without complaint, and looks correct in a still slot. So counting quads proves nothing;
    /// the claim is that the number of walls equals the number of steps round the silhouette, which
    /// a cube cannot satisfy for any drawing (it has four sides whatever is painted on it). A torch
    /// is in the list because its ink is a narrow stick with a lot of boundary for its area, and a
    /// tool is a diagonal with almost none of its tile filled.
    /// </remarks>
    private static List<string> ValidateSprites()
    {
        var faults = new List<string>();

        for (var shape = 0; shape < TileGen.ToolShapes.Length; shape++)
        {
            var tile = TileGen.IconTool(4000 + shape, shape, 150, 120, 90);
            faults.AddRange(ItemSprite.Validate(ItemSprite.Mask(tile, TileGen.Size), $"tool {shape}"));
        }

        faults.AddRange(ItemSprite.Validate(ItemSprite.Mask(TileGen.Torch(7001), TileGen.Size), "torch"));

        // And the grip has to be ON the drawing. A hold point in the transparent corner of a tile is
        // what put the torch in mid-air beside a closed fist.
        for (var shape = 0; shape < TileGen.ToolShapes.Length; shape++)
        {
            var mask = ItemSprite.Mask(TileGen.IconTool(4000 + shape, shape, 150, 120, 90), TileGen.Size);
            var hold = ItemSprite.Hold(mask);

            var gx = (int)((hold.X + 0.5f) * ItemSprite.Grid);
            var gy = (int)((0.5f - hold.Y) * ItemSprite.Grid);

            if (gx < 0 || gy < 0 || gx >= ItemSprite.Grid || gy >= ItemSprite.Grid || !mask[gy * ItemSprite.Grid + gx])
                faults.Add($"tool {shape} is gripped at {gx},{gy}, where nothing is drawn");
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
