using Driftwood.Core.Blocks;

namespace Driftwood.Core.Textures;

/// <summary>
/// Which cell of a pre-1.6 <c>terrain.png</c> each of our layers comes off.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The third column, and it is a different KIND of column.</b> Every other layout in this
/// project answers "where is the file" and resolves through
/// <see cref="PackLayouts.Legacy"/>; a 2012 pack has no per-texture file at all. A block is a cell
/// number on one image, so the whole candidate-path mechanism misses it by construction — which is
/// exactly why the shelf reported one of the user's own downloads as a layout we do not read.</para>
/// <para>⛔ <b>EVERY INDEX BELOW WAS MEASURED, NOT REMEMBERED.</b> The classic grid is well known and
/// half-knowing it is worse than not knowing it: an earlier candidate table put diamond ore at a cell
/// that samples plain grey. These come off <c>--atlas</c>, run against the user's own
/// <c>Picture-perfect-pack-128X128</c>, and the evidence for each is written beside it — gold block
/// at 224,167,20 is the most distinctive cell on the sheet, bedrock is near-black, sand is pale,
/// water is blue, lava is orange, and a cutout is a cell that is mostly clear.</para>
/// <para>⚠ <b>A sparse table on purpose.</b> Cells that could not be told apart by colour are left
/// out, and a layer with no entry keeps our own art — which is precisely how a sparse modern pack
/// already behaves. Guessing at the ambiguous ones would put a dispenser on a furnace and there
/// would be nothing anywhere to say so. Run <c>--atlas</c> against another pack of this era to
/// extend it; the instrument is the point.</para>
/// </remarks>
public static class PackAtlas
{
    /// <summary>One of our layers, the cell it reads, and which sheet that cell is on.</summary>
    /// <param name="Items">True for <c>gui/items.png</c> rather than <c>terrain.png</c>.</param>
    public readonly record struct Cell(int Index, bool Items = false);

