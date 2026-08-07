using System.Reflection;

namespace Driftwood.Core.Textures;

/// <summary>
/// The handful of tiles that are painted rather than generated, carried inside the assembly.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Every other tile in this game is drawn in code by <see cref="TileGen"/>, and that
/// argument is about the WORLD.</b> It buys two things there — a set that is unambiguously ours, and
/// a complete world under any half-finished pack — and neither of them is about interface chrome.
/// A book on a button is one picture, seen at one size, whose whole job is to read as a book at a
/// glance; sixteen procedural pixels cannot be generated into being more legible than a drawing of
/// it, and the recipe-book button spent its life as two pale rectangles because of that.</para>
/// <para>⚠ <b>Resampled from the source rather than from a 16px intermediate.</b> The rest of our
/// art is drawn at <see cref="TileGen.Size"/> and upscaled, which is why a 512-pixel pack sits
/// beside enormous flat squares of ours. This one has real pixels to give at any size the array is
/// built at, so it goes straight to that size and is the one layer in the set that does not get
/// worse as a pack gets better.</para>
/// <para>⚠ <b>Nearest neighbour, and letterboxed.</b> Smoothing would turn pixel art into a blur of
/// it, and the source is 532×469 — stretching a non-square drawing into a square tile is how a book
/// ends up looking like a door.</para>
/// </remarks>
public static class PaintedArt
{
    /// <summary>The recipe book: a closed, clasped, leather-bound one seen corner-on.</summary>
    public const string RecipeBook = "Driftwood.Core.recipe-book.png";

    /// <summary>The heart the health bar is counted in — a hollow pixel-art outline.</summary>
    public const string Heart = "Driftwood.Core.health.png";

    /// <summary>A drumstick, painted in full colour: what a filled notch of the hunger bar is.</summary>
    public const string Food = "Driftwood.Core.foodbar.png";

    /// <summary>And the same drumstick as a hollow outline: the socket under it.</summary>
    public const string FoodSocket = "Driftwood.Core.emptyfood.png";

    /// <summary>
    /// A bubble of air, drawn as a hollow ring with a highlight in it.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Hollow is right here, where it was wrong for the heart.</b> A heart is a solid thing and
    /// its outline is the edge of it, so the middle had to be flooded in. A bubble IS a rim of light
    /// with nothing inside — filling one would draw a pale disc, which is a pearl. So this takes the
    /// plain sheet path and no derivation at all.
    /// </remarks>
    public const string Breath = "Driftwood.Core.bubbles.png";

    private static readonly Dictionary<string, Image?> Loaded = [];
    private static readonly Dictionary<(string, int), byte[]> Tiles = [];
    private static readonly Dictionary<int, byte[]?> Hearts = [];

    /// <summary>
    /// One painted tile at the size the texture array is being built at, or null when it is absent.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Null rather than a throw.</b> A missing embedded resource is a build that was assembled
    /// wrongly, and the honest behaviour is the same as any other layer we have no art for: the
    /// caller falls back to a generated one and the texture check says which. A game that refuses to
    /// start over a button is worse than a button that looks plain.
    /// </remarks>
    public static byte[]? Tile(string name, int size)
    {
        if (size <= 0) return null;
        if (Tiles.TryGetValue((name, size), out var cached)) return cached;

        var source = Source(name);
        if (source is null) return null;

        var tile = Fit(source, size);
        Tiles[(name, size)] = tile;
        return tile;
    }

    /// <summary>True when this painted tile is actually carried by this build.</summary>
    public static bool Has(string name) => Source(name) is not null;

