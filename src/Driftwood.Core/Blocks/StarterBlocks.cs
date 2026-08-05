using Driftwood.Core.Audio;
using Driftwood.Core.Items;
using Driftwood.Core.Lighting;

namespace Driftwood.Core.Blocks;

/// <summary>
/// The P0 block set, chosen to cover the opening survival loop: wood to punch, stone and ore
/// under it, and enough terrain material to make a world read as a world. Every entry here is
/// expected to survive into the data-driven content pass — the ids are not.
/// </summary>
public static class StarterBlocks
{
    // Texture array layers. At P0 these index a colour palette; later they index real art.
    public const ushort LayerStone = 0;
    public const ushort LayerDirt = 1;
    public const ushort LayerGrassTop = 2;
    public const ushort LayerGrassSide = 3;
    public const ushort LayerSand = 4;
    public const ushort LayerWater = 5;
    public const ushort LayerGravel = 6;
    public const ushort LayerLogSide = 7;
    public const ushort LayerLogTop = 8;
    public const ushort LayerLeaves = 9;
    public const ushort LayerPlanks = 10;
    public const ushort LayerCoalOre = 11;
    public const ushort LayerIronOre = 12;
    public const ushort LayerBedrock = 13;
    public const ushort LayerEmberstone = 14;
    public const ushort LayerVine = 15;

    // Everything below arrived with the material pass: a texture pack ships art for a whole
    // vocabulary of rock, ore and ground, and until these existed there was nothing for most of it
    // to attach to. Each one also has to be somewhere in the world, or it is only a name.
    public const ushort LayerDeepstone = 16;
    public const ushort LayerCoralstone = 17;
    public const ushort LayerDriftstone = 18;
    public const ushort LayerSaltstone = 19;
    public const ushort LayerCopperOre = 20;
    public const ushort LayerGoldOre = 21;
    public const ushort LayerStormglassOre = 22;
    public const ushort LayerAzuriteOre = 23;
    public const ushort LayerClay = 24;
    public const ushort LayerSandstone = 25;
    public const ushort LayerSandstoneTop = 26;
    public const ushort LayerSnow = 27;

    // Model-driven shapes brought their own art with them: the fringe a grass block wears down its
    // side is a cut-out laid over the dirt, and a tuft of grass is a texture with no cube to sit on.
    public const ushort LayerGrassSideOverlay = 28;
    public const ushort LayerMeadowgrass = 29;
    public const ushort LayerSeaflax = 30;
    public const ushort LayerMarshlily = 31;
    public const ushort LayerTorch = 32;

    // What crafting brought with it. Rubble is what a pickaxe leaves and stone is what a furnace
    // gives back, so the two are seen side by side constantly and have to look like cause and
    // effect. The bench and the furnace each need a face that is not their sides, which is why the
    // cube model grew a facing form.
    public const ushort LayerRubble = 33;
    public const ushort LayerGlass = 34;
    public const ushort LayerBricks = 35;
    public const ushort LayerBenchTop = 36;
    public const ushort LayerBenchSide = 37;
    public const ushort LayerFurnaceTop = 38;
    public const ushort LayerFurnaceSide = 39;
    public const ushort LayerFurnaceFront = 40;
    public const ushort LayerFurnaceFrontLit = 41;

    /// <summary>The first layer that is an item icon rather than a block face.</summary>
    /// <remarks>
    /// Items share the block texture array rather than taking one of their own. They are the same
    /// sixteen-pixel tiles, they are drawn by the same two places — a slot on the bar and a thing
    /// spinning on the floor — and a second array would be a second bind, a second upload and a
    /// second pack-import path for no difference anybody could see.
    /// </remarks>
    public const ushort LayerFirstIcon = 42;

