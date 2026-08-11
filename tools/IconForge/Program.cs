using System.Buffers.Binary;
using Driftwood.Core.Textures;

namespace Driftwood.Tools.IconForge;

/// <summary>
/// Turns supplied source artwork into the compact derivatives the repository actually ships: a
/// Windows icon, a web-sized banner, and the borderless nineteen-spell atlas.
/// </summary>
/// <remarks>
/// <para>A build tool rather than a step in the game's build. The originals are large or carry
/// source layout, and their derivatives are committed — so this runs when art changes and never
/// otherwise.</para>
/// <para>Written in C# against our own PNG codec rather than as a shell script, which is a scar:
/// a hand-rolled icon writer using PowerShell's BinaryWriter silently emitted a short final frame,
/// the Win32 resource loader rejected the whole file, and MSBuild reported zero errors while the
/// executable quietly kept the stock .NET icon.</para>
/// </remarks>
public static class Program
{
    /// <summary>
    /// Where the raft sits inside the supplied logo, which also carries a wordmark underneath.
    /// </summary>
    /// <remarks>
    /// The wordmark is unreadable below about 128 pixels and would only muddy the small sizes, so
    /// the icon is the raft alone. The crop is by coordinate because the artwork's background is a
    /// painted scene rather than transparency — there is no alpha edge to find it by.
    /// </remarks>
    private const int RaftX = 145;
    private const int RaftY = 0;
    private const int RaftWidth = 620;
    private const int RaftHeight = 400;

    /// <summary>
    /// Sizes Windows actually asks for. 16 and 32 are the taskbar and title bar, 48 is the desktop,
    /// 256 is the large view in Explorer; the rest stop Windows from scaling one of those badly.
    /// </summary>
    private static readonly int[] Sizes = [16, 24, 32, 48, 64, 128, 256];

    /// <summary>Readme banner width. Wide enough for a full-width GitHub page, small enough to commit.</summary>
    private const int BannerWidth = 1280;

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--spell-icons") return BuildSpellIcons(args);

        var root = args.Length > 0 ? args[0] : FindRepositoryRoot();
        var assets = Path.Combine(root, "assets");

        var iconSource = Path.Combine(assets, "driftwood icon.png");
        var bannerSource = Path.Combine(assets, "driftwood banner.png");

        foreach (var path in (ReadOnlySpan<string>)[iconSource, bannerSource])
        {
            if (File.Exists(path)) continue;
            Console.Error.WriteLine($"iconforge: no artwork at '{path}'");
            return 1;
        }

        var logo = Decode(iconSource);
        var square = PadToSquare(Crop(logo, RaftX, RaftY, RaftWidth, RaftHeight));

        // An icon is the one output nobody can check by reading a byte count. --preview writes the
        // squared crop somewhere harmless so it can be looked at before it is embedded in anything.
        var preview = Array.IndexOf(args, "--preview");
        if (preview >= 0 && preview + 1 < args.Length)
        {
            File.WriteAllBytes(args[preview + 1], Png.Encode(square));
            Console.WriteLine($"wrote preview {args[preview + 1]}");
        }

        var icoPath = Path.Combine(assets, "driftwood.ico");
        File.WriteAllBytes(icoPath, BuildIcon(square, Sizes));
        Console.WriteLine($"wrote {icoPath} — {Sizes.Length} frames at {string.Join(", ", Sizes)}");

        // The same artwork again as plain PNG, for the window itself.
        //
        // Setting <ApplicationIcon> alone is not enough on Windows: it dresses the file in Explorer,
        // but the taskbar button belongs to the *window*, and a GLFW window is created without one.
        // These get embedded in the client assembly and handed to the window at startup, which is
        // what actually puts the raft on the taskbar. Two sizes, because Windows asks for a small
        // icon and a large one and scaling one badly is exactly the artefact this avoids.
        foreach (var size in (ReadOnlySpan<int>)[32, 64])
        {
            var path = Path.Combine(assets, $"window-icon-{size}.png");
            File.WriteAllBytes(path, Png.Encode(Resample(square, size, size)));
            Console.WriteLine($"wrote {path}");
        }

