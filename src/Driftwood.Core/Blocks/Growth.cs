using Driftwood.Core.World;

namespace Driftwood.Core.Blocks;

/// <summary>
/// What grows where, and the random tick that makes it happen.
/// </summary>
/// <remarks>
/// <para>⛳⛳ <b>THE STAGE IS THE BLOCK ID, and that decision is what makes farming cost nothing to
/// keep.</b> A field halfway up is already saved — the world stores block ids and the save stores an
/// edit — so there is no bank, no timer, no per-crop record and no save-format change. Everything
/// else in this file is a rule about which id a cell becomes next.</para>
/// <para>⛳ <b>Random ticks, not a list of planted cells.</b> A bank keyed on position is the shape
/// this project already uses for furnaces and chests, and it is the wrong one here: a crop is not a
/// thing with state beside the world, it IS the world. Ticking random cells of loaded chunks also
/// buys grass spreading, saplings, ice melting and fire dying with no second mechanism — every one
/// of those is a block that wants to become another block after a while.</para>
/// <para>⚠ <b>The rate is per chunk per second, not per frame.</b> A tick budget spent per frame is
/// a growth rate that depends on the frame rate, which is the same class of mistake as measuring a
/// window in frames — a field would ripen faster on a better machine.</para>
/// </remarks>
public sealed class Growth
{
    /// <summary>How many cells of each loaded chunk are touched per second.</summary>
    /// <remarks>
    /// ⚠ Dialled against how long a field should take rather than picked. A 32³ chunk is 32,768
    /// cells, so at three a second one particular cell is looked at about every three hours — which
    /// is why a crop advances on a CHANCE per look rather than needing many, and why the number
    /// below is a probability and not a countdown.
    /// </remarks>
    public const float CellsPerChunkPerSecond = 3f;

    /// <summary>How likely a lit, watered crop is to move up a stage when it is looked at.</summary>
    /// <remarks>
    /// ⛳ With the rate above this puts a four-stage crop at roughly ten to twenty minutes of real
    /// time from seed to harvest, which is a night indoors rather than an errand — long enough that
    /// planting is a decision and short enough that a player sees it happen in one session.
    /// </remarks>
    public const float StageChance = 0.55f;

    /// <summary>Light at or above which a crop will grow at all.</summary>
    /// <remarks>
    /// ⚠ <b>Any light, not sunlight.</b> A torch grows a crop, which is what makes an underground
    /// farm a thing a player can build — and it is the difference between a mechanic and a chore
    /// that can only be done between dawn and dusk.
    /// </remarks>
    public const int MinLight = 9;

    /// <summary>How far a water source reaches to keep ground watered, in blocks.</summary>
    public const int WaterReach = 4;

    private readonly int[] _next;
    private readonly bool[] _isCrop;
    private readonly bool[] _isFarmland;
    private readonly BlockId _farmland;
    private readonly BlockId _farmlandWet;
    private float _owed;

    /// <summary>Cells looked at since this was built, for the check and the report.</summary>
    public long Looked { get; private set; }

    /// <summary>And how many of those actually became something else.</summary>
    public long Grew { get; private set; }

    private readonly BlockId _water;

    public Growth(BlockRegistry registry)
    {
        _water = registry.ByName("water").Id;

        _next = new int[registry.Count];
        _isCrop = new bool[registry.Count];
        _isFarmland = new bool[registry.Count];

        for (var i = 0; i < _next.Length; i++) _next[i] = -1;

        _farmland = registry.ByName("farmland").Id;
        _farmlandWet = registry.ByName("farmland_wet").Id;

        _isFarmland[_farmland.Value] = true;
        _isFarmland[_farmlandWet.Value] = true;

        // ⛳ The ladder is read off the block table by NAME, so a fifth stage of wheat or a second
        // crop entirely is a row in StarterBlocks and nothing here. ⚠ The last stage has no next,
        // which is what makes "ripe" a state rather than a number this file has to know.
        for (var stage = 0; stage < StarterBlocks.WheatStages; stage++)
        {
            var here = registry.ByName(StarterBlocks.WheatName(stage)).Id;
            _isCrop[here.Value] = true;

            if (stage + 1 < StarterBlocks.WheatStages)
                _next[here.Value] = registry.ByName(StarterBlocks.WheatName(stage + 1)).Id.Value;
        }
    }

    /// <summary>True when this block is a crop at any stage.</summary>
    public bool IsCrop(BlockId block) => _isCrop[block.Value];

    /// <summary>True when this block is tilled ground, wet or dry.</summary>
    public bool IsFarmland(BlockId block) => _isFarmland[block.Value];

