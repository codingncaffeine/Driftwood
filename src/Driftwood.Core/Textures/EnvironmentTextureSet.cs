namespace Driftwood.Core.Textures;

/// <summary>Pack-aware sun, moon, rain and snow art with complete generated fallbacks.</summary>
public static class EnvironmentTextureSet
{
    public sealed record Result(
        Image Sun, Image Moon, Image Rain, Image Snow,
        bool SunFromPack, bool MoonFromPack, bool RainFromPack, bool SnowFromPack,
        IReadOnlyList<string> Sources)
    {
        public int FromPack => (SunFromPack ? 1 : 0) + (MoonFromPack ? 1 : 0)
                              + (RainFromPack ? 1 : 0) + (SnowFromPack ? 1 : 0);
        public string Summary => $"{FromPack} of 4 sky/weather sprites from the pack";
    }

    public static Result Build(string? packPath, int size = 64)
    {
        if (size < 8 || size > 1024) throw new ArgumentOutOfRangeException(nameof(size));
        using var pack = string.IsNullOrWhiteSpace(packPath) ? null : TexturePack.Open(packPath);
        var sources = new List<string>();

        var sun = Load(pack, size, ["textures/environment/sun.png"], Sun(size), sources, out var sunPack);
        var moon = LoadMoon(pack, size, Moon(size), sources, out var moonPack);
        var rain = Load(pack, size,
            ["textures/environment/rain.png", "textures/weather/rain.png"],
            Rain(size), sources, out var rainPack);
        var snow = Load(pack, size,
            ["textures/environment/snow.png", "textures/weather/snow.png"],
            Snow(size), sources, out var snowPack);
        return new Result(sun, moon, rain, snow, sunPack, moonPack, rainPack, snowPack, sources);
    }

    private static Image LoadMoon(
        TexturePack? pack, int size, Image fallback, List<string> sources, out bool supplied)
    {
        if (pack is not null)
        {
            var single = pack.TryLoadTile("textures/environment/moon.png", size, out var from);
            if (single is not null)
            {
                supplied = true;
                sources.Add(from);
                return new Image(size, size, single);
            }

            var sheet = pack.TryLoadSheet("textures/environment/moon_phases.png", out from);
            if (sheet is not null)
            {
                // The standard sheet is four phases across by two down. The moon shader wants one
                // disc, not the entire sheet squeezed into it, so phase zero is cropped before it
                // is resized. Odd hand-made square sheets remain one image rather than being cut.
                var cellWidth = sheet.Width >= sheet.Height * 2 ? sheet.Width / 4 : sheet.Width;
                var cellHeight = sheet.Height >= cellWidth * 2 ? sheet.Height / 2 : Math.Min(sheet.Height, cellWidth);
                supplied = true;
                if (!sources.Contains(from, StringComparer.OrdinalIgnoreCase)) sources.Add(from);
                return new Image(size, size, CropResize(sheet, cellWidth, cellHeight, size));
            }
        }
        supplied = false;
        return fallback;
    }

