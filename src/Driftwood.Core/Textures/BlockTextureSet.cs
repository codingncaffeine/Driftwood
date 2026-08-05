using Driftwood.Core.Blocks;

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
public readonly record struct BlockTextureLayer(
    string Name, string PackPath, bool Cutout, string PackPathAlt = "");

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
    /// <summary>Indexed by layer id — the same numbers <see cref="StarterBlocks"/> hands out.</summary>
    public static readonly BlockTextureLayer[] Layers =
    [
        new("stone",       "textures/block/stone.png",            false),
        new("dirt",        "textures/block/dirt.png",             false),
        new("grass_top",   "textures/block/grass_block_top.png",  false),
        new("grass_side",  "textures/block/grass_block_side.png", false),
        new("sand",        "textures/block/sand.png",             false),
        new("water",       "textures/block/water_still.png",      false),
        new("gravel",      "textures/block/gravel.png",           false),
        new("driftoak_side", "textures/block/oak_log.png",        false),
        new("driftoak_top",  "textures/block/oak_log_top.png",    false),
        new("driftoak_leaves", "textures/block/oak_leaves.png",   true),
        new("driftoak_planks", "textures/block/oak_planks.png",   false),
        new("coal_ore",    "textures/block/coal_ore.png",         false),
        new("iron_ore",    "textures/block/iron_ore.png",         false),
        new("bedrock",     "textures/block/bedrock.png",          false),

        // Emberstone is ours; glowstone is the nearest thing a pack will have painted, and a warm
        // glowing rock is close enough that borrowing its art reads correctly.
        new("emberstone",  "textures/block/glowstone.png",        false),
        new("vine",        "textures/block/vine.png",             true),

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
        new("grass_side_overlay", "textures/block/grass_block_side_overlay.png", true),

        // Short grass was called grass.png until it was renamed, so both paths are worth trying.
        new("meadowgrass", "textures/block/short_grass.png",      true, "textures/block/grass.png"),

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
        new("stormglass",  "textures/item/diamond.png",           true),
        new("azurite",     "textures/item/lapis_lazuli.png",      true),
        new("clay_lump",   "textures/item/clay_ball.png",         true),
        new("brick",       "textures/item/brick.png",             true),

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
        new("copper_pickaxe", "",                                 true),
        new("copper_axe",  "",                                    true),
        new("copper_shovel", "",                                  true),
        new("copper_sword", "",                                   true),
        new("gold_pickaxe", "textures/item/golden_pickaxe.png",   true),
        new("gold_axe",    "textures/item/golden_axe.png",        true),
        new("gold_shovel", "textures/item/golden_shovel.png",     true),
        new("gold_sword",  "textures/item/golden_sword.png",      true),
        new("iron_pickaxe", "textures/item/iron_pickaxe.png",     true),
        new("iron_axe",    "textures/item/iron_axe.png",          true),
        new("iron_shovel", "textures/item/iron_shovel.png",       true),
        new("iron_sword",  "textures/item/iron_sword.png",        true),
        new("stormglass_pickaxe", "textures/item/diamond_pickaxe.png", true),
        new("stormglass_axe", "textures/item/diamond_axe.png",    true),
        new("stormglass_shovel", "textures/item/diamond_shovel.png", true),
        new("stormglass_sword", "textures/item/diamond_sword.png", true),
    ];

    /// <param name="GrassMap">Grass colormap, the pack's if it ships one.</param>
    /// <param name="FoliageMap">Foliage colormap, likewise.</param>
    public sealed record Result(
        byte[][] Tiles, int Size, string Summary, byte[] GrassMap, byte[] FoliageMap);

    /// <summary>
    /// Draws Driftwood's own tiles, then lets a pack replace the ones it has.
    /// </summary>
    /// <param name="packPath">A folder or .zip to import from, or null for Driftwood's own art.</param>
    /// <summary>
    /// Draws Driftwood's own tiles, then lets a pack replace the ones it has.
    /// </summary>
    /// <param name="packPath">A folder or .zip to import from, or null for Driftwood's own art.</param>
    /// <param name="size">
    /// The tile size to build at, or 0 to take the pack's own. Zero is the default because a
    /// player who chose a pack has already said what resolution they want.
    /// </param>
    /// <param name="ceiling">The largest tile the machine will take. See <c>--texture-size</c>.</param>
    public static Result Build(string? packPath, int size = 0, int ceiling = 512)
    {
        var grass = Colormap.Grass();
        var foliage = Colormap.Foliage();

        if (string.IsNullOrWhiteSpace(packPath))
        {
            var own = size > 0 ? size : TileGen.Size;
            var plain = new byte[Layers.Length][];
            for (var i = 0; i < Layers.Length; i++) plain[i] = Own(i, own);

            return new Result(plain, own, $"{Layers.Length} built-in tiles at {own}x{own}", grass, foliage);
        }

        using var pack = TexturePack.Open(packPath);
        if (pack is null)
        {
            var own = size > 0 ? size : TileGen.Size;
            var plain = new byte[Layers.Length][];
            for (var i = 0; i < Layers.Length; i++) plain[i] = Own(i, own);

            return new Result(plain, own, $"no pack at '{packPath}' — using built-in tiles", grass, foliage);
        }

        // The pack's own resolution unless somebody said otherwise, clamped to what the machine
        // will take. Asking the pack is the difference between a 512-pixel import looking like the
        // pack and looking like a bad photograph of it.
        var detected = pack.DetectResolution();
        var chosen = Math.Clamp(size > 0 ? size : detected, TileGen.Size, Math.Max(TileGen.Size, ceiling));

        var tiles = new byte[Layers.Length][];
        for (var i = 0; i < Layers.Length; i++) tiles[i] = Own(i, chosen);

        size = chosen;

        for (var i = 0; i < Layers.Length; i++)
        {
            if (Layers[i].PackPath.Length == 0) continue;

            var replacement = pack.TryLoadTile(Layers[i].PackPath, size);
            if (replacement is null && Layers[i].PackPathAlt.Length > 0)
                replacement = pack.TryLoadTile(Layers[i].PackPathAlt, size);
            if (replacement is not null) tiles[i] = replacement;
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
                    + $" (format {pack.Format}, {resolution}): "
                    + $"{pack.Loaded - colormaps} of {Layers.Length} layers replaced"
                    + (colormaps > 0 ? $", {colormaps} colormaps" : ", built-in colormaps")
                    + (pack.Namespaces.Count > 1 ? $", {pack.Namespaces.Count} namespaces" : "")
                    + (pack.Faults.Count > 0 ? $", {pack.Faults.Count} unreadable: {pack.Faults[0]}" : "");

        return new Result(tiles, size, summary, grass, foliage);
    }

    /// <summary>Driftwood's own art for one layer.</summary>
    private static byte[] Own(int layer, int size)
    {
        // Drawn at the native tile size and then scaled, so the generators stay written for one
        // size rather than being parameterised over every resolution a pack might arrive at.
        var tile = Draw(layer);
        return TileGen.Upscale(tile, size);
    }

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

            _ => Tool(layer),
        };
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
