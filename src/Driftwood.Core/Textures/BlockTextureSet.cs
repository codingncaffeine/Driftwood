using Driftwood.Core.Blocks;
using Driftwood.Core.Items;

namespace Driftwood.Core.Textures;

/// <summary>
/// Every block texture layer: Driftwood's own tile, and where an imported pack keeps its version.
/// </summary>
/// <param name="Name">Driftwood's name for the layer.</param>
/// <param name="PackPath">
/// Path inside an imported pack, relative to <c>assets/minecraft/</c>. Empty when nothing in a
/// pack corresponds — those layers always keep Driftwood's own art.
/// </param>
/// <param name="Cutout">
/// True when the texture has fully transparent pixels that must be discarded rather than blended.
/// </param>
/// <param name="PackPathAlt">
/// An older path for the same texture, tried when <paramref name="PackPath"/> is not in the pack.
/// </param>
/// <remarks>
/// The alternate path is not a nicety. Textures get renamed between game versions — short grass was
/// <c>grass.png</c> until it became <c>short_grass.png</c> — and a pack written for either side of
/// that rename is a pack a player owns. Falling back costs one dictionary miss at load and is the
/// difference between a texture importing and silently keeping ours.
/// </remarks>
/// <param name="Tinted">
/// True when this layer is multiplied by a climate colour before it reaches the screen.
/// </param>
/// <param name="Borrow">
/// A texture of a <em>different</em> material to take the shape from when the pack has none of its
/// own, recoloured to <paramref name="BorrowTint"/>.
/// </param>
/// <param name="BorrowTint">
/// Packed 0xRRGGBB the borrowed texture is recoloured to. Zero means no borrowing.
/// </param>
/// <remarks>
/// ⛳ <b>Borrowing is for a rung of a ladder the reference does not have.</b> Our tool and armour
/// ladders run seven and six deep against the genre's six and five, so there are tiers nobody has
/// ever painted — copper had none at all until the reference gave it a set, and a pack written
/// before that has six materials of tools and one of ours left in our own art. One odd tier out of
/// seven reads worse than none of them matching.
/// <para>⛔ <b>Borrow from a NEUTRAL material, never a coloured one.</b> The recolour multiplies a
/// texture's own light against a target, so what it needs is shading rather than hue: iron carries
/// the shape and nothing else, and gold would fight the new colour with the old one all the way
/// through. It is the same reason copper armour wore chainmail before copper existed.</para>
/// </remarks>
public readonly record struct BlockTextureLayer(
    string Name, string PackPath, bool Cutout, string PackPathAlt = "", bool Tinted = false,
    string Borrow = "", uint BorrowTint = 0);

/// <summary>
/// Builds the full set of block tiles, starting from Driftwood's own and letting a pack override
/// whichever ones it carries.
/// </summary>
/// <remarks>
/// The mapping table is the whole point of this file. Driftwood's blocks are deliberately not named
/// after anybody else's, so nothing can be matched automatically — the correspondence between "our
/// oak log's side" and the file an imported pack keeps it in has to be written down, once, in one
/// place. Everything else here is bookkeeping around that table.
/// </remarks>
public static class BlockTextureSet
{
    /// <summary>
    /// Copper's own colour, for recolouring a borrowed iron picture into it.
    /// </summary>
    /// <remarks>
    /// ⚠ Taken from <see cref="Items.Armour"/>'s table rather than typed again, so the tools, the
    /// armour and the borrowed art are all one metal. Two copies of a colour is two coppers.
    /// </remarks>
    private static readonly uint CopperTint =
        Items.Armour.Materials.First(m => m.Name == "copper").Tint;