    public const ushort LayerStick = 42;
    public const ushort LayerCoal = 43;
    public const ushort LayerCharcoal = 44;
    public const ushort LayerRawCopper = 45;
    public const ushort LayerRawIron = 46;
    public const ushort LayerRawGold = 47;
    public const ushort LayerCopperIngot = 48;
    public const ushort LayerIronIngot = 49;
    public const ushort LayerGoldIngot = 50;
    public const ushort LayerStormglass = 51;
    public const ushort LayerAzurite = 52;
    public const ushort LayerClayLump = 53;
    public const ushort LayerBrick = 54;

    /// <summary>The tool icons: one palette per tier, four heads each, tier-major.</summary>
    public const ushort LayerFirstTool = 55;

    /// <summary>Head shapes a tier comes in — pickaxe, axe, shovel, sword.</summary>
    public const int ToolShapeCount = 4;

    /// <summary>Palettes a head comes in — wood, stone, copper, gold, iron, stormglass.</summary>
    public const int ToolTierCount = 6;

    public const int LayerCount = LayerFirstTool + ToolShapeCount * ToolTierCount;

    public sealed record Ids(
        BlockId Stone,
        BlockId Dirt,
        BlockId Grass,
        BlockId Sand,
        BlockId Water,
        BlockId Gravel,
        BlockId Log,
        BlockId Leaves,
        BlockId Planks,
        BlockId CoalOre,
        BlockId IronOre,
        BlockId Bedrock,
        BlockId Emberstone,
        BlockId Vine,
        BlockId Deepstone,
        BlockId Coralstone,
        BlockId Driftstone,
        BlockId Saltstone,
        BlockId CopperOre,
        BlockId GoldOre,
        BlockId StormglassOre,
        BlockId AzuriteOre,
        BlockId Clay,
        BlockId Sandstone,
        BlockId Snow,
        BlockId SnowLayer,
        BlockId Meadowgrass,
        BlockId Seaflax,
        BlockId Marshlily,
        BlockId Rubble,
        BlockId Glass,
        BlockId Bricks,
        BlockId Bench,
        BlockId Furnace,
        BlockId FurnaceLit)
    {
        /// <summary>Every rock an ore can form in. Ore replaces rock, whichever rock it is.</summary>
        public BlockId[] Rock => [Stone, Deepstone, Coralstone, Driftstone, Saltstone];

        /// <summary>Everything mining is meant to yield, for the census to weigh against rock.</summary>
        public BlockId[] Ores => [CoalOre, IronOre, CopperOre, GoldOre, StormglassOre, AzuriteOre, Emberstone];

        /// <summary>Everything that grows on open ground, for the census to weigh together.</summary>
        public BlockId[] GroundCover => [Meadowgrass, Seaflax, Marshlily];

        /// <summary>The flowers, which are rarer than the grass they stand in.</summary>
        public BlockId[] Flowers => [Seaflax, Marshlily];
    }

