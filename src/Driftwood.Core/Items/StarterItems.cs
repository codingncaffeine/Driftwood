using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Textures;

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

        // ⛳ The top, and it gates NOTHING — diamond ore itself wants a stormglass pickaxe, and
        // there is no tier 6 block. That is what a top tool is once the ladder runs out: speed and
        // durability. Half again as quick as stormglass and it lasts more than twice as long, which
        // is what makes the trip to the lava worth making after you already have the gem.
        new("diamond", "diamond", 6, 14f, 3400, 6),
    ];

    /// <summary>The four heads a tier comes in, in <see cref="Textures.TileGen.ToolShapes"/> order.</summary>
    public static readonly (string Name, ToolClass Class)[] Heads =
    [
        ("pickaxe", ToolClass.Pickaxe),
        ("axe", ToolClass.Axe),
        ("shovel", ToolClass.Shovel),
        ("sword", ToolClass.Sword),
    ];

    // ⛔⛔ THE HOE IS NOT A FIFTH HEAD, AND THAT IS A DECISION ABOUT THE TABLE ABOVE. Adding one
    // would change ToolShapeCount, which LayerFirstFluid is computed from — so every layer after the
    // tools renumbers, while the twenty-eight tool rows in BlockTextureSet.Layers are written out by
    // hand and would not move with them. The count and the table would disagree silently: the exact
    // failure the dyes had, where every texture check passed and a pack painted a pickaxe onto a dye.
    //
    // ⛳ And one hoe is the better design anyway. The ladder exists because a pickaxe of the wrong
    // tier brings up NOTHING — that is what a tier means here. A hoe has no such gate: it turns dirt
    // over, and turning dirt over faster is not a thing anybody wants. Seven of them would be seven
    // recipes, seven icons and seven identical outcomes. It is one item, appended like any other.

    /// <summary>Seconds of burn one piece of timber is worth — one and a half smelts.</summary>
    public const float Timber = 15f;

    /// <summary>
    /// One meat: which animal it comes off, what it is called, how it is drawn, and its colour.
    /// </summary>
    /// <param name="Raw">Half-hearts eating it raw puts back.</param>
    /// <param name="Cooked">And cooked, which is always more — that is the whole reason to cook it.</param>
    public readonly record struct Meat(
        string Animal, string Name, TileGen.MeatShape Shape,
        byte R, byte G, byte B, int Raw, int Cooked);

    /// <summary>
    /// The four, in the order their icon layers run.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Real names, per the naming rule.</b> Beef, pork, mutton and chicken are words nobody
    /// owns and renaming them would cost legibility for nothing — the same call that left copper,
    /// iron and clay alone while coining coralstone and stormglass.
    /// </remarks>
    public static readonly Meat[] Meats =
    [
        new("cow", "beef", TileGen.MeatShape.Cut, 188, 66, 60, 2, 6),
        new("pig", "pork", TileGen.MeatShape.Chop, 226, 140, 132, 2, 6),
        new("sheep", "mutton", TileGen.MeatShape.Cut, 170, 56, 54, 2, 6),
        new("chicken", "chicken", TileGen.MeatShape.Leg, 232, 176, 148, 1, 5),
    ];

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
        Block(items, blocks, "coal_block", "block of coal", StarterBlocks.LayerCoalBlock);

        for (var m = 0; m < StarterBlocks.MetalBlockCount; m++)
            Block(items, blocks,
                StarterBlocks.MetalBlocks[m].Name, StarterBlocks.MetalBlocks[m].Label,
                (ushort)(StarterBlocks.LayerFirstMetalBlock + m));

        // ⛳ The flowers, which are worth carrying now that they are worth crushing. All four:
        // until the dye tree there was nothing to do with one, so picking a flower left nothing at
        // all — which is the right answer for grass and the wrong one for the thing a whole colour
        // axis is made of.
        Block(items, blocks, "seaflax", "seaflax", StarterBlocks.LayerSeaflax);
        Block(items, blocks, "marshlily", "marshlily", StarterBlocks.LayerMarshlily);
        Block(items, blocks, "emberbloom", "emberbloom", StarterBlocks.LayerEmberbloom);
        Block(items, blocks, "sunwort", "sunwort", StarterBlocks.LayerSunwort);

        // ⛳ THE WIDEST AXIS IN THE GAME: sixteen wools, sixteen carpets, sixteen powders, all off
        // one table of colours. Forty-eight items in twelve lines, which is what a content axis is
        // supposed to cost by the time it reaches here.
        for (var i = 0; i < StarterBlocks.Colours.Length; i++)
        {
            var dye = StarterBlocks.Colours[i];
            var shown = dye.Name.Replace('_', ' ');

            Block(items, blocks, $"wool_{dye.Name}", $"{shown} wool", (ushort)(StarterBlocks.LayerFirstWool + i));
            Block(items, blocks, $"carpet_{dye.Name}", $"{shown} carpet", (ushort)(StarterBlocks.LayerFirstWool + i));

            Loose(items, $"dye_{dye.Name}", $"{shown} dye", (ushort)(StarterBlocks.LayerFirstDye + i));

            // ⛳ And the same colour in glass. The PANE needs no line here — a family that joins up
            // with its neighbours gets its item from the ConnectedNames loop below, which is why
            // sixteen pane families cost nothing at all in this file.
            Block(
                items, blocks, $"stained_glass_{dye.Name}", $"{shown} glass",
                (ushort)(StarterBlocks.LayerFirstStainedGlass + i));
        }
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
                DrawsAsBlock = true,
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
                DrawsAsBlock = true,
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
                DrawsAsBlock = true,
                Places = new Placeable
                {
                    Label = material.Replace('_', ' '),
                    Kind = PlacementKind.Plain,
                    Variants = StarterBlocks.Connected(blocks, material),
                },
            });
        }

        // ⚠ A torch is flat art on crossed planes, so a solid of it is a solid of black. Declared
        // rather than derived from "does it place a block", because it places one and is not one.
        // The same goes for a ladder below: both are a cut-out on a sheet, and a sheet drawn as a
        // block is three copies of a shape with holes in it. Everything else that puts a block down
        // is drawn as one.
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
            DrawsAsBlock = true,
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
            DrawsAsBlock = true,
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
            BurnSeconds = Timber, DrawsAsBlock = true,
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
            BurnSeconds = Timber, DrawsAsBlock = true,
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
            BurnSeconds = Timber, DrawsAsBlock = true,
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
            DrawsAsBlock = true,
            Places = new Placeable
            {
                Label = "furnace",
                Kind = PlacementKind.Facing,
                Variants = StarterBlocks.Furnaces(blocks, lit: false),
            },
        });

        items.Register(new ItemType
        {
            Name = "blast_furnace", Label = "blast furnace", IconLayer = StarterBlocks.LayerBlastFront,
            DrawsAsBlock = true,
            Places = new Placeable
            {
                Label = "blast furnace",
                Kind = PlacementKind.Facing,
                Variants = StarterBlocks.BlastFurnaces(blocks, lit: false),
            },
        });

        items.Register(new ItemType
        {
            Name = "smoker", Label = "smoker", IconLayer = StarterBlocks.LayerSmokerFront,
            DrawsAsBlock = true,
            Places = new Placeable
            {
                Label = "smoker",
                Kind = PlacementKind.Facing,
                Variants = StarterBlocks.Smokers(blocks, lit: false),
            },
        });

        // ⛳ A barrel takes no facing at all, which is the one way it differs from every station
        // registered above it: a chest and three smelters all have a front, and a barrel opens
        // upward, so there is nothing to point.
        Block(items, blocks, "barrel", "barrel", StarterBlocks.LayerBarrelSide);

        // ⛳ The composter always goes down empty — its fill stages are what it BECOMES, and an
        // item that placed a half-full one would be free compost. Every stage still resolves to
        // this one item when mined, which is what Variants is for.
        items.Register(new ItemType
        {
            Name = "composter", Label = "composter", IconLayer = StarterBlocks.LayerComposterSide,
            DrawsAsBlock = true, BurnSeconds = Timber,
            Places = new Placeable
            {
                Label = "composter",
                Kind = PlacementKind.Plain,
                Variants = StarterBlocks.Composters(blocks),
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
        Loose(items, "diamond", "diamond", StarterBlocks.LayerDiamond);
        Loose(items, "azurite", "azurite", StarterBlocks.LayerAzurite);
        // ⛳ The anvil, which always goes down new: the two worn forms are what it BECOMES, and an
        // item that could place a chipped one would let a player launder wear by picking it up.
        items.Register(new ItemType
        {
            Name = "anvil", Label = "anvil", IconLayer = StarterBlocks.LayerAnvilTop,
            DrawsAsBlock = true, MaxStack = 16,
            Places = new Placeable
            {
                Label = "anvil",
                Kind = PlacementKind.Axis,
                Variants =
                [
                    blocks.ByName(StarterBlocks.AnvilName(0, true)).Id,
                    blocks.ByName(StarterBlocks.AnvilName(0, false)).Id,
                ],
            },
        });

        // ⚠ Farmland places nothing. It is MADE by turning ground over with a hoe and never carried,
        // which is why it has an item at all: something has to come back when it is dug up, and what
        // comes back is dirt. Its rule is in BlockDrops, not here.
        // ⚠ ONE hoe, and its durability is iron's because that is what it is made of. It carries no
        // Tier and no MiningSpeed: a hoe brings up nothing and digs at the speed of a hand, so both
        // of those would be numbers that never get read. See the note on StarterItems.Heads.
        items.Register(new ItemType
        {
            Name = "hoe", Label = "hoe", IconLayer = StarterBlocks.LayerHoe,
            Tool = ToolClass.Hoe, Durability = 251, MaxStack = 1,
        });

        Loose(items, "seeds", "seeds", StarterBlocks.LayerSeeds);
        Loose(items, "wheat", "wheat", StarterBlocks.LayerWheatItem);
        Loose(items, "bonemeal", "bone meal", StarterBlocks.LayerBonemeal);

        // ⚠ Six half-hearts, which is a cooked steak. Bread costs three wheat and a field, and a
        // steak costs an animal and a fire — two routes to the same meal is the point of farming.
        items.Register(new ItemType
        {
            Name = "bread", Label = "bread", IconLayer = StarterBlocks.LayerBread, Feeds = 6,
        });

        // ⛳ The three that are eaten as they come up, and each of which is also its own seed. They
        // feed less than bread on purpose: bread is three wheat and a bench, and something you can
        // pull up and eat where you stand should not be the better meal.
        for (var c = 0; c < StarterBlocks.CropCount; c++)
        {
            var crop = StarterBlocks.Crops[c];

            items.Register(new ItemType
            {
                Name = crop.Name, Label = crop.Name,
                IconLayer = (ushort)(StarterBlocks.LayerFirstCropItem + c),
                Feeds = crop.Feeds,
            });
        }

        // ⛳ What the bush pays. The least of the foods on purpose: berries come off a plant that
        // asked for no hoe, no water and no tilled ground, and the cheapest food should be the one
        // with the least agriculture in it. Planting one back is SownOnSoil's rule, not a Places —
        // what a berry becomes depends on the ground it is put to.
        items.Register(new ItemType
        {
            Name = "berries", Label = "berries",
            IconLayer = StarterBlocks.LayerBerries, Feeds = 2,
        });

        // ⛳ The cave mushrooms: a raw mouthful each, placeable back the way a flower is, and the
        // reason to carry them is the fire — either roasts to the same meal. A mushroom is what
        // you eat when the dark caught you unprovisioned, so raw feeds the least of anything.
        foreach (var (name, label, layer) in ((string, string, ushort)[])
                 [
                     ("mushroom_brown", "brown mushroom", StarterBlocks.LayerMushroomBrown),
                     ("mushroom_red", "red mushroom", StarterBlocks.LayerMushroomRed),
                 ])
        {
            items.Register(new ItemType
            {
                Name = name, Label = label, IconLayer = layer,
                DrawsAsBlock = true, Feeds = 1,
                Places = new Placeable
                {
                    Label = label,
                    Kind = PlacementKind.Plain,
                    Variants = [blocks.ByName(name).Id],
                },
            });
        }

        // ⚠ Four: under the baked potato and the meats, because the ingredient was found rather
        // than grown — but enough that roasting on the way down is worth the furnace slot.
        items.Register(new ItemType
        {
            Name = "roasted_mushroom", Label = "roasted mushroom",
            IconLayer = StarterBlocks.LayerRoastedMushroom, Feeds = 4,
        });

        // ⛳ The pumpkin and what a pair of shears makes of it. The whole one is a wild find and a
        // building block; carved, it wears a face the way it was turned when cut; and with a torch
        // shut inside it is the meadow's own lantern. The carve itself is an act on the world, not
        // a recipe — the shears do it where the pumpkin stands.
        Block(items, blocks, "pumpkin", "pumpkin", StarterBlocks.LayerPumpkinSide);

        foreach (var (name, label, icon, lit) in ((string, string, ushort, bool)[])
                 [
                     ("carved_pumpkin", "carved pumpkin", StarterBlocks.LayerPumpkinFace, false),
                     ("jack_o_lantern", "jack o'lantern", StarterBlocks.LayerJackOLantern, true),
                 ])
        {
            items.Register(new ItemType
            {
                Name = name, Label = label, IconLayer = icon, DrawsAsBlock = true,
                Places = new Placeable
                {
                    Label = label,
                    Kind = PlacementKind.Facing,
                    Variants = StarterBlocks.CarvedPumpkins(blocks, lit),
                },
            });
        }

        // ⚠ Six, which is bread and a cooked steak — so a fire turns the meanest of the three into
        // the best meal a field can produce. That is the whole reason to grow potatoes rather than
        // carrots, and it is the only thing separating the three crops mechanically.
        items.Register(new ItemType
        {
            Name = "baked_potato", Label = "baked potato",
            IconLayer = StarterBlocks.LayerBakedPotato, Feeds = 6,
        });

        Loose(items, "clay_lump", "clay lump", StarterBlocks.LayerClayLump);
        Loose(items, "brick", "brick", StarterBlocks.LayerBrick);

        // ⚠ Paper burns, and cheaply — a sheet is worth a fifth of a stick. It is pulped wood, so
        // refusing to burn would be the odd claim; and it being poor fuel is what stops anybody
        // turning a forest into paper to feed a furnace when the planks would do it five times over.
        Loose(items, "paper", "paper", StarterBlocks.LayerPaper, burn: 1f);

        // What an animal leaves. ⚠ Leather and feather are components with nothing yet to spend
        // them on, and that is honest rather than an oversight — armour and arrows are the two
        // things they are for, and both are their own work. They are obtainable, which is what the
        // reachability walk asks of an item; being consumed is a different claim.
        Loose(items, "leather", "leather", StarterBlocks.LayerLeather);
        Loose(items, "feather", "feather", StarterBlocks.LayerFeather);
        Loose(items, "egg", "egg", StarterBlocks.LayerEgg);

        // And what the dark leaves. ⚠ Rotten flesh is food, which is a joke the genre makes and a
        // real decision here: it is worth one half-heart against a cooked steak's six, so a player
        // who fought their way through a night has something and not much.
        Loose(items, "string", "string", StarterBlocks.LayerString);
        Loose(items, "bone", "bone", StarterBlocks.LayerBone);

        // ⚠ A component with nothing yet to spend it on, the leather-and-feather posture: M1's
        // reagents are what it is for, and being obtainable is this phase's whole claim.
        Loose(items, "slimeball", "slimeball", StarterBlocks.LayerSlimeball);

        items.Register(new ItemType
        {
            Name = "rotten_flesh", Label = "rotten flesh",
            IconLayer = StarterBlocks.LayerRottenFlesh, Feeds = 1,
        });

        RegisterMeats(items);
        RegisterTools(items);

        // ⛳ The one tool whose work is not mining. It takes a fleece off a live sheep, which is why
        // it carries no tier: there is no block it is the right answer for, and giving it one would
        // make it a shovel with a strange picture.
        items.Register(new ItemType
        {
            Name = "shears", Label = "shears", IconLayer = StarterBlocks.LayerShears,
            MaxStack = 1, Tool = ToolClass.Shears, Durability = 238,
        });

        // ⛳ The buckets, and they are what turn a fluid from scenery into a thing a player uses.
        // Everything about flow that anybody can actually DO — a moat, a farm, quenching a flow you
        // have to cross, a cauldron — is downstream of being able to pick one cell of it up and put
        // it down somewhere else. A full one carries a single source, so plumbing is a series of
        // trips rather than one gesture.
        items.Register(new ItemType
        {
            Name = "bucket", Label = "bucket", IconLayer = StarterBlocks.LayerBucket, MaxStack = 16,
        });

        items.Register(new ItemType
        {
            Name = "water_bucket", Label = "bucket of water",
            IconLayer = StarterBlocks.LayerWaterBucket, MaxStack = 1,
        });

        // ⚠ Fuel, and the best in the game — which is the genre's own answer and a good one. A
        // hundred smelts is worth a trip to the deep and a burn or two, and it is the first reason
        // to go down there that is not simply another ore.
        items.Register(new ItemType
        {
            Name = "lava_bucket", Label = "bucket of lava",
            IconLayer = StarterBlocks.LayerLavaBucket, MaxStack = 1, BurnSeconds = 1000f,
        });

        RegisterArmour(items);

        return items.Seal(blocks);
    }

    /// <summary>
    /// Twenty pieces of armour, off the one table.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>This is what leather has been for since the animals landed.</b> It dropped, it was
    /// carried, and nothing anywhere consumed it — the one honest hole in "a drop that feeds the
    /// recipe tree". A cow is now the first armour in the game and the only kind obtainable without
    /// going underground at all.</para>
    /// <para>⚠ <b>One in a slot, always.</b> Armour carries wear, and wear is part of a stack's
    /// identity — two half-worn helmets folding into one another would silently repair or silently
    /// ruin one of them depending on which was merged into which.</para>
    /// </remarks>
    private static void RegisterArmour(ItemRegistry items)
    {
        for (var m = 0; m < Armour.Materials.Length; m++)
        for (var p = 0; p < Armour.Pieces.Length; p++)
        {
            var material = Armour.Materials[m];
            var piece = Armour.Pieces[p];

            items.Register(new ItemType
            {
                Name = Armour.ItemName(material, piece),
                Label = $"{material.Name} {piece.Name}",
                IconLayer = (ushort)(StarterBlocks.LayerFirstArmour + m * StarterBlocks.ArmourPieceCount + p),
                MaxStack = 1,
                Wears = piece.Slot,
                ArmourPoints = material.Points[(int)piece.Slot],
                Durability = material.Durability,
            });
        }

        // ⛳ THE SHIELDS, and they are what makes the other hand worth having. The offhand has been
        // real storage since the player screen landed and took anything at all, which is a place to
        // lose a stack rather than a slot — a shield is the one thing that is only useful there, so
        // putting one in is a decision rather than a shrug.
        //
        // ⚠ `Wears` is Offhand rather than null. Nothing filters on it — the other hand accepts
        // anything by design — but it is what the tooltip reads to say where a thing goes, and a
        // non-zero ShieldShare is how the game tells a shield from the torch somebody left in there.
        for (var i = 0; i < Armour.Shields.Length; i++)
        {
            var shield = Armour.Shields[i];

            items.Register(new ItemType
            {
                Name = shield.Name,
                Label = shield.Name.Replace('_', ' '),
                IconLayer = (ushort)(StarterBlocks.LayerFirstShield + i),
                MaxStack = 1, Wears = EquipSlot.Offhand,
                Durability = shield.Durability, ShieldShare = shield.Share,
            });
        }
    }

    /// <summary>Every meat, raw and cooked, off the one table.</summary>
    /// <remarks>
    /// ⚠ <b>Raw is not fuel and neither is cooked.</b> Everything else loose in this file that burns
    /// says so; meat says nothing, which is the table declining to let a player heat a furnace with
    /// the dinner. Two rows per animal and the layer pair falls out of the index — the same shape the
    /// tools use, and the reason a fifth animal is one row here rather than a chapter.
    /// </remarks>
    private static void RegisterMeats(ItemRegistry items)
    {
        for (var i = 0; i < Meats.Length; i++)
        {
            var meat = Meats[i];
            var layer = (ushort)(StarterBlocks.LayerFirstMeat + i * 2);

            items.Register(new ItemType
            {
                Name = $"raw_{meat.Name}", Label = $"raw {meat.Name}",
                IconLayer = layer, Feeds = meat.Raw,
            });

            items.Register(new ItemType
            {
                Name = $"cooked_{meat.Name}", Label = $"cooked {meat.Name}",
                IconLayer = (ushort)(layer + 1), Feeds = meat.Cooked,
            });
        }
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

                // ⛔ From the tier's own rung, not from the item's — a sword's Tier is zero above,
                // so reading it back would make every sword in the game hit like a wooden one.
                AttackDamage = Combat.DamageFor(head, t.Tier),
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

    /// <summary>
    /// What each animal leaves, and what had to happen for it to leave it.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Every kind gives at least one thing the recipe tree wants and one thing to eat.</b>
    /// That was the whole point of the animals, in the user's own framing — a drop that feeds the
    /// recipe set rather than being decoration. Leather, wool and feather are the components; the
    /// meat is what makes killing one worth doing on the day you have no use for the component.</para>
    /// <para>⚠ <b>A sheep is the kind with rows under two triggers</b>, and it is the reason the
    /// trigger is a column rather than three tables. Shorn, it gives wool and keeps walking; killed,
    /// it gives its wool <em>and</em> its mutton — unless it was sheared this afternoon, which is
    /// what <c>NeedsFleece</c> says on both rows at once.</para>
    /// </remarks>
    public static CreatureDrops Creatures(ItemRegistry items) => new(
        items,
        new CreatureDrops.Rule("cow", DropTrigger.Killed, "leather", 1, 3),
        new CreatureDrops.Rule("cow", DropTrigger.Killed, "raw_beef", 1, 3),

        new CreatureDrops.Rule("pig", DropTrigger.Killed, "raw_pork", 1, 3),

        // ⚠ White, because every sheep in the world is. A fleece that came off the animal already
        // dyed would want the animal to carry a colour, which is a field on the creature and a
        // column in the save — and the dye tree makes the other fifteen out of this one anyway.
        new CreatureDrops.Rule("sheep", DropTrigger.Killed, "raw_mutton", 1, 2),
        new CreatureDrops.Rule("sheep", DropTrigger.Killed, "wool_white", 1, 1, NeedsFleece: true),
        new CreatureDrops.Rule(
            "sheep", DropTrigger.Harvested, "wool_white", 1, 3,
            Tool: ToolClass.Shears, NeedsFleece: true),

        new CreatureDrops.Rule("chicken", DropTrigger.Killed, "feather", 0, 2),
        new CreatureDrops.Rule("chicken", DropTrigger.Killed, "raw_chicken", 1, 1),
        new CreatureDrops.Rule("chicken", DropTrigger.Shed, "egg", 1, 1),

        // ⛳ And the three the dark gives up, which are the last components the recipe tree was
        // waiting on. String and bone were named in the plan as two of the five that unblock the
        // most, and until something hostile stood in the world neither could be obtained at all —
        // which the reachability walk said out loud rather than quietly letting them pass.
        new CreatureDrops.Rule("spider", DropTrigger.Killed, "string", 0, 2),
        new CreatureDrops.Rule("skeleton", DropTrigger.Killed, "bone", 1, 3),
        new CreatureDrops.Rule("zombie", DropTrigger.Killed, "rotten_flesh", 1, 2),

        // ⛳ The zombie's cousins. The drowned sometimes surfaces with an ingot in its fist — the
        // reference's own tell, and the one drop that makes wading after one worth the wet.
        new CreatureDrops.Rule("drowned", DropTrigger.Killed, "rotten_flesh", 1, 2),
        new CreatureDrops.Rule("drowned", DropTrigger.Killed, "copper_ingot", 0, 1),
        new CreatureDrops.Rule("husk", DropTrigger.Killed, "rotten_flesh", 1, 2),

        // ⛳ #93's first new drop. A component the magic arc (M1) is waiting on, obtainable the
        // day the slime is — the string-and-bone posture exactly.
        new CreatureDrops.Rule("slime", DropTrigger.Killed, "slimeball", 1, 2));

    /// <summary>What every stage of every root crop leaves when it is pulled up.</summary>
    /// <remarks>
    /// ⚠ <b>The ripe one gives more, which is the entire reason to wait.</b> One back for an unripe
    /// pull is break-even; two to three for a ripe one is what makes a field grow rather than just
    /// persist. Built off the table so a fourth crop is a row in <see cref="StarterBlocks.Crops"/>
    /// and nothing here.
    /// </remarks>
    private static BlockDrops.Rule[] CropDropRules()
    {
        var rules = new BlockDrops.Rule[StarterBlocks.CropCount * StarterBlocks.CropStages];

        for (var c = 0; c < StarterBlocks.CropCount; c++)
        for (var s = 0; s < StarterBlocks.CropStages; s++)
        {
            var ripe = s == StarterBlocks.CropStages - 1;

            rules[c * StarterBlocks.CropStages + s] = new BlockDrops.Rule(
                StarterBlocks.CropName(c, s),
                StarterBlocks.Crops[c].Name,
                ripe ? 3 : 1);
        }

        return rules;
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
        new BlockDrops.Rule("diamond_ore", "diamond"),
        new BlockDrops.Rule("azurite_ore", "azurite", 4),

        new BlockDrops.Rule("driftoak_leaves", null),
        new BlockDrops.Rule("vine", null),

        // ⚠ Grass still leaves nothing and the flowers no longer do. The difference is that one of
        // them is now a material: every colour in the game starts as something picked out of a
        // field, so a bloom that vanished when it was broken would be a dye source a player could
        // walk over and never obtain.
        // ⛳ GRASS GIVES SEEDS, and it is the whole entry to farming. There is no other source and
        // there should not be: the first field comes from walking through a meadow pulling up tufts,
        // which is a thing a player does by accident on their first afternoon and can then use.
        new BlockDrops.Rule("meadowgrass", "seeds"),

        // ⚠ Tilled ground digs up as the dirt it was made from. Farmland has an item nowhere and
        // needs none — it is a state of the ground rather than a thing to carry.
        new BlockDrops.Rule("farmland", "dirt"),
        new BlockDrops.Rule("farmland_wet", "dirt"),

        // ⛔ THE THREE UNRIPE STAGES GIVE THE SEED BACK AND NOTHING ELSE. Without this a player who
        // walks through their own field gets a full harvest from it, and waiting for the crop — the
        // entire mechanic — buys nothing. The ripe one is handled in code, because it gives two
        // different items and BlockDrops is one item per block.
        new BlockDrops.Rule(StarterBlocks.WheatName(0), "seeds"),
        new BlockDrops.Rule(StarterBlocks.WheatName(1), "seeds"),
        new BlockDrops.Rule(StarterBlocks.WheatName(2), "seeds"),
        new BlockDrops.Rule(StarterBlocks.WheatName(3), "wheat"),

        // ⛳ The root crops, on the same rule and for the same reason — but with one difference that
        // matters: a crop that IS its own seed gives the same item at every stage, so pulling one up
        // early costs you the wait and never the crop. That is deliberate. Wheat can punish an early
        // harvest because its seed is a separate item; making a carrot vanish would be a trap rather
        // than a lesson, since the thing you planted and the thing you want are one item.
        .. CropDropRules(),

        // ⛳ The bush pays berries both ways round: breaking the wild ripe one is the FIRST berry —
        // the reachability walk's dig source — and the young one refunds the berry that planted it,
        // so no round trip through the ground ever loses the thing itself. Two for the ripe against
        // a pick's two-to-three, which is Foraging's pin: keeping the bush has to be the better deal.
        new BlockDrops.Rule(StarterBlocks.BerryBushRipeName, "berries", 2),
        new BlockDrops.Rule(StarterBlocks.BerryBushName, "berries"),

        // ⛳ A web tears into the string it was spun from — the daylight route to the spider's own
        // drop, and the reason a webbed pocket is worth walking into on purpose.
        new BlockDrops.Rule("cobweb", "string"),
        new BlockDrops.Rule("snow_layer", null),
        new BlockDrops.Rule("water", null),
        new BlockDrops.Rule("bedrock", null),

        // A burning furnace is a furnace. Without these four rules the lit forms leave nothing,
        // and a player who mines one mid-smelt loses it.
        new BlockDrops.Rule("furnace_east_lit", "furnace"),
        new BlockDrops.Rule("furnace_west_lit", "furnace"),
        new BlockDrops.Rule("furnace_south_lit", "furnace"),
        new BlockDrops.Rule("furnace_north_lit", "furnace"),

        new BlockDrops.Rule("blast_furnace_east_lit", "blast_furnace"),
        new BlockDrops.Rule("blast_furnace_west_lit", "blast_furnace"),
        new BlockDrops.Rule("blast_furnace_south_lit", "blast_furnace"),
        new BlockDrops.Rule("blast_furnace_north_lit", "blast_furnace"),

        // ⚠ And the smoker's, which is the same rule a third time: a burning station and a cold one
        // are the same object, so breaking either has to leave exactly one of it. Missing these is
        // how a player loses a station by breaking it while something was cooking.
        new BlockDrops.Rule("smoker_east_lit", "smoker"),
        new BlockDrops.Rule("smoker_west_lit", "smoker"),
        new BlockDrops.Rule("smoker_south_lit", "smoker"),
        new BlockDrops.Rule("smoker_north_lit", "smoker"),

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
            DrawsAsBlock = true,
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