    /// <summary>
    /// The heart, as a white shape whose OUTLINE and INSIDE are two different values.
    /// </summary>
    /// <remarks>
    /// <para>⛳⛳ <b>The art is an outline, and the bar needs something that fills.</b> Measured off
    /// the user's own sheet 2026-08-07: 1242×140, seven cells, every opaque pixel pure white and
    /// every cell the same drawing — so it is one hollow heart laid out seven times, not a
    /// full/half/empty set and not a fill sequence. What is drawn is the <em>socket</em>.</para>
    /// <para>⛳ So the inside is derived rather than drawn: flood from the edges of the cell, and
    /// anything the flood cannot reach and the outline does not occupy is the middle of the heart.
    /// The tile comes out with the outline at 176 and the middle at 255, which is exactly the shape
    /// the old generated heart handed the HUD — <b>so the two tints and the half-heart uv trick keep
    /// working unchanged, and the red now fills INSIDE the user's own line.</b></para>
    /// <para>⛔ <b>Filled at the SOURCE's resolution, not the tile's.</b> The outline is about twelve
    /// pixels thick in a 160-pixel cell and a little over one in a 16-pixel tile; reduced first, its
    /// thinnest corners drop below the alpha threshold, the flood leaks out through the gap and the
    /// whole heart comes back empty. Reducing afterwards also lets the outline keep its
    /// anti-aliasing, which is what stops a 16-pixel heart looking like a staircase.</para>
    /// <para>⚠ <b>The cell is FOUND, not written down.</b> The first run of columns holding any ink
    /// is the first heart, so the sheet can be redrawn — more hearts, different spacing, a different
    /// size — without a constant here going stale. The measured pitch was already uneven (179 to 181
    /// pixels), which is what a hand-laid sheet looks like and what a hard-coded stride would clip.
    /// </para>
    /// </remarks>
    public static byte[]? HeartTile(int size)
    {
        if (size <= 0) return null;
        if (Hearts.TryGetValue(size, out var cached)) return cached;

        var built = BuildHeart(size);
        Hearts[size] = built;
        return built;
    }