        var banner = Decode(bannerSource);
        var scaled = Resample(banner, BannerWidth, (int)Math.Round(banner.Height * (double)BannerWidth / banner.Width));
        var bannerPath = Path.Combine(assets, "banner.png");
        File.WriteAllBytes(bannerPath, Png.Encode(scaled));
        Console.WriteLine($"wrote {bannerPath} — {scaled.Width}x{scaled.Height}, "
                        + $"{new FileInfo(bannerPath).Length / 1024:N0} KiB from {new FileInfo(bannerSource).Length / 1024:N0} KiB");

        return Verify(icoPath, Sizes) ? 0 : 1;
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "assets"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static Image Decode(string path)
    {
        if (Png.TryDecode(File.ReadAllBytes(path), out var image, out var error)) return image;
        throw new InvalidDataException($"{Path.GetFileName(path)}: {error}");
    }

    /// <summary>
    /// Derives the compact runtime atlas from the user's local labelled spell sheet.
    /// </summary>
    /// <remarks>
    /// ⛳ The source is original Driftwood art, but it is still source-layout material: large,
    /// framed and lettered. Only the nineteen reviewed picture wells belong in the executable. The
    /// crop coordinates live in <see cref="SpellIconAtlas.Definitions"/>, which is also the runtime
    /// spell ordering, so the derivation cannot quietly swap two pictures.
    /// </remarks>
    private static int BuildSpellIcons(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("iconforge: --spell-icons needs <source.png> <output.png>");
            return 1;
        }

        var source = Decode(args[1]);
        if (source.Width != SpellIconAtlas.OriginalSourceWidth
            || source.Height != SpellIconAtlas.OriginalSourceHeight)
        {
            Console.Error.WriteLine(
                $"iconforge: spell source is {source.Width}×{source.Height}, wanted "
                + $"{SpellIconAtlas.OriginalSourceWidth}×{SpellIconAtlas.OriginalSourceHeight}");
            return 1;
        }

        var pixels = new byte[SpellIconAtlas.AtlasWidth * SpellIconAtlas.AtlasHeight * 4];
        var definitions = SpellIconAtlas.Definitions;

        for (var i = 0; i < definitions.Length; i++)
        {
            var crop = definitions[i];
            if (crop.SourceX < 0 || crop.SourceY < 0
                || crop.SourceX + SpellIconAtlas.CropSize > source.Width
                || crop.SourceY + SpellIconAtlas.CropSize > source.Height)
            {
                Console.Error.WriteLine($"iconforge: '{crop.TextureName}' runs outside the source");
                return 1;
            }

            var dx = i % SpellIconAtlas.Columns * SpellIconAtlas.CropSize;
            var dy = i / SpellIconAtlas.Columns * SpellIconAtlas.CropSize;
            for (var y = 0; y < SpellIconAtlas.CropSize; y++)
            {
                var from = ((crop.SourceY + y) * source.Width + crop.SourceX) * 4;
                var to = ((dy + y) * SpellIconAtlas.AtlasWidth + dx) * 4;
                Array.Copy(source.Pixels, from, pixels, to, SpellIconAtlas.CropSize * 4);
            }
        }

