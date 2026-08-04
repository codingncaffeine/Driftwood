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

    public const int LayerCount = 15;

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
        BlockId Emberstone);

    public static Ids Register(BlockRegistry registry)
    {
        // Id 0 must be air; chunk storage treats a zeroed array as empty.
        registry.Register(new BlockType { Name = "air", Solid = false, Opaque = false });

        var stone = registry.Register(new BlockType
        {
            Name = "stone", TopLayer = LayerStone, SideLayer = LayerStone, BottomLayer = LayerStone,
        });
        var dirt = registry.Register(new BlockType
        {
            Name = "dirt", TopLayer = LayerDirt, SideLayer = LayerDirt, BottomLayer = LayerDirt,
        });
        var grass = registry.Register(new BlockType
        {
            Name = "grass", TopLayer = LayerGrassTop, SideLayer = LayerGrassSide, BottomLayer = LayerDirt,
        });
        var sand = registry.Register(new BlockType
        {
            Name = "sand", TopLayer = LayerSand, SideLayer = LayerSand, BottomLayer = LayerSand,
        });

        // Water is non-solid and non-opaque: you fall through it and it does not hide the
        // sea floor. P0 still draws it in the opaque pass; sorted translucency is a later phase.
        // The attenuation is what makes depth read as depth — light falls off twice as fast under
        // water, so a shallow sandbar stays bright while a trench goes black.
        var water = registry.Register(new BlockType
        {
            Name = "water", Solid = false, Opaque = false, LightAttenuation = 1,
            TopLayer = LayerWater, SideLayer = LayerWater, BottomLayer = LayerWater,
        });

        var gravel = registry.Register(new BlockType
        {
            Name = "gravel", TopLayer = LayerGravel, SideLayer = LayerGravel, BottomLayer = LayerGravel,
        });
        var log = registry.Register(new BlockType
        {
            Name = "oak_log", TopLayer = LayerLogTop, SideLayer = LayerLogSide, BottomLayer = LayerLogTop,
        });

        // Leaves are solid but see-through, which is exactly why the two flags are separate. They
        // dim what passes through rather than stopping it, so a canopy casts shade instead of a
        // hole and the forest floor is darker than the field beside it.
        var leaves = registry.Register(new BlockType
        {
            Name = "oak_leaves", Opaque = false, LightAttenuation = 1,
            TopLayer = LayerLeaves, SideLayer = LayerLeaves, BottomLayer = LayerLeaves,
        });

        var planks = registry.Register(new BlockType
        {
            Name = "oak_planks", TopLayer = LayerPlanks, SideLayer = LayerPlanks, BottomLayer = LayerPlanks,
        });
        var coal = registry.Register(new BlockType
        {
            Name = "coal_ore", TopLayer = LayerCoalOre, SideLayer = LayerCoalOre, BottomLayer = LayerCoalOre,
        });
        var iron = registry.Register(new BlockType
        {
            Name = "iron_ore", TopLayer = LayerIronOre, SideLayer = LayerIronOre, BottomLayer = LayerIronOre,
        });
        var bedrock = registry.Register(new BlockType
        {
            Name = "bedrock", TopLayer = LayerBedrock, SideLayer = LayerBedrock, BottomLayer = LayerBedrock,
        });

        // The one thing in the world that gives off light. Added so lighting has something to
        // prove itself against underground before a placeable torch exists — a light system whose
        // only source is the sun can only ever be tested outdoors, where it is hardest to be wrong
        // in a way anyone notices. Warm and slightly red, so coloured light is visibly coloured.
        var emberstone = registry.Register(new BlockType
        {
            Name = "emberstone",
            LightEmission = LightValue.PackBlock(15, 10, 4),
            TopLayer = LayerEmberstone, SideLayer = LayerEmberstone, BottomLayer = LayerEmberstone,
        });

        return new Ids(
            stone, dirt, grass, sand, water, gravel, log, leaves, planks, coal, iron, bedrock, emberstone);
    }
}
