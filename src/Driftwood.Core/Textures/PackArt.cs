namespace Driftwood.Core.Textures;

/// <summary>
/// A pack's picture prepared for a wide card: its screenshot cropped to 16:9, or its square icon
/// set in the middle of a backdrop of its own colour.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Stored square, shown wide.</b> The interface keeps its pictures in square texture
/// layers, so a 16:9 picture is stretched into the square and drawn back into a 16:9 card, which
/// undoes the stretch exactly. A square icon is squeezed by the inverse amount for the same reason,
/// so on the card it is square again.</para>
/// <para>Shrunk by averaging every source pixel that lands in a target pixel, not by picking one:
/// a screenshot sampled point by point at a quarter of its size shimmers with aliasing.</para>
/// </remarks>
public static class PackArt
{
    /// <summary>Wider than this is a screenshot to crop; narrower is an icon to frame.</summary>
    private const float ScreenshotAspect = 1.2f;

    /// <summary>Returns a size×size RGBA layer for a 16:9 card, or null when the bytes are not a usable PNG.</summary>
    public static byte[]? Card(byte[]? encoded, int size)
    {
        if (encoded is not { Length: > 0 }
            || !Png.TryReadDimensions(encoded, out var width, out var height, out _)
            || width <= 0 || height <= 0 || width > 8192 || height > 8192 || (long)width * height > 16_777_216
            || !Png.TryDecode(encoded, out var image, out _))
            return null;

        var layer = new byte[size * size * 4];

        if (width >= height * ScreenshotAspect)
        {
            // Cover: the widest 16:9 window of the picture, centred.
            var cropHeight = Math.Min(height, (int)(width * 9f / 16f));
            var cropWidth = Math.Min(width, (int)(cropHeight * 16f / 9f));
            var x0 = (width - cropWidth) / 2;
            var y0 = (height - cropHeight) / 2;
            Resample(image, x0, y0, cropWidth, cropHeight, layer, size, 0, 0, size, size);
            return layer;
        }

        // An icon: its average colour, darkened, behind it; the icon a full card tall and as wide as
        // a square of that height is once the card is drawn 16:9.
        var (r, g, b) = Average(image);
        for (var i = 0; i < size * size; i++)
        {
            layer[i * 4] = (byte)(r * 0.35f);
            layer[i * 4 + 1] = (byte)(g * 0.35f);
            layer[i * 4 + 2] = (byte)(b * 0.35f);
            layer[i * 4 + 3] = 255;
        }

        var inset = size / 12;
        var iconWide = (int)MathF.Round((size - 2 * inset) * 9f / 16f);
        Resample(image, 0, 0, width, height, layer, size, (size - iconWide) / 2, inset, iconWide, size - 2 * inset);
        return layer;
    }

    private static (float R, float G, float B) Average(Image image)
    {
        double r = 0, g = 0, b = 0, weight = 0;
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            var a = image.Pixels[i * 4 + 3] / 255.0;
            r += image.Pixels[i * 4] * a;
            g += image.Pixels[i * 4 + 1] * a;
            b += image.Pixels[i * 4 + 2] * a;
            weight += a;
        }
        return weight <= 0 ? (40f, 44f, 48f) : ((float)(r / weight), (float)(g / weight), (float)(b / weight));
    }

    /// <summary>Area-averages a source rectangle into a target rectangle, alpha-blending over what is there.</summary>
    private static void Resample(
        Image image, int sx, int sy, int sw, int sh,
        byte[] target, int targetSize, int tx, int ty, int tw, int th)
    {
        for (var y = 0; y < th; y++)
        {
            var y0 = sy + (int)((long)y * sh / th);
            var y1 = Math.Max(y0 + 1, sy + (int)((long)(y + 1) * sh / th));
            for (var x = 0; x < tw; x++)
            {
                var x0 = sx + (int)((long)x * sw / tw);
                var x1 = Math.Max(x0 + 1, sx + (int)((long)(x + 1) * sw / tw));

                double r = 0, g = 0, b = 0, a = 0;
                var count = 0;
                for (var yy = y0; yy < Math.Min(y1, image.Height); yy++)
                for (var xx = x0; xx < Math.Min(x1, image.Width); xx++)
                {
                    var i = (yy * image.Width + xx) * 4;
                    var alpha = image.Pixels[i + 3];
                    r += image.Pixels[i] * alpha;
                    g += image.Pixels[i + 1] * alpha;
                    b += image.Pixels[i + 2] * alpha;
                    a += alpha;
                    count++;
                }
                if (count == 0 || a <= 0) continue;

                var to = ((ty + y) * targetSize + tx + x) * 4;
                var coverage = (float)(a / count / 255.0);
                var under = 1f - coverage;
                target[to] = (byte)Math.Clamp(r / a * coverage + target[to] * under, 0, 255);
                target[to + 1] = (byte)Math.Clamp(g / a * coverage + target[to + 1] * under, 0, 255);
                target[to + 2] = (byte)Math.Clamp(b / a * coverage + target[to + 2] * under, 0, 255);
                target[to + 3] = (byte)Math.Max(target[to + 3], (int)(coverage * 255));
            }
        }
    }
}
