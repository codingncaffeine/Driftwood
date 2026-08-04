using Driftwood.Core.Blocks;
using Driftwood.Core.World;

namespace Driftwood.Core.Gen;

/// <summary>
/// Turns a <see cref="WorldSeed"/> into terrain. Fully deterministic: the same seed always
/// produces the same world, and chunks may be generated in any order on any thread.
/// </summary>
/// <remarks>
/// Generation runs in two passes. <see cref="GenerateChunk"/> fills a chunk from its own columns
/// only, so it never needs a neighbour. <see cref="DecorateRegion"/> then plants trees, which do
/// straddle chunk seams and therefore write through the world.
/// <para>That split is deliberate. Once chunks stream in at P1 a tree whose trunk is in a loaded
/// chunk and whose canopy is in an unloaded one needs deferred placement — the structure gets
/// queued against the chunk that is not there yet and applied when it arrives. Keeping decoration
/// already separated from terrain fill is what makes that change a swap of one pass rather than a
/// rewrite of the generator.</para>
/// </remarks>
public sealed class TerrainGenerator
{
    public const int SeaLevel = 62;
    public const int WorldHeight = 128;

    /// <summary>
    /// Trees are rolled once per cell of this grid. Tight on purpose: at 7 the crowns never
    /// touched and a forest read as an orchard of separate lollipops. At 5, with crowns up to
    /// three wide, neighbours interlock into a continuous canopy with trunks running up through
    /// it — which is the shape a wood actually has. How many of those cells grow anything is left
    /// to the forest-density field, so tightening the grid thickens the woods without carpeting
    /// the meadows.
    /// </summary>
    private const int TreeGrid = 5;

    /// <summary>
    /// How far anything a tree grows can reach sideways from its cell's trunk position. Chunk
    /// decoration widens its search by this much, so it must stay well under <see cref="Chunk.Size"/>
    /// — a structure wider than a chunk would need a search radius bigger than one neighbour.
    /// </summary>
    /// <remarks>
    /// Adds up as: a crown offset of one plus a crown radius of three, or a branch two long with its
    /// leaf knot one wider. Five covers both with a block to spare. The audit
    /// decorates once normally and once at twice this reach and fails if they differ, which is the
    /// only thing that catches a chunk quietly missing the far edge of a tree.
    /// </remarks>
    private const int CanopyRadius = 5;

    private readonly StarterBlocks.Ids _ids;

    private readonly int _seedContinent;
    private readonly int _seedHills;
    private readonly int _seedDetail;
    private readonly int _seedCaveA;
    private readonly int _seedCaveB;
    private readonly int _seedCoal;
    private readonly int _seedIron;
    private readonly int _seedGravel;
    private readonly int _seedEmber;
    private readonly int _seedTree;
    private readonly int _seedForest;

    /// <summary>Default share of the surface that sits at or below sea level.</summary>
    public const float DefaultOceanCoverage = 0.25f;

    private readonly float _heightBias;

    public WorldSeed Seed { get; }

    /// <summary>The ocean coverage this generator was calibrated to hit.</summary>
    public float OceanCoverage { get; }

    public TerrainGenerator(WorldSeed seed, StarterBlocks.Ids ids, float oceanCoverage = DefaultOceanCoverage)
    {
        Seed = seed;
        _ids = ids;
        OceanCoverage = Math.Clamp(oceanCoverage, 0f, 0.9f);

        _seedContinent = seed.Derive("terrain.continent");
        _seedHills = seed.Derive("terrain.hills");
        _seedDetail = seed.Derive("terrain.detail");
        _seedCaveA = seed.Derive("caves.a");
        _seedCaveB = seed.Derive("caves.b");
        _seedCoal = seed.Derive("ore.coal");
        _seedIron = seed.Derive("ore.iron");
        _seedGravel = seed.Derive("deposit.gravel");
        _seedEmber = seed.Derive("ore.emberstone");
        _seedTree = seed.Derive("decor.tree");
        _seedForest = seed.Derive("decor.forest");

        _heightBias = CalibrateHeightBias(OceanCoverage);
    }