        var output = new Image(SpellIconAtlas.AtlasWidth, SpellIconAtlas.AtlasHeight, pixels);
        File.WriteAllBytes(args[2], Png.Encode(output));
        Console.WriteLine(
            $"wrote {args[2]} — {definitions.Length} clean {SpellIconAtlas.CropSize}px spell wells, "
            + $"{output.Width}×{output.Height}");
        return 0;
    }

    private static Image Crop(Image source, int x, int y, int width, int height)
    {
        var pixels = new byte[width * height * 4];

        for (var row = 0; row < height; row++)
        {
            var sy = Math.Clamp(y + row, 0, source.Height - 1);
            for (var col = 0; col < width; col++)
            {
                var sx = Math.Clamp(x + col, 0, source.Width - 1);
                Array.Copy(source.Pixels, (sy * source.Width + sx) * 4, pixels, (row * width + col) * 4, 4);
            }
        }

        return new Image(width, height, pixels);
    }

    /// <summary>
    /// Squares a wide crop by extending it with the background colour above and below.
    /// </summary>
    /// <remarks>
    /// The obvious approach — repeating the edge rows — was tried first and is wrong here. The
    /// mast crosses the top edge and the raft's shadow crosses the bottom, so replicating those
    /// rows smeared both into black bars running off the top and bottom of the icon. Sampling a
    /// single colour from well inside the margin, away from anything the raft occupies, gives clean
    /// sky above and clean water below instead.
    /// </remarks>
    private static Image PadToSquare(Image source)
    {
        var side = Math.Max(source.Width, source.Height);
        if (side == source.Height && side == source.Width) return source;

        var pixels = new byte[side * side * 4];
        var top = (side - source.Height) / 2;

        // One colour for both margins, taken an eighth in along the top edge — the raft is centred,
        // so that lands on open sky. Sampling the bottom edge separately was tried and picked up
        // the raft's own outline, which put a black band under it; and since the artwork is sky
        // above and sea below, one blue reads as either.
        var sky = source.Width / 8 * 4;

        for (var y = 0; y < side; y++)
        {
            var dst = y * side * 4;

            if (y < top || y >= top + source.Height)
            {
                for (var x = 0; x < side; x++) Array.Copy(source.Pixels, sky, pixels, dst + x * 4, 4);
                continue;
            }

            Array.Copy(source.Pixels, (y - top) * source.Width * 4, pixels, dst, source.Width * 4);
        }

        return new Image(side, side, pixels);
    }

    /// <summary>
    /// Box-filtered downscale, averaging in premultiplied alpha.
    /// </summary>
    /// <remarks>
    /// Premultiplying matters wherever the artwork has soft edges: averaging straight RGBA lets the
    /// colour of fully transparent pixels bleed into their neighbours, which shows up as a dark
    /// fringe around everything once the icon is 16 pixels across.
    /// </remarks>
    private static Image Resample(Image source, int width, int height)
    {
        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            var y0 = y * source.Height / height;
            var y1 = Math.Max(y0 + 1, (y + 1) * source.Height / height);

            for (var x = 0; x < width; x++)
            {
                var x0 = x * source.Width / width;
                var x1 = Math.Max(x0 + 1, (x + 1) * source.Width / width);

                long r = 0, g = 0, b = 0, a = 0;
                var n = 0;

                for (var sy = y0; sy < y1; sy++)
                for (var sx = x0; sx < x1; sx++)
                {
                    var i = (sy * source.Width + sx) * 4;
                    var alpha = source.Pixels[i + 3];

                    r += source.Pixels[i] * alpha;
                    g += source.Pixels[i + 1] * alpha;
                    b += source.Pixels[i + 2] * alpha;
                    a += alpha;
                    n++;
                }

                var o = (y * width + x) * 4;
                if (a == 0)
                {
                    pixels[o] = pixels[o + 1] = pixels[o + 2] = pixels[o + 3] = 0;
                    continue;
                }

                pixels[o] = (byte)(r / a);
                pixels[o + 1] = (byte)(g / a);
                pixels[o + 2] = (byte)(b / a);
                pixels[o + 3] = (byte)(a / n);
            }
        }

        return new Image(width, height, pixels);
    }

    /// <summary>
    /// Writes a Windows .ico holding one uncompressed 32-bit frame per size.
    /// </summary>
    /// <remarks>
    /// Uncompressed DIB at every size, including 256. PNG-compressed frames inside an ico are only
    /// reliably rendered at 256 and are the classic cause of a blank taskbar icon at the small
    /// sizes. Rows run bottom-up, and each frame carries the legacy 1-bit AND mask after its colour
    /// data — modern Windows shades from the alpha channel, but the mask is part of the structure
    /// and a frame without one has the wrong declared length.
    /// </remarks>
    private static byte[] BuildIcon(Image square, int[] sizes)
    {
        var frames = new List<byte[]>(sizes.Length);
        foreach (var size in sizes) frames.Add(BuildFrame(Resample(square, size, size)));

        using var output = new MemoryStream();
        var word = new byte[2];
        var dword = new byte[4];

        void Word(int v) { BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)v); output.Write(word); }
        void Dword(int v) { BinaryPrimitives.WriteUInt32LittleEndian(dword, (uint)v); output.Write(dword); }

        Word(0);              // reserved
        Word(1);              // type: icon
        Word(sizes.Length);

        var offset = 6 + sizes.Length * 16;
        for (var i = 0; i < sizes.Length; i++)
        {
            output.WriteByte((byte)(sizes[i] >= 256 ? 0 : sizes[i]));   // 0 means 256
            output.WriteByte((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            output.WriteByte(0);   // palette size
            output.WriteByte(0);   // reserved
            Word(1);               // colour planes
            Word(32);              // bits per pixel
            Dword(frames[i].Length);
            Dword(offset);
            offset += frames[i].Length;
        }

        foreach (var frame in frames) output.Write(frame);
        return output.ToArray();
    }

    private static byte[] BuildFrame(Image image)
    {
        var colourBytes = image.Width * image.Height * 4;
        var maskStride = (image.Width + 31) / 32 * 4;
        var maskBytes = maskStride * image.Height;

        using var output = new MemoryStream();
        var dword = new byte[4];
        var word = new byte[2];

        void Word(int v) { BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)v); output.Write(word); }
        void Dword(int v) { BinaryPrimitives.WriteUInt32LittleEndian(dword, (uint)v); output.Write(dword); }

        Dword(40);                        // BITMAPINFOHEADER size
        Dword(image.Width);
        Dword(image.Height * 2);          // colour rows plus mask rows
        Word(1);                          // planes
        Word(32);                         // bits per pixel
        Dword(0);                         // BI_RGB, uncompressed
        Dword(colourBytes + maskBytes);
        Dword(0); Dword(0); Dword(0); Dword(0);

        // Colour data, bottom row first, as BGRA.
        for (var y = image.Height - 1; y >= 0; y--)
        for (var x = 0; x < image.Width; x++)
        {
            var i = (y * image.Width + x) * 4;
            output.WriteByte(image.Pixels[i + 2]);
            output.WriteByte(image.Pixels[i + 1]);
            output.WriteByte(image.Pixels[i]);
            output.WriteByte(image.Pixels[i + 3]);
        }

        // AND mask, also bottom-up: a set bit means "leave the background showing".
        var maskRow = new byte[maskStride];
        for (var y = image.Height - 1; y >= 0; y--)
        {
            Array.Clear(maskRow);
            for (var x = 0; x < image.Width; x++)
            {
                if (image.Pixels[(y * image.Width + x) * 4 + 3] >= 128) continue;
                maskRow[x / 8] |= (byte)(0x80 >> (x % 8));
            }

            output.Write(maskRow);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Checks the file the way the Win32 resource loader will.
    /// </summary>
    /// <remarks>
    /// The failure this is here for is a frame table that declares more bytes than the file holds.
    /// Windows rejects the entire icon for that, MSBuild embeds it anyway without complaint, and
    /// the executable ends up wearing the stock .NET apphost icon. The last frame's offset plus its
    /// length must equal the file size exactly.
    /// </remarks>
    private static bool Verify(string path, int[] sizes)
    {
        var bytes = File.ReadAllBytes(path);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));

        if (count != sizes.Length)
        {
            Console.Error.WriteLine($"iconforge: header declares {count} frames, wrote {sizes.Length}");
            return false;
        }

        var end = 0L;
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry + 8));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry + 12));

            if (offset + length > (uint)bytes.Length)
            {
                Console.Error.WriteLine($"iconforge: frame {i} runs to {offset + length} past the {bytes.Length}-byte file");
                return false;
            }

            end = Math.Max(end, offset + length);
        }

        if (end != bytes.Length)
        {
            Console.Error.WriteLine($"iconforge: frames end at {end}, file is {bytes.Length} bytes");
            return false;
        }

        Console.WriteLine($"verified {path} — {bytes.Length:N0} bytes, frame table ends exactly at EOF");
        return true;
    }
}
