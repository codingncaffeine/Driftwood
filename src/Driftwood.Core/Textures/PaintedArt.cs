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

    private static readonly Dictionary<string, Image?> Loaded = [];
    private static readonly Dictionary<(string, int), byte[]> Tiles = [];

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
    private static byte[] Fit(Image image, int size)
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
            float r = 0f, g = 0f, b = 0f, a = 0f, weight = 0f;

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
            }

            if (weight <= 0f || a <= 0f) continue;

            var to = (y * size + x) * 4;
            tile[to] = (byte)Math.Clamp((int)MathF.Round(r / a), 0, 255);
            tile[to + 1] = (byte)Math.Clamp((int)MathF.Round(g / a), 0, 255);
            tile[to + 2] = (byte)Math.Clamp((int)MathF.Round(b / a), 0, 255);
            tile[to + 3] = (byte)Math.Clamp((int)MathF.Round(a / weight * 255f), 0, 255);
        }

        return tile;
    }
}
