namespace Driftwood.Core.Textures;

/// <summary>
/// A 256x256 colour lookup indexed by temperature and rainfall — how grass and foliage get their
/// colour from the climate they grow in.
/// </summary>
/// <remarks>
/// <para>The layout matches the one texture packs are painted against, because a pack's colormap
/// has to be able to replace ours. Temperature runs from 1 at the left edge to 0 at the right, and
/// rainfall from 1 at the top to 0 at the bottom — so the hot, wet corner is bottom-left and the
/// origin of the whole scheme is the <em>bottom-right</em>. Only the lower-left triangle is ever
/// sampled: rainfall is bounded by temperature in practice, and the upper-right half of the image
/// stands for climates that do not occur. A lookup landing outside is clamped rather than wrapped.
/// </para>
/// <para>Driftwood generates its own, so the world is coloured with or without a pack. Ours is a
/// simple bilinear blend between four corner colours, which is enough to put olive grass in a dry
/// warm region and blue-green in a cold wet one.</para>
/// </remarks>
public static class Colormap
{
    public const int Size = 256;

    /// <summary>Grass colours: dry-cold, dry-hot, wet-cold, wet-hot.</summary>
    public static byte[] Grass() => Build(
        coldDry: (0x8A, 0xB6, 0x8A),
        hotDry: (0xBF, 0xB7, 0x55),
        coldWet: (0x6D, 0xA3, 0x6D),
        hotWet: (0x59, 0xC9, 0x3C));

    /// <summary>Foliage runs darker than grass, the way a canopy does against a field.</summary>
    public static byte[] Foliage() => Build(
        coldDry: (0x6B, 0x94, 0x6B),
        hotDry: (0xA0, 0x99, 0x40),
        coldWet: (0x4E, 0x7E, 0x4E),
        hotWet: (0x3E, 0xA6, 0x28));

    private static byte[] Build(
        (int R, int G, int B) coldDry,
        (int R, int G, int B) hotDry,
        (int R, int G, int B) coldWet,
        (int R, int G, int B) hotWet)
    {
        var map = new byte[Size * Size * 4];

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // x runs from hot on the left to cold on the right, y from wet at the top to dry at
            // the bottom — the axes the pack format uses, not the ones that read naturally.
            var temperature = 1f - x / (Size - 1f);
            var downfall = 1f - y / (Size - 1f);

            var dry = Lerp(coldDry, hotDry, temperature);
            var wet = Lerp(coldWet, hotWet, temperature);
            var (r, g, b) = Lerp(dry, wet, downfall);

            var i = (y * Size + x) * 4;
            map[i] = (byte)r;
            map[i + 1] = (byte)g;
            map[i + 2] = (byte)b;
            map[i + 3] = 255;
        }

        return map;
    }

    private static (int R, int G, int B) Lerp((int R, int G, int B) a, (int R, int G, int B) b, float t) =>
        ((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    /// <summary>
    /// Looks a colour up the way the format specifies: temperature and rainfall to a pixel.
    /// </summary>
    public static (byte R, byte G, byte B) Sample(byte[] map, float temperature, float downfall)
    {
        temperature = Math.Clamp(temperature, 0f, 1f);

        // Rainfall is scaled by temperature before it is used, which is what confines every real
        // lookup to the lower-left triangle of the image. Skip it and cold wet climates read a
        // corner of the map no pack has ever painted.
        downfall = Math.Clamp(downfall, 0f, 1f) * temperature;

        var x = (int)((1f - temperature) * (Size - 1));
        var y = (int)((1f - downfall) * (Size - 1));

        var i = (Math.Clamp(y, 0, Size - 1) * Size + Math.Clamp(x, 0, Size - 1)) * 4;
        return (map[i], map[i + 1], map[i + 2]);
    }
}
