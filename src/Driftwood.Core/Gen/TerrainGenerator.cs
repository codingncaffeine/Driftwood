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

    /// <summary>One past the highest cell there is.</summary>
    public const int WorldTop = 128;

    /// <summary>
    /// The lowest cell there is, and the floor the world is built on.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>The world grew DOWNWARD and never upward, and that is the whole constraint.</b> A
    /// chunk is a pure function of seed and position, so raising <see cref="SeaLevel"/> or shifting
    /// the surface would regenerate every existing world's terrain — the player's edits survive as a
    /// diff and would end up floating in the air or buried in rock. Going down instead leaves
    /// everything at and above y 0 bit-identical, and the only cell whose meaning changed is the
    /// bedrock that used to be at y 0 and is now ordinary rock with 192 blocks under it.</para>
    /// <para>⛳ <see cref="World.ChunkPos.FromWorld"/> already arithmetic-shifts, so negative
    /// coordinates floor correctly and a chunk index below zero is not a special case anywhere.</para>
    /// </remarks>
    public const int WorldBottom = -256;

    /// <summary>How many cells tall the world is. Not an upper bound — see <see cref="WorldTop"/>.</summary>
    public const int WorldHeight = WorldTop - WorldBottom;

    /// <summary>Lowest chunk layer, inclusive.</summary>
    public const int ChunkBottom = WorldBottom >> Chunk.SizeLog2;

    /// <summary>One past the highest chunk layer.</summary>
    public const int ChunkTop = WorldTop >> Chunk.SizeLog2;

    /// <summary>Chunk layers in a full column.</summary>
    public const int ChunksTall = ChunkTop - ChunkBottom;

    /// <summary>
    /// Where the ordinary underground stops and the deep begins.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Everything at or above this is generated exactly as it was before the world got deeper.</b>
    /// Ore bands, rock intrusions and caves above y 0 are untouched, so an existing world keeps every
    /// vein it had; the deep gets its own bands rather than a re-spreading of the old ones, which is
    /// what makes this change additive rather than a reshuffle of somebody's mine.
    /// </remarks>
    public const int DeepFloor = 0;

    /// <summary>
    /// The four bands under the sky, and why there are four of them.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>Measured against the genre rather than picked.</b> The reference world is 384 cells
    /// tall with about 126 of them under sea level; ours is 384 tall with <b>318</b> under it, because
    /// all of the extra was spent downward. That is two and a half times the standard dig.</para>
    /// <para>⛔ <b>The number follows the bands, not the other way round.</b> Depth is nearly free at
    /// runtime — the streamer loads two chunk layers above and below the viewer whatever the world's
    /// height — so the real limit is whether each band reads as a <em>place</em> rather than as a
    /// longer walk to the same rewards. Four of them, each with its own rock, its own ores and its
    /// own reason to be frightening:</para>
    /// <list type="table">
    /// <item><term>62 .. 128</term><description>surface and sky</description></item>
    /// <item><term>0 .. 62</term><description>the ordinary underground — caves, coal, iron, copper</description></item>
    /// <item><term>−64 .. 0</term><description>the deep — deepstone, gold, stormglass, azurite</description></item>
    /// <item><term>−160 .. −64</term><description>the hollows — big caverns, the first lava, water pockets</description></item>
    /// <item><term>−256 .. −160</term><description>the Emberdeep — lava rivers, and the core at the bottom</description></item>
    /// </list>
    /// </remarks>
    public const int HollowsTop = -64;

    /// <summary>Where the Emberdeep begins — lava commoner and runnier the further down.</summary>
    public const int EmberdeepTop = -160;

    /// <summary>True for a cell in the world at all.</summary>
    public static bool InWorld(int wy) => wy >= WorldBottom && wy < WorldTop;

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
    private readonly int _seedCherry;
    private readonly int _seedDiamondOre;
    private readonly int _seedCopper;
    private readonly int _seedGold;
    private readonly int _seedDiamond;
    private readonly int _seedAzurite;
    private readonly int _seedGranite;
    private readonly int _seedAndesite;
    private readonly int _seedDiorite;
    private readonly int _seedDeep;
    private readonly int _seedClay;
    private readonly int _seedMeadow;
    private readonly int _seedDune;
    private readonly int _seedSeagrass;
    private readonly int _seedGlowcap;
    private readonly int _seedReed;
    private readonly int _seedMoss;

    /// <summary>Depth below which no moss grows — rain only seeps so far. The glow floor's
    /// opposite number: moss owns the wet shallows, the glowcap the dry deep.</summary>
    public const int MossFloor = -40;

    /// <summary>Depth above which no glowcap grows. The deep earns its own light; the shallows
    /// have torches and daylight down their shafts, and a glow that grows everywhere means
    /// nothing anywhere.</summary>
    public const int GlowFloor = -80;
    private readonly int _seedFlora;
    private readonly int _seedLavaTable;
    private readonly int _seedLavaPools;

    /// <summary>
    /// Climate, for the one thing terrain reads out of it: where snow lies.
    /// </summary>
    /// <remarks>
    /// The same fields the tinter uses, derived from the same seed, so a region whose grass is
    /// painted cold is the region that has snow on it. Two independent climates would put a
    /// snowfield in the middle of a lush green valley.
    /// </remarks>
    private readonly ClimateField _climate;

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
        _seedCherry = seed.Derive("decor.cherry");
        _seedCopper = seed.Derive("ore.copper");
        _seedGold = seed.Derive("ore.gold");
        // ⚠ The stormglass seam's stream is called "ore.diamond" and must stay called that: a derived
        // seed is a hash of its own name, so renaming it reshuffles every stormglass vein in every
        // world that already exists. The real diamond gets a stream of its own.
        _seedDiamond = seed.Derive("ore.diamond");
        _seedDiamondOre = seed.Derive("ore.deepdiamond");
        _seedAzurite = seed.Derive("ore.azurite");
        _seedGranite = seed.Derive("rock.granite");
        _seedAndesite = seed.Derive("rock.andesite");
        _seedDiorite = seed.Derive("rock.diorite");
        _seedDeep = seed.Derive("rock.deepstone");
        _seedClay = seed.Derive("deposit.clay");
        _seedMeadow = seed.Derive("decor.meadowgrass");
        _seedDune = seed.Derive("decor.dunes");
        _seedSeagrass = seed.Derive("decor.seagrass");
        _seedGlowcap = seed.Derive("decor.glowcap");
        _seedReed = seed.Derive("decor.reeds");
        _seedMoss = seed.Derive("decor.moss");
        _seedFlora = seed.Derive("decor.cave_flora");
        _seedLavaTable = seed.Derive("deep.lava_table");
        _seedLavaPools = seed.Derive("deep.lava_pools");

        _climate = new ClimateField(seed);

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
        Math.Clamp((int)MathF.Round(RawHeight(wx, wz) + _heightBias), 1, WorldTop - 8);

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
                if (wy >= WorldTop) break;
                if (wy < WorldBottom) continue;

                ushort id;

                if (wy == WorldBottom)
                {
                    id = _ids.Bedrock;
                }
                else if (wy > surface)
                {
                    // ⛳ Cold water wears a lid of ice: the top cell of the sea freezes below the
                    // ice line — the temperature field's own coldest fringe, a new READ of the
                    // shipped field and never a re-banding, so played worlds keep every coastline
                    // they had. (Not the snow's rule: that line lives below the field's floor and
                    // only altitude ever crosses it — see IceLine for the measurement.)
                    id = wy > SeaLevel ? (ushort)0
                        : wy == SeaLevel && FrozenSurface(wx, wz) ? _ids.Ice.Value
                        : _ids.Water.Value;
                }
                else
                {
                    var depth = surface - wy;

                    if (depth == 0) id = TopOf(wx, wz, surface, beach);
                    else if (depth <= 3) id = beach ? SoftBeach(wx, wy, wz) : _ids.Dirt.Value;
                    else if (beach && depth <= 7) id = _ids.Sandstone;   // every beach stands on it
                    else id = RockAt(wx, wy, wz);

                    // Caves cut through everything but bedrock and the seabed.
                    if (Carved(wx, wy, wz, surface))
                        id = wy <= LavaTable(wx, wz) ? _ids.Lava.Value : (ushort)0;

                    if (IsRock(id)) id = OreAt(wx, wy, wz, id);
                }

                raw[Chunk.Index(x, y, z)] = id;
            }
        }

        chunk.RecountSolid();
    }

    /// <summary>
    /// True when a cell inside the terrain has been hollowed out by a cave.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>One statement of the rule, because two callers ask it.</b> Generation asks it to decide
    /// what a cell holds; <see cref="SkyLightAt"/> asks it to decide whether a beam gets past. Written
    /// twice, the two would agree until somebody changed one of them — and the symptom would be a
    /// chunk lit for a world it is not in, which is invisible in every census and every timing.
    /// <para>Never the top two blocks of a column, never the two lowest cells of the world, and never
    /// under the open sea: a cave that breached the sea floor would drain the ocean into it.</para>
    /// </remarks>
    public bool Carved(int wx, int wy, int wz, int surface)
    {
        if (wy <= WorldBottom + 1) return false;
        if (surface - wy <= 1) return false;
        if (wy <= SeaLevel && surface <= SeaLevel) return false;
        return IsCave(wx, wy, wz);
    }

    /// <summary>
    /// The height molten rock stands at in this column, the way <see cref="SeaLevel"/> is for water.
    /// </summary>
    /// <remarks>
    /// <para>⛳⛳ <b>Bulk lava is placed AT REST, exactly as the ocean is, and therefore costs no flow
    /// at all.</b> That is the whole trick to affording a molten core: a cave floor below the lava
    /// table is filled with sources by the generator, so a lake, a river running along a cavern and
    /// the shore where the floor rises are all a pure function of seed and position, all settled the
    /// moment they are generated, and all free. Making the generator place a few sources and letting
    /// the flow work out the rest would be tens of thousands of cell updates every time a chunk
    /// loads, for a result the generator can simply state.</para>
    /// <para>⛳ <b>A gradient rather than a boundary</b>, which is the user's own idea and the better
    /// one. The table rises with depth instead of switching on at a line: a stray pool at the top of
    /// the hollows, lakes through the middle of them, and by the floor of the Emberdeep it is above
    /// every cave there is, so the bottom of the world is a molten sea. Every hundred blocks down is
    /// a different place rather than one line crossed once.</para>
    /// <para>The noise is what stops it reading as a spirit level: without it the surface of every
    /// lake in the world would sit at exactly the same height, which is the tell that a number was
    /// picked rather than a place existing.</para>
    /// </remarks>
    public int LavaTable(int wx, int wz)
    {
        // The core: a molten sea across the floor of the world, under every column there is. This is
        // the bottom of the world and it is meant to be a wall rather than a place — you go as far
        // as its shore and no further.
        var core = WorldBottom + 26
                 + (int)(Noise.Fbm2(wx / 300f, wz / 300f, _seedLavaTable, 3) * 16f);

        // Lakes and rivers standing above it, wherever a slow field says there are any. Where the
        // field is strong the table is lifted most of the way up into the hollows, so the deep is
        // threaded with molten water rather than having one surface everybody crosses at once.
        var pool = Noise.Fbm2(wx / 140f, wz / 140f, _seedLavaPools, 3);
        if (pool <= PoolThreshold) return core;

        var lift = Math.Clamp((pool - PoolThreshold) / 0.26f, 0f, 1f);
        return core + (int)(lift * (HollowsTop - 20 - core));
    }

    /// <summary>
    /// How much of the field is lava-bearing. Measured against the census, not chosen.
    /// </summary>
    /// <remarks>
    /// The field runs about −0.4 to 0.4, so this is roughly the top third of columns. Lower and the
    /// deep is one continuous lake with nowhere to stand; higher and a descent turns up nothing but
    /// grey rock and the Emberdeep is a name.
    /// </remarks>
    private const float PoolThreshold = 0.12f;

    /// <summary>
    /// Sky light in a cell, worked out from the generator alone — no chunks, no loaded world.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>This is what lets the streamer stop loading full columns.</b> Sunlight is seeded by
    /// walking a column down from the top of the world, so a chunk 200 blocks under the surface used
    /// to need every chunk above it in memory to know how bright its ceiling was. It does not: the
    /// answer is a pure function of the seed, and this is it.</para>
    /// <para><b>It is O(1) for land and O(ocean depth) for sea, which is the whole reason it is
    /// affordable.</b> The walk stops at the first cell that stops the beam, and the top block of a
    /// column is never carved — <see cref="Carved"/> refuses the top two — so any query below the
    /// surface returns after exactly one step. Only a column that is under water walks at all, and
    /// only as far as the seabed.</para>
    /// <para>⚠ <b>It does not know about player edits</b>, so a shaft dug from the surface down to the
    /// deep reads as solid rock here. That is survivable and self-correcting: the chunks a shaft
    /// passes through are loaded whenever the player is in it, and a loaded chunk's real light is
    /// preferred to this. Anything this gets wrong is corrected the moment the chunk above arrives.
    /// </para>
    /// </remarks>
    public int SkyLightAt(int wx, int wy, int wz)
    {
        if (wy >= WorldTop) return Lighting.LightValue.Max;
        if (wy < WorldBottom) return 0;

        var surface = SurfaceHeight(wx, wz);

        // Above both the ground and the sea there is nothing but air, and air costs a full beam
        // nothing — straight down is the one direction light does not weaken in.
        var highest = Math.Max(surface, SeaLevel);
        if (wy > highest) return Lighting.LightValue.Max;

        var level = Lighting.LightValue.Max;

        for (var y = highest; y >= wy; y--)
        {
            int loss;

            if (y > surface)
            {
                loss = y <= SeaLevel ? 1 : 0;       // a metre of water, or air over a shore
            }
            else
            {
                // Everything the generator puts in the ground is opaque, so an uncarved cell ends
                // the beam outright and there is nothing left to walk for.
                if (!Carved(wx, y, wz, surface)) return 0;
                loss = 0;
            }

            // ⛔ Deliberately the same arithmetic LightEngine.Attenuate uses one step downward, not a
            // second rule that happens to agree — two formulas for one physical step is how a seeded
            // plane and a flooded column end up differing by a level along a seam.
            level = level == Lighting.LightValue.Max && loss == 0
                ? Lighting.LightValue.Max
                : level - 1 - loss;

            if (level <= 0) return 0;
        }

        return level;
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
    /// <summary>An ore and the depths it forms between, inclusive.</summary>
    public readonly record struct OreBand(BlockId Ore, string Name, int Low, int High);

    /// <summary>
    /// The depths every ore forms at, declared once so a census can weigh a vein against the rock it
    /// could actually have formed in.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Written down because "percent of rock" needs a denominator that covers the same cells as
    /// its numerator.</b> Iron forms between y 4 and y 58; measuring it against every rock in a
    /// 384-tall world divides it by how deep the world is, and the day the world got three times
    /// deeper six correct ore bands went red at once without a vein moving. The four that reach the
    /// bedrock say so by starting at <see cref="WorldBottom"/> — that is the deep gradient in
    /// <see cref="DeepOreAt"/>, and it is the whole reason a descent is worth making.
    /// </remarks>
    public static OreBand[] OreBands(StarterBlocks.Ids ids) =>
    [
        new(ids.CoalOre, "coal", 4, 92),
        new(ids.CopperOre, "copper", 8, 72),
        new(ids.IronOre, "iron", 4, 58),
        new(ids.GoldOre, "gold", WorldBottom, 32),
        new(ids.AzuriteOre, "azurite", WorldBottom, 30),
        new(ids.StormglassOre, "stormglass", WorldBottom, 16),

        // ⛳ The only ore that lives WHERE THE LAVA IS. Its band starts below the hollows rather than
        // at the deep floor, so it is not "stormglass but rarer" — it is somewhere else, and the way
        // you meet it is by going down far enough that the rock is already glowing.
        new(ids.DiamondOre, "diamond", WorldBottom, DiamondTop),
        new(ids.Emberstone, "emberstone", WorldBottom, 40),
    ];

    /// <summary>
    /// The highest a diamond ever forms. Well inside the Emberdeep, and nowhere near the hollows.
    /// </summary>
    /// <remarks>
    /// ⚠ Below <see cref="HollowsTop"/> by a long way on purpose: a player who has walked to the
    /// bottom of the ordinary underground should still have a descent left. The lava core tops out
    /// around y −84, so this is where the rock starts being lit from below.
    /// </remarks>
    public const int DiamondTop = -150;

    private ushort OreAt(int x, int y, int z, ushort rock)
    {
        // ⛳ The deep has its own bands, and every one of them is gated on y < DeepFloor so that not
        // one cell at or above y 0 can reach them. That is what makes 250 blocks of new underground
        // ADDITIVE: an existing world keeps every vein it had, because the code that decides those
        // veins is the code below this block and it has not been touched.
        if (y < DeepFloor) return DeepOreAt(x, y, z, rock);

        // Deepest and rarest first, because the first match wins and a cell that qualifies for two
        // veins should become the one worth walking to. Caves are carved before ore is assigned, so
        // a vein only ever forms in rock that survived — which is why a seam shows up in a cave wall
        // rather than sealed inside a mountain where nobody would ever see it.
        //
        // Every threshold is calibrated against what two-octave fBm actually produces, which peaks
        // near +/-0.5 rather than +/-1, and rate falls off as roughly exp(-26 * threshold). The
        // bands in --audit are what keep the whole ladder honest: each tier has to be rarer than the
        // one above it, and no tier may be so rare it never gates anything or so common it stops
        // being a find.
        if (y is >= 2 and <= 16 && Noise.Fbm3(x / 6f, y / 6f, z / 6f, _seedDiamond, 2) > 0.545f)
            return _ids.StormglassOre;
        if (y is >= 2 and <= 30 && Noise.Fbm3(x / 7f, y / 7f, z / 7f, _seedAzurite, 2) > 0.535f)
            return _ids.AzuriteOre;
        if (y is >= 2 and <= 32 && Noise.Fbm3(x / 7f, y / 7f, z / 7f, _seedGold, 2) > 0.535f)
            return _ids.GoldOre;
        if (y is >= 4 and <= 40 && Noise.Fbm3(x / 7f, y / 7f, z / 7f, _seedEmber, 2) > 0.53f)
            return _ids.Emberstone;

        // Iron sits deeper and rarer than coal, which is the first progression gate the survival
        // loop leans on. Copper is shallower and commoner than either — the metal you trip over
        // before you have gone looking for anything.
        if (y is >= 4 and <= 58 && Noise.Fbm3(x / 9f, y / 9f, z / 9f, _seedIron, 2) > 0.50f)
            return _ids.IronOre;
        if (y is >= 8 and <= 72 && Noise.Fbm3(x / 10f, y / 10f, z / 10f, _seedCopper, 2) > 0.49f)
            return _ids.CopperOre;
        if (y is >= 4 and <= 92 && Noise.Fbm3(x / 12f, y / 12f, z / 12f, _seedCoal, 2) > 0.475f)
            return _ids.CoalOre;
        if (Noise.Fbm3(x / 14f, y / 14f, z / 14f, _seedGravel, 2) > 0.50f)
            return _ids.Gravel;

        return rock;
    }

    /// <summary>
    /// What the deep is made of — the ore bands below y 0, which no existing world has ever seen.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>A gradient rather than a set of bands with edges.</b> Every threshold slides with
    /// depth, so a hundred blocks down is a different place from two hundred rather than the same
    /// place with a different label, and there is no line anywhere for a player to notice crossing.
    /// The rate of a threshold goes as roughly <c>exp(-26 * t)</c>, so the eight-hundredths
    /// emberstone travels is about a factor of eight, not a nudge.</para>
    /// <para>The shallow metals — iron, copper, coal — are deliberately absent. Descending should be
    /// about the things that are only down here; a deep full of coal is a longer walk to the same
    /// rewards, which is the failure mode a bigger world has and a deeper one should not.</para>
    /// </remarks>
    private ushort DeepOreAt(int x, int y, int z, ushort rock)
    {
        // 0 at the floor of the ordinary underground, 1 at the bedrock.
        var deepness = Math.Clamp((DeepFloor - y) / (float)(DeepFloor - WorldBottom), 0f, 1f);

        // ⚠ Measured, not chosen. Rate goes as roughly exp(-26 * threshold), so a hundredth is a
        // third: the first pass gave stormglass 0.257% of the rock it can form in against gold's
        // 0.282% — an ore that is meant to be the rarest thing in the game arriving at the same rate
        // as the metal two rungs above it. Every number below was walked back until the ladder had
        // margin rather than a rounding error.
        //
        // ⛳ DIAMOND FIRST, because the first match wins and it has to be the rarest thing there is.
        // Its own gradient measured from ITS OWN band rather than from the deep floor: gated at
        // DiamondTop, a seam near the ceiling of the Emberdeep would otherwise be as common as one
        // on the molten floor, and the whole point of it is that you go all the way down.
        if (y <= DiamondTop)
        {
            var emberness = Math.Clamp(
                (DiamondTop - y) / (float)(DiamondTop - WorldBottom), 0f, 1f);

            if (Noise.Fbm3(x / 5f, y / 5f, z / 5f, _seedDiamondOre, 2) > 0.60f - 0.035f * emberness)
                return _ids.DiamondOre;
        }

        if (Noise.Fbm3(x / 6f, y / 6f, z / 6f, _seedDiamond, 2) > 0.545f - 0.015f * deepness)
            return _ids.StormglassOre;
        if (Noise.Fbm3(x / 7f, y / 7f, z / 7f, _seedAzurite, 2) > 0.535f - 0.025f * deepness)
            return _ids.AzuriteOre;
        if (Noise.Fbm3(x / 7f, y / 7f, z / 7f, _seedGold, 2) > 0.535f - 0.025f * deepness)
            return _ids.GoldOre;

        // Emberstone is the one that says how deep you are. It already glows warm and already lived
        // down here, and it is what the Emberdeep is named after.
        if (Noise.Fbm3(x / 7f, y / 7f, z / 7f, _seedEmber, 2) > 0.53f - 0.09f * deepness)
            return _ids.Emberstone;

        return rock;
    }

    /// <summary>Which rock fills a cell: deepstone at depth, three intrusions, stone otherwise.</summary>
    /// <remarks>
    /// The transition to deepstone is a noise field rather than a flat plane, so descending into it
    /// is a change of country rather than crossing a line somebody drew at a round number.
    /// </remarks>
    private ushort RockAt(int x, int y, int z)
    {
        const int DeepFrom = 12;
        const int DeepBy = 22;

        if (y < DeepBy)
        {
            var blend = (y - DeepFrom) / (float)(DeepBy - DeepFrom);
            if (Noise.Fbm3(x / 18f, y / 6f, z / 18f, _seedDeep, 2) * 1.6f + 0.5f > blend)
                return _ids.Deepstone;
        }

        // Big soft blobs, far coarser than an ore vein. At this scale they read as bodies of rock
        // the tunnels cut through rather than as speckle.
        if (Noise.Fbm3(x / 26f, y / 20f, z / 26f, _seedGranite, 2) > 0.36f) return _ids.Coralstone;
        if (Noise.Fbm3(x / 26f, y / 20f, z / 26f, _seedAndesite, 2) > 0.36f) return _ids.Driftstone;
        if (Noise.Fbm3(x / 26f, y / 20f, z / 26f, _seedDiorite, 2) > 0.36f) return _ids.Saltstone;

        return _ids.Stone;
    }

    private bool IsRock(ushort id) =>
        id == _ids.Stone.Value || id == _ids.Deepstone.Value || id == _ids.Coralstone.Value
        || id == _ids.Driftstone.Value || id == _ids.Saltstone.Value;

    /// <summary>
    /// What lies on top of a column: snow where it is cold, sand at the shore, grass otherwise.
    /// </summary>
    /// <remarks>
    /// Altitude counts for far more here than it does in the colour lookup, and on purpose. A gentle
    /// lapse rate is right for tinting, where the point is that a highland meadow is a slightly
    /// different green; it is wrong for snow, where the point is that a mountain has a white top.
    /// </remarks>
    private ushort TopOf(int x, int z, int surface, bool beach)
    {
        if (beach) return _ids.Sand;
        return SnowDepth(x, z, surface) > 0f ? _ids.Snow : _ids.Grass;
    }

    /// <summary>
    /// How far past the snow line a column sits. Positive is snow, and the more positive the deeper
    /// into the cold.
    /// </summary>
    /// <remarks>
    /// Altitude counts by the square, which is the only shape that does both jobs at once. Linear
    /// cannot: gentle enough to leave the lowlands alone and a warm seed has no snow anywhere in it
    /// (seed 'stonebreak' had none at all), steep enough to guarantee white peaks and half the world
    /// is a snowfield (seed 'driftwood' reached 50%). Squared, the first dozen blocks above the sea
    /// are worth almost nothing and the last dozen are worth everything, which is what a snow line
    /// actually looks like.
    /// </remarks>
    private float SnowDepth(int x, int z, int surface)
    {
        var above = MathF.Max(0f, surface - SeaLevel);
        return SnowLine - (_climate.Temperature(x, z) - above * above / 2600f);
    }

    /// <summary>
    /// Warmth below which open water freezes over.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Ice cannot ride the snow line, and the first draft did — a rule that could never
    /// fire.</b> The snow line sits at about 0.26 while the temperature field's own floor is about
    /// 0.31: snow only ever exists by the ALTITUDE term, and at sea level that term is zero, so
    /// "freeze where snow lies" froze nothing on any seed. The audit's census said "never
    /// generated" and the equivalence check counted 0 of 1,963. Ice takes its own line, inside
    /// the field's range on purpose: the coldest fringe of coasts freezes, most water never does.
    /// </remarks>
    public const float IceLine = 0.36f;

    /// <summary>Whether water at this column freezes — its own line, not the snow's (see it).</summary>
    /// <remarks>
    /// Public so the audit can ask the same question the generator answered, column for column,
    /// rather than restating the formula beside it.
    /// </remarks>
    public bool FrozenSurface(int x, int z) => _climate.Temperature(x, z) < IceLine;

    /// <summary>Warmth above which, and rainfall below which, sand counts as the arid fringe.</summary>
    /// <remarks>
    /// Both inside their fields' real range (each runs about 0.31 to 0.69), the ice line's own
    /// discipline: the fringe is a fringe, most beaches grow nothing, and a soaking or cool seed
    /// legitimately has none at all.
    /// </remarks>
    public const float HotLine = 0.55f;

    public const float DryLine = 0.44f;

    /// <summary>Whether this column's sand is hot and dry enough to grow the desert kit.</summary>
    /// <remarks>Public for the audit, <see cref="FrozenSurface"/>'s own reason.</remarks>
    public bool AridSurface(int x, int z) =>
        _climate.Temperature(x, z) > HotLine && _climate.Downfall(x, z) < DryLine;

    /// <summary>Rainfall above which a shoreline is wet enough to stand reeds.</summary>
    public const float WetLine = 0.56f;

    /// <summary>Whether this column's shore is soaked enough for reeds. Public for the audit.</summary>
    public bool WetShore(int x, int z) => _climate.Downfall(x, z) > WetLine;

    /// <summary>
    /// The reed rule WHOLE — ground at the waterline or one bank up, sand or grass, soaked, and
    /// one fast roll. The decorator places where this says and the audit counts what this says,
    /// so the two can never drift apart.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>No slow patch field, and that is a measured decision, not an omission.</b> Eligible
    /// ground is a thin shoreline contour — 85 columns in a whole gate sample, lying in one or
    /// two contiguous strips — and a coherent field goes negative over an entire strip at once:
    /// the staged probe read 82 of 85 columns dying at the patch gate on a correct seed. The
    /// meadow's two-field discipline exists to cut PATCHES out of broad ground; a shoreline is
    /// already a strip, so the per-column roll alone gives stands with gaps in them, which is
    /// what a reedy shore is.
    /// </remarks>
    public bool ReedSite(int wx, int wz)
    {
        var surface = SurfaceHeight(wx, wz);
        if (surface < SeaLevel || surface > SeaLevel + 1) return false;

        var top = TopOf(wx, wz, surface, surface <= SeaLevel + 2);
        if (top != _ids.Sand.Value && top != _ids.Grass.Value) return false;

        return WetShore(wx, wz) && Noise.Value2(wx, wz, _seedReed + 7) < 0.5f;
    }

    /// <summary>
    /// How far short of the snow line a column may sit and still carry a dusting of it.
    /// </summary>
    /// <remarks>
    /// The band exists because a snow line drawn as a line looks drawn. Ground just too warm to
    /// hold snow keeps its grass and wears a layer over it, so the snowfield has an edge that fades
    /// rather than a contour you could trace. Sized against the field it is measured in, not in
    /// blocks: temperature runs about 0.31 to 0.69 across a world, so a band of 0.03 is a fringe
    /// rather than a second biome.
    /// </remarks>
    private const float SnowDusting = 0.03f;

    /// <summary>
    /// Warmth below which snow lies rather than grass.
    /// </summary>
    /// <remarks>
    /// Measured, not guessed, and it moves fast: 0.30 buried a third of all open ground, 0.22 left
    /// it on nothing but the highest peaks. Rate goes as roughly exp(25 * line), which is what a
    /// two-point measurement converged on. Banded in the audit against grass rather than against the
    /// whole volume, since that is the comparison a player actually makes.
    /// </remarks>
    private const float SnowLine = 0.33f;

    /// <summary>Shore material: sand, with clay in patches where the water is shallow.</summary>
    /// <remarks>
    /// The threshold looks generous next to an ore's and is not: this only ever runs on the top
    /// three blocks of a shore column, so the field is being sampled across a sheet rather than
    /// through a volume. At an ore-like 0.42 the whole world held under a thousand clay.
    /// </remarks>
    private ushort SoftBeach(int x, int y, int z) =>
        Noise.Fbm3(x / 11f, y / 5f, z / 11f, _seedClay, 2) > 0.30f ? _ids.Clay : _ids.Sand;

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

        ScatterCaveFlora(chunk, ox, oy, oz);

        // ⛳ Nothing ABOVE ground decorates the deep, and it is worth the two lines. SurfaceHeight is
        // clamped to at least 1 and the longest vine hangs five below a trunk's base, so no tree, tuft
        // or flower can reach a cell below y −5. Six of the world's ten chunk layers are under that
        // line and each of them used to pay a full search over eighty-one tree cells to find nothing.
        // The cave flora above runs first exactly because the deep is where IT decorates.
        if (oy + Chunk.Size <= -5) return;

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

        ScatterGroundCover(chunk, ox, oy, oz);
    }

    /// <summary>Grows the underground's own flora on cave floors, at any depth.</summary>
    /// <remarks>
    /// <para>⛳ <b>Reads and writes only this chunk's own cells.</b> A mushroom is one cell, so
    /// unlike a tree nothing about it can straddle a seam — and the one thing it needs beyond its
    /// own cell, the floor under it, is required to be inside the chunk too, which is why local y
    /// starts at 1. A floor on the chunk's bottom row is skipped by both neighbours rather than
    /// guessed at by either; a scattering does not miss the row.</para>
    /// <para>⚠ <b>Two slow fields and a fast roll, the meadows' own shape.</b> A pocket field says
    /// where the underground blooms at all, so caves are mostly bare and then suddenly not; the
    /// roll picks the cells; and WHICH mushroom is its own field, so a pocket is brown or red the
    /// way a patch is carrots or potatoes, never a salad.</para>
    /// <para>⚠ Depth is a heightmap comparison, not a light read — lighting does not exist at
    /// generation time. Five below the surface is the line the cave ambience already treats as
    /// underground, and a chunk in the sky pays one heightmap read per column and nothing else.</para>
    /// </remarks>
    private void ScatterCaveFlora(Chunk chunk, int ox, int oy, int oz)
    {
        for (var z = 0; z < Chunk.Size; z++)
        for (var x = 0; x < Chunk.Size; x++)
        {
            var wx = ox + x;
            var wz = oz + z;

            var ceiling = SurfaceHeight(wx, wz) - 5;
            if (oy + 1 >= ceiling) continue;

            var top = Math.Min(Chunk.Size, ceiling - oy);
            for (var y = 1; y < top; y++)
            {
                if (!chunk.Get(x, y, z).IsAir) continue;

                var floor = chunk.Get(x, y - 1, z);
                if (floor != _ids.Stone && floor != _ids.Deepstone) continue;

                var wy = oy + y;

                // ⛳ Moss first, and it DRESSES THE FLOOR rather than standing on it: wet climate
                // seeping into shallow stone turns the cell UNDER this air green. Its own seed,
                // its own pockets, only above the moss floor — the glowcap's depth logic run the
                // other way up — and the floor cell is in-chunk by the loop's own y >= 1.
                // ⚠ wy − 1, because the RULE is about where the moss lies and the moss dresses
                // the floor below this air cell — gated on the air, the lowest moss lands exactly
                // AT the floor line, and a check on its own boundary agrees with anything.
                if (wy - 1 > MossFloor && WetShore(wx, wz)
                    && Noise.Fbm3(wx / 20f, wy / 20f, wz / 20f, _seedMoss, 2) > 0.26f
                    && Noise.Value3(wx, wy, wz, _seedMoss + 7) < 0.35f)
                {
                    chunk.Set(x, y - 1, z, _ids.Moss);
                    continue;
                }

                // Webs before mushrooms: a webbed pocket is somebody's lair, not a garden. Its
                // field is tighter and rarer than the mushrooms', and denser inside — walking
                // into one should read as a place, not a sprinkle.
                if (Noise.Fbm3(wx / 22f, wy / 22f, wz / 22f, _seedFlora + 29, 2) > 0.34f)
                {
                    if (Noise.Value3(wx, wy, wz, _seedFlora + 31) < 0.03f)
                        chunk.Set(x, y, z, _ids.Cobweb);
                    continue;
                }

                // ⛳ The glowcap, the deep's own light: pockets on their OWN derived seed so the
                // mushroom fields keep every roll they had, and only below the glow floor — the
                // one flora with a depth to it, because a light that grows everywhere is a light
                // that means nothing. Asked before the mushrooms so a glowcap pocket is a glowcap
                // pocket rather than a mushroom pocket with intruders.
                if (wy < GlowFloor
                    && Noise.Fbm3(wx / 24f, wy / 24f, wz / 24f, _seedGlowcap, 2) > 0.30f)
                {
                    if (Noise.Value3(wx, wy, wz, _seedGlowcap + 7) < 0.02f)
                        chunk.Set(x, y, z, _ids.Glowcap);
                    continue;
                }

                if (Noise.Fbm3(wx / 28f, wy / 28f, wz / 28f, _seedFlora, 2) < 0.24f) continue;
                if (Noise.Value3(wx, wy, wz, _seedFlora + 7) > 0.012f) continue;

                var red = Noise.Fbm3(wx / 64f, wy / 64f, wz / 64f, _seedFlora + 13, 2) > 0f;
                chunk.Set(x, y, z, red ? _ids.MushroomRed : _ids.MushroomBrown);
            }
        }
    }

    /// <summary>Puts something on top of every open column that has earned it.</summary>
    /// <remarks>
    /// <para>Runs after the trees, and only into air, so a trunk standing on a grass column keeps
    /// the cell it is already in. That ordering is what makes the result chunk-pure: trees are
    /// decided from the heightmap and land identically however many neighbours exist, so what is
    /// left as air is identical too.</para>
    /// <para>Two fields rather than one for the tufts. A single per-column roll gives an even wash
    /// of grass over every meadow in the world, which reads as noise; a slow field deciding
    /// <em>where</em> grass grows and a fast one deciding <em>which</em> columns gives patches with
    /// bare ground between them, which reads as a meadow.</para>
    /// <para>One cell, one thing: snow first, then flowers, then grass. Anything else needs the
    /// order written down somewhere, and a column that grew a flower and then had it overwritten by
    /// a tuft would be invisible in every count.</para>
    /// </remarks>
    private void ScatterGroundCover(Chunk chunk, int ox, int oy, int oz)
    {
        for (var z = 0; z < Chunk.Size; z++)
        for (var x = 0; x < Chunk.Size; x++)
        {
            var wx = ox + x;
            var wz = oz + z;

            var surface = SurfaceHeight(wx, wz);
            var beach = surface <= SeaLevel + 2;
            var top = TopOf(wx, wz, surface, beach);

            // ⛳ Seagrass first (#96): the flooded floor's own meadow, anywhere the sea stands at
            // least two deep over the sand. A slow patch field decides WHERE beds grow and a fast
            // per-column roll which columns stand a blade — the meadow discipline, and the sea
            // floor is broad enough that the reed lesson about patch fields on strips does not
            // bite. The blade replaces a WATER cell, which its own registration hands straight
            // back: seagrass is waterlogged, so the sea it stands in never has a hole in it.
            if (surface + 2 <= SeaLevel)
            {
                if (Noise.Fbm2(wx / 40f, wz / 40f, _seedSeagrass, 2) > 0.10f
                    && Noise.Value2(wx, wz, _seedSeagrass + 7) < 0.22f)
                    PlaceIntoWater(chunk, ox, oy, oz, wx, surface + 1, wz, _ids.Seagrass);

                continue;   // the drowned floor grows nothing else
            }

            // ⛳ The marsh reed first: ground sitting exactly at the waterline, soaked climate,
            // sand or grass underfoot — two or three joints of cane on the reeds' own derived
            // seed, every chunk in the stack keeping its own share of cells (a reed straddles
            // vertical seams exactly as a cactus does). Before the sand branch, or a sea-level
            // sand column would fall into the dunes' continue and never stand one.
            // ⚠ At the waterline or one bank above it — the exact-contour first draft found ZERO
            // eligible columns across a whole gate sample, which the reed census said out loud.
            if (ReedSite(wx, wz))
            {
                var joints = Noise.Value2(wx, wz, _seedReed + 11) < 0.35f ? 3 : 2;
                for (var up = 1; up <= joints; up++)
                    PlaceIntoAir(chunk, ox, oy, oz, wx, surface + up, wz, _ids.MarshReed);

                continue;
            }

            // ⛳ The arid fringe: hot dry sand grows the desert kit, on the dunes' OWN derived
            // seed so every meadow field keeps the very rolls it had. The column is considered
            // by every chunk in its stack and each keeps its own share of cells — the trees'
            // purity strategy, because a three-tall cactus can straddle a vertical seam — which
            // is why this runs before the single-cell height gate below.
            if (top == _ids.Sand.Value)
            {
                if (surface > SeaLevel && AridSurface(wx, wz)
                    && Noise.Fbm2(wx / 56f, wz / 56f, _seedDune, 2) > 0.02f)
                {
                    var dune = Noise.Value2(wx, wz, _seedDune + 7);

                    if (dune < 0.02f)
                    {
                        // One, two, or rarely three high — a tall one is a landmark, not a lawn.
                        var tall = dune < 0.004f ? 3 : dune < 0.011f ? 2 : 1;
                        for (var up = 1; up <= tall; up++)
                            PlaceIntoAir(chunk, ox, oy, oz, wx, surface + up, wz, _ids.Cactus);
                    }
                    else if (dune < 0.05f)
                    {
                        PlaceIntoAir(chunk, ox, oy, oz, wx, surface + 1, wz, _ids.DeadBush);
                    }
                }

                continue;   // sand grows nothing else
            }

            var y = surface + 1 - oy;
            if ((uint)y >= Chunk.Size) continue;

            if (top != _ids.Grass.Value) continue;

            // Cold enough to hold a dusting but not to hold a snowfield. Nothing grows through it.
            if (SnowDepth(wx, wz, surface) > -SnowDusting)
            {
                PlaceIntoAir(chunk, ox, oy, oz, wx, surface + 1, wz, _ids.SnowLayer);
                continue;
            }

            if (Noise.Fbm2(wx / 44f, wz / 44f, _seedMeadow, 2) < -0.04f) continue;

            var roll = Noise.Value2(wx, wz, _seedMeadow + 11);
            if (roll > 0.44f) continue;

            // ⛳ WILD CROPS, and they are the whole entry to growing anything but wheat. Nothing
            // drops a carrot and nothing crafts one, so without this the three root crops are
            // registered, drawn, edible and unreachable — which the reachability walk says out loud.
            //
            // ⚠ Rarer than the flowers by a factor of five, off the SAME roll for the same reason
            // the flowers are: a patch belongs to the meadow it is in rather than being a second
            // scattering that sometimes agrees with the first. Which crop is its own slow field, so
            // a patch is carrots or potatoes and never a salad.
            if (roll < 0.006f)
            {
                // ⛳ A slice off the bottom of the same roll for the WILD FINDS — the berry bush
                // and the pumpkin — so they are exactly as much members of their meadow as a crop
                // patch is, and every crop cell above the slice keeps the very kind it had before
                // either existed: the derived-seed discipline for a world already being played in.
                // Which find is a slow field against zero, which gives exactly the two wanted.
                // The bush is ripe, for the wild crops' reason: finding one is the entry to keeping
                // one; a pumpkin is whole because carving it is the player's own act.
                if (roll < 0.0018f)
                {
                    var find = Noise.Fbm2(wx / 96f, wz / 96f, _seedMeadow + 41, 2) < 0f
                        ? _ids.BerryBushRipe
                        : _ids.Pumpkin;

                    PlaceIntoAir(chunk, ox, oy, oz, wx, surface + 1, wz, find);
                    continue;
                }

                var wild = _ids.WildCrops;
                var pick = Noise.Fbm2(wx / 128f, wz / 128f, _seedMeadow + 37, 2);

                // Three bands of a field that runs about −0.4 to 0.4, same quantisation as the
                // flowers below and for the same reason: comparing against zero only ever gives two.
                var crop = pick < -0.09f ? wild[0] : pick < 0.09f ? wild[1] : wild[2];

                PlaceIntoAir(chunk, ox, oy, oz, wx, surface + 1, wz, crop);
                continue;
            }

            // Flowers come out of the same roll as the grass rather than a second one, so a meadow
            // is a meadow with flowers in it instead of two unrelated scatterings that sometimes
            // agree. Which flower is a slow field of its own, so a patch is one kind or the other.
            if (roll < 0.03f)
            {
                // ⚠ Which flower is a SLOW field, so a patch is one kind rather than a scattering of
                // four — and it is quantised into bands rather than compared against zero, because
                // "below zero or not" only ever divides a field into two however many kinds there
                // are. Four bands of a field that runs about −0.4 to 0.4, which is where fBm
                // normalised by its octave sum actually lives.
                var which = Noise.Fbm2(wx / 96f, wz / 96f, _seedMeadow + 23, 2);
                var kind = which switch
                {
                    < -0.12f => _ids.Seaflax,
                    < 0f => _ids.Emberbloom,
                    < 0.12f => _ids.Sunwort,
                    _ => _ids.Marshlily,
                };

                PlaceIntoAir(chunk, ox, oy, oz, wx, surface + 1, wz, kind);
                continue;
            }

            PlaceIntoAir(chunk, ox, oy, oz, wx, surface + 1, wz, _ids.Meadowgrass);
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
        int Seed,
        bool Cherry)
    {
        /// <summary>Y of the topmost log.</summary>
        public int TopY => BaseY + TrunkHeight - 1;

        /// <summary>The species' own wood and canopy, resolved where the writes happen.</summary>
        public BlockId LogOf(StarterBlocks.Ids ids) => Cherry ? ids.CherryLog : ids.Log;

        public BlockId LeavesOf(StarterBlocks.Ids ids) => Cherry ? ids.CherryLeaves : ids.Leaves;
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

        // ⛳ THE GROVES (#94): a slow field on its own derived seed decides which STANDS are
        // cherry — a split of the existing tree roll, the bush-and-pumpkin discipline, so no
        // tree moves and no shipped field re-bands. Mild ground only: the blossom stays off the
        // arid fringe and clear of the snow country.
        // ⚠ The threshold is calibrated against fBm's REAL spread (typically ±0.2, the P0
        // lesson), not its nominal ±1 — 0.22 here was the extreme tail and grew nothing on any
        // seed at the gate's own sample.
        var cherry = Noise.Fbm2(x / 240f, z / 240f, _seedCherry, 2) > 0.10f
                     && _climate.Temperature(x, z) is > 0.38f and < HotLine;

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
            Seed: Noise.Hash2(cellX, cellZ, _seedTree),
            Cherry: cherry);

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
            Place(chunk, ox, oy, oz, tree.X, tree.BaseY + i, tree.Z, tree.LogOf(_ids));

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
            PlaceIntoAir(chunk, ox, oy, oz, tree.X + dx, tree.BaseY, tree.Z + dz, tree.LogOf(_ids));
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
                PlaceIntoAir(chunk, ox, oy, oz, bx, by, bz, tree.LogOf(_ids));
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

                PlaceLeaf(chunk, ox, oy, oz, tree, lx, y, lz);

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
        // No vines through the blossom: green strands on pink read as damage, not dressing.
        if (tree.Cherry) return;

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
    /// Writes a waterlogged plant only over the water it lives in, so a cave mouth or an iced lid
    /// under the sea never grows a blade in its air pocket.
    /// </summary>
    private void PlaceIntoWater(Chunk chunk, int ox, int oy, int oz, int wx, int wy, int wz, BlockId id)
    {
        var lx = wx - ox;
        var ly = wy - oy;
        var lz = wz - oz;
        if ((uint)lx >= Chunk.Size || (uint)ly >= Chunk.Size || (uint)lz >= Chunk.Size) return;
        if (chunk.Get(lx, ly, lz).Value != _ids.Water.Value) return;
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
    private void PlaceLeaf(Chunk chunk, int ox, int oy, int oz, in TreeSpec tree, int wx, int wy, int wz)
    {
        var lx = wx - ox;
        var ly = wy - oy;
        var lz = wz - oz;
        if ((uint)lx >= Chunk.Size || (uint)ly >= Chunk.Size || (uint)lz >= Chunk.Size) return;
        if (!chunk.Get(lx, ly, lz).IsAir) return;
        chunk.Set(lx, ly, lz, tree.LeavesOf(_ids));
    }
}