    public static Ids Register(BlockRegistry registry)
    {
        // Id 0 must be air; chunk storage treats a zeroed array as empty.
        registry.Register(new BlockType { Name = "air", Solid = false, Opaque = false });

        // Hardness is in the genre's units; MiningRules turns it into seconds. Loose ground is
        // under a second, timber is a few, and anything that wants a pickaxe says so — which is
        // what makes the first pickaxe worth crafting at P6 rather than a formality.
        var stone = registry.Register(new BlockType
        {
            Name = "stone", Hardness = 1.5f, HarvestClass = ToolClass.Pickaxe, HarvestTier = 1,
            TopLayer = LayerStone, SideLayer = LayerStone, BottomLayer = LayerStone,
        });
        var dirt = registry.Register(new BlockType
        {
            Name = "dirt", Hardness = 0.5f, Sounds = SoundMaterial.Dirt,
            HarvestClass = ToolClass.Shovel,
            TopLayer = LayerDirt, SideLayer = LayerDirt, BottomLayer = LayerDirt,
        });
        // The one block that was never really a cube. Its top is the climate colour over a grey
        // tile, its bottom is plain dirt, and the green rolling over its sides is a second cut-out
        // laid over the first and tinted the same as the top. Before models there was nowhere to
        // put that second pass, so grass sides came out as bare dirt in every pack ever imported.
        var grass = registry.Register(new BlockType
        {
            Name = "grass", Hardness = 0.6f, Tint = TintSource.Grass, Sounds = SoundMaterial.Grass,
            HarvestClass = ToolClass.Shovel,
            Model = BlockModel.CubeWithSideOverlay(
                LayerGrassTop, LayerGrassSide, LayerDirt, LayerGrassSideOverlay),
        });
        var sand = registry.Register(new BlockType
        {
            Name = "sand", Hardness = 0.5f, Sounds = SoundMaterial.Sand,
            HarvestClass = ToolClass.Shovel,
            TopLayer = LayerSand, SideLayer = LayerSand, BottomLayer = LayerSand,
        });

        // Water is non-solid and non-opaque: you fall through it and it does not hide the
        // sea floor. P0 still draws it in the opaque pass; sorted translucency is a later phase.
        // The attenuation is what makes depth read as depth — light falls off twice as fast under
        // water, so a shallow sandbar stays bright while a trench goes black.
        // Unbreakable because a fluid is not something you mine — a ray passes through it to the
        // sea bed and it is not targetable at all. Saying so here means that if it ever does become
        // targetable, the answer is already no rather than a silent hole in the ocean.
        var water = registry.Register(new BlockType
        {
            Name = "water", Solid = false, Opaque = false, LightAttenuation = 1, Hardness = -1f,
            Tint = TintSource.Water, Sounds = SoundMaterial.Water,
            TopLayer = LayerWater, SideLayer = LayerWater, BottomLayer = LayerWater,
        });

        var gravel = registry.Register(new BlockType
        {
            Name = "gravel", Hardness = 0.6f, Sounds = SoundMaterial.Gravel,
            HarvestClass = ToolClass.Shovel,
            TopLayer = LayerGravel, SideLayer = LayerGravel, BottomLayer = LayerGravel,
        });
        var log = registry.Register(new BlockType
        {
            Name = "driftoak_log", Hardness = 2f, Sounds = SoundMaterial.Wood,
            HarvestClass = ToolClass.Axe,
            TopLayer = LayerLogTop, SideLayer = LayerLogSide, BottomLayer = LayerLogTop,
        });

        // Leaves are solid but see-through, which is exactly why the two flags are separate. They
        // dim what passes through rather than stopping it, so a canopy casts shade instead of a
        // hole and the forest floor is darker than the field beside it.
        var leaves = registry.Register(new BlockType
        {
            Name = "driftoak_leaves", Hardness = 0.2f, Opaque = false, LightAttenuation = 1,
            Sounds = SoundMaterial.Leaves,
            Tint = TintSource.Foliage,
            TopLayer = LayerLeaves, SideLayer = LayerLeaves, BottomLayer = LayerLeaves,
        });

        var planks = registry.Register(new BlockType
        {
            Name = "driftoak_planks", Hardness = 2f, Crafted = true, Sounds = SoundMaterial.Wood,
            HarvestClass = ToolClass.Axe,
            TopLayer = LayerPlanks, SideLayer = LayerPlanks, BottomLayer = LayerPlanks,
        });

        // The ore ladder is a tier ladder. Coal comes up with the first wooden pickaxe, the two
        // working metals want stone, the two showy ones want copper, and the gem at the floor of
        // the world wants iron — so each rung is the reason to make the next.
        var coal = registry.Register(new BlockType
        {
            Name = "coal_ore", Hardness = 3f, HarvestClass = ToolClass.Pickaxe, HarvestTier = 1,
            TopLayer = LayerCoalOre, SideLayer = LayerCoalOre, BottomLayer = LayerCoalOre,
        });
        var iron = registry.Register(new BlockType
        {
            Name = "iron_ore", Hardness = 3f, HarvestClass = ToolClass.Pickaxe, HarvestTier = 2,
            TopLayer = LayerIronOre, SideLayer = LayerIronOre, BottomLayer = LayerIronOre,
        });

        // The floor of the world. Unbreakable is the whole job.
        var bedrock = registry.Register(new BlockType
        {
            Name = "bedrock", Hardness = -1f,
            TopLayer = LayerBedrock, SideLayer = LayerBedrock, BottomLayer = LayerBedrock,
        });

        // The one thing in the world that gives off light. Added so lighting has something to
        // prove itself against underground before a placeable torch exists — a light system whose
        // only source is the sun can only ever be tested outdoors, where it is hardest to be wrong
        // in a way anyone notices. Warm and slightly red, so coloured light is visibly coloured.
        var emberstone = registry.Register(new BlockType
        {
            Name = "emberstone", Hardness = 0.3f, HarvestClass = ToolClass.Pickaxe, HarvestTier = 1,
            LightEmission = LightValue.PackBlock(15, 10, 4),
            TopLayer = LayerEmberstone, SideLayer = LayerEmberstone, BottomLayer = LayerEmberstone,
        });

        // Vines hang off canopy undersides and overhangs. Neither solid nor opaque — you walk
        // through them and they barely shade what is behind — but they still dim light by a level,
        // which is what makes a curtain of them read as a curtain.
        //
        // Crossed planes rather than a cube. Ours hang in open air below a crown rather than
        // clinging to a wall, and a cube of vine texture floating under a tree reads as a mistake
        // from every angle — six faces of holes with nothing inside them.
        var vine = registry.Register(new BlockType
        {
            Name = "vine", Hardness = 0.2f, Solid = false, Opaque = false, LightAttenuation = 1,
            Sounds = SoundMaterial.Plant,
            Tint = TintSource.Foliage,
            Model = BlockModel.Cross(LayerVine),
        });

        // Rock below the metals' reach. Harder than stone, and it is what makes going deep read as
        // going somewhere rather than as more of the same grey.
        var deepstone = registry.Register(new BlockType
        {
            Name = "deepstone", Hardness = 3f, HarvestClass = ToolClass.Pickaxe, HarvestTier = 2,
            TopLayer = LayerDeepstone, SideLayer = LayerDeepstone, BottomLayer = LayerDeepstone,
        });

        // Three intrusions through the stone. Our own names for them, in the same register as
        // emberstone: plain compound nouns that say what the rock looks like, in the game's own
        // coastal vocabulary. One uniform grey underground reads as a texture rather than as
        // geology, and a pack has art for three kinds of rock whatever anyone calls them.
        var coralstone = registry.Register(new BlockType
        {
            Name = "coralstone", Hardness = 1.5f, HarvestClass = ToolClass.Pickaxe, HarvestTier = 1,
            TopLayer = LayerCoralstone, SideLayer = LayerCoralstone, BottomLayer = LayerCoralstone,
        });
        var driftstone = registry.Register(new BlockType
        {
            Name = "driftstone", Hardness = 1.5f, HarvestClass = ToolClass.Pickaxe, HarvestTier = 1,
            TopLayer = LayerDriftstone, SideLayer = LayerDriftstone, BottomLayer = LayerDriftstone,
        });
        var saltstone = registry.Register(new BlockType
        {
            Name = "saltstone", Hardness = 1.5f, HarvestClass = ToolClass.Pickaxe, HarvestTier = 1,
            TopLayer = LayerSaltstone, SideLayer = LayerSaltstone, BottomLayer = LayerSaltstone,
        });

        // The rest of the ore ladder. Real metals keep their real names — nobody owns the word
        // copper, and a player knows what gold is worth on sight. The top tier is ours: stormglass,
        // a cold gem found only at the floor of the world.
        var copper = registry.Register(new BlockType
        {
            Name = "copper_ore", Hardness = 3f, HarvestClass = ToolClass.Pickaxe, HarvestTier = 2,
            TopLayer = LayerCopperOre, SideLayer = LayerCopperOre, BottomLayer = LayerCopperOre,
        });
        var gold = registry.Register(new BlockType
        {
            Name = "gold_ore", Hardness = 3f, HarvestClass = ToolClass.Pickaxe, HarvestTier = 3,
            TopLayer = LayerGoldOre, SideLayer = LayerGoldOre, BottomLayer = LayerGoldOre,
        });
        var stormglass = registry.Register(new BlockType
        {
            Name = "stormglass_ore", Hardness = 3f, HarvestClass = ToolClass.Pickaxe, HarvestTier = 4,
            TopLayer = LayerStormglassOre, SideLayer = LayerStormglassOre, BottomLayer = LayerStormglassOre,
        });

        // Azurite is a real blue copper mineral, and ours rather than anybody's coined name.
        var azurite = registry.Register(new BlockType
        {
            Name = "azurite_ore", Hardness = 3f, HarvestClass = ToolClass.Pickaxe, HarvestTier = 3,
            TopLayer = LayerAzuriteOre, SideLayer = LayerAzuriteOre, BottomLayer = LayerAzuriteOre,
        });

        var clay = registry.Register(new BlockType
        {
            Name = "clay", Hardness = 0.6f, Sounds = SoundMaterial.Dirt,
            HarvestClass = ToolClass.Shovel,
            TopLayer = LayerClay, SideLayer = LayerClay, BottomLayer = LayerClay,
        });

        // Under every beach. The top face is its own texture because a stratified side and a
        // stratified top would put the bands on edge when seen from above.
        var sandstone = registry.Register(new BlockType
        {
            Name = "sandstone", Hardness = 0.8f, HarvestClass = ToolClass.Pickaxe, HarvestTier = 1,
            TopLayer = LayerSandstoneTop, SideLayer = LayerSandstone, BottomLayer = LayerSandstoneTop,
        });

        // Lies on cold ground and on high ground. Soft — it is the one thing in the world that
        // comes away in a single blow.
        var snow = registry.Register(new BlockType
        {
            Name = "snow", Hardness = 0.2f, Sounds = SoundMaterial.Snow,
            HarvestClass = ToolClass.Shovel,
            TopLayer = LayerSnow, SideLayer = LayerSnow, BottomLayer = LayerSnow,
        });

        // The first fall of snow, a fifth of a block deep, lying over whatever it settled on. It is
        // what turns the snow line from a drawn edge into a fade: a band of ground still green
        // under a dusting, between the meadow and the snowfield proper.
        var snowLayer = registry.Register(new BlockType
        {
            Name = "snow_layer", Hardness = 0.1f, Solid = false, Opaque = false, Sounds = SoundMaterial.Snow,
            HarvestClass = ToolClass.Shovel,
            Model = BlockModel.Layer(LayerSnow, LayerSnow, LayerSnow, 3f),
        });

        // The first block in the world with a shape rather than a size. Two planes crossed through
        // the middle of the cell, no collision, no occlusion, and lit flat so both halves match —
        // the model format's own answer for every small plant, and the thing that proves the
        // per-block path works against something a player can walk through.
        var meadowgrass = registry.Register(new BlockType
        {
            Name = "meadowgrass", Hardness = 0.05f, Solid = false, Opaque = false, Sounds = SoundMaterial.Plant,
            Tint = TintSource.Grass,
            Model = BlockModel.Cross(LayerMeadowgrass),
        });

        // Flowers take no climate colour. A pack paints grass and leaves near-grey expecting the
        // multiply, and paints a flower the colour the flower is — running one through the grass
        // colormap turns every bloom in the world the same green as the field it stands in.
        var seaflax = registry.Register(new BlockType
        {
            Name = "seaflax", Hardness = 0.05f, Solid = false, Opaque = false, Sounds = SoundMaterial.Plant,
            Model = BlockModel.Cross(LayerSeaflax, tinted: false),
        });
        var marshlily = registry.Register(new BlockType
        {
            Name = "marshlily", Hardness = 0.05f, Solid = false, Opaque = false, Sounds = SoundMaterial.Plant,
            Model = BlockModel.Cross(LayerMarshlily, tinted: false),
        });

        // What a pickaxe leaves when it takes stone, and what a furnace turns back into stone. The
        // loop between the two is the reason a furnace is worth building before there is any metal
        // to smelt, and it is why rubble is the block most early walls are made of.
        var rubble = registry.Register(new BlockType
        {
            // Crafted, in the sense the flag means: nothing in the ground is made of it. Rubble is
            // what stone becomes on the way out, so a world census that expected to find some would
            // be looking for something that only exists once somebody has swung at a wall.
            Name = "rubble", Hardness = 2f, Crafted = true,
            HarvestClass = ToolClass.Pickaxe, HarvestTier = 1,
            TopLayer = LayerRubble, SideLayer = LayerRubble, BottomLayer = LayerRubble,
        });

        // Solid, and see-through. The pair of flags earns its keep again: a pane stops a player and
        // does not stop the sun, so a glasshouse is bright inside.
        var glass = registry.Register(new BlockType
        {
            Name = "glass", Hardness = 0.3f, Opaque = false, Crafted = true,
            Sounds = SoundMaterial.Glass,
            Model = BlockModel.Cube(LayerGlass, LayerGlass, LayerGlass),
        });

        var bricks = registry.Register(new BlockType
        {
            Name = "bricks", Hardness = 2f, Crafted = true,
            HarvestClass = ToolClass.Pickaxe, HarvestTier = 1,
            TopLayer = LayerBricks, SideLayer = LayerBricks, BottomLayer = LayerBricks,
        });

        RegisterConnected(registry);

        // The built shapes. Every orientation is its own block, because there is nowhere else to
        // keep one — a cell holds an id and nothing beside it. That is also how the genre stores it
        // underneath, and it means the mesher never asks which way a stair faces: the id says.
        RegisterShapes(registry);

        // Standing on the floor, and the first light a player can carry into a cave. Warm and
        // slightly red, so a torch-lit tunnel does not read as daylight underground.
        registry.Register(new BlockType
        {
            Name = "torch", Hardness = 0.05f, Solid = false, Opaque = false, Crafted = true,
            Sounds = SoundMaterial.Wood,
            LightEmission = LightValue.PackBlock(14, 10, 5),
            Model = BlockModel.Torch(LayerTorch),
        });

        // The two blocks a player uses rather than builds with. Both are Interactive, which is what
        // makes a right-click open them instead of stacking another one on top — the first time the
        // world has had a block that answers back.
        var bench = registry.Register(new BlockType
        {
            Name = "bench", Hardness = 2.5f, Crafted = true, Interactive = true,
            HarvestClass = ToolClass.Axe, Sounds = SoundMaterial.Wood,
            TopLayer = LayerBenchTop, SideLayer = LayerBenchSide, BottomLayer = LayerPlanks,
        });

        // Four facings each, lit and unlit. Eight ids for one machine, which is the price of a cell
        // that holds an id and nothing beside it — and the reason the mesher never has to ask which
        // way a furnace is pointing or whether it is burning.
        var furnace = BlockId.Air;
        var furnaceLit = BlockId.Air;
        for (var i = 0; i < Placeable.Facings.Length; i++)
        foreach (var lit in (bool[])[false, true])
        {
            var id = registry.Register(new BlockType
            {
                Name = $"furnace_{FacingNames[i]}{(lit ? "_lit" : "")}",
                Hardness = 3.5f, Crafted = true, Interactive = true,
                HarvestClass = ToolClass.Pickaxe, HarvestTier = 1,
                LightEmission = lit ? LightValue.PackBlock(13, 9, 4) : (ushort)0,
                Model = BlockModel.CubeFacing(
                    LayerFurnaceTop, LayerFurnaceSide, LayerFurnaceTop,
                    lit ? LayerFurnaceFrontLit : LayerFurnaceFront,
                    Placeable.Facings[i]),
            });

            if (i != 0) continue;
            if (lit) furnaceLit = id; else furnace = id;
        }

        return new Ids(
            stone, dirt, grass, sand, water, gravel, log, leaves, planks, coal, iron, bedrock,
            emberstone, vine, deepstone, coralstone, driftstone, saltstone, copper, gold, stormglass,
            azurite, clay, sandstone, snow, snowLayer, meadowgrass, seaflax, marshlily,
            rubble, glass, bricks, bench, furnace, furnaceLit);
    }

