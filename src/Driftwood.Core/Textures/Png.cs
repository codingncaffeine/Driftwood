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

    /// <summary>
    /// Writes an 8-bit RGBA image. The plainest PNG the format allows: no interlacing, no palette,
    /// no filtering.
    /// </summary>
    /// <remarks>
    /// Filter type 0 on every scanline. Choosing filters per row is where most of a PNG encoder's
    /// size win lives, and none of it is worth having here — this exists to derive project art and
    /// to save screenshots, not to compete with an optimiser. Deflate does the real work and .NET
    /// already ships it.
    /// </remarks>
    public static byte[] Encode(Image image)
    {
        var raw = new byte[(image.Width * 4 + 1) * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            var src = y * image.Width * 4;
            var dst = y * (image.Width * 4 + 1);
            raw[dst] = 0;   // filter: none
            Array.Copy(image.Pixels, src, raw, dst + 1, image.Width * 4);
        }

        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var deflate = new ZLibStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
                deflate.Write(raw, 0, raw.Length);

            compressed = buffer.ToArray();
        }

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)image.Height);
        header[8] = 8;    // bit depth
        header[9] = 6;    // colour type: RGBA
        header[10] = 0;   // deflate
        header[11] = 0;   // adaptive filtering
        header[12] = 0;   // no interlace

        using var output = new MemoryStream();
        output.Write(Signature);
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] body)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)body.Length);
        stream.Write(length);

        var typed = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typed);
        stream.Write(body);

        var crc = Crc32(typed, body);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
        stream.Write(tail);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in body) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    /// <summary>Reads only the mandatory first chunk, before any compressed bytes are expanded.</summary>
    public static bool TryReadDimensions(
        ReadOnlySpan<byte> data, out int width, out int height, out string error)
    {
        width = height = 0;
        error = "";
        if (data.Length < 33 || !data[..8].SequenceEqual(Signature))
        {
            error = "not a PNG";
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        if (length != 13 || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            error = "the first PNG chunk is not a 13-byte IHDR";
            return false;
        }

        var wide = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
        var high = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
        if (wide is 0 or > int.MaxValue || high is 0 or > int.MaxValue)
        {
            error = "the PNG dimensions are empty or too large";
            return false;
        }

        width = (int)wide;
        height = (int)high;
        return true;
    }

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

        var ended = false;
        while (offset + 12 <= data.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            var typed = data.Slice(offset + 4, 4);
            var type = System.Text.Encoding.ASCII.GetString(typed);
            var body = offset + 8;

            if (offset == 8 && type != "IHDR")
            {
                error = "the first PNG chunk is not IHDR";
                return false;
            }

            if (length < 0 || body + (long)length + 4 > data.Length)
            {
                error = $"chunk '{type}' runs past the end of the file";
                return false;
            }

            var storedCrc = BinaryPrimitives.ReadUInt32BigEndian(data[(body + length)..]);
            if (Crc32(typed, data.Slice(body, length)) != storedCrc)
            {
                error = $"chunk '{type}' has a bad checksum";
                return false;
            }

            switch (type)
            {
                case "IHDR":
                    if (width != 0 || height != 0)
                    {
                        error = "PNG has more than one IHDR";
                        return false;
                    }
                    if (length != 13)
                    {
                        error = "IHDR is not 13 bytes";
                        return false;
                    }
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
                    if (length != 0)
                    {
                        error = "IEND is not empty";
                        return false;
                    }
                    ended = true;
                    break;
            }

            offset = body + length + 4;   // skip the trailing CRC
            if (ended) break;
        }

        if (width <= 0 || height <= 0)
        {
            error = "missing or empty IHDR";
            return false;
        }
        if (!ended)
        {
            error = "missing IEND";
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

        var bitsPerPixel = channels * bitDepth;
        var bytesPerRowLong = ((long)width * bitsPerPixel + 7) / 8;
        var expectedRaw = (bytesPerRowLong + 1) * height;
        var expectedPixels = (long)width * height * 4;
        const long MaximumDecodedBytes = 512L * 1024 * 1024;
        if (bytesPerRowLong > int.MaxValue
            || expectedRaw <= 0 || expectedRaw > MaximumDecodedBytes
            || expectedPixels <= 0 || expectedPixels > MaximumDecodedBytes)
        {
            error = "decoded PNG is larger than 512 MiB";
            return false;
        }

        var bytesPerRow = (int)bytesPerRowLong;
        var filterStride = Math.Max(1, bitsPerPixel / 8);

        idat.Position = 0;
        byte[] raw;
        try
        {
            using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
            using var output = new MemoryStream((int)expectedRaw);
            var buffer = new byte[8192];
            while (true)
            {
                var read = inflate.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (output.Length + read > expectedRaw)
                {
                    error = "inflated pixel data is longer than the declared image";
                    return false;
                }
                output.Write(buffer, 0, read);
            }
            raw = output.ToArray();
        }
        catch (Exception ex)
        {
            error = $"inflate failed: {ex.Message}";
            return false;
        }

        if (raw.Length < expectedRaw)
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
