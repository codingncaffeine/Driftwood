using Driftwood.Core.Blocks;

namespace Driftwood.Core.Items;

/// <summary>
/// Everything a player can carry, and what each block leaves when it comes apart.
/// </summary>
/// <remarks>
/// <para>Registered after the blocks, because an item that puts a block down needs the block's id.
/// The dependency only runs one way, which is what lets the whole item layer be added without a
/// single edit to worldgen, meshing or lighting.</para>
/// <para>Tools are generated from two tables rather than written out twenty times. Five tiers of
/// four heads is the same template-times-axes shape the block set already uses for slabs and
/// stairs, and it is the only way a content phase of this size stays a table instead of a
/// chapter — a sixth tier is one row here and one row in the recipe set.</para>
/// </remarks>
public static class StarterItems
{
    /// <summary>One rung of the tool ladder: what its heads are made of and how they behave.</summary>
    /// <param name="Tier">
    /// The hardest <see cref="BlockType.HarvestTier"/> this rung will bring up.
    /// </param>
    /// <param name="Speed">How much faster than a bare hand it works at its own class.</param>
    /// <param name="Material">
    /// What its heads are cut from — an item's name, or a tag's when more than one thing will do.
    /// </param>
    /// <param name="Palette">Which row of tool icons this tier wears.</param>
    public readonly record struct ToolTier(
        string Name, string Material, int Tier, float Speed, int Durability, ushort Palette);

    /// <summary>
    /// The ladder. Gold is the rung that proves tier and speed are two axes rather than one.
    /// </summary>
    /// <remarks>
    /// It cuts faster than iron and will not bring up a stormglass, and it wears out in a fifth of
    /// the time — so it is a choice a player makes for a particular afternoon's digging rather than
    /// a rung on the way up. A ladder where every step is strictly better is a ladder with no
    /// decisions on it.
    /// </remarks>
    public static readonly ToolTier[] Tiers =
    [
        new("wood", "#planks", 1, 2f, 60, 0),
        new("stone", "#rough_stone", 2, 4f, 132, 1),
        new("copper", "copper_ingot", 3, 6f, 190, 2),
        new("gold", "gold_ingot", 2, 12f, 33, 3),
        new("iron", "iron_ingot", 4, 8f, 251, 4),
        new("stormglass", "stormglass", 5, 10f, 1562, 5),
    ];

    /// <summary>The four heads a tier comes in, in <see cref="Textures.TileGen.ToolShapes"/> order.</summary>
    public static readonly (string Name, ToolClass Class)[] Heads =
    [
        ("pickaxe", ToolClass.Pickaxe),
        ("axe", ToolClass.Axe),
        ("shovel", ToolClass.Shovel),
        ("sword", ToolClass.Sword),
    ];

    /// <summary>Seconds of burn one piece of timber is worth — one and a half smelts.</summary>
    public const float Timber = 15f;