    /// <summary>
    /// Finds the vertical offset that puts the requested share of the surface under water.
    /// </summary>
    /// <remarks>
    /// Ocean coverage is a design target, so it should not be an emergent accident of whichever
    /// noise constants happened to be chosen. Sampling the raw height field and taking the
    /// quantile that ought to sit at sea level pins the number exactly, and pins it per seed —
    /// otherwise one seed spawns a continent and the next an archipelago, both nominally "25%".
    /// <para>Sampling is over a fixed grid, so this stays deterministic and the same seed keeps
    /// producing the same world.</para>
    /// </remarks>
    private float CalibrateHeightBias(float oceanCoverage)
    {
        // Wide enough to cross several continents at a 640-block wavelength.
        const int Span = 8192;
        const int Stride = 128;

        var side = Span / Stride;
        var samples = new float[side * side];

        var i = 0;
        for (var z = -Span / 2; z < Span / 2; z += Stride)
        for (var x = -Span / 2; x < Span / 2; x += Stride)
            samples[i++] = RawHeight(x, z);

        Array.Sort(samples);

        var index = Math.Clamp((int)(oceanCoverage * samples.Length), 0, samples.Length - 1);
        return SeaLevel - samples[index];
    }

    /// <summary>Surface height for a world column: the Y of its topmost terrain block.</summary>
    /// <remarks>
    /// Three scales: continents set the broad land/sea shape, hills give it relief, detail keeps
    /// ridgelines from looking machined.
    /// <para>Continentalness runs through a shaping curve before it becomes height. Raw fBm is
    /// bell-shaped around zero, so scaling it directly produces a world of uniform rolling hills —
    /// every column lands near the mean and nothing is ever flat or ever steep. The curve
    /// compresses the middle and stretches the tails, which is what separates plains from
    /// mountains. It also has to earn back the range fBm never uses: normalising by the sum of
    /// octave amplitudes leaves the output at roughly +/-0.4, not +/-1, so a constant that reads
    /// like "44 blocks of relief" would otherwise deliver about 17.</para>
    /// </remarks>
    public int SurfaceHeight(int wx, int wz) =>
        Math.Clamp((int)MathF.Round(RawHeight(wx, wz) + _heightBias), 1, WorldHeight - 8);

    /// <summary>
    /// Surface height before the ocean-coverage bias is applied. Kept separate because the
    /// calibration pass has to sample it while the bias is still being computed.
    /// </summary>
    private float RawHeight(int wx, int wz)
    {
        var x = wx;
        var z = wz;

        var continent = Noise.Fbm2(x / 640f, z / 640f, _seedContinent, 5);
        var shaped = MathF.Sign(continent) * MathF.Pow(MathF.Min(MathF.Abs(continent) * 2.4f, 1f), 1.6f);

        var hills = Noise.Fbm2(x / 150f, z / 150f, _seedHills, 4);
        var detail = Noise.Fbm2(x / 38f, z / 38f, _seedDetail, 3);

        // Hills flatten out over deep ocean so the seabed does not mirror the mountains.
        var landness = Math.Clamp(shaped * 2f + 0.6f, 0.15f, 1f);

        return SeaLevel + shaped * 44f + hills * 16f * landness + detail * 4f;
    }