    /// <summary>Indexed by layer id — the same numbers <see cref="StarterBlocks"/> hands out.</summary>
    public static readonly BlockTextureLayer[] Layers =
    [
        new("stone",       "textures/block/stone.png",            false),
        new("dirt",        "textures/block/dirt.png",             false),
        new("grass_top",   "textures/block/grass_block_top.png",  false, "", true),
        new("grass_side",  "textures/block/grass_block_side.png", false),
        new("sand",        "textures/block/sand.png",             false),
        new("water",       "textures/block/water_still.png",      false),
        new("gravel",      "textures/block/gravel.png",           false),
        new("driftoak_side", "textures/block/oak_log.png",        false),
        new("driftoak_top",  "textures/block/oak_log_top.png",    false),
        new("driftoak_leaves", "textures/block/oak_leaves.png",   true, "", true),
        new("driftoak_planks", "textures/block/oak_planks.png",   false),
        new("coal_ore",    "textures/block/coal_ore.png",         false),
        new("iron_ore",    "textures/block/iron_ore.png",         false),
        new("bedrock",     "textures/block/bedrock.png",          false),

        // Emberstone is ours; glowstone is the nearest thing a pack will have painted, and a warm
        // glowing rock is close enough that borrowing its art reads correctly.
        new("emberstone",  "textures/block/glowstone.png",        false),
        new("vine",        "textures/block/vine.png",             true, "", true),

        // Everything from here down is named ours on the left and theirs on the right, which is the
        // whole reason this table exists: our vocabulary can be entirely our own and a pack is still
        // looked up by the path its author actually shipped.
        new("deepstone",   "textures/block/deepslate.png",        false),
        new("coralstone",  "textures/block/granite.png",          false),
        new("driftstone",  "textures/block/andesite.png",         false),
        new("saltstone",   "textures/block/diorite.png",          false),
        new("copper_ore",  "textures/block/copper_ore.png",       false),
        new("gold_ore",    "textures/block/gold_ore.png",         false),

        // Stormglass is ours; diamond is the deep gem a pack will have painted.
        new("stormglass_ore", "textures/block/diamond_ore.png",   false),

        // Azurite is our own blue mineral; lapis is the blue ore a pack will have art for.
        new("azurite_ore", "textures/block/lapis_ore.png",        false),
        new("clay",        "textures/block/clay.png",             false),
        new("sandstone",   "textures/block/sandstone.png",        false),
        new("sandstone_top", "textures/block/sandstone_top.png",  false),
        new("snow",        "textures/block/snow.png",             false),

        // The fringe of grass rolling over a block's side, as its own cut-out for the climate
        // colour to run through. Every pack ships one and until models arrived there was nothing to
        // hang it on, which is why grass sides were plain dirt no matter what was imported.
        new("grass_side_overlay", "textures/block/grass_block_side_overlay.png", true, "", true),

        // Short grass was called grass.png until it was renamed, so both paths are worth trying.
        new("meadowgrass", "textures/block/short_grass.png",      true, "textures/block/grass.png", true),

        // Ours are seaflax and marshlily; a pack has painted a small blue flower and a small white
        // one whatever anybody calls them, and those are the two nearest.
        new("seaflax",     "textures/block/cornflower.png",       true),
        new("marshlily",   "textures/block/oxeye_daisy.png",      true),
        new("torch",       "textures/block/torch.png",            true),

        // What crafting brought with it.
        new("rubble",      "textures/block/cobblestone.png",      false),
        new("glass",       "textures/block/glass.png",            true),
        new("bricks",      "textures/block/bricks.png",           false),
        new("bench_top",   "textures/block/crafting_table_top.png",  false),
        new("bench_side",  "textures/block/crafting_table_side.png", false),
        new("furnace_top", "textures/block/furnace_top.png",      false),
        new("furnace_side", "textures/block/furnace_side.png",    false),
        new("furnace_front", "textures/block/furnace_front.png",  false),
        new("furnace_front_lit", "textures/block/furnace_front_on.png", false),

        // Cut stone. Ours on the left and the nearest thing a pack will already have painted on the
        // right — our deepstone is their deepslate, our coralstone their granite — which is the
        // same trade the raw rocks made and the reason a pack skins a vocabulary it has never heard
        // of. Every one of these was checked against a real pack of each layout before it was
        // written down.
        new("stone_bricks", "textures/block/stone_bricks.png",    false),
        new("smooth_stone", "textures/block/smooth_stone.png",    false),
        new("deepstone_polished", "textures/block/polished_deepslate.png", false),
        new("deepstone_bricks", "textures/block/deepslate_bricks.png", false),
        new("coralstone_polished", "textures/block/polished_granite.png", false),
        new("driftstone_polished", "textures/block/polished_andesite.png", false),
        new("saltstone_polished", "textures/block/polished_diorite.png", false),
        new("sandstone_cut", "textures/block/cut_sandstone.png",  false),
        new("sandstone_chiseled", "textures/block/chiseled_sandstone.png", false),

        // Light a player can make. Our smokeglass is their tinted glass and our stormglass lamp is
        // the bright block a pack has painted for the bottom of the sea, which is the same trade
        // every coined name in this table makes. The fire that stands in a campfire is the one file
        // whose name differs between the layouts, and the rename table carries it.
        //
        // ⚠ A lantern keeps ours, and the empty path is the table saying why out loud. A pack's
        // lantern.png is not a picture of a lantern — it is a sheet with a body, a cap and a chain
        // packed into corners of it, and which corner is which is stated in the model file beside
        // it and nowhere else. Measured on a real pack of each layout: neither puts a six-by-seven
        // body anywhere a rect derived from ours would land. Reading a pack's own models is the
        // real fix and is the only way any of the packed-sheet blocks will ever import; guessing at
        // rects would put a lantern's chain across its own door in most packs and in none of them
        // obviously. Everything else new here maps a whole tile onto a whole face and is safe.
        new("lantern",     "",                                    false),
        new("campfire_fire", "textures/block/campfire_fire.png",  true),
        new("smokeglass",  "textures/block/tinted_glass.png",     false),
        new("stormglass_lamp", "textures/block/sea_lantern.png",  false),

        // Things that open. All four map a whole tile onto a whole face, which is what every pack
        // draws them as — a door is painted as two tiles because it is two blocks tall, and a
        // ladder is a cut-out with the wall showing through it.
        new("ladder",      "textures/block/ladder.png",           true),
        new("door_lower",  "textures/block/oak_door_bottom.png",  false),
        new("door_upper",  "textures/block/oak_door_top.png",     true),
        new("trapdoor",    "textures/block/oak_trapdoor.png",     true),

        // ⚠ A chest keeps ours, and not for want of trying. The genre renders one as an ENTITY — a
        // single wrapped sheet on a hinged model at textures/entity/chest/normal.png, a separate
        // draw path since the game was in beta — so there is no block texture at any path for these
        // three to point at, and a block-texture importer silently skips every chest reskin in every
        // pack ever made. Ours are drawn as a finished chest rather than as something expecting to
        // be replaced, because they are what anybody will actually be looking at.
        new("chest_top",   "",                                    false),
        new("chest_side",  "",                                    false),
        new("chest_front", "",                                    false),

        // Stations. Every one of these is a plain cube face with a whole tile on it, so a pack's
        // own art lands correctly — which is why the bench finally has a front to point at.
        new("bench_front", "textures/block/crafting_table_front.png", false),
        new("stonecutter_top", "textures/block/stonecutter_top.png", false),
        new("stonecutter_side", "textures/block/stonecutter_side.png", false),
        new("blast_top",   "textures/block/blast_furnace_top.png",  false),
        new("blast_side",  "textures/block/blast_furnace_side.png", false),
        new("blast_front", "textures/block/blast_furnace_front.png", false),
        new("blast_front_lit", "textures/block/blast_furnace_front_on.png", false),

        // Two more flowers, for the two colours nothing else in the world could have given. Ours by
        // name; a pack has painted a red bloom and a yellow one whatever its own game calls them.
        new("emberbloom",  "textures/block/poppy.png",            true, "textures/block/flower_rose.png"),
        new("sunwort",     "textures/block/dandelion.png",        true, "textures/block/flower_dandelion.png"),

        // ⛳ The sixteen wools, generated from StarterBlocks.Colours rather than written out — see
        // WoolRows below. Each is its own file in a pack, which is exactly why they cannot be one
        // tinted layer: a tint is a colour nothing a player installs could ever replace.
        .. WoolRows(),

        // Items, from here to the end. They live in the same array as the block faces because they
        // are the same sixteen-pixel tiles drawn by the same two places — a slot on the bar and a
        // thing spinning on the floor — and a pack that reskins the world should reskin the pockets
        // too. Every one of them is a cut-out: an icon on an opaque square is a sticker.
        new("stick",       "textures/item/stick.png",             true),
        new("coal",        "textures/item/coal.png",              true),
        new("charcoal",    "textures/item/charcoal.png",          true),
        new("raw_copper",  "textures/item/raw_copper.png",        true),
        new("raw_iron",    "textures/item/raw_iron.png",          true),
        new("raw_gold",    "textures/item/raw_gold.png",          true),
        new("copper_ingot", "textures/item/copper_ingot.png",     true),
        new("iron_ingot",  "textures/item/iron_ingot.png",        true),
        new("gold_ingot",  "textures/item/gold_ingot.png",        true),

        // Ours are stormglass and azurite; the deep gem and the blue mineral are what a pack has
        // painted, whatever its own game calls them.
        // ⚠ Off the pack's diamond and onto its amethyst, the day the game got a real diamond. The
        // shard is the nearer picture anyway: stormglass is a cold cut gem and ours is teal.
        new("stormglass",  "textures/item/amethyst_shard.png",    true),
        new("azurite",     "textures/item/lapis_lazuli.png",      true),
        new("clay_lump",   "textures/item/clay_ball.png",         true),
        new("brick",       "textures/item/brick.png",             true),

        // What an animal leaves, and the tool that takes one thing off a live one.
        new("leather",     "textures/item/leather.png",           true),
        new("feather",     "textures/item/feather.png",           true),
        new("egg",         "textures/item/egg.png",               true),
        new("shears",      "textures/item/shears.png",            true),

        // The meats, raw then cooked, in StarterItems.Meats order. ⚠ Their names are the genre's
        // own on both sides of the table, which is unusual here and correct: beef is beef.
        new("raw_beef",    "textures/item/beef.png",              true, "textures/item/raw_beef.png"),
        new("cooked_beef", "textures/item/cooked_beef.png",       true),
        new("raw_pork",    "textures/item/porkchop.png",          true, "textures/item/raw_porkchop.png"),
        new("cooked_pork", "textures/item/cooked_porkchop.png",   true),
        new("raw_mutton",  "textures/item/mutton.png",            true, "textures/item/raw_mutton.png"),
        new("cooked_mutton", "textures/item/cooked_mutton.png",   true),
        new("raw_chicken", "textures/item/chicken.png",           true, "textures/item/raw_chicken.png"),
        new("cooked_chicken", "textures/item/cooked_chicken.png", true),

        // ⛔ THE SIXTEEN POWDERS, AND THEY BELONG HERE RATHER THAN AT THE END. This array is indexed
        // by the same numbers StarterBlocks hands out, so its order IS the numbering — and the dyes
        // first went in after the tools while LayerFirstDye said 112 and LayerFirstTool said 128.
        // Every check passed: the count was right, every tile was painted, every cutout had holes.
        // What was wrong was invisible from here — a pack's wooden pickaxe would have landed on
        // white dye and its dye on a tool. ⚠ Appending to this array is not the same as appending to
        // the layer numbers, and only reading both together says so.
        .. DyeRows(),

        // What the dark leaves behind.
        new("string",      "textures/item/string.png",            true),
        new("bone",        "textures/item/bone.png",              true),
        new("rotten_flesh", "textures/item/rotten_flesh.png",     true),

        // Six tiers of four heads, tier-major. Copper tooling has no counterpart to look up, so
        // those four keep our own art rather than being pointed at somebody else's nearest thing —
        // an empty path is the table saying so out loud.
        new("wood_pickaxe", "textures/item/wooden_pickaxe.png",   true),
        new("wood_axe",    "textures/item/wooden_axe.png",        true),
        new("wood_shovel", "textures/item/wooden_shovel.png",     true),
        new("wood_sword",  "textures/item/wooden_sword.png",      true),
        new("stone_pickaxe", "textures/item/stone_pickaxe.png",   true),
        new("stone_axe",   "textures/item/stone_axe.png",         true),
        new("stone_shovel", "textures/item/stone_shovel.png",     true),
        new("stone_sword", "textures/item/stone_sword.png",       true),
        // ⛔ THESE FOUR WERE AN EMPTY PATH — hard-wired to keep our art no matter what was loaded —
        // and the reason was true when it was written and is not any more. Copper had no tools in
        // the reference, so nobody had painted one; the copper era gave it a full set, and MEASURED
        // against the packs on this machine, three of seven now ship all four. Reported by the user
        // as copper tools staying default while everything else in the bar wore the pack.
        //
        // ⚠ No alternate to a warmer metal on purpose. Falling back to the gold ones would put the
        // same picture on two rungs of the ladder, and telling a copper pickaxe from a gold one at
        // a glance is worth more than matching the pack's style on one tier. A pack without copper
        // keeps ours, which is at least the right colour.
        new("copper_pickaxe", "textures/item/copper_pickaxe.png", true,
            Borrow: "textures/item/iron_pickaxe.png", BorrowTint: CopperTint),
        new("copper_axe",  "textures/item/copper_axe.png",        true,
            Borrow: "textures/item/iron_axe.png", BorrowTint: CopperTint),
        new("copper_shovel", "textures/item/copper_shovel.png",   true,
            Borrow: "textures/item/iron_shovel.png", BorrowTint: CopperTint),
        new("copper_sword", "textures/item/copper_sword.png",     true,
            Borrow: "textures/item/iron_sword.png", BorrowTint: CopperTint),
        new("gold_pickaxe", "textures/item/golden_pickaxe.png",   true),
        new("gold_axe",    "textures/item/golden_axe.png",        true),
        new("gold_shovel", "textures/item/golden_shovel.png",     true),
        new("gold_sword",  "textures/item/golden_sword.png",      true),
        new("iron_pickaxe", "textures/item/iron_pickaxe.png",     true),
        new("iron_axe",    "textures/item/iron_axe.png",          true),
        new("iron_shovel", "textures/item/iron_shovel.png",       true),
        new("iron_sword",  "textures/item/iron_sword.png",        true),
        new("stormglass_pickaxe", "textures/item/netherite_pickaxe.png", true),
        new("stormglass_axe", "textures/item/netherite_axe.png",  true),
        new("stormglass_shovel", "textures/item/netherite_shovel.png", true),
        new("stormglass_sword", "textures/item/netherite_sword.png", true),

        // ⚠ Diamond takes the pack's diamond art and stormglass moved off it, for the same reason
        // their armour did: a pack ships one picture per material and two of ours reading the same
        // one is a row of tools nobody can tell apart. Stormglass wears netherite's, which is the
        // only other top-tier set anybody paints.
        new("diamond_pickaxe", "textures/item/diamond_pickaxe.png", true),
        new("diamond_axe", "textures/item/diamond_axe.png",       true),
        new("diamond_shovel", "textures/item/diamond_shovel.png", true),
        new("diamond_sword", "textures/item/diamond_sword.png",   true),

        // ⛳ The fluids, last in the array because THIS ARRAY'S ORDER IS THE LAYER NUMBERING and a
        // face inserted beside water would move every constant after it — see
        // StarterBlocks.LayerFirstFluid; the sixteen dyes cost this project that lesson once already.
        //
        // Still and flowing are two different pictures in every pack there is, and putting the still
        // one on a waterfall is the most obvious way to make a fluid look wrong: still is a surface
        // seen from above, flowing is a sheet travelling in a direction. Both are strips of frames.
        new("water_flow",  "textures/block/water_flow.png",       false),
        new("lava",        "textures/block/lava_still.png",       false),
        new("lava_flow",   "textures/block/lava_flow.png",        false),

        // And the pail that carries them, which is what makes a fluid a thing a player uses rather
        // than a thing a player looks at.
        new("bucket",      "textures/item/bucket.png",            true),
        new("water_bucket", "textures/item/water_bucket.png",     true),
        new("lava_bucket", "textures/item/lava_bucket.png",       true),
        new("coal_block",  "textures/block/coal_block.png",       false),

        // ⛳ The two nothing is made of. Every particle before these was a crop of whatever it came
        // off; fire is not made of anything, and smoke is not made of the thing it came out of.
        new("flame",       "textures/particle/flame.png",         true),
        new("smoke",       "textures/particle/generic_0.png",     true, "textures/particle/smoke.png"),

        // ⛳ The twenty pieces of armour, last for the same reason the fluids are: THIS ARRAY'S ORDER
        // IS THE LAYER NUMBERING. Built off the armour table rather than written out, so a sixth
        // material is one row there and none here.
        .. ArmourRows(),

        // ⛳ Three shields, faced in three metals. A pack ships exactly one shield picture, so the
        // other two keep our own art — an empty path is the table saying so out loud.
        new("shield",      "textures/item/shield.png",            true),
        new("stormglass_shield", "",                              true),
        new("diamond_shield", "",                                 true),

        // The third smelter and the second container, appended for the same reason everything since
        // the fluids has been: this array's order IS the layer numbering.
        new("smoker_top",  "textures/block/smoker_top.png",       false),
        new("smoker_side", "textures/block/smoker_side.png",      false),
        new("smoker_front", "textures/block/smoker_front.png",    false),
        new("smoker_front_lit", "textures/block/smoker_front_on.png", false),
        new("barrel_top",  "textures/block/barrel_top.png",       false),
        new("barrel_side", "textures/block/barrel_side.png",      false),

        // ⚠ Diamond's ore takes the pack's own diamond_ore only where a DEEPSLATE one exists —
        // stormglass already claimed the plain file, and two of our layers reading one of a pack's
        // is the fault PackAtlas.Validate exists to catch on the other layout. The deepslate variant
        // is the right picture anyway: ours forms in the deep and nowhere else.
        new("diamond_ore", "textures/block/deepslate_diamond_ore.png", false),
        new("diamond",     "textures/item/diamond.png",           true),

        // ⛳ Every pack in the genre ships this file and until now there was nothing to hang it on.
        new("paper",       "textures/item/paper.png",             true),

        // ⛳ Chrome rather than a block, and a layer anyway so a pack reskins the button with the
        // same machinery it reskins the world. The pack's own is the item they put in a lectern.
        new("recipe_book", "textures/item/book.png",              true),

        // The three metals packed away. Their names are the genre's own, so the mapping is direct.
        .. MetalBlockRows(),

        // ⛳ The anvil's three stages of wear on one side texture, which is the format's own
        // arrangement — the damage shows on the face you strike and nowhere else.
        new("anvil_side",  "textures/block/anvil.png",            false),
        new("anvil_top",   "textures/block/anvil_top.png",        false),
        new("anvil_chipped", "textures/block/chipped_anvil_top.png", false),
        new("anvil_damaged", "textures/block/damaged_anvil_top.png", false),

        // Tilled ground, dry and watered.
        new("farmland",    "textures/block/farmland.png",         false),
        new("farmland_wet", "textures/block/farmland_moist.png",  false),

        // ⚠ Wheat's stages, and OURS ARE FOUR WHERE THE PACK'S ARE EIGHT. The rows are spread across
        // the pack's eight so a field still reads as growing rather than as four copies of stage 0.
        .. WheatRows(),

        // What farming and the anvil are carried in a pocket as.
        new("hoe",         "textures/item/iron_hoe.png",          true),
        new("seeds",       "textures/item/wheat_seeds.png",       true, "textures/item/seeds_wheat.png"),
        new("wheat",       "textures/item/wheat.png",             true),
        new("bread",       "textures/item/bread.png",             true),
        new("bonemeal",    "textures/item/bone_meal.png",         true, "textures/item/dye_powder_white.png"),

        // ⛳ The three that are pulled up. Four stages each and then their three icons, appended for
        // the same reason everything since the fluids has been: this array's order IS the numbering.
        .. CropRows(),

        new("baked_potato", "textures/item/baked_potato.png", true),

        // ⛳ Sixteen panes of coloured glass, serving both the block and the pane family.
        .. StainedGlassRows(),

        // The composter: its slats, its floor, and the two states of what is rotting in it.
        new("composter_side",   "textures/block/composter_side.png",    false),
        new("composter_bottom", "textures/block/composter_bottom.png",  false),
        new("compost",          "textures/block/composter_compost.png", false),
        new("compost_ready",    "textures/block/composter_ready.png",   false),

        // ⛳ The berry bush's two states land on the pack's own four-stage run: ours are the two
        // that look like anything — stage1 is a young bush, stage3 carries fruit. The alternate is
        // the underscored spelling the older staged plants use where a pack ships it.
        new("berry_bush",      "textures/block/sweet_berry_bush_stage1.png", true,
            "textures/block/sweet_berry_bush_stage_1.png"),
        new("berry_bush_ripe", "textures/block/sweet_berry_bush_stage3.png", true,
            "textures/block/sweet_berry_bush_stage_3.png"),
        new("berries",         "textures/item/sweet_berries.png",            true),

        // ⛳ The cave mushrooms: the block tile serves as the icon too, the flowers' arrangement.
        // The alternate is the pre-flattening spelling, which happens to be our own name.
        new("mushroom_brown", "textures/block/brown_mushroom.png", true,
            "textures/block/mushroom_brown.png"),
        new("mushroom_red",   "textures/block/red_mushroom.png",   true,
            "textures/block/mushroom_red.png"),

        // No pack path: the genre roasts no mushroom — its cooked fungus is a stew in a bowl,
        // which is a different item — so this icon is ours under every pack.
        new("roasted_mushroom", "", true),
    ];