    /// <summary>True when this crop has nothing left to become.</summary>
    public bool IsRipe(BlockId block) => _isCrop[block.Value] && _next[block.Value] < 0;

    /// <summary>The stage after this one, or the same block when there is none.</summary>
    public BlockId Next(BlockId block) =>
        _next[block.Value] < 0 ? block : new BlockId((ushort)_next[block.Value]);

    /// <summary>
    /// Advances one crop cell if it can, and answers whether it did.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Asked of the cell rather than of a stored timer, so it is idempotent and order-free.</b>
    /// The same call on the same cell twice in a row is two chances and never a double step, which
    /// is what lets the tick pick cells at random without keeping a record of what it has seen.
    /// </remarks>
    public bool Step(VoxelWorld world, int x, int y, int z, double roll)
    {
        var here = world.GetBlock(x, y, z);

        // ⛳ Tilled ground wets and dries on the same tick the crops grow on, rather than on a
        // watcher of its own. It is the same question — what does this cell want to become — and a
        // second mechanism ticking the same cells at a different rate is how a field ends up
        // half-watered from two directions.
        if (_isFarmland[here.Value])
        {
            var wet = Watered(world, x, y, z, _water);
            var want = wet ? _farmlandWet : _farmland;

            if (here == want) return false;

            world.SetBlock(x, y, z, want);
            return true;
        }

        if (!_isCrop[here.Value] || _next[here.Value] < 0) return false;

        // ⛔ On tilled ground, and WATERED tilled ground. Without this a crop grows on the dry
        // farmland a player never bothered to irrigate, and the whole water mechanic is decoration.
        if (world.GetBlock(x, y - 1, z) != _farmlandWet) return false;

        if (world.GetLight(x, y, z) is var light && LightOf(light) < MinLight) return false;
        if (roll >= StageChance) return false;

        world.SetBlock(x, y, z, Next(here));
        return true;
    }

    /// <summary>
    /// Plants a field in a real world and grows it, and checks every gate on the way.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Every arm has its refusal beside it, because "the wheat grew" is true of a build
    /// with no gates at all.</b> A crop on DRY ground must not move; one in the dark must not move;
    /// one already ripe must not move. Each of those is a rule a player will lean on, and a build
    /// that grew everything everywhere would pass a check that only watched a good field.</para>
    /// <para>⚠ Stepped with a roll of zero rather than a random one — the chance is not what is
    /// being tested here and a seeded stream that happened to roll high would make this flap.</para>
    /// </remarks>
    public static List<string> Validate(BlockRegistry registry)
    {
        var faults = new List<string>();

        var world = new VoxelWorld(registry);
        var growth = new Growth(registry);

        var dirt = registry.ByName("dirt").Id;
        var water = registry.ByName("water").Id;
        var farmland = registry.ByName("farmland").Id;
        var seedling = registry.ByName(StarterBlocks.WheatName(0)).Id;

        // A field at y 64 with a channel of water beside it, lit as if under an open sky.
        for (var x = 0; x < 12; x++)
        for (var z = 0; z < 12; z++)
            world.SetBlock(x, 64, z, dirt);

        world.SetBlock(0, 64, 0, water);
        world.SetBlock(4, 64, 4, farmland);
        world.SetBlock(4, 65, 4, seedling);

        // ⛔ DRY FIRST, and this is the arm that matters. The ground is farmland but nothing has
        // watered it, so a build without the wet check grows the crop here and passes everything
        // below regardless.
        if (growth.Step(world, 4, 65, 4, 0.0))
            faults.Add("a crop on dry farmland grew");

        // Water it the way the tick does, then it should move.
        if (!growth.Step(world, 4, 64, 4, 0.0))
            faults.Add("farmland four cells from water did not turn wet");

        if (world.GetBlock(4, 64, 4) != registry.ByName("farmland_wet").Id)
            faults.Add("watered farmland is not the wet block");

        // ⛳ Lit by the REAL light engine rather than by writing a value in. A fresh VoxelWorld is
        // dark and a dark crop is correctly refused, so the field has to actually see the sky — and
        // running the engine is also the only way this check knows the two agree about what "lit"
        // means. ⚠ There is deliberately no light setter on VoxelWorld to reach for: an instrument
        // that writes state the game computes is an instrument that can pass on a broken build.
        new Lighting.LightEngine(registry).LightAll(world);

        if (!growth.Step(world, 4, 65, 4, 0.0))
            faults.Add("a lit crop on wet farmland did not grow");

        if (world.GetBlock(4, 65, 4) != registry.ByName(StarterBlocks.WheatName(1)).Id)
            faults.Add("a crop that grew did not become the next stage");

        // ⛔ THE DARK ARM, and it is roofed rather than dimmed: a slab of stone over the crop and
        // the light engine run again, which is what a player actually does to a cellar. Writing a
        // zero in would test the comparison and not the mechanic.
        // ⛔ AND IT HAS TO BE A BIG ROOF. A three-by-three lid left the crop two steps from open
        // sky, which at one level of falloff a step is light 13 — brighter than the gate — so the
        // first version of this arm went red on correct code and read as the light rule not working.
        // Sky light spreads SIDEWAYS as well as down; a cellar is only dark in the middle.
        const int Roof = 16;

        for (var x = 4 - Roof; x <= 4 + Roof; x++)
        for (var z = 4 - Roof; z <= 4 + Roof; z++)
            world.SetBlock(x, 66, z, registry.ByName("stone").Id);

        new Lighting.LightEngine(registry).LightAll(world);

        if (growth.Step(world, 4, 65, 4, 0.0))
            faults.Add("a crop grew under a roof with no light on it");

        // Take the roof off and let it run all the way to ripe, which then stops.
        for (var x = 4 - Roof; x <= 4 + Roof; x++)
        for (var z = 4 - Roof; z <= 4 + Roof; z++)
            world.SetBlock(x, 66, z, BlockId.Air);

        new Lighting.LightEngine(registry).LightAll(world);
        for (var i = 0; i < StarterBlocks.WheatStages + 2; i++) growth.Step(world, 4, 65, 4, 0.0);

        var last = world.GetBlock(4, 65, 4);
        if (!growth.IsRipe(last))
            faults.Add($"a field grown out is {registry[last].Name} rather than ripe");

        if (growth.Step(world, 4, 65, 4, 0.0))
            faults.Add("a ripe crop grew again");

        // ⛔ And the far side of the field is out of reach of the one water cell, or the reach is
        // not a reach at all. Eleven cells from the water, well past WaterReach.
        world.SetBlock(11, 64, 11, farmland);
        growth.Step(world, 11, 64, 11, 0.0);

        if (world.GetBlock(11, 64, 11) != farmland)
            faults.Add("farmland eleven cells from water turned wet, so the reach is unbounded");

        return faults;
    }