    /// <summary>Fills one chunk with stone, soil, ore, water and caves. Neighbour-free.</summary>
    public void GenerateChunk(Chunk chunk)
    {
        var (ox, oy, oz) = chunk.Position.Origin;

        // One heightmap for the chunk; recomputing per block would evaluate the same
        // three fBm stacks 32 times over.
        Span<int> heights = stackalloc int[Chunk.Size * Chunk.Size];
        for (var z = 0; z < Chunk.Size; z++)
        for (var x = 0; x < Chunk.Size; x++)
            heights[z * Chunk.Size + x] = SurfaceHeight(ox + x, oz + z);

        var raw = chunk.Raw;

        for (var z = 0; z < Chunk.Size; z++)
        for (var x = 0; x < Chunk.Size; x++)
        {
            var surface = heights[z * Chunk.Size + x];
            var wx = ox + x;
            var wz = oz + z;
            var beach = surface <= SeaLevel + 2;

            for (var y = 0; y < Chunk.Size; y++)
            {
                var wy = oy + y;
                if (wy >= WorldHeight) break;

                ushort id;

                if (wy == 0)
                {
                    id = _ids.Bedrock;
                }
                else if (wy > surface)
                {
                    id = wy <= SeaLevel ? _ids.Water.Value : (ushort)0;
                }
                else
                {
                    var depth = surface - wy;
                    if (depth == 0) id = beach ? _ids.Sand.Value : _ids.Grass.Value;
                    else if (depth <= 3) id = beach ? _ids.Sand.Value : _ids.Dirt.Value;
                    else id = _ids.Stone;

                    // Caves cut through everything but bedrock and the seabed. Two independent
                    // fields intersected gives connected tunnels; a single field gives flat sheets.
                    if (wy > 1 && depth > 1 && IsCave(wx, wy, wz))
                    {
                        // Do not drain the ocean into a cave system.
                        id = wy <= SeaLevel && surface <= SeaLevel ? id : (ushort)0;
                    }

                    if (id == _ids.Stone.Value) id = OreAt(wx, wy, wz);
                }

                raw[Chunk.Index(x, y, z)] = id;
            }
        }

        chunk.RecountSolid();
    }

    private bool IsCave(int x, int y, int z)
    {
        const float threshold = 0.08f;
        var a = Noise.Fbm3(x / 48f, y / 24f, z / 48f, _seedCaveA, 3);
        if (MathF.Abs(a) >= threshold) return false;
        var b = Noise.Fbm3(x / 48f, y / 24f, z / 48f, _seedCaveB, 3);
        return MathF.Abs(b) < threshold;
    }

    /// <remarks>
    /// Thresholds are calibrated against what two-octave fBm actually produces, which peaks near
    /// +/-0.5 rather than +/-1 — a threshold that reads as "rare" on paper spawns nothing at all.
    /// <para>Noise thresholding gives blobs but only indirect control over how much ore exists.
    /// P4 replaces this with explicit vein placement — roll N vein origins per chunk per depth
    /// band and grow each one — which is how a designer sets "iron should be twice as common as
    /// gold" and gets it. The census in <c>--audit</c> is what keeps either method honest.</para>
    /// </remarks>
    private ushort OreAt(int x, int y, int z)
    {
        // Emberstone is deeper and rarer than anything else, and it is placed before the metals so
        // a cell that qualifies for both becomes the interesting one. Caves are carved before ore
        // is assigned, so a vein only ever forms in the rock that survived — which is why one shows
        // up as a glow in a cave wall rather than as a lamp sealed inside a mountain.
        if (y is >= 4 and <= 40 && Noise.Fbm3(x / 7f, y / 7f, z / 7f, _seedEmber, 2) > 0.53f)
            return _ids.Emberstone;

        // Iron sits deeper and rarer than coal, which is the first progression gate the
        // survival loop leans on.
        // Target mix, checked by the audit: coal near 0.8% of stone, iron near 0.35%, coal
        // roughly twice as common as iron so the first tool tier is the easy one.
        if (y is >= 4 and <= 58 && Noise.Fbm3(x / 9f, y / 9f, z / 9f, _seedIron, 2) > 0.50f)
            return _ids.IronOre;
        if (y is >= 4 and <= 92 && Noise.Fbm3(x / 12f, y / 12f, z / 12f, _seedCoal, 2) > 0.475f)
            return _ids.CoalOre;
        if (Noise.Fbm3(x / 14f, y / 14f, z / 14f, _seedGravel, 2) > 0.50f)
            return _ids.Gravel;
        return _ids.Stone;
    }

