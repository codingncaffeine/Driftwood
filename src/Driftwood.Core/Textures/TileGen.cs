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
    /// A sheet of parchment rolled at both ends: paper.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>At sixteen pixels a flat rectangle of tan is a PLANK</b>, and that is the whole
    /// difficulty of this drawing. Plank is one of the commonest things in the game, it is the same
    /// family of browns, and it sits in the same bar. The curls at top and bottom are therefore not
    /// decoration — they are the entire read, and they are drawn <em>wider than the sheet</em> so the
    /// silhouette alone says scroll before a single interior pixel is looked at.</para>
    /// <para>Three rows per roll rather than one: a tube needs a curve away, a highlight along its
    /// top, and a shaded underside, and one row of a lighter colour reads as a stripe. The ends are
    /// darkened because the cut face of a rolled sheet is what stops the two rolls reading as a pair
    /// of bands drawn across a rectangle.</para>
    /// <para>⚠ The ink stays one square in from the tile's edge, as everything held does — a sprite
    /// extruded into the fist wears the square one step in from the edge it stands on.</para>
    /// </remarks>
    public static byte[] IconScroll(int seed)
    {
        var t = new byte[BytesPerTile];

        const int SheetLeft = 4;
        const int SheetRight = 11;
        const int SheetTop = 4;
        const int SheetBottom = 11;

        const int RollLeft = 2;
        const int RollRight = 13;

        // Parchment, lit from the left like every other icon here.
        const int R = 214, G = 196, B = 158;

        for (var y = SheetTop; y <= SheetBottom; y++)
        for (var x = SheetLeft; x <= SheetRight; x++)
        {
            var d = (int)((Noise(x, y, seed) * 2f - 1f) * 7f);

            if (x == SheetLeft) d += 20;            // the lit edge
            else if (x == SheetRight) d -= 30;      // and the one turned away
            if (y == SheetTop) d -= 24;             // in the shadow of the roll above it

            Put(t, x, y, Clamp(R + d), Clamp(G + d), Clamp(B + d), 255);
        }

        // Curve away, highlight, shaded underside — and the bottom roll is lit from above too, so
        // its dark row is the one the sheet casts onto rather than the one furthest from the light.
        int[] top = [-24, 26, -12];
        int[] bottom = [-12, 26, -30];

        for (var roll = 0; roll < 2; roll++)
        {
            var first = roll == 0 ? 1 : 12;
            var tones = roll == 0 ? top : bottom;

            for (var row = 0; row < 3; row++)
            for (var x = RollLeft; x <= RollRight; x++)
            {
                var y = first + row;
                var d = tones[row] + (int)((Noise(x, y, seed + 53) * 2f - 1f) * 6f);

                if (x == RollLeft || x == RollRight) d -= 34;
                else if (x == RollLeft + 1) d += 10;

                Put(t, x, y, Clamp(R + d), Clamp(G + d), Clamp(B + d), 255);
            }
        }

        return t;
    }

    /// <summary>
    /// Ground turned over: furrows cut across it, with the earth heaped between them.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>This was <see cref="Scored"/> over dirt and it read as PLANKS.</b> Scored draws even
    /// bands the full width of the tile, which is exactly what a floorboard is — a field of it
    /// looked like somebody had decked their garden. Caught by <c>--icon-sheet</c>, which is the
    /// third time that instrument has found a drawing nobody would have questioned in the game.
    /// <para>⛳ What separates a furrow from a plank is that a furrow is <b>uneven and broken</b>: the
    /// groove wanders, the ridge either side of it is lit, and clods of earth sit across it. A
    /// straight dark line with a straight light line under it is joinery.</para>
    /// </remarks>
    public static byte[] Tilled(int seed, byte r, byte g, byte b)
    {
        var t = Speckle(seed, r, g, b, 16, 0.5f);

        // ⛔ TWICE WRONG BEFORE THIS, AND BOTH TIMES IT WAS TIMBER. Horizontal grooves with a lit lip
        // under each is a floorboard; vertical grooves at full height and full contrast is the same
        // board stood on end. Going and LOOKING at the reference settled it in one glance and both
        // guesses had missed the same thing: its tile is mostly MOTTLE. The seams are faint, they do
        // not run the whole way, and what actually says "sown" is four dark holes.
        for (var furrow = 0; furrow < 4; furrow++)
        {
            var at = 1 + furrow * 4;

            for (var y = 0; y < Size; y++)
            {
                // Broken, not continuous: about a third of each seam is missing, which is the whole
                // difference between a furrow in earth and a join between two planks.
                if (Noise(y, furrow, seed + 5) > 0.68f) continue;

                var x = at + (int)MathF.Round((Noise(y, furrow, seed) * 2f - 1f) * 0.6f);
                if (x < 0 || x >= Size) continue;

                // ⚠ A sixth of the contrast the first two versions used. At −40 it is a line drawn
                // on the tile; at −7 it is a shadow in it.
                Put(t, x, y, Clamp(r - 7), Clamp(g - 6), Clamp(b - 5), 255);
            }
        }

        // ⛳ The holes seed goes into, and they carry the whole read. Two by two, dark and square,
        // the one thing in the reference's tile that could not be mistaken for a material.
        for (var i = 0; i < 4; i++)
        {
            var x = 3 + (i % 2) * 8;
            var y = 4 + (i / 2) * 7;

            if (x >= Size - 1 || y >= Size - 1) continue;

            Put(t, x, y, Clamp(r - 58), Clamp(g - 52), Clamp(b - 44), 255);
            Put(t, x + 1, y, Clamp(r - 50), Clamp(g - 45), Clamp(b - 38), 255);
            Put(t, x, y + 1, Clamp(r - 50), Clamp(g - 45), Clamp(b - 38), 255);
            Put(t, x + 1, y + 1, Clamp(r - 44), Clamp(g - 40), Clamp(b - 34), 255);
        }

        return t;
    }

    /// <summary>
    /// A heap of powder: one lumpy connected pile, lit from above.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>CONNECTED, and that is the reference's own answer rather than a guess.</b> Bone meal
    /// went in as a scatter beside the seeds, on the reasoning that both are loose stuff in a
    /// pocket — and the reference draws seeds as nine separate grains and bone meal as a single
    /// lump. It is right: seed corn is counted and meal is poured. Looking at the real tile settled
    /// in one glance what two attempts at reasoning had got wrong.
    /// </remarks>
    /// <summary>
    /// One root vegetable in a pocket: the crop itself, with a tuft of its own tops on the shoulder.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Tapered or round is the whole of what separates the three.</b> A carrot and a beetroot
    /// are cones and a potato is a lump, so the shape is a parameter and the colour does the rest —
    /// which keeps three icons legible at sixteen pixels without three bespoke drawings.
    /// <para>⚠ <b>The tops are not decoration.</b> Without them a beetroot is a red blob and a lump
    /// of raw meat is a red blob; the green flash at the top is what says "this came out of a field".
    /// </para>
    /// <para>⚠ Ink stays off the border, like every other icon here — a held item is extruded from
    /// its own silhouette, and ink on the edge extrudes into a wall wearing its own outline.</para>
    /// </remarks>
    public static byte[] IconRoot(
        int seed, byte r, byte g, byte b, byte leafR, byte leafG, byte leafB, bool tapered)
    {
        var t = new byte[BytesPerTile];

        // The body runs corner to corner, thickest at the top and drawn to a point at the bottom
        // when it is tapered. Rows 4..13, so two clear squares top and bottom.
        for (var y = 4; y < Size - 2; y++)
        {
            var down = (y - 4) / 9f;

            var half = tapered
                ? float.Lerp(3.1f, 0.6f, down)
                : 3.0f - MathF.Abs(down - 0.5f) * 2.2f;

            var cx = 7.5f + (down - 0.5f) * 1.6f;

            for (var x = 1; x < Size - 1; x++)
            {
                if (MathF.Abs(x - cx) > half) continue;

                // Lit from the top left, the way every icon here is, plus a little grain along it.
                var lift = (Noise(x, y, seed) * 2f - 1f) * 13f - (y - 8) * 2.2f + (8 - x) * 1.4f;
                var v = (int)lift;

                Put(t, x, y, Clamp(r + v), Clamp(g + v), Clamp(b + v), 255);
            }
        }

        // And the tuft, three short leaves off the shoulder.
        for (var leaf = 0; leaf < 3; leaf++)
        {
            var x = 6 + leaf * 2;

            for (var y = 1; y <= 3 + (leaf == 1 ? 0 : -1); y++)
            {
                var d = (int)((Noise(x, y, seed + 5) * 2f - 1f) * 14f);
                Put(t, x, y, Clamp(leafR + d), Clamp(leafG + d), Clamp(leafB + d), 255);
            }
        }

        return t;
    }

    public static byte[] IconPile(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        for (var y = 2; y < Size - 2; y++)
        for (var x = 2; x < Size - 2; x++)
        {
            var dx = (x - 7.5f) / 5.6f;
            var dy = (y - 8.0f) / 5.2f;

            // A lumpy round mass: a disc with its edge chewed by noise, so it is a heap of grains
            // rather than a ball.
            if (dx * dx + dy * dy > 0.72f + (Noise(x, y, seed) - 0.5f) * 0.55f) continue;

            // Lit from the top left, the way every icon here is.
            var lift = (Noise(x, y, seed + 13) * 2f - 1f) * 16f - (y - 8) * 3.4f + (8 - x) * 1.6f;
            var v = (int)lift;

            Put(t, x, y, Clamp(r + v), Clamp(g + v), Clamp(b + v), 255);
        }

        return t;
    }

    /// <summary>
    /// A scatter of grains: seed corn, or a pinch of meal.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Both of these were drawn with <see cref="IconSkein"/> and came out as LOOPS.</b> A
    /// skein is a coil of string — a closed ring with a hole in it — which is right for string and
    /// says nothing whatever about a handful of seed. Caught on the icon sheet, where a green ring
    /// and a white ring sat side by side and neither was what it claimed.
    /// <para>⛳ Grains are small, separate and heaped low: a scatter with more of them toward the
    /// bottom of the tile, because a pinch of anything dropped on a surface settles.</para>
    /// </remarks>
    public static byte[] IconGrains(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        // ⛔ LAID ON A GRID AND JITTERED, not scattered from noise. Taking both coordinates straight
        // from the stream makes the result a matter of the seed's luck: the same function drew a
        // decent handful of seed corn and, one seed along, six specks of bone meal in two clumps.
        // A drawing that is only right for the seed it was tried with is not a drawing.
        const int Across = 5;
        const int Down = 4;

        for (var i = 0; i < Across * Down; i++)
        {
            var col = i % Across;
            var row = i / Across;

            var x = 2 + col * 3 + (int)MathF.Round((Noise(i, 0, seed) * 2f - 1f) * 1.2f);

            // Squared, so the heap settles toward the bottom rather than filling the square evenly.
            var fall = (row + Noise(0, i, seed + 19)) / Down;
            var y = 3 + (int)(fall * fall * 11f);

            // ⚠ Two thirds of the grid, so it reads as a scatter rather than as a pattern — but
            // dropped from a full grid, so no seed can leave a corner of the tile empty.
            if (Noise(i, i, seed + 53) > 0.66f) continue;
            if (x < 1 || x > Size - 3 || y < 1 || y > Size - 3) continue;

            // ⛳ Each grain is a 2x2 with a darker corner, which is how the reference draws one and
            // is the difference between a seed and a speck of dust. A single pixel at this size is
            // noise; four with a shaded corner is an object.
            var d = (int)((Noise(x, y, seed + 41) * 2f - 1f) * 18f);

            Put(t, x, y, Clamp(r + d + 14), Clamp(g + d + 14), Clamp(b + d + 8), 255);
            Put(t, x + 1, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
            Put(t, x, y + 1, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
            Put(t, x + 1, y + 1, Clamp(r + d - 30), Clamp(g + d - 30), Clamp(b + d - 20), 255);
        }

        return t;
    }

    /// <summary>
    /// A stand of wheat: blades from the ground up, and ears on it when it is ripe.
    /// </summary>
    /// <param name="height">How far up the tile the tallest blade reaches, in pixels.</param>
    /// <param name="eared">True for the ripe stage, which carries grain on the stalks.</param>
    /// <remarks>
    /// ⛳ <b>The silhouette grows rather than the tile filling.</b> A crop tile is drawn on crossed
    /// planes seen from a distance, so the two things that read across a field are HEIGHT and
    /// COLOUR — a seedling short and green, a ripe ear tall and gold. A stage that changed only its
    /// colour would leave a player walking their own field to find out what is ready.
    /// <para>⚠ Blades at uneven heights and uneven spacing, because a crop drawn as a comb reads as
    /// a fence. The unevenness is noise off the seed, so the same stage always draws the same field.
    /// </para>
    /// </remarks>
    /// <summary>
    /// A stand of leafy tops, with the root itself breaking the soil once it is ready.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Why this is not <see cref="Wheat"/> with different numbers.</b> Wheat says "ripe" by
    /// going gold from the ground up, because the whole plant is the crop. A carrot's tops stay green
    /// to the end and what changes is that the carrot is showing — so the ripe signal has to be a
    /// different COLOUR IN A DIFFERENT PLACE, at the foot, not a recolouring of the leaves.
    /// <para>⛔ <b>It is also the only thing telling three fields apart.</b> Real carrot, potato and
    /// beetroot tops are all much the same green; if the ripe signal were leaf colour, a player would
    /// have to walk into a field to learn which one it was. The root is what carries the difference,
    /// which is why <see cref="StarterBlocks.Crop"/> keeps leaf and root as two colours.</para>
    /// <para>⚠ Blades lean off the seed rather than standing straight, for the same reason wheat's do:
    /// a crop drawn as a comb reads as a fence.</para>
    /// </remarks>
    public static byte[] RootCrop(
        int seed, int height,
        byte leafR, byte leafG, byte leafB,
        byte rootR, byte rootG, byte rootB,
        bool showRoot)
    {
        var t = new byte[BytesPerTile];

        for (var stalk = 0; stalk < 5; stalk++)
        {
            var x = 2 + stalk * 3;
            var tall = height - (int)(Noise(stalk, 7, seed) * 3f);
            var prev = Math.Clamp(x, 1, Size - 2);

            for (var up = 0; up < tall; up++)
            {
                var y = Size - 1 - up;
                if (y < 1) break;

                // A lean of at most one square, so a top flops rather than standing to attention.
                var lean = up > tall / 2 && Noise(stalk, up, seed) > 0.55f ? 1 : 0;
                var lx = Math.Clamp(x + (stalk % 2 == 0 ? lean : -lean), 1, Size - 2);

                // ⛔ THE LEAN FILLS ACROSS, and this is the feather's bug in miniature. Stepping one
                // square sideways and one square up in the same move leaves two runs of ink that
                // touch at a CORNER only — four-connected, that is two islands, and a blade that
                // leans twice is three. The audit counts islands for exactly this and caught it at
                // ten pieces per tile. Filling from the previous column to this one costs one square
                // and makes the blade one connected thing however often it bends.
                var from = Math.Min(prev, lx);
                var to = Math.Max(prev, lx);
                var shade = up < 2 ? -24 : 0;

                for (var bx = from; bx <= to; bx++)
                {
                    var d = (int)((Noise(bx, y, seed) * 2f - 1f) * 12f);

                    Put(t, bx, y,
                        Clamp(leafR + d + shade), Clamp(leafG + d + shade), Clamp(leafB + d + shade),
                        255);
                }

                prev = lx;
            }
        }

        if (!showRoot) return t;

        // The crop itself, shouldering out of the soil along the bottom two rows. Off-centre and
        // uneven, because a row of identical lumps reads as masonry rather than as vegetables.
        for (var lump = 0; lump < 3; lump++)
        {
            var cx = 3 + lump * 5 + (Noise(lump, 3, seed) > 0.5f ? 1 : 0);

            for (var y = Size - 2; y < Size; y++)
            for (var x = cx - 1; x <= cx + 1; x++)
            {
                if (x < 1 || x > Size - 2) continue;

                // The shoulder is narrower than the belly, so it sits IN the ground rather than on it.
                if (y == Size - 2 && x != cx) continue;

                var d = (int)((Noise(x, y, seed + lump) * 2f - 1f) * 10f);
                Put(t, x, y, Clamp(rootR + d), Clamp(rootG + d), Clamp(rootB + d), 255);
            }
        }

        return t;
    }

    /// <summary>
    /// A berry bush: a knee-high tangle of shoots off one low mound, fruiting when it is ripe.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>The ripe signal is colour in a different place, the root crops' own rule:</b> the
    /// leaves stay the same green in both states and the berries are what appears, so the two tiles
    /// read as one plant in two moments rather than as two plants.</para>
    /// <para>⚠ <b>One island by construction.</b> Every shoot rises from a connected base mound and
    /// fills across its lean (the feather's rule), and a berry only ever RECOLOURS a cell that is
    /// already ink — a berry floating beside the plant would be a second island, and the audit
    /// counts.</para>
    /// </remarks>
    public static byte[] BerryBush(int seed, bool ripe)
    {
        var t = new byte[BytesPerTile];

        // The mound the whole plant rises from: two low rows, dark, slightly ragged on top.
        for (var y = Size - 2; y < Size; y++)
        for (var x = 2; x <= 13; x++)
        {
            if (y == Size - 2 && Noise(x, y, seed + 3) > 0.8f) continue;

            var d = (int)((Noise(x, y, seed) * 2f - 1f) * 14f);
            Put(t, x, y, Clamp(38 + d), Clamp(74 + d), Clamp(34 + d), 255);
        }

        // Five shoots off the mound, leaning outward as they climb, each filled across its lean.
        for (var shoot = 0; shoot < 5; shoot++)
        {
            var x = 3 + shoot * 2 + (shoot > 1 ? shoot - 1 : 0);
            var tall = 8 + (int)(Noise(shoot, 1, seed + 7) * 5f);
            var prev = x;

            for (var up = 2; up < tall; up++)
            {
                var y = Size - 1 - up;
                if (y < 2) break;

                var lean = up > 3 && Noise(shoot, up, seed + 11) > 0.55f
                    ? (shoot < 2 ? -1 : shoot > 2 ? 1 : 0)
                    : 0;
                var lx = Math.Clamp(x + lean, 1, Size - 2);

                var from = Math.Min(prev, lx);
                var to = Math.Max(prev, lx);

                for (var bx = from; bx <= to; bx++)
                {
                    var d = (int)((Noise(bx, y, seed + 17) * 2f - 1f) * 16f);
                    Put(t, bx, y, Clamp(52 + d), Clamp(98 + d), Clamp(44 + d), 255);
                }

                // A leaf beside the shoot most rows, so it is a bush rather than a broom.
                if (Noise(shoot, up, seed + 23) > 0.4f)
                {
                    var side = Noise(shoot, up, seed + 29) > 0.5f ? 1 : -1;
                    var ax = Math.Clamp(lx + side, 1, Size - 2);
                    var d = (int)((Noise(ax, y, seed + 31) * 2f - 1f) * 14f);
                    Put(t, ax, y, Clamp(66 + d), Clamp(116 + d), Clamp(54 + d), 255);
                }

                prev = lx;
            }
        }

        if (!ripe) return t;

        // The fruit, hung through the middle of the tangle. Each berry recolours a painted cell,
        // with a bright shoulder above it where that cell is painted too.
        for (var berry = 0; berry < 12; berry++)
        {
            var bx = 2 + (int)(Noise(berry, 5, seed + 37) * 11.9f);
            var by = 4 + (int)(Noise(berry, 9, seed + 41) * 9.9f);
            if ((uint)bx >= Size || (uint)by >= Size) continue;

            if (t[(by * Size + bx) * 4 + 3] == 0) continue;

            Put(t, bx, by, 182, 36, 50, 255);

            if (by > 0 && t[((by - 1) * Size + bx) * 4 + 3] != 0)
                Put(t, bx, by - 1, 216, 66, 74, 255);
        }

        return t;
    }

    /// <summary>
    /// A mushroom: a pale stem under a domed cap, spotted when the cap is loud.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>The stem is the tell between raw and roasted</b>, the cooked-meat rule at work: a
    /// roast browns THROUGHOUT, so its stem goes toasted gold where a raw one's is near-white — two
    /// browns side by side in a slot still read as before-and-after rather than as two kinds.</para>
    /// <para>⚠ Cap and stem overlap on the rim row, so the drawing is one island. Spots are painted
    /// over cap ink only. <paramref name="ground"/> roots the stem on the tile's floor for the
    /// standing block; off, it stops short of the border the way every carried icon must.</para>
    /// </remarks>
    public static byte[] Mushroom(
        int seed, byte capR, byte capG, byte capB, byte stemR, byte stemG, byte stemB,
        bool spotted, bool ground)
    {
        var t = new byte[BytesPerTile];

        // The stem, darker toward its foot, planted or lifted by a row depending on the job.
        var foot = ground ? Size : Size - 2;
        for (var y = 8; y < foot; y++)
        for (var x = 7; x <= 8; x++)
        {
            var d = (int)((Noise(x, y, seed) * 2f - 1f) * 10f) - (y - 10) * 2;
            Put(t, x, y, Clamp(stemR + d), Clamp(stemG + d), Clamp(stemB + d), 255);
        }

        // The cap: a dome overhanging the stem, lit at its crown, darker along the underside rim.
        for (var y = 3; y <= 8; y++)
        {
            var half = y switch { 3 => 2, 4 => 3, _ => 4 };

            for (var x = 8 - half; x <= 7 + half; x++)
            {
                var d = (int)((Noise(x, y, seed + 7) * 2f - 1f) * 14f)
                        + (5 - y) * 4 + (y == 8 ? -34 : 0);
                Put(t, x, y, Clamp(capR + d), Clamp(capG + d), Clamp(capB + d), 255);
            }
        }

        if (!spotted) return t;

        // The spots, over cap ink only — never beside it.
        foreach (var (sx, sy) in (ReadOnlySpan<(int X, int Y)>)[(6, 4), (9, 5), (5, 6), (11, 6), (8, 3)])
        {
            var d = (int)((Noise(sx, sy, seed + 19) * 2f - 1f) * 10f);
            Put(t, sx, sy, Clamp(232 + d), Clamp(226 + d), Clamp(214 + d), 255);
        }

        return t;
    }

    /// <summary>A pumpkin's flank: fat vertical ribs under a darker rind at the crown.</summary>
    /// <remarks>
    /// ⛳ The ribs are four texels wide with a darker seam between them — the one thing every
    /// pack's pumpkin agrees on, and what separates it from any plain orange block at a distance.
    /// </remarks>
    public static byte[] PumpkinSide(int seed)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var seam = x % 4 == 3;
            var crown = y == 0;

            var d = (int)((Noise(x, y, seed) * 2f - 1f) * 12f);
            var (r, g, b) = seam ? (176, 88, 22) : (214, 116, 34);
            if (crown) (r, g, b) = (r - 36, g - 28, b - 8);

            // A little roundness: the flank dims toward its edges the way a drum does.
            var shade = Math.Abs(x - 8) * 2;
            Put(t, x, y, Clamp(r + d - shade), Clamp(g + d - shade), Clamp(b + d - shade), 255);
        }

        return t;
    }

    /// <summary>And its crown: the same ribs run to a woody stem in the middle.</summary>
    public static byte[] PumpkinTop(int seed)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // Ribs radiate: the seam follows whichever axis is farther from the middle, so the
            // top reads as segments meeting at the stem rather than as a second flank.
            var dx = Math.Abs(x - 8);
            var dz = Math.Abs(y - 8);
            var seam = (dx >= dz ? x : y) % 4 == 3;

            var d = (int)((Noise(x, y, seed) * 2f - 1f) * 12f);
            var (r, g, b) = seam ? (170, 86, 22) : (206, 112, 32);
            var shade = Math.Max(dx, dz);

            Put(t, x, y, Clamp(r + d - shade), Clamp(g + d - shade), Clamp(b + d - shade), 255);
        }

        // The stem, a woody knot of green-brown squarely in the middle.
        for (var y = 6; y <= 9; y++)
        for (var x = 6; x <= 9; x++)
        {
            var d = (int)((Noise(x, y, seed + 7) * 2f - 1f) * 14f);
            Put(t, x, y, Clamp(96 + d), Clamp(78 + d), Clamp(38 + d), 255);
        }

        return t;
    }

    /// <summary>
    /// The carved face: the flank with triangle eyes and a jagged grin cut into it.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Lit is the same face glowing</b> — the cut is identical, only what shows through it
    /// changes — so a jack o'lantern reads as the carved pumpkin somebody put a torch in, which
    /// is exactly what it is.
    /// </remarks>
    public static byte[] PumpkinFace(int seed, bool lit)
    {
        var t = PumpkinSide(seed);

        var (r, g, b) = lit ? (252, 214, 84) : (30, 18, 10);

        void Cut(int x, int y)
        {
            var d = lit ? (int)((Noise(x, y, seed + 11) * 2f - 1f) * 10f) : 0;
            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        // Two triangle eyes...
        foreach (var ex in (ReadOnlySpan<int>)[3, 10])
        {
            Cut(ex, 5); Cut(ex + 1, 5); Cut(ex + 2, 5);
            Cut(ex + 1, 4);
        }

        // ...and a grin with two teeth in it.
        for (var x = 3; x <= 12; x++)
        {
            if (x is 5 or 10) continue;
            Cut(x, 10);
            if (x is > 4 and < 11) Cut(x, 11);
        }

        return t;
    }

    /// <summary>A cobweb: spokes from a middle, two sagging rings, on open air.</summary>
    /// <remarks>
    /// ⚠ Spokes and rings all cross, so for the island count the web is one drawing — which for
    /// once is also the truth of the thing drawn.
    /// </remarks>
    public static byte[] Cobweb(int seed)
    {
        var t = new byte[BytesPerTile];

        void Silk(int x, int y, int fade)
        {
            if ((uint)x >= Size || (uint)y >= Size) return;
            var d = (int)((Noise(x, y, seed) * 2f - 1f) * 10f) - fade;
            Put(t, x, y, Clamp(226 + d), Clamp(228 + d), Clamp(232 + d), 255);
        }

        // Eight spokes: the two axes and the two diagonals. ⛔ Every diagonal FILLS ACROSS its
        // step — the feather's rule — because a run of corner-touching pixels is one strand to
        // the eye and dozens of islands to the audit, and the audit counts.
        for (var i = 0; i < Size; i++)
        {
            Silk(i, 7, 8);
            Silk(7, i, 8);

            Silk(i, i, 0);
            Silk(i, Size - 1 - i, 0);
            if (i < Size - 1)
            {
                Silk(i + 1, i, 6);
                Silk(i + 1, Size - 1 - i, 6);
            }
        }

        // Two rings, drawn as diamonds because silk sags straight between spokes — each edge
        // filled across its steps for the spokes' own reason.
        foreach (var ring in (ReadOnlySpan<int>)[3, 6])
        for (var i = 0; i <= ring; i++)
        {
            Silk(7 - ring + i, 7 - i, 4);
            Silk(7 + ring - i, 7 - i, 4);
            Silk(7 - ring + i, 7 + i, 4);
            Silk(7 + ring - i, 7 + i, 4);

            if (i >= ring) continue;
            Silk(7 - ring + i + 1, 7 - i, 7);
            Silk(7 + ring - i - 1, 7 - i, 7);
            Silk(7 - ring + i + 1, 7 + i, 7);
            Silk(7 + ring - i - 1, 7 + i, 7);
        }

        return t;
    }

    /// <summary>A picked handful: three round berries in a clump, a leaf at the shoulder.</summary>
    /// <remarks>
    /// ⚠ Drawn as one connected clump on purpose — the berries overlap and the leaf touches the top
    /// one — because a handful of separate dots is exactly the spray the island count refuses. Ink
    /// stays off the border, like every icon here.
    /// </remarks>
    public static byte[] IconBerries(int seed)
    {
        var t = new byte[BytesPerTile];

        // Three berries packed into a triangle, lit from the top left like everything else.
        foreach (var (cx, cy) in (ReadOnlySpan<(int X, int Y)>)[(6, 9), (10, 9), (8, 6)])
        for (var dy = -2; dy <= 2; dy++)
        for (var dx = -2; dx <= 2; dx++)
        {
            if (dx * dx + dy * dy > 4) continue;

            var x = cx + dx;
            var y = cy + dy;
            if ((uint)x >= Size || (uint)y >= Size) continue;

            var d = (int)((Noise(x, y, seed) * 2f - 1f) * 12f);
            var lift = (2 - dx - dy) * 6;
            Put(t, x, y, Clamp(170 + lift + d), Clamp(36 + lift / 3 + d), Clamp(52 + lift / 3 + d), 255);
        }

        // The leaf, touching the top berry so the drawing is one thing.
        for (var i = 0; i < 3; i++)
        {
            var d = (int)((Noise(i, 2, seed + 11) * 2f - 1f) * 12f);
            Put(t, 8 + i, 3 + (i == 2 ? 1 : 0), Clamp(70 + d), Clamp(118 + d), Clamp(56 + d), 255);
        }

        return t;
    }

    public static byte[] Wheat(int seed, int height, byte r, byte g, byte b, bool eared)
    {
        var t = new byte[BytesPerTile];

        // Five stalks across the tile, none of them on the border — the ink rule every held thing
        // here follows, and a crop is picked up and carried like anything else.
        for (var stalk = 0; stalk < 5; stalk++)
        {
            var x = 2 + stalk * 3;
            var tall = height - (int)(Noise(stalk, 0, seed) * 3f);

            for (var up = 0; up < tall; up++)
            {
                var y = Size - 1 - up;
                if (y < 1) break;

                var d = (int)((Noise(x, y, seed) * 2f - 1f) * 14f);

                // Darker at the foot, where a stand of anything is in its own shadow.
                var shade = up < 2 ? -26 : 0;
                Put(t, x, y, Clamp(r + d + shade), Clamp(g + d + shade), Clamp(b + d + shade), 255);
            }

            if (!eared) continue;

            // The grain, on the top third of each stalk and to one side of it, so a ripe field reads
            // as heavy rather than as a taller green one.
            for (var ear = 0; ear < 3; ear++)
            {
                var y = Size - tall + ear * 2;
                if (y < 1 || y > Size - 2) continue;

                var side = (stalk + ear) % 2 == 0 ? -1 : 1;
                var ex = Math.Clamp(x + side, 1, Size - 2);

                Put(t, ex, y, Clamp(r + 22), Clamp(g + 12), Clamp(b - 10), 255);
            }
        }

        return t;
    }

    /// <summary>
    /// A closed book: a leather cover, a spine down one side and page edges down the other.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The stand-in for a painted tile, not the tile itself.</b> The recipe book ships as a
    /// drawing (see <c>PaintedArt</c>); this is what a build that lost the embedded resource falls
    /// back to, and it exists so that failure is a plain brown book rather than the magenta
    /// placeholder — which reads as a hole in a texture pack and sends somebody looking in the
    /// wrong place entirely.
    /// </remarks>
    public static byte[] IconBook(int seed)
    {
        var t = new byte[BytesPerTile];

        const int Left = 2, Right = 13, Top = 2, Bottom = 13;

        for (var y = Top; y <= Bottom; y++)
        for (var x = Left; x <= Right; x++)
        {
            var d = (int)((Noise(x, y, seed) * 2f - 1f) * 8f);

            // The last two columns are the cut edges of the pages; everything else is the cover.
            if (x >= Right - 1)
            {
                var v = Clamp(226 + d);
                Put(t, x, y, v, Clamp(v - 12), Clamp(v - 42), 255);
                continue;
            }

            // The spine is darker than the board, which is what makes it read as a book edge-on
            // rather than as a brown card.
            var spine = x <= Left + 1;
            var r = spine ? 96 : 138;
            var g = spine ? 58 : 88;
            var b = spine ? 36 : 54;

            if (y == Top || y == Bottom) { r -= 26; g -= 18; b -= 12; }

            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
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
    /// <para>Drawn white and coloured at the point of use, so full, half and empty are one tile and
    /// three tints rather than three tiles that can drift apart. The outline is part of the shape
    /// rather than a separate pass: an empty heart is the same pixels in a dark colour, and it has to
    /// read as the same heart or the bar looks like two different rows.</para>
    /// <para>⛳ <b>The user's own drawing is used when this build carries it</b>, and the generated
    /// one below is the fallback. Their sheet is an outline, so its middle is flooded rather than
    /// drawn — see <see cref="PaintedArt.HeartTile"/>, which hands back exactly the shape this
    /// produces: the line darker than the middle, so one tile still serves both tints.</para>
    /// </remarks>
    public static byte[] Heart() => PaintedArt.HeartTile(Size) ?? GeneratedHeart();

    /// <summary>The drumstick a full notch of the hunger bar is, in the user's own colours.</summary>
    /// <remarks>
    /// ⛳ <b>Drawn rather than tinted, unlike every other icon on the bar.</b> The user painted this
    /// one in full colour — measured at thirty thousand distinct shades against the socket's fifteen
    /// hundred — so it is put on screen as it is, with a white tint that changes nothing. Tinting a
    /// finished drawing is how a piece of roast meat becomes a red silhouette of one.
    /// </remarks>
    public static byte[] DrumstickFull() => PaintedArt.SheetTile(PaintedArt.Food, Size) ?? Drumstick();

    /// <summary>And the hollow one under it, white so the empty tint can darken it.</summary>
    public static byte[] DrumstickSocket() =>
        PaintedArt.SheetTile(PaintedArt.FoodSocket, Size) ?? Drumstick();

    /// <summary>
    /// The generated drumstick, kept as the fallback when this build carries no painted one.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>A leg of meat, and it has to be nothing like a heart at nine pixels.</b> The two bars sit
    /// either side of the crosshair and are read at a glance in the dark; a round shape opposite a
    /// round shape is two rows a player has to count to tell apart. This is a bone running corner to
    /// corner with the meat on one end — diagonal where a heart is upright, which is the difference
    /// that survives being small.
    /// ⚠ Same two values as the heart: a darker rim at 176 and the body at 255, so the identical
    /// socket-and-fill tinting works on it and a partly-eaten drumstick tears the same way.
    /// </remarks>
    public static byte[] Drumstick()
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var u = (x + 0.5f) / Size;
            var v = (y + 0.5f) / Size;

            // The bone: a bar along the up-right diagonal, from the middle to the bottom left.
            var alongBone = MathF.Abs((u - v) - 0.16f);
            var bone = alongBone < 0.085f && u + v > 0.62f && u + v < 1.62f;

            // The meat: a fat lobe on the top-right end of it.
            var mx = u - 0.66f;
            var my = v - 0.34f;
            var meat = mx * mx * 1.35f + my * my < 0.075f;

            if (!bone && !meat) continue;

            // The rim is wherever the shape is about to stop, which is what gives the tint an edge.
            var edge = meat
                ? mx * mx * 1.35f + my * my > 0.050f
                : alongBone > 0.055f || u + v < 0.70f || u + v > 1.54f;

            var value = edge ? (byte)176 : (byte)255;
            Put(t, x, y, value, value, value, 255);
        }

        return t;
    }

    private static byte[] GeneratedHeart()
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

    /// <summary>
    /// The plate the armour bar is counted in, white to be tinted like the heart is.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>A shield outline rather than a chestplate.</b> At nine pixels on the bar, beside a row
    /// of hearts, a chestplate silhouette is a blob with two notches in it and reads as a heart in
    /// the wrong colour — which is the one thing the row must not do. A pointed heater is the only
    /// small shape in the vocabulary that is nothing like a heart.
    /// </remarks>
    public static byte[] Plate()
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var u = (x + 0.5f) / Size * 2f - 1f;
            var v = (y + 0.5f) / Size;

            // Straight sides for the top two thirds, then tapering to a point.
            var half = v < 0.62f ? 0.74f : 0.74f * (1f - (v - 0.62f) / 0.33f);
            if (v > 0.95f || MathF.Abs(u) > half) continue;

            var edge = MathF.Abs(u) > half - 0.22f || v < 0.10f || v > 0.86f;
            var shade = edge ? (byte)168 : (byte)255;
            Put(t, x, y, shade, shade, shade, 255);
        }

        return t;
    }

    /// <summary>The bubble the breath meter is counted in — the user's own, or ours.</summary>
    /// <remarks>
    /// ⛳ Taken off the sheet whole, with no middle flooded into it: a bubble is a ring of light and
    /// filling one would give a pearl. That is the difference between this and the heart, whose art
    /// arrived as the same kind of outline and had to have an inside derived for the red to go in.
    /// </remarks>
    public static byte[] BubbleTile() =>
        PaintedArt.SheetTile(PaintedArt.Breath, Size, keepThinLines: true) ?? Bubble();

    /// <summary>The generated bubble, kept as the fallback when no painted one is carried.</summary>
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
    /// <summary>
    /// A pane of coloured glass: the colour fills it, with a darker frame and a highlight.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Filled, where <see cref="Glass"/> is empty, and that is the whole difference between
    /// them.</b> A clear window IS its hole — the frame and the sky-streak are all there is to draw.
    /// A coloured one has to have glass in the middle or it is not coloured at all, so the middle is
    /// the point and the frame is trim round it.
    /// <para>⚠ <b>Every square is opaque in the tile.</b> The shader alpha-tests at 0.5 and discards
    /// below it, so a pane drawn half-transparent would come out as a pane with holes in it. What
    /// makes this see-through is the PASS it is drawn in — see <c>BlockType.Translucent</c>.</para>
    /// <para>⚠ Lifted well off the dye's own colour: sixteen wools at their true value are legible
    /// because you see them lit from in front, and a window is seen with daylight coming THROUGH it.
    /// The dark ones especially — a black pane at its wool value is a hole in the wall.</para>
    /// </remarks>
    public static byte[] StainedGlass(int seed, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var edge = x == 0 || y == 0 || x == Size - 1 || y == Size - 1;
            var streak = y >= 2 && y <= 6 && x - y >= 1 && x - y <= 3;

            var d = (int)((Noise(x, y, seed) * 2f - 1f) * 9f);

            // Three tones off one colour: a frame that reads as leading, the glass itself, and the
            // streak where a pane catches the sky.
            var lift = edge ? -34 : streak ? 54 : 30;

            Put(t, x, y, Clamp(r + d + lift), Clamp(g + d + lift), Clamp(b + d + lift), 255);
        }

        return t;
    }

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
    public static byte[] LanternTile(int seed, byte r, byte g, byte b, float glow = 1f, float lean = 0f)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // The two bars top and bottom and the two posts either side are the cage; everything
            // inside it is the flame it is holding. ⚠ The cage never takes glow or lean — it is
            // iron, and the animation check below holds it to that by measurement.
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
            // as a coloured pane behind a grille. Glow breathes the whole bead and lean sways its
            // centre — at the defaults both fall away exactly, so frame 0 IS the still tile.
            var dx = (x - (Size - 1) / 2f - lean) / 5f;
            var dy = (y - (Size - 2f)) / 9f;
            var heat = Math.Clamp(1f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
            heat = Math.Clamp((heat + (Noise(x, y, seed + 17) * 2f - 1f) * 0.12f) * glow, 0f, 1f);

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

    /// <summary>
    /// Ice: winter's lid on the water — pale blue faintly swirled, with bright pressure cracks.
    /// </summary>
    /// <remarks>
    /// Fully opaque texels on a see-through BLOCK, the leaves' own arrangement: what makes ice
    /// read as ice at distance is colour against dark water below, not holes in the picture. The
    /// cracks run one diagonal and are FILLED per pixel by remainder, not walked — the feather's
    /// checkerboard lesson.
    /// </remarks>
    public static byte[] Ice(int seed)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // A scatter of clear pinpricks — trapped air. Honest holes: this layer is cutout so
            // a pack's translucent ice can land on it, and the cutout audit refuses a cutout
            // with nothing cut; water glinting through its own lid is also simply what ice does.
            if ((x * 7 + y * 11 + seed) % 53 == 0)
            {
                Put(t, x, y, 0, 0, 0, 0);
                continue;
            }

            var swirl = Noise(x, y, seed) * 2f - 1f;
            var r = Clamp((int)(170 + swirl * 9f));
            var g = Clamp((int)(198 + swirl * 9f));
            var b = Clamp((int)(228 + swirl * 7f));

            // Two long pressure cracks, jittered so neither reads as a ruled line.
            var drift = (int)(Noise(x / 5, y / 5, seed + 9) * 5f);
            if ((x + y + drift) % 11 == 0 || (x - y + 32 + drift) % 13 == 0)
            {
                r = Clamp(r + 42);
                g = Clamp(g + 34);
                b = Clamp(b + 24);
            }

            Put(t, x, y, r, g, b, 255);
        }

        return t;
    }

    /// <summary>Cactus flank: ribbed desert green, a spine down each ridge line.</summary>
    /// <remarks>
    /// The ribs are vertical bands of two greens — what reads as a cactus at forty paces is the
    /// striping, not the spines — and the spines are single pale texels on the rib crests, filled
    /// per pixel by remainder rather than walked.
    /// </remarks>
    public static byte[] CactusSide(int seed)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // The clear margin every pack cuts this art with — see the block's own note.
            if (x is 0 or Size - 1) continue;

            var grain = (int)((Noise(x, y, seed) * 2f - 1f) * 8f);

            // Four ribs across the face: crest, flank, trough, flank.
            var rib = x % 4;
            var (r, g, b) = rib switch
            {
                0 => (96, 138, 70),
                2 => (66, 102, 50),
                _ => (80, 120, 60),
            };

            // A spine on every crest, every fourth row, offset per column so they stagger.
            if (rib == 0 && (y + x / 4 * 2) % 4 == 0) (r, g, b) = (214, 214, 188);

            Put(t, x, y, Clamp(r + grain), Clamp(g + grain), Clamp(b + grain), 255);
        }

        return t;
    }

    /// <summary>Cactus top: the same ribs run to a middle, with the pale heart a cut one shows.</summary>
    public static byte[] CactusTop(int seed)
    {
        var t = new byte[BytesPerTile];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // The same clear margin as the flank, all the way round.
            if (x is 0 or Size - 1 || y is 0 or Size - 1) continue;

            var grain = (int)((Noise(x, y, seed + 3) * 2f - 1f) * 8f);
            var dx = MathF.Abs(x - 7.5f);
            var dy = MathF.Abs(y - 7.5f);
            var ring = MathF.Max(dx, dy);

            var (r, g, b) = ring < 3f
                ? (150, 176, 122)                      // the pale flesh of the crown
                : ((int)ring % 2 == 0 ? (94, 136, 68) : (70, 106, 52));

            Put(t, x, y, Clamp(r + grain), Clamp(g + grain), Clamp(b + grain), 255);
        }

        return t;
    }

    /// <summary>The dead bush: a dry fork of twigs on nothing, in old-rope browns.</summary>
    public static byte[] DeadBush(int seed)
    {
        var t = new byte[BytesPerTile];

        // A trunk up the middle of the lower half, then three forks drawn per pixel against
        // segment distance — ToSegment is what cannot skip cells on a diagonal.
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var trunk = x is 7 or 8 && y >= 9;
            var left = ToSegment(x, y, 7f, 10f, 3f, 4f) < 0.7f;
            var right = ToSegment(x, y, 8f, 10f, 12f, 3f) < 0.7f;
            var middle = ToSegment(x, y, 7.5f, 9f, 7f, 2f) < 0.6f;

            if (!trunk && !left && !right && !middle) continue;

            var grain = (int)((Noise(x, y, seed) * 2f - 1f) * 14f);
            Put(t, x, y, Clamp(122 + grain), Clamp(92 + grain), Clamp(58 + grain), 255);
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

    /// <summary>
    /// The four pieces of armour, in <see cref="Items.EquipSlot"/> order.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>Silhouettes first, and they are what a player reads at sixteen pixels.</b> A helmet
    /// is a dome with a face cut out of it, a chestplate is shoulders wider than its waist, leggings
    /// are a band with two legs under it, and boots are a pair. Those four outlines are distinct at
    /// a glance in a way four differently-shaded rectangles are not — which is the fault the tools
    /// were caught with, where the axe and the shovel were the same shape shifted one column.</para>
    /// <para>⚠ <b>Two of them are deliberately two pieces of ink.</b> Boots are a pair and leggings
    /// have a leg either side of a gap; the drawing check allows up to six islands and reads three
    /// for the shears, so a pair is well inside it. Joining them would be drawing a mistake to
    /// satisfy a check.</para>
    /// </remarks>
    public static readonly string[][] ArmourShapes =
    [
        // Helmet: a domed cap, cheek pieces either side of the face.
        [
            "................",
            "................",
            "....llllllll....",
            "...lmmmmmmmml...",
            "..lmmmmmmmmmml..",
            "..mmmmmmmmmmmm..",
            "..mmmmmmmmmmmm..",
            "..mmmmmmmmmmmm..",
            "..mmm......mmm..",
            "..mmM......Mmm..",
            "..mMM......MMm..",
            "...MM......MM...",
            "................",
            "................",
            "................",
            "................",
        ],

        // Chestplate: shoulders, a neck cut out between them, a waist narrower than the chest.
        [
            "................",
            ".mmm..llll..mmm.",
            ".mmmmllllllmmmm.",
            ".mmmmmllllmmmmm.",
            ".mmmmmmmmmmmmmm.",
            ".mmmmmmmmmmmmmm.",
            "..mmmmmmmmmmmm..",
            "..mmmmmmmmmmmm..",
            "..mmmmmmmmmmmm..",
            "..Mmmmmmmmmmmm..",
            "..Mmmmmmmmmmmm..",
            "..MMmmmmmmmmMM..",
            "...MMMMMMMMMM...",
            "................",
            "................",
            "................",
        ],

        // Leggings: a waistband with two legs hanging off it.
        [
            "................",
            "................",
            "..llllllllllll..",
            "..mmmmmmmmmmmm..",
            "..mmmmmmmmmmmm..",
            "..mmmmmmmmmmmm..",
            "..mmmm....mmmm..",
            "..mmmm....mmmm..",
            "..mmmm....mmmm..",
            "..mmmm....mmmm..",
            "..MmmM....MmmM..",
            "..MMMM....MMMM..",
            "................",
            "................",
            "................",
            "................",
        ],

        // Boots: a pair, seen from the side, toes outward.
        [
            "................",
            "................",
            "................",
            "................",
            "................",
            "................",
            "..llll....llll..",
            "..mmmm....mmmm..",
            "..mmmm....mmmm..",
            "..mmmm....mmmm..",
            ".mmmmm...mmmmm..",
            ".mmmmmm..mmmmmm.",
            ".MMMMMM..MMMMMM.",
            "................",
            "................",
            "................",
        ],
    ];

    /// <summary>One piece of armour: a silhouette in a material's colours, riveted and edged.</summary>
    /// <remarks>
    /// ⛳ Shares the tool icons' shading letters and the same grown outline. A row of armour and a
    /// row of tools in the same metal have to read as the same metal, and two separate shading rules
    /// is exactly how they stop doing so.
    /// </remarks>
    public static byte[] IconArmour(int seed, int piece, byte r, byte g, byte b)
    {
        var t = new byte[BytesPerTile];
        var rows = ArmourShapes[piece];

        for (var y = 0; y < Size && y < rows.Length; y++)
        for (var x = 0; x < Size && x < rows[y].Length; x++)
        {
            var c = rows[y][x];
            if (c == '.') continue;

            var d = c switch
            {
                'l' => 44,
                'm' => 0,
                _ => -36,
            } + (int)((Noise(x, y, seed) * 2f - 1f) * 9f);

            // ⚠ Rivets rather than a gradient. A plate at this size reads as beaten metal because of
            // the studs along its edges, and the one place a gradient was tried on an icon in this
            // project it was measured to make the tone range slightly WORSE — see IconTool.
            if ((x + 1) % 5 == 0 && (y + 2) % 4 == 0) d += 30;

            Put(t, x, y, Clamp(r + d), Clamp(g + d), Clamp(b + d), 255);
        }

        return Edged(t);
    }

    /// <summary>
    /// A shield: a heater board with a rim and a boss, drawn in two materials.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Both materials are on the tile on purpose.</b> The recipe is timber round an iron boss,
    /// and an icon that showed only the board would be a picture a player could not read the recipe
    /// off. It is also what tells it apart from a plain plank in a slot at sixteen pixels.
    /// </remarks>
    public static byte[] IconShield(int seed, byte r, byte g, byte b, byte mr, byte mg, byte mb)
    {
        var t = new byte[BytesPerTile];
        const float Centre = (Size - 1) / 2f;

        for (var y = 1; y < Size - 1; y++)
        for (var x = 2; x < Size - 2; x++)
        {
            // A heater: straight sides down to two thirds, then tapering to a point at the bottom.
            var shoulder = y < Size * 2 / 3;
            var half = shoulder ? 6f : 6f - (y - Size * 2f / 3f) * 1.7f;
            if (MathF.Abs(x - Centre) > half) continue;

            var rim = MathF.Abs(x - Centre) > half - 1.2f || y <= 2 || half < 1.6f;

            // The boss, dead centre, in the same metal as the rim.
            var dx = x - Centre;
            var dy = y - Centre + 0.5f;
            var boss = dx * dx + dy * dy < 5.2f;

            var metal = rim || boss;
            var (br, bg, bb) = metal ? (mr, mg, mb) : (r, g, b);

            var d = (boss ? 22 : 0)
                  + (int)((Centre - y) * 1.6f)
                  + (int)((Noise(x, y, seed) * 2f - 1f) * 9f);

            // The grain of the boards runs down it, which is what stops the timber reading as felt.
            if (!metal) d += x % 4 == 0 ? -14 : 0;

            Put(t, x, y, Clamp(br + d), Clamp(bg + d), Clamp(bb + d), 255);
        }

        return Edged(t);
    }

    /// <summary>Grows a one-pixel dark line round whatever has been drawn.</summary>
    /// <remarks>
    /// ⛳ Shared by the tools and the armour. Written as a pass over what is there rather than as
    /// pixels in the drawings: a hand-drawn outline is a hand-drawn mistake on a shape that is
    /// otherwise sampled, and this way a shape gets its edge for free including every gap inside it.
    /// </remarks>
    private static byte[] Edged(byte[] tile)
    {
        var edged = (byte[])tile.Clone();

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            if (tile[(y * Size + x) * 4 + 3] >= 128) continue;

            var touches = false;
            for (var side = 0; side < 4 && !touches; side++)
            {
                var nx = x + (side == 0 ? -1 : side == 1 ? 1 : 0);
                var ny = y + (side == 2 ? -1 : side == 3 ? 1 : 0);
                if (nx < 0 || ny < 0 || nx >= Size || ny >= Size) continue;
                touches = tile[(ny * Size + nx) * 4 + 3] >= 128;
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

    /// <summary>Keeps a channel in range. Shared so a painter outside this file shades the same way.</summary>
    internal static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    /// <summary>Deterministic 0..1 hash noise. Stateless, so tiles can be built in any order.</summary>
    internal static float Noise(int x, int y, int seed)
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
