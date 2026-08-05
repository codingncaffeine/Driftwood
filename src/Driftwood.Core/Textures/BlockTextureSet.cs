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
/// <param name="Tinted">
/// True when this layer is multiplied by a climate colour before it reaches the screen.
/// </param>
public readonly record struct BlockTextureLayer(
    string Name, string PackPath, bool Cutout, string PackPathAlt = "", bool Tinted = false);

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
    /// <summary>What happened to one layer, for the report that says whether a pack is being used.</summary>
    /// <param name="From">The path it came from, or empty when it kept Driftwood's own art.</param>
    public readonly record struct LayerOutcome(string Name, bool Replaced, string From, bool Neutralised);

    public sealed record Result(
        byte[][] Tiles, int Size, string Summary, byte[] GrassMap, byte[] FoliageMap,
        IReadOnlyList<LayerOutcome> Outcomes)
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

            return new Result(plain, own, $"{Layers.Length} built-in tiles at {own}x{own}", grass, foliage, Untouched());
        }

        using var pack = TexturePack.Open(packPath);
        if (pack is null)
        {
            var own = size > 0 ? size : TileGen.Size;
            var plain = new byte[Layers.Length][];
            for (var i = 0; i < Layers.Length; i++) plain[i] = Own(i, own);

            return new Result(plain, own, $"no pack at '{packPath}' — using built-in tiles", grass, foliage, Untouched());
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
        var outcomes = new List<LayerOutcome>(Layers.Length);

        for (var i = 0; i < Layers.Length; i++)
        {
            if (Layers[i].PackPath.Length == 0)
            {
                outcomes.Add(new LayerOutcome(Layers[i].Name, false, "", false));
                continue;
            }

            var replacement = pack.TryLoadTile(Layers[i].PackPath, size, out var from);

            if (replacement is null && Layers[i].PackPathAlt.Length > 0)
                replacement = pack.TryLoadTile(Layers[i].PackPathAlt, size, out from);

            if (replacement is null)
            {
                outcomes.Add(new LayerOutcome(Layers[i].Name, false, "", false));
                continue;
            }

            var flattened = Layers[i].Tinted && Neutralise(replacement);
            if (flattened) neutralised++;

            tiles[i] = replacement;

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
                    + $" ({pack.Dialect.ToString().ToLowerInvariant()} format {pack.Format}, {resolution}): "
                    + $"{pack.Loaded - colormaps} of {Layers.Length} layers replaced"
                    + (colormaps > 0 ? $", {colormaps} colormaps" : ", built-in colormaps")
                    + (pack.Namespaces.Count > 1 ? $", {pack.Namespaces.Count} namespaces" : "")
                    + (neutralised > 0 ? $", {neutralised} tinted layers flattened" : "")
                    + (pack.Faults.Count > 0 ? $", {pack.Faults.Count} unreadable: {pack.Faults[0]}" : "");

        return new Result(tiles, size, summary, grass, foliage, outcomes);
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