    /// <summary>
    /// That the heart on the bar is the user's drawing, and that it has a middle to fill.
    /// </summary>
    /// <remarks>
    /// ⛔ <b><see cref="HeartTile"/> answers null on every failure and the caller quietly draws the
    /// generated heart instead.</b> That is the right behaviour and it is also invisible: a build
    /// whose embedded resource did not make it, or whose flood leaked out through a thin corner of
    /// the outline, looks exactly like a build that is working. Nothing else in the project would
    /// ever say so — the bar would simply be the old heart again.
    /// ⚠ <b>Both values are asserted, not just that a tile came back.</b> The whole point of the
    /// derivation is that the line and the middle are different, because that is what lets one tile
    /// serve the socket and the fill; a tile that is uniformly white passes "it exists" and gives a
    /// bar with no visible outline at all.
    /// </remarks>
    public static List<string> ValidateHeart(int size, out string detail)
    {
        var faults = new List<string>();
        detail = "";

        if (!Has(Heart))
        {
            faults.Add("this build carries no health.png, so the bar is drawing the generated heart");
            return faults;
        }

        if (HeartTile(size) is not { } tile)
        {
            faults.Add("health.png is carried but no heart could be derived from it — the flood "
                     + "found no middle, so the bar has fallen back to the generated heart");
            return faults;
        }

        int line = 0, middle = 0, clear = 0;

        for (var i = 0; i < size * size; i++)
        {
            var alpha = tile[i * 4 + 3];
            if (alpha < 128) { clear++; continue; }

            if (tile[i * 4] >= 216) middle++;
            else line++;
        }

        if (middle == 0)
            faults.Add("the heart has no middle, so a filled one and an empty one are the same picture");

        if (line == 0)
            faults.Add("the heart has no outline, so an empty socket has no edge to read");

        if (clear == 0)
            faults.Add($"the heart fills all {size * size} texels of its tile, which is a square");

        // ⛔ The middle has to be ENCLOSED, or the flood escaped and what is being called a middle is
        // the background. Walked down the centre column: outside, then line, then middle.
        var mid = size / 2;
        int firstLine = -1, firstMiddle = -1;

        for (var y = 0; y < size; y++)
        {
            var at = (y * size + mid) * 4;
            if (tile[at + 3] < 128) continue;

            if (tile[at] < 216 && firstLine < 0) firstLine = y;
            else if (tile[at] >= 216 && firstMiddle < 0) firstMiddle = y;
        }

        if (firstMiddle >= 0 && firstLine >= 0 && firstMiddle < firstLine)
            faults.Add($"down the middle of the heart the fill starts at row {firstMiddle} and the "
                     + $"outline at {firstLine}, so the fill is outside the line");

        // ── And the hunger bar's pair, which fails the same way and for the same reason ──────────
        //
        // ⛔ The two are drawn rather than derived, so what has to be proved is that they are two
        // DIFFERENT pictures. Both falling back to the generated drumstick gives a bar whose full
        // and empty states are the same tile under two tints — which looks like a working bar until
        // you notice it never appears to empty.
        var colours = 0;
        var socketInk = 0;
        var fullInk = 0;

        if (!Has(Food) || !Has(FoodSocket))
        {
            faults.Add("this build carries no foodbar.png/emptyfood.png, so the hunger bar is "
                     + "drawing the generated drumstick for both states");
        }
        else if (SheetTile(Food, size) is not { } meat || SheetTile(FoodSocket, size) is not { } socket)
        {
            faults.Add("the hunger bar's art is carried but no tile could be taken from it");
        }
        else
        {
            var seen = new HashSet<int>();

            for (var i = 0; i < size * size; i++)
            {
                if (meat[i * 4 + 3] >= 128)
                {
                    fullInk++;
                    seen.Add((meat[i * 4] << 16) | (meat[i * 4 + 1] << 8) | meat[i * 4 + 2]);
                }

                if (socket[i * 4 + 3] >= 128) socketInk++;
            }

            colours = seen.Count;

            // ⚠ The painted one is a drawing and the socket is a mask, so the first has many colours
            // and the second has almost one. Equal counts means both came from the same fallback.
            if (colours < 8)
                faults.Add($"the filled drumstick has {colours} colours in it, so it is a "
                         + "silhouette rather than the painting — the tint would be flattening it");

            if (socketInk == 0) faults.Add("the empty drumstick has no ink at all");
            if (fullInk == 0) faults.Add("the filled drumstick has no ink at all");

            // A hollow socket has to cover LESS than a solid painted one, or it is not hollow.
            if (socketInk >= fullInk)
                faults.Add($"the empty drumstick covers {socketInk} texels against the full one's "
                         + $"{fullInk}, so they are the same picture");
        }

        // ── And the bubble, which is the one that is MEANT to stay hollow ───────────────────────
        //
        // ⛔ The opposite claim from the heart's, and that is why it is asked. Both arrived as white
        // outlines; the heart had to have a middle flooded into it so the red has somewhere to go,
        // and a bubble must NOT — a filled one is a pearl. Nothing about the two paths makes that
        // difference visible except saying it out loud here.
        var bubbleInk = 0;
        var bubbleHollow = 0;
        var bubbleWhole = 0;

        if (!Has(Breath))
        {
            faults.Add("this build carries no bubbles.png, so breath is drawing the generated one");
        }
        else if (SheetTile(Breath, size, keepThinLines: true) is not { } bubble)
        {
            faults.Add("bubbles.png is carried but no tile could be taken from it");
        }
        else
        {
            // Walked across the middle row: rim, then a clear span, then rim. A disc has no gap.
            var row = size / 2;
            var seenInk = false;

            for (var x = 0; x < size; x++)
            {
                var at = (row * size + x) * 4;
                if (bubble[at + 3] >= 128) { bubbleInk++; seenInk = true; }
                else if (seenInk) bubbleHollow++;
            }

            for (var i = 0; i < size * size; i++)
                if (bubble[i * 4 + 3] >= 128) bubbleWhole++;

            if (bubbleInk == 0) faults.Add("the bubble has no ink across its middle");

            // ⛔ REPORTED BY THE USER: "we've got bubbles but they aren't displaying at all when i'm
            // under water." They were drawing — the wiring was right — and there was simply almost
            // nothing there to see. A ring one texel wide in pale blue over a water-tinted screen is
            // invisible, and every check here passed it because each was asking about the SHAPE of
            // the ring rather than about whether there is enough of it to read.
            // ⚠ Against the generated bubble it replaced, not against a number: "enough ink" has no
            // absolute value, and the old one is the thing a player was able to see.
            var generatedInk = 0;
            var generated = TileGen.Bubble();
            for (var i = 0; i < size * size && i * 4 + 3 < generated.Length; i++)
                if (generated[i * 4 + 3] >= 128) generatedInk++;

            if (bubbleWhole * 2 < generatedInk)
                faults.Add($"the painted bubble is {bubbleWhole} texels of ink against the generated "
                         + $"one's {generatedInk} — less than half as much on screen, which is a "
                         + "bubble a player cannot see");

            // ⛳ The fault carries the TILE, not just the claim. "It came out solid" is a sentence
            // somebody then has to go and reproduce; the sixteen rows say whether the ring closed up,
            // whether the shape is off centre, or whether this check is reading the wrong row.
            if (bubbleHollow == 0)
            {
                var shape = new System.Text.StringBuilder();
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                        shape.Append(bubble[(y * size + x) * 4 + 3] >= 128 ? '#' : '.');
                    shape.Append(' ');
                }

                faults.Add("the bubble is solid across its middle, so it has been filled in and "
                         + $"reads as a pearl rather than as air: {shape}");
            }
        }

