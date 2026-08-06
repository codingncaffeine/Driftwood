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

    /// <summary>
    /// Water, as a loop of frames: two swells crossing each other and travelling.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>Ours was a speckle, and a speckle does not move.</b> Every pack in the genre ships
    /// water as a strip of thirty-two pictures precisely because still water reads as blue rock, and
    /// a lake that is visibly a lake is most of what a coastline is for. Now that a strip from a pack
    /// can be played, ours has to be a strip too, or importing a pack is the only way to get moving
    /// water in a game whose whole art set is drawn in code.</para>
    /// <para>Two sine swells at different angles and different rates, plus the same dither the rest
    /// of the tiles use. Two rather than one because a single travelling wave reads as a moving
    /// stripe; crossed, they read as a surface. Both wavelengths divide the tile exactly, so the
    /// pattern wraps across a lake with no seam, and the phases close over the loop so the last frame
    /// runs into the first.</para>
    /// </remarks>
    public static byte[][] WaterFrames(int seed, int count, byte r, byte g, byte b)
    {
        var frames = new byte[count][];

        for (var f = 0; f < count; f++)
        {
            var t = new byte[BytesPerTile];
            var phase = f / (float)count * MathF.Tau;

            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                // Whole numbers of waves across the tile, so it still tiles.
                var a = MathF.Sin((x + y) / (float)Size * MathF.Tau * 2f - phase);
                var c = MathF.Sin((x * 2f - y) / (float)Size * MathF.Tau - phase * 2f);

                var swell = (a * 0.6f + c * 0.4f) * 9f;
                var grain = (Noise(x, y, seed) * 2f - 1f) * 5f;

                var d = (int)(swell + grain);
                Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + (int)(d * 1.3f)), 255);
            }

            frames[f] = t;
        }

        return frames;
    }

    /// <summary>
    /// A fluid seen travelling rather than lying still: bands running down the tile.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>A different picture from the still one, not the same picture moving faster</b>, and
    /// every pack in the genre agrees — they all ship <c>_still</c> and <c>_flow</c> as separate
    /// files. Still water is a surface seen from above and its waves cross; flowing water is a sheet
    /// seen edge on and everything in it runs one way. Putting the still tile on a waterfall is the
    /// single most obvious way to make a fluid look wrong, and it costs nothing to be right.</para>
    /// <para>The bands travel along v, which is down every side face the mesher emits, so a fall
    /// reads as falling from any angle without the geometry knowing anything about it. The phase
    /// closes over the loop and the wavelength divides the tile, so a column of it has no seam.</para>
    /// </remarks>
    public static byte[][] FlowFrames(int seed, int count, byte r, byte g, byte b, float contrast)
    {
        var frames = new byte[count][];

        for (var f = 0; f < count; f++)
        {
            var t = new byte[BytesPerTile];
            var phase = f / (float)count * MathF.Tau;

            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                // Two rates so the bands do not read as a barber's pole, and a slow lean across x
                // so the sheet has some body to it rather than being a stack of stripes.
                var a = MathF.Sin(y / (float)Size * MathF.Tau * 2f - phase * 2f);
                var c = MathF.Sin((y * 3f + x) / (float)Size * MathF.Tau - phase * 3f);

                var band = (a * 0.65f + c * 0.35f) * contrast;
                var grain = (Noise(x, y, seed) * 2f - 1f) * (contrast * 0.35f);

                var d = (int)(band + grain);
                Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
            }

            frames[f] = t;
        }

        return frames;
    }

    /// <summary>
    /// Molten rock: a dark crust with the heat showing through the cracks in it, moving slowly.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Not simply water in orange.</b> What makes lava read as lava rather than as juice is that
    /// it is mostly <em>dark</em> — a skin of cooled crust with a bright network under it — and that
    /// it moves at about a fifth of water's rate. A tile that is uniformly bright orange reads as a
    /// flat colour at any distance and takes the eye off everything else in the frame, which matters
    /// more here than in most places because this block also lights the room.
    /// </remarks>
    public static byte[][] LavaFrames(int seed, int count)
    {
        var frames = new byte[count][];

        for (var f = 0; f < count; f++)
        {
            var t = new byte[BytesPerTile];
            var phase = f / (float)count * MathF.Tau;

            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                // Two crossed swells as the water tile has, but read as a THRESHOLD rather than as a
                // brightness: above the line is crust, below it is the glow coming through.
                var a = MathF.Sin((x + y) / (float)Size * MathF.Tau * 2f - phase);
                var c = MathF.Sin((x * 2f - y) / (float)Size * MathF.Tau - phase * 2f);
                var swell = a * 0.6f + c * 0.4f;

                var grain = Noise(x, y, seed) * 2f - 1f;
                var heat = Math.Clamp(swell * 0.5f + 0.5f + grain * 0.18f, 0f, 1f);

                // Crust through ember to the brightest core, so the ramp has three colours in it
                // rather than one fading — molten rock is never a single hue.
                var (r, g, b) = heat < 0.55f
                    ? Mix(58, 22, 12, 148, 46, 16, heat / 0.55f)
                    : Mix(148, 46, 16, 255, 196, 92, (heat - 0.55f) / 0.45f);

                Put(t, x, y, r, g, b, 255);
            }

            frames[f] = t;
        }

        return frames;

        static (byte R, byte G, byte B) Mix(
            int r0, int g0, int b0, int r1, int g1, int b1, float k) =>
            (Clamp((int)float.Lerp(r0, r1, k)),
             Clamp((int)float.Lerp(g0, g1, k)),
             Clamp((int)float.Lerp(b0, b1, k)));
    }

    /// <summary>
    /// A pail: a tapered body with a rim and a handle, optionally full of something.
    /// </summary>
    /// <remarks>
    /// <para>Drawn as a silhouette walked per row rather than as a stack of rectangles, so the taper
    /// is a real slope instead of two steps — a bucket is one of the few item shapes where the outline
    /// is the whole recognisability, and a straight-sided one reads as a tin can.</para>
    /// <para>⚠ The ink stays one square in from the tile's edge, the way every tool here does. A
    /// sprite extruded into the fist wears the square one step in from the edge it stands on, so ink
    /// on the border comes out as a wall of outline running the length of the item.</para>
    /// </remarks>
    public static byte[] IconBucket(int seed, bool filled, byte fr, byte fg, byte fb)
    {
        var t = new byte[BytesPerTile];

        const int Top = 4;
        const int Bottom = 14;
        const byte Metal = 176;

        for (var y = Top; y <= Bottom; y++)
        {
            // Wide at the rim, narrower at the base: one column in over the body's height.
            var k = (y - Top) / (float)(Bottom - Top);
            var half = (int)MathF.Round(float.Lerp(5.5f, 3.5f, k));

            for (var x = 8 - half; x <= 7 + half; x++)
            {
                var edge = x == 8 - half || x == 7 + half || y == Bottom;
                var rim = y <= Top + 1;

                if (edge || rim)
                {
                    // A little grain, so the metal is not a flat plate.
                    var d = (int)((Noise(x, y, seed) * 2f - 1f) * 18f);
                    var shade = x < 8 ? 1.0f : 0.82f;      // lit from the left, like every other icon
                    var v = Clamp((int)((Metal + d) * shade));
                    Put(t, x, y, v, v, Clamp((int)(v * 1.06f)), 255);
                    continue;
                }

                if (!filled)
                {
                    // The dark inside of an empty pail, which is what tells it from a full one at a
                    // glance far more than the rim does.
                    var v = Clamp(58 + (int)((Noise(x, y, seed + 7) * 2f - 1f) * 10f));
                    Put(t, x, y, v, v, Clamp(v + 6), 255);
                    continue;
                }

                var w = (int)((Noise(x, y, seed + 13) * 2f - 1f) * 22f);
                Put(t, x, y, Clamp(fr + w), Clamp(fg + w), Clamp(fb + w), 255);
            }
        }

        // The handle, an arc over the rim.
        for (var x = 3; x <= 12; x++)
        {
            var k = (x - 3) / 9f;
            var y = 3 - (int)MathF.Round(MathF.Sin(k * MathF.PI) * 1.6f);
            if (y < 1) y = 1;
            Put(t, x, y, Metal, Metal, Clamp(Metal + 10), 255);
        }

        return t;
    }

    /// <summary>
    /// A tongue of fire: bright at the base, yellow through orange to a dark tip, and ragged.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>Drawn as a teardrop with a torn edge, not a blob.</b> What reads as fire at a
    /// dozen paces is the <em>silhouette</em> — wide and hot at the bottom, narrowing and cooling,
    /// with the top broken up rather than rounded. A soft round gradient reads as a light bulb.</para>
    /// <para>The tile is a cut-out, so most of it is nothing. It is drawn once and thrown a few
    /// hundred times a second at random crops of itself, which is why the ragged edge matters more
    /// than the interior: every particle shows a quarter of this and the quarters have to differ.</para>
    /// </remarks>
    public static byte[] Flame(int seed)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // 0 at the bottom of the tile, 1 at the top — fire is drawn rising.
            var up = 1f - y / (float)(Size - 1);

            // A teardrop: widest a third of the way up, pinched at both ends.
            var width = MathF.Sin(MathF.Pow(up, 0.7f) * MathF.PI) * 6.6f + 0.6f;
            var dx = MathF.Abs(x - 7.5f);

            // Torn, and torn differently at every height, so two crops never look like each other.
            var tear = (Noise(x, y, seed) * 2f - 1f) * 1.9f;
            if (dx > width + tear) continue;

            // Hot core to cool edge, and cooler the further up it has got.
            var heat = Math.Clamp((1f - dx / MathF.Max(width, 0.5f)) * (1f - up * 0.55f), 0f, 1f);

            var (r, g, b) = heat > 0.62f
                ? (255, (int)float.Lerp(210, 248, (heat - 0.62f) / 0.38f), (int)float.Lerp(90, 190, (heat - 0.62f) / 0.38f))
                : ((int)float.Lerp(168, 255, heat / 0.62f), (int)float.Lerp(48, 210, heat / 0.62f), (int)float.Lerp(16, 90, heat / 0.62f));

            Put(t, x, y, Clamp(r), Clamp(g), Clamp(b), 255);
        }

        return t;
    }

    /// <summary>
    /// A wisp of smoke: a soft grey clump with holes in it.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Holes, not a gradient.</b> A smoke particle is drawn a hundred times over itself as a
    /// plume, so a solid disc stacks into an opaque grey ball; a clump with gaps in it stacks into
    /// something you can see through, which is what smoke is. The colour is deliberately near enough
    /// to neutral that the world's light does the work — a plume in a cave should be lit by the fire
    /// under it, not by a value written here.
    /// </remarks>
    public static byte[] Smoke(int seed)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var dx = (x - 7.5f) / 7.0f;
            var dy = (y - 7.5f) / 7.0f;
            var r2 = dx * dx + dy * dy;
            if (r2 > 1f) continue;

            // Ragged rather than round, and holed through the middle.
            var n = Noise(x, y, seed);
            if (r2 > 0.30f + n * 0.62f) continue;
            if (Noise(x, y, seed + 41) > 0.80f) continue;

            var v = Clamp(150 + (int)((n * 2f - 1f) * 26f));
            Put(t, x, y, v, v, Clamp(v + 4), 255);
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

    /// <summary>
    /// The pointer, drawn as pixels rather than handed to the window manager.
    /// </summary>
    /// <remarks>
    /// <para>The system cursor is the one thing on a screen like this that is not ours: it is
    /// anti-aliased, it is whatever theme the desktop is wearing, and it is drawn at a size that has
    /// nothing to do with the interface under it. Drawing our own means it scales with the rest of
    /// the overlay and lands on the same pixel grid — and it costs one quad, because the batcher
    /// already draws a rectangle with a layer number.</para>
    /// <para>The hotspot is the top left corner, which is where every pointer in this shape has had
    /// it since the first one. The hit test uses that corner and not the middle of the tile.</para>
    /// </remarks>
    public static byte[] Cursor() => FromArt(
    [
        "#...............",
        "##..............",
        "#@#.............",
        "#@@#............",
        "#@@@#...........",
        "#@@@@#..........",
        "#@@@@@#.........",
        "#@@@@@@#........",
        "#@@@@@@@#.......",
        "#@@@@@@@@#......",
        "#@@@@@@@@@#.....",
        "#@@@@@######....",
        "#@@#@@#.........",
        "#@#.#@@#........",
        "##..#@@#........",
        "....####........",
    ]);

    /// <summary>
    /// A soft round bloom: white, fading to nothing at the edge of the tile.
    /// </summary>
    /// <remarks>
    /// ⛳ From the user's own reference sheet for the inventory, where glowstone, a beacon, a
    /// redstone lamp and a lantern each sit in a pool of their own colour. It is what tells a light
    /// apart from a rock in a square the size of a fingernail — the two are the same shape, the same
    /// size and the same three shaded faces, and only one of them is worth carrying into a cave.
    /// <para>Drawn at the tile size rather than as a shader, because the overlay batcher already
    /// draws a rectangle with a layer number and a second pass for one gradient would cost more code
    /// than the gradient is worth. The falloff is squared so the middle stays bright and the edge
    /// gets out of the way of whatever is drawn on top of it.</para>
    /// </remarks>
    public static byte[] Bloom()
    {
        var t = new byte[BytesPerTile];
        const float Centre = (Size - 1) / 2f;

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var dx = (x - Centre) / (Size / 2f);
            var dy = (y - Centre) / (Size / 2f);
            var d = MathF.Sqrt(dx * dx + dy * dy);

            var a = 1f - Math.Clamp(d, 0f, 1f);
            Put(t, x, y, 255, 255, 255, (byte)(a * a * 255f));
        }

        return t;
    }

    /// <summary>
    /// The four worn slots and the other hand, as silhouettes of what belongs in them.
    /// </summary>
    /// <remarks>
    /// Drawn faintly behind an empty slot, in the order of <c>EquipSlot</c>. Four grey squares in a
    /// column say nothing at all; four squares with a helmet, a chestplate, leggings and boots
    /// ghosted into them say what the column is for without a word of text.
    /// </remarks>
    public static byte[][] EquipGhosts() =>
    [
        FromArt(
        [
            "................",
            "................",
            "...@@@@@@@@@@...",
            "..@@@@@@@@@@@@..",
            ".@@@@@@@@@@@@@@.",
            ".@@@@@@@@@@@@@@.",
            ".@@@@@....@@@@@.",
            ".@@@@......@@@@.",
            ".@@@@......@@@@.",
            ".@@@@......@@@@.",
            ".@@@@@@@@@@@@@@.",
            ".@@@@@@@@@@@@@@.",
            ".@@@@@@@@@@@@@@.",
            "..@@@@@@@@@@@@..",
            "................",
            "................",
        ]),
        FromArt(
        [
            "................",
            "................",
            ".@@@........@@@.",
            ".@@@@@@@@@@@@@@.",
            ".@@@@@@@@@@@@@@.",
            "..@@@@@@@@@@@@..",
            "..@@@@@@@@@@@@..",
            "..@@@@@@@@@@@@..",
            "..@@@@@@@@@@@@..",
            "..@@@@@@@@@@@@..",
            "..@@@@....@@@@..",
            "..@@@@....@@@@..",
            "..@@@@....@@@@..",
            "................",
            "................",
            "................",
        ]),
        FromArt(
        [
            "................",
            "................",
            "................",
            "..@@@@@@@@@@@@..",
            "..@@@@@@@@@@@@..",
            "..@@@@@@@@@@@@..",
            "..@@@@@@@@@@@@..",
            "..@@@@@..@@@@@..",
            "..@@@@....@@@@..",
            "..@@@@....@@@@..",
            "..@@@@....@@@@..",
            "..@@@@....@@@@..",
            "..@@@@....@@@@..",
            "................",
            "................",
            "................",
        ]),
        FromArt(
        [
            "................",
            "................",
            "................",
            "................",
            "................",
            "..@@@@....@@@@..",
            "..@@@@....@@@@..",
            "..@@@@....@@@@..",
            "..@@@@....@@@@..",
            "..@@@@@..@@@@@..",
            "..@@@@@@@@@@@@..",
            ".@@@@@@@@@@@@@@.",
            ".@@@@@@@@@@@@@@.",
            "................",
            "................",
            "................",
        ]),
        FromArt(
        [
            "................",
            "................",
            "...@@@@@@@@@@...",
            "...@@@@@@@@@@...",
            "...@@@@@@@@@@...",
            "...@@@@@@@@@@...",
            "...@@@@@@@@@@...",
            "...@@@@@@@@@@...",
            "....@@@@@@@@....",
            "....@@@@@@@@....",
            ".....@@@@@@.....",
            ".....@@@@@@.....",
            "......@@@@......",
            ".......@@.......",
            "................",
            "................",
        ]),
    ];

    /// <summary>
    /// A tile from rows of characters: <c>#</c> is black, <c>@</c> is white, anything else is air.
    /// </summary>
    /// <remarks>
    /// The same idea the font is drawn with, and for the same reason: noise makes a material, not a
    /// shape, and a pointer or a silhouette is a shape somebody has to have decided on. Authored at
    /// the tile's own size, so what is written here is what lands.
    /// </remarks>
    private static byte[] FromArt(string[] rows)
    {
        var tile = new byte[BytesPerTile];

        for (var y = 0; y < Size && y < rows.Length; y++)
        for (var x = 0; x < Size && x < rows[y].Length; x++)
        {
            switch (rows[y][x])
            {
                case '#': Put(tile, x, y, 0, 0, 0, 255); break;
                case '@': Put(tile, x, y, 255, 255, 255, 255); break;
            }
        }

        return tile;
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
    /// <summary>
    /// Rock worked flat: the grain of the speckle knocked back and a soft sheen across it.
    /// </summary>
    /// <remarks>
    /// The whole job of a polished form is to read as <em>the same rock, worked</em>, so it takes
    /// the rough one's own colours and its own seed and quietens them rather than being drawn from
    /// scratch. A polished granite that shares nothing with the granite beside it looks like a
    /// different rock, which is exactly the mistake this axis exists to avoid.
    /// </remarks>
    public static byte[] Polished(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // A third of the roughness the raw rock carries, so the material still reads through.
            var grain = (int)((Noise(x, y, seed) * 2f - 1f) * 6f);

            // And a broad diagonal sheen, which is what a cut face does to light.
            var sheen = (int)(MathF.Sin((x + y) * 0.24f) * 4f);

            var d = grain + sheen;
            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;
    }

    /// <summary>A block cut square: a flat face inside a chiselled border.</summary>
    public static byte[] CutBlock(int seed, byte r, byte g, byte b)
    {
        var t = Polished(seed, r, g, b);

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var edge = x == 0 || y == 0 || x == Size - 1 || y == Size - 1;
            var inner = x == 1 || y == 1 || x == Size - 2 || y == Size - 2;
            if (!edge && !inner) continue;

            // Lit on the inside lip and shaded on the outside, so the border reads as cut rather
            // than as a line drawn round a square.
            var d = edge ? -14 : 10;
            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;
    }

    /// <summary>A worked face carrying a motif: a bordered square with a mark struck into it.</summary>
    /// <remarks>
    /// Ours, and deliberately not anybody else's — a chiselled block is a place a game puts its own
    /// iconography, so this one wears the same lozenge the project's own mark does rather than a
    /// copy of a creature nobody here has drawn.
    /// </remarks>
    public static byte[] Chiselled(int seed, byte r, byte g, byte b)
    {
        var t = CutBlock(seed, r, g, b);
        const float Centre = (Size - 1) / 2f;

        for (var y = 3; y < Size - 3; y++)
        for (var x = 3; x < Size - 3; x++)
        {
            var reach = MathF.Abs(x - Centre) + MathF.Abs(y - Centre);
            if (reach > Size * 0.34f) continue;

            // Cut in, with its upper left lit the way every other bevel in the project is.
            var lit = x - Centre + (y - Centre) < 0f;
            var d = reach > Size * 0.24f ? (lit ? 12 : -16) : -8;
            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;
    }

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

    /// <summary>
    /// A metal cage round a flame, with a ring of chain above it — a lantern.
    /// </summary>
    /// <remarks>
    /// Drawn to fill the tile, because the model puts the whole tile on each of the box's six faces
    /// rather than reading a strip of it. That is the one thing this generator has to know about the
    /// shape it lands on: art in the margins would be art squeezed onto a face nobody sees, and a
    /// gap in the middle would be a hole in a solid lamp.
    /// </remarks>
    public static byte[] LanternTile(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // The two bars top and bottom and the two posts either side are the cage; everything
            // inside it is the flame it is holding.
            var frame = y <= 2 || y >= Size - 3 || x <= 1 || x >= Size - 2;
            var grain = (int)((Noise(x, y, seed) * 2f - 1f) * 12f);

            if (frame)
            {
                // A rivet at each corner of the cage, which is what stops it reading as a box.
                var rivet = (x is 1 or 14) && (y is 1 or 14);
                var d = grain + (rivet ? 34 : 0);
                Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
                continue;
            }

            // Hottest low and in the middle, cooling outward, so it reads as a flame rather than
            // as a coloured pane behind a grille.
            var dx = (x - (Size - 1) / 2f) / 5f;
            var dy = (y - (Size - 2f)) / 9f;
            var heat = Math.Clamp(1f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
            heat = Math.Clamp(heat + (Noise(x, y, seed + 17) * 2f - 1f) * 0.12f, 0f, 1f);

            Put(t, x, y,
                Clamp((int)(150 + heat * 105f)),
                Clamp((int)(110 + heat * 130f)),
                Clamp((int)(52 + heat * 150f)),
                255);
        }

        // The chain, in the two columns at the top the hanging form reads for it.
        for (var y = 0; y < 2; y++)
        for (var x = 7; x < 9; x++)
        {
            var d = (int)((Noise(x, y, seed + 3) * 2f - 1f) * 10f);
            Put(t, x, y, Clamp(r + d - 20), Clamp(g + d - 20), Clamp(b + d - 20), 255);
        }

        return t;
    }

    /// <summary>A sheet of flame, transparent around it — what stands in a campfire.</summary>
    /// <remarks>
    /// Tapering as it rises and eaten into at the edges, so the two crossed planes it is drawn on
    /// read as one fire from any angle rather than as two rectangles meeting at a corner.
    /// </remarks>
    public static byte[] Fire(int seed)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // Wide at the bottom and drawn to a point, measured from the tile's own bottom edge.
            var rise = (Size - 1 - y) / (float)(Size - 1);
            var halfWidth = (1f - rise) * 6.5f + 1.2f;
            var offset = MathF.Abs(x - (Size - 1) / 2f);
            if (offset > halfWidth) continue;

            // Torn edges, so the silhouette is a flame and not a triangle.
            var edge = offset > halfWidth - 1.4f;
            if (edge && Noise(x, y, seed + 29) < 0.42f) continue;

            var heat = Math.Clamp(1f - rise * 0.85f - offset / 9f, 0f, 1f);
            heat = Math.Clamp(heat + (Noise(x, y, seed) * 2f - 1f) * 0.16f, 0f, 1f);

            Put(t, x, y,
                Clamp((int)(214 + heat * 41f)),
                Clamp((int)(96 + heat * 130f)),
                Clamp((int)(34 + heat * 96f)),
                255);
        }

        return t;
    }

    /// <summary>Glass with the light taken out of it: the same pane, smoked and opaque-looking.</summary>
    /// <remarks>
    /// Drawn as a full tile rather than as a frame, unlike <see cref="Glass"/>, because the block is
    /// the one thing in the set that is seen through and not passed through — a nearly empty tile
    /// would say the opposite of what it does.
    /// </remarks>
    public static byte[] Smokeglass(int seed)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var edge = x == 0 || y == 0 || x == Size - 1 || y == Size - 1;
            var streak = y >= 2 && y <= 6 && x - y >= 1 && x - y <= 3;

            var d = (int)((Noise(x, y, seed) * 2f - 1f) * 9f);
            var lift = edge ? 26 : streak ? 18 : 0;

            Put(t, x, y,
                Clamp(48 + d + lift), Clamp(52 + d + lift), Clamp(60 + d + lift),
                edge ? (byte)235 : (byte)206);
        }

        return t;
    }

    /// <summary>A block of set gem, glowing from inside — the brightest thing that can be built.</summary>
    public static byte[] Lamp(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // Cells of set crystal, brightest at their middles, so the face reads as packed rather
            // than as a flat colour with noise on it.
            var cellX = MathF.Abs((x % 4) - 1.5f) / 1.5f;
            var cellY = MathF.Abs((y % 4) - 1.5f) / 1.5f;
            var core = 1f - Math.Clamp(MathF.Max(cellX, cellY), 0f, 1f);

            var d = (int)(core * 46f) + (int)((Noise(x, y, seed) * 2f - 1f) * 8f);
            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;
    }

    /// <summary>Two rails and the rungs between them, everything else empty — a ladder.</summary>
    /// <remarks>
    /// Almost all cut-out, which is what makes a ladder read as something you can see the wall
    /// through rather than as a plank with lines drawn on it.
    /// </remarks>
    public static byte[] Ladder(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var rail = x is 1 or 2 or 13 or 14;
            var rung = y % 4 is 1 or 2 && x >= 1 && x <= 14;
            if (!rail && !rung) continue;

            // Rails a little darker than the rungs, so the two read as separate pieces of wood
            // rather than as one grid.
            var d = (int)((Noise(x, y, seed) * 2f - 1f) * 12f) + (rail ? -14 : 6);
            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;
    }

    /// <summary>A panelled board with a handle, in two halves — the two tiles of a door.</summary>
    /// <param name="upper">True for the half with the window and the handle in it.</param>
    /// <remarks>
    /// Two tiles rather than one used twice, because a door is two blocks tall and one tile read
    /// at both heights puts two handles on it, one at knee height. Every pack paints them the same
    /// way for the same reason.
    /// </remarks>
    public static byte[] Door(int seed, byte r, byte g, byte b, bool upper)
    {
        var t = Planks(seed, r, g, b);

        // The hinge stile down the left and a frame around the board.
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var stile = x <= 2;
            var edge = x == Size - 1 || (upper ? y == 0 : y == Size - 1);
            if (!stile && !edge) continue;

            var i = y * Stride + x * 4;
            var d = stile ? 20 : 26;
            t[i] = Clamp(t[i] - d);
            t[i + 1] = Clamp(t[i + 1] - d);
            t[i + 2] = Clamp(t[i + 2] - d);
        }

        if (!upper) return t;

        // A window in the top half and a handle beside it, which is what says which way up a door
        // goes and which side it opens from at a glance. The pane is a hole rather than painted
        // glass, which is what every pack does and what makes it read as something to see through.
        for (var y = 3; y < 9; y++)
        for (var x = 5; x < 12; x++)
        {
            var frame = y == 3 || y == 8 || x == 5 || x == 11;
            if (frame) Put(t, x, y, Clamp(r - 40), Clamp(g - 40), Clamp(b - 40), 255);
            else Put(t, x, y, 0, 0, 0, 0);
        }

        for (var y = 11; y < 14; y++)
            Put(t, 13, y, 208, 190, 132, 255);

        return t;
    }

    /// <summary>A slatted board with a frame — a trapdoor, seen from above or edge on.</summary>
    public static byte[] Trapdoor(int seed, byte r, byte g, byte b)
    {
        var t = Planks(seed, r, g, b);

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // A batten across each end, and a real gap between the two middle boards — a hole,
            // not a dark line, because a trapdoor is boards nailed together and you can see the
            // room below between them. That is also what the format's own tile does.
            var batten = x is 1 or 2 or 13 or 14;
            var gap = y is 7 or 8;

            if (gap && !batten) { Put(t, x, y, 0, 0, 0, 0); continue; }
            if (!batten) continue;

            var i = y * Stride + x * 4;
            t[i] = Clamp(t[i] + 12);
            t[i + 1] = Clamp(t[i + 1] + 12);
            t[i + 2] = Clamp(t[i + 2] + 12);
        }

        return t;
    }

    /// <summary>
    /// Boards with a band across them and, on the front, a clasp — the faces of a chest.
    /// </summary>
    /// <param name="front">True for the face that wears the lock.</param>
    /// <param name="lid">True for the face seen from above, which has no band on it.</param>
    /// <remarks>
    /// ⚠ The genre draws a chest as an <em>entity</em> — one sheet wrapped round a hinged model, not
    /// six block faces — so an imported pack has nothing at these paths and every chest in the game
    /// keeps ours. Which is why this is drawn as a whole chest rather than as a texture expecting to
    /// be replaced: it is the one we will be looking at.
    /// </remarks>
    public static byte[] ChestFace(int seed, byte r, byte g, byte b, bool front, bool lid)
    {
        var t = Planks(seed, r, g, b);

        // The lid line, three rows down, on every side but the top.
        if (!lid)
            for (var y = 3; y < 5; y++)
            for (var x = 0; x < Size; x++)
            {
                var i = y * Stride + x * 4;
                var d = y == 3 ? 30 : -16;
                t[i] = Clamp(t[i] - d);
                t[i + 1] = Clamp(t[i + 1] - d);
                t[i + 2] = Clamp(t[i + 2] - d);
            }

        // A dark border all the way round, so the box reads as a box at any distance.
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            if (x is not (0 or 15) && y is not (0 or 15)) continue;
            var i = y * Stride + x * 4;
            t[i] = Clamp(t[i] - 34);
            t[i + 1] = Clamp(t[i + 1] - 34);
            t[i + 2] = Clamp(t[i + 2] - 34);
        }

        if (!front) return t;

        // The clasp, straddling the lid line, and a keyhole in it.
        for (var y = 2; y < 8; y++)
        for (var x = 7; x < 10; x++)
            Put(t, x, y, 186, 158, 92, 255);

        Put(t, 8, 5, 62, 50, 30, 255);
        Put(t, 8, 6, 62, 50, 30, 255);

        return t;
    }

    /// <summary>Planks with tools hung on them — the working face of a bench.</summary>
    /// <remarks>
    /// The one face that says which way you stand at it. A bench with the same tile on all four
    /// sides is a crate, which is what ours was until this existed.
    /// </remarks>
    public static byte[] BenchFront(int seed, byte r, byte g, byte b)
    {
        var t = Panel(Planks(seed, r, g, b), 3, 26);

        // A saw hung on the left and a mallet on the right, both dark against the wood.
        for (var y = 5; y < 12; y++) Put(t, 5, y, 92, 92, 98, 255);
        for (var x = 4; x < 8; x++) Put(t, x, 11, 92, 92, 98, 255);

        for (var y = 5; y < 9; y++) Put(t, 11, y, 74, 58, 38, 255);
        for (var x = 10; x < 13; x++) { Put(t, x, 9, 118, 92, 58, 255); Put(t, x, 10, 118, 92, 58, 255); }

        return t;
    }

    /// <summary>A stone bed with a blade standing in it — the top of a stonecutter.</summary>
    public static byte[] StonecutterTop(int seed, byte r, byte g, byte b)
    {
        var t = Speckle(seed, r, g, b, 16, 0.5f);

        // The slot the blade runs in, across the middle, and the blade itself standing proud of it.
        for (var x = 2; x < 14; x++)
        {
            Put(t, x, 7, 54, 54, 58, 255);
            Put(t, x, 8, 54, 54, 58, 255);
        }

        for (var x = 4; x < 12; x++)
        {
            var tooth = x % 2 == 0;
            Put(t, x, 6, 196, 198, 206, 255);
            Put(t, x, 9, tooth ? (byte)212 : (byte)168, tooth ? (byte)214 : (byte)170, 220, 255);
        }

        return t;
    }

    /// <summary>A stone base with a band round it — the side of a stonecutter.</summary>
    public static byte[] StonecutterSide(int seed, byte r, byte g, byte b)
    {
        var t = Speckle(seed + 5, r, g, b, 14, 0.5f);

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // A dark plinth at the bottom and a lighter lip at the top, so it reads as a table
            // rather than as a block of stone that happens to be short.
            var lip = y <= 1;
            var plinth = y >= Size - 3;
            if (!lip && !plinth) continue;

            var i = y * Stride + x * 4;
            var d = lip ? 24 : -22;
            t[i] = Clamp(t[i] + d);
            t[i + 1] = Clamp(t[i + 1] + d);
            t[i + 2] = Clamp(t[i + 2] + d);
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
    /// <param name="slot">True for a straight-sided letterbox rather than an arch.</param>
    /// <remarks>
    /// ⛳ <b>The one difference between a furnace's face and a blast furnace's is a SHAPE, on
    /// purpose.</b> Two stations that do nearly the same thing have to be told apart in a row along
    /// a wall, at a distance, and in a slot sixteen pixels across — a darker grey survives none of
    /// those. An arch is a fire you feed; a letterbox is a machine you load.
    /// </remarks>
    public static byte[] Hearth(int seed, byte[] baseTile, bool lit, bool slot = false)
    {
        var t = (byte[])baseTile.Clone();

        for (var y = 5; y < 14; y++)
        for (var x = 3; x < 13; x++)
        {
            // A rounded top on the opening, so it reads as an arch rather than as a letterbox.
            if (!slot && y == 5 && (x < 5 || x > 10)) continue;
            if (!slot && y == 6 && (x < 4 || x > 11)) continue;

            // And a letterbox is exactly what the other one wants: solid above and below, so the
            // mouth is a band across the middle with a lintel over it.
            if (slot && y is < 7 or > 12) continue;

            var lip = y == 13 || x == 3 || x == 12;
            if (slot) lip = y is 7 or 12 || x == 3 || x == 12;
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

    /// <summary>
    /// A fleece laid as a block: soft, clumped, and lit from nowhere in particular.
    /// </summary>
    /// <remarks>
    /// <para>⚠ <b>Two scales of noise and almost no contrast.</b> Wool is the one material here with
    /// no edges in it at all — no grain, no facets, no mortar — so everything that makes a rock read
    /// as a rock makes wool read as a rock painted white. What it does have is <em>clumping</em>: a
    /// coarse field that gathers the fleece into tufts and a fine one that gives each tuft its fibre.
    /// </para>
    /// <para>⛔ <b>AND THE CURLS, WITHOUT WHICH THIS TILE IS SNOW.</b> The first pass was exactly the
    /// paragraph above and nothing else, and the icon sheet showed what that comes out as: a flat
    /// near-white square, two rows away from snow's own flat near-white square and indistinguishable
    /// from it. Noise cannot fix that at any amplitude, because the difference between wool and
    /// drift is not how rough it is — it is that wool is made of <em>strands</em>. A short dark arc
    /// stamped through each tuft is what finally separates them, and it is one line of code that a
    /// week of tuning the spread would never have found.</para>
    /// </remarks>
    public static byte[] Wool(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // Tufts, on a lattice a quarter the size of the tile, so a clump spans about four
            // pixels — big enough to see against a wall and small enough not to read as a pattern.
            var clump = (Noise(x >> 2, y >> 2, seed) * 2f - 1f) * 18f;
            var fibre = (Noise(x, y, seed + 47) * 2f - 1f) * 8f;

            // ⚠ A fleece has no lit side, so the only shading is the hollows between the tufts —
            // taken from the clump field itself rather than from a direction, which is what stops a
            // wall of it looking like a wall of stone that has been recoloured.
            var hollow = Noise((x + 2) >> 2, (y + 2) >> 2, seed + 91) < 0.34f ? -16 : 0;

            // One curl per tuft, turned one way or the other so a wall of it has no direction in it.
            var cx = (x >> 2) * 4 + 2;
            var cy = (y >> 2) * 4 + 2;
            var mirrored = Noise(x >> 2, y >> 2, seed + 131) > 0.5f;
            var onCurl = mirrored ? x - cx == y - cy : x - cx == cy - y;
            var curl = onCurl && Math.Abs(x - cx) <= 1 ? -30 : 0;

            var d = (int)(clump + fibre) + hollow + curl;
            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;
    }

    /// <summary>
    /// A pinch of dye: a low heap of powder with a few grains loose on top of it.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>One drawing for all sixteen, and it has to be.</b> Sixteen different silhouettes would
    /// be sixteen things to learn where the player is choosing purely by colour — a dye is the one
    /// item in the game whose whole identity is its colour, which is the opposite of the argument
    /// that gave the four tool heads four shapes. ⚠ The heap is lit from the upper left like every
    /// other icon here, so a row of sixteen reads as one family seen in sixteen colours.
    /// </remarks>
    public static byte[] IconDye(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];
        const float Centre = (Size - 1) / 2f;

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // A heap: wide at the bottom, domed on top, sitting in the lower two thirds of the tile.
            var dx = (x - Centre) / 5.8f;
            var dy = (y - 10.4f) / 4.6f;

            var wobble = 1f + (Noise(x >> 1, y >> 1, seed) * 2f - 1f) * 0.14f;
            var inHeap = dx * dx + dy * dy <= wobble && y >= 5;

            // And a few grains above it, placed on the dome's own shoulders so they touch it rather
            // than floating — a picture made of loose specks fails the audit's ink-island count, and
            // rightly: it would read as a spray rather than as a pinch of something.
            var grain = !inHeap && y is >= 4 and <= 6
                        && Math.Abs(x - (int)Centre) <= 3
                        && Noise(x, y, seed + 71) > 0.52f;

            if (!inHeap && !grain) continue;

            var lift = (int)((Centre - x) * 1.3f + (10.4f - y) * 2.4f);
            var speckle = (int)((Noise(x, y, seed + 19) * 2f - 1f) * 12f);

            // ⚠ Dark colours keep their texture because the shading is an OFFSET rather than a
            // scale. A multiply would take black dye to black and leave the heap a flat square.
            var d = lift + speckle + (grain ? 26 : 0);
            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;
    }

    /// <summary>A skein of thread: a loose coil with a tail hanging off it.</summary>
    /// <remarks>
    /// ⚠ <b>A ring rather than a ball.</b> A filled disc of white is a snowball, a pearl or an egg
    /// depending on what else is in the row — the hole in the middle is the only thing that says
    /// this is something wound rather than something solid.
    /// </remarks>
    public static byte[] IconSkein(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];
        const float Centre = (Size - 1) / 2f;

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var dx = (x - Centre) / 5.4f;
            var dy = (y - Centre + 1f) / 4.8f;
            var reach = dx * dx + dy * dy;

            // The coil, and the tail that runs down out of it. The tail touches the ring, so the
            // whole icon is one piece of ink — which is what the audit's island count asks for.
            var onRing = reach is <= 1f and >= 0.30f;
            var onTail = x is >= 7 and <= 8 && y >= (int)Centre && y <= 14;

            if (!onRing && !onTail) continue;

            // ⚠ Banded along the coil rather than speckled. Thread is wound, so what reads at this
            // size is the winding — a noise field would make it wool, which is a different item.
            var wind = (x + y * 2) % 4 switch { 0 => 20, 1 => 4, 2 => -14, _ => -30 };
            var lift = (int)((Centre - x) * 1.1f + (Centre - y) * 1.4f);

            var d = wind + lift + (int)((Noise(x, y, seed) * 2f - 1f) * 5f);
            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;
    }

    /// <summary>A bone: a shaft on the diagonal with a knob at each end.</summary>
    /// <remarks>
    /// ⚠ <b>Two lumps per end, offset across the shaft.</b> One circle apiece comes out a cotton
    /// bud; it is the pair that says the end of a bone. Drawn against distances rather than stepped
    /// along the diagonal, for the reason the feather and the shears both had to learn.
    /// </remarks>
    public static byte[] IconBone(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        const float X0 = 3.5f, Y0 = 12.5f, X1 = 12.5f, Y1 = 3.5f;
        const float Across = 1.6f;

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var shaft = ToSegment(x, y, X0, Y0, X1, Y1) - 1.3f;

            var ends = MathF.Min(
                MathF.Min(Blob(x, y, X0 + Across, Y0 + Across), Blob(x, y, X0 - Across, Y0 - Across)),
                MathF.Min(Blob(x, y, X1 + Across, Y1 + Across), Blob(x, y, X1 - Across, Y1 - Across)));

            var nearest = MathF.Min(shaft, ends);
            if (nearest > 0f) continue;

            var d = (int)(nearest * 22f) + (int)((Noise(x, y, seed) * 2f - 1f) * 6f);
            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;

        static float Blob(int x, int y, float cx, float cy) =>
            MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - 1.9f;
    }

    /// <summary>A cured hide: a rounded piece with a darker edge and a couple of creases.</summary>
    public static byte[] IconLeather(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];
        const float Centre = (Size - 1) / 2f;

        for (var y = 2; y < Size - 2; y++)
        for (var x = 1; x < Size - 1; x++)
        {
            // Wider than it is tall, and wobbling, so it reads as something cut off an animal
            // rather than as a rounded rectangle.
            var dx = (x - Centre) / 6.6f;
            var dy = (y - Centre) / 5.4f;
            var wobble = 1f + (Noise(x >> 1, y >> 1, seed) * 2f - 1f) * 0.22f;
            if (dx * dx + dy * dy > wobble) continue;

            var edge = dx * dx + dy * dy > wobble * 0.62f;
            var crease = (x + y * 2) % 7 == 0 && !edge ? -12 : 0;

            var d = (edge ? -26 : 0) + crease
                  + (int)((Centre - y) * 1.5f)
                  + (int)((Noise(x, y, seed + 13) * 2f - 1f) * 8f);

            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;
    }

    /// <summary>A quill lying corner to corner: a shaft with a vane either side of it.</summary>
    /// <remarks>
    /// <para>⚠ <b>The vane widens from the quill and tapers to the tip</b>, which is the whole
    /// silhouette. Drawn at a constant width it is a leaf; tapered from one end only it is a knife.
    /// </para>
    /// <para>⛔ <b>IT CAME OUT A CHECKERBOARD, AND THE CAUSE IS GEOMETRY, NOT SHADING.</b> The first
    /// two passes walked the shaft in steps of <c>(+1, −1)</c> and filled across it in steps of
    /// <c>(+1, +1)</c> — genuinely perpendicular, and the reason it fails anyway is worth keeping.
    /// Every cell those two reach has <c>x + y = 15 + 2w</c>, which is <em>always odd</em>: the two
    /// diagonals together only ever address one parity class of the grid, so half the pixels of the
    /// vane can never be painted whatever the widths say. It rendered as a dither field with no
    /// feather in it. The first diagnosis blamed the barb rule and was wrong — <b>a shape walked
    /// along one diagonal and filled along another covers half a grid, and no amount of adjusting
    /// what is drawn at each step can reach the other half.</b> It is filled per pixel now: every
    /// cell in the tile works out where it falls along the shaft and how far across, which is the
    /// same shape as the shape test in <see cref="IconMeat"/> and cannot skip anything.</para>
    /// </remarks>
    public static byte[] IconFeather(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        // The quill end, and the length and direction of the shaft from it.
        const float OriginX = 2f, OriginY = 13f;
        const float Length = 12f;

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // Where this pixel falls in the shaft's own frame: how far up it, and how far across.
            // The two axes are (1,−1) and (1,1) over root two, and the halves are that root two
            // squared — written out rather than normalised, because the numbers below are in pixels.
            var dx = x - OriginX;
            var dy = y - OriginY;

            var along = (dx - dy) * 0.5f;
            var across = (dx + dy) * 0.5f;

            if (along is < -1f or > Length) continue;

            var t01 = Math.Clamp(along / Length, 0f, 1f);

            // Fattest a third of the way up from the quill, tapering to nothing at the tip. Below
            // zero along is the bare stub, which carries no vane at all.
            var span = along < 0f ? 0f : 3.4f * MathF.Sin(t01 * MathF.PI) * (0.5f + t01 * 0.7f);

            // ⚠ Barbs: the outermost half-pixel of alternate rungs, on one side only. Alternating
            // and one-sided, so the edge reads as separated barbs rather than as a serrated blade.
            var rung = (int)MathF.Round(along);
            if (span > 1.2f && rung % 2 == 1 && across > 0f) span -= 0.9f;

            var reach = MathF.Abs(across);
            if (reach > span && !(along >= -1f && reach < 0.6f)) continue;

            // The shaft itself, a shade darker, so the two halves of the vane read as two halves
            // rather than as one blob.
            if (reach < 0.6f)
            {
                Put(t, x, y, Clamp(r - 46), Clamp(g - 42), Clamp(b - 34), 255);
                continue;
            }

            var shade = (int)((Noise(x, y, seed) * 2f - 1f) * 6f) - (int)(reach * 7f);
            Put(t, x, y, Clamp(r + shade), Clamp(g + shade), Clamp(b + shade), 255);
        }

        return t;
    }

    /// <summary>An egg: an ovoid, narrower at the top, freckled.</summary>
    public static byte[] IconEgg(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];
        const float Centre = (Size - 1) / 2f;

        for (var y = 2; y < Size - 2; y++)
        for (var x = 3; x < Size - 3; x++)
        {
            // ⚠ The horizontal radius grows down the egg, which is what makes it an egg rather than
            // an ellipse. Symmetric top to bottom it is a pill, and every pack draws it pointed.
            var down = (y - 2) / 11f;
            var radius = 3.1f + down * 1.5f;

            var dx = (x - Centre) / radius;
            var dy = (y - Centre) / 6.2f;
            if (dx * dx + dy * dy > 1f) continue;

            var freckle = Noise(x, y, seed + 5) < 0.16f ? -30 : 0;
            var d = (int)((Centre - x) * 1.4f + (Centre - y) * 1.8f) + freckle
                  + (int)((Noise(x, y, seed) * 2f - 1f) * 5f);

            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return t;
    }

    /// <summary>
    /// Shears: two blades crossed on a pivot, the one tool that takes something off a live animal.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>An X, and it has to be.</b> Every other tool in the game hangs off a diagonal haft, so a
    /// pair of shears drawn as one more diagonal would be a fifth thing in a row of four that all
    /// look alike. Two strokes crossing is the one silhouette nothing else here has.
    /// </remarks>
    /// <remarks>
    /// ⛔ <b>Drawn per pixel against four line segments, and the first pass was not.</b> Stepping a
    /// diagonal and thickening it by a second offset in the same direction gives a line one pixel
    /// wide with a gap at every step — the audit counted <b>23 disconnected pieces of ink</b> where a
    /// drawing should be one or two. The same fault the feather had, found by the same check, and it
    /// is why that check now runs over every icon: a 45° line is not four-connected however many
    /// pixels are written along it, so the width has to be a <em>distance</em> rather than a step.
    /// </remarks>
    public static byte[] IconShears(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        // Blades in a narrow V up from the pivot, handles in a wider V down from it: which is what
        // a pair of shears is, and the one silhouette nothing else in the set has.
        (float X0, float Y0, float X1, float Y1, float Width, bool Metal)[] strokes =
        [
            (8.0f, 9.0f, 3.2f, 2.2f, 1.5f, true),
            (8.0f, 9.0f, 12.8f, 2.2f, 1.5f, true),
            (8.0f, 9.0f, 4.4f, 14.2f, 1.2f, false),
            (8.0f, 9.0f, 11.6f, 14.2f, 1.2f, false),
        ];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var nearest = float.MaxValue;
            var metal = true;

            foreach (var (x0, y0, x1, y1, width, isMetal) in strokes)
            {
                var reach = ToSegment(x, y, x0, y0, x1, y1) - width * 0.5f;
                if (reach >= nearest) continue;

                nearest = reach;
                metal = isMetal;
            }

            if (nearest > 0f) continue;

            var (br, bg, bb) = metal ? (r, g, b) : ((byte)118, (byte)78, (byte)56);

            // Lit along the middle of each stroke and shaded at its edges, so two crossing bars read
            // as two bars rather than as a solid X.
            var lift = (int)(nearest * 26f);
            var d = lift + (int)((Noise(x, y, seed) * 2f - 1f) * 7f);

            Put(t, x, y, Clamp(br + d), Clamp(bg + d), Clamp(bb + d), 255);
        }

        // The pivot, dark, so the four strokes visibly hinge rather than merely overlap.
        for (var y = 8; y <= 9; y++)
        for (var x = 7; x <= 8; x++)
            Put(t, x, y, Clamp(r - 62), Clamp(g - 58), Clamp(b - 52), 255);

        return t;
    }

    /// <summary>How far a point is from a line segment. The one primitive a stroke needs.</summary>
    private static float ToSegment(float px, float py, float x0, float y0, float x1, float y1)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;

        var lengthSquared = dx * dx + dy * dy;
        var along = lengthSquared > 1e-6f
            ? Math.Clamp(((px - x0) * dx + (py - y0) * dy) / lengthSquared, 0f, 1f)
            : 0f;

        var ax = px - (x0 + dx * along);
        var ay = py - (y0 + dy * along);

        return MathF.Sqrt(ax * ax + ay * ay);
    }

    /// <summary>Which drawing a meat wears. Three, so eight meats are told apart in a slot.</summary>
    public enum MeatShape
    {
        /// <summary>A slab off the flank — beef and mutton.</summary>
        Cut,

        /// <summary>A cut with the bone along its top edge — pork.</summary>
        Chop,

        /// <summary>A drumstick: a bulb on a bone — poultry.</summary>
        Leg,
    }

    /// <summary>
    /// One piece of meat, raw or cooked.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Cooked is not the raw palette darkened.</b> Meat browns from the outside in, so what
    /// changes is the <em>rim</em> and the sear across it, and the middle stays close to what it was.
    /// A whole tile shifted brown reads as a different, dirtier animal rather than as the same one
    /// off a fire — which is the failure that made the first pass of these unreadable side by side.
    /// </remarks>
    public static byte[] IconMeat(int seed, byte r, byte g, byte b, MeatShape shape, bool cooked)
    {
        var t = new byte[BytesPerTile];
        const float Centre = (Size - 1) / 2f;

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            bool inside;
            var bone = false;

            switch (shape)
            {
                case MeatShape.Leg:
                {
                    // A bulb low and left, and a bone running up to the right out of it.
                    var dx = (x - 6.2f) / 4.6f;
                    var dy = (y - 10.0f) / 4.4f;
                    var wobble = 1f + (Noise(x >> 1, y >> 1, seed) * 2f - 1f) * 0.16f;
                    inside = dx * dx + dy * dy <= wobble;

                    bone = !inside && MathF.Abs((x - 6) - (5 - y)) <= 1 && x is >= 6 and <= 13;
                    break;
                }

                case MeatShape.Chop:
                {
                    var dx = (x - Centre) / 6.2f;
                    var dy = (y - 9.2f) / 4.2f;
                    var wobble = 1f + (Noise(x >> 1, y >> 1, seed) * 2f - 1f) * 0.20f;
                    inside = dx * dx + dy * dy <= wobble;

                    // ⛔ The bone along the top edge, ATTACHED to the cut rather than floating over
                    // it. Drawn as its own ellipse two rows clear, it came out on the icon sheet as a
                    // white bar sitting above the meat — which reads as a lid on a pot, not as a
                    // chop. It sits ON the meat's own upper edge now and takes the pixels there.
                    bone = inside && y <= 6.2f - 3.4f * MathF.Abs(x - Centre) / 7f;
                    if (bone) inside = false;
                    break;
                }

                default:
                {
                    var dx = (x - Centre) / 6.0f;
                    var dy = (y - Centre) / 4.6f;
                    var wobble = 1f + (Noise(x >> 1, y >> 1, seed) * 2f - 1f) * 0.24f;
                    inside = dx * dx + dy * dy <= wobble;
                    break;
                }
            }

            if (bone)
            {
                var grain = (int)((Noise(x, y, seed + 3) * 2f - 1f) * 7f);
                Put(t, x, y, Clamp(228 + grain), Clamp(222 + grain), Clamp(202 + grain), 255);
                continue;
            }

            if (!inside) continue;

            // How near the edge, taken from whether the neighbours are inside too — one measure that
            // works for all three shapes rather than three copies of a distance formula.
            var rim = Rim(x, y, shape, seed);

            var (pr, pg, pb) = (r, g, b);

            // ⛔ COOKED BROWNS THROUGHOUT, NOT ONLY AT THE RIM — and the first pass had it the other
            // way round on a theory about how meat actually cooks. It does brown from the outside in,
            // but a rim on a sixteen-pixel icon is one pixel wide: on the icon sheet raw beef and
            // cooked beef were the same red blob, side by side, and nobody could have told them
            // apart in a slot. The rim still browns hardest, which keeps the from-the-outside
            // reading; the middle goes most of the way with it, which is what makes them two things.
            if (cooked)
            {
                var deep = rim ? 1f : 0.62f;
                pr = (byte)Math.Clamp(r + (126 - r) * deep, 0, 255);
                pg = (byte)Math.Clamp(g + (74 - g) * deep, 0, 255);
                pb = (byte)Math.Clamp(b + (40 - b) * deep, 0, 255);
            }

            // Marbling: a few pale streaks through the middle, which is what says meat rather than
            // clay at this size.
            var marble = !rim && Noise(x, y * 2, seed + 29) > 0.80f ? 34 : 0;

            // A sear stripe or two once it has been on a fire.
            var sear = cooked && !rim && (x + y) % 5 == 0 ? -30 : 0;

            var lift = (int)((Centre - x) * 1.1f + (Centre - y) * 1.6f);
            var d = lift + marble + sear + (int)((Noise(x, y, seed + 17) * 2f - 1f) * 9f);

            Put(t, x, y, Clamp(pr + d), Clamp(pg + d), Clamp(pb + d), 255);
        }

        return t;
    }

    /// <summary>True when this pixel of a meat shape has a neighbour outside it.</summary>
    private static bool Rim(int x, int y, MeatShape shape, int seed)
    {
        for (var i = 0; i < 4; i++)
        {
            var nx = x + (i == 0 ? -1 : i == 1 ? 1 : 0);
            var ny = y + (i == 2 ? -1 : i == 3 ? 1 : 0);
            if (nx is < 0 or >= Size || ny is < 0 or >= Size) return true;
            if (!MeatInside(nx, ny, shape, seed)) return true;
        }

        return false;
    }

    /// <summary>The shape test on its own, so the rim can ask it of a neighbour.</summary>
    private static bool MeatInside(int x, int y, MeatShape shape, int seed)
    {
        const float Centre = (Size - 1) / 2f;
        var wobble = 1f + (Noise(x >> 1, y >> 1, seed) * 2f - 1f) * (shape == MeatShape.Cut ? 0.24f : shape == MeatShape.Chop ? 0.20f : 0.16f);

        return shape switch
        {
            MeatShape.Leg => Sq((x - 6.2f) / 4.6f) + Sq((y - 10.0f) / 4.4f) <= wobble,
            MeatShape.Chop => Sq((x - Centre) / 6.2f) + Sq((y - 9.2f) / 4.2f) <= wobble,
            _ => Sq((x - Centre) / 6.0f) + Sq((y - Centre) / 4.6f) <= wobble,
        };
    }

    private static float Sq(float v) => v * v;

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
    /// <c>l</c> the highlight along its lit edge, <c>o</c> the dark line round the outside,
    /// <c>.</c> nothing.</para>
    /// <para>⛔ <b>REDRAWN 2026-08-05, and the reason is worth keeping.</b> The user asked whether
    /// these had been modelled on a real pack's tools. They had not — every other visual decision in
    /// this project was measured against one (the panel grid off <c>inventory.png</c> pixel by
    /// pixel, the six texture projections face for face against the format's own defaults) and these
    /// were drawn from imagination. Then they spotted the proof: <b>the axe and the shovel were the
    /// same shape.</b> Shifted one column, the two silhouettes differed by a single character.</para>
    /// <para>What a real pack does, measured off four of its wooden tools rather than remembered:
    /// <b>ten shades</b> where we had three, a <b>one-pixel dark line all the way round</b>, the
    /// <b>whole tile used corner to corner</b> where ours sat in the left twelve columns, and — the
    /// one that matters — <b>three head shapes that are nothing like each other</b>: a pickaxe is a
    /// wide swept bar with its tips turned down, an axe is a tall wedge hanging off one side of the
    /// haft, and a shovel is a small scoop, visibly smaller than either. The shapes below are ours;
    /// the conventions are the genre's.</para>
    /// </remarks>
    public static readonly string[][] ToolShapes =
    [
        // ⛳ SAMPLED FROM THE PROJECT'S OWN REFERENCE SHEET, not drawn here. Six hand-drawn passes
        // traded one fault for another — a pickaxe that read as a bent pipe, an axe as a lollipop, a
        // shovel as a stub — because 16-pixel art is a skill and guessing at it in a text file is
        // not one. The sheet is ours, so the shapes come straight off it: the metal-headed variant
        // of each tool, so head and haft separate by colour rather than by my judgement, downsampled
        // to sixteen squares and classified by brightness into the letters below.
        //
        // ⚠ Regenerate with the same method if the sheet changes — the extraction reads the STONE
        // pickaxe, the IRON axe, the IRON shovel and the STONE sword, because on a wooden tool the
        // head and the haft are the same timber and nothing can tell them apart.

        // Pickaxe: a bar across the top, its right end turning down, the haft off its middle.
        [
            "................",
            "......mmmmm.....",
            ".....Mmmmmmm.HH.",
            ".........MmmHH..",
            "..........mmmM..",
            ".........h..mmm.",
            "........Hh...mm.",
            ".......MH.....m.",
            ".......H......m.",
            ".....HhM......m.",
            "....HH..........",
            "...HH...........",
            "...H............",
            ".m..............",
            ".m..............",
            "................",
        ],

        // Axe: a broad blade with its edge on the left, the haft passing behind it.
        [
            "................",
            "........mllm....",
            "......Mmlllm....",
            "......mlllll....",
            ".....mllllllh...",
            "......mlllllm...",
            ".......M.mlmllM.",
            "........hH.lllM.",
            ".......hh..lll..",
            "......hh...MM...",
            ".....h..........",
            "...Hhh..........",
            "...h............",
            ".hh.............",
            ".hh.............",
            "................",
        ],

        // Shovel: a spade blade, wide and angular, on a long shaft.
        [
            "................",
            "...........Mmmm.",
            "..........mmlml.",
            ".........Mlmmml.",
            "........mmmmmml.",
            ".........mmmmlm.",
            ".........H.mlm..",
            "........hH.ml...",
            ".......Hh.......",
            "......Hh........",
            ".....Hh.........",
            "....HH..........",
            "...hH...........",
            ".Hh.............",
            "..h.............",
            "................",
        ],

        // Sword: a blade the whole diagonal, a cross-guard over it, a short grip and a pommel.
        [
            "................",
            "............mlm.",
            "...........mlmm.",
            "..........mmmmm.",
            ".........mmmmm..",
            "........mmmmm...",
            "........mmmm....",
            "...m..mmmmm.....",
            "...MmmmmmM......",
            "....MmmmM.......",
            "....MMM.........",
            "....MMMM........",
            "..mM............",
            ".MMM............",
            ".m..............",
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

            // ⚠ The outline takes no noise. It is the line that holds the shape together against
            // whatever is behind it, and a line that varies pixel to pixel is a frayed edge rather
            // than an outline — the one place a speckle actively hurts.
            if (c == 'o')
            {
                Put(t, x, y, Clamp(br - 92), Clamp(bg - 74), Clamp(bb - 46), 255);
                continue;
            }

            // ⛔ A GRADIENT ACROSS THE TOOL WAS TRIED HERE AND TAKEN BACK OUT. The reason is worth
            // more than the code was. It went in to fix "ours are flat beside a real pack's" — three
            // tones against their ten shades — and BOTH HALVES OF THAT WERE WRONG. Measured: the
            // dither already produces twenty distinct colours, so counting shades says nothing about
            // shading; and the tone already travels 57 levels from its tenth percentile to its
            // ninetieth where the pack travels 59 to 73. There was no flatness to fix. The gradient
            // moved that to 55 — very slightly worse — so it was a change justified by a claim that
            // measurement refuted, and it went back out. What was genuinely wrong was the DRAWINGS.
            var d = c switch
            {
                'l' => 46,
                'm' or 'h' => 0,
                _ => -34,      // 'M' and 'H', the shadowed side
            } + (int)((Noise(x, y, seed) * 2f - 1f) * 8f);

            // ⛳ THE HAFT IS KNURLED, and it is the thing a flat bar of timber most obviously is
            // not. Every tool in the reference art has light and dark banding running along its
            // shaft — it is what reads as grain at this size, and ours was one flat colour. The
            // haft is drawn on the diagonal, so (x + y) runs along it and its remainder is a band
            // across it.
            if (handle) d += (x + y) % 3 switch { 0 => 18, 1 => 0, _ => -16 };

            Put(t, x, y, Clamp(br + d), Clamp(bg + d), Clamp(bb + d), 255);
        }

        // ⛳ THE DARK EDGE, GROWN ROUND WHATEVER WAS DRAWN RATHER THAN DRAWN IN. Every tool in the
        // reference art carries a one-pixel line all the way round, and it is what holds a shape
        // together against whatever is behind it — without one a tool floats. Written as a pass
        // rather than as pixels in the drawings for two reasons: a hand-drawn outline is a hand-
        // drawn mistake on a shape that is otherwise sampled, and this way a shape sampled from the
        // sheet gets its edge for free, including the sword's guard and the gaps down a knurled
        // haft. Taken from what is already there, so the tile it wraps is the tile it belongs to.
        var edged = (byte[])t.Clone();

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            if (t[(y * Size + x) * 4 + 3] >= 128) continue;

            var touches = false;
            for (var side = 0; side < 4 && !touches; side++)
            {
                var nx = x + (side == 0 ? -1 : side == 1 ? 1 : 0);
                var ny = y + (side == 2 ? -1 : side == 3 ? 1 : 0);
                if (nx < 0 || ny < 0 || nx >= Size || ny >= Size) continue;
                touches = t[(ny * Size + nx) * 4 + 3] >= 128;
            }

            if (touches) Put(edged, x, y, 30, 24, 18, 255);
        }

        return edged;
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
