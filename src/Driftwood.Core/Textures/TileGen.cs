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

    /// <summary>A stick down the middle with a flame on it, everything else empty — a torch.</summary>
    /// <remarks>
    /// Two columns wide and stopping ten rows up, because the model reads that patch and no other:
    /// the cap of the post samples the 2x2 square at the top of the stick and the sides stretch the
    /// whole tile across the cell. Art drawn anywhere else on the tile is art nobody sees.
    /// </remarks>
    public static byte[] Torch(int seed)
    {
        var t = new byte[BytesPerTile];
        const int Left = 7;

        // The stick, from the bottom edge up to where the flame starts.
        for (var y = 8; y < Size; y++)
        for (var x = Left; x < Left + 2; x++)
        {
            var d = (int)((Noise(x, y, seed) * 2f - 1f) * 18f);
            Put(t, x, y, Clamp(148 + d), Clamp(112 + d), Clamp(68 + d), 255);
        }

        // The flame, sitting in the 2x2 the model's cap reads plus a little above it.
        for (var y = 5; y < 8; y++)
        for (var x = Left - 1; x < Left + 3; x++)
        {
            var edge = y == 5 || x == Left - 1 || x == Left + 2;
            if (edge && Noise(x, y, seed + 41) < 0.45f) continue;

            var heat = 1f - (y - 5) / 3f;
            Put(t, x, y,
                Clamp((int)(232 + heat * 20f)),
                Clamp((int)(150 + heat * 80f)),
                Clamp((int)(60 + heat * 90f)),
                255);
        }

        return t;
    }

    /// <summary>The heart the health bar is counted in, as a white shape to be tinted.</summary>
    /// <remarks>
    /// Drawn white and coloured at the point of use, so full, half and empty are one tile and three
    /// tints rather than three tiles that can drift apart. The outline is part of the shape rather
    /// than a separate pass: an empty heart is the same pixels in a dark colour, and it has to read
    /// as the same heart or the bar looks like two different rows.
    /// </remarks>
    public static byte[] Heart()
    {
        var t = new byte[BytesPerTile];

        // Two lobes and a point. Written as a coverage test rather than as a pixel list so it
        // scales with the tile size instead of being a bitmap somebody has to redraw.
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var u = (x + 0.5f) / Size * 2f - 1f;
            var v = 1f - (y + 0.5f) / Size * 1.15f;

            var lobes = MathF.Min(
                Distance(u, v, -0.42f, 0.42f, 0.46f),
                Distance(u, v, 0.42f, 0.42f, 0.46f));
            var body = MathF.Abs(u) + MathF.Max(0f, 0.60f - v * 1.55f) - 0.60f;

            var inside = lobes < 0f || (body < 0f && v < 0.46f);
            if (!inside) continue;

            // A darker rim wherever the shape is about to end, so the tint has an edge to read.
            var edge = lobes > -0.10f && body > -0.10f;
            Put(t, x, y, edge ? (byte)176 : (byte)255, edge ? (byte)176 : (byte)255, edge ? (byte)176 : (byte)255, 255);
        }

        return t;

        static float Distance(float x, float y, float cx, float cy, float r) =>
            MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;
    }

    /// <summary>The ten digits, as white glyphs on a transparent tile.</summary>
    /// <remarks>
    /// A three by five grid blown up to the tile, which is the smallest shape a digit is still
    /// legible at and the shape every low-resolution font in the genre uses. Written as bit rows
    /// rather than drawn, because a digit is a fixed thing and generating one procedurally would
    /// mean inventing a typeface.
    /// </remarks>
    public static byte[][] Digits()
    {
        // Five rows of three bits each, most significant bit on the left.
        ushort[] glyphs =
        [
            0b111_101_101_101_111,
            0b010_110_010_010_111,
            0b111_001_111_100_111,
            0b111_001_111_001_111,
            0b101_101_111_001_001,
            0b111_100_111_001_111,
            0b111_100_111_101_111,
            0b111_001_001_001_001,
            0b111_101_111_101_111,
            0b111_101_111_001_111,
        ];

        var tiles = new byte[10][];
        for (var d = 0; d < 10; d++)
        {
            var tile = new byte[BytesPerTile];
            var cell = Size / 5;                 // one glyph pixel, in tile pixels
            var left = (Size - cell * 3) / 2;

            for (var row = 0; row < 5; row++)
            for (var column = 0; column < 3; column++)
            {
                var bit = (glyphs[d] >> (14 - (row * 3 + column))) & 1;
                if (bit == 0) continue;

                for (var y = 0; y < cell; y++)
                for (var x = 0; x < cell; x++)
                    Put(tile, left + column * cell + x, row * cell + y, 255, 255, 255, 255);
            }

            tiles[d] = tile;
        }

        return tiles;
    }

    /// <summary>The bubble the breath meter is counted in, likewise white.</summary>
    public static byte[] Bubble()
    {
        var t = new byte[BytesPerTile];
        const float Centre = (Size - 1) / 2f;

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var dx = (x - Centre) / (Size * 0.42f);
            var dy = (y - Centre) / (Size * 0.42f);
            var r = MathF.Sqrt(dx * dx + dy * dy);
            if (r > 1f) continue;

            // A highlight up and left, which is what makes a flat disc read as a bubble.
            var lit = (x - Centre) < -Size * 0.10f && (y - Centre) < -Size * 0.10f && r > 0.35f && r < 0.78f;
            var rim = r > 0.72f;

            Put(t, x, y,
                lit ? (byte)255 : rim ? (byte)190 : (byte)225,
                lit ? (byte)255 : rim ? (byte)190 : (byte)225,
                lit ? (byte)255 : rim ? (byte)190 : (byte)225,
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

    /// <summary>Irregular stones in dark mortar — broken rock rather than cut rock.</summary>
    /// <remarks>
    /// The mortar is what tells rubble from stone at a glance. Speckle alone reads as the same grey
    /// at a different roughness, and the two blocks sit next to each other constantly: one is what a
    /// pickaxe leaves and the other is what a furnace gives back.
    /// </remarks>
    public static byte[] Cobble(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        // Each pixel belongs to whichever scattered stone centre is nearest, which gives blocky
        // irregular cells; the seams between them become the mortar.
        Span<int> cx = stackalloc int[7];
        Span<int> cy = stackalloc int[7];
        Span<int> shade = stackalloc int[7];
        for (var i = 0; i < 7; i++)
        {
            cx[i] = (int)(Noise(i, 0, seed) * Size);
            cy[i] = (int)(Noise(0, i, seed + 41) * Size);
            shade[i] = (int)((Noise(i, i, seed + 83) * 2f - 1f) * 26f);
        }

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var best = 0;
            var bestD = int.MaxValue;
            var secondD = int.MaxValue;

            for (var i = 0; i < 7; i++)
            {
                // Wrapped distance, so the stones carry across the tile edge and the block does not
                // show a grid where two of its faces meet.
                var dx = Math.Abs(x - cx[i]);
                var dy = Math.Abs(y - cy[i]);
                dx = Math.Min(dx, Size - dx);
                dy = Math.Min(dy, Size - dy);

                var d = dx * dx + dy * dy;
                if (d < bestD) { secondD = bestD; bestD = d; best = i; }
                else if (d < secondD) secondD = d;
            }

            var grain = (int)((Noise(x, y, seed + 17) * 2f - 1f) * 9f);
            var mortar = secondD - bestD <= 2;

            Put(t, x, y,
                Clamp(r + shade[best] + grain - (mortar ? 46 : 0)),
                Clamp(g + shade[best] + grain - (mortar ? 46 : 0)),
                Clamp(b + shade[best] + grain - (mortar ? 46 : 0)),
                255);
        }

        return t;
    }

    /// <summary>Courses of brick in running bond, offset every other row.</summary>
    public static byte[] Bricks(int seed, byte r, byte g, byte b, byte mortar)
    {
        var t = new byte[BytesPerTile];
        const int CourseHeight = 4;
        const int BrickWidth = 8;

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var course = y / CourseHeight;
            var offset = (course & 1) * (BrickWidth / 2);
            var joint = y % CourseHeight == 0 || (x + offset) % BrickWidth == 0;

            if (joint)
            {
                var m = (int)((Noise(x, y, seed + 7) * 2f - 1f) * 6f);
                Put(t, x, y, Clamp(mortar + m), Clamp(mortar + m), Clamp(mortar + m), 255);
                continue;
            }

            // One shade per brick, so a course reads as bricks rather than as a striped wall.
            var brick = (int)((Noise((x + offset) / BrickWidth, course, seed) * 2f - 1f) * 18f);
            var d = brick + (int)((Noise(x, y, seed + 31) * 2f - 1f) * 7f);
            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;
    }

    /// <summary>A pane: a pale frame, a corner highlight, and nothing in the middle.</summary>
    /// <remarks>
    /// Almost entirely transparent, which is the point — a glass tile that is a translucent wash
    /// reads as dirty ice, and cannot be drawn in the cut-out pass the rest of our alpha uses.
    /// </remarks>
    public static byte[] Glass(int seed)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var edge = x == 0 || y == 0 || x == Size - 1 || y == Size - 1;

            // A short diagonal streak in the upper left, the way a pane catches the sky.
            var streak = y >= 2 && y <= 6 && x - y >= 1 && x - y <= 3;

            if (!edge && !streak) continue;

            var d = (int)((Noise(x, y, seed) * 2f - 1f) * 10f);
            Put(t, x, y,
                Clamp(214 + d), Clamp(230 + d), Clamp(238 + d),
                edge ? (byte)200 : (byte)120);
        }

        return t;
    }

    /// <summary>A tile with a darker inset panel on it — the side and front of built furniture.</summary>
    public static byte[] Panel(byte[] baseTile, int inset, int darken)
    {
        var t = (byte[])baseTile.Clone();

        for (var y = inset; y < Size - inset; y++)
        for (var x = inset; x < Size - inset; x++)
        {
            var border = x == inset || y == inset || x == Size - 1 - inset || y == Size - 1 - inset;
            var i = y * Stride + x * 4;
            var d = border ? darken : darken / 3;

            t[i] = Clamp(t[i] - d);
            t[i + 1] = Clamp(t[i + 1] - d);
            t[i + 2] = Clamp(t[i + 2] - d);
        }

        return t;
    }

    /// <summary>A tile crossed by grooves — the worn top of a bench.</summary>
    public static byte[] Scored(int seed, byte[] baseTile)
    {
        var t = (byte[])baseTile.Clone();

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var groove = x == Size / 2 || y == Size / 2 || x == 0 || y == 0;
            var nick = Noise(x, y, seed) > 0.94f;
            if (!groove && !nick) continue;

            var i = y * Stride + x * 4;
            var d = groove ? 40 : 22;
            t[i] = Clamp(t[i] - d);
            t[i + 1] = Clamp(t[i + 1] - d);
            t[i + 2] = Clamp(t[i + 2] - d);
        }

        return t;
    }

    /// <summary>A stone face with a mouth in it, dark or burning — the front of a furnace.</summary>
    public static byte[] Hearth(int seed, byte[] baseTile, bool lit)
    {
        var t = (byte[])baseTile.Clone();

        for (var y = 5; y < 14; y++)
        for (var x = 3; x < 13; x++)
        {
            // A rounded top on the opening, so it reads as an arch rather than as a letterbox.
            if (y == 5 && (x < 5 || x > 10)) continue;
            if (y == 6 && (x < 4 || x > 11)) continue;

            var lip = y == 13 || x == 3 || x == 12;
            if (lip)
            {
                Put(t, x, y, 88, 84, 80, 255);
                continue;
            }

            if (!lit)
            {
                var soot = (int)(Noise(x, y, seed) * 14f);
                Put(t, x, y, Clamp(28 + soot), Clamp(26 + soot), Clamp(25 + soot), 255);
                continue;
            }

            // Hottest at the floor of the opening and cooling upward, with the flicker frozen —
            // an animated tile is one frame here until the sidecar that drives them lands.
            var heat = (y - 6) / 7f;
            var jitter = (Noise(x, y, seed + 61) * 2f - 1f) * 0.18f;
            heat = Math.Clamp(heat + jitter, 0f, 1f);

            Put(t, x, y,
                Clamp((int)(210 + heat * 45f)),
                Clamp((int)(74 + heat * 130f)),
                Clamp((int)(24 + heat * 60f)),
                255);
        }

        return t;
    }

    /// <summary>A length of timber lying corner to corner — the stick everything else is built on.</summary>
    public static byte[] IconStick(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        for (var i = 0; i < 11; i++)
        {
            var x = 3 + i;
            var y = 12 - i;
            for (var w = 0; w < 2; w++)
            {
                var d = (int)((Noise(x, y + w, seed) * 2f - 1f) * 16f) - w * 18;
                Put(t, x, y + w, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
            }
        }

        return t;
    }

    /// <summary>A rounded nugget — coal, a raw metal, a ball of clay.</summary>
    public static byte[] IconLump(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];
        const float Centre = (Size - 1) / 2f;

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var dx = (x - Centre) / 5.4f;
            var dy = (y - Centre) / 4.8f;

            // A wobble on the radius, so a lump is a lump rather than a ball bearing.
            var wobble = 1f + (Noise(x >> 1, y >> 1, seed) * 2f - 1f) * 0.18f;
            if (dx * dx + dy * dy > wobble) continue;

            // Lit from the upper left: the standard for every icon here, so a row of them agrees.
            var lift = (int)((Centre - x) * 1.6f + (Centre - y) * 2.2f);
            var d = lift + (int)((Noise(x, y, seed + 23) * 2f - 1f) * 10f);
            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;
    }

    /// <summary>A cast bar with a bevel — what comes out of a furnace.</summary>
    public static byte[] IconIngot(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        for (var y = 5; y <= 11; y++)
        {
            // Narrower at the top than the bottom, which is the whole silhouette of a cast bar.
            var inset = 3 - (y - 5) / 2;
            for (var x = 2 + inset; x < Size - 2 - inset; x++)
            {
                var top = y <= 6;
                var d = (top ? 34 : 0) - (y >= 10 ? 26 : 0)
                      + (int)((Noise(x, y, seed) * 2f - 1f) * 7f);
                Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
            }
        }

        return t;
    }

    /// <summary>A cut stone with facets — the deep gem, and anything else worth keeping.</summary>
    public static byte[] IconGem(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];
        const float Centre = (Size - 1) / 2f;

        for (var y = 2; y < Size - 2; y++)
        for (var x = 2; x < Size - 2; x++)
        {
            // A rhombus: the taxicab distance from the middle, which is a diamond rather than a disc.
            var reach = MathF.Abs(x - Centre) + MathF.Abs(y - Centre) * 1.15f;
            if (reach > 6.2f) continue;

            // Three facets divided by where the pixel sits, so it has flats rather than a gradient.
            var facet = y < Centre - 1 ? 40 : x < Centre ? 4 : -30;
            var d = facet + (int)((Noise(x, y, seed) * 2f - 1f) * 6f);
            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;
    }

    /// <summary>One fired brick, held rather than laid.</summary>
    public static byte[] IconBrick(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        for (var y = 5; y < 11; y++)
        for (var x = 2; x < 14; x++)
        {
            var edge = y == 5 || y == 10 || x == 2 || x == 13;
            var d = (y == 5 ? 26 : 0) - (y == 10 ? 24 : 0)
                  + (edge ? -10 : 0) + (int)((Noise(x, y, seed) * 2f - 1f) * 9f);
            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;
    }

    /// <summary>The silhouettes tools are drawn from, one row of the tile per string.</summary>
    /// <remarks>
    /// <para>Drawn rather than generated, and deliberately. Speckle and noise make a convincing
    /// material and cannot make a recognisable pickaxe: a tool icon is a shape a player identifies
    /// in a slot the size of a fingernail, and the only honest way to write one is to draw it.</para>
    /// <para>Four shapes and a palette a tier hands in is what keeps this a template rather than
    /// twenty pictures — every tier that is ever added is a row of colours, not a new drawing.</para>
    /// <para><c>h</c> handle, <c>H</c> handle in shadow, <c>m</c> head, <c>M</c> head in shadow,
    /// <c>l</c> the highlight along its lit edge, <c>.</c> nothing.</para>
    /// </remarks>
    public static readonly string[][] ToolShapes =
    [
        // Pickaxe: a swept head across the top, the haft falling away to the left.
        [
            "...m........m...",
            "..mMmm....mmMm..",
            "...mMMmmmmMMm...",
            "....mmllhhmm....",
            ".......hh.......",
            "......hh........",
            "......hh........",
            ".....hh.........",
            ".....hh.........",
            "....hh..........",
            "....hh..........",
            "...hh...........",
            "...hh...........",
            "..hH............",
            "..hH............",
            "................",
        ],

        // Axe: a bit with weight in it, biting to the left of the haft.
        [
            "....mmmm........",
            "...mMlmMm.......",
            "...mMlMMm.......",
            "...mMMMMm.......",
            "....mmMhh.......",
            "......hh........",
            "......hh........",
            ".....hh.........",
            ".....hh.........",
            "....hh..........",
            "....hh..........",
            "...hh...........",
            "...hh...........",
            "..hH............",
            "..hH............",
            "................",
        ],

        // Shovel: a broad blade on a long haft.
        [
            ".....mmmm.......",
            "....mMlMMm......",
            "....mMlMMm......",
            "....mMMMMm......",
            ".....mmMhh......",
            "......hh........",
            "......hh........",
            ".....hh.........",
            ".....hh.........",
            "....hh..........",
            "....hh..........",
            "...hh...........",
            "...hh...........",
            "..hH............",
            "..hH............",
            "................",
        ],

        // Sword: a blade up to the corner, a guard, a grip.
        [
            "..........mmm...",
            ".........mMlm...",
            "........mMlMm...",
            ".......mMlMm....",
            "......mMlMm.....",
            ".....mMlMm......",
            "....mMlMm.......",
            "...mMlMm........",
            "..mMMMm.........",
            ".mHhhhHm........",
            "..HhhH..........",
            "...hh...........",
            "...hh...........",
            "..hHH...........",
            "..HHH...........",
            "................",
        ],
    ];

    /// <summary>One tool: a silhouette from <see cref="ToolShapes"/> in a tier's colours.</summary>
    public static byte[] IconTool(int seed, int shape, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];
        var rows = ToolShapes[shape];

        // The haft is the same timber on every tier. Only the head changes, which is what makes a
        // row of tools read as one family at five materials rather than as five unrelated pictures.
        const byte HandleR = 128, HandleG = 94, HandleB = 56;

        for (var y = 0; y < Size && y < rows.Length; y++)
        for (var x = 0; x < Size && x < rows[y].Length; x++)
        {
            var c = rows[y][x];
            if (c == '.') continue;

            var handle = c is 'h' or 'H';
            var (br, bg, bb) = handle ? (HandleR, HandleG, HandleB) : (r, g, b);

            var d = c switch
            {
                'l' => 46,
                'm' or 'h' => 0,
                _ => -34,      // 'M' and 'H', the shadowed side
            } + (int)((Noise(x, y, seed) * 2f - 1f) * 8f);

            Put(t, x, y, Clamp(br + d), Clamp(bg + d), Clamp(bb + d), 255);
        }

        return t;
    }

    /// <summary>Printable ASCII, from space to tilde. The range every string in the game uses.</summary>
    public const int FirstGlyph = 32;

    public const int GlyphCount = 95;

    /// <summary>Ink width and height of a glyph cell, before it is drawn into a tile.</summary>
    private const int GlyphWidth = 5;
    private const int GlyphHeight = 8;

    /// <summary>
    /// Every printable character, five wide and eight tall, one row per slash.
    /// </summary>
    /// <remarks>
    /// <para>Drawn rather than generated, for the same reason the tool silhouettes are: noise makes
    /// a convincing material and cannot make a legible letter. A font is a drawing, and the only
    /// honest way to write one is to draw it.</para>
    /// <para>The character is repeated at the front of each row rather than the table relying on
    /// ASCII order. One glyph accidentally dropped would otherwise shift every letter after it by
    /// one, which reads as a font that works and spells nothing.</para>
    /// <para>Five by eight is the smallest cell that still tells an <c>8</c> from a <c>B</c>, which
    /// is the pair this size fails on first. The baseline is row six, so <c>g</c>, <c>j</c>,
    /// <c>p</c>, <c>q</c> and <c>y</c> have row seven to hang into.</para>
    /// </remarks>
    private static readonly string[] Glyphs =
    [
        " ...../...../...../...../...../...../...../.....",
        "!..#../..#../..#../..#../..#../...../..#../.....",
        "\".#.#./.#.#./...../...../...../...../...../.....",
        "#.#.#./.#.#./#####/.#.#./#####/.#.#./.#.#./.....",
        "$..#../.####/#.#../.###./..#.#/####./..#../.....",
        "%##..#/##..#/...#./..#../.#.../#..##/#..##/.....",
        "&.##../#..#./#.#../.#.../#.#.#/#..#./.##.#/.....",
        "'..#../..#../...../...../...../...../...../.....",
        "(...#./..#../.#.../.#.../.#.../..#../...#./.....",
        ").#.../..#../...#./...#./...#./..#../.#.../.....",
        "*...../#.#.#/.###./#####/.###./#.#.#/...../.....",
        "+...../..#../..#../#####/..#../..#../...../.....",
        ",...../...../...../...../...../..#../..#../.#...",
        "-...../...../...../#####/...../...../...../.....",
        "....../...../...../...../...../..#../..#../.....",
        "/....#/....#/...#./..#../.#.../#..../#..../.....",
        "0.###./#...#/#..##/#.#.#/##..#/#...#/.###./.....",
        "1..#../.##../..#../..#../..#../..#../.###./.....",
        "2.###./#...#/....#/...#./..#../.#.../#####/.....",
        "3#####/...#./..#../...#./....#/#...#/.###./.....",
        "4...#./..##./.#.#./#..#./#####/...#./...#./.....",
        "5#####/#..../####./....#/....#/#...#/.###./.....",
        "6..##./.#.../#..../####./#...#/#...#/.###./.....",
        "7#####/....#/...#./..#../.#.../.#.../.#.../.....",
        "8.###./#...#/#...#/.###./#...#/#...#/.###./.....",
        "9.###./#...#/#...#/.####/....#/...#./.##../.....",
        ":...../..#../..#../...../..#../..#../...../.....",
        ";...../..#../..#../...../..#../..#../.#.../.....",
        "<...#./..#../.#.../#..../.#.../..#../...#./.....",
        "=...../...../#####/...../#####/...../...../.....",
        ">.#.../..#../...#./....#/...#./..#../.#.../.....",
        "?.###./#...#/....#/...#./..#../...../..#../.....",
        "@.###./#...#/#.###/#.#.#/#.###/#..../.###./.....",
        "A.###./#...#/#...#/#####/#...#/#...#/#...#/.....",
        "B####./#...#/#...#/####./#...#/#...#/####./.....",
        "C.###./#...#/#..../#..../#..../#...#/.###./.....",
        "D###../#..#./#...#/#...#/#...#/#..#./###../.....",
        "E#####/#..../#..../####./#..../#..../#####/.....",
        "F#####/#..../#..../####./#..../#..../#..../.....",
        "G.###./#...#/#..../#.###/#...#/#...#/.###./.....",
        "H#...#/#...#/#...#/#####/#...#/#...#/#...#/.....",
        "I.###./..#../..#../..#../..#../..#../.###./.....",
        "J....#/....#/....#/....#/#...#/#...#/.###./.....",
        "K#...#/#..#./#.#../##.../#.#../#..#./#...#/.....",
        "L#..../#..../#..../#..../#..../#..../#####/.....",
        "M#...#/##.##/#.#.#/#.#.#/#...#/#...#/#...#/.....",
        "N#...#/##..#/#.#.#/#..##/#...#/#...#/#...#/.....",
        "O.###./#...#/#...#/#...#/#...#/#...#/.###./.....",
        "P####./#...#/#...#/####./#..../#..../#..../.....",
        "Q.###./#...#/#...#/#...#/#.#.#/#..#./.##.#/.....",
        "R####./#...#/#...#/####./#.#../#..#./#...#/.....",
        "S.####/#..../#..../.###./....#/....#/####./.....",
        "T#####/..#../..#../..#../..#../..#../..#../.....",
        "U#...#/#...#/#...#/#...#/#...#/#...#/.###./.....",
        "V#...#/#...#/#...#/#...#/#...#/.#.#./..#../.....",
        "W#...#/#...#/#...#/#.#.#/#.#.#/##.##/#...#/.....",
        "X#...#/#...#/.#.#./..#../.#.#./#...#/#...#/.....",
        "Y#...#/#...#/.#.#./..#../..#../..#../..#../.....",
        "Z#####/....#/...#./..#../.#.../#..../#####/.....",
        "[.###./.#.../.#.../.#.../.#.../.#.../.###./.....",
        "\\#..../#..../.#.../..#../...#./....#/....#/.....",
        "].###./...#./...#./...#./...#./...#./.###./.....",
        "^..#../.#.#./#...#/...../...../...../...../.....",
        "_...../...../...../...../...../...../#####/.....",
        "`.#.../..#../...../...../...../...../...../.....",
        "a...../...../.###./....#/.####/#...#/.####/.....",
        "b#..../#..../####./#...#/#...#/#...#/####./.....",
        "c...../...../.####/#..../#..../#..../.####/.....",
        "d....#/....#/.####/#...#/#...#/#...#/.####/.....",
        "e...../...../.###./#...#/#####/#..../.###./.....",
        "f..##./.#..#/.#.../###../.#.../.#.../.#.../.....",
        "g...../...../.####/#...#/#...#/.####/....#/.###.",
        "h#..../#..../####./#...#/#...#/#...#/#...#/.....",
        "i..#../...../.##../..#../..#../..#../.###./.....",
        "j...#./...../..##./...#./...#./...#./#..#./.##..",
        "k#..../#..../#..#./#.#../##.../#.#../#..#./.....",
        "l.##../..#../..#../..#../..#../..#../.###./.....",
        "m...../...../##.#./#.#.#/#.#.#/#...#/#...#/.....",
        "n...../...../####./#...#/#...#/#...#/#...#/.....",
        "o...../...../.###./#...#/#...#/#...#/.###./.....",
        "p...../...../####./#...#/#...#/####./#..../#....",
        "q...../...../.####/#...#/#...#/.####/....#/....#",
        "r...../...../#.##./##..#/#..../#..../#..../.....",
        "s...../...../.####/#..../.###./....#/####./.....",
        "t.#.../.#.../###../.#.../.#.../.#..#/..##./.....",
        "u...../...../#...#/#...#/#...#/#..##/.##.#/.....",
        "v...../...../#...#/#...#/#...#/.#.#./..#../.....",
        "w...../...../#...#/#...#/#.#.#/#.#.#/.#.#./.....",
        "x...../...../#...#/.#.#./..#../.#.#./#...#/.....",
        "y...../...../#...#/#...#/#...#/.####/....#/.###.",
        "z...../...../#####/...#./..#../.#.../#####/.....",
        "{...##/..#../..#../.#.../..#../..#../...##/.....",
        "|..#../..#../..#../..#../..#../..#../..#../.....",
        "}##.../..#../..#../...#./..#../..#../##.../.....",
        "~...../...../.#..#/#.#.#/#..#./...../...../.....",
    ];

    /// <summary>
    /// The whole font as tiles, one glyph per layer, drawn at twice its authored size.
    /// </summary>
    /// <remarks>
    /// One layer per glyph rather than one atlas with texture coordinates, because the overlay
    /// batcher already draws a rectangle with a layer number and nothing else. Ninety-five layers
    /// of sixteen-pixel tile is under a hundred kilobytes, and it costs no new machinery at all.
    /// </remarks>
    public static byte[][] Font()
    {
        var tiles = new byte[GlyphCount][];
        for (var i = 0; i < GlyphCount; i++) tiles[i] = DrawGlyph(i);
        return tiles;
    }

    /// <summary>
    /// How far the pen moves after each glyph, in tile pixels.
    /// </summary>
    /// <remarks>
    /// Measured from the glyph's own ink rather than fixed, because a monospaced <c>i</c> reads as
    /// a terminal and not as a game. A space has no ink at all, so it gets a width of its own.
    /// </remarks>
    public static int[] FontAdvance()
    {
        var advance = new int[GlyphCount];

        for (var i = 0; i < GlyphCount; i++)
        {
            var rows = Glyphs[i][1..].Split('/');
            var widest = 0;

            for (var y = 0; y < rows.Length; y++)
            for (var x = 0; x < rows[y].Length; x++)
                if (rows[y][x] != '.') widest = Math.Max(widest, x + 1);

            // Twice the ink, plus a pixel of air. Empty glyphs are a space, which is narrower than
            // a letter and wider than nothing.
            advance[i] = widest == 0 ? 6 : widest * 2 + 2;
        }

        return advance;
    }

    /// <summary>Which layer a character is on, or -1 for anything the font does not carry.</summary>
    public static int GlyphOf(char c) =>
        c >= FirstGlyph && c < FirstGlyph + GlyphCount ? c - FirstGlyph : -1;

    /// <summary>The character a layer draws, for the check that reads the table back.</summary>
    public static char GlyphChar(int index) => Glyphs[index][0];

    private static byte[] DrawGlyph(int index)
    {
        var tile = new byte[BytesPerTile];
        var rows = Glyphs[index][1..].Split('/');

        for (var y = 0; y < GlyphHeight && y < rows.Length; y++)
        for (var x = 0; x < GlyphWidth && x < rows[y].Length; x++)
        {
            if (rows[y][x] == '.') continue;

            // Twice the size, so a five-by-eight letter fills a sixteen-pixel tile's height and
            // stays crisp when the overlay scales it up.
            for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
                Put(tile, x * 2 + dx, y * 2 + dy, 255, 255, 255, 255);
        }

        return tile;
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