    public static ItemRegistry Register(BlockRegistry blocks)
    {
        var items = new ItemRegistry();

        // Blocks you can hold. Every entry is one item however many block ids it covers — a stair
        // is eight orientations and one thing to carry, which is the whole reason items got an id
        // space of their own.
        // Anything made of timber burns, which is what stops a player who spawned in a forest with
        // no cave nearby from being unable to light their first furnace at all.
        Block(items, blocks, "driftoak_log", "driftoak log", StarterBlocks.LayerLogSide, Timber);
        Block(items, blocks, "driftoak_planks", "driftoak planks", StarterBlocks.LayerPlanks, Timber);
        Block(items, blocks, "stone", "stone", StarterBlocks.LayerStone);
        Block(items, blocks, "rubble", "rubble", StarterBlocks.LayerRubble);
        Block(items, blocks, "dirt", "dirt", StarterBlocks.LayerDirt);
        Block(items, blocks, "sand", "sand", StarterBlocks.LayerSand);
        Block(items, blocks, "gravel", "gravel", StarterBlocks.LayerGravel);
        Block(items, blocks, "clay", "clay", StarterBlocks.LayerClay);
        Block(items, blocks, "sandstone", "sandstone", StarterBlocks.LayerSandstone);
        Block(items, blocks, "snow", "snow", StarterBlocks.LayerSnow);
        Block(items, blocks, "deepstone", "deepstone", StarterBlocks.LayerDeepstone);
        Block(items, blocks, "coralstone", "coralstone", StarterBlocks.LayerCoralstone);
        Block(items, blocks, "driftstone", "driftstone", StarterBlocks.LayerDriftstone);
        Block(items, blocks, "saltstone", "saltstone", StarterBlocks.LayerSaltstone);
        Block(items, blocks, "emberstone", "emberstone", StarterBlocks.LayerEmberstone);
        Block(items, blocks, "glass", "glass", StarterBlocks.LayerGlass);
        Block(items, blocks, "bricks", "bricks", StarterBlocks.LayerBricks);
        Block(items, blocks, "bench", "bench", StarterBlocks.LayerBenchTop, Timber);
        Block(items, blocks, "stonecutter", "stonecutter", StarterBlocks.LayerStonecutterTop);

        // Every worked form of every rock, off the same table the blocks came from. Nine lines of
        // nothing, which is what a family is supposed to cost by the time it reaches here.
        foreach (var cut in StarterBlocks.CutStoneNames)
            Block(items, blocks, cut, cut.Replace('_', ' '), blocks.ByName(cut).Model.ParticleLayer);

        // Slabs and stairs, straight off the same table the blocks came from.
        foreach (var material in StarterBlocks.ShapedNames)
        {
            var layer = blocks.ByName($"{material}_slab_lower").Model.ParticleLayer;

            items.Register(new ItemType
            {
                Name = $"{material}_slab", Label = $"{material} slab", IconLayer = layer,
                DrawsAsCube = true,
                Places = new Placeable
                {
                    Label = $"{material} slab",
                    Kind = PlacementKind.Halved,
                    Variants = StarterBlocks.Slabs(blocks, material),
                },
            });

            items.Register(new ItemType
            {
                Name = $"{material}_stairs", Label = $"{material} stairs", IconLayer = layer,
                DrawsAsCube = true,
                Places = new Placeable
                {
                    Label = $"{material} stairs",
                    Kind = PlacementKind.Stairs,
                    Variants = StarterBlocks.Stairs(blocks, material),
                },
            });
        }

        // Things that join up with what is beside them. All sixteen forms are listed as variants
        // even though only the first is ever placed: that is what maps every one of them back to
        // this item, so a fence broken out of the middle of a run still comes up as a fence.
        foreach (var material in StarterBlocks.ConnectedNames)
        {
            var bare = blocks.ByName($"{material}_0");

            items.Register(new ItemType
            {
                Name = material,
                Label = material.Replace('_', ' '),
                IconLayer = bare.Model.ParticleLayer,
                Places = new Placeable
                {
                    Label = material.Replace('_', ' '),
                    Kind = PlacementKind.Plain,
                    Variants = StarterBlocks.Connected(blocks, material),
                },
            });
        }

        // A torch is flat art on crossed planes, so a cube of it is a cube of black. Declared
        // rather than derived from "does it place a block", because it places one and is not one.
        // Five forms out of one item: on the floor, or leaning off whichever wall was aimed at.
        items.Register(new ItemType
        {
            Name = "torch", Label = "torch", IconLayer = StarterBlocks.LayerTorch,
            Places = new Placeable
            {
                Label = "torch",
                Kind = PlacementKind.Attached,
                Variants = StarterBlocks.Torches(blocks),
            },
        });

        // Standing on what is under it, or hanging from what is over it, off one item.
        items.Register(new ItemType
        {
            Name = "lantern", Label = "lantern", IconLayer = StarterBlocks.LayerLantern,
            Places = new Placeable
            {
                Label = "lantern",
                Kind = PlacementKind.Hung,
                Variants = [blocks.ByName("lantern").Id, blocks.ByName("lantern_hanging").Id],
            },
        });

        // Put down alight, the way a fire somebody just built would be. The four unlit forms leave
        // this same item, below.
        items.Register(new ItemType
        {
            Name = "campfire", Label = "campfire", IconLayer = StarterBlocks.LayerCampfireFire,
            Places = new Placeable
            {
                Label = "campfire",
                Kind = PlacementKind.Axis,
                Variants = StarterBlocks.Campfires(blocks, lit: true),
            },
        });

        Block(items, blocks, "smokeglass", "smokeglass", StarterBlocks.LayerSmokeglass);
        Block(items, blocks, "stormglass_lamp", "stormglass lamp", StarterBlocks.LayerStormglassLamp);

        // Things that open. A ladder goes on a wall and nowhere else; a trapdoor takes a facing and
        // a half exactly as a stair does; a door is the one thing in the game that puts down two
        // blocks, and its upper halves are deliberately not listed as forms this places — nothing
        // maps them back to an item, which is what stops one door coming apart into two.
        items.Register(new ItemType
        {
            Name = "ladder", Label = "ladder", IconLayer = StarterBlocks.LayerLadder,
            BurnSeconds = Timber,
            Places = new Placeable
            {
                Label = "ladder",
                Kind = PlacementKind.Wall,
                Variants = StarterBlocks.Ladders(blocks),
            },
        });

        items.Register(new ItemType
        {
            Name = "trapdoor", Label = "trapdoor", IconLayer = StarterBlocks.LayerTrapdoor,
            BurnSeconds = Timber,
            Places = new Placeable
            {
                Label = "trapdoor",
                Kind = PlacementKind.Trapdoor,
                Variants = StarterBlocks.Trapdoors(blocks),
            },
        });

        items.Register(new ItemType
        {
            Name = "door", Label = "door", IconLayer = StarterBlocks.LayerDoorUpper,
            BurnSeconds = Timber,
            Places = new Placeable
            {
                Label = "door",
                Kind = PlacementKind.Door,
                Variants = StarterBlocks.Doors(blocks),
            },
        });

        items.Register(new ItemType
        {
            Name = "chest", Label = "chest", IconLayer = StarterBlocks.LayerChestFront,
            BurnSeconds = Timber,
            Places = new Placeable
            {
                Label = "chest",
                Kind = PlacementKind.Facing,
                Variants = StarterBlocks.Chests(blocks),
            },
        });

        items.Register(new ItemType
        {
            Name = "furnace", Label = "furnace", IconLayer = StarterBlocks.LayerFurnaceFront,
            DrawsAsCube = true,
            Places = new Placeable
            {
                Label = "furnace",
                Kind = PlacementKind.Facing,
                Variants = StarterBlocks.Furnaces(blocks, lit: false),
            },
        });

        // Loose things: nothing puts these down, and half the recipe tree is made of them.
        Loose(items, "stick", "stick", StarterBlocks.LayerStick, burn: 5f);
        Loose(items, "coal", "coal", StarterBlocks.LayerCoal, burn: 80f);
        Loose(items, "charcoal", "charcoal", StarterBlocks.LayerCharcoal, burn: 80f);
        Loose(items, "raw_copper", "raw copper", StarterBlocks.LayerRawCopper);
        Loose(items, "raw_iron", "raw iron", StarterBlocks.LayerRawIron);
        Loose(items, "raw_gold", "raw gold", StarterBlocks.LayerRawGold);
        Loose(items, "copper_ingot", "copper ingot", StarterBlocks.LayerCopperIngot);
        Loose(items, "iron_ingot", "iron ingot", StarterBlocks.LayerIronIngot);
        Loose(items, "gold_ingot", "gold ingot", StarterBlocks.LayerGoldIngot);
        Loose(items, "stormglass", "stormglass", StarterBlocks.LayerStormglass);
        Loose(items, "azurite", "azurite", StarterBlocks.LayerAzurite);
        Loose(items, "clay_lump", "clay lump", StarterBlocks.LayerClayLump);
        Loose(items, "brick", "brick", StarterBlocks.LayerBrick);

        RegisterTools(items);

        return items.Seal(blocks);
    }