    /// <summary>Every form of the furnace, by facing then lit — the order they were registered.</summary>
    public static BlockId[] Furnaces(BlockRegistry registry, bool lit)
    {
        var ids = new BlockId[Placeable.Facings.Length];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = registry.ByName($"furnace_{FacingNames[i]}{(lit ? "_lit" : "")}").Id;
        return ids;
    }

    /// <summary>One material that comes in slab and stair form: its tiles, its sound, its tool.</summary>
    public readonly record struct ShapedMaterial(
        string Name, ushort Top, ushort Side, ushort Bottom, SoundMaterial Sound,
        ToolClass Harvest, int Tier);

    /// <summary>
    /// The materials shapes are cut from. One row here is ten blocks and two recipes.
    /// </summary>
    /// <remarks>
    /// The table is the point. Eight hundred blocks in this genre is fifty-odd template families
    /// times a few axes, not eight hundred drawings — so every axis we have should read like this
    /// one, where adding a wood species is a line rather than a chapter.
    /// </remarks>
    private static readonly ShapedMaterial[] ShapedMaterials =
    [
        new("driftoak", LayerPlanks, LayerPlanks, LayerPlanks, SoundMaterial.Wood, ToolClass.Axe, 0),
        new("stone", LayerStone, LayerStone, LayerStone, SoundMaterial.Stone, ToolClass.Pickaxe, 1),
        new("rubble", LayerRubble, LayerRubble, LayerRubble, SoundMaterial.Stone, ToolClass.Pickaxe, 1),
    ];