    /// <summary>
    /// Plants every tree that reaches into this chunk, writing only the blocks that land inside it.
    /// </summary>
    /// <remarks>
    /// <para>A chunk is a pure function of seed and position, including its decoration. Rather than
    /// planting a tree once and queueing the parts that spill across a seam, every chunk
    /// independently considers each tree whose canopy could reach it and keeps its own share. Two
    /// neighbours therefore agree on the overlap without either knowing the other exists.</para>
    /// <para>That is what streaming needs. Chunks arrive in whatever order the player walks, and
    /// there is no moment when "the world" is complete enough to decorate — so a tree straddling a
    /// seam must not depend on which side loaded first. The alternative, queueing pending writes
    /// against chunks that have not loaded yet, needs that queue persisted or the tree half
    /// vanishes when the world reopens.</para>
    /// <para>Order independence rests on tree placement being decided entirely from the heightmap.
    /// The old surface tests read the world, which required terrain to already exist; they were
    /// also redundant, since the fill never carves the surface block and never floods above
    /// <see cref="SeaLevel"/>, so "is it grass with air above" is exactly "is the surface above
    /// sea level plus two".</para>
    /// </remarks>
    public void DecorateChunk(Chunk chunk) => DecorateChunk(chunk, CanopyRadius);

    /// <summary>
    /// Decoration with an explicit search reach, so a test can widen it and prove the production
    /// value is large enough.
    /// </summary>
    /// <remarks>
    /// A reach that is too small does not crash or look obviously wrong — chunks quietly miss the
    /// parts of structures whose origin sits just outside them, leaving canopies with bites taken
    /// out along seams. Nothing in a block census notices. The guard is to decorate twice, once
    /// normally and once with a deliberately excessive reach, and insist the results match.
    /// </remarks>
    public void DecorateChunk(Chunk chunk, int reach)
    {
        var (ox, oy, oz) = chunk.Position.Origin;

        var cellMinX = FloorDiv(ox - reach, TreeGrid);
        var cellMaxX = FloorDiv(ox + Chunk.Size - 1 + reach, TreeGrid);
        var cellMinZ = FloorDiv(oz - reach, TreeGrid);
        var cellMaxZ = FloorDiv(oz + Chunk.Size - 1 + reach, TreeGrid);

        // Fixed cell order so overlapping trees resolve the same way no matter which chunk is
        // asking — the relative order of any two trees is identical in every sub-range.
        for (var cz = cellMinZ; cz <= cellMaxZ; cz++)
        for (var cx = cellMinX; cx <= cellMaxX; cx++)
        {
            if (!TryTreeAt(cx, cz, out var tree)) continue;

            // Cheap vertical reject: nothing of this tree reaches this chunk's slab. Vines hang
            // below the crown, so the lower bound has to allow for the longest strand.
            const int VineReach = 4;
            var lowest = tree.BaseY - VineReach - 1;
            var highest = tree.BaseY + tree.TrunkHeight + 2;
            if (lowest >= oy + Chunk.Size) continue;
            if (highest < oy) continue;

            PlantOakInto(chunk, ox, oy, oz, tree);
        }
    }

    /// <summary>Everything about one tree, decided from its grid cell and nothing else.</summary>
    /// <remarks>
    /// The trunk is a straight vertical run, deliberately. A leaning trunk was tried and taken back
    /// out: shifting the column partway up splits it into two stacks that are visually joined but
    /// separately rooted, and nothing reading the world back can then tell how tall the tree is.
    /// The crown offset below buys the same asymmetry without costing that.
    /// </remarks>
    private readonly record struct TreeSpec(
        int X,
        int Z,
        int BaseY,
        int TrunkHeight,
        int CrownRadius,
        int CrownOffsetX,
        int CrownOffsetZ,
        int CrownDepth,
        int Branches,
        int Seed)
    {
        /// <summary>Y of the topmost log.</summary>
        public int TopY => BaseY + TrunkHeight - 1;
    }

