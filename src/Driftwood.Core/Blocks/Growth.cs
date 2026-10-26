using Driftwood.Core.Gen;
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
/// <para>⛳ <b>The tree's renewal rides the same tick (2026-08-22):</b> a sapling is a block that
/// wants to become a whole tree, and a leaf whose tree is gone is a block that wants to become air
/// and pay its drops on the way. Neither needed a second mechanism; both needed the tick to be
/// honest about its rate — see <see cref="CellsPerChunkPerSecond"/>.</para>
/// </remarks>
public sealed class Growth
{
    /// <summary>How many cells of each loaded chunk are touched per second.</summary>
    /// <remarks>
    /// <para>⛔ <b>This was 3 until 2026-08-22, and its own note promised "ten to twenty minutes" for
    /// a field.</b> A 32³ chunk is 32,768 cells, so at three a second one particular cell was looked
    /// at about every three HOURS, and a four-stage crop at a 0.55 chance a look took the better
    /// part of a day — the note and the number disagreed by a hundred, and nobody had timed a field
    /// because bone meal was always to hand. The saplings are what made it matter: a tree that
    /// takes a day to come up is a tree nobody ever sees come up.</para>
    /// <para>⛳ Measured against the genre rather than picked: the reference ticks three cells per
    /// sixteen-cube section per game tick, which is 480 a second for a chunk this size — one look
    /// per cell every 68 seconds. This is a third of that, one look every ~205 seconds, and the
    /// chances below are dialled to it: a field in 15–20 minutes, a sapling in ten, a felled
    /// canopy gone in a few. The cost is a few thousand cell reads a frame over every meshed chunk,
    /// which the tick report measures.</para>
    /// </remarks>
    public const float CellsPerChunkPerSecond = 160f;

    /// <summary>How likely a lit, watered crop is to move up a stage when it is looked at.</summary>
    /// <remarks>
    /// ⛳ With the rate above this puts a four-stage crop at roughly fifteen to twenty minutes of
    /// real time from seed to harvest, which is a night indoors rather than an errand — long enough
    /// that planting is a decision and short enough that a player sees it happen in one session.
    /// </remarks>
    public const float StageChance = 0.55f;

    /// <summary>How likely a lit sapling is to become a tree when it is looked at.</summary>
    /// <remarks>
    /// One look in three, at one look every ~205 seconds: a tree in about ten minutes on average,
    /// which is a little quicker than the genre's sixteen and slow enough that bone meal still
    /// means something.
    /// </remarks>
    public const float SaplingChance = 0.35f;

    /// <summary>How likely a helping of bone meal is to grow a sapling into its tree outright.</summary>
    public const float BonemealSaplingChance = 0.45f;

    /// <summary>How far, walking only through leaves, a leaf may be from a log before it decays.</summary>
    /// <remarks>
    /// Six, the genre's own reach: the widest crown the generator grows is radius four off a branch
    /// that is itself a log, so every leaf of a standing tree is within it, and the first leaf of a
    /// felled one is outside it the moment the last log goes.
    /// </remarks>
    public const int LeafReach = 6;

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
    private readonly bool[] _onFarmland;
    private readonly bool[] _isFarmland;
    private readonly bool[] _isSapling;
    private readonly bool[] _saplingIsCherry;
    private readonly bool[] _naturalLeaves;
    private readonly bool[] _anyLeaves;
    /// <summary>For a crop of one ground only, that ground's id; −1 for every other block.</summary>
    private readonly int[] _ground;

    private readonly bool[] _isLog;
    private readonly BlockId _farmland;
    private readonly BlockId _farmlandWet;
    private readonly BlockId _grass;
    private readonly BlockId _dirt;
    private float _owed;

    /// <summary>Cells looked at since this was built, for the check and the report.</summary>
    public long Looked { get; private set; }

    /// <summary>And how many of those actually became something else.</summary>
    public long Grew { get; private set; }

    /// <summary>How many saplings became trees.</summary>
    public long TreesGrown { get; private set; }

    /// <summary>How many leaves decayed for want of a tree.</summary>
    public long Decayed { get; private set; }

    /// <summary>
    /// The door every write goes through. Null writes the world directly, which is right for a
    /// headless check; the game sets the streamer's edit path so light and mesh follow a leaf
    /// falling or a canopy appearing.
    /// </summary>
    public Action<int, int, int, BlockId>? Writer { get; set; }

    /// <summary>Who grows the trees. Null and a sapling simply waits.</summary>
    public TerrainGenerator? Trees { get; set; }