        detail = $"the user's own heart at {size}px: {line} texels of outline round {middle} of "
               + $"fill, {clear} clear, line met first down the middle; their drumstick in "
               + $"{colours} colours over {fullInk} texels against a hollow socket of {socketInk}; "
               + $"and their bubble {bubbleWhole} texels of ink, still hollow, {bubbleInk} of rim with {bubbleHollow} clear "
               + "across the middle";

        return faults;
    }

    /// <summary>
    /// The first drawing on a sheet of them, at the size the array is being built at.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Every sheet in this project is one shape laid out several times</b> — seven hearts, seven
    /// drumsticks — because that is how they were drawn rather than because the game wants seven of
    /// anything. Taking the first and finding it by its ink means the sheets can be redrawn at a
    /// different size, spacing or count without a constant here going stale. Measured: the hearts sit
    /// at a pitch of 179 to 181 pixels, which is what a hand-laid row looks like and what any fixed
    /// stride would clip.
    /// </remarks>
    /// <param name="keepThinLines">
    /// Take the strongest alpha over each destination texel rather than the average of it.
    /// </param>
    /// <remarks>
    /// ⛔⛔ <b>A DRAWING MADE OF THIN LINES DISINTEGRATES UNDER AN AVERAGE, and the bubble is the
    /// proof.</b> Its rim is about five pixels in a hundred-and-twenty-two-pixel cell — a bit over
    /// half a texel once reduced to sixteen — so averaging alpha over each destination square left
    /// most of the ring below the cutout threshold and it came out as <em>scattered dots</em>, not as
    /// a circle. The heart survived the identical path only because its outline is twice as thick.
    /// <para>⛳ Taking the strongest alpha instead is the standard answer for reducing line art: a
    /// destination texel that any part of a line passes through keeps the line. It thickens slightly,
    /// which at this size is exactly what is wanted — the alternative is a ring with holes in it.</para>
    /// <para>⚠ Wrong for anything with a filled body: max-alpha over a drumstick would grow its
    /// silhouette by a texel all round. So it is asked for per sheet rather than applied to all of
    /// them, and only the bubble asks.</para>
    /// </remarks>
    public static byte[]? SheetTile(string name, int size, bool keepThinLines = false)
    {
        if (size <= 0) return null;
        if (Tiles.TryGetValue((name, size), out var cached)) return cached;

        if (Source(name) is not { } sheet) return null;
        if (FirstCell(sheet) is not { } cell) return null;

        var tile = Fit(Crop(sheet, cell), size, keepThinLines);
        Tiles[(name, size)] = tile;
        return tile;
    }

    /// <summary>The bounds of the first drawing on a sheet, or null when there is no ink at all.</summary>
    private static (int X0, int Y0, int X1, int Y1)? FirstCell(Image sheet)
    {
        int x0 = -1, x1 = -1;
        for (var x = 0; x < sheet.Width; x++)
        {
            var ink = false;
            for (var y = 0; y < sheet.Height && !ink; y++)
                ink = sheet.Pixels[(y * sheet.Width + x) * 4 + 3] > 8;

            if (ink && x0 < 0) x0 = x;
            else if (!ink && x0 >= 0) { x1 = x - 1; break; }
        }

        if (x0 < 0) return null;
        if (x1 < 0) x1 = sheet.Width - 1;

        int y0 = -1, y1 = -1;
        for (var y = 0; y < sheet.Height; y++)
        {
            var ink = false;
            for (var x = x0; x <= x1 && !ink; x++)
                ink = sheet.Pixels[(y * sheet.Width + x) * 4 + 3] > 8;

            if (!ink) continue;
            if (y0 < 0) y0 = y;
            y1 = y;
        }

        return y0 < 0 ? null : (x0, y0, x1, y1);
    }

    private static Image Crop(Image sheet, (int X0, int Y0, int X1, int Y1) cell)
    {
        var w = cell.X1 - cell.X0 + 1;
        var h = cell.Y1 - cell.Y0 + 1;
        var pixels = new byte[w * h * 4];

        for (var y = 0; y < h; y++)
            Array.Copy(
                sheet.Pixels, ((cell.Y0 + y) * sheet.Width + cell.X0) * 4,
                pixels, y * w * 4, w * 4);

        return new Image(w, h, pixels);
    }

    private static byte[]? BuildHeart(int size)
    {
        if (Source(Heart) is not { } sheet) return null;
        if (FirstCell(sheet) is not { } cell) return null;

        var (x0, y0, x1, y1) = cell;

        // ⚠ One clear row and column all round, so the flood below always has an outside to start
        // from even when the drawing runs right to the edge of its cell — which this one does.
        var w = x1 - x0 + 3;
        var h = y1 - y0 + 3;

        var line = new bool[w * h];
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
            line[(y - y0 + 1) * w + (x - x0 + 1)] = sheet.Pixels[(y * sheet.Width + x) * 4 + 3] > 96;

        // ── Flood the outside, four-connected. What is left is the middle of the heart. ──
        //
        // ⚠ Four-connected, not eight: an eight-connected flood squeezes diagonally between two
        // pixels that touch only at a corner, which is exactly what the notch at the top of a heart
        // is made of — and it would drain the fill out through the dip between the two lobes.
        var outside = new bool[w * h];
        var queue = new Queue<int>();

        outside[0] = true;
        queue.Enqueue(0);

        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            var ax = at % w;
            var ay = at / w;

            foreach (var (dx, dy) in (ReadOnlySpan<(int, int)>)[(1, 0), (-1, 0), (0, 1), (0, -1)])
            {
                int nx = ax + dx, ny = ay + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;

                var next = ny * w + nx;
                if (outside[next] || line[next]) continue;

                outside[next] = true;
                queue.Enqueue(next);
            }
        }

        // ── Paint it back out at source resolution: the line darker, the middle bright. ──
        var full = new byte[w * h * 4];
        var inside = 0;

        for (var i = 0; i < line.Length; i++)
        {
            byte value;

            if (line[i]) value = 176;
            else if (!outside[i]) { value = 255; inside++; }
            else continue;

            full[i * 4] = value;
            full[i * 4 + 1] = value;
            full[i * 4 + 2] = value;
            full[i * 4 + 3] = 255;
        }

        // ⛔ A heart with no middle is a flood that leaked, and it would ship as a bar that never
        // appears to fill. Falling back to the generated heart is the honest answer, and the texture
        // check says which one is being used.
        if (inside < line.Length / 20) return null;

        return Fit(new Image(w, h, full), size);
    }

    private static Image? Source(string name)
    {
        if (Loaded.TryGetValue(name, out var already)) return already;

        Image? image = null;

        using (var stream = typeof(PaintedArt).GetTypeInfo().Assembly.GetManifestResourceStream(name))
        {
            if (stream is not null)
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                if (Png.TryDecode(buffer.ToArray(), out var decoded, out _)) image = decoded;
            }
        }

        Loaded[name] = image;
        return image;
    }

    /// <summary>
    /// Scales an image into a square tile, keeping its proportions and centring what is left.
    /// </summary>
    /// <remarks>
    /// ⚠ The margin is left fully transparent rather than filled, so the tile is a cut-out and the
    /// panel behind a button shows through round the drawing — which is what makes it a picture ON
    /// the button rather than a smaller button inside it.
    /// </remarks>
    private static byte[] Fit(Image image, int size, bool keepThinLines = false)
    {
        var tile = new byte[size * size * 4];

        // The largest whole scale that fits, so the source's own pixels stay square. Below 1 the
        // drawing is being reduced and the ratio is fractional, which is the ordinary case for a
        // 532-pixel book in a 16-pixel tile.
        var scale = MathF.Min(size / (float)image.Width, size / (float)image.Height);

        var drawnW = MathF.Max(1f, image.Width * scale);
        var drawnH = MathF.Max(1f, image.Height * scale);

        var left = (size - drawnW) * 0.5f;
        var top = (size - drawnH) * 0.5f;

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            // The square of the source this destination pixel covers. When the drawing is being
            // reduced that is many source pixels; when it is being enlarged it is a fraction of one.
            var u0 = (x - left) / drawnW;
            var u1 = (x + 1f - left) / drawnW;
            var v0 = (y - top) / drawnH;
            var v1 = (y + 1f - top) / drawnH;

            if (u1 <= 0f || u0 >= 1f || v1 <= 0f || v0 >= 1f) continue;

            var sx0 = Math.Clamp((int)MathF.Floor(u0 * image.Width), 0, image.Width - 1);
            var sx1 = Math.Clamp((int)MathF.Ceiling(u1 * image.Width) - 1, sx0, image.Width - 1);
            var sy0 = Math.Clamp((int)MathF.Floor(v0 * image.Height), 0, image.Height - 1);
            var sy1 = Math.Clamp((int)MathF.Ceiling(v1 * image.Height) - 1, sy0, image.Height - 1);

            // ⛔ AVERAGED OVER THE WHOLE SQUARE, NOT SAMPLED AT ITS MIDDLE, and the difference is
            // the whole drawing at small sizes. A 532-pixel book in a 16-pixel tile is a reduction
            // of thirty-three to one: point sampling keeps one source pixel in eleven hundred and
            // throws the metal corner caps, the clasp and both bookmarks away entirely, which came
            // out as a brown rectangle with a pale stripe down it. ⚠ This is NOT the smoothing the
            // rest of the project refuses — that rule is about ENLARGING pixel art, where averaging
            // blurs hard edges that exist. Here the detail is real and being thrown away.
            //
            // ⚠ Weighted by alpha, or the transparent margin round the drawing drags its edge
            // toward whatever colour the empty pixels happen to carry.
            float r = 0f, g = 0f, b = 0f, a = 0f, weight = 0f, strongest = 0f;

            for (var sy = sy0; sy <= sy1; sy++)
            for (var sx = sx0; sx <= sx1; sx++)
            {
                var from = (sy * image.Width + sx) * 4;
                var alpha = image.Pixels[from + 3] / 255f;

                r += image.Pixels[from] * alpha;
                g += image.Pixels[from + 1] * alpha;
                b += image.Pixels[from + 2] * alpha;
                a += alpha;
                weight++;
                strongest = MathF.Max(strongest, alpha);
            }

            if (weight <= 0f || a <= 0f) continue;

            var to = (y * size + x) * 4;
            tile[to] = (byte)Math.Clamp((int)MathF.Round(r / a), 0, 255);
            tile[to + 1] = (byte)Math.Clamp((int)MathF.Round(g / a), 0, 255);
            tile[to + 2] = (byte)Math.Clamp((int)MathF.Round(b / a), 0, 255);

            // ⛔ The COLOUR is still the average and only the alpha changes, because a line's colour
            // is the same all along it and its coverage is the thing being destroyed. Taking the
            // strongest colour too would pick whichever pixel happened to be most opaque, which on an
            // anti-aliased edge is a coin toss between the line and the highlight beside it.
            tile[to + 3] = keepThinLines
                ? (byte)Math.Clamp((int)MathF.Round(strongest * 255f), 0, 255)
                : (byte)Math.Clamp((int)MathF.Round(a / weight * 255f), 0, 255);
        }

        return tile;
    }
}