    /// <summary>
    /// Decides whether a tree grid cell grows a tree, and what shape it takes. Pure: depends only
    /// on the seed and the heightmap, never on world state.
    /// </summary>
    /// <remarks>
    /// Every roll is drawn from the cell coordinate with its own derived offset, so adding a new
    /// one — a third branch, a wider crown — does not reshuffle the trees that were already there.
    /// The same reason the generator derives a seed per stage, one level down.
    /// </remarks>
    private bool TryTreeAt(int cellX, int cellZ, out TreeSpec spec)
    {
        spec = default;

        // Forest density rather than a flat coin flip. A uniform scatter gives every part of the
        // world the same handful of trees per hectare, which reads as orchard: no thickets to push
        // through, no clearings to come out into. This makes the odds themselves vary over a few
        // hundred blocks, so the world has woods and it has meadows.
        var density = 0.62f + Noise.Fbm2(cellX * TreeGrid / 210f, cellZ * TreeGrid / 210f, _seedForest, 3) * 1.7f;
        if (Noise.Value2(cellX, cellZ, _seedTree) > density) return false;

        // Jitter inside the cell so the grid never shows through as rows.
        var jx = (int)(Noise.Value2(cellX, cellZ, _seedTree + 17) * TreeGrid);
        var jz = (int)(Noise.Value2(cellX, cellZ, _seedTree + 31) * TreeGrid);
        var x = cellX * TreeGrid + jx;
        var z = cellZ * TreeGrid + jz;

        var surface = SurfaceHeight(x, z);
        if (surface <= SeaLevel + 2) return false;   // beaches and water stay bare

        // Trunk length in logs. Was 4-6, which is the shortest species in the genre and read as
        // scrub from the ground — the whole world was at the bottom of one range. 5-8 with a crown
        // that grows alongside it gives a wood rather than a shrubbery, and the audit holds the
        // mean to a band so a later tweak cannot quietly walk it back down.
        var trunk = 5 + (int)(Noise.Value2(cellX, cellZ, _seedTree + 53) * 4f);

        spec = new TreeSpec(
            X: x,
            Z: z,
            BaseY: surface + 1,
            TrunkHeight: trunk,
            CrownRadius: 2 + (int)(Noise.Value2(cellX, cellZ, _seedTree + 103) * 2f),
            CrownOffsetX: (int)(Noise.Value2(cellX, cellZ, _seedTree + 109) * 3f) - 1,
            CrownOffsetZ: (int)(Noise.Value2(cellX, cellZ, _seedTree + 113) * 3f) - 1,
            CrownDepth: 3 + (int)(Noise.Value2(cellX, cellZ, _seedTree + 127) * 2f),
            Branches: trunk >= 6 ? (int)(Noise.Value2(cellX, cellZ, _seedTree + 131) * 3f) : 0,
            Seed: Noise.Hash2(cellX, cellZ, _seedTree));

        return true;
    }

    /// <summary>Floor division that keeps working left of the origin, unlike <c>/</c>.</summary>
    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -(((-a) + b - 1) / b);