    private static void RegisterTools(ItemRegistry items)
    {
        for (var tier = 0; tier < Tiers.Length; tier++)
        for (var head = 0; head < Heads.Length; head++)
        {
            var t = Tiers[tier];
            var (headName, headClass) = Heads[head];

            items.Register(new ItemType
            {
                Name = $"{t.Name}_{headName}",
                Label = $"{t.Name} {headName}",
                IconLayer = (ushort)(StarterBlocks.LayerFirstTool + t.Palette * StarterBlocks.ToolShapeCount + head),
                MaxStack = 1,
                Tool = headClass,

                // A sword harvests nothing, so it carries no tier. Giving it one would make it a
                // pickaxe that happens to be shaped differently.
                Tier = headClass == ToolClass.Sword ? 0 : t.Tier,
                MiningSpeed = headClass == ToolClass.Sword ? 1f : t.Speed,
                Durability = t.Durability,
            });
        }
    }

    /// <summary>
    /// What each block leaves. Only the interesting cases: everything else leaves what puts it back.
    /// </summary>
    /// <remarks>
    /// Three shapes of rule live here. A block that becomes something else — stone into rubble, an
    /// ore into a lump of raw metal. A block that leaves several — clay into four lumps. And a block
    /// that leaves nothing, which is every kind of foliage, the grass that grows on top of the dirt,
    /// and the burning form of a furnace, which is the same furnace and must not be two of them.
    /// </remarks>
    public static BlockDrops Drops(BlockRegistry blocks, ItemRegistry items)
    {
        var rules = new List<BlockDrops.Rule>(Written(blocks, items));

        // ⚠ Half a door leaves nothing, and a door in either state leaves one door. Generated
        // rather than written out thirty-two times, because a table with thirty-two near-identical
        // rows in it is a table with one row wrong in it — and the two failures are a door that
        // comes apart into two doors, and one that opens and can then never be picked up.
        foreach (var name in StarterBlocks.UpperHalves) rules.Add(new BlockDrops.Rule(name, null));
        foreach (var name in StarterBlocks.OpenLowerHalves) rules.Add(new BlockDrops.Rule(name, "door"));
        foreach (var name in StarterBlocks.OpenTrapdoors) rules.Add(new BlockDrops.Rule(name, "trapdoor"));

        return new BlockDrops(blocks, items, [.. rules]);
    }

