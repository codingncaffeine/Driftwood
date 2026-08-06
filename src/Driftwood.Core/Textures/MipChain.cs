namespace Driftwood.Core.Textures;

/// <summary>
/// Halving a tile toward the next mip level, in the two ways it can be done.
/// </summary>
/// <remarks>
/// <para>In Core rather than beside the uploader for one reason: the difference between the two is
/// the whole point and it can be measured without a card. See <see cref="Validate"/>.</para>
/// <para><b>The problem.</b> A cut-out texture — leaves, vines, a pane of glass, a grass fringe —
/// is drawn by discarding texels below a threshold rather than by blending them. What colour those
/// discarded texels hold is therefore arbitrary, and in almost every pack it is black, because that
/// is what an image editor leaves under an alpha of zero. Average rgba flat and every mip level pulls
/// the edge of the ink toward that black: foliage grows a dark halo that gets worse with distance,
/// and it is a known enough problem that the format grew a <c>mipmap_strategy</c> field for it.</para>
/// </remarks>
public static class MipChain
{
    /// <summary>
    /// One level down, each texel weighted by its own alpha.
    /// </summary>
    /// <remarks>
    /// A fully transparent neighbour contributes nothing to the colour and everything to the
    /// coverage, which is what keeps an edge the colour the artist painted it while still fading it
    /// out. This is the format's <c>dark_cutout</c> in effect.
    /// </remarks>
    public static byte[] Halve(byte[] tile, int size) => Reduce(tile, size, weighted: true);

    /// <summary>
    /// One level down, averaged flat. What a driver's own mipmap generator does.
    /// </summary>
    /// <remarks>
    /// Here so the check has something to disagree with. A claim that the weighted version keeps an
    /// edge brighter is only a claim if the thing it is brighter than is in the room.
    /// </remarks>
    public static byte[] HalveFlat(byte[] tile, int size) => Reduce(tile, size, weighted: false);

    private static byte[] Reduce(byte[] tile, int size, bool weighted)
    {
        var half = size / 2;
        var next = new byte[half * half * 4];

        for (var y = 0; y < half; y++)
        for (var x = 0; x < half; x++)
        {
            int r = 0, g = 0, b = 0, a = 0, weight = 0;

            for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
            {
                var src = ((y * 2 + dy) * size + x * 2 + dx) * 4;
                var alpha = tile[src + 3];
                var w = weighted ? alpha : 1;

                r += tile[src] * w;
                g += tile[src + 1] * w;
                b += tile[src + 2] * w;
                a += alpha;
                weight += w;
            }

            var dst = (y * half + x) * 4;
            if (weight > 0)
            {
                next[dst] = (byte)(r / weight);
                next[dst + 1] = (byte)(g / weight);
                next[dst + 2] = (byte)(b / weight);
            }

            next[dst + 3] = (byte)(a / 4);
        }

        return next;
    }

    /// <summary>
    /// Checks the weighted halving keeps a cut-out's edge, and changes nothing about a solid one.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Two claims, and the second is what makes the first mean anything.</b> On a tile with
    /// transparent holes in it the weighted reduction has to come out visibly brighter than the flat
    /// one — that is the dark fringe, measured rather than asserted. On a tile with no transparency
    /// at all the two have to agree <em>exactly</em>, because there is no transparent neighbour to
    /// weight differently; if they disagree there, the weighting is doing something to every texture
    /// in the game rather than to the edges of the cut-out ones, and the first number is measuring
    /// that instead.
    /// </remarks>
    public static List<string> Validate()
    {
        var faults = new List<string>();

        // ⛔ NOT OUR OWN LEAVES, AND THE REASON IS A FINDING. `TileGen.Leaves` writes the leaf colour
        // under an alpha of zero — the holes hold green, not black — so flat averaging costs it
        // nothing and the first version of this check failed on 76.1 against 76.2. Our art does not
        // have the problem. IMPORTED ART DOES: an image editor leaves black under a cleared pixel,
        // which is what every pack ships, and that is what the re-mip exists for. So the tile the
        // check uses is authored the way packs author them.
        var leaves = new byte[TileGen.Size * TileGen.Size * 4];
        for (var y = 0; y < TileGen.Size; y++)
        for (var x = 0; x < TileGen.Size; x++)
        {
            var at = (y * TileGen.Size + x) * 4;

            // A ragged blob, so there is a real boundary rather than one straight edge.
            var inside = (x + y) % 5 != 0 && x is > 1 and < TileGen.Size - 2 && y is > 1 and < TileGen.Size - 2;
            if (!inside) continue;

            leaves[at] = 61;
            leaves[at + 1] = 120;
            leaves[at + 2] = 51;
            leaves[at + 3] = 255;
        }

        var kept = Halve(leaves, TileGen.Size);
        var flat = HalveFlat(leaves, TileGen.Size);

        long keptSum = 0, flatSum = 0;
        var counted = 0;

        for (var p = 0; p < kept.Length; p += 4)
        {
            // Only where something is drawn. A texel that is transparent in both is not on the
            // screen either way and would dilute the difference toward nothing.
            if (kept[p + 3] < 8) continue;

            keptSum += kept[p] + kept[p + 1] + kept[p + 2];
            flatSum += flat[p] + flat[p + 1] + flat[p + 2];
            counted++;
        }

        if (counted == 0)
        {
            faults.Add("the cut-out tile has no visible texels at half size, so nothing was compared");
            return faults;
        }

        var keptMean = keptSum / (double)counted / 3.0;
        var flatMean = flatSum / (double)counted / 3.0;

        if (keptMean <= flatMean * 1.05)
        {
            faults.Add(
                $"weighted halving reads {keptMean:F1} against flat's {flatMean:F1} — "
                + "no brighter, so the dark fringe is not being kept out");
        }

        // The control: stone has no transparency anywhere, so the two must agree to the byte.
        var stone = TileGen.Speckle(1001, 128, 128, 132, 18, 0.55f);
        if (!Halve(stone, TileGen.Size).AsSpan().SequenceEqual(HalveFlat(stone, TileGen.Size)))
            faults.Add("the two halvings disagree on an opaque tile, so the weighting is not about alpha");

        return faults;
    }
}