    /// <summary>
    /// One row per colour of stained glass, on the pack's own name for it.
    /// </summary>
    /// <remarks>
    /// ⛳ Measured across the shelf 2026-08-07: Dokucraft ships all 48 files (block, pane and pane
    /// top for each of sixteen), Vintage 16 and Silent Hill 21 — and every one of them uses
    /// <c>{colour}_stained_glass.png</c>, so the modern path is the one that matches everywhere.
    /// ⚠ The alternate is the pre-flattening <c>glass_{colour}.png</c> and is best effort, exactly
    /// like the dye rows: a name that is not in a pack simply keeps our own tile.
    /// ⛔ <b>NOT cutout, where plain glass is — and the audit is what said so.</b> Cutout means "this
    /// tile has holes in it, alpha-test them away", which is true of a clear pane (it IS a hole) and
    /// false of a coloured one (it is filled, or it would not be coloured). Marked cutout it fails
    /// "is marked cutout but has no holes" sixteen times over, and it would also have taken the
    /// weighted-halving mip path meant for foliage. What makes these see-through is the PASS they are
    /// drawn in — see <c>BlockType.Translucent</c> — and that is a different question from this flag.
    /// </remarks>
    private static BlockTextureLayer[] StainedGlassRows()
    {
        var rows = new BlockTextureLayer[StarterBlocks.Colours.Length];

        for (var i = 0; i < rows.Length; i++)
        {
            var dye = StarterBlocks.Colours[i];
            var old = dye.Pack == "light_gray" ? "silver" : dye.Pack;

            rows[i] = new BlockTextureLayer(
                $"stained_glass_{dye.Name}",
                $"textures/block/{dye.Pack}_stained_glass.png",
                false,
                $"textures/block/glass_{old}.png");
        }

        return rows;
    }

    /// <summary>
    /// Four stages of tops and one icon for each root crop, straight onto the pack's own names.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>No spreading rule, unlike <see cref="WheatRows"/>.</b> Measured across the shelf
    /// 2026-08-07: Silent Hill, Vintage and Dokucraft all paint <c>carrots_stage0..3</c> and
    /// <c>potatoes_stage0..3</c> — four stages, the same four we grow — so stage n is stage n and
    /// there is nothing to map.
    /// ⚠ <b>Beetroot is the one to watch.</b> Silent Hill ships only <c>item/beetroot.png</c> and no
    /// field art at all, where Vintage ships all four stages. So on that pack a beetroot field keeps
    /// our own tops while its icon comes off theirs — which is the per-layer fallback doing exactly
    /// what it is for, and the reason coverage is asked per layer rather than per feature.
    /// ⚠ The pack's block names are PLURAL and ours are not, which is the whole reason
    /// <see cref="StarterBlocks.Crop.Pack"/> exists beside <c>Name</c>.
    /// </remarks>
    private static BlockTextureLayer[] CropRows()
    {
        var stages = StarterBlocks.CropCount * StarterBlocks.CropStages;
        var rows = new BlockTextureLayer[stages + StarterBlocks.CropCount];

        for (var c = 0; c < StarterBlocks.CropCount; c++)
        {
            var crop = StarterBlocks.Crops[c];

            for (var s = 0; s < StarterBlocks.CropStages; s++)
                rows[c * StarterBlocks.CropStages + s] = new BlockTextureLayer(
                    StarterBlocks.CropName(c, s),
                    $"textures/block/{crop.Pack}_stage{s}.png",
                    true,
                    $"textures/block/{crop.Pack}_stage_{s}.png");
        }

        // Then the pockets, after every stage of every crop — the order LayerFirstCropItem states.
        for (var c = 0; c < StarterBlocks.CropCount; c++)
            rows[stages + c] = new BlockTextureLayer(
                StarterBlocks.Crops[c].Name,
                $"textures/item/{StarterBlocks.Crops[c].Name}.png",
                true);

        return rows;
    }

    /// <summary>One row per stage of wheat, spread across the pack's own eight.</summary>
    /// <remarks>
    /// ⛔ <b>Spread, not truncated.</b> Ours are four and every pack paints eight; taking the first
    /// four would give a field that never looks more than a third grown, because the pack's stages
    /// 0-3 are all seedlings. Stage n of ours is stage <c>n * 7 / 3</c> of theirs, which lands on
    /// 0, 2, 4 and 7 — a sprout, a shoot, a stalk and a ripe ear.
    /// </remarks>
    private static BlockTextureLayer[] WheatRows()
    {
        var rows = new BlockTextureLayer[StarterBlocks.WheatStages];

        for (var s = 0; s < rows.Length; s++)
        {
            var theirs = s * 7 / (StarterBlocks.WheatStages - 1);
            rows[s] = new BlockTextureLayer(
                StarterBlocks.WheatName(s), $"textures/block/wheat_stage{theirs}.png", true);
        }

        return rows;
    }

    /// <summary>One row per metal storage block, off the block table rather than written out.</summary>
    private static BlockTextureLayer[] MetalBlockRows()
    {
        var rows = new BlockTextureLayer[StarterBlocks.MetalBlockCount];

        for (var m = 0; m < rows.Length; m++)
            rows[m] = new BlockTextureLayer(
                StarterBlocks.MetalBlocks[m].Name,
                $"textures/block/{StarterBlocks.MetalBlocks[m].Name}.png",
                false);

        return rows;
    }

