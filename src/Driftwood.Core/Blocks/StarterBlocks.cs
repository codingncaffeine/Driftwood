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

    public const int LayerCount = 28;

    /// <summary>Debug colours standing in for textures until the art pass. RGB, 0..1.</summary>
    public static readonly float[] PaletteRgb =
    [
        0.52f, 0.52f, 0.54f, // stone
        0.46f, 0.33f, 0.22f, // dirt
        0.36f, 0.60f, 0.26f, // grass top
        0.42f, 0.40f, 0.24f, // grass side
        0.83f, 0.78f, 0.58f, // sand
        0.16f, 0.36f, 0.62f, // water
        0.50f, 0.48f, 0.47f, // gravel
        0.41f, 0.31f, 0.19f, // log side
        0.58f, 0.46f, 0.30f, // log top
        0.24f, 0.47f, 0.20f, // leaves
        0.70f, 0.56f, 0.34f, // planks
        0.28f, 0.28f, 0.29f, // coal ore
        0.66f, 0.56f, 0.46f, // iron ore
        0.12f, 0.12f, 0.13f, // bedrock
        0.94f, 0.62f, 0.30f, // emberstone
        0.20f, 0.42f, 0.16f, // vine
    ];

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
        BlockId Snow)
    {
        /// <summary>Every rock an ore can form in. Ore replaces rock, whichever rock it is.</summary>
        public BlockId[] Rock => [Stone, Deepstone, Coralstone, Driftstone, Saltstone];

        /// <summary>Everything mining is meant to yield, for the census to weigh against rock.</summary>
        public BlockId[] Ores => [CoalOre, IronOre, CopperOre, GoldOre, StormglassOre, AzuriteOre, Emberstone];
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
            Name = "stone", Hardness = 1.5f, NeedsTool = true,
            TopLayer = LayerStone, SideLayer = LayerStone, BottomLayer = LayerStone,
        });
        var dirt = registry.Register(new BlockType
        {
            Name = "dirt", Hardness = 0.5f,
            TopLayer = LayerDirt, SideLayer = LayerDirt, BottomLayer = LayerDirt,
        });
        var grass = registry.Register(new BlockType
        {
            Name = "grass", Hardness = 0.6f, Tint = TintSource.Grass, TintTopOnly = true,
            TopLayer = LayerGrassTop, SideLayer = LayerGrassSide, BottomLayer = LayerDirt,
        });
        var sand = registry.Register(new BlockType
        {
            Name = "sand", Hardness = 0.5f,
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
            Tint = TintSource.Water,
            TopLayer = LayerWater, SideLayer = LayerWater, BottomLayer = LayerWater,
        });

        var gravel = registry.Register(new BlockType
        {
            Name = "gravel", Hardness = 0.6f,
            TopLayer = LayerGravel, SideLayer = LayerGravel, BottomLayer = LayerGravel,
        });
        var log = registry.Register(new BlockType
        {
            Name = "driftoak_log", Hardness = 2f,
            TopLayer = LayerLogTop, SideLayer = LayerLogSide, BottomLayer = LayerLogTop,
        });

        // Leaves are solid but see-through, which is exactly why the two flags are separate. They
        // dim what passes through rather than stopping it, so a canopy casts shade instead of a
        // hole and the forest floor is darker than the field beside it.
        var leaves = registry.Register(new BlockType
        {
            Name = "driftoak_leaves", Hardness = 0.2f, Opaque = false, LightAttenuation = 1,
            Tint = TintSource.Foliage,
            TopLayer = LayerLeaves, SideLayer = LayerLeaves, BottomLayer = LayerLeaves,
        });

        var planks = registry.Register(new BlockType
        {
            Name = "driftoak_planks", Hardness = 2f,
            TopLayer = LayerPlanks, SideLayer = LayerPlanks, BottomLayer = LayerPlanks,
        });
        var coal = registry.Register(new BlockType
        {
            Name = "coal_ore", Hardness = 3f, NeedsTool = true,
            TopLayer = LayerCoalOre, SideLayer = LayerCoalOre, BottomLayer = LayerCoalOre,
        });
        var iron = registry.Register(new BlockType
        {
            Name = "iron_ore", Hardness = 3f, NeedsTool = true,
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
            Name = "emberstone", Hardness = 0.3f,
            LightEmission = LightValue.PackBlock(15, 10, 4),
            TopLayer = LayerEmberstone, SideLayer = LayerEmberstone, BottomLayer = LayerEmberstone,
        });

        // Vines hang off canopy undersides and overhangs. Neither solid nor opaque — you walk
        // through them and they barely shade what is behind — but they still dim light by a level,
        // which is what makes a curtain of them read as a curtain.
        var vine = registry.Register(new BlockType
        {
            Name = "vine", Hardness = 0.2f, Solid = false, Opaque = false, LightAttenuation = 1,
            Tint = TintSource.Foliage,
            TopLayer = LayerVine, SideLayer = LayerVine, BottomLayer = LayerVine,
        });

        // Rock below the metals' reach. Harder than stone, and it is what makes going deep read as
        // going somewhere rather than as more of the same grey.
        var deepstone = registry.Register(new BlockType
        {
            Name = "deepstone", Hardness = 3f, NeedsTool = true,
            TopLayer = LayerDeepstone, SideLayer = LayerDeepstone, BottomLayer = LayerDeepstone,
        });

        // Three intrusions through the stone. Our own names for them, in the same register as
        // emberstone: plain compound nouns that say what the rock looks like, in the game's own
        // coastal vocabulary. One uniform grey underground reads as a texture rather than as
        // geology, and a pack has art for three kinds of rock whatever anyone calls them.
        var coralstone = registry.Register(new BlockType
        {
            Name = "coralstone", Hardness = 1.5f, NeedsTool = true,
            TopLayer = LayerCoralstone, SideLayer = LayerCoralstone, BottomLayer = LayerCoralstone,
        });
        var driftstone = registry.Register(new BlockType
        {
            Name = "driftstone", Hardness = 1.5f, NeedsTool = true,
            TopLayer = LayerDriftstone, SideLayer = LayerDriftstone, BottomLayer = LayerDriftstone,
        });
        var saltstone = registry.Register(new BlockType
        {
            Name = "saltstone", Hardness = 1.5f, NeedsTool = true,
            TopLayer = LayerSaltstone, SideLayer = LayerSaltstone, BottomLayer = LayerSaltstone,
        });

        // The rest of the ore ladder. Real metals keep their real names — nobody owns the word
        // copper, and a player knows what gold is worth on sight. The top tier is ours: stormglass,
        // a cold gem found only at the floor of the world.
        var copper = registry.Register(new BlockType
        {
            Name = "copper_ore", Hardness = 3f, NeedsTool = true,
            TopLayer = LayerCopperOre, SideLayer = LayerCopperOre, BottomLayer = LayerCopperOre,
        });
        var gold = registry.Register(new BlockType
        {
            Name = "gold_ore", Hardness = 3f, NeedsTool = true,
            TopLayer = LayerGoldOre, SideLayer = LayerGoldOre, BottomLayer = LayerGoldOre,
        });
        var stormglass = registry.Register(new BlockType
        {
            Name = "stormglass_ore", Hardness = 3f, NeedsTool = true,
            TopLayer = LayerStormglassOre, SideLayer = LayerStormglassOre, BottomLayer = LayerStormglassOre,
        });

        // Azurite is a real blue copper mineral, and ours rather than anybody's coined name.
        var azurite = registry.Register(new BlockType
        {
            Name = "azurite_ore", Hardness = 3f, NeedsTool = true,
            TopLayer = LayerAzuriteOre, SideLayer = LayerAzuriteOre, BottomLayer = LayerAzuriteOre,
        });

        var clay = registry.Register(new BlockType
        {
            Name = "clay", Hardness = 0.6f,
            TopLayer = LayerClay, SideLayer = LayerClay, BottomLayer = LayerClay,
        });

        // Under every beach. The top face is its own texture because a stratified side and a
        // stratified top would put the bands on edge when seen from above.
        var sandstone = registry.Register(new BlockType
        {
            Name = "sandstone", Hardness = 0.8f, NeedsTool = true,
            TopLayer = LayerSandstoneTop, SideLayer = LayerSandstone, BottomLayer = LayerSandstoneTop,
        });

        // Lies on cold ground and on high ground. Soft — it is the one thing in the world that
        // comes away in a single blow.
        var snow = registry.Register(new BlockType
        {
            Name = "snow", Hardness = 0.2f,
            TopLayer = LayerSnow, SideLayer = LayerSnow, BottomLayer = LayerSnow,
        });

        return new Ids(
            stone, dirt, grass, sand, water, gravel, log, leaves, planks, coal, iron, bedrock,
            emberstone, vine, deepstone, coralstone, driftstone, saltstone, copper, gold, stormglass,
            azurite, clay, sandstone, snow);
    }
}
