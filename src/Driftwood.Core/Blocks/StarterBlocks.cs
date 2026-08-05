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

    // Cut stone. Every rock a player can dig turns into a worked form and the worked form into a
    // bonded one, and each of those is a slab, a stair and a wall as well — which is the whole of
    // why the tables below are tables. This one axis is more buildable blocks than everything that
    // came before it put together, and not one of them needed a new system to exist.
    public const ushort LayerStoneBricks = 42;
    public const ushort LayerSmoothStone = 43;
    public const ushort LayerDeepstonePolished = 44;
    public const ushort LayerDeepstoneBricks = 45;
    public const ushort LayerCoralstonePolished = 46;
    public const ushort LayerDriftstonePolished = 47;
    public const ushort LayerSaltstonePolished = 48;
    public const ushort LayerSandstoneCut = 49;
    public const ushort LayerSandstoneChiseled = 50;

    // Light a player can make and carry. The one family in the coverage report where the genre has
    // eighty files of art and we had two — a torch and a pane of glass — and not one of these
    // needed a system that did not already exist: emission is a per-block value, the torch proved
    // the model path, and the three-way split between solid, opaque and attenuating is what lets a
    // dark pane of glass be a real thing rather than a contradiction.
    public const ushort LayerLantern = 51;
    public const ushort LayerCampfireFire = 52;
    public const ushort LayerSmokeglass = 53;
    public const ushort LayerStormglassLamp = 54;

    // Things with a shut state and an open one, which is the piece the shape vocabulary was
    // missing. All three want a block that answers a right click by becoming a different block,
    // and a door wants two cells to be one thing — the field a double chest will want sideways.
    public const ushort LayerLadder = 55;
    public const ushort LayerDoorLower = 56;
    public const ushort LayerDoorUpper = 57;
    public const ushort LayerTrapdoor = 58;

    // The first place in the world a player can put something down and expect to find it again.
    public const ushort LayerChestTop = 59;
    public const ushort LayerChestSide = 60;
    public const ushort LayerChestFront = 61;

    // Stations. A bench had no front until now — every pack paints one and we mapped the same tile
    // to all four sides, so it read as a crate rather than as a thing with a working face.
    public const ushort LayerBenchFront = 62;
    public const ushort LayerStonecutterTop = 63;
    public const ushort LayerStonecutterSide = 64;

    /// <summary>The first layer that is an item icon rather than a block face.</summary>
    /// <remarks>
    /// Items share the block texture array rather than taking one of their own. They are the same
    /// sixteen-pixel tiles, they are drawn by the same two places — a slot on the bar and a thing
    /// spinning on the floor — and a second array would be a second bind, a second upload and a
    /// second pack-import path for no difference anybody could see.
    /// </remarks>
    public const ushort LayerFirstIcon = 65;

    public const ushort LayerStick = 65;
    public const ushort LayerCoal = 66;
    public const ushort LayerCharcoal = 67;
    public const ushort LayerRawCopper = 68;
    public const ushort LayerRawIron = 69;
    public const ushort LayerRawGold = 70;
    public const ushort LayerCopperIngot = 71;
    public const ushort LayerIronIngot = 72;
    public const ushort LayerGoldIngot = 73;
    public const ushort LayerStormglass = 74;
    public const ushort LayerAzurite = 75;
    public const ushort LayerClayLump = 76;
    public const ushort LayerBrick = 77;

    /// <summary>The tool icons: one palette per tier, four heads each, tier-major.</summary>
    public const ushort LayerFirstTool = 78;

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

        // Cut stone: every rock worked, and the harder ones bonded after that. One loop, because
        // the difference between them is a name, a tile and how hard it is to break — which is what
        // a family is. A hand-written block each would be nine near-identical declarations and a
        // tenth that quietly disagreed with the others about something.
        foreach (var cut in CutStones)
            registry.Register(new BlockType
            {
                Name = cut.Name, Hardness = cut.Hardness, Crafted = true,
                HarvestClass = ToolClass.Pickaxe, HarvestTier = cut.Tier,
                Sounds = SoundMaterial.Stone,
                TopLayer = cut.Layer, SideLayer = cut.Layer, BottomLayer = cut.Layer,
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
            Sounds = SoundMaterial.Wood, SupportFace = Faces.NegY,
            LightEmission = LightValue.PackBlock(14, 10, 5),
            Model = BlockModel.Torch(LayerTorch),
        });

        RegisterLights(registry);
        RegisterOpenings(registry);

        // Four facings, and the whole block is its front. Not a full cube — a chest stands a little
        // clear of the cell so a row of them reads as a row, which is also why it can never hide
        // what is behind it and says so with Opaque.
        for (var i = 0; i < Placeable.Facings.Length; i++)
            registry.Register(new BlockType
            {
                Name = $"chest_{FacingNames[i]}",
                Hardness = 2.5f, Opaque = false, Crafted = true, Use = BlockUse.Chest,
                HarvestClass = ToolClass.Axe, Sounds = SoundMaterial.Wood,
                SupportFace = Faces.NegY,
                Model = BlockModel.Chest(
                    LayerChestTop, LayerChestSide, LayerChestFront, Placeable.Facings[i]),
            });

        // The two blocks a player uses rather than builds with. Both answer a right click, which is
        // what makes one open instead of having another stacked on top of it — the first time the
        // world had a block that answers back.
        var bench = registry.Register(new BlockType
        {
            Name = "bench", Hardness = 2.5f, Crafted = true, Use = BlockUse.Bench,
            HarvestClass = ToolClass.Axe, Sounds = SoundMaterial.Wood,
            Model = BlockModel.CubeTwoSided(
                LayerBenchTop, LayerBenchFront, LayerBenchSide, LayerPlanks),
        });

        // A saw on a stone table. One rock in, and everything that rock can be cut into offered
        // together — which is the whole reason it exists rather than being a second bench.
        registry.Register(new BlockType
        {
            Name = "stonecutter", Hardness = 3.5f, Opaque = false, Crafted = true,
            Use = BlockUse.Stonecutter,
            HarvestClass = ToolClass.Pickaxe, HarvestTier = 1, Sounds = SoundMaterial.Stone,
            SupportFace = Faces.NegY,
            Model = BlockModel.Layer(LayerStonecutterTop, LayerStonecutterSide, LayerStonecutterSide, 9f),
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
                Hardness = 3.5f, Crafted = true, Use = BlockUse.Furnace,
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

    /// <summary>
    /// The light a player can make, and the glass that stops it.
    /// </summary>
    /// <remarks>
    /// <para>Four things, and between them a torch stops being the only answer to a dark room.
    /// A wall torch is the torch a wall was aimed at; a lantern is brighter, whiter and hangs; a
    /// campfire is a floor light with a state; and smokeglass is the one block in the set that
    /// exists <em>because</em> solidity, opacity and attenuation are three separate fields — it is
    /// seen through and it is not passed through, which is a combination nothing else has needed.
    /// </para>
    /// <para>The stormglass lamp is the first thing azurite has ever been for. Six ores came up out
    /// of the ground and five of them went somewhere; a mineral with no recipe at all is a hole in
    /// the tree rather than a decision, and a cold bright lamp is what the deep blue rock should
    /// obviously make.</para>
    /// </remarks>
    private static void RegisterLights(BlockRegistry registry)
    {
        // The same torch, fixed to a wall instead of standing on the floor. Four ids, because a
        // cell holds an id and nothing beside it, and the same light out of every one of them.
        for (var i = 0; i < Placeable.Facings.Length; i++)
            registry.Register(new BlockType
            {
                Name = $"torch_wall_{FacingNames[i]}",
                Hardness = 0.05f, Solid = false, Opaque = false, Crafted = true,
                Sounds = SoundMaterial.Wood,
                SupportFace = Placeable.Opposite(Placeable.Facings[i]),
                LightEmission = LightValue.PackBlock(14, 10, 5),
                Model = BlockModel.WallTorch(LayerTorch, Placeable.Facings[i]),
            });

        // Brighter than a torch and much whiter, which is the whole reason the genre carries both:
        // a torch says "somebody was here", a lantern says "somebody lives here".
        foreach (var hanging in (bool[])[false, true])
            registry.Register(new BlockType
            {
                Name = hanging ? "lantern_hanging" : "lantern",
                Hardness = 0.6f, Solid = false, Opaque = false, Crafted = true,
                Sounds = SoundMaterial.Metal,
                HarvestClass = ToolClass.Pickaxe,
                SupportFace = hanging ? Faces.PosY : Faces.NegY,
                LightEmission = LightValue.PackBlock(15, 13, 10),
                Model = BlockModel.Lantern(LayerLantern, hanging),
            });

        // Two ways the logs can lie and two states, not four ways and two. A stack of timber looks
        // the same from both ends, so a facing form would be four ids for two shapes — and a check
        // that cannot tell east from west is a check that passes anything.
        foreach (var axis in (int[])[Faces.PosX, Faces.PosZ])
        foreach (var lit in (bool[])[false, true])
            registry.Register(new BlockType
            {
                Name = $"campfire_{AxisNames[axis == Faces.PosX ? 0 : 1]}{(lit ? "_lit" : "")}",
                Hardness = 2f, Opaque = false, Crafted = true,
                Sounds = SoundMaterial.Wood, Use = BlockUse.Toggle,
                HarvestClass = ToolClass.Axe,
                SupportFace = Faces.NegY,
                LightEmission = lit ? LightValue.PackBlock(15, 11, 6) : (ushort)0,
                Model = BlockModel.Campfire(LayerLogSide, LayerLogTop, LayerCampfireFire, axis, lit),
            });

        // ⚠ Solid, see-through, and dark. Opaque is a question about faces and visibility;
        // LightAttenuation is a question about how much light survives the crossing, and this is
        // the block that needs them answered differently — a window that lets nothing through.
        // Fusing the two fields, which every voxel engine is tempted to do, makes this impossible.
        registry.Register(new BlockType
        {
            Name = "smokeglass", Hardness = 0.4f, Opaque = false, Crafted = true,
            LightAttenuation = LightValue.Max,
            Sounds = SoundMaterial.Glass,
            Model = BlockModel.Cube(LayerSmokeglass, LayerSmokeglass, LayerSmokeglass),
        });

        // The brightest thing that can be built, and cold where everything else is warm.
        registry.Register(new BlockType
        {
            Name = "stormglass_lamp", Hardness = 0.6f, Crafted = true,
            Sounds = SoundMaterial.Glass,
            LightEmission = LightValue.PackBlock(12, 15, 15),
            TopLayer = LayerStormglassLamp, SideLayer = LayerStormglassLamp,
            BottomLayer = LayerStormglassLamp,
        });
    }

    /// <summary>
    /// The things that open: ladders, trapdoors and doors.
    /// </summary>
    /// <remarks>
    /// <para>What kept these waiting was never the geometry — the mesher has drawn boxes since
    /// models landed. It was two rules. A block has to be able to <em>become</em> a different block
    /// when it is used, which the campfire brought; and a block has to be able to be two cells,
    /// which a door needs and which nothing before it did.</para>
    /// <para>A door is deliberately not "a tall block". Each half names the other, both ways, and
    /// each is held up by the other being there. That means breaking either end takes the whole
    /// thing without a single line anywhere asking which end was struck, and it is the same field a
    /// double chest will use pointing sideways instead of up.</para>
    /// <para>⚠ <b>An open door and an open trapdoor are not solid, and a shut one is.</b> Collision
    /// is per cell rather than per box, so a solid open door would be a doorway nobody can walk
    /// through — which is the whole point of opening it. The cost is that the panel itself can be
    /// walked through while it is swung aside, and that is the right way round.</para>
    /// </remarks>
    private static void RegisterOpenings(BlockRegistry registry)
    {
        // A ladder is a sheet on a wall and the first thing in the world that can be climbed.
        for (var i = 0; i < Placeable.Facings.Length; i++)
            registry.Register(new BlockType
            {
                Name = $"ladder_{FacingNames[i]}",
                Hardness = 0.4f, Solid = false, Opaque = false, Crafted = true, Climbable = true,
                Sounds = SoundMaterial.Wood, HarvestClass = ToolClass.Axe,
                SupportFace = Placeable.Opposite(Placeable.Facings[i]),
                Model = BlockModel.Sheet(LayerLadder, Placeable.Facings[i]),
            });

        // Four facings, either half of the cell, shut or swung up. Nothing holds a trapdoor: it is
        // fixed to the frame around it, which is a thing the world has no way to express and which
        // it would be worse to guess at than to leave alone.
        for (var i = 0; i < Placeable.Facings.Length; i++)
        foreach (var upper in (bool[])[false, true])
        foreach (var open in (bool[])[false, true])
            registry.Register(new BlockType
            {
                Name = TrapdoorName(i, upper, open),
                Hardness = 2f, Solid = !open, Opaque = false, Crafted = true,
                Sounds = SoundMaterial.Wood, Use = BlockUse.Toggle,
                HarvestClass = ToolClass.Axe,
                Model = BlockModel.Trapdoor(LayerTrapdoor, Placeable.Facings[i], upper, open),
            });

        // Four facings, two hinges, two halves, shut or open. The upper half wears its own texture
        // because every pack paints a door as two tiles — the panel and the part with the window in
        // it — and reading one tile twice would put the handle at knee height.
        for (var i = 0; i < Placeable.Facings.Length; i++)
        foreach (var hinge in Placeable.Hinges(Placeable.Facings[i]))
        foreach (var upper in (bool[])[false, true])
        foreach (var open in (bool[])[false, true])
            registry.Register(new BlockType
            {
                Name = DoorName(i, hinge, upper, open),
                Hardness = 3f, Solid = !open, Opaque = false, Crafted = true,
                Sounds = SoundMaterial.Wood, Use = BlockUse.Toggle,
                HarvestClass = ToolClass.Axe,

                // Only the lower half stands on anything; the upper is held by the lower being
                // there, which the partner rule already says and which is the same statement.
                SupportFace = upper ? -1 : Faces.NegY,
                PartnerFace = upper ? Faces.NegY : Faces.PosY,
                Model = BlockModel.Door(
                    upper ? LayerDoorUpper : LayerDoorLower, Placeable.Facings[i], hinge, open),
            });
    }

    private static string TrapdoorName(int facing, bool upper, bool open) =>
        $"trapdoor_{FacingNames[facing]}_{(upper ? "upper" : "lower")}{(open ? "_open" : "")}";

    private static string DoorName(int facing, int hinge, bool upper, bool open) =>
        $"door_{FacingNames[facing]}_{FacingNames[Array.IndexOf(Placeable.Facings, hinge)]}"
        + $"_{(upper ? "upper" : "lower")}{(open ? "_open" : "")}";

    /// <summary>The four chests, one per facing.</summary>
    public static BlockId[] Chests(BlockRegistry registry)
    {
        var ids = new BlockId[Placeable.Facings.Length];
        for (var i = 0; i < ids.Length; i++) ids[i] = registry.ByName($"chest_{FacingNames[i]}").Id;
        return ids;
    }

    /// <summary>The four ladders, one per wall, in <see cref="Placeable.Facings"/> order.</summary>
    public static BlockId[] Ladders(BlockRegistry registry)
    {
        var ids = new BlockId[Placeable.Facings.Length];
        for (var i = 0; i < ids.Length; i++) ids[i] = registry.ByName($"ladder_{FacingNames[i]}").Id;
        return ids;
    }

    /// <summary>The eight shut trapdoors, in the order a stair-shaped placeable wants them.</summary>
    public static BlockId[] Trapdoors(BlockRegistry registry)
    {
        var ids = new BlockId[Placeable.Facings.Length * 2];
        for (var i = 0; i < Placeable.Facings.Length; i++)
        {
            ids[i * 2] = registry.ByName(TrapdoorName(i, upper: false, open: false)).Id;
            ids[i * 2 + 1] = registry.ByName(TrapdoorName(i, upper: true, open: false)).Id;
        }
        return ids;
    }

    /// <summary>
    /// The sixteen shut doors: four facings, each with two hinges, each a lower half and an upper.
    /// </summary>
    public static BlockId[] Doors(BlockRegistry registry)
    {
        var ids = new BlockId[Placeable.Facings.Length * 4];
        for (var i = 0; i < Placeable.Facings.Length; i++)
        {
            var hinges = Placeable.Hinges(Placeable.Facings[i]);
            for (var h = 0; h < hinges.Length; h++)
            {
                ids[i * 4 + h * 2] = registry.ByName(DoorName(i, hinges[h], false, false)).Id;
                ids[i * 4 + h * 2 + 1] = registry.ByName(DoorName(i, hinges[h], true, false)).Id;
            }
        }
        return ids;
    }

    /// <summary>Each lower half and the upper half that goes above it, shut and open alike.</summary>
    public static IEnumerable<(BlockId Lower, BlockId Upper)> TallPairs(BlockRegistry registry)
    {
        for (var i = 0; i < Placeable.Facings.Length; i++)
        foreach (var hinge in Placeable.Hinges(Placeable.Facings[i]))
        foreach (var open in (bool[])[false, true])
            yield return (
                registry.ByName(DoorName(i, hinge, false, open)).Id,
                registry.ByName(DoorName(i, hinge, true, open)).Id);
    }

    /// <summary>Every upper half, which is the part of a two-cell block that leaves nothing.</summary>
    public static IEnumerable<string> UpperHalves
    {
        get
        {
            for (var i = 0; i < Placeable.Facings.Length; i++)
            foreach (var hinge in Placeable.Hinges(Placeable.Facings[i]))
            foreach (var open in (bool[])[false, true])
                yield return DoorName(i, hinge, true, open);
        }
    }

    /// <summary>Every swung-open lower half — a form nothing places, so nothing maps it back.</summary>
    public static IEnumerable<string> OpenLowerHalves
    {
        get
        {
            for (var i = 0; i < Placeable.Facings.Length; i++)
            foreach (var hinge in Placeable.Hinges(Placeable.Facings[i]))
                yield return DoorName(i, hinge, false, open: true);
        }
    }

    /// <summary>Every swung-open trapdoor, for the same reason.</summary>
    public static IEnumerable<string> OpenTrapdoors
    {
        get
        {
            for (var i = 0; i < Placeable.Facings.Length; i++)
            foreach (var upper in (bool[])[false, true])
                yield return TrapdoorName(i, upper, open: true);
        }
    }

    /// <summary>Each block with another state, and the state a right click swaps it to.</summary>
    /// <remarks>
    /// Named in one place because the pairing is content, not geometry. A lit campfire is not
    /// derivable from an unlit one by any rule the registry knows — they are two registered blocks
    /// whose names happen to differ by a suffix, and the day the pair is a door and its open form
    /// the suffix will not be the same one.
    /// </remarks>
    public static IEnumerable<(BlockId From, BlockId To)> Toggles(BlockRegistry registry)
    {
        foreach (var axis in AxisNames)
        {
            var outFire = registry.ByName($"campfire_{axis}").Id;
            var lit = registry.ByName($"campfire_{axis}_lit").Id;
            yield return (outFire, lit);
            yield return (lit, outFire);
        }

        for (var i = 0; i < Placeable.Facings.Length; i++)
        {
            foreach (var upper in (bool[])[false, true])
            {
                var shut = registry.ByName(TrapdoorName(i, upper, open: false)).Id;
                var open = registry.ByName(TrapdoorName(i, upper, open: true)).Id;
                yield return (shut, open);
                yield return (open, shut);
            }

            foreach (var hinge in Placeable.Hinges(Placeable.Facings[i]))
            foreach (var upper in (bool[])[false, true])
            {
                var shut = registry.ByName(DoorName(i, hinge, upper, open: false)).Id;
                var open = registry.ByName(DoorName(i, hinge, upper, open: true)).Id;
                yield return (shut, open);
                yield return (open, shut);
            }
        }
    }

    /// <summary>The two ways something can lie along the ground, in the order a placeable wants them.</summary>
    private static readonly string[] AxisNames = ["x", "z"];

    /// <summary>Both forms of the campfire, along x then along z, lit or out.</summary>
    public static BlockId[] Campfires(BlockRegistry registry, bool lit)
    {
        var ids = new BlockId[AxisNames.Length];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = registry.ByName($"campfire_{AxisNames[i]}{(lit ? "_lit" : "")}").Id;
        return ids;
    }

    /// <summary>A torch's five forms: standing, then leaning off each of the four walls.</summary>
    public static BlockId[] Torches(BlockRegistry registry)
    {
        var ids = new BlockId[1 + Placeable.Facings.Length];
        ids[0] = registry.ByName("torch").Id;
        for (var i = 0; i < Placeable.Facings.Length; i++)
            ids[1 + i] = registry.ByName($"torch_wall_{FacingNames[i]}").Id;
        return ids;
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
        new("bricks", LayerBricks, LayerBricks, LayerBricks, SoundMaterial.Stone, ToolClass.Pickaxe, 1),
        new("sandstone", LayerSandstoneTop, LayerSandstone, LayerSandstoneTop,
            SoundMaterial.Stone, ToolClass.Pickaxe, 0),

        // And every cut form, which is where this table stops being a list and starts being a
        // multiplier: nine rows below turn into eighteen shapes and eighteen recipes.
        new("stone_bricks", LayerStoneBricks, LayerStoneBricks, LayerStoneBricks,
            SoundMaterial.Stone, ToolClass.Pickaxe, 1),
        new("smooth_stone", LayerSmoothStone, LayerSmoothStone, LayerSmoothStone,
            SoundMaterial.Stone, ToolClass.Pickaxe, 1),
        new("polished_deepstone", LayerDeepstonePolished, LayerDeepstonePolished, LayerDeepstonePolished,
            SoundMaterial.Stone, ToolClass.Pickaxe, 1),
        new("deepstone_bricks", LayerDeepstoneBricks, LayerDeepstoneBricks, LayerDeepstoneBricks,
            SoundMaterial.Stone, ToolClass.Pickaxe, 1),
        new("polished_coralstone", LayerCoralstonePolished, LayerCoralstonePolished, LayerCoralstonePolished,
            SoundMaterial.Stone, ToolClass.Pickaxe, 1),
        new("polished_driftstone", LayerDriftstonePolished, LayerDriftstonePolished, LayerDriftstonePolished,
            SoundMaterial.Stone, ToolClass.Pickaxe, 1),
        new("polished_saltstone", LayerSaltstonePolished, LayerSaltstonePolished, LayerSaltstonePolished,
            SoundMaterial.Stone, ToolClass.Pickaxe, 1),
        new("cut_sandstone", LayerSandstoneCut, LayerSandstoneCut, LayerSandstoneCut,
            SoundMaterial.Stone, ToolClass.Pickaxe, 0),
    ];

    /// <summary>One rock worked into another form: what it is called, what it looks like, its cost.</summary>
    public readonly record struct CutStone(string Name, ushort Layer, float Hardness, int Tier);

    /// <summary>
    /// Every worked form of every rock a player can dig.
    /// </summary>
    /// <remarks>
    /// <para>Nine rows, and between them and the tables either side of this one they are more
    /// buildable blocks than everything that existed before them — each is also a slab, a stair and
    /// most are a wall. Not one needed a new system: they are stone, arranged.</para>
    /// <para>Which is the content-scope finding made concrete. Eight hundred blocks in this genre is
    /// fifty-odd families times a few axes, and this is the axis that costs least and shows most.
    /// </para>
    /// </remarks>
    private static readonly CutStone[] CutStones =
    [
        new("stone_bricks", LayerStoneBricks, 2f, 1),
        new("smooth_stone", LayerSmoothStone, 2f, 1),
        new("polished_deepstone", LayerDeepstonePolished, 3.5f, 1),
        new("deepstone_bricks", LayerDeepstoneBricks, 3.5f, 1),
        new("polished_coralstone", LayerCoralstonePolished, 1.5f, 1),
        new("polished_driftstone", LayerDriftstonePolished, 1.5f, 1),
        new("polished_saltstone", LayerSaltstonePolished, 1.5f, 1),
        new("cut_sandstone", LayerSandstoneCut, 0.8f, 0),
        new("chiseled_sandstone", LayerSandstoneChiseled, 0.8f, 0),
    ];

    /// <summary>The worked forms, for whatever wants to walk them.</summary>
    public static IEnumerable<string> CutStoneNames
    {
        get { foreach (var cut in CutStones) yield return cut.Name; }
    }

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
        new("stone_brick_wall", LayerStoneBricks, SoundMaterial.Stone, ToolClass.Pickaxe, 1,
            4f, 3f, [(0f, 14f)]),
        new("deepstone_brick_wall", LayerDeepstoneBricks, SoundMaterial.Stone, ToolClass.Pickaxe, 1,
            4f, 3f, [(0f, 14f)]),
        new("sandstone_wall", LayerSandstone, SoundMaterial.Stone, ToolClass.Pickaxe, 0,
            4f, 3f, [(0f, 14f)]),
        new("brick_wall", LayerBricks, SoundMaterial.Stone, ToolClass.Pickaxe, 1,
            4f, 3f, [(0f, 14f)]),
        new("glass_pane", LayerGlass, SoundMaterial.Glass, ToolClass.None, 0,
            1f, 1f, [(0f, 16f)]),
        new("smokeglass_pane", LayerSmokeglass, SoundMaterial.Glass, ToolClass.None, 0,
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

    /// <summary>What a material's two slabs add up to.</summary>
    /// <remarks>
    /// Named rather than derived, because a slab's whole block is not always the block it is cut
    /// from: driftoak slabs are cut from planks and make planks, but a stone slab is cut from stone
    /// and a rubble slab from rubble. The day a material's slab is made of something else, this is
    /// where that is said.
    /// </remarks>
    private static readonly (string Material, string Full)[] SlabDoubles =
    [
        ("driftoak", "driftoak_planks"),
        ("stone", "stone"),
        ("rubble", "rubble"),
    ];

    /// <summary>Each half-slab, and the whole block two of them become.</summary>
    public static IEnumerable<(BlockId Slab, BlockId Full)> SlabMerges(BlockRegistry registry)
    {
        foreach (var (material, full) in SlabDoubles)
        {
            var whole = registry.ByName(full).Id;
            yield return (registry.ByName($"{material}_slab_lower").Id, whole);
            yield return (registry.ByName($"{material}_slab_upper").Id, whole);
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