    /// <summary>
    /// The leaves that decayed since this was last drained, for the caller to pay their drops and
    /// shed their particles — the tick cannot, it has no hands.
    /// </summary>
    public List<(int X, int Y, int Z, BlockId Was)> Fallen { get; } = new();

    private readonly BlockId _water;
    private readonly BlockId _kelp;

    /// <summary>How likely a kelp stalk's top is to put on a joint when it is looked at.</summary>
    /// <remarks>
    /// A tenth of a crop's chance: a cut forest grows back over an hour or two rather than while a
    /// player watches, which is the reference's own slow pace for it.
    /// </remarks>
    public const float KelpChance = 0.14f;

    /// <summary>The tallest a stalk grows on its own.</summary>
    public const int KelpMost = 14;

    public Growth(BlockRegistry registry)
    {
        _water = registry.ByName("water").Id;
        _kelp = registry.ByName("kelp").Id;

        _next = new int[registry.Count];
        _isCrop = new bool[registry.Count];
        _onFarmland = new bool[registry.Count];
        _isFarmland = new bool[registry.Count];
        _isSapling = new bool[registry.Count];
        _saplingIsCherry = new bool[registry.Count];
        _naturalLeaves = new bool[registry.Count];
        _anyLeaves = new bool[registry.Count];
        _ground = new int[registry.Count];
        Array.Fill(_ground, -1);
        _isLog = new bool[registry.Count];

        for (var i = 0; i < _next.Length; i++) _next[i] = -1;

        _farmland = registry.ByName("farmland").Id;
        _farmlandWet = registry.ByName("farmland_wet").Id;
        _grass = registry.ByName("grass").Id;
        _dirt = registry.ByName("dirt").Id;

        _isFarmland[_farmland.Value] = true;
        _isFarmland[_farmlandWet.Value] = true;

        // ⛳ The ladder is read off the block table by NAME, so a fifth stage of wheat or a second
        // crop entirely is a row in StarterBlocks and nothing here. ⚠ The last stage has no next,
        // which is what makes "ripe" a state rather than a number this file has to know.
        //
        // ⛔ That claim used to be FALSE while this loop said WheatName. Adding the root crops is
        // what proved it: the ladders come out of StarterBlocks now, one per crop, and this file
        // has never heard of wheat. A crop with one stage or with nine costs nothing here.
        // The berry bush is the row that made the GROUND part of the ladder too.
        foreach (var ladder in StarterBlocks.CropLadders())
        for (var stage = 0; stage < ladder.Rungs.Length; stage++)
        {
            var here = registry.ByName(ladder.Rungs[stage]).Id;
            _isCrop[here.Value] = true;
            _onFarmland[here.Value] = ladder.OnFarmland;
            _ground[here.Value] = ladder.Ground is { } ground ? registry.ByName(ground).Id.Value : -1;

            if (stage + 1 < ladder.Rungs.Length)
                _next[here.Value] = registry.ByName(ladder.Rungs[stage + 1]).Id.Value;
        }

        // ⛳ The trees, by name as well. A third species is a sapling row here, its leaves in the
        // two lists, its log in the third — and nothing else in this file changes.
        _isSapling[registry.ByName(StarterBlocks.SaplingName).Id.Value] = true;
        _isSapling[registry.ByName(StarterBlocks.CherrySaplingName).Id.Value] = true;
        _saplingIsCherry[registry.ByName(StarterBlocks.CherrySaplingName).Id.Value] = true;

        foreach (var name in (string[])["driftoak_leaves", "cherry_leaves"])
        {
            _naturalLeaves[registry.ByName(name).Id.Value] = true;
            _anyLeaves[registry.ByName(name).Id.Value] = true;
        }

        foreach (var name in (string[])[StarterBlocks.LeavesPlacedName, StarterBlocks.CherryLeavesPlacedName])
            _anyLeaves[registry.ByName(name).Id.Value] = true;

        foreach (var name in (string[])["driftoak_log", "cherry_log"])
            _isLog[registry.ByName(name).Id.Value] = true;
    }

    /// <summary>True when this block is a crop at any stage.</summary>
    public bool IsCrop(BlockId block) => _isCrop[block.Value];

    /// <summary>True when this block is tilled ground, wet or dry.</summary>
    public bool IsFarmland(BlockId block) => _isFarmland[block.Value];