    /// <summary>Facing names in <see cref="Placeable.Facings"/> order: +x, -x, +z, -z.</summary>
    private static readonly string[] FacingNames = ["east", "west", "south", "north"];

    /// <summary>
    /// The things that join up with what is beside them: what they are cut from and how they build.
    /// </summary>
    /// <param name="PostHalf">Half the centre post's width, in sixteenths.</param>
    /// <param name="Bars">The heights the arms run at — two for a fence's rails, one for a wall.</param>
    public readonly record struct ConnectedMaterial(
        string Name, ushort Layer, SoundMaterial Sound, ToolClass Harvest, int Tier,
        float PostHalf, float ArmHalf, (float Low, float High)[] Bars);

    private static readonly ConnectedMaterial[] ConnectedMaterials =
    [
        new("driftoak_fence", LayerPlanks, SoundMaterial.Wood, ToolClass.Axe, 0,
            2f, 1f, [(6f, 9f), (12f, 15f)]),
        new("rubble_wall", LayerRubble, SoundMaterial.Stone, ToolClass.Pickaxe, 1,
            4f, 3f, [(0f, 14f)]),
        new("glass_pane", LayerGlass, SoundMaterial.Glass, ToolClass.None, 0,
            1f, 1f, [(0f, 16f)]),
    ];