    private static readonly Dictionary<ushort, Cell> Grid = new()
    {
        // Row 0. Grass is 68,109,30 and nothing else on the sheet is that green; dirt is the only
        // 102,81,58; planks the only 174,140,86; bricks 154,113,86.
        [StarterBlocks.LayerGrassTop] = new(0),
        [StarterBlocks.LayerStone] = new(1),
        [StarterBlocks.LayerDirt] = new(2),
        [StarterBlocks.LayerGrassSide] = new(3),
        [StarterBlocks.LayerPlanks] = new(4),

        // ⚠ Cell 6 is the top of a stone slab, which IS the smooth stone face — the same
        // relationship the modern layout states outright by calling the file smooth_stone.
        [StarterBlocks.LayerSmoothStone] = new(6),
        [StarterBlocks.LayerBricks] = new(7),

        // The two flowers whose colours settle them: 13 is yellow at 11% cover, 12 is red at 17%.
        [StarterBlocks.LayerEmberbloom] = new(12),
        [StarterBlocks.LayerSunwort] = new(13),

        // Row 1. Bedrock is 29,26,26 — the darkest solid cell — and sand the palest at 234,209,160.
        [StarterBlocks.LayerRubble] = new(16),
        [StarterBlocks.LayerBedrock] = new(17),
        [StarterBlocks.LayerSand] = new(18),
        [StarterBlocks.LayerGravel] = new(19),
        [StarterBlocks.LayerLogSide] = new(20),
        [StarterBlocks.LayerLogTop] = new(21),
        [StarterBlocks.LayerChestTop] = new(25),
        [StarterBlocks.LayerChestSide] = new(26),
        [StarterBlocks.LayerChestFront] = new(27),

        // Row 2. The three ores sit together and are told apart by their fleck rather than their
        // rock, so they are taken as a run: 32, 33, 34 in the order gold, iron, coal.
        [StarterBlocks.LayerGoldOre] = new(32),
        [StarterBlocks.LayerIronOre] = new(33),
        [StarterBlocks.LayerCoalOre] = new(34),

        // ⛳ The overlay, and this is the one that most changes how a world looks. 66,106,29 at 30%
        // cover is a green cut-out — the grass fringe every pack in the genre paints separately and
        // the thing an importer that missed it renders as plain dirt sides.
        [StarterBlocks.LayerGrassSideOverlay] = new(38),
        [StarterBlocks.LayerMeadowgrass] = new(39),

        // The bench: three tan cells at 43, 59 and 60, which are its top, side and front.
        [StarterBlocks.LayerBenchTop] = new(43),
        [StarterBlocks.LayerBenchSide] = new(59),
        [StarterBlocks.LayerBenchFront] = new(60),

        // ⚠ 44 is markedly darker than 45 and 46 (78,78,78 against 113 and 116). A furnace front has
        // a mouth in it and the sides do not, which is what makes the darker of three greys the
        // front rather than a coin toss.
        [StarterBlocks.LayerFurnaceFront] = new(44),
        [StarterBlocks.LayerFurnaceSide] = new(45),

        // Row 3. Glass is 30% cover, which nothing else near it is; diamond ore reads 117,119,128 —
        // and the cell an earlier remembered table put diamond in samples plain grey.
        [StarterBlocks.LayerGlass] = new(49),
        [StarterBlocks.LayerStormglassOre] = new(50),
        [StarterBlocks.LayerLeaves] = new(52),
        [StarterBlocks.LayerStoneBricks] = new(54),

        // Row 4. Snow is 241,240,251 and there is nothing else that white; clay is the only bluish
        // grey at 158,164,176.
        [StarterBlocks.LayerSnow] = new(66),
        [StarterBlocks.LayerClay] = new(72),

        // Row 5. A torch is 8% cover — a stick on an empty tile — and a ladder 25%.
        [StarterBlocks.LayerTorch] = new(80),
        [StarterBlocks.LayerDoorUpper] = new(81),
        [StarterBlocks.LayerLadder] = new(83),
        [StarterBlocks.LayerTrapdoor] = new(84),
        [StarterBlocks.LayerDoorLower] = new(97),

        // ⚠ 143 is 53% cover and achromatic, which is a vine painted near-greyscale expecting the
        // climate tint — the same convention the modern packs use and the reason ours is tinted.
        [StarterBlocks.LayerVine] = new(143),

        // Row 10. Lapis ore is the only blue-grey rock on the sheet, and azurite is ours for it.
        [StarterBlocks.LayerAzuriteOre] = new(160),

        // Rows 11-13. Sandstone runs top / side / bottom down one column at 176, 192, 208.
        [StarterBlocks.LayerSandstoneTop] = new(176),
        [StarterBlocks.LayerSandstone] = new(192),

        // ⛳ The fluids, and they are the two most obvious cells on the whole sheet: five cells of
        // 65,133,191 and five of 221,89,5. Nothing else is blue and nothing else is orange.
        [StarterBlocks.LayerWater] = new(205),
        [StarterBlocks.LayerLava] = new(237),
    };

    /// <summary>The cell this layer reads on a 2012 pack, or null when it has none.</summary>
    public static Cell? Of(int layer) =>
        Grid.TryGetValue((ushort)layer, out var cell) ? cell : null;

    /// <summary>How many of our layers this era can supply at all.</summary>
    public static int Mapped => Grid.Count;

    /// <summary>
    /// Checks the table names layers that exist and no cell twice.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Two layers reading one cell is the fault this catches and nothing else would.</b> It
    /// imports cleanly, every tile is painted and the count is right — the world simply has the
    /// wrong picture on one of the two blocks, and only somebody who knows what that block should
    /// look like would ever notice. ⚠ It is also a real thing in this format: the sandstone bottom
    /// and top are genuinely the same picture, so the check is "no DUPLICATE", not "all distinct" —
    /// it is written as one and there are none, which is the state to hold it to.
    /// </remarks>
    public static List<string> Validate()
    {
        var faults = new List<string>();
        var seen = new Dictionary<int, ushort>();

        foreach (var (layer, cell) in Grid)
        {
            if (layer >= StarterBlocks.LayerCount)
            {
                faults.Add($"the atlas table names layer {layer}, past the {StarterBlocks.LayerCount} there are");
                continue;
            }

            if (cell.Index is < 0 or >= TexturePack.AtlasCells * TexturePack.AtlasCells)
            {
                faults.Add($"'{BlockTextureSet.Layers[layer].Name}' reads cell {cell.Index}, which is off the grid");
                continue;
            }

            var key = cell.Index + (cell.Items ? 1000 : 0);
            if (seen.TryGetValue(key, out var already))
            {
                faults.Add($"'{BlockTextureSet.Layers[layer].Name}' and "
                         + $"'{BlockTextureSet.Layers[already].Name}' both read cell {cell.Index}");
                continue;
            }

            seen[key] = layer;
        }

        return faults;
    }
}