    private static BlockDrops.Rule[] Written(BlockRegistry blocks, ItemRegistry items) =>
    [
        new BlockDrops.Rule("stone", "rubble"),
        new BlockDrops.Rule("grass", "dirt"),
        new BlockDrops.Rule("clay", "clay_lump", 4),

        new BlockDrops.Rule("coal_ore", "coal"),
        new BlockDrops.Rule("copper_ore", "raw_copper", 2),
        new BlockDrops.Rule("iron_ore", "raw_iron"),
        new BlockDrops.Rule("gold_ore", "raw_gold"),
        new BlockDrops.Rule("stormglass_ore", "stormglass"),
        new BlockDrops.Rule("azurite_ore", "azurite", 4),

        new BlockDrops.Rule("driftoak_leaves", null),
        new BlockDrops.Rule("vine", null),
        new BlockDrops.Rule("meadowgrass", null),
        new BlockDrops.Rule("seaflax", null),
        new BlockDrops.Rule("marshlily", null),
        new BlockDrops.Rule("snow_layer", null),
        new BlockDrops.Rule("water", null),
        new BlockDrops.Rule("bedrock", null),

        // A burning furnace is a furnace. Without these four rules the lit forms leave nothing,
        // and a player who mines one mid-smelt loses it.
        new BlockDrops.Rule("furnace_east_lit", "furnace"),
        new BlockDrops.Rule("furnace_west_lit", "furnace"),
        new BlockDrops.Rule("furnace_south_lit", "furnace"),
        new BlockDrops.Rule("furnace_north_lit", "furnace"),

        // And a fire that has gone out is still a campfire. The item places the lit pair, so it is
        // the other two that need saying — the same shape of rule, from the other side.
        new BlockDrops.Rule("campfire_x", "campfire"),
        new BlockDrops.Rule("campfire_z", "campfire"),
    ];

    private static void Block(
        ItemRegistry items, BlockRegistry blocks, string name, string label, ushort icon,
        float burn = 0f) =>
        items.Register(new ItemType
        {
            Name = name,
            Label = label,
            IconLayer = icon,
            DrawsAsCube = true,
            BurnSeconds = burn,
            Places = new Placeable
            {
                Label = label,
                Kind = PlacementKind.Plain,
                Variants = [blocks.ByName(name).Id],
            },
        });

    private static void Loose(
        ItemRegistry items, string name, string label, ushort icon, float burn = 0f) =>
        items.Register(new ItemType
        {
            Name = name, Label = label, IconLayer = icon, BurnSeconds = burn,
        });
}