    /// <summary>The names of the things that join up with their neighbours.</summary>
    public static IEnumerable<string> ConnectedNames
    {
        get { foreach (var m in ConnectedMaterials) yield return m.Name; }
    }

    /// <summary>
    /// Sixteen forms of each, one per set of sides it could be joined on.
    /// </summary>
    /// <remarks>
    /// Which sides a fence joins is a set rather than an orientation, and a set of four has sixteen
    /// members. Every one is its own id for the same reason every stair facing is: a cell holds an
    /// id and nothing beside it. <see cref="ConnectionTable"/> is what keeps them right.
    /// </remarks>
    private static void RegisterConnected(BlockRegistry registry)
    {
        foreach (var m in ConnectedMaterials)
        for (var mask = 0; mask < ConnectionFamily.Masks; mask++)
        {
            registry.Register(new BlockType
            {
                Name = $"{m.Name}_{mask}",
                Hardness = 2f, Opaque = false, Crafted = true, Sounds = m.Sound,
                HarvestClass = m.Harvest, HarvestTier = m.Tier,
                Model = BlockModel.Connected(
                    m.Layer, m.Layer, m.Layer, m.PostHalf, m.ArmHalf, m.Bars, mask),
            });
        }
    }

    /// <summary>All sixteen forms of one connecting material, in mask order.</summary>
    public static BlockId[] Connected(BlockRegistry registry, string material)
    {
        var ids = new BlockId[ConnectionFamily.Masks];
        for (var mask = 0; mask < ids.Length; mask++)
            ids[mask] = registry.ByName($"{material}_{mask}").Id;
        return ids;
    }