    private static byte[] CropResize(Image image, int sourceWidth, int sourceHeight, int size)
    {
        var pixels = new byte[checked(size * size * 4)];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var sx = Math.Clamp(x * sourceWidth / size, 0, image.Width - 1);
            var sy = Math.Clamp(y * sourceHeight / size, 0, image.Height - 1);
            var from = (sy * image.Width + sx) * 4;
            var to = (y * size + x) * 4;
            image.Pixels.AsSpan(from, 4).CopyTo(pixels.AsSpan(to, 4));
        }
        return pixels;
    }

    private static Image Load(
        TexturePack? pack, int size, IReadOnlyList<string> candidates, Image fallback,
        List<string> sources, out bool supplied)
    {
        if (pack is not null)
        {
            foreach (var candidate in candidates)
            {
                var pixels = pack.TryLoadTile(candidate, size, out var from);
                if (pixels is null) continue;
                supplied = true;
                if (!sources.Contains(from, StringComparer.OrdinalIgnoreCase)) sources.Add(from);
                return new Image(size, size, pixels);
            }
        }
        supplied = false;
        return fallback;
    }

    private static Image Sun(int size)
    {
        var pixels = Empty(size);
        var centre = (size - 1) * 0.5f;
        var radius = size * 0.32f;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var distance = MathF.Sqrt(MathF.Pow(x - centre, 2) + MathF.Pow(y - centre, 2));
            var alpha = Math.Clamp((radius + 1.5f - distance) / 1.5f, 0f, 1f);
            Set(pixels, size, x, y, 255, (byte)(235 + 20 * alpha), (byte)(174 + 55 * alpha), alpha);
        }
        return new Image(size, size, pixels);
    }

    private static Image Moon(int size)
    {
        var pixels = Empty(size);
        var centre = (size - 1) * 0.5f;
        var radius = size * 0.31f;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = x - centre;
            var dy = y - centre;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            var alpha = Math.Clamp((radius + 1.2f - distance) / 1.2f, 0f, 1f);
            var crater = ((x * 17 + y * 31 + x * y * 3) & 15) < 3 ? 22 : 0;
            Set(pixels, size, x, y,
                (byte)(220 - crater), (byte)(227 - crater), (byte)(242 - crater), alpha);
        }
        return new Image(size, size, pixels);
    }

    private static Image Rain(int size)
    {
        var pixels = Empty(size);
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var centre = size * 0.5f + (y - size * 0.5f) * 0.12f;
            var distance = MathF.Abs(x - centre);
            var alpha = Math.Clamp(1f - distance / Math.Max(1f, size * 0.055f), 0f, 1f)
                        * MathF.Sin(MathF.PI * (y + 1) / (size + 1));
            Set(pixels, size, x, y, 170, 205, 235, alpha * 0.85f);
        }
        return new Image(size, size, pixels);
    }

    private static Image Snow(int size)
    {
        var pixels = Empty(size);
        var centre = (size - 1) * 0.5f;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = MathF.Abs(x - centre);
            var dy = MathF.Abs(y - centre);
            var spoke = MathF.Min(MathF.Min(dx, dy), MathF.Abs(dx - dy) * 0.71f);
            var envelope = Math.Clamp(1f - MathF.Max(dx, dy) / (size * 0.38f), 0f, 1f);
            var alpha = Math.Clamp(1f - spoke / Math.Max(1f, size * 0.035f), 0f, 1f) * envelope;
            Set(pixels, size, x, y, 238, 246, 255, alpha);
        }
        return new Image(size, size, pixels);
    }

    private static byte[] Empty(int size) => new byte[checked(size * size * 4)];

    private static void Set(byte[] pixels, int size, int x, int y, byte r, byte g, byte b, float alpha)
    {
        var p = (y * size + x) * 4;
        pixels[p] = r;
        pixels[p + 1] = g;
        pixels[p + 2] = b;
        pixels[p + 3] = (byte)Math.Clamp((int)(alpha * 255f), 0, 255);
    }

    public static IReadOnlyList<string> SelfTest(out string detail)
    {
        var faults = new List<string>();
        var result = Build(null, 32);
        foreach (var (name, image) in new[]
                 {
                     ("sun", result.Sun), ("moon", result.Moon),
                     ("rain", result.Rain), ("snow", result.Snow),
                 })
        {
            if (image.Width != 32 || image.Height != 32 || image.Pixels.Length != 32 * 32 * 4)
                faults.Add($"the fallback {name} sprite has the wrong dimensions");
            if (!image.Pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0))
                faults.Add($"the fallback {name} sprite is transparent");
        }

        var root = Path.Combine(Path.GetTempPath(), $"driftwood-environment-{Environment.ProcessId}");
        try
        {
            var environment = Path.Combine(root, "assets", "minecraft", "textures", "environment");
            Directory.CreateDirectory(environment);
            File.WriteAllText(Path.Combine(root, "pack.mcmeta"), "{\"pack\":{\"pack_format\":34}}");
            File.WriteAllBytes(Path.Combine(environment, "sun.png"), Solid(2, 244, 80, 30));
            File.WriteAllBytes(Path.Combine(environment, "rain.png"), Solid(2, 30, 90, 220));
            File.WriteAllBytes(Path.Combine(environment, "snow.png"), Solid(2, 210, 225, 250));

            // Four by two phases. Only the red first phase should reach the runtime moon sprite.
            var phases = new byte[8 * 4 * 4];
            for (var y = 0; y < 4; y++)
            for (var x = 0; x < 8; x++)
            {
                var p = (y * 8 + x) * 4;
                phases[p] = x < 2 && y < 2 ? (byte)230 : (byte)20;
                phases[p + 1] = 20;
                phases[p + 2] = x < 2 && y < 2 ? (byte)30 : (byte)230;
                phases[p + 3] = 255;
            }
            File.WriteAllBytes(Path.Combine(environment, "moon_phases.png"),
                Png.Encode(new Image(8, 4, phases)));

            var packed = Build(root, 16);
            if (packed.FromPack != 4) faults.Add($"a Java environment pack supplied {packed.FromPack} of 4 sprites");
            if (packed.Moon.Pixels[0] < 200 || packed.Moon.Pixels[2] > 60)
                faults.Add("the moon renderer squeezed the whole phase sheet instead of cropping phase zero");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            faults.Add($"could not build an environment pack: {error.Message}");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }

        detail = "sun, moon, rain and snow have generated fallbacks; a Java pack replaces all four and its moon sheet is cropped";
        return faults;

        static byte[] Solid(int size, byte r, byte g, byte b)
        {
            var pixels = new byte[size * size * 4];
            for (var p = 0; p < pixels.Length; p += 4)
            {
                pixels[p] = r;
                pixels[p + 1] = g;
                pixels[p + 2] = b;
                pixels[p + 3] = 255;
            }
            return Png.Encode(new Image(size, size, pixels));
        }
    }
}
