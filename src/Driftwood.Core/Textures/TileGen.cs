namespace Driftwood.Core.Textures;

/// <summary>
/// Draws Driftwood's own block textures in code, one 16x16 RGBA tile per layer.
/// </summary>
/// <remarks>
/// <para>Generated rather than painted, for now, because the alternative was shipping a flat colour
/// per block — and a flat colour hides everything the renderer does. Ambient occlusion, baked light,
/// the difference between a merged quad and four separate ones: none of it reads until there is
/// detail on the surface to read it against.</para>
/// <para>They are also unambiguously ours, which the whole project depends on. An imported texture
/// pack layers over this set rather than replacing a hole in it, and a pack that only reskins half
/// the blocks still leaves a complete world.</para>
/// <para>Deterministic from fixed seeds: the same build always draws the same tiles, so a texture
/// change is a code change and shows up in review rather than drifting.</para>
/// </remarks>
public static class TileGen
{
    public const int Size = 16;
    public const int Stride = Size * 4;
    public const int BytesPerTile = Size * Size * 4;

    /// <summary>Builds every layer's pixels, indexed by layer number.</summary>
    public static byte[][] BuildAll(int layerCount)
    {
        var tiles = new byte[layerCount][];
        for (var i = 0; i < layerCount; i++) tiles[i] = Solid(255, 0, 255, 255);   // loud, so a gap shows
        return tiles;
    }

    public static byte[] Solid(byte r, byte g, byte b, byte a)
    {
        var t = new byte[BytesPerTile];
        for (var i = 0; i < BytesPerTile; i += 4)
        {
            t[i] = r; t[i + 1] = g; t[i + 2] = b; t[i + 3] = a;
        }
        return t;
    }

    /// <summary>Flat colour roughened by per-pixel value noise. The backbone of most tiles.</summary>
    public static byte[] Speckle(int seed, byte r, byte g, byte b, int spread, float coarseness = 0f)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var n = Noise(x, y, seed) * 2f - 1f;

            // A second, blockier octave gives grain a sense of scale — without it every material
            // is the same television static in a different colour.
            if (coarseness > 0f)
                n = n * (1f - coarseness) + (Noise(x >> 2, y >> 2, seed + 7919) * 2f - 1f) * coarseness;