    /// <summary>
    /// Writes whatever parts of one tree land inside this chunk: trunk, branches, crown, vines.
    /// </summary>
    /// <remarks>
    /// Crown before trunk, so a log always wins its own column. Branches before the crowns they
    /// carry, for the same reason.
    /// <para>Nothing here checks whether a neighbouring tree is in the way, and that is deliberate.
    /// Leaves only ever write into air, so where two crowns meet they interlock instead of one
    /// carving the other — which is what makes a stand of trees read as woodland rather than as a
    /// row of separate lollipops. Trunks pass straight up through a neighbour's foliage.</para>
    /// </remarks>
    private void PlantOakInto(Chunk chunk, int ox, int oy, int oz, in TreeSpec tree)
    {
        PlaceBranches(chunk, ox, oy, oz, tree);
        PlaceCrown(
            chunk, ox, oy, oz, tree,
            tree.X + tree.CrownOffsetX, tree.TopY, tree.Z + tree.CrownOffsetZ,
            tree.CrownRadius, tree.CrownDepth);

        for (var i = 0; i < tree.TrunkHeight; i++)
            Place(chunk, ox, oy, oz, tree.X, tree.BaseY + i, tree.Z, _ids.Log);

        // A flare of logs at the foot, so a trunk meets the ground instead of being planted in it.
        for (var face = 0; face < 4; face++)
        {
            if (Noise.Value3(tree.X, face, tree.Z, tree.Seed + 3) > 0.42f) continue;

            var (dx, dz) = face switch
            {
                0 => (1, 0),
                1 => (-1, 0),
                2 => (0, 1),
                _ => (0, -1),
            };
            PlaceIntoAir(chunk, ox, oy, oz, tree.X + dx, tree.BaseY, tree.Z + dz, _ids.Log);
        }
    }

    /// <summary>
    /// Grows one to three limbs out of the upper trunk, each ending in a knot of leaves.
    /// </summary>
    /// <remarks>
    /// This is the single change that stops a tree reading as a cylinder with a ball on top. A
    /// branch also breaks the crown's silhouette from below, which is the angle the player spends
    /// nearly all their time looking from.
    /// </remarks>
    private void PlaceBranches(Chunk chunk, int ox, int oy, int oz, in TreeSpec tree)
    {
        for (var b = 0; b < tree.Branches; b++)
        {
            // Upper half of the trunk only. Lower down a limb just reads as a fork in the trunk.
            var span = Math.Max(1, tree.TrunkHeight / 2);
            var y = tree.BaseY + tree.TrunkHeight / 2 + (int)(Noise.Value3(tree.X, b, tree.Z, tree.Seed + 11) * span);
            y = Math.Min(y, tree.TopY - 1);

            var dir = (int)(Noise.Value3(tree.X, b, tree.Z, tree.Seed + 17) * 4f) & 3;
            var (dx, dz) = dir switch
            {
                0 => (1, 0),
                1 => (-1, 0),
                2 => (0, 1),
                _ => (0, -1),
            };

            var length = 1 + (int)(Noise.Value3(tree.X, b, tree.Z, tree.Seed + 23) * 2f);
            var bx = tree.X;
            var bz = tree.Z;
            var by = y;

            for (var step = 1; step <= length; step++)
            {
                bx += dx;
                bz += dz;
                if (step == length) by++;   // limbs rise as they go out, they do not stick out flat
                PlaceIntoAir(chunk, ox, oy, oz, bx, by, bz, _ids.Log);
            }

            PlaceCrown(chunk, ox, oy, oz, tree, bx, by, bz, radius: 1, depth: 2);
        }
    }

    /// <summary>
    /// Lays a rounded knot of leaves around a point, with gaps punched through it.
    /// </summary>
    /// <remarks>
    /// The gaps matter more than the shape. A crown with no holes in it is a solid mass that reads
    /// as one object; a few missing leaves let sky through, and the lighting pass turns that into
    /// dapples on the ground underneath for free.
    /// </remarks>
    private void PlaceCrown(
        Chunk chunk, int ox, int oy, int oz, in TreeSpec tree,
        int cx, int topY, int cz, int radius, int depth)
    {
        for (var dy = -(depth - 1); dy <= 1; dy++)
        {
            var y = topY + dy;

            // Widest through the middle, narrowing to a cap. Layer radius is the crown's shape;
            // everything else about it is noise on top.
            var layerRadius = dy >= 0 ? Math.Max(1, radius - 1) : radius;
            if (dy == -(depth - 1)) layerRadius = Math.Max(1, radius - 1);

            for (var dz = -layerRadius; dz <= layerRadius; dz++)
            for (var dx = -layerRadius; dx <= layerRadius; dx++)
            {
                // Round off, rather than clipping only the exact corners: at radius 3 a plain
                // corner clip still leaves a very square crown.
                if (dx * dx + dz * dz > layerRadius * layerRadius + layerRadius) continue;

                var lx = cx + dx;
                var lz = cz + dz;

                // Gaps, but never in the column that holds the crown up.
                var atCentre = dx == 0 && dz == 0;
                if (!atCentre && Noise.Value3(lx, y, lz, tree.Seed + 29) > 0.88f) continue;

                PlaceLeaf(chunk, ox, oy, oz, lx, y, lz);

                // Vines hang off the underside only, which is the edge you see them against.
                if (dy == -(depth - 1)) HangVine(chunk, ox, oy, oz, tree, lx, y, lz);
            }
        }
    }