    /// <summary>Whether a crop's first stage may be put in this ground — its own rule, whichever it is.</summary>
    public bool Accepts(BlockId seedling, BlockId ground) =>
        _ground[seedling.Value] >= 0 ? ground.Value == _ground[seedling.Value]
        : _onFarmland[seedling.Value] ? _isFarmland[ground.Value]
        : ground == _grass || ground == _dirt;

    /// <summary>True when this crop has nothing left to become.</summary>
    public bool IsRipe(BlockId block) => _isCrop[block.Value] && _next[block.Value] < 0;

    /// <summary>True when this block is a sapling of any species.</summary>
    public bool IsSapling(BlockId block) => _isSapling[block.Value];

    /// <summary>True when this block is a leaf that grew on a tree — the kind that decays.</summary>
    public bool IsNaturalLeaf(BlockId block) => _naturalLeaves[block.Value];

    /// <summary>The stage after this one, or the same block when there is none.</summary>
    public BlockId Next(BlockId block) =>
        _next[block.Value] < 0 ? block : new BlockId((ushort)_next[block.Value]);

    private void Write(VoxelWorld world, int x, int y, int z, BlockId id)
    {
        if (Writer is { } writer) writer(x, y, z, id);
        else world.SetBlock(x, y, z, id);
    }

    /// <summary>
    /// Advances one cell if it can, and answers whether it did.
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

