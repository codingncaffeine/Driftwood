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

    public const int LayerCount = 33;

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
        BlockId Marshlily)
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
            Name = "stone", Hardness = 1.5f, NeedsTool = true,
            TopLayer = LayerStone, SideLayer = LayerStone, BottomLayer = LayerStone,
        });
        var dirt = registry.Register(new BlockType
        {
            Name = "dirt", Hardness = 0.5f,
            TopLayer = LayerDirt, SideLayer = LayerDirt, BottomLayer = LayerDirt,
        });
        // The one block that was never really a cube. Its top is the climate colour over a grey
        // tile, its bottom is plain dirt, and the green rolling over its sides is a second cut-out
        // laid over the first and tinted the same as the top. Before models there was nowhere to
        // put that second pass, so grass sides came out as bare dirt in every pack ever imported.
        var grass = registry.Register(new BlockType
        {
            Name = "grass", Hardness = 0.6f, Tint = TintSource.Grass,
            Model = BlockModel.CubeWithSideOverlay(
                LayerGrassTop, LayerGrassSide, LayerDirt, LayerGrassSideOverlay),
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
            Name = "driftoak_planks", Hardness = 2f, Crafted = true,
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
        //
        // Crossed planes rather than a cube. Ours hang in open air below a crown rather than
        // clinging to a wall, and a cube of vine texture floating under a tree reads as a mistake
        // from every angle — six faces of holes with nothing inside them.
        var vine = registry.Register(new BlockType
        {
            Name = "vine", Hardness = 0.2f, Solid = false, Opaque = false, LightAttenuation = 1,
            Tint = TintSource.Foliage,
            Model = BlockModel.Cross(LayerVine),
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

        // The first fall of snow, a fifth of a block deep, lying over whatever it settled on. It is
        // what turns the snow line from a drawn edge into a fade: a band of ground still green
        // under a dusting, between the meadow and the snowfield proper.
        var snowLayer = registry.Register(new BlockType
        {
            Name = "snow_layer", Hardness = 0.1f, Solid = false, Opaque = false,
            Model = BlockModel.Layer(LayerSnow, LayerSnow, LayerSnow, 3f),
        });

        // The first block in the world with a shape rather than a size. Two planes crossed through
        // the middle of the cell, no collision, no occlusion, and lit flat so both halves match —
        // the model format's own answer for every small plant, and the thing that proves the
        // per-block path works against something a player can walk through.
        var meadowgrass = registry.Register(new BlockType
        {
            Name = "meadowgrass", Hardness = 0.05f, Solid = false, Opaque = false,
            Tint = TintSource.Grass,
            Model = BlockModel.Cross(LayerMeadowgrass),
        });

        // Flowers take no climate colour. A pack paints grass and leaves near-grey expecting the
        // multiply, and paints a flower the colour the flower is — running one through the grass
        // colormap turns every bloom in the world the same green as the field it stands in.
        var seaflax = registry.Register(new BlockType
        {
            Name = "seaflax", Hardness = 0.05f, Solid = false, Opaque = false,
            Model = BlockModel.Cross(LayerSeaflax, tinted: false),
        });
        var marshlily = registry.Register(new BlockType
        {
            Name = "marshlily", Hardness = 0.05f, Solid = false, Opaque = false,
            Model = BlockModel.Cross(LayerMarshlily, tinted: false),
        });

        // The built shapes. Every orientation is its own block, because there is nowhere else to
        // keep one — a cell holds an id and nothing beside it. That is also how the genre stores it
        // underneath, and it means the mesher never asks which way a stair faces: the id says.
        RegisterShapes(registry);

        // Standing on the floor, and the first light a player can carry into a cave. Warm and
        // slightly red, so a torch-lit tunnel does not read as daylight underground.
        registry.Register(new BlockType
        {
            Name = "torch", Hardness = 0.05f, Solid = false, Opaque = false, Crafted = true,
            LightEmission = LightValue.PackBlock(14, 10, 5),
            Model = BlockModel.Torch(LayerTorch),
        });

        return new Ids(
            stone, dirt, grass, sand, water, gravel, log, leaves, planks, coal, iron, bedrock,
            emberstone, vine, deepstone, coralstone, driftstone, saltstone, copper, gold, stormglass,
            azurite, clay, sandstone, snow, snowLayer, meadowgrass, seaflax, marshlily);
    }

    /// <summary>The materials that come in slab and stair form, and the tiles each wears.</summary>
    private static readonly (string Name, ushort Top, ushort Side, ushort Bottom)[] ShapedMaterials =
    [
        ("driftoak", LayerPlanks, LayerPlanks, LayerPlanks),
        ("stone", LayerStone, LayerStone, LayerStone),
    ];

    /// <summary>Facing names in <see cref="Placeable.Facings"/> order: +x, -x, +z, -z.</summary>
    private static readonly string[] FacingNames = ["east", "west", "south", "north"];

    private static void RegisterShapes(BlockRegistry registry)
    {
        foreach (var (name, top, side, bottom) in ShapedMaterials)
        {
            foreach (var upper in (bool[])[false, true])
            {
                registry.Register(new BlockType
                {
                    Name = $"{name}_slab_{(upper ? "upper" : "lower")}",
                    Hardness = 2f, Opaque = false, Crafted = true,
                    Model = BlockModel.Slab(top, side, bottom, upper),
                });
            }

            for (var i = 0; i < Placeable.Facings.Length; i++)
            foreach (var upper in (bool[])[false, true])
            {
                registry.Register(new BlockType
                {
                    Name = $"{name}_stairs_{FacingNames[i]}_{(upper ? "upper" : "lower")}",
                    Hardness = 2f, Opaque = false, Crafted = true,
                    Model = BlockModel.Stairs(top, side, bottom, Placeable.Facings[i], upper),
                });
            }
        }
    }

    /// <summary>
    /// What a player can hold and put down, in the order a picker walks through it.
    /// </summary>
    /// <remarks>
    /// Built by name out of the registry rather than threaded through <see cref="Ids"/>. Twenty-odd
    /// stair orientations have no business in a record every caller has to carry, and the names are
    /// generated a few lines above from the same two tables — so a material added there appears in
    /// the hand without anything here changing.
    /// </remarks>
    public static Placeable[] Hand(BlockRegistry registry)
    {
        var hand = new List<Placeable>
        {
            new() { Label = "driftoak planks", Kind = PlacementKind.Plain, Variants = [registry.ByName("driftoak_planks").Id] },
        };

        foreach (var (name, _, _, _) in ShapedMaterials)
        {
            hand.Add(new Placeable
            {
                Label = $"{name} slab",
                Kind = PlacementKind.Halved,
                Variants =
                [
                    registry.ByName($"{name}_slab_lower").Id,
                    registry.ByName($"{name}_slab_upper").Id,
                ],
            });

            var stairs = new BlockId[Placeable.Facings.Length * 2];
            for (var i = 0; i < Placeable.Facings.Length; i++)
            {
                stairs[i * 2] = registry.ByName($"{name}_stairs_{FacingNames[i]}_lower").Id;
                stairs[i * 2 + 1] = registry.ByName($"{name}_stairs_{FacingNames[i]}_upper").Id;
            }

            hand.Add(new Placeable { Label = $"{name} stairs", Kind = PlacementKind.Stairs, Variants = stairs });
        }

        hand.Add(new Placeable { Label = "torch", Kind = PlacementKind.Standing, Variants = [registry.ByName("torch").Id] });

        foreach (var plain in (string[])["stone", "dirt", "sand", "meadowgrass"])
            hand.Add(new Placeable { Label = plain, Kind = PlacementKind.Plain, Variants = [registry.ByName(plain).Id] });

        return [.. hand];
    }
}
