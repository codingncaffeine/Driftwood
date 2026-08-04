namespace Driftwood.Core.Lighting;

/// <summary>
/// One cell's light, packed into a <see cref="ushort"/>: four bits of sunlight and four bits each
/// of red, green and blue block light.
/// </summary>
/// <remarks>
/// <para>Sunlight is kept apart from block light rather than summed into it. They have to be, and
/// not for storage reasons: sunlight is scaled by the time of day at draw time, block light is not.
/// Fusing them lights every cave with the sun at noon and blacks out every torch at midnight.</para>
/// <para>Block light is coloured. That costs three nibbles instead of one and doubles a chunk's
/// memory alongside the block array, which is the price of an ember glow reading orange against a
/// blue-grey cave instead of everything being a different brightness of the same white.</para>
/// <para>Sixteen levels is the genre's convention and it is not arbitrary: light falls one level
/// per block, so the maximum range of a source is fifteen blocks. Choosing a wider range would
/// widen every relight's blast radius by the same amount.</para>
/// </remarks>
public static class LightValue
{
    public const int Max = 15;

    private const int SkyShift = 0;
    private const int RedShift = 4;
    private const int GreenShift = 8;
    private const int BlueShift = 12;

    public const ushort SkyMask = 0x000F;
    public const ushort BlockMask = 0xFFF0;

    public static int Sky(ushort packed) => (packed >> SkyShift) & 0xF;
    public static int Red(ushort packed) => (packed >> RedShift) & 0xF;
    public static int Green(ushort packed) => (packed >> GreenShift) & 0xF;
    public static int Blue(ushort packed) => (packed >> BlueShift) & 0xF;

    public static ushort WithSky(ushort packed, int sky) =>
        (ushort)((packed & ~SkyMask) | ((sky & 0xF) << SkyShift));

    public static ushort Pack(int sky, int red, int green, int blue) => (ushort)(
        ((sky & 0xF) << SkyShift) |
        ((red & 0xF) << RedShift) |
        ((green & 0xF) << GreenShift) |
        ((blue & 0xF) << BlueShift));

    public static ushort PackBlock(int red, int green, int blue) => Pack(0, red, green, blue);

    /// <summary>The brightest of the three block channels — what "is this cell lit" means.</summary>
    public static int BlockPeak(ushort packed)
    {
        var r = Red(packed);
        var g = Green(packed);
        var b = Blue(packed);
        return Math.Max(r, Math.Max(g, b));
    }

    /// <summary>Channel-wise maximum, which is how two light sources combine.</summary>
    public static ushort MaxBlock(ushort a, ushort b) => PackBlock(
        Math.Max(Red(a), Red(b)),
        Math.Max(Green(a), Green(b)),
        Math.Max(Blue(a), Blue(b)));
}