            Write(world, x, y, z, want);
            return true;
        }

        // ⛳ A leaf with no tree: the walk to the nearest log is the whole rule, and a leaf that
        // finds none comes down — paying what a leaf pays, through Fallen, because the tick has no
        // hands to drop an item with. Every look, not a chance of one: the slow part is the look.
        if (_naturalLeaves[here.Value])
        {
            if (Supported(world, x, y, z)) return false;

            Write(world, x, y, z, BlockId.Air);
            Fallen.Add((x, y, z, here));
            Decayed++;
            return true;
        }

        // ⛳ A sapling wants light and a chance, the crop's own two gates, and then the whole tree
        // at once — there is no half-grown tree to be a stage. Blocked overhead it simply waits.
        if (_isSapling[here.Value])
        {
            if (Trees is null) return false;
            if (LightOf(world.GetLight(x, y, z)) < MinLight) return false;
            if (roll >= SaplingChance) return false;

            return GrowSapling(world, x, y, z, here, roll);
        }

        // ⛳ Kelp (2026-10-26): the top of a stalk puts on a joint while two cells of open water
        // stand over it, so a stalk never breaks the surface, and stops at its own height.
        if (here == _kelp)
        {
            if (roll >= KelpChance) return false;
            if (world.GetBlock(x, y + 1, z) != _water || world.GetBlock(x, y + 2, z) != _water) return false;

            var length = 1;
            while (length < KelpMost && world.GetBlock(x, y - length, z) == _kelp) length++;
            if (length >= KelpMost) return false;

            Write(world, x, y + 1, z, _kelp);
            return true;
        }

        if (!_isCrop[here.Value] || _next[here.Value] < 0) return false;

        // ⛔ On tilled ground, and WATERED tilled ground. Without this a crop grows on the dry
        // farmland a player never bothered to irrigate, and the whole water mechanic is decoration.
        // ⛳ Unless the ladder says otherwise: a berry bush stands on plain grass or dirt and asks
        // only for light. The flag is the ladder's own, and it cuts both ways — a bush on watered
        // farmland is refused too, so the two ground rules never blur into "anything underneath".
        var below = world.GetBlock(x, y - 1, z);

        // ⛳ A crop of one particular ground grows on that and nothing else, needs no light, and is
        // slow — the ashcap in its ashsand, the reference's wart a tenth of its ticks.
        if (_ground[here.Value] >= 0)
        {
            if (below.Value != _ground[here.Value] || roll >= StageChance * 0.5) return false;
            Write(world, x, y, z, Next(here));
            return true;
        }

        if (_onFarmland[here.Value])
        {
            if (below != _farmlandWet) return false;
        }
        else if (below != _grass && below != _dirt)
        {
            return false;
        }

        if (world.GetLight(x, y, z) is var light && LightOf(light) < MinLight) return false;
        if (roll >= StageChance) return false;

        Write(world, x, y, z, Next(here));
        return true;
    }

    /// <summary>
    /// Grows the sapling at a cell into its tree now — bone meal's door — or answers false when the
    /// cell is not a sapling, nobody grows trees, or the trunk's column is blocked.
    /// </summary>
    public bool GrowSapling(VoxelWorld world, int x, int y, int z, double roll)
    {
        var here = world.GetBlock(x, y, z);
        return _isSapling[here.Value] && GrowSapling(world, x, y, z, here, roll);
    }

    private bool GrowSapling(VoxelWorld world, int x, int y, int z, BlockId sapling, double roll)
    {
        if (Trees is not { } trees) return false;

        // The shape comes off the roll and the cell, so two saplings side by side grow two trees
        // and the same sapling looked at twice does not grow the same tree it refused.
        var shape = unchecked((int)(roll * int.MaxValue) ^ (x * 73_856_093) ^ (y * 19_349_663) ^ (z * 83_492_791));
        if (!trees.GrowTree(world, x, y, z, _saplingIsCherry[sapling.Value], shape, (wx, wy, wz, id) => Write(world, wx, wy, wz, id)))
            return false;

        TreesGrown++;
        return true;
    }

    /// <summary>
    /// True when a leaf can reach a log within <see cref="LeafReach"/> steps, walking only through
    /// leaves of any species or placing.
    /// </summary>
    /// <remarks>
    /// A breadth-first walk rather than a box search: a log six cells away across open air is not
    /// this leaf's tree, and a box would count it. The frontier is bounded by the reach, so the
    /// worst case is a few hundred reads for a leaf deep in a dense canopy.
    /// </remarks>
    public bool Supported(VoxelWorld world, int x, int y, int z)
    {
        var seen = new HashSet<(int, int, int)> { (x, y, z) };
        var frontier = new Queue<(int X, int Y, int Z, int Steps)>();
        frontier.Enqueue((x, y, z, 0));

        while (frontier.Count > 0)
        {
            var (cx, cy, cz, steps) = frontier.Dequeue();
            if (steps >= LeafReach) continue;

            foreach (var (dx, dy, dz) in Neighbours)
            {
                var nx = cx + dx;
                var ny = cy + dy;
                var nz = cz + dz;
                if (!seen.Add((nx, ny, nz))) continue;

                var block = world.GetBlock(nx, ny, nz);
                if (_isLog[block.Value]) return true;
                if (_anyLeaves[block.Value]) frontier.Enqueue((nx, ny, nz, steps + 1));
            }
        }

        return false;
    }

    private static readonly (int, int, int)[] Neighbours =
        [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

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

        // ⛳ THE BUSH ARMS — the first ladder that does NOT want farmland. On plain lit grass it
        // must move, with no hoe and no water anywhere near the rule; a pick puts the young one
        // back and the same ladder must regrow it; and it must REFUSE tilled ground, because a
        // bush is not a crop and "anything underneath" is how the two rules would blur.
        var bushYoung = registry.ByName(StarterBlocks.BerryBushName).Id;
        var bushRipe = registry.ByName(StarterBlocks.BerryBushRipeName).Id;
        var grass = registry.ByName("grass").Id;

        world.SetBlock(8, 64, 8, grass);
        world.SetBlock(8, 65, 8, bushYoung);

        if (!growth.Step(world, 8, 65, 8, 0.0))
            faults.Add("a berry bush on plain lit grass did not grow");
        if (world.GetBlock(8, 65, 8) != bushRipe)
            faults.Add("a bush that grew did not become the ripe bush");
        if (growth.Step(world, 8, 65, 8, 0.0))
            faults.Add("a ripe bush grew past ripe");

        // Picked back to young — the ladder is a loop by way of the pick, and this is the regrow.
        world.SetBlock(8, 65, 8, bushYoung);
        if (!growth.Step(world, 8, 65, 8, 0.0))
            faults.Add("a picked bush did not regrow");

        world.SetBlock(2, 64, 2, registry.ByName("farmland_wet").Id);
        world.SetBlock(2, 65, 2, bushYoung);
        if (growth.Step(world, 2, 65, 2, 0.0))
            faults.Add("a berry bush grew on watered farmland, which is the crops' ground");

        return faults;
    }

    /// <summary>
    /// Plants a sapling in a real world and grows a tree out of it, fells it and watches the canopy
    /// come down, and checks the refusals on the way — with a second, placed canopy as the control
    /// that must NOT come down.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Every arm has its refusal</b>: a sapling in the dark must not grow; a sapling under
    /// a ceiling must not grow; a leaf on a standing tree must not fall; a placed leaf with no tree
    /// anywhere near it must never fall. "The tree grew and the leaves fell" is true of a build that
    /// grows everything and drops everything.</para>
    /// <para>⚠ Stepped with a roll of zero, as the field check is: the chance is not under test.</para>
    /// </remarks>
    public static List<string> ValidateTrees(BlockRegistry registry, TerrainGenerator trees)
    {
        var faults = new List<string>();

        var world = new VoxelWorld(registry);
        var growth = new Growth(registry) { Trees = trees };

        var dirt = registry.ByName("dirt").Id;
        var stone = registry.ByName("stone").Id;
        var sapling = registry.ByName(StarterBlocks.SaplingName).Id;
        var log = registry.ByName("driftoak_log").Id;
        var leaves = registry.ByName("driftoak_leaves").Id;
        var placed = registry.ByName(StarterBlocks.LeavesPlacedName).Id;

        // A lawn at y 64, wide enough for a crown, and a sapling in the middle of it. Dark first.
        for (var x = -12; x <= 12; x++)
        for (var z = -12; z <= 12; z++)
            world.SetBlock(x, 64, z, dirt);

        world.SetBlock(0, 65, 0, sapling);

        if (growth.Step(world, 0, 65, 0, 0.0))
            faults.Add("a sapling in the dark grew");

        new Lighting.LightEngine(registry).LightAll(world);

        // ⛔ THE CEILING ARM: a slab of stone two above the sapling. Lit from the sides (sky light
        // spreads sideways, the field check's own lesson), so only the blockage can refuse it.
        world.SetBlock(0, 67, 0, stone);
        if (growth.Step(world, 0, 65, 0, 0.0))
            faults.Add("a sapling grew a trunk through a ceiling");
        if (world.GetBlock(0, 67, 0) != stone)
            faults.Add("a refused sapling carved the ceiling anyway");
        world.SetBlock(0, 67, 0, BlockId.Air);

        if (!growth.Step(world, 0, 65, 0, 0.0))
            faults.Add("a lit sapling on open ground did not grow");
        if (world.GetBlock(0, 65, 0) != log)
            faults.Add($"a grown sapling's cell is {registry[world.GetBlock(0, 65, 0)].Name}, not a log");

        var canopy = new List<(int X, int Y, int Z)>();
        for (var x = -8; x <= 8; x++)
        for (var y = 65; y < 80; y++)
        for (var z = -8; z <= 8; z++)
            if (world.GetBlock(x, y, z) == leaves) canopy.Add((x, y, z));

        if (canopy.Count < 12)
            faults.Add($"a grown tree has {canopy.Count} leaves, which is not a canopy");

        // Every leaf of a standing tree is within reach of its wood, or the reach is too short.
        foreach (var (x, y, z) in canopy)
            if (!growth.Supported(world, x, y, z))
            {
                faults.Add($"a leaf of a standing tree at {x},{y},{z} counts as unsupported");
                break;
            }

        if (canopy.Count > 0 && growth.Step(world, canopy[0].X, canopy[0].Y, canopy[0].Z, 0.0))
            faults.Add("a leaf on a standing tree fell");

        // ⛳ A placed canopy with no log in the world at all, as the control: it must never fall.
        world.SetBlock(10, 70, 10, placed);
        world.SetBlock(10, 71, 10, placed);

        // Fell the tree — every log, wherever the flare and the branches put them.
        var logs = new List<(int X, int Y, int Z)>();
        for (var x = -8; x <= 8; x++)
        for (var y = 65; y < 80; y++)
        for (var z = -8; z <= 8; z++)
            if (world.GetBlock(x, y, z) == log) logs.Add((x, y, z));
        foreach (var (x, y, z) in logs) world.SetBlock(x, y, z, BlockId.Air);

        var fell = 0;
        foreach (var (x, y, z) in canopy)
            if (growth.Step(world, x, y, z, 0.0)) fell++;

        if (fell != canopy.Count)
            faults.Add($"{fell} of {canopy.Count} leaves fell once their tree was felled");
        if (growth.Fallen.Count != fell)
            faults.Add($"{growth.Fallen.Count} leaves reported fallen for {fell} that did");

        if (growth.Step(world, 10, 70, 10, 0.0) || world.GetBlock(10, 70, 10) != placed)
            faults.Add("a placed leaf with no tree near it decayed");

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
    /// ⚠ The budget is carried between frames rather than rounded per frame. At a few looks a chunk
    /// a second and sixty frames, every frame's share of one chunk is a fraction of a cell — rounded
    /// down that is nothing at all, and nothing would ever grow.
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