    /// <summary>
    /// One row per piece of armour, material-major.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The pack's material name, not ours.</b> A pack ships six materials under the genre's own
    /// names and ours deliberately are not those, so <see cref="Items.Armour.Material.Pack"/> states
    /// the mapping — copper wears chainmail's icons and stormglass wears diamond's, which are the
    /// nearest things anybody has actually painted. ⛔ Their <em>helmet</em> is spelled
    /// <c>_helmet</c> on modern layouts and <c>_helmet</c> on the old one too, but the old one puts
    /// the whole set under a <c>helmetCloth</c>-style stem for leather alone; that one is left to our
    /// own art rather than guessed at, which the empty alternate path says out loud.
    /// </remarks>
    private static BlockTextureLayer[] ArmourRows()
    {
        var rows = new List<BlockTextureLayer>(
            Items.Armour.Materials.Length * Items.Armour.Pieces.Length);

        foreach (var material in Items.Armour.Materials)
        foreach (var piece in Items.Armour.Pieces)
        {
            rows.Add(new BlockTextureLayer(
                Items.Armour.ItemName(material, piece),
                $"textures/item/{material.Pack}_{piece.Name}.png",
                true,
                $"textures/items/{material.Pack}_{piece.Name}.png",
                Borrow: material.Borrow.Length == 0
                    ? ""
                    : $"textures/item/{material.Borrow}_{piece.Name}.png",
                BorrowTint: material.Borrow.Length == 0 ? 0 : material.Tint));
        }

        return [.. rows];
    }

    /// <summary>One row per colour of wool, off the colour table rather than written out.</summary>
    /// <remarks>
    /// ⚠ <b>Two pack paths, and both are needed.</b> The modern layout is <c>white_wool.png</c>; the
    /// pre-flattening one is <c>wool_colored_white.png</c>, and its grey is spelled <c>silver</c>
    /// rather than <c>light_gray</c> — which is why the stem comes off the table instead of being
    /// derived from our own name. Sixteen rows written by hand would be sixteen chances to get one
    /// of those wrong, in a way that shows up as one colour of wool keeping our art.
    /// </remarks>
    private static BlockTextureLayer[] WoolRows()
    {
        var rows = new BlockTextureLayer[StarterBlocks.Colours.Length];

        for (var i = 0; i < rows.Length; i++)
        {
            var dye = StarterBlocks.Colours[i];
            var old = dye.Pack == "light_gray" ? "silver" : dye.Pack;

            rows[i] = new BlockTextureLayer(
                $"wool_{dye.Name}",
                $"textures/block/{dye.Pack}_wool.png",
                false,
                $"textures/block/wool_colored_{old}.png");
        }

        return rows;
    }

    /// <summary>And one per powder.</summary>
    private static BlockTextureLayer[] DyeRows()
    {
        var rows = new BlockTextureLayer[StarterBlocks.Colours.Length];

        for (var i = 0; i < rows.Length; i++)
        {
            var dye = StarterBlocks.Colours[i];

            // ⚠ The old layout kept every dye in ONE file, <c>dye_powder_{colour}.png</c>, and its
            // blue is <c>lapis_lazuli</c> — a mineral rather than a dye. Ours is a dye made from
            // azurite, so the modern path is the one that matches and the alternate is best effort.
            rows[i] = new BlockTextureLayer(
                $"dye_{dye.Name}",
                $"textures/item/{dye.Pack}_dye.png",
                true,
                $"textures/item/dye_powder_{(dye.Pack == "light_gray" ? "silver" : dye.Pack)}.png");
        }

        return rows;
    }

    /// <param name="GrassMap">Grass colormap, the pack's if it ships one.</param>
    /// <param name="FoliageMap">Foliage colormap, likewise.</param>
    /// <summary>What happened to one layer, for the report that says whether a pack is being used.</summary>
    /// <param name="From">The path it came from, or empty when it kept Driftwood's own art.</param>
    public readonly record struct LayerOutcome(string Name, bool Replaced, string From, bool Neutralised);

    /// <summary>
    /// One layer that moves: which layer, its frames, and how long each is held.
    /// </summary>
    /// <remarks>
    /// Frame 0 is already in <c>Tiles</c>, so a build that ignores this is a build with still water
    /// rather than a build with no water — which is what every earlier one was.
    /// </remarks>
    public readonly record struct LayerAnimation(int Layer, byte[][] Frames, float[] Seconds);