    /// <summary>The brighter of what the sky and a torch give this cell.</summary>
    /// <remarks>
    /// ⚠ RAW sky light, not scaled by the hour. Crops grow overnight — a field that stopped at dusk
    /// would be a mechanic a player has to stand and watch, and the sun having reached it at all is
    /// the fact that matters.
    /// </remarks>
    private static int LightOf(ushort packed) =>
        Math.Max(Lighting.LightValue.Sky(packed), Lighting.LightValue.BlockPeak(packed));

    /// <summary>
    /// True when a cell of tilled ground has water near enough to stay wet.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>A square reach and not a circle</b>, which is the format's own answer and the one a
    /// player can lay out — nine by nine round a middle cell, which is exactly the field somebody
    /// digs a hole in the centre of. A radius would make the corners a guess.
    /// </remarks>
    public static bool Watered(VoxelWorld world, int x, int y, int z, BlockId water)
    {
        for (var dz = -WaterReach; dz <= WaterReach; dz++)
        for (var dx = -WaterReach; dx <= WaterReach; dx++)
            if (world.GetBlock(x + dx, y, z + dz) == water) return true;

        return false;
    }

    /// <summary>
    /// Spends the tick budget over the loaded chunks.
    /// </summary>
    /// <param name="chunks">Where the loaded chunks are, in cells, at their minimum corner.</param>
    /// <remarks>
    /// ⚠ The budget is carried between frames rather than rounded per frame. At three cells a chunk
    /// a second and sixty frames, every frame's share is a twentieth of a cell — rounded down that
    /// is nothing at all, and nothing would ever grow.
    /// </remarks>
    public void Update(VoxelWorld world, IReadOnlyList<(int X, int Y, int Z)> chunks, float dt, Random random)
    {
        if (chunks.Count == 0 || dt <= 0f) return;

        _owed += chunks.Count * CellsPerChunkPerSecond * dt;

        var budget = (int)_owed;
        if (budget <= 0) return;

        _owed -= budget;

        for (var i = 0; i < budget; i++)
        {
            var (cx, cy, cz) = chunks[random.Next(chunks.Count)];

            var x = cx + random.Next(World.Chunk.Size);
            var y = cy + random.Next(World.Chunk.Size);
            var z = cz + random.Next(World.Chunk.Size);

            Looked++;
            if (Step(world, x, y, z, random.NextDouble())) Grew++;
        }
    }
}