            var d = (int)(n * spread);
            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;
    }

    /// <summary>Speckle plus scattered blobs of a second colour, for ore in rock.</summary>
    public static byte[] Ore(int seed, byte[] baseTile, byte r, byte g, byte b, int blobs)
    {
        var t = (byte[])baseTile.Clone();

        for (var i = 0; i < blobs; i++)
        {
            var cx = (int)(Noise(i, 0, seed) * Size);
            var cy = (int)(Noise(0, i, seed + 31) * Size);
            var radius = 1 + (int)(Noise(i, i, seed + 61) * 2f);

            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > radius * radius) continue;

                // Wrap rather than clip: a tile that stops its detail at the edge shows a grid.
                var x = ((cx + dx) % Size + Size) % Size;
                var y = ((cy + dy) % Size + Size) % Size;

                var shade = (int)(Noise(x, y, seed + 97) * 24f) - 12;
                Put(t, x, y, Clamp(r + shade), Clamp(g + shade), Clamp(b + shade), 255);
            }
        }

        return t;
    }

    /// <summary>Vertical grain with a few darker knots — bark.</summary>
    public static byte[] Bark(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        for (var x = 0; x < Size; x++)
        {
            // One shade per column, so the grain runs the length of the trunk rather than
            // dissolving into noise.
            var columnShade = (int)((Noise(x, 0, seed) * 2f - 1f) * 22f);

            for (var y = 0; y < Size; y++)
            {
                var d = columnShade + (int)((Noise(x, y, seed + 13) * 2f - 1f) * 7f);
                if (Noise(x >> 1, y >> 2, seed + 53) > 0.93f) d -= 30;   // knot
                Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
            }
        }

        return t;
    }

    /// <summary>Concentric rings — the cut end of a log.</summary>
    public static byte[] Rings(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];
        const float centre = (Size - 1) / 2f;

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var dx = x - centre;
            var dy = y - centre;
            var radius = MathF.Sqrt(dx * dx + dy * dy);

            var ring = MathF.Sin(radius * 2.1f) * 12f;
            var grain = (Noise(x, y, seed) * 2f - 1f) * 6f;
            var d = (int)(ring + grain);

            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;
    }

    /// <summary>Horizontal boards with dark seams between them.</summary>
    public static byte[] Planks(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        {
            var board = y >> 2;
            var boardShade = (int)((Noise(board, 0, seed) * 2f - 1f) * 14f);
            var seam = (y & 3) == 0;

            for (var x = 0; x < Size; x++)
            {
                var d = boardShade + (int)((Noise(x, y, seed + 17) * 2f - 1f) * 8f);
                if (seam) d -= 34;

                // Staggered butt joints, so the boards do not read as one long plank.
                if ((x + board * 5) % 16 == 0) d -= 26;

                Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
            }
        }

        return t;
    }

    /// <summary>Horizontal bands of varying tone — sedimentary rock seen from the side.</summary>
    /// <remarks>
    /// Bands rather than speckle because that is the whole visual difference between sandstone and
    /// sand: the same grains, but laid down over time and readable as layers. Only for side faces —
    /// a top face wearing this shows the strata on edge.
    /// </remarks>
    public static byte[] Strata(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        {
            // A band every few rows, each its own tone, with the boundary between two of them
            // darker than either — that line is what the eye reads as a layer.
            var band = y / 3;
            var tone = (int)((Noise(0, band, seed) * 2f - 1f) * 16f);
            var boundary = y % 3 == 0;

            for (var x = 0; x < Size; x++)
            {
                var d = tone + (int)((Noise(x, y, seed + 23) * 2f - 1f) * 6f);
                if (boundary) d -= 18;

                Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
            }
        }

        return t;
    }

    /// <summary>Foliage: clumped colour with holes punched right through.</summary>
    /// <remarks>
    /// The holes are the point. Opaque leaves read as a solid green cube no matter how good the
    /// colour is; gaps let sky through and let the block behind show, which is what makes a canopy
    /// look like foliage rather than like a hedge.
    /// </remarks>
    public static byte[] Leaves(int seed, byte r, byte g, byte b, float holeChance)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var clump = Noise(x >> 1, y >> 1, seed + 3) * 2f - 1f;
            var fine = Noise(x, y, seed) * 2f - 1f;
            var d = (int)(clump * 26f + fine * 12f);

            var transparent = Noise(x, y, seed + 211) < holeChance;
            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), transparent ? (byte)0 : (byte)255);
        }

        return t;
    }

    /// <summary>Hanging strands, mostly empty.</summary>
    public static byte[] Vine(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        for (var x = 0; x < Size; x++)
        {
            var hasStrand = Noise(x, 0, seed) > 0.55f;
            for (var y = 0; y < Size; y++)
            {
                var on = hasStrand && Noise(x, y >> 1, seed + 5) > 0.22f;
                if (!on) { Put(t, x, y, 0, 0, 0, 0); continue; }

                var d = (int)((Noise(x, y, seed + 11) * 2f - 1f) * 20f);
                Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
            }
        }

        return t;
    }

    /// <summary>
    /// A dirt tile with a band of grass rolling over its top edge, drawn colourless.
    /// </summary>
    /// <remarks>
    /// Grey rather than green, and that is the format's convention rather than a choice. The fringe
    /// a player actually sees is <see cref="GrassSideOverlay"/> laid over this and multiplied by the
    /// climate colour; painting this one green as well would put a second, untinted green under the
    /// first, and every pack in the world would disagree with it.
    /// </remarks>
    public static byte[] GrassSide(int seed, byte[] dirt, byte level)
    {
        var t = (byte[])dirt.Clone();

        for (var x = 0; x < Size; x++)
        for (var y = 0; y < FringeDepth(x, seed); y++)
        {
            var d = (int)((Noise(x, y, seed + 29) * 2f - 1f) * 18f);
            Put(t, x, y, Clamp(level + d), Clamp(level + d), Clamp(level + d), 255);
        }

        return t;
    }

    /// <summary>The same fringe as a cut-out, for the climate colour to run through.</summary>
    /// <remarks>
    /// Built from the same edge as <see cref="GrassSide"/> so the two cannot drift apart: every
    /// pixel this one covers is a pixel that one made grey, and a mismatch would show as a rim of
    /// untinted grey along the top of every grass block in the world.
    /// </remarks>
    public static byte[] GrassSideOverlay(int seed, byte level)
    {
        var t = new byte[BytesPerTile];

        for (var x = 0; x < Size; x++)
        for (var y = 0; y < FringeDepth(x, seed); y++)
        {
            var d = (int)((Noise(x, y, seed + 29) * 2f - 1f) * 18f);
            Put(t, x, y, Clamp(level + d), Clamp(level + d), Clamp(level + d), 255);
        }

        return t;
    }

    /// <summary>How far the grass fringe reaches down a block's side at one column.</summary>
    /// <remarks>Ragged, not a straight line: the join is the most-looked-at edge in the game.</remarks>
    private static int FringeDepth(int x, int seed) => 3 + (int)(Noise(x, 0, seed) * 3f);

    /// <summary>Blades rising from the bottom edge, everything else empty — a tuft of plant.</summary>
    /// <remarks>
    /// Drawn bottom-heavy on purpose. The tile is stretched over two crossed planes standing in the
    /// block, so a blade that reaches the top edge meets the block above and the tuft reads as a
    /// column of grass rather than as something growing out of the ground.
    /// </remarks>
    public static byte[] Tuft(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        for (var x = 0; x < Size; x++)
        {
            if (Noise(x, 0, seed) < 0.42f) continue;

            // Height from the bottom, and a slow lean so the blades are not a picket fence.
            var height = 4 + (int)(Noise(x, 1, seed + 19) * 8f);
            var lean = Noise(x, 2, seed + 37) * 2f - 1f;

            for (var i = 0; i < height; i++)
            {
                var y = Size - 1 - i;
                var bx = x + (int)MathF.Round(lean * i * 0.25f);
                if ((uint)bx >= Size) continue;

                // Paler toward the tip, which is what stops a blade reading as a wire.
                var d = (int)(i / (float)height * 26f) + (int)((Noise(bx, y, seed + 53) * 2f - 1f) * 10f);
                Put(t, bx, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
            }
        }

        return t;
    }

    /// <summary>A stem with a bloom on it, most of the tile empty — one small flower.</summary>
    /// <remarks>
    /// Drawn on the same footing as <see cref="Tuft"/>: the tile is stretched over two crossed
    /// planes standing in the cell, so the stem has to start at the bottom edge and the bloom has to
    /// stop short of the top or the flower grows through the block above it.
    /// </remarks>
    public static byte[] Flower(int seed, byte stemR, byte stemG, byte stemB, byte r, byte g, byte b, byte coreR, byte coreG, byte coreB)
    {
        var t = new byte[BytesPerTile];
        const int Centre = Size / 2;

        var height = 8 + (int)(Noise(0, 0, seed) * 3f);
        for (var i = 0; i < height; i++)
        {
            var y = Size - 1 - i;
            var lean = (int)MathF.Round((Noise(0, i, seed + 7) * 2f - 1f) * 1.2f);
            var d = (int)((Noise(Centre, y, seed + 13) * 2f - 1f) * 12f);
            Put(t, Centre + lean, y, Clamp(stemR + d), Clamp(stemG + d), Clamp(stemB + d), 255);

            // A pair of leaves partway up, so the stem is not a bare wire.
            if (i != height / 2) continue;
            Put(t, Centre + lean - 1, y, stemR, stemG, stemB, 255);
            Put(t, Centre + lean + 1, y, stemR, stemG, stemB, 255);
        }

        // The bloom: a rough disc over the top of the stem with a different colour at its middle.
        var top = Size - height;
        for (var dy = -2; dy <= 2; dy++)
        for (var dx = -2; dx <= 2; dx++)
        {
            if (dx * dx + dy * dy > 5) continue;

            var x = Centre + dx;
            var y = top + dy;
            if ((uint)x >= Size || (uint)y >= Size) continue;

            var core = dx * dx + dy * dy <= 1;
            var d = (int)((Noise(x, y, seed + 29) * 2f - 1f) * 14f);
            Put(t, x, y,
                Clamp((core ? coreR : r) + d),
                Clamp((core ? coreG : g) + d),
                Clamp((core ? coreB : b) + d),
                255);
        }

        return t;
    }

    /// <summary>Glowing veins through dark rock.</summary>
    public static byte[] Ember(int seed, byte[] baseTile, byte r, byte g, byte b)
    {
        var t = (byte[])baseTile.Clone();

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var vein = Noise(x, y >> 1, seed) * 0.6f + Noise(x >> 1, y, seed + 7) * 0.4f;
            if (vein < 0.62f) continue;

            var heat = (vein - 0.62f) / 0.38f;
            Put(t, x, y,
                Clamp((int)(r * (0.55f + heat * 0.45f))),
                Clamp((int)(g * (0.35f + heat * 0.65f))),
                Clamp((int)(b * (0.25f + heat * 0.75f))),
                255);
        }

        return t;
    }

    /// <summary>
    /// The stages of a block coming apart, drawn as one nested set.
    /// </summary>
    /// <remarks>
    /// <para>Built together rather than one per call, and that is the whole design. Cracking has to
    /// be <em>cumulative</em> — every fracture visible at one stage must still be there at the next,
    /// or the block appears to heal between frames as the overlay swaps. Generating each stage
    /// independently cannot guarantee that however carefully the seeds are chosen.</para>
    /// <para>So each pixel is stamped with the moment it first fractures, and a stage is simply
    /// every pixel stamped at or before it. Fractures come from walkers wandering out across the
    /// face, which gives branching lines rather than the spatter that per-pixel noise produces.</para>
    /// </remarks>
    public static byte[][] Cracks(int seed, int stages)
    {
        const int Walkers = 16;

        // When each pixel first breaks, 0..1. Above 1 means it never does.
        var appears = new float[Size * Size];
        Array.Fill(appears, 2f);

        for (var w = 0; w < Walkers; w++)
        {
            var born = w / (float)Walkers;

            var x = Noise(w, 0, seed) * Size;
            var y = Noise(0, w, seed + 17) * Size;
            var angle = Noise(w, w, seed + 31) * MathF.Tau;
            var steps = 4 + (int)(Noise(w, 3, seed + 47) * 10f);

            for (var s = 0; s < steps; s++)
            {
                // Wander rather than run straight: a fracture that holds its heading reads as a
                // scratch, and a whole face of them reads as brushed metal.
                angle += (Noise(w, s, seed + 61) * 2f - 1f) * 0.9f;
                x += MathF.Cos(angle);
                y += MathF.Sin(angle);

                var px = ((int)MathF.Floor(x) % Size + Size) % Size;
                var py = ((int)MathF.Floor(y) % Size + Size) % Size;

                var i = py * Size + px;
                if (appears[i] > born) appears[i] = born;
            }
        }

        var tiles = new byte[stages][];

        for (var stage = 0; stage < stages; stage++)
        {
            var threshold = (stage + 1) / (float)stages;
            var tile = new byte[BytesPerTile];

            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                if (appears[y * Size + x] <= threshold)
                {
                    Put(tile, x, y, 22, 19, 17, 200);
                    continue;
                }

                // A softer edge beside every fracture. Without it the lines read as drawn on rather
                // than as the surface having given way.
                var beside = false;
                for (var dy = -1; dy <= 1 && !beside; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = ((x + dx) % Size + Size) % Size;
                    var ny = ((y + dy) % Size + Size) % Size;
                    if (appears[ny * Size + nx] > threshold) continue;
                    beside = true;
                    break;
                }

                Put(tile, x, y, 34, 30, 27, beside ? (byte)72 : (byte)0);
            }

            tiles[stage] = tile;
        }

        return tiles;
    }

    /// <summary>Nearest-neighbour upscale, so generated art stays as crisp as imported art.</summary>
    public static byte[] Upscale(byte[] tile, int size)
    {
        if (size == Size) return tile;

        var scaled = new byte[size * size * 4];

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var sx = x * Size / size;
            var sy = y * Size / size;

            var src = (sy * Size + sx) * 4;
            var dst = (y * size + x) * 4;

            scaled[dst] = tile[src];
            scaled[dst + 1] = tile[src + 1];
            scaled[dst + 2] = tile[src + 2];
            scaled[dst + 3] = tile[src + 3];
        }

        return scaled;
    }

    private static void Put(byte[] tile, int x, int y, byte r, byte g, byte b, byte a)
    {
        var i = y * Stride + x * 4;
        tile[i] = r; tile[i + 1] = g; tile[i + 2] = b; tile[i + 3] = a;
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    /// <summary>Deterministic 0..1 hash noise. Stateless, so tiles can be built in any order.</summary>
    private static float Noise(int x, int y, int seed)
    {
        unchecked
        {
            var h = seed;
            h ^= x * 0x27D4EB2D;
            h ^= y * unchecked((int)0x9E3779B1);
            h = (h ^ (h >> 15)) * 0x2C1B3C6D;
            h = (h ^ (h >> 12)) * 0x297A2D39;
            h ^= h >> 15;
            return (h & 0x00FFFFFF) / (float)0x01000000;
        }
    }
}