    /// <summary>Every family that joins up with its neighbours, built out of the registry.</summary>
    public static ConnectionTable Connections(BlockRegistry registry)
    {
        var families = new ConnectionFamily[ConnectedMaterials.Length];

        for (var f = 0; f < families.Length; f++)
        {
            var byMask = new BlockId[ConnectionFamily.Masks];
            for (var mask = 0; mask < byMask.Length; mask++)
                byMask[mask] = registry.ByName($"{ConnectedMaterials[f].Name}_{mask}").Id;

            families[f] = new ConnectionFamily { Name = ConnectedMaterials[f].Name, ByMask = byMask };
        }

        return new ConnectionTable(registry, families);
    }

    private static void RegisterShapes(BlockRegistry registry)
    {
        foreach (var m in ShapedMaterials)
        {
            foreach (var upper in (bool[])[false, true])
            {
                registry.Register(new BlockType
                {
                    Name = $"{m.Name}_slab_{(upper ? "upper" : "lower")}",
                    Hardness = 2f, Opaque = false, Crafted = true, Sounds = m.Sound,
                    HarvestClass = m.Harvest, HarvestTier = m.Tier,
                    Model = BlockModel.Slab(m.Top, m.Side, m.Bottom, upper),
                });
            }

            for (var i = 0; i < Placeable.Facings.Length; i++)
            foreach (var upper in (bool[])[false, true])
            {
                registry.Register(new BlockType
                {
                    Name = $"{m.Name}_stairs_{FacingNames[i]}_{(upper ? "upper" : "lower")}",
                    Hardness = 2f, Opaque = false, Crafted = true, Sounds = m.Sound,
                    HarvestClass = m.Harvest, HarvestTier = m.Tier,
                    Model = BlockModel.Stairs(m.Top, m.Side, m.Bottom, Placeable.Facings[i], upper),
                });
            }
        }
    }

    /// <summary>The names of the materials that come in slab and stair form.</summary>
    public static IEnumerable<string> ShapedNames
    {
        get { foreach (var m in ShapedMaterials) yield return m.Name; }
    }

    /// <summary>Every form of one shaped material, in the order a <see cref="Placeable"/> wants them.</summary>
    public static BlockId[] Slabs(BlockRegistry registry, string material) =>
    [
        registry.ByName($"{material}_slab_lower").Id,
        registry.ByName($"{material}_slab_upper").Id,
    ];

    /// <summary>Eight stair orientations: <see cref="Placeable.Facings"/> order, each lower then upper.</summary>
    public static BlockId[] Stairs(BlockRegistry registry, string material)
    {
        var stairs = new BlockId[Placeable.Facings.Length * 2];
        for (var i = 0; i < Placeable.Facings.Length; i++)
        {
            stairs[i * 2] = registry.ByName($"{material}_stairs_{FacingNames[i]}_lower").Id;
            stairs[i * 2 + 1] = registry.ByName($"{material}_stairs_{FacingNames[i]}_upper").Id;
        }

        return stairs;
    }
}