    public sealed record Result(
        byte[][] Tiles, int Size, string Summary, byte[] GrassMap, byte[] FoliageMap,
        IReadOnlyList<LayerOutcome> Outcomes, IReadOnlyList<LayerAnimation> Animations)
    {
        /// <summary>
        /// A line per layer: what we call it, whether the pack supplied it, and where from.
        /// </summary>
        /// <remarks>
        /// Built because "twenty of seventy-nine replaced" answers how many and not <em>which</em>,
        /// and the question anybody actually has when a pack looks like it did nothing is whether
        /// the thing they are staring at is one of the twenty. A count cannot answer that and a
        /// screenshot cannot either.
        /// </remarks>
        public string Report()
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine(Summary);
            text.AppendLine();
            text.AppendLine($"{"layer",-24} {"from the pack",-46} note");

            var replaced = 0;
            foreach (var outcome in Outcomes)
            {
                if (outcome.Replaced) replaced++;

                text.AppendLine(
                    $"{outcome.Name,-24} {(outcome.Replaced ? outcome.From : "— ours —"),-46} "
                    + (outcome.Neutralised ? "hue divided out before tinting" : ""));
            }

            text.AppendLine();
            text.AppendLine($"{replaced} of {Outcomes.Count} layers came from the pack, at {Size}x{Size}");
            return text.ToString();
        }
    }

    /// <summary>
    /// Draws Driftwood's own tiles, then lets a pack replace the ones it has.
    /// </summary>
    /// <param name="packPath">A folder or .zip to import from, or null for Driftwood's own art.</param>
    /// <param name="size">
    /// The tile size to build at, or 0 to take the pack's own. Zero is the default because a
    /// player who chose a pack has already said what resolution they want.
    /// </param>
    /// <param name="ceiling">The largest tile the machine will take. See <c>--texture-size</c>.</param>
    /// <remarks>
    /// ⛔ <b>The ceiling is applied HERE, once, and it used to be applied on the pack path only.</b>
    /// Which is the wrong path: a player running with no pack at all is the ordinary case, and
    /// <c>--texture-size 4096</c> with no pack asked for two hundred layers of 4096², which is
    /// <b>13.7 GB</b> and a process that dies before the window opens. The pack path was clamped
    /// because that is where somebody was thinking about resolution; the two paths that were not
    /// are the two where nobody was.
    /// </remarks>
    public static Result Build(string? packPath, int size = 0, int ceiling = 512)
    {
        var grass = Colormap.Grass();
        var foliage = Colormap.Foliage();

        var limit = Math.Max(TileGen.Size, ceiling);
        var asked = size;
        if (size > 0) size = Math.Clamp(size, TileGen.Size, limit);

        // ⚠ Said out loud rather than done quietly. A player who typed a number and got a different
        // one has to be told which, or the only evidence is that the game looks softer than they
        // asked for — and the number they typed is right there in their own command line.
        var reduced = asked > size ? $" (asked {asked}, {limit} is what this machine affords)" : "";

        if (string.IsNullOrWhiteSpace(packPath))
        {
            var own = size > 0 ? size : TileGen.Size;
            var plain = new byte[Layers.Length][];
            for (var i = 0; i < Layers.Length; i++) plain[i] = Own(i, own);

            var ownMoving = OwnAnimations(plain, own);
            return new Result(
                plain, own,
                $"{Layers.Length} built-in tiles at {own}x{own}{reduced}, {ownMoving.Count} moving",
                grass, foliage, Untouched(), ownMoving);
        }

        using var pack = TexturePack.Open(packPath, out var refused);
        if (pack is null)
        {
            var own = size > 0 ? size : TileGen.Size;
            var plain = new byte[Layers.Length][];
            for (var i = 0; i < Layers.Length; i++) plain[i] = Own(i, own);

            // ⛳ WHY, not merely that. "No pack at '...'" is the same four words for a mistyped path
            // and for a .rar full of the textures the player can see with their own eyes, and only
            // one of those is fixed by looking harder at the path.
            return new Result(
                plain, own,
                $"'{packPath}' — {refused ?? "not a pack"}; using built-in tiles at {own}x{own}{reduced}",
                grass, foliage, Untouched(), OwnAnimations(plain, own));
        }

        // The pack's own resolution unless somebody said otherwise, clamped to what the machine
        // will take. Asking the pack is the difference between a 512-pixel import looking like the
        // pack and looking like a bad photograph of it.
        var detected = pack.DetectResolution();
        var chosen = Math.Clamp(size > 0 ? size : detected, TileGen.Size, Math.Max(TileGen.Size, ceiling));

        var tiles = new byte[Layers.Length][];
        for (var i = 0; i < Layers.Length; i++) tiles[i] = Own(i, chosen);

        size = chosen;
        var neutralised = 0;
        var borrowed = 0;
        var outcomes = new List<LayerOutcome>(Layers.Length);
        var moving = OwnAnimations(tiles, size);

        for (var i = 0; i < Layers.Length; i++)
        {
            if (Layers[i].PackPath.Length == 0)
            {
                outcomes.Add(new LayerOutcome(Layers[i].Name, false, "", false));
                continue;
            }

            // ⛳ A 2012 pack is asked by CELL rather than by path, and it has to be tried first: it
            // ships no per-texture files at all, so every path candidate misses and the layer would
            // silently keep our art on a pack that plainly has a picture of that block in it.
            byte[]? replacement = null;
            var from = Layers[i].PackPath;

            if (pack.Dialect == PackDialect.Atlas && PackAtlas.Of(i) is { } cell)
                replacement = pack.TryLoadAtlasTile(cell.Index, size, cell.Items, out from);

            replacement ??= pack.TryLoadTile(Layers[i].PackPath, size, out from);

            if (replacement is null && Layers[i].PackPathAlt.Length > 0)
                replacement = pack.TryLoadTile(Layers[i].PackPathAlt, size, out from);

            // ⛳ LAST, AND ONLY WHEN THE MATERIAL'S OWN PICTURE IS NOT THERE. A pack that has copper
            // tools gives us copper tools; one written before the reference had any lends us the
            // shape of its iron ones and we recolour them. Either way the tier wears the pack's
            // style instead of being the one rung in seven still in ours.
            if (replacement is null && Layers[i].Borrow.Length > 0 && Layers[i].BorrowTint != 0)
            {
                replacement = pack.TryLoadTile(Layers[i].Borrow, size, out from);

                if (replacement is not null)
                {
                    Recolour(replacement, Layers[i].BorrowTint);
                    from = $"{from} recoloured";
                    borrowed++;
                }
            }

            if (replacement is null)
            {
                outcomes.Add(new LayerOutcome(Layers[i].Name, false, "", false));
                continue;
            }

            var flattened = Layers[i].Tinted && Neutralise(replacement);
            if (flattened) neutralised++;

            tiles[i] = replacement;

            // ⚠ The pack's own strip beats ours, and it replaces it rather than adding to it — a
            // layer running two animations at once would flicker between two authors' water. Read
            // off the same path the tile came from, whichever of the candidates that turned out
            // to be.
            var animated = pack.TryLoadFrames(Layers[i].PackPath, size)
                        ?? (Layers[i].PackPathAlt.Length > 0
                            ? pack.TryLoadFrames(Layers[i].PackPathAlt, size)
                            : null);

            moving.RemoveAll(a => a.Layer == i);

            if (animated is { } strip)
            {
                if (Layers[i].Tinted) foreach (var frame in strip.Frames) Neutralise(frame);

                moving.Add(new LayerAnimation(i, strip.Frames, strip.Seconds));

                // The tile that sits in the array until the clock moves has to be the first frame
                // played, not the first frame stored — a pack whose `frames` list starts elsewhere
                // would otherwise show one wrong picture until the first tick.
                tiles[i] = strip.Frames[0];
            }

            outcomes.Add(new LayerOutcome(Layers[i].Name, true, from, flattened));
        }

        // Colormaps are loaded at their own fixed size rather than the tile size, because the
        // lookup indexes them by climate rather than sampling them across a face.
        var packGrass = pack.TryLoadTile("textures/colormap/grass.png", Colormap.Size);
        var packFoliage = pack.TryLoadTile("textures/colormap/foliage.png", Colormap.Size);

        var colormaps = (packGrass is not null ? 1 : 0) + (packFoliage is not null ? 1 : 0);
        if (packGrass is not null) grass = packGrass;
        if (packFoliage is not null) foliage = packFoliage;

        var resolution = chosen == detected
            ? $"{chosen}px, its own"
            : $"{chosen}px, painted at {detected}";

        var summary = $"pack '{pack.Name}'"
                    + (pack.Description.Length > 0 ? $" — {pack.Description}" : "")
                    + $" ({pack.Dialect.ToString().ToLowerInvariant()} format {pack.Format}, {resolution}{reduced}): "
                    + $"{pack.Loaded - colormaps} of {Layers.Length} layers replaced"
                    + (colormaps > 0 ? $", {colormaps} colormaps" : ", built-in colormaps")
                    + (pack.Namespaces.Count > 1 ? $", {pack.Namespaces.Count} namespaces" : "")
                    + (neutralised > 0 ? $", {neutralised} tinted layers flattened" : "")
                    + (borrowed > 0 ? $", {borrowed} borrowed from another material and recoloured" : "")
                    + (pack.Faults.Count > 0 ? $", {pack.Faults.Count} unreadable: {pack.Faults[0]}" : "");

        return new Result(tiles, size, summary, grass, foliage, outcomes, moving);
    }

    /// <summary>
    /// The layers of our own art that move, and their frames.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Ours has to be a strip too, and that is not symmetry for its own sake.</b> Every pack in
    /// the genre ships water as thirty-two pictures because still water reads as blue rock — so with
    /// only the pack path animated, importing somebody else's art would be the only way to get a lake
    /// that moves, in a game whose entire art set is drawn in code. Frame 0 is written back over the
    /// still tile so a build that never ticks the clock is unchanged.
    /// </remarks>
    private static List<LayerAnimation> OwnAnimations(byte[][] tiles, int size)
    {
        var moving = new List<LayerAnimation>();

        // Sixteen frames over two seconds. Fewer and the swell steps; more and it costs memory at a
        // pack's resolution for motion nobody can see.
        const int Frames = 16;
        const float Seconds = 2f / Frames;

        var times = new float[Frames];
        Array.Fill(times, Seconds);

        // ⚠ Lava runs at a fifth of water's rate. Molten rock that ripples like a pond reads as
        // orange water, and the pace is most of what says otherwise before the colour does.
        var slow = new float[Frames];
        Array.Fill(slow, Seconds * 5f);

        Add(StarterBlocks.LayerWater, TileGen.WaterFrames(1006, Frames, 41, 92, 158), times);
        Add(StarterBlocks.LayerWaterFlow, TileGen.FlowFrames(1090, Frames, 41, 92, 158, 11f), times);
        Add(StarterBlocks.LayerLava, TileGen.LavaFrames(1091, Frames), slow);
        Add(StarterBlocks.LayerLavaFlow, TileGen.FlowFrames(1092, Frames, 176, 74, 22, 46f), slow);

        return moving;

        void Add(ushort layer, byte[][] frames, float[] hold)
        {
            var scaled = Upscale(frames, size);
            moving.Add(new LayerAnimation(layer, scaled, hold));

            // Frame 0 written back over the still tile, so a build that never ticks the clock is
            // unchanged rather than blank.
            tiles[layer] = scaled[0];
        }
    }

    /// <summary>Nearest-neighbour, the same way <see cref="Own"/> takes a 16px tile to the pack's size.</summary>
    private static byte[][] Upscale(byte[][] frames, int size)
    {
        if (size == TileGen.Size) return frames;

        var scaled = new byte[frames.Length][];
        for (var f = 0; f < frames.Length; f++) scaled[f] = TileGen.Upscale(frames[f], size);
        return scaled;
    }

    /// <summary>
    /// Per layer, whether it is a cut-out — one the shader discards texels of rather than blending.
    /// </summary>
    /// <remarks>
    /// Read straight off the layer table, which has carried the flag since the first import, so
    /// nothing new has to be decided and a layer added tomorrow answers for itself.
    /// </remarks>
    public static bool[] Cutouts()
    {
        var flags = new bool[Layers.Length];
        for (var i = 0; i < Layers.Length; i++) flags[i] = Layers[i].Cutout;
        return flags;
    }

    /// <summary>Every layer, all of them ours. What a run with no pack reports.</summary>
    private static List<LayerOutcome> Untouched()
    {
        var outcomes = new List<LayerOutcome>(Layers.Length);
        foreach (var layer in Layers) outcomes.Add(new LayerOutcome(layer.Name, false, "", false));
        return outcomes;
    }

    /// <summary>
    /// Takes the baked-in hue out of a texture the climate colour is about to be multiplied over.
    /// </summary>
    /// <returns>True when the texture needed it.</returns>
    /// <remarks>
    /// <para>Grass, leaves and vines are drawn near-colourless by the format's own convention,
    /// because the game is expected to multiply a climate colour over them. Plenty of packs do not
    /// follow it — a "realistic" pack in particular paints its grass the green it wants and expects
    /// to be left alone. Multiplying a green tint over an already-green texture gives a dark muddy
    /// green that gets worse the better the pack is, and it looks like the pack rather than like us.
    /// </para>
    /// <para>The fix is to divide the hue back out rather than to skip the tint. Skipping it would
    /// lose the biome variation entirely and make every meadow in the world the same colour; this
    /// keeps the author's brightness and every bit of their detail, and hands the hue back to the
    /// climate, which is what tinting is for. A texture already neutral is left completely alone.
    /// </para>
    /// </remarks>
    private static bool Neutralise(byte[] tile)
    {
        double r = 0, g = 0, b = 0;
        var counted = 0;

        for (var i = 0; i < tile.Length; i += 4)
        {
            if (tile[i + 3] < 8) continue;      // clear pixels carry no colour to measure
            r += tile[i];
            g += tile[i + 1];
            b += tile[i + 2];
            counted++;
        }

        if (counted == 0) return false;

        r /= counted;
        g /= counted;
        b /= counted;

        var high = Math.Max(r, Math.Max(g, b));
        var low = Math.Min(r, Math.Min(g, b));
        if (low < 1.0) low = 1.0;

        // A vanilla grass tile sits within a few percent of grey. A third out is well past anything
        // that could be called incidental, and well under anything that would catch a stone.
        if (high / low < 1.25) return false;

        var mean = (r + g + b) / 3.0;
        double sr = mean / r, sg = mean / g, sb = mean / b;

        for (var i = 0; i < tile.Length; i += 4)
        {
            tile[i] = Clamp(tile[i] * sr);
            tile[i + 1] = Clamp(tile[i + 1] * sg);
            tile[i + 2] = Clamp(tile[i + 2] * sb);
        }

        return true;

        static byte Clamp(double v) => (byte)Math.Clamp(v, 0.0, 255.0);
    }

    /// <summary>
    /// Driftwood's own tile for one layer, at the native sixteen, for an instrument to look at.
    /// </summary>
    /// <remarks>
    /// ⛳ The one way in from outside. Everything else here builds a whole array against a pack, which
    /// is the wrong shape for a sheet that wants to show what we ship — and a second copy of the
    /// layer-to-drawing table written for the instrument would be a copy that drifts.
    /// </remarks>
    /// <summary>
    /// One layer exactly as a no-pack build would wear it, for the icon sheet.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>This was <c>Draw(layer)</c>, and for every generated layer that is the same picture —
    /// which is precisely why it went unnoticed.</b> The moment one layer was PAINTED rather than
    /// drawn, the instrument built to look at our art started showing something the game does not
    /// use: the sheet reported the generated stand-in while the button wore the painting, and the
    /// two look different enough that the difference was mistaken for the feature not working. An
    /// instrument that answers a slightly different question than the game is the failure this
    /// project has already paid for once with <c>--pack-coverage</c>.
    /// </remarks>
    public static byte[] OwnTile(int layer) => Own(layer, TileGen.Size);

    /// <summary>Driftwood's own art for one layer.</summary>
    private static byte[] Own(int layer, int size)
    {
        // ⛳ The painted ones go straight to the size the array is being built at, rather than
        // through a sixteen-pixel intermediate. They have real pixels to give and upscaling from 16
        // would throw all of them away — see PaintedArt. Falls through to the generator when a build
        // does not carry the resource, so a mis-assembled artifact is a plain button rather than a
        // game that will not start.
        if (Painted(layer) is { } name && PaintedArt.Tile(name, size) is { } painted) return painted;

        // Drawn at the native tile size and then scaled, so the generators stay written for one
        // size rather than being parameterised over every resolution a pack might arrive at.
        var tile = Draw(layer);
        return TileGen.Upscale(tile, size);
    }

    /// <summary>
    /// Recolours a borrowed texture to another metal, in place, keeping its own shading.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>The texture's own light times the target colour.</b> What makes an iron pickaxe
    /// look like a pickaxe is where it is bright and where it is dark, and that survives being
    /// multiplied; what makes it look like iron is that the three channels are equal, and that is
    /// exactly what is being replaced. Luminance rather than a plain average, because a flat mean
    /// reads a saturated highlight as darker than the eye does and the edge of a blade goes dull.
    /// </para>
    /// <para>⚠ <b>Scaled so mid-grey lands on the target rather than so white does.</b> Multiplying
    /// straight would only ever darken — a metal is mostly middling values, so the result came out
    /// as a brown smear with two bright pixels. Normalising against the source's own middle keeps
    /// the range it was painted with.</para>
    /// <para>⚠ Alpha is untouched. The silhouette is the pack author's and recolouring is not
    /// permission to reshape it.</para>
    /// </remarks>
    /// <summary>Recolours a copy, for a check that wants both the before and the after.</summary>
    public static byte[] RecolourFor(byte[] tile, uint rgb)
    {
        var copy = (byte[])tile.Clone();
        Recolour(copy, rgb);
        return copy;
    }

    private static void Recolour(byte[] tile, uint rgb)
    {
        var tr = (rgb >> 16) & 0xFF;
        var tg = (rgb >> 8) & 0xFF;
        var tb = rgb & 0xFF;

        // The source's own mid-point, over the pixels that are actually drawn. A texture painted
        // dark and one painted bright must both come out the target's colour rather than the
        // target's colour times how bright somebody happened to paint their iron.
        float sum = 0f, count = 0f;

        for (var i = 0; i < tile.Length; i += 4)
        {
            if (tile[i + 3] < 8) continue;
            sum += 0.299f * tile[i] + 0.587f * tile[i + 1] + 0.114f * tile[i + 2];
            count++;
        }

        if (count <= 0f) return;

        var middle = MathF.Max(1f, sum / count);

        for (var i = 0; i < tile.Length; i += 4)
        {
            if (tile[i + 3] < 8) continue;

            var light = (0.299f * tile[i] + 0.587f * tile[i + 1] + 0.114f * tile[i + 2]) / middle;

            tile[i] = (byte)Math.Clamp((int)MathF.Round(tr * light), 0, 255);
            tile[i + 1] = (byte)Math.Clamp((int)MathF.Round(tg * light), 0, 255);
            tile[i + 2] = (byte)Math.Clamp((int)MathF.Round(tb * light), 0, 255);
        }
    }

    /// <summary>Which painted resource a layer takes, or null when it is generated like the rest.</summary>
    public static string? Painted(int layer) =>
        layer == StarterBlocks.LayerRecipeBook ? PaintedArt.RecipeBook : null;

    private static byte[] Draw(int layer)
    {
        var stone = TileGen.Speckle(1001, 128, 128, 133, 16, 0.45f);
        var dirt = TileGen.Speckle(1002, 118, 85, 57, 18, 0.5f);
        var deepstone = TileGen.Speckle(1018, 62, 62, 68, 14, 0.55f);

        return layer switch
        {
            StarterBlocks.LayerStone => stone,
            StarterBlocks.LayerDirt => dirt,
            StarterBlocks.LayerGrassTop => TileGen.Speckle(1003, 92, 153, 66, 20, 0.55f),

            // Grey, with the green arriving through the overlay below it. See TileGen.GrassSide.
            StarterBlocks.LayerGrassSide => TileGen.GrassSide(1004, dirt, 138),
            StarterBlocks.LayerGrassSideOverlay => TileGen.GrassSideOverlay(1004, 138),
            StarterBlocks.LayerSand => TileGen.Speckle(1005, 212, 199, 148, 12, 0.3f),
            StarterBlocks.LayerWater => TileGen.Speckle(1006, 41, 92, 158, 14, 0.6f),
            StarterBlocks.LayerGravel => TileGen.Speckle(1007, 128, 122, 118, 26, 0.7f),
            StarterBlocks.LayerLogSide => TileGen.Bark(1008, 105, 79, 48),
            StarterBlocks.LayerLogTop => TileGen.Rings(1009, 148, 118, 76),
            StarterBlocks.LayerLeaves => TileGen.Leaves(1010, 61, 120, 51, 0.22f),
            StarterBlocks.LayerPlanks => TileGen.Planks(1011, 179, 143, 87),
            StarterBlocks.LayerCoalOre => TileGen.Ore(1012, stone, 38, 38, 40, 5),
            StarterBlocks.LayerIronOre => TileGen.Ore(1013, stone, 176, 142, 112, 5),
            StarterBlocks.LayerBedrock => TileGen.Speckle(1014, 40, 40, 44, 30, 0.8f),
            StarterBlocks.LayerEmberstone => TileGen.Ember(1015, TileGen.Speckle(1016, 74, 60, 54, 14, 0.5f), 255, 158, 76),
            StarterBlocks.LayerVine => TileGen.Vine(1017, 62, 112, 48),
            StarterBlocks.LayerDeepstone => deepstone,

            // The three intrusions are the same rock at three temperatures of grey, which is close
            // to what tells them apart in life. Diorite is the pale one, granite the pink.
            StarterBlocks.LayerCoralstone => TileGen.Speckle(1019, 154, 111, 97, 20, 0.5f),
            StarterBlocks.LayerDriftstone => TileGen.Speckle(1020, 134, 136, 132, 18, 0.5f),
            StarterBlocks.LayerSaltstone => TileGen.Speckle(1021, 202, 202, 205, 22, 0.6f),
            StarterBlocks.LayerCopperOre => TileGen.Ore(1022, stone, 196, 112, 62, 5),
            StarterBlocks.LayerGoldOre => TileGen.Ore(1023, stone, 232, 196, 82, 4),
            StarterBlocks.LayerStormglassOre => TileGen.Ore(1024, stone, 118, 224, 220, 4),
            StarterBlocks.LayerAzuriteOre => TileGen.Ore(1025, stone, 54, 82, 190, 5),
            StarterBlocks.LayerClay => TileGen.Speckle(1026, 160, 166, 176, 8, 0.35f),
            StarterBlocks.LayerSandstone => TileGen.Strata(1027, 214, 199, 152),
            StarterBlocks.LayerSandstoneTop => TileGen.Speckle(1028, 219, 205, 160, 10, 0.4f),
            StarterBlocks.LayerSnow => TileGen.Speckle(1029, 243, 246, 250, 7, 0.3f),
            StarterBlocks.LayerMeadowgrass => TileGen.Tuft(1030, 96, 148, 62),
            StarterBlocks.LayerSeaflax => TileGen.Flower(1031, 74, 118, 58, 78, 116, 208, 226, 232, 118),
            StarterBlocks.LayerMarshlily => TileGen.Flower(1032, 82, 126, 62, 236, 238, 232, 236, 196, 84),
            StarterBlocks.LayerTorch => TileGen.Torch(1033),

            StarterBlocks.LayerRubble => TileGen.Cobble(1034, 126, 126, 130),
            StarterBlocks.LayerGlass => TileGen.Glass(1035),
            StarterBlocks.LayerBricks => TileGen.Bricks(1036, 154, 90, 74, 168),

            // Cut stone. Each worked form takes the colour of the rock it is cut from — the same
            // numbers, a few lines up — so a polished coralstone reads as coralstone that has been
            // worked rather than as a new rock somebody happened to draw in the same palette. That
            // relationship is the whole point of the axis and it is one a generator can hold and a
            // hand-drawn set cannot.
            StarterBlocks.LayerStoneBricks => TileGen.Bricks(1051, 122, 122, 126, 104),
            StarterBlocks.LayerSmoothStone => TileGen.Polished(1052, 132, 132, 136),
            StarterBlocks.LayerDeepstonePolished => TileGen.Polished(1053, 74, 74, 80),
            StarterBlocks.LayerDeepstoneBricks => TileGen.Bricks(1054, 74, 74, 80, 60),
            StarterBlocks.LayerCoralstonePolished => TileGen.Polished(1019, 154, 111, 97),
            StarterBlocks.LayerDriftstonePolished => TileGen.Polished(1020, 134, 136, 132),
            StarterBlocks.LayerSaltstonePolished => TileGen.Polished(1021, 202, 202, 205),
            StarterBlocks.LayerSandstoneCut => TileGen.CutBlock(1027, 214, 199, 152),
            StarterBlocks.LayerSandstoneChiseled => TileGen.Chiselled(1027, 214, 199, 152),

            // Light. The lantern is iron round a flame, so it takes the iron ingot's own colour;
            // the lamp is stormglass set solid, so it takes the gem's. Both relationships are the
            // same one the worked rocks have with the rock they were cut from.
            StarterBlocks.LayerLantern => TileGen.LanternTile(1056, 176, 176, 184),
            StarterBlocks.LayerCampfireFire => TileGen.Fire(1057),
            StarterBlocks.LayerSmokeglass => TileGen.Smokeglass(1058),
            StarterBlocks.LayerStormglassLamp => TileGen.Lamp(1059, 132, 210, 214),

            // Things that open, all cut from the same timber the planks are, so a door in a plank
            // wall reads as part of it rather than as something bolted on.
            StarterBlocks.LayerLadder => TileGen.Ladder(1060, 152, 118, 70),
            StarterBlocks.LayerDoorLower => TileGen.Door(1061, 168, 132, 80, upper: false),
            StarterBlocks.LayerDoorUpper => TileGen.Door(1061, 168, 132, 80, upper: true),
            StarterBlocks.LayerTrapdoor => TileGen.Trapdoor(1062, 164, 128, 76),

            StarterBlocks.LayerChestTop => TileGen.ChestFace(1063, 158, 118, 68, front: false, lid: true),
            StarterBlocks.LayerChestSide => TileGen.ChestFace(1063, 158, 118, 68, front: false, lid: false),
            StarterBlocks.LayerChestFront => TileGen.ChestFace(1063, 158, 118, 68, front: true, lid: false),

            // Stations. The bench's front takes the same planks its side does, so the two read as
            // one piece of furniture seen from two angles rather than as two blocks.
            StarterBlocks.LayerBenchFront => TileGen.BenchFront(1038, 168, 132, 80),
            StarterBlocks.LayerStonecutterTop => TileGen.StonecutterTop(1064, 122, 122, 128),
            StarterBlocks.LayerStonecutterSide => TileGen.StonecutterSide(1064, 122, 122, 128),

            // The bench is planks that have been worked on: grooves where a straight edge was laid
            // and nicks where it was not.
            StarterBlocks.LayerBenchTop => TileGen.Scored(1037, TileGen.Planks(1011, 179, 143, 87)),
            StarterBlocks.LayerBenchSide => TileGen.Panel(TileGen.Planks(1038, 168, 132, 80), 3, 26),

            StarterBlocks.LayerFurnaceTop => TileGen.Speckle(1039, 112, 110, 112, 14, 0.5f),
            StarterBlocks.LayerFurnaceSide => TileGen.Cobble(1040, 116, 114, 116),
            StarterBlocks.LayerFurnaceFront =>
                TileGen.Hearth(1041, TileGen.Cobble(1040, 116, 114, 116), lit: false),
            StarterBlocks.LayerFurnaceFrontLit =>
                TileGen.Hearth(1042, TileGen.Cobble(1040, 116, 114, 116), lit: true),

            // Deepstone brick rather than cobble, and a letterbox rather than an arch. Both, so the
            // two are told apart by shape as well as by shade — see the note on TileGen.Hearth.
            StarterBlocks.LayerBlastTop => TileGen.Speckle(1080, 74, 74, 82, 12, 0.5f),
            StarterBlocks.LayerBlastSide => TileGen.Bricks(1081, 78, 78, 86, 52),
            StarterBlocks.LayerBlastFront =>
                TileGen.Hearth(1082, TileGen.Bricks(1081, 78, 78, 86, 52), lit: false, slot: true),
            StarterBlocks.LayerBlastFrontLit =>
                TileGen.Hearth(1083, TileGen.Bricks(1081, 78, 78, 86, 52), lit: true, slot: true),

            StarterBlocks.LayerStick => TileGen.IconStick(1043, 150, 112, 66),
            StarterBlocks.LayerCoal => TileGen.IconLump(1044, 46, 44, 46),
            StarterBlocks.LayerCharcoal => TileGen.IconLump(1045, 62, 54, 48),
            StarterBlocks.LayerRawCopper => TileGen.IconLump(1046, 190, 118, 74),
            StarterBlocks.LayerRawIron => TileGen.IconLump(1047, 190, 162, 140),
            StarterBlocks.LayerRawGold => TileGen.IconLump(1048, 226, 190, 84),
            StarterBlocks.LayerCopperIngot => TileGen.IconIngot(1049, 200, 116, 66),
            StarterBlocks.LayerIronIngot => TileGen.IconIngot(1050, 214, 214, 220),
            StarterBlocks.LayerGoldIngot => TileGen.IconIngot(1051, 236, 200, 86),
            StarterBlocks.LayerStormglass => TileGen.IconGem(1052, 118, 214, 214),
            StarterBlocks.LayerAzurite => TileGen.IconGem(1053, 58, 88, 194),
            StarterBlocks.LayerClayLump => TileGen.IconLump(1054, 164, 170, 180),
            StarterBlocks.LayerBrick => TileGen.IconBrick(1055, 158, 94, 78),

            // The two flowers the dye tree needed. A bloom and a stem, the colour the flower is.
            StarterBlocks.LayerEmberbloom => TileGen.Flower(1092, 66, 112, 52, 176, 34, 30, 226, 196, 82),
            StarterBlocks.LayerSunwort => TileGen.Flower(1093, 78, 124, 54, 236, 198, 44, 250, 244, 190),

            // What comes off the animals with the fleece.
            StarterBlocks.LayerLeather => TileGen.IconLeather(1085, 166, 116, 70),
            StarterBlocks.LayerFeather => TileGen.IconFeather(1086, 238, 240, 244),
            StarterBlocks.LayerEgg => TileGen.IconEgg(1087, 232, 220, 198),
            StarterBlocks.LayerShears => TileGen.IconShears(1088, 206, 208, 214),

            // What the dark leaves behind. ⚠ Rotten flesh is a meat cut in a spoiled palette rather
            // than a drawing of its own — it is the same thing off a different animal, which is
            // exactly what the shape argument for the meats says it should look like.
            StarterBlocks.LayerString => TileGen.IconSkein(1094, 238, 238, 236),
            StarterBlocks.LayerBone => TileGen.IconBone(1095, 234, 232, 216),
            StarterBlocks.LayerRottenFlesh =>
                TileGen.IconMeat(1096, 128, 78, 74, TileGen.MeatShape.Cut, cooked: false),

            // The fluids' first frames. OwnAnimations writes these back over the top with frame 0 of
            // the real strip, so these are what a build that never ticks a clock draws — a still
            // answer for every layer rather than a hole where the moving ones should be.
            StarterBlocks.LayerWaterFlow => TileGen.FlowFrames(1090, 1, 41, 92, 158, 11f)[0],
            StarterBlocks.LayerLava => TileGen.LavaFrames(1091, 1)[0],
            StarterBlocks.LayerLavaFlow => TileGen.FlowFrames(1092, 1, 176, 74, 22, 46f)[0],

            StarterBlocks.LayerBucket => TileGen.IconBucket(1097, false, 0, 0, 0),
            StarterBlocks.LayerWaterBucket => TileGen.IconBucket(1098, true, 52, 108, 178),
            StarterBlocks.LayerLavaBucket => TileGen.IconBucket(1099, true, 214, 108, 34),

            // Darker and shinier than the ore it is made of, so a wall of it does not read as a
            // seam of coal in stone — a storage block and its ore are seen side by side constantly.
            StarterBlocks.LayerCoalBlock => TileGen.Speckle(1101, 30, 30, 34, 22, 0.75f),

            StarterBlocks.LayerFlame => TileGen.Flame(1102),
            StarterBlocks.LayerSmoke => TileGen.Smoke(1103),

            // ⛳ Diamond, in the deepstone it forms in rather than in plain stone: it is the one ore
            // that never appears above the Emberdeep, so a seam of it drawn in grey rock would be a
            // picture of somewhere it cannot be.
            StarterBlocks.LayerDiamondOre => TileGen.Ore(
                1111, TileGen.Speckle(1016, 58, 58, 66, 16, 0.6f),
                Items.Armour.DiamondR, Items.Armour.DiamondG, Items.Armour.DiamondB, 5),

            StarterBlocks.LayerDiamond => TileGen.IconGem(
                1112, Items.Armour.DiamondR, Items.Armour.DiamondG, Items.Armour.DiamondB),

            StarterBlocks.LayerPaper => TileGen.IconScroll(1113),

            // ⚠ The generated stand-in for the painted one, reached only by a build that lost its
            // embedded resource. It exists so that failure is a plain book rather than the magenta
            // placeholder, which reads as a missing texture in a pack and sends somebody hunting.
            StarterBlocks.LayerRecipeBook => TileGen.IconBook(1114),

            // ⛳ THE SMOKER IS TIMBER, and that is the whole design of its art. The furnace is grey
            // cobble with an arch and the blast furnace dark brick with a letterbox; a third grey
            // box would have to be told apart by shade alone, which survives neither a wall at
            // distance nor a sixteen-pixel slot. Its mouth is an arch, like the furnace it is a
            // specialisation of, because the two do the same job on different things.
            StarterBlocks.LayerSmokerTop => TileGen.Scored(1105, TileGen.Planks(1106, 146, 108, 62)),
            StarterBlocks.LayerSmokerSide => TileGen.Planks(1106, 146, 108, 62),
            StarterBlocks.LayerSmokerFront =>
                TileGen.Hearth(1107, TileGen.Planks(1106, 146, 108, 62), lit: false),
            StarterBlocks.LayerSmokerFrontLit =>
                TileGen.Hearth(1108, TileGen.Planks(1106, 146, 108, 62), lit: true),

            // A barrel: staves banded round with a lid on top. Darker than the bench's planks so a
            // room with both in it reads as two things.
            StarterBlocks.LayerBarrelTop => TileGen.Scored(1109, TileGen.Planks(1110, 128, 94, 54)),
            StarterBlocks.LayerBarrelSide => TileGen.Panel(TileGen.Planks(1110, 128, 94, 54), 2, 34),

            // ⛳ The anvil is dark worked iron. Its face is scored where it has been struck, and each
            // stage takes more of it — which is the same Scored/Panel vocabulary the stations use.
            StarterBlocks.LayerAnvilSide => TileGen.Panel(TileGen.Speckle(1130, 74, 74, 80, 9, 0.4f), 3, 30),
            StarterBlocks.LayerAnvilTop => TileGen.Speckle(1131, 96, 96, 102, 7, 0.3f),
            StarterBlocks.LayerAnvilChipped => TileGen.Ore(
                1132, TileGen.Speckle(1131, 96, 96, 102, 7, 0.3f), 58, 56, 54, 4),
            StarterBlocks.LayerAnvilDamaged => TileGen.Ore(
                1133, TileGen.Speckle(1131, 90, 90, 96, 9, 0.35f), 48, 44, 42, 9),

            // ⛳ Tilled ground is dirt turned over: same colour, combed into rows. Wet is the same
            // tile darkened, because that is what wet earth is and a player has to tell them apart
            // from standing height across a field.
            StarterBlocks.LayerFarmland => TileGen.Tilled(1134, 118, 85, 57),
            StarterBlocks.LayerFarmlandWet => TileGen.Tilled(1135, 78, 54, 34),

            // ⚠ The hoe borrows IconTool's shovel shape — a blade on a haft is the same silhouette
            // at sixteen pixels, and drawing a fifth tool shape to be told apart from a shovel it is
            // never held beside would be a drawing nobody can read either way.
            StarterBlocks.LayerHoe => TileGen.IconTool(1150, 2, 196, 196, 202),

            StarterBlocks.LayerSeeds => TileGen.IconGrains(1151, 150, 176, 96),
            StarterBlocks.LayerWheatItem => TileGen.Wheat(1152, 14, 214, 186, 74, eared: true),
            StarterBlocks.LayerBread => TileGen.IconMeat(1153, 186, 138, 82, TileGen.MeatShape.Cut, cooked: true),
            StarterBlocks.LayerBonemeal => TileGen.IconPile(1154, 214, 214, 222),

            // ⚠ A baked potato is the raw one gone browner and softer at the edges, drawn round
            // rather than tapered — so the pair reads as the same vegetable before and after a fire.
            StarterBlocks.LayerBakedPotato =>
                TileGen.IconRoot(1190, 166, 122, 62, 132, 96, 48, tapered: false),

            // The composter's side is a framed panel of weathered planks — the bench side's own
            // recipe, darker. ⚠ Not the trapdoor tile, whose slats have real holes: this face is
            // marked opaque and the audit refuses an opaque layer with clear pixels in it. The
            // floor is darker planks; the fill is humus, and the ready fill is the same humus
            // flecked pale with the bone meal in it.
            StarterBlocks.LayerComposterSide =>
                TileGen.Panel(TileGen.Planks(1310, 148, 116, 74), 2, 22),
            StarterBlocks.LayerComposterBottom => TileGen.Planks(1311, 122, 96, 60),
            StarterBlocks.LayerCompost => TileGen.Speckle(1312, 82, 64, 40, 22, 0.55f),
            StarterBlocks.LayerCompostReady =>
                TileGen.Ore(1313, TileGen.Speckle(1312, 82, 64, 40, 22, 0.55f), 216, 214, 206, 7),

            // ⚠ One seed for both states of the bush, so the ripe tile is the young tile with
            // fruit on it — one plant in two moments, never two plants.
            StarterBlocks.LayerBerryBush => TileGen.BerryBush(1320, ripe: false),
            StarterBlocks.LayerBerryBushRipe => TileGen.BerryBush(1320, ripe: true),
            StarterBlocks.LayerBerries => TileGen.IconBerries(1322),

            StarterBlocks.LayerMushroomBrown =>
                TileGen.Mushroom(1330, 148, 106, 66, 224, 214, 192, spotted: false, ground: true),
            StarterBlocks.LayerMushroomRed =>
                TileGen.Mushroom(1331, 198, 52, 46, 224, 214, 192, spotted: true, ground: true),
            StarterBlocks.LayerRoastedMushroom =>
                TileGen.Mushroom(1332, 116, 78, 46, 186, 142, 88, spotted: false, ground: false),

            _ => Wheat(layer) ?? Crop(layer) ?? CropIcon(layer) ?? StainedGlass(layer)
                 ?? MetalBlock(layer) ?? Wool(layer) ?? Meat(layer) ?? Dye(layer)
                 ?? Armour(layer) ?? Shield(layer) ?? Tool(layer),
        };
    }

    /// <summary>One stage of wheat, or null when this layer is not one.</summary>
    /// <remarks>
    /// ⛳ <b>Taller and yellower as it goes.</b> The two things that read across a field are height
    /// and colour: a seedling is a short green tuft and a ripe ear is a tall gold one, and a player
    /// standing at the edge of a field has to be able to see which rows are ready without walking
    /// them. Drawn as blades from the ground up so the silhouette grows rather than the tile filling.
    /// </remarks>
    /// <summary>One colour of stained glass, or null when this layer is not one.</summary>
    /// <remarks>
    /// ⛔ <b>The pane is FILLED, unlike plain glass, and that is the whole difference.</b>
    /// <c>TileGen.Glass</c> paints a frame and a streak and leaves the middle empty, because a clear
    /// window IS its hole. A coloured one has to have something in the middle to be coloured at all —
    /// the glass is the point rather than the frame round it.
    /// </remarks>
    private static byte[]? StainedGlass(int layer)
    {
        var index = layer - StarterBlocks.LayerFirstStainedGlass;
        if (index < 0 || index >= StarterBlocks.Colours.Length) return null;

        var dye = StarterBlocks.Colours[index];
        return TileGen.StainedGlass(1200 + index, dye.R, dye.G, dye.B);
    }

    /// <summary>One stage of a root crop's tops, or null when this layer is not one.</summary>
    /// <remarks>
    /// ⛳ <b>The tops go green and the ROOT shows at the end.</b> Every stage is the same leaf colour
    /// getting taller and a little richer — because that is what the tops of all three actually do —
    /// so the only thing telling a ripe carrot from a ripe beetroot at a glance is the root breaking
    /// the surface on the last stage. Three fields side by side have to be distinguishable, and they
    /// cannot be by leaf colour: real ones are all the same green.
    /// </remarks>
    private static byte[]? Crop(int layer)
    {
        var index = layer - StarterBlocks.LayerFirstCrop;
        if (index < 0 || index >= StarterBlocks.CropCount * StarterBlocks.CropStages) return null;

        var crop = StarterBlocks.Crops[index / StarterBlocks.CropStages];
        var stage = index % StarterBlocks.CropStages;
        var t = stage / (float)(StarterBlocks.CropStages - 1);

        return TileGen.RootCrop(
            1160 + index,
            height: (int)MathF.Round(float.Lerp(4f, 12f, t)),
            leafR: (byte)float.Lerp(crop.Leaf.R * 0.72f, crop.Leaf.R, t),
            leafG: (byte)float.Lerp(crop.Leaf.G * 0.78f, crop.Leaf.G, t),
            leafB: (byte)float.Lerp(crop.Leaf.B * 0.72f, crop.Leaf.B, t),
            rootR: crop.Root.R, rootG: crop.Root.G, rootB: crop.Root.B,
            showRoot: stage == StarterBlocks.CropStages - 1);
    }

    /// <summary>And what one is carried as, or null when this layer is not one.</summary>
    private static byte[]? CropIcon(int layer)
    {
        var index = layer - StarterBlocks.LayerFirstCropItem;
        if (index < 0 || index >= StarterBlocks.CropCount) return null;

        var crop = StarterBlocks.Crops[index];

        return TileGen.IconRoot(
            1180 + index,
            crop.Root.R, crop.Root.G, crop.Root.B,
            crop.Leaf.R, crop.Leaf.G, crop.Leaf.B,
            tapered: crop.Name != "potato");
    }

    private static byte[]? Wheat(int layer)
    {
        var stage = layer - StarterBlocks.LayerFirstWheat;
        if (stage < 0 || stage >= StarterBlocks.WheatStages) return null;

        var t = stage / (float)(StarterBlocks.WheatStages - 1);

        return TileGen.Wheat(
            1140 + stage,
            height: (int)MathF.Round(float.Lerp(5f, 15f, t)),
            r: (byte)float.Lerp(96f, 214f, t),
            g: (byte)float.Lerp(140f, 186f, t),
            b: (byte)float.Lerp(62f, 74f, t),
            eared: stage == StarterBlocks.WheatStages - 1);
    }

    /// <summary>One metal packed into a block, or null when this layer is not one.</summary>
    /// <remarks>
    /// ⚠ Brighter and far smoother than the ore it came out of. An ore is blobs of metal in rock and
    /// a block is solid metal, so the two must not share a grain — a storage block that reads as a
    /// rich seam is a block somebody mines expecting to get their ingots back nine at a time.
    /// </remarks>
    private static byte[]? MetalBlock(int layer)
    {
        var index = layer - StarterBlocks.LayerFirstMetalBlock;
        if (index < 0 || index >= StarterBlocks.MetalBlockCount) return null;

        var (_, _, _, r, g, b) = StarterBlocks.MetalBlocks[index];
        return TileGen.Panel(TileGen.Speckle(1120 + index, r, g, b, 7, 0.25f), 2, 26);
    }

    /// <summary>One shield, or null when this layer is not one.</summary>
    /// <remarks>
    /// ⚠ Timber in every case — the metal is only the boss and the rim, which is exactly what the
    /// recipe says, so the picture is readable as its own recipe at sixteen pixels.
    /// </remarks>
    private static byte[]? Shield(int layer)
    {
        var index = layer - StarterBlocks.LayerFirstShield;
        if (index < 0 || index >= StarterBlocks.ShieldCount) return null;

        var shield = Items.Armour.Shields[index];
        return TileGen.IconShield(1104 + index, 152, 118, 70, shield.R, shield.G, shield.B);
    }

    /// <summary>One piece of armour, or null when this layer is not one.</summary>
    private static byte[]? Armour(int layer)
    {
        var index = layer - StarterBlocks.LayerFirstArmour;
        if (index < 0 || index >= StarterBlocks.ArmourMaterialCount * StarterBlocks.ArmourPieceCount)
            return null;

        var material = Items.Armour.Materials[index / StarterBlocks.ArmourPieceCount];

        return TileGen.IconArmour(
            1200 + index, index % StarterBlocks.ArmourPieceCount, material.R, material.G, material.B);
    }

    /// <summary>One wool tile, or null when this layer is not one.</summary>
    /// <remarks>
    /// ⚠ <b>Not white tinted sixteen ways.</b> A tint multiplies, so every colour would come out
    /// darker than the one before it and black wool would be black — which is exactly what a hue
    /// rotation over one grey tile produces. Each is drawn from its own numbers, and
    /// <see cref="TileGen.Wool"/>'s shading is written as an offset rather than a scale so a dark
    /// colour keeps its texture instead of collapsing into a flat square.
    /// </remarks>
    private static byte[]? Wool(int layer)
    {
        var index = layer - StarterBlocks.LayerFirstWool;
        if (index < 0 || index >= StarterBlocks.Colours.Length) return null;

        var dye = StarterBlocks.Colours[index];
        return TileGen.Wool(1200 + index, dye.R, dye.G, dye.B);
    }

    /// <summary>One dye powder, or null when this layer is not one.</summary>
    private static byte[]? Dye(int layer)
    {
        var index = layer - StarterBlocks.LayerFirstDye;
        if (index < 0 || index >= StarterBlocks.Colours.Length) return null;

        var dye = StarterBlocks.Colours[index];
        return TileGen.IconDye(1240 + index, dye.R, dye.G, dye.B);
    }

    /// <summary>
    /// One meat icon, or null when this layer is not one.
    /// </summary>
    /// <remarks>
    /// ⚠ Null rather than the magenta, because a layer past the meats is a tool and the two ranges
    /// meet — a "no art here" answer given by the first of two handlers would swallow every tool.
    /// </remarks>
    private static byte[]? Meat(int layer)
    {
        var index = layer - StarterBlocks.LayerFirstMeat;
        if (index < 0 || index >= StarterItems.Meats.Length * 2) return null;

        var meat = StarterItems.Meats[index / 2];
        return TileGen.IconMeat(1090 + index, meat.R, meat.G, meat.B, meat.Shape, cooked: index % 2 == 1);
    }

    /// <summary>The head colour of each tool tier, in the order the layers run.</summary>
    private static readonly (byte R, byte G, byte B)[] ToolPalettes =
    [
        (158, 122, 72),     // wood
        (132, 132, 137),    // stone
        (196, 112, 62),     // copper
        (232, 196, 82),     // gold
        (214, 214, 220),    // iron
        (118, 224, 220),    // stormglass
        (Items.Armour.DiamondR, Items.Armour.DiamondG, Items.Armour.DiamondB),
    ];

    /// <summary>One tool icon, or the loud magenta that says a layer has no art behind it.</summary>
    private static byte[] Tool(int layer)
    {
        var index = layer - StarterBlocks.LayerFirstTool;
        if (index < 0 || index >= ToolPalettes.Length * StarterBlocks.ToolShapeCount)
            return TileGen.Solid(255, 0, 255, 255);

        var (r, g, b) = ToolPalettes[index / StarterBlocks.ToolShapeCount];
        return TileGen.IconTool(1100 + index, index % StarterBlocks.ToolShapeCount, r, g, b);
    }
}
