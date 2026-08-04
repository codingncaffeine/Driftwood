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
public readonly record struct BlockTextureLayer(string Name, string PackPath, bool Cutout);

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
        new("log_side",    "textures/block/oak_log.png",          false),
        new("log_top",     "textures/block/oak_log_top.png",      false),
        new("leaves",      "textures/block/oak_leaves.png",       true),
        new("planks",      "textures/block/oak_planks.png",       false),
        new("coal_ore",    "textures/block/coal_ore.png",         false),
        new("iron_ore",    "textures/block/iron_ore.png",         false),
        new("bedrock",     "textures/block/bedrock.png",          false),

        // Emberstone is ours; glowstone is the nearest thing a pack will have painted, and a warm
        // glowing rock is close enough that borrowing its art reads correctly.
        new("emberstone",  "textures/block/glowstone.png",        false),
        new("vine",        "textures/block/vine.png",             true),
    ];

    public sealed record Result(byte[][] Tiles, int Size, string Summary);

    /// <summary>
    /// Draws Driftwood's own tiles, then lets a pack replace the ones it has.
    /// </summary>
    /// <param name="packPath">A folder or .zip to import from, or null for Driftwood's own art.</param>
    public static Result Build(string? packPath, int size = TileGen.Size)
    {
        var tiles = new byte[Layers.Length][];
        for (var i = 0; i < Layers.Length; i++) tiles[i] = Own(i, size);

        if (string.IsNullOrWhiteSpace(packPath))
            return new Result(tiles, size, $"{Layers.Length} built-in tiles at {size}x{size}");

        using var pack = TexturePack.Open(packPath);
        if (pack is null)
            return new Result(tiles, size, $"no pack at '{packPath}' — using built-in tiles");

        for (var i = 0; i < Layers.Length; i++)
        {
            if (Layers[i].PackPath.Length == 0) continue;

            var replacement = pack.TryLoadTile(Layers[i].PackPath, size);
            if (replacement is not null) tiles[i] = replacement;
        }

        var summary = $"pack '{pack.Name}'"
                    + (pack.Description.Length > 0 ? $" — {pack.Description}" : "")
                    + $" (format {pack.Format}): {pack.Loaded} of {Layers.Length} layers replaced"
                    + (pack.Missing > 0 ? $", {pack.Missing} kept" : "")
                    + (pack.Faults.Count > 0 ? $", {pack.Faults.Count} unreadable: {pack.Faults[0]}" : "");

        return new Result(tiles, size, summary);
    }

    /// <summary>Driftwood's own art for one layer.</summary>
    private static byte[] Own(int layer, int size)
    {
        // Drawn at the native tile size and then scaled, so the generators stay written for one
        // size rather than being parameterised over every resolution a pack might arrive at.
        var tile = Draw(layer);
        return size == TileGen.Size ? tile : Upscale(tile, size);
    }

    private static byte[] Draw(int layer)
    {
        var stone = TileGen.Speckle(1001, 128, 128, 133, 16, 0.45f);
        var dirt = TileGen.Speckle(1002, 118, 85, 57, 18, 0.5f);

        return layer switch
        {
            StarterBlocks.LayerStone => stone,
            StarterBlocks.LayerDirt => dirt,
            StarterBlocks.LayerGrassTop => TileGen.Speckle(1003, 92, 153, 66, 20, 0.55f),
            StarterBlocks.LayerGrassSide => TileGen.GrassSide(1004, dirt, 92, 153, 66),
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
            _ => TileGen.Solid(255, 0, 255, 255),
        };
    }

    /// <summary>Nearest-neighbour upscale, so generated art stays as crisp as imported art.</summary>
    private static byte[] Upscale(byte[] tile, int size)
    {
        var scaled = new byte[size * size * 4];

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var sx = x * TileGen.Size / size;
            var sy = y * TileGen.Size / size;

            var src = (sy * TileGen.Size + sx) * 4;
            var dst = (y * size + x) * 4;

            scaled[dst] = tile[src];
            scaled[dst + 1] = tile[src + 1];
            scaled[dst + 2] = tile[src + 2];
            scaled[dst + 3] = tile[src + 3];
        }

        return scaled;
    }
}
