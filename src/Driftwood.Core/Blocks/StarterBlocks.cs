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

    // A blast furnace. Darker and harder than a furnace — deepstone brick rather than cobble — and
    // its mouth is a slot rather than an arch, so the two are told apart at a glance in a row.
    public const ushort LayerBlastTop = 65;
    public const ushort LayerBlastSide = 66;
    public const ushort LayerBlastFront = 67;
    public const ushort LayerBlastFrontLit = 68;

    // Two more flowers, and they exist to be crushed. ⛳ The dye tree needs a red and a yellow it
    // can find in a field, and the two we had are a blue and a white — so rather than inventing a
    // source, the world grows one. Ours by name, in the same coastal register as driftoak.
    public const ushort LayerEmberbloom = 69;
    public const ushort LayerSunwort = 70;

    /// <summary>
    /// A fleece laid as a block, in each of the sixteen. The first thing made out of an animal.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Sixteen layers rather than one tinted one.</b> A pack keeps every dyed wool in its own
    /// file — <c>white_wool.png</c>, <c>red_wool.png</c> — so a tint applied to one layer would be a
    /// colour nothing in a pack could ever replace. It is also why <see cref="Colours"/> is a table
    /// of real numbers rather than a hue rotation: ours have to sit where theirs sit.
    /// </remarks>
    public const ushort LayerFirstWool = 71;

    /// <summary>The first layer that is an item icon rather than a block face.</summary>
    /// <remarks>
    /// <para>Items share the block texture array rather than taking one of their own. They are the
    /// same sixteen-pixel tiles, they are drawn by the same two places — a slot on the bar and a
    /// thing spinning on the floor — and a second array would be a second bind, a second upload and
    /// a second pack-import path for no difference anybody could see.</para>
    /// <para>⚠ <b>Faces and icons are kept contiguous</b>, so a block family added here moves every
    /// number below it. That is a search and replace and the audit's "every icon is painted" check
    /// is what catches one missed.</para>
    /// </remarks>
    public const ushort LayerFirstIcon = 87;

    public const ushort LayerStick = 87;
    public const ushort LayerCoal = 88;
    public const ushort LayerCharcoal = 89;
    public const ushort LayerRawCopper = 90;
    public const ushort LayerRawIron = 91;
    public const ushort LayerRawGold = 92;
    public const ushort LayerCopperIngot = 93;
    public const ushort LayerIronIngot = 94;
    public const ushort LayerGoldIngot = 95;
    public const ushort LayerStormglass = 96;
    public const ushort LayerAzurite = 97;
    public const ushort LayerClayLump = 98;
    public const ushort LayerBrick = 99;

    // What an animal leaves, and the one tool that takes something off a live one.
    public const ushort LayerLeather = 100;
    public const ushort LayerFeather = 101;
    public const ushort LayerEgg = 102;
    public const ushort LayerShears = 103;

    /// <summary>
    /// The meats: raw and cooked, in the <see cref="Meats"/> order.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Eight layers, three drawings.</b> A cut, a chop with the bone along one edge, and a leg —
    /// and each animal's palette on top of that, raw and cooked. Eight pictures of a lump of meat
    /// would be eight things nobody could tell apart in a slot the size of a fingernail, which is the
    /// same argument that made the four tool heads four different silhouettes rather than four
    /// colours of the same one.
    /// </remarks>
    public const ushort LayerFirstMeat = 104;

    /// <summary>The sixteen dye powders, in <see cref="Colours"/> order.</summary>
    public const ushort LayerFirstDye = 112;

    // What the dark leaves behind.
    public const ushort LayerString = 128;
    public const ushort LayerBone = 129;
    public const ushort LayerRottenFlesh = 130;

    /// <summary>The tool icons: one palette per tier, four heads each, tier-major.</summary>
    public const ushort LayerFirstTool = 131;

    /// <summary>One of the sixteen: our name for it, and what it looks like.</summary>
    /// <param name="Pack">
    /// The stem a pack uses for it. Ours are the same words for every one of them — nobody owns
    /// "red" — but the ordering and the spelling of the two greys are theirs, and a wool tile is
    /// looked up as <c>{stem}_wool.png</c>.
    /// </param>
    public readonly record struct Dye(string Name, string Pack, byte R, byte G, byte B);

    /// <summary>
    /// The sixteen, in the order everything derived from them runs.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>Sixteen is the genre's number and it is the right one for a reason worth writing
    /// down.</b> It is the largest set that still reads as a palette rather than as a gradient — every
    /// one of them is nameable at a glance in a slot — and it is what every pack in existence has
    /// already painted. Ours sit near theirs deliberately, per the same contract as the 64×64 skin
    /// and the 176×166 panel: a player's pack has to be able to replace them.</para>
    /// <para>⚠ <b>The colours are the wool's, not the dye's.</b> A dye powder is drawn from the same
    /// numbers lifted a little, because what a player is actually choosing between is sixteen
    /// blocks — and a powder that did not match the block it makes is a slot that lies.</para>
    /// </remarks>
    public static readonly Dye[] Colours =
    [
        new("white", "white", 233, 236, 236),
        new("orange", "orange", 240, 118, 19),
        new("magenta", "magenta", 189, 68, 179),
        new("light_blue", "light_blue", 58, 175, 217),
        new("yellow", "yellow", 248, 198, 39),
        new("lime", "lime", 112, 185, 25),
        new("pink", "pink", 237, 141, 172),
        new("grey", "gray", 62, 68, 71),
        new("light_grey", "light_gray", 142, 142, 134),
        new("cyan", "cyan", 21, 137, 145),
        new("purple", "purple", 121, 42, 172),
        new("blue", "blue", 53, 57, 157),
        new("brown", "brown", 114, 71, 40),
        new("green", "green", 84, 109, 27),
        new("red", "red", 160, 39, 34),
        new("black", "black", 20, 21, 25),
    ];

    /// <summary>Head shapes a tier comes in — pickaxe, axe, shovel, sword.</summary>
    public const int ToolShapeCount = 4;

    /// <summary>Palettes a head comes in — wood, stone, copper, gold, iron, stormglass, diamond.</summary>
    public const int ToolTierCount = 7;

    /// <summary>
    /// How hard an ore of each tier is, indexed by <see cref="BlockType.HarvestTier"/>.
    /// </summary>
    /// <remarks>
    /// <para>⛔⛔ <b>WRITTEN DOWN AS A CURVE BECAUSE A FLAT NUMBER MADE THE LADDER RUN BACKWARDS.</b>
    /// Every ore used to be Hardness 3 while pickaxe speed ran 2, 4, 6, 8, 10 — so measured with the
    /// <em>minimum viable</em> tool, coal took 2.25 s, iron 1.13 s, gold 0.75 s and stormglass
    /// 0.56 s. Going deeper made the work quicker, which is the opposite of what a descent is for.
    /// </para>
    /// <para>These are chosen against the speed of the pickaxe each tier actually needs, so what a
    /// player feels is a gentle climb rather than a cliff — the user's own line was "it should feel
    /// like more work the deeper you go" and "I don't want it to become a chore":</para>
    /// <list type="table">
    /// <item><term>coal, 3.0</term><description>wooden pickaxe, 2.25 s</description></item>
    /// <item><term>copper and iron, 7.0</term><description>stone pickaxe, 2.63 s</description></item>
    /// <item><term>gold and azurite, 12.0</term><description>copper pickaxe, 3.00 s</description></item>
    /// <item><term>stormglass, 19.0</term><description>iron pickaxe, 3.56 s</description></item>
    /// <item><term>diamond, 27.0</term><description>stormglass pickaxe, 4.05 s</description></item>
    /// </list>
    /// <para>⛳ <b>AND THE PROPERTY THAT FELL OUT OF IT, which is worth keeping deliberately:</b>
    /// because the curve is matched to the speed curve, being <em>one rung under</em> costs almost
    /// exactly the same wherever a player meets it — 17.5 s on iron with a wooden pickaxe, 15.0 s on
    /// gold with stone, 15.8 s on stormglass with copper, 16.9 s on diamond with iron. The lesson is
    /// the same fifteen seconds at every tier, which is how a rule teaches itself.</para>
    /// <para>⚠ <b>Index 0 is not an ore</b> and is only here so the array can be indexed by tier
    /// without an offset nobody would remember. <b>Rock is not on this curve</b> — deepstone stays at
    /// 3 because it is the medium rather than the prize; see its own note.</para>
    /// </remarks>
    public static readonly float[] OreHardness = [1.5f, 3f, 7f, 12f, 19f, 27f];

    /// <summary>
    /// The fluids, appended past the tools rather than filed beside the other block faces.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Appended on purpose, and the reason is a trap this project has already paid for.</b>
    /// <see cref="Textures.BlockTextureSet.Layers"/>' <em>order</em> is the layer numbering, so
    /// slotting a face in beside <see cref="LayerWater"/> moves eighty-five constants by one while
    /// every texture check goes on passing — the sixteen dyes went in at the end of that array while
    /// their constant said 112, and a pack would have painted a wooden pickaxe onto white dye.
    /// Appending costs the <c>--icon-sheet</c> report a fourth group and nothing else.
    /// <para>Three of them because a fluid is drawn two ways. Every pack in the genre ships a
    /// <c>_still</c> and a <c>_flow</c> and they are different pictures — still is a surface seen
    /// from above, flowing is a sheet travelling in a direction. Water already had its still tile at
    /// <see cref="LayerWater"/>; this adds the moving one, and both of lava's.</para>
    /// </remarks>
    public const ushort LayerFirstFluid = LayerFirstTool + ToolShapeCount * ToolTierCount;

    public const ushort LayerWaterFlow = LayerFirstFluid;
    public const ushort LayerLava = LayerFirstFluid + 1;
    public const ushort LayerLavaFlow = LayerFirstFluid + 2;

    /// <summary>
    /// The buckets, which are what turn a fluid from scenery into something a player uses.
    /// </summary>
    /// <remarks>
    /// Three icons rather than one with a tint: an empty pail, a pail of water and a pail of lava are
    /// three different things in a slot and a player picks between them at a glance. Every pack in the
    /// genre paints all three separately for the same reason.
    /// </remarks>
    public const ushort LayerBucket = LayerFirstFluid + 3;
    public const ushort LayerWaterBucket = LayerFirstFluid + 4;
    public const ushort LayerLavaBucket = LayerFirstFluid + 5;

    /// <summary>Nine coal packed into a block — and what quenching a lava source leaves.</summary>
    public const ushort LayerCoalBlock = LayerFirstFluid + 6;

    /// <summary>
    /// The two tiles nothing in the world is made of: a tongue of fire, and a wisp of smoke.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>The first layers here that no block wears.</b> Every particle until now was a crop of
    /// whatever it came off — which is exactly right for a chip of stone and useless for a flame,
    /// because fire is not made of anything. Packs paint both of these as their own files, so the
    /// mapping is the same shape as every other row in the table.
    /// </remarks>
    public const ushort LayerFlame = LayerFirstFluid + 7;
    public const ushort LayerSmoke = LayerFirstFluid + 8;

    /// <summary>
    /// The twenty armour icons: one material per row of four pieces, material-major.
    /// </summary>
    /// <remarks>
    /// ⛔ Appended, like the fluids and for the same reason — <see cref="Textures.BlockTextureSet.Layers"/>'
    /// order <em>is</em> this numbering, and a row slipped in among the item icons moves every
    /// constant after it while every texture check goes on passing. Material-major so the index
    /// arithmetic is the same shape the tools use: <c>material * 4 + piece</c>.
    /// </remarks>
    public const ushort LayerFirstArmour = LayerFirstFluid + 9;

    /// <summary>Helmet, chestplate, leggings, boots — <see cref="Items.EquipSlot"/> order.</summary>
    public const int ArmourPieceCount = 4;

    /// <summary>Leather, copper, gold, iron, stormglass, diamond.</summary>
    public const int ArmourMaterialCount = 6;

    /// <summary>
    /// The shields, which are the only things in the game carried in the other hand rather than worn.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>This was ONE, on the argument that a shield is a board and its facing barely changes
    /// what it does.</b> The user asked for a diamond one, and they are right that a shield with no
    /// ladder at all was the odd item out beside six materials of armour. Three, not six: leather
    /// and gold shields are silly, and what varies is the metal the board is faced with.
    /// </remarks>
    public const ushort LayerFirstShield =
        LayerFirstArmour + ArmourPieceCount * ArmourMaterialCount;

    /// <summary>Iron, stormglass, diamond.</summary>
    public const int ShieldCount = 3;

    /// <summary>
    /// The smoker: the third smelter, and the one that is told apart by its MATERIAL.
    /// </summary>
    /// <remarks>
    /// ⛳ A furnace is grey cobble with an arch and a blast furnace is dark brick with a letterbox —
    /// two stations already distinguished by shape as well as shade, per the note on
    /// <see cref="Textures.TileGen.Hearth"/>. A third grey box would have run that argument out of
    /// road, so this one is made of timber: it is a different colour from ten blocks away, which is
    /// what "told apart in a row along a wall" actually asks for.
    /// </remarks>
    public const ushort LayerSmokerTop = LayerFirstShield + ShieldCount;
    public const ushort LayerSmokerSide = LayerSmokerTop + 1;
    public const ushort LayerSmokerFront = LayerSmokerTop + 2;
    public const ushort LayerSmokerFrontLit = LayerSmokerTop + 3;

    /// <summary>The barrel: a chest that opens upward, so it has a lid and a stave.</summary>
    public const ushort LayerBarrelTop = LayerSmokerTop + 4;
    public const ushort LayerBarrelSide = LayerSmokerTop + 5;

    /// <summary>
    /// Diamond: the seam in the Emberdeep wall, and the cut gem it leaves.
    /// </summary>
    /// <remarks>
    /// ⚠ Appended rather than filed beside the other ores, for the reason every append since the
    /// fluids has been: <see cref="Textures.BlockTextureSet.Layers"/>' order IS this numbering, and
    /// a face slipped in beside <see cref="LayerStormglassOre"/> would move a hundred and sixty
    /// constants while every texture check went on passing.
    /// </remarks>
    public const ushort LayerDiamondOre = LayerBarrelSide + 1;
    public const ushort LayerDiamond = LayerBarrelSide + 2;

    /// <summary>
    /// Paper: the first base material added for something that does not exist yet.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Not a magic item, and it should not be scoped as one.</b> It arrived with the spellbook's
    /// design and it is wanted by four things that have nothing to do with spells — a map is paper, a
    /// handbook is a book you make, the cartography table is listed as blocked on maps, and every pack
    /// in the genre ships a <c>paper.png</c> we have had nothing to put on. It is a hole in the base
    /// set rather than a component of a feature.
    /// </remarks>
    public const ushort LayerPaper = LayerBarrelSide + 3;

    /// <summary>
    /// The recipe book, for the button that folds it out.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>The first layer here that no block and no item wears.</b> The flame and the smoke were
    /// the first two nothing was made of; this is the first that is not in the world at all — it is
    /// interface chrome, and it is a layer anyway so that a pack can reskin it exactly like every
    /// other picture in the game. It is also the first tile we <em>painted</em> rather than
    /// generated; see <see cref="Textures.PaintedArt"/> for why that line is drawn here and nowhere
    /// else yet.
    /// </remarks>
    public const ushort LayerRecipeBook = LayerBarrelSide + 4;

    /// <summary>
    /// The metals packed away: iron, gold and copper, in that order.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>All three at once off one template row, rather than the iron the anvil needs.</b> They
    /// are the same block three times with a different colour — the packs carry art for every one of
    /// them — and a set where two of the three metals can be stored is a set somebody has to come
    /// back to. ⚠ Copper's is the one with a future: the reference weathers it through four stages,
    /// which is a whole mechanic we do not have and is its own task.
    /// </remarks>
    public const ushort LayerFirstMetalBlock = LayerRecipeBook + 1;

    /// <summary>Iron, gold, copper — the order <see cref="MetalBlocks"/> is written in.</summary>
    public const int MetalBlockCount = 3;

    /// <summary>Our name, the ingot it packs, and the colour it is drawn in.</summary>
    public static readonly (string Name, string Label, string Ingot, byte R, byte G, byte B)[] MetalBlocks =
    [
        ("iron_block", "block of iron", "iron_ingot", 216, 216, 220),
        ("gold_block", "block of gold", "gold_ingot", 232, 196, 82),
        ("copper_block", "block of copper", "copper_ingot", 198, 124, 78),
    ];

    /// <summary>
    /// The anvil: one side, and a top per stage of wear.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Three tops and one side, which is the format's own arrangement and not our invention.</b>
    /// Measured off the user's packs: <c>anvil.png</c>, <c>anvil_top.png</c>,
    /// <c>chipped_anvil_top.png</c> and <c>damaged_anvil_top.png</c>. The wear shows on the face you
    /// strike, which is the only face you look at while using one.
    /// </remarks>
    public const ushort LayerAnvilSide = LayerFirstMetalBlock + MetalBlockCount;
    public const ushort LayerAnvilTop = LayerAnvilSide + 1;
    public const ushort LayerAnvilChipped = LayerAnvilSide + 2;
    public const ushort LayerAnvilDamaged = LayerAnvilSide + 3;

    /// <summary>How many stages an anvil goes through before it is gone.</summary>
    public const int AnvilStages = 3;

    /// <summary>
    /// Farming: the ground it grows in, and the wheat that grows there.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Dry and wet farmland are two blocks, not one with a flag.</b> The whole point of the wet
    /// one is that it is visibly different — a player has to be able to see, from where they are
    /// standing, whether their field is watered — and a flag nothing draws is a mechanic nobody can
    /// find. It is also how the format ships it.
    /// </remarks>
    public const ushort LayerFarmland = LayerAnvilSide + 4;
    public const ushort LayerFarmlandWet = LayerAnvilSide + 5;

    /// <summary>
    /// Wheat, one layer per stage of growth.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Four stages rather than the reference's eight.</b> Eight is a lot of drawings for a
    /// difference nobody reads at a glance — the stages that matter are "just planted", "coming up",
    /// "nearly", and "take it". ⚠ The count is a constant rather than four names, because the growth
    /// rule counts and a rule written against names is a rule that breaks when a fifth is drawn.
    /// </remarks>
    public const ushort LayerFirstWheat = LayerAnvilSide + 6;
    public const int WheatStages = 4;

    /// <summary>The four item icons farming and the anvil bring with them.</summary>
    /// <remarks>
    /// ⚠ Appended, like everything since the fluids, because <see cref="Textures.BlockTextureSet"/>'s
    /// array order IS this numbering. ⛳ The hoe is one item rather than a rung on the tool ladder —
    /// see the note in <c>StarterItems.Heads</c>, which is where changing that would break things.
    /// </remarks>
    public const ushort LayerHoe = LayerFirstWheat + WheatStages;
    public const ushort LayerSeeds = LayerHoe + 1;
    public const ushort LayerWheatItem = LayerHoe + 2;
    public const ushort LayerBread = LayerHoe + 3;
    public const ushort LayerBonemeal = LayerHoe + 4;

    /// <summary>
    /// The three root crops: four stages of tops each, then one icon each.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Four stages is not a compromise here, the way it is for wheat.</b> Measured off the
    /// shelf 2026-08-07: every pack that paints these paints <c>carrots_stage0..3</c> and
    /// <c>potatoes_stage0..3</c> — exactly four, where wheat's own eight have to be spread across
    /// ours. So these land on a pack's art one for one and their rows need no spreading rule.
    /// ⚠ Appended, like everything since the fluids, because <see cref="Textures.BlockTextureSet"/>'s
    /// array order IS this numbering.
    /// </remarks>
    public const ushort LayerFirstCrop = LayerHoe + 5;

    /// <summary>How many stages a root crop grows through, and how many tiles of tops it has.</summary>
    public const int CropStages = 4;

    /// <summary><see cref="Crops"/>'s length, as something a <c>const</c> can be built from.</summary>
    public const int CropCount = 3;

    /// <summary>The icons the three are carried as, after every stage of tops.</summary>
    public const ushort LayerFirstCropItem = LayerFirstCrop + CropCount * CropStages;

    /// <summary>
    /// And the one of them worth putting on a fire.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>One cooked crop rather than three.</b> Cooking is what makes a potato worth growing —
    /// it is the meanest of the three raw and the best of them baked — and that is a reason to grow
    /// a particular crop. Three cooked vegetables would be three items that all say the same thing.
    /// ⚠ It is also the second thing the smoker can cook, which until now was meat and nothing else.
    /// </remarks>
    public const ushort LayerBakedPotato = LayerFirstCropItem + CropCount;

    /// <summary>
    /// Sixteen panes of coloured glass, in <see cref="Colours"/> order.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>One layer per colour serves both the block and the pane</b>, exactly as plain glass
    /// already does — <c>glass_pane</c> in <see cref="ConnectedMaterials"/> draws <c>LayerGlass</c>.
    /// The packs do ship a separate <c>_pane_top</c> for the pane's rim (measured: Dokucraft has all
    /// 48 files, Silent Hill 21, Vintage 16), and taking it would be a second run of sixteen layers
    /// to draw one edge nobody looks at straight on.
    /// ⚠ Appended, because this array's order IS the numbering.
    /// </remarks>
    public const ushort LayerFirstStainedGlass = LayerBakedPotato + 1;

    /// <summary>The composter's slatted side, its floor, and the two states of what is in it.</summary>
    public const ushort LayerComposterSide = LayerFirstStainedGlass + 16;
    public const ushort LayerComposterBottom = LayerComposterSide + 1;
    public const ushort LayerCompost = LayerComposterSide + 2;
    public const ushort LayerCompostReady = LayerComposterSide + 3;

    /// <summary>The berry bush's two states, then what a pick takes off it.</summary>
    /// <remarks>
    /// ⚠ Appended, like everything since the fluids, because
    /// <see cref="Textures.BlockTextureSet"/>'s array order IS this numbering.
    /// </remarks>
    public const ushort LayerBerryBush = LayerComposterSide + 4;
    public const ushort LayerBerryBushRipe = LayerBerryBush + 1;
    public const ushort LayerBerries = LayerBerryBush + 2;

    /// <summary>The cave mushrooms, then the roast either one becomes on a fire.</summary>
    public const ushort LayerMushroomBrown = LayerBerries + 1;
    public const ushort LayerMushroomRed = LayerMushroomBrown + 1;
    public const ushort LayerRoastedMushroom = LayerMushroomBrown + 2;

    /// <summary>The pumpkin's three faces, the lantern one carves into, and the spiders' webs.</summary>
    public const ushort LayerPumpkinSide = LayerRoastedMushroom + 1;
    public const ushort LayerPumpkinTop = LayerPumpkinSide + 1;
    public const ushort LayerPumpkinFace = LayerPumpkinSide + 2;
    public const ushort LayerJackOLantern = LayerPumpkinSide + 3;
    public const ushort LayerCobweb = LayerPumpkinSide + 4;

    /// <summary>What the rest of the hostile roster leaves behind (#93).</summary>
    /// <remarks>
    /// ⚠ Appended, like everything since the fluids, because
    /// <see cref="Textures.BlockTextureSet"/>'s array order IS this numbering.
    /// </remarks>
    public const ushort LayerSlimeball = LayerCobweb + 1;
    public const ushort LayerGunpowder = LayerCobweb + 2;
    public const ushort LayerFarpearl = LayerCobweb + 3;

    /// <summary>The rabbit's pair of meats and its hide.</summary>
    /// <remarks>
    /// ⚠ NOT rows in <c>StarterItems.Meats</c> — that table's icon layers are a run in the middle
    /// of this numbering, so a fifth meat there would renumber everything after it. The rabbit
    /// registers standalone, the rotten-flesh arrangement.
    /// </remarks>
    public const ushort LayerRawRabbit = LayerCobweb + 4;
    public const ushort LayerCookedRabbit = LayerCobweb + 5;
    public const ushort LayerRabbitHide = LayerCobweb + 6;
    public const ushort LayerInkSac = LayerCobweb + 7;

    /// <summary>The seed's missing nature (#95), in the order the slices land.</summary>
    /// <remarks>
    /// ⚠ Appended, like everything since the fluids, because
    /// <see cref="Textures.BlockTextureSet"/>'s array order IS this numbering.
    /// </remarks>
    public const ushort LayerIce = LayerCobweb + 8;
    public const ushort LayerCactusSide = LayerCobweb + 9;
    public const ushort LayerCactusTop = LayerCobweb + 10;
    public const ushort LayerDeadBush = LayerCobweb + 11;
    public const ushort LayerGlowcap = LayerCobweb + 12;
    public const ushort LayerMarshReed = LayerCobweb + 13;
    public const ushort LayerMoss = LayerCobweb + 14;
    public const ushort LayerMossyRubble = LayerCobweb + 15;
    public const ushort LayerSeagrass = LayerCobweb + 16;

    // #27, the signal kit: four wire brightnesses, the lamp pair, and a symbol per gate and state.
    public const ushort LayerTidewireOff = LayerCobweb + 17;
    public const ushort LayerTidewireLow = LayerCobweb + 18;
    public const ushort LayerTidewireMid = LayerCobweb + 19;
    public const ushort LayerTidewireHigh = LayerCobweb + 20;
    public const ushort LayerTidelamp = LayerCobweb + 21;
    public const ushort LayerTidelampLit = LayerCobweb + 22;
    public const ushort LayerGateFirst = LayerCobweb + 23;   // and, or, xor, not, latch; off then on

    // #28, the track: one straight drawing turned four ways, one bend turned four ways, and the
    // booster pair. The cart icon is the one item drawing the kit needs.
    public const ushort LayerRail = LayerCobweb + 33;
    public const ushort LayerRailBend = LayerCobweb + 34;
    public const ushort LayerRailBoost = LayerCobweb + 35;
    public const ushort LayerRailBoostOn = LayerCobweb + 36;
    public const ushort LayerCartIcon = LayerCobweb + 37;

    public const int LayerCount = LayerCartIcon + 1;

    /// <summary>One anvil's name, by how worn it is and which way it lies.</summary>
    /// <remarks>
    /// ⚠ Built rather than written out, so the stage count is one constant and every table that
    /// walks the stages — the block set, the drops, the wear rule — reads the same names.
    /// </remarks>
    public static string AnvilName(int stage, bool alongX) =>
        $"anvil{(stage == 0 ? "" : stage == 1 ? "_chipped" : "_damaged")}_{(alongX ? "x" : "z")}";

    /// <summary>And one stage of wheat.</summary>
    public static string WheatName(int stage) => $"wheat_{stage}";

    /// <summary>
    /// A crop that is pulled up rather than reaped: green tops, and the thing itself underground.
    /// </summary>
    /// <param name="Name">Ours, and the stem of every block name it registers.</param>
    /// <param name="Pack">Theirs, which is plural — <c>carrots_stage0.png</c>, not <c>carrot_</c>.</param>
    /// <param name="Leaf">The tops, which is what the world sees at every stage.</param>
    /// <param name="Root">What comes out of the ground, and what the icon is drawn in.</param>
    /// <param name="Feeds">Half-hearts eating one restores.</param>
    public readonly record struct Crop(
        string Name, string Pack, (byte R, byte G, byte B) Leaf, (byte R, byte G, byte B) Root,
        int Feeds);

    /// <summary>
    /// The three, in the order every layer, block and item derived from them runs.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Each one sows itself.</b> The reference gives beetroot a separate seed item and lets the
    /// other two plant themselves, which is a rule with one exception in it — and the exception buys
    /// nothing here, because we have no trading and no villager gardens for the seed to come out of.
    /// One rule: you plant what you harvested, and a field's first crop costs one of it.
    /// ⚠ <b>Feeds is the only number here that is a balance decision.</b> A potato is the least of the
    /// three raw and the most of them baked, which is the whole reason the smelt exists.
    /// </remarks>
    public static readonly Crop[] Crops =
    [
        new("carrot", "carrots", (86, 140, 44), (232, 137, 42), 3),
        new("potato", "potatoes", (78, 132, 50), (198, 166, 98), 2),
        new("beetroot", "beetroots", (94, 128, 52), (160, 40, 52), 2),
    ];

    /// <summary>One stage of one root crop, as a block name.</summary>
    public static string CropName(int crop, int stage) => $"{Crops[crop].Name}_{stage}";

    /// <summary>The berry bush's two states: growing, and carrying fruit worth a pick.</summary>
    /// <remarks>
    /// ⛳ <b>The first crop that is not a crop.</b> It stands on plain grass or dirt — no hoe, no
    /// water — and a pick resets the ripe one to this rather than taking the plant, so a kept bush
    /// is a supply where a field is a harvest. Two stages, because that is all the mechanic has to
    /// say: growing, and ready.
    /// </remarks>
    public const string BerryBushName = "berry_bush";
    public const string BerryBushRipeName = "berry_bush_ripe";

    /// <summary>The tile a stage of tops is drawn from.</summary>
    public static ushort CropStageLayer(int crop, int stage) =>
        (ushort)(LayerFirstCrop + crop * CropStages + stage);

    /// <summary>One ladder of growth: the rungs in order, and what the bottom rung stands on.</summary>
    /// <param name="Rungs">The block names, ripe last.</param>
    /// <param name="OnFarmland">
    /// True for a tilled crop, which grows only over watered farmland. False for a plant that
    /// stands on plain grass or dirt and asks only for light — the berry bush's rule.
    /// </param>
    public readonly record struct GrowthLadder(string[] Rungs, bool OnFarmland);

    /// <summary>
    /// Every crop in the game as a ladder of block names, ripe last.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Wheat is in here too, which is the point.</b> <see cref="Growth"/>'s note has always said
    /// a second crop should be "a row in StarterBlocks and nothing there" — it was not true while the
    /// ladder was built from <see cref="WheatName"/> by hand. It is true now: the growth rule, the
    /// drops, the sowing rule and the reachability walk all read this one list.
    /// ⛳ The bush is the row that made the ground a field on the ladder rather than a rule in
    /// <see cref="Growth"/> — a plant that regrows on plain grass could not say so anywhere else.
    /// </remarks>
    public static GrowthLadder[] CropLadders()
    {
        var ladders = new GrowthLadder[2 + CropCount];

        var wheat = new string[WheatStages];
        for (var s = 0; s < WheatStages; s++) wheat[s] = WheatName(s);
        ladders[0] = new GrowthLadder(wheat, OnFarmland: true);

        for (var c = 0; c < CropCount; c++)
        {
            var rungs = new string[CropStages];
            for (var s = 0; s < CropStages; s++) rungs[s] = CropName(c, s);
            ladders[c + 1] = new GrowthLadder(rungs, OnFarmland: true);
        }

        ladders[^1] = new GrowthLadder([BerryBushName, BerryBushRipeName], OnFarmland: false);
        return ladders;
    }

    /// <summary>What an item sows when it is used on tilled ground, or null when it sows nothing.</summary>
    /// <remarks>
    /// ⛔ <b>Asked of the table, not written into the interaction.</b> Sowing used to be
    /// <c>held.Name != "seeds"</c> against <c>WheatName(0)</c> in the client, which is a rule about
    /// the crop set living in the renderer — so a fourth crop would have grown, dropped and been
    /// eaten while being unplantable.
    /// </remarks>
    public static string? SownBy(string item)
    {
        if (item == "seeds") return WheatName(0);

        for (var c = 0; c < CropCount; c++)
            if (Crops[c].Name == item) return CropName(c, 0);

        return null;
    }

    /// <summary>What an item plants on plain grass or dirt, or null when it plants nothing there.</summary>
    /// <remarks>
    /// ⛳ The bush's own door beside <see cref="SownBy"/>'s tilled one, and a table for the same
    /// reason: what the game grows is never written into the input handler. The two never overlap —
    /// a berry refuses farmland and a seed refuses a lawn — which is what keeps a click's meaning
    /// readable from the ground alone.
    /// </remarks>
    public static string? SownOnSoil(string item) => item == "berries" ? BerryBushName : null;

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
        BlockId DiamondOre,
        BlockId AzuriteOre,
        BlockId Clay,
        BlockId Sandstone,
        BlockId Snow,
        BlockId SnowLayer,
        BlockId Meadowgrass,
        BlockId Seaflax,
        BlockId Marshlily,
        BlockId Emberbloom,
        BlockId Sunwort,
        BlockId Rubble,
        BlockId Glass,
        BlockId Bricks,
        BlockId Bench,
        BlockId Furnace,
        BlockId FurnaceLit,
        BlockId Lava,

        /// <summary>
        /// The three root crops at their ripe stage, which is how they are found growing wild.
        /// </summary>
        /// <remarks>
        /// ⛳ <b>One array rather than three fields</b>, because nothing ever wants a particular one:
        /// the generator picks between them and the census weighs them together. A fourth crop is a
        /// row in <see cref="Crops"/> and changes nothing that reads this.
        /// </remarks>
        BlockId[] WildCrops,

        /// <summary>The ripe berry bush, which is how one is found growing wild.</summary>
        BlockId BerryBushRipe,

        /// <summary>The cave mushrooms, for the cave-floor decorator and the census.</summary>
        BlockId MushroomBrown,
        BlockId MushroomRed,

        /// <summary>The pumpkin as found wild, and the webs the cave decorator spins.</summary>
        BlockId Pumpkin,
        BlockId Cobweb,

        /// <summary>The frozen lid cold water wears, by the snow's own rule.</summary>
        BlockId Ice,

        /// <summary>The arid fringe's two tells, stood on hot dry sand.</summary>
        BlockId Cactus,
        BlockId DeadBush,

        /// <summary>The deep's own light, growing only below the glow floor.</summary>
        BlockId Glowcap,

        /// <summary>The wet shoreline's cane, standing where ground meets the waterline.</summary>
        BlockId MarshReed,

        /// <summary>The wet shallow caves' floor covering.</summary>
        BlockId Moss,

        /// <summary>The sea floor's meadow: the always-waterlogged plant that proved #96.</summary>
        BlockId Seagrass)
    {
        /// <summary>Every rock an ore can form in. Ore replaces rock, whichever rock it is.</summary>
        public BlockId[] Rock => [Stone, Deepstone, Coralstone, Driftstone, Saltstone];

        /// <summary>Everything mining is meant to yield, for the census to weigh against rock.</summary>
        public BlockId[] Ores =>
            [CoalOre, IronOre, CopperOre, GoldOre, StormglassOre, DiamondOre, AzuriteOre, Emberstone];

        /// <summary>Everything that grows on open ground, for the census to weigh together.</summary>
        public BlockId[] GroundCover => [Meadowgrass, Seaflax, Marshlily, Emberbloom, Sunwort];

        /// <summary>The flowers, which are rarer than the grass they stand in.</summary>
        public BlockId[] Flowers => [Seaflax, Marshlily, Emberbloom, Sunwort];
    }

    public static Ids Register(BlockRegistry registry)
    {
        // Id 0 must be air; chunk storage treats a zeroed array as empty.
        //
        // ⚠ Replaceable, which is not a formality: it is what a fluid asks before it moves into a
        // cell, and with it left at the default a river cannot flow into an empty space — which is
        // to say it cannot flow at all. Measured, and it read as a fall crossing 0 of 20 cells.
        registry.Register(new BlockType
        {
            Name = "air", Solid = false, Opaque = false, Replaceable = true,
        });

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
        var water = RegisterFluid(
            registry, "water", FluidKind.Water,
            LayerWater, LayerWaterFlow, TintSource.Water, SoundMaterial.Water,
            attenuation: 1, emission: 0);

        // ⛳ The Emberdeep's own material, and the reason the deep is dangerous rather than merely
        // far away. Emissive, so it lights the room it is in — which means a settling river is a
        // relight per cell and is the one number in this whole system that had to be measured before
        // it could be believed. Full red at the top of the scale, warm through green, almost no blue.
        var lava = RegisterFluid(
            registry, "lava", FluidKind.Lava,
            LayerLava, LayerLavaFlow, TintSource.None, SoundMaterial.Stone,
            attenuation: 2, emission: LightValue.PackBlock(15, 8, 2));

        // ⛳ Nine coal packed away, and the one thing quenching a lava SOURCE leaves behind. The two
        // uses are the same block on purpose: what you get for a clever bit of plumbing should be
        // something you already know the value of, not a curiosity with one recipe.
        registry.Register(new BlockType
        {
            Name = "coal_block", Hardness = 5f, Crafted = true, Sounds = SoundMaterial.Stone,
            HarvestClass = ToolClass.Pickaxe, HarvestTier = 1,
            TopLayer = LayerCoalBlock, SideLayer = LayerCoalBlock, BottomLayer = LayerCoalBlock,
        });

        // ⛳ The three metals packed away. Harder than coal and needing a real pickaxe, because what
        // is in one is a trip underground rather than an afternoon with an axe — and the anvil is
        // three of the iron one, which is what makes thirty ingots a number a player feels.
        for (var m = 0; m < MetalBlockCount; m++)
        {
            var layer = (ushort)(LayerFirstMetalBlock + m);

            registry.Register(new BlockType
            {
                Name = MetalBlocks[m].Name, Hardness = 5f, Crafted = true,
                Sounds = SoundMaterial.Metal,
                HarvestClass = ToolClass.Pickaxe, HarvestTier = 2,
                TopLayer = layer, SideLayer = layer, BottomLayer = layer,
            });
        }

        // ⛳ THE ANVIL, and it wears out. Three stages on one side texture, each its own pair of
        // facings — the first thing in this game that degrades where it stands rather than in a
        // pocket. ⚠ Two facings and not four: an anvil is symmetric end to end, so four ids would be
        // four names for two shapes and a check that could never tell half of them apart. Exactly
        // the campfire's argument.
        for (var stage = 0; stage < AnvilStages; stage++)
        for (var axis = 0; axis < 2; axis++)
        {
            var top = stage switch
            {
                0 => LayerAnvilTop,
                1 => LayerAnvilChipped,
                _ => LayerAnvilDamaged,
            };

            registry.Register(new BlockType
            {
                Name = AnvilName(stage, axis == 0),
                Hardness = 5f, Crafted = true, Derived = stage > 0 || axis > 0,
                Sounds = SoundMaterial.Metal,
                HarvestClass = ToolClass.Pickaxe, HarvestTier = 2,
                Use = BlockUse.Anvil,
                Opaque = false,
                SupportFace = Faces.NegY,
                Model = BlockModel.Anvil(top, LayerAnvilSide, axis == 0),
            });
        }

        // ⛳ FARMLAND, dry and wet. Not solid-through: it is a full block a pace shorter, which is
        // what makes a tilled field read as tilled from across it and what stops a player walking a
        // crop row without noticing they are on it.
        registry.Register(new BlockType
        {
            Name = "farmland", Hardness = 0.6f, Crafted = true, Sounds = SoundMaterial.Dirt,
            HarvestClass = ToolClass.Shovel,
            Opaque = false,
            Model = BlockModel.Layer(LayerFarmland, LayerFarmland, LayerDirt, 15f),
        });

        registry.Register(new BlockType
        {
            Name = "farmland_wet", Hardness = 0.6f, Crafted = true, Derived = true,
            Sounds = SoundMaterial.Dirt, HarvestClass = ToolClass.Shovel,
            Opaque = false,
            Model = BlockModel.Layer(LayerFarmlandWet, LayerFarmlandWet, LayerDirt, 15f),
        });

        // ⛳ WHEAT, one block per stage. The stage IS the block id, which is the decision the whole
        // growth system rests on: the world already stores block ids and already saves an edit, so
        // a field halfway up costs nothing to keep and needs no bank, no timer and no save format.
        for (var stage = 0; stage < WheatStages; stage++)
        {
            registry.Register(new BlockType
            {
                Name = WheatName(stage), Hardness = 0.05f, Crafted = true, Derived = stage > 0,
                Solid = false, Opaque = false, Sounds = SoundMaterial.Plant,
                SupportFace = Faces.NegY,
                Model = BlockModel.Cross((ushort)(LayerFirstWheat + stage), tinted: false),
            });
        }

        // ⛳ And the three that are pulled up rather than reaped. Identical to wheat in every field
        // that matters — the stage is the block id, so a field halfway up is already in the save —
        // which is exactly why they are a loop over a table rather than three more hand-written
        // ladders that could quietly disagree with wheat about hardness or what holds them up.
        var wildCrops = new BlockId[CropCount];

        for (var crop = 0; crop < CropCount; crop++)
        for (var stage = 0; stage < CropStages; stage++)
        {
            var id = registry.Register(new BlockType
            {
                Name = CropName(crop, stage), Hardness = 0.05f, Crafted = true, Derived = stage > 0,
                Solid = false, Opaque = false, Sounds = SoundMaterial.Plant,
                SupportFace = Faces.NegY,
                Model = BlockModel.Cross(CropStageLayer(crop, stage), tinted: false),
            });

            // ⛳ The RIPE stage is the one found growing wild, which is what makes a wild patch worth
            // walking to: it is a crop and a seed at once, so finding one is the whole entry to
            // growing that crop. An unripe one would be a plant a player has to leave and come back to.
            if (stage == CropStages - 1) wildCrops[crop] = id;
        }

        // ⛳ The berry bush, the first plant on the ladder that wants no farmland. The young one is
        // only ever planted or left by a pick, so it is Derived; the RIPE one is what the wild
        // patches place, and it is deliberately NOT Crafted — the census demands it in the ground,
        // which is the standing proof the ride-along actually rides.
        registry.Register(new BlockType
        {
            Name = BerryBushName, Hardness = 0.05f, Crafted = true, Derived = true,
            Solid = false, Opaque = false, Sounds = SoundMaterial.BerryBush,
            SupportFace = Faces.NegY,
            Model = BlockModel.Cross(LayerBerryBush, tinted: false),
        });

        var berryBushRipe = registry.Register(new BlockType
        {
            Name = BerryBushRipeName, Hardness = 0.05f,
            Solid = false, Opaque = false, Sounds = SoundMaterial.BerryBush,
            SupportFace = Faces.NegY, Use = BlockUse.Berries,
            Model = BlockModel.Cross(LayerBerryBushRipe, tinted: false),
        });

        // ⛳ The cave mushrooms, the underground's own flora. The cave-floor decorator puts them on
        // stone and deepstone at any depth, so neither is Crafted — the census demands both in the
        // ground, which is the standing proof the decorator decorates.
        var mushroomBrown = registry.Register(new BlockType
        {
            Name = "mushroom_brown", Hardness = 0.05f,
            Solid = false, Opaque = false, Sounds = SoundMaterial.Plant,
            SupportFace = Faces.NegY,
            Model = BlockModel.Cross(LayerMushroomBrown, tinted: false),
        });

        var mushroomRed = registry.Register(new BlockType
        {
            Name = "mushroom_red", Hardness = 0.05f,
            Solid = false, Opaque = false, Sounds = SoundMaterial.Plant,
            SupportFace = Faces.NegY,
            Model = BlockModel.Cross(LayerMushroomRed, tinted: false),
        });

        // ⛳ The pumpkin, found whole in the meadows' wild patches and worth carrying home for what
        // a pair of shears turns it into. Wood to the ear and to the axe.
        var pumpkin = registry.Register(new BlockType
        {
            Name = "pumpkin", Hardness = 1.0f, Sounds = SoundMaterial.Wood,
            HarvestClass = ToolClass.Axe,
            TopLayer = LayerPumpkinTop, SideLayer = LayerPumpkinSide, BottomLayer = LayerPumpkinTop,
        });

        // Carved, it keeps its body and gains a face on one side — and with a torch shut inside it
        // is the meadow's own lantern. Four facings each: every orientation is its own id.
        for (var i = 0; i < Placeable.Facings.Length; i++)
        {
            registry.Register(new BlockType
            {
                Name = $"carved_pumpkin_{FacingNames[i]}", Hardness = 1.0f, Crafted = true,
                Sounds = SoundMaterial.Wood, HarvestClass = ToolClass.Axe,
                Model = BlockModel.CubeFacing(
                    LayerPumpkinTop, LayerPumpkinSide, LayerPumpkinTop, LayerPumpkinFace,
                    Placeable.Facings[i]),
            });

            registry.Register(new BlockType
            {
                Name = $"jack_o_lantern_{FacingNames[i]}", Hardness = 1.0f, Crafted = true,
                Sounds = SoundMaterial.Wood, HarvestClass = ToolClass.Axe,
                LightEmission = LightValue.PackBlock(15, 11, 5),
                Model = BlockModel.CubeFacing(
                    LayerPumpkinTop, LayerPumpkinSide, LayerPumpkinTop, LayerJackOLantern,
                    Placeable.Facings[i]),
            });
        }

        // ⛳ The cobweb, spun through pockets of the caves where something has clearly lived. It
        // SNARES — walking slows to a crawl, a jump barely rises, a fall is caught dead — and what
        // it leaves is string, the daylight route to the spider's own drop. A sword takes it
        // quickly, which is the one job a blade has against scenery.
        var cobweb = registry.Register(new BlockType
        {
            Name = "cobweb", Hardness = 1.2f, Solid = false, Opaque = false, Snares = true,
            Sounds = SoundMaterial.Cobweb, HarvestClass = ToolClass.Sword,
            Model = BlockModel.Cross(LayerCobweb, tinted: false),
        });

        var gravel = registry.Register(new BlockType
        {
            Name = "gravel", Hardness = 0.6f, Sounds = SoundMaterial.Gravel,
            HarvestClass = ToolClass.Shovel,
            TopLayer = LayerGravel, SideLayer = LayerGravel, BottomLayer = LayerGravel,
        });

        // ⛳ Ice: the top cell of cold water, frozen by the same rule that lays the snow beside it.
        // Glass's own two-flag shape — stood on and seen through — and it leaves NOTHING when
        // broken: there is no way to carry a pane of winter home yet, and saying so beats a block
        // that pretends. It is the one natural block with no item on purpose.
        var ice = registry.Register(new BlockType
        {
            Name = "ice", Hardness = 0.5f, Opaque = false,
            Sounds = SoundMaterial.Glass,
            Model = BlockModel.Cube(LayerIce, LayerIce, LayerIce),
        });

        // ⛳ The desert kit, standing on the hot dry sand. The cactus HURTS — lean on one, stand
        // on one, press into one and it costs, which is the whole difference between a plant and
        // an obstacle — and it is the first block whose harm is a fact about touching it rather
        // than about being inside it. The dead bush is the other tell of the arid fringe, and
        // what it leaves is sticks: dry wood off a dry land, the one place wood grows unforested.
        // ⚠ Opaque = false and a clear margin down each side of the tile, which is one decision:
        // every pack cuts its cactus art for the inset model, margins transparent — art that
        // cannot land on an opaque cube — and the same margins on ours are what make a full cube
        // READ as the genre's inset cactus without carrying a second model shape.
        var cactus = registry.Register(new BlockType
        {
            Name = "cactus", Hardness = 0.5f, Hurts = true, Opaque = false,
            Sounds = SoundMaterial.Grass,
            SupportFace = Faces.NegY,
            TopLayer = LayerCactusTop, SideLayer = LayerCactusSide, BottomLayer = LayerCactusTop,
        });

        var deadBush = registry.Register(new BlockType
        {
            Name = "dead_bush", Hardness = 0.05f, Solid = false, Opaque = false,
            Sounds = SoundMaterial.Grass,
            SupportFace = Faces.NegY,
            Model = BlockModel.Cross(LayerDeadBush, tinted: false),
        });

        // ⛳ Moss: the wet shallow caves' floor, a soft green block where rain seeps down into
        // stone. Pressed against rubble it makes the mossy form — the first worked stone whose
        // look is GROWN rather than cut, and the decor vocabulary's door into "old".
        var moss = registry.Register(new BlockType
        {
            Name = "moss", Hardness = 0.4f, Sounds = SoundMaterial.Grass,
            TopLayer = LayerMoss, SideLayer = LayerMoss, BottomLayer = LayerMoss,
        });

        registry.Register(new BlockType
        {
            Name = "mossy_rubble", Hardness = 2f, Crafted = true,
            HarvestClass = ToolClass.Pickaxe, HarvestTier = 1,
            Sounds = SoundMaterial.Stone,
            TopLayer = LayerMossyRubble, SideLayer = LayerMossyRubble, BottomLayer = LayerMossyRubble,
        });

        // ⛳ The marsh reed: the wet shoreline's tell, two or three joints of cane standing where
        // the ground sits exactly at the waterline. It stacks on itself the cactus's way — one
        // id, no tall-pair machinery — and three of its joints press into paper, the wetland's
        // own door into M0's recipe beside the planks path, replacing nothing.
        var marshReed = registry.Register(new BlockType
        {
            Name = "marsh_reed", Hardness = 0.05f, Solid = false, Opaque = false,
            Sounds = SoundMaterial.Grass,
            SupportFace = Faces.NegY,
            Model = BlockModel.Cross(LayerMarshReed, tinted: false),
        });

        // ⛳ The glowcap — ours, in the coined register beside driftoak and stormglass. The deep's
        // own light: a luminous mushroom that grows only below the glow floor, and picked and
        // replanted it is a light a player can FARM — the lamp that costs no coal and no trip
        // back. Cold light on purpose, against lava's orange and a lantern's warm white.
        var glowcap = registry.Register(new BlockType
        {
            Name = "glowcap", Hardness = 0.05f, Solid = false, Opaque = false,
            Sounds = SoundMaterial.Grass,
            SupportFace = Faces.NegY,
            LightEmission = LightValue.PackBlock(5, 10, 9),
            Model = BlockModel.Cross(LayerGlowcap, tinted: false),
        });

        // ⛳ Seagrass — the first block that IS its own cell's water, and the reason waterlogging
        // exists (#96). Waterlogged with no dry form registered: the flow reads its cell as a full
        // still source, a head in it drowns, and taking the plant leaves the sea it stood in.
        // Shears lift the plant itself; a bare hand gets nothing, which is exactly what the
        // harvest-tier line already means. It stands only on the flooded floor — the placement
        // rule adds "and only into a water source", which is the one thing SupportFace cannot say.
        var seagrass = registry.Register(new BlockType
        {
            Name = "seagrass", Hardness = 0.05f, Solid = false, Opaque = false,
            Sounds = SoundMaterial.Grass,
            SupportFace = Faces.NegY,
            HarvestClass = ToolClass.Shears, HarvestTier = 1,
            Fluid = FluidKind.Water, FluidLevel = FluidEngine.MaxLevel, Waterlogged = true,
            LightAttenuation = 1,
            Model = BlockModel.Cross(LayerSeagrass, tinted: false),
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
        // working metals want stone, the two showy ones want copper, the gem at the floor of the
        // ordinary underground wants iron, and the one in the Emberdeep wants that gem — so each
        // rung is the reason to make the next.
        //
        // ⛔⛔ HARDNESS CLIMBS WITH THE TIER, AND MEASUREMENT IS WHY. Every ore used to be Hardness
        // 3 while tool speed ran 2, 4, 6, 8 — so with the MINIMUM VIABLE pickaxe, coal took 2.25 s,
        // iron 1.13 s, gold 0.75 s and stormglass 0.56 s. The deeper ore was the quicker one and
        // the whole ladder ran backwards. See OreHardness for the numbers and what they buy.
        var coal = registry.Register(new BlockType
        {
            Name = "coal_ore", Hardness = OreHardness[1], HarvestClass = ToolClass.Pickaxe, HarvestTier = 1,
            TopLayer = LayerCoalOre, SideLayer = LayerCoalOre, BottomLayer = LayerCoalOre,
        });
        var iron = registry.Register(new BlockType
        {
            Name = "iron_ore", Hardness = OreHardness[2], HarvestClass = ToolClass.Pickaxe, HarvestTier = 2,
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
            Sounds = SoundMaterial.Deepstone,
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
        //
        // ⛳ TIER TWO, DELIBERATELY, AND IT WAS ARGUED. Two tiers under is a refusal now, and this is
        // 98.6% of everything below y 0 — so at tier 2 a player with NO pickaxe at all cannot move a
        // single cell of the deep. That was raised as an entombment and the user's answer was the
        // right one: rubble is everywhere in the ordinary underground, a stone pickaxe costs three of
        // it, so arriving in the Emberdeep empty-handed is a CHOICE rather than an accident. And the
        // worst case is not a lost world — it is dying and walking back down.
        //
        // ⚠ THE CONSEQUENCE, NAMED SO IT STAYS A DECISION: a player who loses their last pickaxe
        // below the deepstone line has no way out but death. A wooden one still works (one tier
        // under, 7.5 s a cell); bare hands do not work at all. That is what makes the deep somewhere
        // you prepare for, and it is the rule to revisit first if it ever reads as unfair.
        var deepstone = registry.Register(new BlockType
        {
            Name = "deepstone", Hardness = 3f, HarvestClass = ToolClass.Pickaxe, HarvestTier = 2,
            Sounds = SoundMaterial.Deepstone,
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
            Name = "copper_ore", Hardness = OreHardness[2], HarvestClass = ToolClass.Pickaxe, HarvestTier = 2,
            TopLayer = LayerCopperOre, SideLayer = LayerCopperOre, BottomLayer = LayerCopperOre,
        });
        // The deep ores ring like the rock they sit in — gold down, everything is deepstone.
        var gold = registry.Register(new BlockType
        {
            Name = "gold_ore", Hardness = OreHardness[3], HarvestClass = ToolClass.Pickaxe, HarvestTier = 3,
            Sounds = SoundMaterial.Deepstone,
            TopLayer = LayerGoldOre, SideLayer = LayerGoldOre, BottomLayer = LayerGoldOre,
        });
        var stormglass = registry.Register(new BlockType
        {
            Name = "stormglass_ore", Hardness = OreHardness[4], HarvestClass = ToolClass.Pickaxe, HarvestTier = 4,
            Sounds = SoundMaterial.Deepstone,
            TopLayer = LayerStormglassOre, SideLayer = LayerStormglassOre, BottomLayer = LayerStormglassOre,
        });

        // ⛳ THE NEW TOP OF THE LADDER, and the only ore in the game that lives where the lava is.
        // It wants a stormglass pickaxe, which is the gem out of the floor of the ordinary
        // underground — so reaching it is a second descent rather than a deeper version of the
        // first. Nothing needs a DIAMOND pickaxe: the top tool is speed and durability, which is
        // what a top tool is for once there is nothing left to gate.
        var diamond = registry.Register(new BlockType
        {
            Name = "diamond_ore", Hardness = OreHardness[5], HarvestClass = ToolClass.Pickaxe, HarvestTier = 5,
            Sounds = SoundMaterial.Deepstone,
            TopLayer = LayerDiamondOre, SideLayer = LayerDiamondOre, BottomLayer = LayerDiamondOre,
        });

        // Azurite is a real blue copper mineral, and ours rather than anybody's coined name.
        var azurite = registry.Register(new BlockType
        {
            Name = "azurite_ore", Hardness = OreHardness[3], HarvestClass = ToolClass.Pickaxe, HarvestTier = 3,
            Sounds = SoundMaterial.Deepstone,
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
            SupportFace = Faces.NegY,
            Model = BlockModel.Cross(LayerSeaflax, tinted: false),
        });
        var marshlily = registry.Register(new BlockType
        {
            Name = "marshlily", Hardness = 0.05f, Solid = false, Opaque = false, Sounds = SoundMaterial.Plant,
            SupportFace = Faces.NegY,
            Model = BlockModel.Cross(LayerMarshlily, tinted: false),
        });

        // ⛳ Two more, and they exist because the dye tree needs them. A red and a yellow are the two
        // colours nothing else in the world could have given — azurite is the blue, coal is the
        // black, the marshlily is the white — so rather than inventing a source somewhere abstract,
        // the field grows one. That is the genre's own answer and it is the right one: the reason to
        // look at a meadow should be that there is something in it worth picking.
        var emberbloom = registry.Register(new BlockType
        {
            Name = "emberbloom", Hardness = 0.05f, Solid = false, Opaque = false, Sounds = SoundMaterial.Plant,
            SupportFace = Faces.NegY,
            Model = BlockModel.Cross(LayerEmberbloom, tinted: false),
        });
        var sunwort = registry.Register(new BlockType
        {
            Name = "sunwort", Hardness = 0.05f, Solid = false, Opaque = false, Sounds = SoundMaterial.Plant,
            SupportFace = Faces.NegY,
            Model = BlockModel.Cross(LayerSunwort, tinted: false),
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

        // ⛳ The first blocks in the game made out of an animal, and the widest axis in it. Soft
        // enough to take by hand — a fleece that wanted a tool would be a wall a player could not
        // knock down after building it — and they name no harvest class at all, which is what lets a
        // bare hand keep one.
        // ⚠ A carpet is the SAME sixteen tiles at a sixteenth of the height, so the axis costs 32
        // blocks and 16 layers rather than 32 of each: it is snow's own model with wool on it.
        for (var i = 0; i < Colours.Length; i++)
        {
            var layer = (ushort)(LayerFirstWool + i);

            registry.Register(new BlockType
            {
                Name = $"wool_{Colours[i].Name}", Hardness = 0.8f, Crafted = true,
                Sounds = SoundMaterial.Cloth,
                TopLayer = layer, SideLayer = layer, BottomLayer = layer,
            });
        }

        for (var i = 0; i < Colours.Length; i++)
        {
            var layer = (ushort)(LayerFirstWool + i);

            registry.Register(new BlockType
            {
                Name = $"carpet_{Colours[i].Name}", Hardness = 0.1f, Crafted = true,
                Solid = false, Opaque = false, Sounds = SoundMaterial.Cloth,
                SupportFace = Faces.NegY,
                Model = BlockModel.Layer(layer, layer, layer, 1f),
            });
        }

        // ⛳ THE SAME SIXTEEN, IN GLASS — and the first thing in the game that is genuinely SEEN
        // THROUGH rather than merely seen past. Plain glass is a hole with a frame round it and is
        // drawn alpha-tested in the first pass; a coloured pane has to blend, which is what
        // Translucent is and why it took a field rather than a tile.
        // ⚠ Not opaque and no attenuation, exactly like plain glass: a coloured window that darkened
        // the room would make sixteen of these a worse choice than one of those, and the colour a
        // player wants is on the WALL rather than on the floor under it.
        for (var i = 0; i < Colours.Length; i++)
        {
            var layer = (ushort)(LayerFirstStainedGlass + i);

            registry.Register(new BlockType
            {
                Name = $"stained_glass_{Colours[i].Name}", Hardness = 0.3f, Crafted = true,
                Opaque = false, Translucent = true, Sounds = SoundMaterial.Glass,
                Model = BlockModel.Cube(layer, layer, layer),
            });
        }

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

            // ⛳ At the TIP of the stick, which the model draws ten units up. A flame at the middle
            // of the cell burns out of the middle of the handle.
            FlameScale = 0.34f, FlameHeight = 0.66f,
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

                // ⛳ SMOKE AND NO FLAME, which is what a furnace actually looks like: the fire is
                // shut inside it and what a room sees is the chimney. Off the TOP of the cell, so a
                // furnace built into a wall still shows it.
                SmokeScale = lit ? 0.55f : 0f, SmokeHeight = 1.05f,
                Model = BlockModel.CubeFacing(
                    LayerFurnaceTop, LayerFurnaceSide, LayerFurnaceTop,
                    lit ? LayerFurnaceFrontLit : LayerFurnaceFront,
                    Placeable.Facings[i]),
            });

            if (i != 0) continue;
            if (lit) furnaceLit = id; else furnace = id;
        }

        // The same machine, harder to build and twice as quick, and it will only take ore. Its own
        // family rather than a flag on the furnace, for the same reason every stair facing is its
        // own id: a cell holds an id and nothing beside it.
        for (var i = 0; i < Placeable.Facings.Length; i++)
        foreach (var lit in (bool[])[false, true])
            registry.Register(new BlockType
            {
                Name = $"blast_furnace_{FacingNames[i]}{(lit ? "_lit" : "")}",
                Hardness = 4.5f, Crafted = true, Use = BlockUse.Furnace,
                HarvestClass = ToolClass.Pickaxe, HarvestTier = 1,

                // Whiter and fiercer than a hearth, and a little dimmer: the mouth is a slot rather
                // than an open arch, so less of it is showing.
                LightEmission = lit ? LightValue.PackBlock(11, 9, 7) : (ushort)0,

                // Twice the furnace's rate of work and a harder draught with it, so the column is
                // thinner and faster rather than bigger.
                SmokeScale = lit ? 0.42f : 0f, SmokeHeight = 1.05f,
                Model = BlockModel.CubeFacing(
                    LayerBlastTop, LayerBlastSide, LayerBlastTop,
                    lit ? LayerBlastFrontLit : LayerBlastFront,
                    Placeable.Facings[i]),
            });

        // ⛳ The third smelter, and the one #58 said was blocked on content rather than on a system:
        // a smoker with nothing edible in the game is a station that opens an empty list. There are
        // eight meats now, so it has work. Same machine as the blast furnace one axis over — food
        // only, in half the time — which is the shape of a specialised smelter and the reason
        // FurnaceKind is an enum rather than a bool.
        for (var i = 0; i < Placeable.Facings.Length; i++)
        foreach (var lit in (bool[])[false, true])
            registry.Register(new BlockType
            {
                Name = $"smoker_{FacingNames[i]}{(lit ? "_lit" : "")}",
                Hardness = 3.5f, Crafted = true, Use = BlockUse.Furnace,
                HarvestClass = ToolClass.Axe, HarvestTier = 0,
                Sounds = SoundMaterial.Wood,

                // Warmer and softer than either stone smelter: a cooking fire behind boards.
                LightEmission = lit ? LightValue.PackBlock(13, 8, 3) : (ushort)0,

                // ⚠ The heaviest smoke of the three, and on purpose — it is the one whose whole
                // point is that something is cooking in it, and a kitchen chimney is what says so
                // from across a field.
                SmokeScale = lit ? 0.72f : 0f, SmokeHeight = 1.05f,
                Model = BlockModel.CubeFacing(
                    LayerSmokerTop, LayerSmokerSide, LayerSmokerTop,
                    lit ? LayerSmokerFrontLit : LayerSmokerFront,
                    Placeable.Facings[i]),
            });

        // ⛳ A BARREL IS A CHEST THAT OPENS UPWARD, and that is the whole of it: one block, no
        // facings, no partner, and it reads the same twenty-seven slots through the same ChestBank.
        // The cheapest remaining station in #58 by a wide margin, and it earns its place by being
        // the container you can put under a low ceiling — a chest needs a clear cell above its lid.
        registry.Register(new BlockType
        {
            Name = "barrel",
            Hardness = 2.5f, Crafted = true, Use = BlockUse.Chest,
            HarvestClass = ToolClass.Axe, HarvestTier = 0,
            Sounds = SoundMaterial.Wood,
            Model = BlockModel.Cube(LayerBarrelTop, LayerBarrelSide, LayerBarrelTop),
        });

        // ⛳ THE COMPOSTER, the last of #58's group that was blocked on content — and the content
        // arrived: there are crops now, so there is something to rot. The fill level IS the block
        // id, exactly as a crop's stage and a furnace's fire are, which is what makes it save for
        // free and need no bank. Stage 0 is the empty bin, 1..7 are filling, 8 is ready.
        for (var stage = 0; stage <= ComposterStages; stage++)
            registry.Register(new BlockType
            {
                Name = ComposterName(stage),
                Hardness = 0.6f, Crafted = true, Derived = stage > 0,
                Use = BlockUse.Composter,
                HarvestClass = ToolClass.Axe, HarvestTier = 0,
                Sounds = SoundMaterial.Wood,
                Opaque = false,
                Model = BlockModel.Composter(
                    LayerComposterSide, LayerComposterBottom,
                    stage >= ComposterStages ? LayerCompostReady : LayerCompost, stage),
            });

        RegisterSignals(registry);
        RegisterRails(registry);
        RegisterWaterlogged(registry);

        return new Ids(
            stone, dirt, grass, sand, water, gravel, log, leaves, planks, coal, iron, bedrock,
            emberstone, vine, deepstone, coralstone, driftstone, saltstone, copper, gold, stormglass,
            diamond, azurite, clay, sandstone, snow, snowLayer, meadowgrass, seaflax, marshlily,
            emberbloom, sunwort,
            rubble, glass, bricks, bench, furnace, furnaceLit, lava, wildCrops, berryBushRipe,
            mushroomBrown, mushroomRed, pumpkin, cobweb, ice, cactus, deadBush, glowcap, marshReed,
            moss, seagrass);
    }

    /// <summary>The five gate kinds, in the order their tiles sit from <see cref="LayerGateFirst"/>.</summary>
    public static readonly string[] GateKinds = ["and", "or", "xor", "not", "latch"];

    /// <summary>The top tile for one gate kind and state.</summary>
    public static ushort GateLayer(int kind, bool on) =>
        (ushort)(LayerGateFirst + kind * 2 + (on ? 1 : 0));

    /// <summary>The five lever or button forms, floor first, in <see cref="Placeable.Facings"/> order.</summary>
    public static string[] AttachedForms(string stem) =>
        [$"{stem}_floor", $"{stem}_east", $"{stem}_west", $"{stem}_south", $"{stem}_north"];

    /// <summary>
    /// The signal kit (#27): tidewire, the hands that feed it, the lamp and doors that follow it,
    /// and the five gates that think about it.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>Wire strength is the id</b> — sixteen registered blocks, mapped onto four tiles so
    /// the brightness reads without sixteen drawings. ⛳ <b>The lever's state is a lean, not a
    /// repaint</b>: the stick tips the other way, which reads across a room. ⳸ <b>The tidelamp is
    /// its own pair rather than a driven stormglass lamp</b>, because that lamp is always-on and
    /// placed all over existing worlds; a pass that darkened the unpowered ones would put out every
    /// light anybody has built.</para>
    /// <para>The gates carry their symbol on a rotated top face, which is why their model is one
    /// unit shy of a cube — the greedy path derives its texture coordinates from world position and
    /// cannot turn an arrow with its block. Native gates are the whole point of #27: the reference
    /// ships four primitives and fifteen years of players discovering the inverter by accident;
    /// shipping AND, OR, XOR, NOT and a latch as real blocks is what makes machines buildable
    /// rather than a puzzle about torches.</para>
    /// </remarks>
    private static void RegisterSignals(BlockRegistry registry)
    {
        // The carrier: sixteen strengths, a flat film on the floor, never a wall or a ceiling.
        for (var strength = 0; strength <= 15; strength++)
        {
            var tile = strength == 0 ? LayerTidewireOff
                : strength <= 5 ? LayerTidewireLow
                : strength <= 10 ? LayerTidewireMid
                : LayerTidewireHigh;

            registry.Register(new BlockType
            {
                Name = $"tidewire_{strength}",
                Hardness = 0.05f, Solid = false, Opaque = false, Crafted = true,
                Derived = strength > 0,
                Sounds = SoundMaterial.Stone,
                SupportFace = Faces.NegY,
                Model = BlockModel.Layer(tile, tile, tile, 1f),
            });
        }

        // The hands: a lever that stays, a button that springs back, a plate that is stood on.
        // All five forms of each, floor first — the torch's own Attached shape.
        var attachedFaces = new[] { -1, Placeable.Facings[0], Placeable.Facings[1], Placeable.Facings[2], Placeable.Facings[3] };

        for (var form = 0; form < 5; form++)
        foreach (var on in (bool[])[false, true])
        {
            registry.Register(new BlockType
            {
                Name = AttachedForms("lever")[form] + (on ? "_on" : ""),
                Hardness = 0.4f, Solid = false, Opaque = false, Crafted = true,
                Derived = on,
                Sounds = SoundMaterial.Stone, Use = BlockUse.Toggle,
                SupportFace = form == 0 ? Faces.NegY : Placeable.Opposite(attachedFaces[form]),
                Model = BlockModel.Lever(LayerRubble, LayerPlanks, attachedFaces[form], on),
            });

            registry.Register(new BlockType
            {
                Name = AttachedForms("button")[form] + (on ? "_pressed" : ""),
                Hardness = 0.4f, Solid = false, Opaque = false, Crafted = true,
                Derived = on,
                Sounds = SoundMaterial.Stone, Use = BlockUse.Toggle,
                SupportFace = form == 0 ? Faces.NegY : Placeable.Opposite(attachedFaces[form]),
                Model = BlockModel.Button(LayerStone, attachedFaces[form], on),
            });
        }

        foreach (var on in (bool[])[false, true])
        {
            registry.Register(new BlockType
            {
                Name = "pressure_plate" + (on ? "_on" : ""),
                Hardness = 0.4f, Solid = false, Opaque = false, Crafted = true,
                Derived = on,
                Sounds = SoundMaterial.Stone,
                SupportFace = Faces.NegY,
                Model = BlockModel.Layer(LayerSmoothStone, LayerSmoothStone, LayerSmoothStone, on ? 0.5f : 1f),
            });
        }

        // The lamp that answers: dark until fed, and the stormglass lamp's own light when lit.
        registry.Register(new BlockType
        {
            Name = "tidelamp",
            Hardness = 0.6f, Crafted = true, Sounds = SoundMaterial.Glass,
            TopLayer = LayerTidelamp, SideLayer = LayerTidelamp, BottomLayer = LayerTidelamp,
        });
        registry.Register(new BlockType
        {
            Name = "tidelamp_lit",
            Hardness = 0.6f, Crafted = true, Derived = true, Sounds = SoundMaterial.Glass,
            LightEmission = LightValue.PackBlock(12, 15, 15),
            TopLayer = LayerTidelampLit, SideLayer = LayerTidelampLit, BottomLayer = LayerTidelampLit,
        });

        // The thinkers: five kinds, four facings, off and on.
        for (var kind = 0; kind < GateKinds.Length; kind++)
        for (var i = 0; i < Placeable.Facings.Length; i++)
        foreach (var on in (bool[])[false, true])
        {
            registry.Register(new BlockType
            {
                Name = $"gate_{GateKinds[kind]}_{FacingNames[i]}" + (on ? "_on" : ""),
                Hardness = 1.5f, Opaque = false, Crafted = true,
                Derived = on,
                Sounds = SoundMaterial.Stone,
                HarvestClass = ToolClass.Pickaxe,
                Model = BlockModel.Gate(
                    GateLayer(kind, on), LayerDeepstonePolished, Placeable.Facings[i]),
            });
        }
    }

    /// <summary>
    /// The track (#28): ten forms of rail, and the six boosters that answer the wire.
    /// </summary>
    /// <remarks>
    /// <para>The item places a straight and <see cref="RailTable"/> re-picks everything from
    /// there, so the bends and climbs are <c>Derived</c> — a census never finds one and only the
    /// pass makes them. Curves never carry power, the genre's own rule, so the powered family is
    /// the two straights and the four climbs.</para>
    /// <para>⚠ The booster pair is a SINK to <see cref="SignalTable"/> by name, which is the whole
    /// point of building rails after signals: a station is a lever, a wire and two boosters, with
    /// nothing new taught to either side.</para>
    /// </remarks>
    private static void RegisterRails(BlockRegistry registry)
    {
        // name, top-tile, quarter turns of it, the face a climb rises toward (or -1), derived
        (string Name, ushort Tile, int Turn, int Climb, bool Derived)[] forms =
        [
            ("x", LayerRail, 90, -1, false),
            ("z", LayerRail, 0, -1, false),
            ("ne", LayerRailBend, 0, -1, true),
            ("se", LayerRailBend, 90, -1, true),
            ("sw", LayerRailBend, 180, -1, true),
            ("nw", LayerRailBend, 270, -1, true),
            ("up_e", LayerRail, 90, Faces.PosX, true),
            ("up_w", LayerRail, 90, Faces.NegX, true),
            ("up_s", LayerRail, 0, Faces.PosZ, true),
            ("up_n", LayerRail, 0, Faces.NegZ, true),
        ];

        foreach (var f in forms)
        {
            registry.Register(new BlockType
            {
                Name = $"rail_{f.Name}",
                Hardness = 0.7f, Solid = false, Opaque = false, Crafted = true,
                Derived = f.Derived,
                Sounds = SoundMaterial.Stone,
                HarvestClass = ToolClass.Pickaxe,
                SupportFace = Faces.NegY,
                Model = BlockModel.Rail(f.Tile, f.Turn, f.Climb),
            });

            if (f.Name.StartsWith("n", StringComparison.Ordinal)
                || f.Name.StartsWith("s", StringComparison.Ordinal))
                continue;   // no powered bends

            foreach (var on in (bool[])[false, true])
            {
                registry.Register(new BlockType
                {
                    Name = $"powered_rail_{f.Name}" + (on ? "_on" : ""),
                    Hardness = 0.7f, Solid = false, Opaque = false, Crafted = true,
                    Derived = f.Derived || on,
                    Sounds = SoundMaterial.Stone,
                    HarvestClass = ToolClass.Pickaxe,
                    SupportFace = Faces.NegY,
                    Model = BlockModel.Rail(on ? LayerRailBoostOn : LayerRailBoost, f.Turn, f.Climb),
                });
            }
        }
    }

    /// <summary>
    /// The waterlogged form of everything that can stand in the sea: same shape, same cost, same
    /// art, plus the cell's own water.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>One clone loop over the families rather than a wet flag on each registration</b>,
    /// so the set of waterloggable things is a list here and nowhere else: slabs, stairs, the
    /// connected families (fences, walls, panes), trapdoors, ladders and chests — the genre's own
    /// set, minus what it also refuses (doors) and what fire forbids (torches, lanterns, campfires;
    /// the glowcap is the underwater lamp). Every clone shares the dry form's model and layers, so
    /// pack support and collision arrive with it.</para>
    /// <para>⚠ <b>Registered after every dry family and before anything reads the registry</b> —
    /// the clone asks the dry form for its facts, and <see cref="Waterlogging"/> pairs the two by
    /// name once the registry seals.</para>
    /// <para>⚠ <c>Derived</c>, not <c>Crafted</c>: no recipe makes one and no census finds one —
    /// they exist the way a flowing tail exists, through play. Except seagrass, which generates
    /// and is registered with the plants, not here.</para>
    /// </remarks>
    private static void RegisterWaterlogged(BlockRegistry registry)
    {
        foreach (var name in WaterloggableNames())
        {
            var dry = registry.ByName(name);

            registry.Register(new BlockType
            {
                Name = name + Waterlogging.Suffix,
                Solid = dry.Solid, Opaque = false, Translucent = dry.Translucent,
                Hardness = dry.Hardness,
                HarvestClass = dry.HarvestClass, HarvestTier = dry.HarvestTier,
                Use = dry.Use, SupportFace = dry.SupportFace, PartnerFace = dry.PartnerFace,
                Climbable = dry.Climbable, Sounds = dry.Sounds, Tint = dry.Tint,
                Derived = true,
                Fluid = FluidKind.Water, FluidLevel = FluidEngine.MaxLevel, Waterlogged = true,

                // At least the water's own dimming, and never less than the dry form's: a wet
                // stained pane keeps its glass depth, and open water through a wet fence goes
                // dark at the sea's own rate.
                LightAttenuation = Math.Max(1, dry.LightAttenuation),
                Model = dry.Model,
            });
        }
    }

    /// <summary>Every dry block that has a waterlogged form, by name. The one list.</summary>
    public static IEnumerable<string> WaterloggableNames()
    {
        foreach (var m in ShapedNames)
        {
            yield return $"{m}_slab_lower";
            yield return $"{m}_slab_upper";

            for (var i = 0; i < Placeable.Facings.Length; i++)
            {
                yield return $"{m}_stairs_{FacingNames[i]}_lower";
                yield return $"{m}_stairs_{FacingNames[i]}_upper";
            }
        }

        foreach (var m in ConnectedNames)
        for (var mask = 0; mask < ConnectionFamily.Masks; mask++)
            yield return $"{m}_{mask}";

        for (var i = 0; i < Placeable.Facings.Length; i++)
        {
            foreach (var upper in (bool[])[false, true])
            foreach (var open in (bool[])[false, true])
                yield return TrapdoorName(i, upper, open);

            yield return $"ladder_{FacingNames[i]}";
            yield return $"chest_{FacingNames[i]}";
        }
    }

    /// <summary>
    /// Registers one fluid: a source, seven flowing depths, and the falling form.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>Nine registered blocks rather than one block and a level stored beside it</b>, which
    /// is what this codebase does with every other kind of state — twenty stair orientations, sixteen
    /// connection masks, sixteen colours of wool, a lit furnace and a cold one. The alternative is a
    /// per-chunk level array: 32 KB on top of every chunk's 128, plus a save section, plus changes to
    /// the snapshot, the mesher and the palette. A state is an id here.</para>
    /// <para>⛔ <b>The source keeps the plain name</b> — "water", not "water_source" — because a save
    /// stores blocks by name through a per-save palette, and renaming it would make every world ever
    /// written come back with its oceans missing.</para>
    /// <para>The source and the falling form are <em>full cubes</em>, so an ocean and a waterfall both
    /// take the greedy merge and cost exactly what they cost before. Only the flowing tail is a shaped
    /// block on the per-block path, and a tail is the fringe of a river rather than its body.</para>
    /// <para>⚠ <b>Still and flowing are two textures.</b> A source is a surface seen from above; every
    /// other state is a sheet travelling somewhere. Using one picture for both is the most obvious way
    /// to make a fluid look wrong, and every pack in the genre already ships the pair.</para>
    /// </remarks>
    private static BlockId RegisterFluid(
        BlockRegistry registry, string name, FluidKind kind,
        ushort still, ushort flow, TintSource tint, SoundMaterial sound,
        int attenuation, ushort emission)
    {
        // Unbreakable because a fluid is not something you mine: a ray passes through it to whatever
        // is behind, and it is not targetable at all. Saying so here means that if it ever does
        // become targetable the answer is already no, rather than a silent hole in the ocean.
        var source = registry.Register(new BlockType
        {
            Name = name,
            Solid = false, Opaque = false, Hardness = -1f, Replaceable = true,
            LightAttenuation = attenuation, LightEmission = emission,
            Tint = tint, Sounds = sound,
            Fluid = kind, FluidLevel = FluidEngine.MaxLevel,
            TopLayer = still, SideLayer = still, BottomLayer = still,
        });

        for (var level = 1; level < FluidEngine.MaxLevel; level++)
        {
            registry.Register(new BlockType
            {
                Name = $"{name}_{level}",
                Solid = false, Opaque = false, Hardness = -1f, Replaceable = true, Derived = true,
                LightAttenuation = attenuation, LightEmission = emission,
                Tint = tint, Sounds = sound,
                Fluid = kind, FluidLevel = level,

                // Two model units per level, so a full cell is sixteen and the shallowest tail is a
                // two-unit film — the same shape a snow layer and a carpet already are.
                Model = BlockModel.Layer(flow, flow, flow, level * 2f),
            });
        }

        registry.Register(new BlockType
        {
            Name = $"{name}_falling",
            Solid = false, Opaque = false, Hardness = -1f, Replaceable = true, Derived = true,
            LightAttenuation = attenuation, LightEmission = emission,
            Tint = tint, Sounds = sound,
            Fluid = kind, FluidLevel = FluidEngine.MaxLevel, FluidFalling = true,
            TopLayer = flow, SideLayer = flow, BottomLayer = flow,
        });

        return source;
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

                // ⚠ Higher and nearer the middle than a standing torch's, because a wall torch
                // leans out of the wall — the flame ends up over the cell rather than over the
                // stick's own base. A shared height would burn out of the masonry.
                FlameScale = 0.32f, FlameHeight = 0.74f,
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

                // The smallest fire in the game: it is caged, and what shows is a bead of it.
                // Following the body of the lantern, which hangs six units lower than it stands.
                FlameScale = 0.20f, FlameHeight = hanging ? 0.58f : 0.22f,
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
                Sounds = SoundMaterial.Wood,

                // ⛳ A LIT ONE COOKS AND AN UNLIT ONE LIGHTS. The two states want different answers
                // to the same click, which is exactly what BlockUse is for — and it is why the pair
                // is two block ids rather than one with a flag.
                // ⚠ Putting a lit one OUT moved to the shovel, because right-click is now taken. The
                // reference's own answer, and the first thing a shovel does that is not digging.
                Use = lit ? BlockUse.Campfire : BlockUse.Toggle,
                HarvestClass = ToolClass.Axe,
                SupportFace = Faces.NegY,
                LightEmission = lit ? LightValue.PackBlock(15, 11, 6) : (ushort)0,

                // ⛳ THE REFERENCE FIRE, and every other scale in the game is a fraction of it. It
                // is the only thing a player builds whose whole purpose is to be a fire, so it gets
                // both halves: tongues out of the logs and a real column of smoke off the top.
                FlameScale = lit ? 0.95f : 0f, FlameHeight = 0.42f,
                SmokeScale = lit ? 0.85f : 0f, SmokeHeight = 0.85f,
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
        //
        // ⛳ An open one is solid again. It was registered Solid = false purely because collision
        // followed the cell, so a swung panel filled the whole doorway — the workaround #57 named
        // and the first thing to undo now that a body collides with three units of panel.
        for (var i = 0; i < Placeable.Facings.Length; i++)
        foreach (var upper in (bool[])[false, true])
        foreach (var open in (bool[])[false, true])
            registry.Register(new BlockType
            {
                Name = TrapdoorName(i, upper, open),
                Hardness = 2f, Opaque = false, Crafted = true,
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
                Hardness = 3f, Opaque = false, Crafted = true,
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

    /// <summary>The four carved pumpkins, or the four lanterns, in <see cref="Placeable.Facings"/> order.</summary>
    public static BlockId[] CarvedPumpkins(BlockRegistry registry, bool lit)
    {
        var stem = lit ? "jack_o_lantern" : "carved_pumpkin";
        var ids = new BlockId[Placeable.Facings.Length];
        for (var i = 0; i < ids.Length; i++) ids[i] = registry.ByName($"{stem}_{FacingNames[i]}").Id;
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

        // A lever clicks over and back; a button presses in, and the CLIENT books its spring back
        // rather than a return toggle — a momentary source that could be clicked off would not be
        // momentary. The signal pass hears both through the same edit any toggle makes.
        foreach (var form in AttachedForms("lever"))
        {
            var off = registry.ByName(form).Id;
            var on = registry.ByName(form + "_on").Id;
            yield return (off, on);
            yield return (on, off);
        }

        foreach (var form in AttachedForms("button"))
            yield return (registry.ByName(form).Id, registry.ByName(form + "_pressed").Id);

        for (var i = 0; i < Placeable.Facings.Length; i++)
        {
            foreach (var upper in (bool[])[false, true])
            {
                var shut = registry.ByName(TrapdoorName(i, upper, open: false)).Id;
                var open = registry.ByName(TrapdoorName(i, upper, open: true)).Id;
                yield return (shut, open);
                yield return (open, shut);

                // A trapdoor under the sea swings between its own wet forms — the water in the
                // cell is not a thing a hinge can empty.
                var shutWet = registry.ByName(TrapdoorName(i, upper, open: false) + Waterlogging.Suffix).Id;
                var openWet = registry.ByName(TrapdoorName(i, upper, open: true) + Waterlogging.Suffix).Id;
                yield return (shutWet, openWet);
                yield return (openWet, shutWet);
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
    public static BlockId[] Furnaces(BlockRegistry registry, bool lit) => Smelters(registry, "furnace", lit);

    /// <summary>The same, for the blast furnace.</summary>
    public static BlockId[] BlastFurnaces(BlockRegistry registry, bool lit) =>
        Smelters(registry, "blast_furnace", lit);

    /// <summary>And for the smoker.</summary>
    public static BlockId[] Smokers(BlockRegistry registry, bool lit) => Smelters(registry, "smoker", lit);

    /// <summary>Fill stages the composter climbs through before the last one is ready.</summary>
    public const int ComposterStages = 8;

    /// <summary>The block one fill level of the composter is.</summary>
    public static string ComposterName(int stage) =>
        stage <= 0 ? "composter" : stage >= ComposterStages ? "composter_ready" : $"composter_{stage}";

    /// <summary>Every stage of the composter, empty through ready.</summary>
    public static BlockId[] Composters(BlockRegistry registry)
    {
        var ids = new BlockId[ComposterStages + 1];
        for (var stage = 0; stage <= ComposterStages; stage++)
            ids[stage] = registry.ByName(ComposterName(stage)).Id;
        return ids;
    }

    private static BlockId[] Smelters(BlockRegistry registry, string family, bool lit)
    {
        var ids = new BlockId[Placeable.Facings.Length];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = registry.ByName($"{family}_{FacingNames[i]}{(lit ? "_lit" : "")}").Id;
        return ids;
    }

    /// <summary>
    /// What every smelting block turns into when its flame goes in or out, keyed by raw block id.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>A table rather than a search through one family's array.</b> The swap used to be
    /// <c>Array.IndexOf</c> over the four cold furnaces, which answered -1 for anything that was not
    /// one — so the day a second family arrived, every blast furnace in the world would have gone on
    /// burning invisibly with nothing to say why. A lit form and a cold form are a property of a
    /// block, and this is where a block says so.
    /// </remarks>
    public static (BlockId[] Lighting, BlockId[] Cooling) SmelterStates(BlockRegistry registry)
    {
        var lighting = new BlockId[registry.Count];
        var cooling = new BlockId[registry.Count];

        foreach (var family in (string[])["furnace", "blast_furnace", "smoker"])
        {
            var cold = Smelters(registry, family, lit: false);
            var hot = Smelters(registry, family, lit: true);

            for (var i = 0; i < cold.Length; i++)
            {
                lighting[cold[i].Value] = hot[i];
                cooling[hot[i].Value] = cold[i];
            }
        }

        return (lighting, cooling);
    }

    /// <summary>Which kind of smelter a block is, or none at all.</summary>
    public static FurnaceKind[] SmelterKinds(BlockRegistry registry)
    {
        var kinds = new FurnaceKind[registry.Count];

        foreach (var lit in (bool[])[false, true])
        {
            foreach (var id in Furnaces(registry, lit)) kinds[id.Value] = FurnaceKind.Furnace;
            foreach (var id in BlastFurnaces(registry, lit)) kinds[id.Value] = FurnaceKind.Blast;
            foreach (var id in Smokers(registry, lit)) kinds[id.Value] = FurnaceKind.Smoker;
        }

        // ⛔ THE LIT CAMPFIRE ONLY, and that is what makes putting the fire out stop the cooking.
        // The kind is read off the cell every tick, so an extinguished campfire falls back to the
        // default — a plain furnace with no fuel in it, which does nothing. Nothing else has to know.
        foreach (var id in Campfires(registry, lit: true)) kinds[id.Value] = FurnaceKind.Campfire;

        return kinds;
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
            SoundMaterial.Deepstone, ToolClass.Pickaxe, 1),
        new("deepstone_bricks", LayerDeepstoneBricks, LayerDeepstoneBricks, LayerDeepstoneBricks,
            SoundMaterial.Deepstone, ToolClass.Pickaxe, 1),
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
    /// <param name="CollideHigh">
    /// How high a body finds it, in sixteenths, when that is not how tall it is drawn.
    /// </param>
    public readonly record struct ConnectedMaterial(
        string Name, ushort Layer, SoundMaterial Sound, ToolClass Harvest, int Tier,
        float PostHalf, float ArmHalf, (float Low, float High)[] Bars, float CollideHigh,
        bool Translucent = false);

    /// <summary>
    /// A fence and a wall stop a body half a block higher than they are drawn.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Named per material rather than derived as "everything except glass".</b> It is a rule
    /// about the game — a fence you can hop is not a fence — and the day something else joins its
    /// neighbours the question is what that thing is for, not which side of a negation it falls on.
    /// A pane is a window and collides with exactly what it is drawn as.
    /// </remarks>
    private const float FenceCollideHigh = 24f;

    private static readonly ConnectedMaterial[] ConnectedMaterials =
    [
        new("driftoak_fence", LayerPlanks, SoundMaterial.Wood, ToolClass.Axe, 0,
            2f, 1f, [(6f, 9f), (12f, 15f)], FenceCollideHigh),
        new("rubble_wall", LayerRubble, SoundMaterial.Stone, ToolClass.Pickaxe, 1,
            4f, 3f, [(0f, 14f)], FenceCollideHigh),
        new("stone_brick_wall", LayerStoneBricks, SoundMaterial.Stone, ToolClass.Pickaxe, 1,
            4f, 3f, [(0f, 14f)], FenceCollideHigh),
        new("deepstone_brick_wall", LayerDeepstoneBricks, SoundMaterial.Deepstone, ToolClass.Pickaxe, 1,
            4f, 3f, [(0f, 14f)], FenceCollideHigh),
        new("sandstone_wall", LayerSandstone, SoundMaterial.Stone, ToolClass.Pickaxe, 0,
            4f, 3f, [(0f, 14f)], FenceCollideHigh),
        new("brick_wall", LayerBricks, SoundMaterial.Stone, ToolClass.Pickaxe, 1,
            4f, 3f, [(0f, 14f)], FenceCollideHigh),
        new("glass_pane", LayerGlass, SoundMaterial.Glass, ToolClass.None, 0,
            1f, 1f, [(0f, 16f)], 0f),
        new("smokeglass_pane", LayerSmokeglass, SoundMaterial.Glass, ToolClass.None, 0,
            1f, 1f, [(0f, 16f)], 0f),

        // ⛳ Sixteen more panes, appended off the colour table rather than written out — which is the
        // whole argument for this table being a table. Same geometry as a plain pane in every number;
        // the only column that differs is the one that says it blends.
        .. StainedPanes(),
    ];

    /// <summary>A pane of each of the sixteen colours, identical to a plain one but for its tile.</summary>
    private static ConnectedMaterial[] StainedPanes()
    {
        var panes = new ConnectedMaterial[Colours.Length];

        for (var i = 0; i < panes.Length; i++)
            panes[i] = new ConnectedMaterial(
                $"stained_glass_pane_{Colours[i].Name}", (ushort)(LayerFirstStainedGlass + i),
                SoundMaterial.Glass, ToolClass.None, 0,
                1f, 1f, [(0f, 16f)], 0f, Translucent: true);

        return panes;
    }

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
                Translucent = m.Translucent,
                HarvestClass = m.Harvest, HarvestTier = m.Tier,
                Model = BlockModel.Connected(
                    m.Layer, m.Layer, m.Layer, m.PostHalf, m.ArmHalf, m.Bars, mask,
                    collideHigh: m.CollideHigh),
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
    /// <remarks>
    /// ⚠ The wet halves merge to the same dry whole: filling the cell squeezes the water out,
    /// which is the genre's own rule — a full cube has no room left to be waterlogged.
    /// </remarks>
    public static IEnumerable<(BlockId Slab, BlockId Full)> SlabMerges(BlockRegistry registry)
    {
        foreach (var (material, full) in SlabDoubles)
        {
            var whole = registry.ByName(full).Id;
            yield return (registry.ByName($"{material}_slab_lower").Id, whole);
            yield return (registry.ByName($"{material}_slab_upper").Id, whole);
            yield return (registry.ByName($"{material}_slab_lower{Waterlogging.Suffix}").Id, whole);
            yield return (registry.ByName($"{material}_slab_upper{Waterlogging.Suffix}").Id, whole);
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