    /// <summary>Hangs a strand of vine below one leaf of a crown's underside.</summary>
    /// <remarks>
    /// Driven from the leaf the tree meant to place, not from the block that ended up there. It has
    /// to be: a strand starting at the bottom of one chunk carries on into the chunk below, and
    /// that chunk cannot see the leaf it hangs from. Replaying the same tree from the same spec is
    /// the only thing both chunks share.
    /// <para>The length is rolled once, on the leaf, rather than per cell — otherwise a curtain
    /// flickers in and out down its own length instead of hanging.</para>
    /// </remarks>
    private void HangVine(Chunk chunk, int ox, int oy, int oz, in TreeSpec tree, int wx, int wy, int wz)
    {
        if (Noise.Value3(wx, wy, wz, tree.Seed + 37) > 0.14f) return;

        var length = 1 + (int)(Noise.Value3(wx, wy, wz, tree.Seed + 41) * 4f);
        for (var d = 1; d <= length; d++)
            PlaceIntoAir(chunk, ox, oy, oz, wx, wy - d, wz, _ids.Vine);
    }

    /// <summary>Writes a block if it falls inside this chunk, and drops it otherwise.</summary>
    private static void Place(Chunk chunk, int ox, int oy, int oz, int wx, int wy, int wz, BlockId id)
    {
        var lx = wx - ox;
        var ly = wy - oy;
        var lz = wz - oz;
        if ((uint)lx >= Chunk.Size || (uint)ly >= Chunk.Size || (uint)lz >= Chunk.Size) return;
        chunk.Set(lx, ly, lz, id);
    }

    /// <summary>Writes a block only into air, so nothing a tree grows eats terrain.</summary>
    private static void PlaceIntoAir(Chunk chunk, int ox, int oy, int oz, int wx, int wy, int wz, BlockId id)
    {
        var lx = wx - ox;
        var ly = wy - oy;
        var lz = wz - oz;
        if ((uint)lx >= Chunk.Size || (uint)ly >= Chunk.Size || (uint)lz >= Chunk.Size) return;
        if (!chunk.Get(lx, ly, lz).IsAir) return;
        chunk.Set(lx, ly, lz, id);
    }

    /// <summary>
    /// Writes a leaf only into air, so a canopy never eats terrain or a neighbouring tree's trunk.
    /// </summary>
    /// <remarks>
    /// Reading the chunk back mid-decoration is safe despite the order-independence rule: every
    /// chunk walks the tree grid in the same cz-then-cx order, so any two trees resolve their
    /// overlap identically no matter which chunk is asking or how many other trees are in range.
    /// Testing the heightmap instead would be pure but wrong — it cannot see the trunk that another
    /// tree already placed, and canopies start carving holes in their neighbours.
    /// </remarks>
    private void PlaceLeaf(Chunk chunk, int ox, int oy, int oz, int wx, int wy, int wz)
    {
        var lx = wx - ox;
        var ly = wy - oy;
        var lz = wz - oz;
        if ((uint)lx >= Chunk.Size || (uint)ly >= Chunk.Size || (uint)lz >= Chunk.Size) return;
        if (!chunk.Get(lx, ly, lz).IsAir) return;
        chunk.Set(lx, ly, lz, _ids.Leaves);
    }
}
