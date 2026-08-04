using System.Buffers.Binary;
using System.IO.Compression;

namespace Driftwood.Core.Textures;

/// <summary>A decoded image: 8-bit RGBA, top row first.</summary>
public sealed record Image(int Width, int Height, byte[] Pixels);

/// <summary>
/// Reads PNG files into RGBA byte arrays.
/// </summary>
/// <remarks>
/// <para>Written rather than taken from a package. The decoding itself is a few hundred lines
/// because .NET already ships the hard part — <see cref="ZLibStream"/> — and the alternative was a
/// dependency whose licence terms we would have to keep answering for in a project that reads
/// other people's texture packs for a living. Nothing here is clever; it is the PNG specification
/// with the interesting cases spelled out.</para>
/// <para>Handles the colour types a texture pack actually contains: 8- and 16-bit greyscale, RGB,
/// palette, greyscale+alpha and RGBA. Interlaced images are rejected by name rather than decoded
/// wrongly — Adam7 is rare in block art and a silently mangled texture is worse than a reported
/// one.</para>
/// </remarks>
public static class Png
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool TryDecode(ReadOnlySpan<byte> data, out Image image, out string error)
    {
        image = null!;
        error = string.Empty;

        if (data.Length < 8 || !data[..8].SequenceEqual(Signature))
        {
            error = "not a PNG";
            return false;
        }

        int width = 0, height = 0, bitDepth = 0, colourType = 0;
        byte[]? palette = null;
        byte[]? paletteAlpha = null;
        var idat = new MemoryStream();
        var offset = 8;

        while (offset + 8 <= data.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            var type = System.Text.Encoding.ASCII.GetString(data.Slice(offset + 4, 4));
            var body = offset + 8;

            if (length < 0 || body + length > data.Length)
            {
                error = $"chunk '{type}' runs past the end of the file";
                return false;
            }

            switch (type)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(data[body..]);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(body + 4)..]);
                    bitDepth = data[body + 8];
                    colourType = data[body + 9];
                    if (data[body + 12] != 0)
                    {
                        error = "interlaced PNGs are not supported";
                        return false;
                    }
                    break;

                case "PLTE":
                    palette = data.Slice(body, length).ToArray();
                    break;

                case "tRNS":
                    paletteAlpha = data.Slice(body, length).ToArray();
                    break;

                case "IDAT":
                    idat.Write(data.Slice(body, length));
                    break;

                case "IEND":
                    offset = data.Length;
                    break;
            }

            offset = body + length + 4;   // skip the trailing CRC
        }

        if (width <= 0 || height <= 0)
        {
            error = "missing or empty IHDR";
            return false;
        }

        var channels = colourType switch
        {
            0 => 1,   // greyscale
            2 => 3,   // rgb
            3 => 1,   // palette index
            4 => 2,   // greyscale + alpha
            6 => 4,   // rgba
            _ => 0,
        };

        if (channels == 0)
        {
            error = $"unsupported colour type {colourType}";
            return false;
        }

        if (bitDepth is not (8 or 16) && !(colourType == 3 && bitDepth is 1 or 2 or 4))
        {
            error = $"unsupported bit depth {bitDepth} for colour type {colourType}";
            return false;
        }

        idat.Position = 0;
        byte[] raw;
        try
        {
            using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflate.CopyTo(output);
            raw = output.ToArray();
        }
        catch (Exception ex)
        {
            error = $"inflate failed: {ex.Message}";
            return false;
        }

        var bitsPerPixel = channels * bitDepth;
        var bytesPerRow = (width * bitsPerPixel + 7) / 8;
        var filterStride = Math.Max(1, bitsPerPixel / 8);

        if (raw.Length < (bytesPerRow + 1) * (long)height)
        {
            error = $"pixel data is short: {raw.Length} bytes for {height} rows of {bytesPerRow}";
            return false;
        }

        Unfilter(raw, width, height, bytesPerRow, filterStride);

        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var row = y * (bytesPerRow + 1) + 1;
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = ReadPixel(raw, row, x, colourType, bitDepth, channels, palette, paletteAlpha);
                var i = (y * width + x) * 4;
                pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = a;
            }
        }

        image = new Image(width, height, pixels);
        return true;
    }

    /// <summary>
    /// Reverses the per-scanline filters in place. Every row's filter refers to the row above it,
    /// so this has to run top to bottom before anything reads a pixel.
    /// </summary>
    private static void Unfilter(byte[] raw, int width, int height, int bytesPerRow, int stride)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * (bytesPerRow + 1);
            var filter = raw[row];
            var data = row + 1;
            var prior = data - (bytesPerRow + 1);

            for (var i = 0; i < bytesPerRow; i++)
            {
                byte a = i >= stride ? raw[data + i - stride] : (byte)0;   // left
                byte b = y > 0 ? raw[prior + i] : (byte)0;                 // above
                byte c = y > 0 && i >= stride ? raw[prior + i - stride] : (byte)0;   // above-left

                raw[data + i] = filter switch
                {
                    0 => raw[data + i],
                    1 => (byte)(raw[data + i] + a),
                    2 => (byte)(raw[data + i] + b),
                    3 => (byte)(raw[data + i] + (a + b) / 2),
                    4 => (byte)(raw[data + i] + Paeth(a, b, c)),
                    _ => raw[data + i],
                };
            }
        }
    }

    /// <summary>The PNG predictor: whichever of left, above, above-left is closest to their sum.</summary>
    private static byte Paeth(byte a, byte b, byte c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static (byte R, byte G, byte B, byte A) ReadPixel(
        byte[] raw, int row, int x, int colourType, int bitDepth, int channels,
        byte[]? palette, byte[]? paletteAlpha)
    {
        if (colourType == 3)
        {
            var index = ReadIndex(raw, row, x, bitDepth);
            if (palette is null || index * 3 + 2 >= palette.Length) return (255, 0, 255, 255);

            var alpha = paletteAlpha is not null && index < paletteAlpha.Length ? paletteAlpha[index] : (byte)255;
            return (palette[index * 3], palette[index * 3 + 1], palette[index * 3 + 2], alpha);
        }

        // 16-bit samples are taken by their high byte. Block art is 8-bit in practice, and
        // discarding the low byte is a far better answer than refusing the file.
        var step = bitDepth / 8;
        var at = row + x * channels * step;

        byte Sample(int channel) => raw[at + channel * step];

        return colourType switch
        {
            0 => (Sample(0), Sample(0), Sample(0), 255),
            2 => (Sample(0), Sample(1), Sample(2), 255),
            4 => (Sample(0), Sample(0), Sample(0), Sample(1)),
            _ => (Sample(0), Sample(1), Sample(2), Sample(3)),
        };
    }

    /// <summary>Palette indices can be packed several to a byte at low bit depths.</summary>
    private static int ReadIndex(byte[] raw, int row, int x, int bitDepth)
    {
        if (bitDepth == 8) return raw[row + x];

        var perByte = 8 / bitDepth;
        var b = raw[row + x / perByte];
        var shift = 8 - bitDepth * (x % perByte + 1);
        return (b >> shift) & ((1 << bitDepth) - 1);
    }
}
