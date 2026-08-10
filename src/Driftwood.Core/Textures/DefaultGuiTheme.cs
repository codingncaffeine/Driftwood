namespace Driftwood.Core.Textures;

/// <summary>
/// Driftwood's original graphite-and-pewter pixel theme for standard menu chrome.
/// </summary>
/// <remarks>
/// These are first-party fallback sprites, not copies of a resource pack. They deliberately use
/// the same standard source dimensions that packs paint — a 16px repeating surface, 200x20
/// controls, 130x24 tabs and a 100px tooltip panel — so a sparse pack replaces any one of them
/// without changing layout or leaving the rest of the interface as bare rectangles.
/// </remarks>
public static class DefaultGuiTheme
{
    public sealed record Result(byte[][] Tiles, bool[] Present, int Painted);

    public static Result Build(int layers)
    {
        var tiles = new byte[layers][];
        var present = new bool[layers];
        for (var i = 0; i < layers; i++)
            tiles[i] = new byte[GuiTextureSet.Size * GuiTextureSet.Size * 4];

        Add(GuiTextureSet.Layer.MenuBackground, Surface(16, 48, 13));
        Add(GuiTextureSet.Layer.MenuListBackground, Surface(16, 42, 31));
        Add(GuiTextureSet.Layer.OptionsBackground, Surface(16, 54, 47));

        Add(GuiTextureSet.Layer.WidgetButton, Control(200, 20, 78, raised: true, seed: 59));
        Add(GuiTextureSet.Layer.WidgetButtonHighlighted, Control(200, 20, 92, raised: false, seed: 71));
        Add(GuiTextureSet.Layer.WidgetButtonDisabled, Control(200, 20, 58, raised: false, seed: 83));

        Add(GuiTextureSet.Layer.TextField, Field(200, 20, focused: false));
        Add(GuiTextureSet.Layer.TextFieldHighlighted, Field(200, 20, focused: true));

        Add(GuiTextureSet.Layer.Tab, Tab(130, 24, selected: false, highlighted: false));
        Add(GuiTextureSet.Layer.TabHighlighted, Tab(130, 24, selected: false, highlighted: true));
        Add(GuiTextureSet.Layer.TabSelected, Tab(130, 24, selected: true, highlighted: false));
        Add(GuiTextureSet.Layer.TabSelectedHighlighted, Tab(130, 24, selected: true, highlighted: true));

        Add(GuiTextureSet.Layer.TooltipBackground, Tooltip(100));
        return new Result(tiles, present, present.Count(value => value));

        void Add(GuiTextureSet.Layer layer, byte[] source)
        {
            var at = (int)layer;
            if (at < 0 || at >= tiles.Length) return;
            tiles[at] = source;
            present[at] = true;
        }
    }

    private static byte[] Surface(int size, int baseTone, int seed)
    {
        var pixels = Canvas(size, size, baseTone);

        // Quiet horizontal graphite grain. It is deterministic and low contrast so icons and text
        // remain the most detailed things on the surface.
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var noise = ((x * 17 + y * 29 + x * y * 3 + seed) % 9) - 4;
            var stripe = y % 5 == 3 ? -4 : 0;
            Pixel(pixels, size, x, y, Tone(baseTone + noise + stripe));
        }

        HLine(pixels, size, 1, 1, size - 3, Tone(baseTone + 12));
        HLine(pixels, size, 5, 7, 6, Tone(baseTone - 11));
        HLine(pixels, size, 11, 2, 7, Tone(baseTone + 7));
        Pixel(pixels, size, 14, 14, Tone(baseTone - 14));
        return Resample(pixels, size, size);
    }

    private static byte[] Control(int width, int height, int fill, bool raised, int seed)
    {
        var pixels = Canvas(width, height, fill);
        Frame(pixels, width, height, fill, raised);

        // Directional wear and short pewter veins make a long control feel machined rather than
        // like a featureless slab. Mirrored end brackets survive nine-slice stretching.
        for (var x = 8; x < width - 8; x += 23)
        {
            var y = 5 + (x * 7 + seed) % 8;
            HLine(pixels, width, y, x, Math.Min(9, width - x - 4),
                Tone(fill + (x % 2 == 0 ? 8 : -8)));
        }

        CornerBrackets(pixels, width, height, Tone(fill + 26), Tone(fill - 28));
        return Resample(pixels, width, height);
    }

    private static byte[] Field(int width, int height, bool focused)
    {
        var fill = focused ? 31 : 25;
        var pixels = Canvas(width, height, fill);
        Frame(pixels, width, height, fill, raised: false);
        HLine(pixels, width, height - 4, 7, width - 14, Tone(focused ? 58 : 43));
        HLine(pixels, width, 4, 9, 19, Tone(focused ? 68 : 49));
        CornerBrackets(pixels, width, height, Tone(focused ? 104 : 77), Tone(13));
        return Resample(pixels, width, height);
    }

    private static byte[] Tab(int width, int height, bool selected, bool highlighted)
    {
        var fill = selected ? 76 : highlighted ? 72 : 61;
        var pixels = Canvas(width, height, fill);
        Frame(pixels, width, height, fill, raised: !selected);

        // A selected tab visually joins the surface below it. The mint state is added by the
        // renderer, keeping theme texture greyscale and selection semantic.
        if (selected) HLine(pixels, width, height - 2, 3, width - 6, Tone(fill));
        HLine(pixels, width, 5, 9, 24, Tone(fill + 13));
        HLine(pixels, width, height - 6, width - 34, 24, Tone(fill - 14));
        CornerBrackets(pixels, width, height, Tone(fill + 30), Tone(fill - 31));
        return Resample(pixels, width, height);
    }

    private static byte[] Tooltip(int size)
    {
        var pixels = Canvas(size, size, 26);
        Frame(pixels, size, size, 26, raised: false);
        for (var y = 8; y < size - 8; y += 13)
            HLine(pixels, size, y, 7 + y % 11,
                Math.Min(29, size - 15 - y % 11), Tone(y % 2 == 0 ? 34 : 20));
        CornerBrackets(pixels, size, size, Tone(92), Tone(10));
        return Resample(pixels, size, size);
    }

    private static byte[] Canvas(int width, int height, int fill)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++) Pixel(pixels, width, x, y, Tone(fill));
        return pixels;
    }

    private static void Frame(byte[] pixels, int width, int height, int fill, bool raised)
    {
        var light = Tone(fill + 48);
        var glint = Tone(fill + 25);
        var shade = Tone(fill - 31);
        var deep = Tone(fill - 45);
        var top = raised ? light : deep;
        var bottom = raised ? deep : light;
        var innerTop = raised ? glint : shade;
        var innerBottom = raised ? shade : glint;

        HLine(pixels, width, 0, 0, width, top);
        VLine(pixels, width, height, 0, 0, height, top);
        HLine(pixels, width, height - 1, 0, width, bottom);
        VLine(pixels, width, height, width - 1, 0, height, bottom);
        HLine(pixels, width, 2, 2, width - 4, innerTop);
        VLine(pixels, width, height, 2, 2, height - 4, innerTop);
        HLine(pixels, width, height - 3, 2, width - 4, innerBottom);
        VLine(pixels, width, height, width - 3, 2, height - 4, innerBottom);

        Pixel(pixels, width, 0, height - 1, Tone(fill));
        Pixel(pixels, width, width - 1, 0, Tone(fill));
    }

    private static void CornerBrackets(byte[] pixels, int width, int height, uint light, uint dark)
    {
        HLine(pixels, width, 4, 5, 7, light);
        VLine(pixels, width, height, 5, 4, 5, light);
        HLine(pixels, width, height - 5, width - 12, 7, dark);
        VLine(pixels, width, height, width - 6, height - 9, 5, dark);
    }

    private static void HLine(byte[] pixels, int width, int y, int x, int length, uint colour)
    {
        if (y < 0 || y >= pixels.Length / (width * 4)) return;
        for (var at = Math.Max(0, x); at < Math.Min(width, x + length); at++)
            Pixel(pixels, width, at, y, colour);
    }

    private static void VLine(
        byte[] pixels, int width, int height, int x, int y, int length, uint colour)
    {
        if (x < 0 || x >= width) return;
        for (var at = Math.Max(0, y); at < Math.Min(height, y + length); at++)
            Pixel(pixels, width, x, at, colour);
    }

    private static void Pixel(byte[] pixels, int width, int x, int y, uint colour)
    {
        if (x < 0 || y < 0 || x >= width || (y * width + x) * 4 + 3 >= pixels.Length) return;
        var at = (y * width + x) * 4;
        pixels[at] = (byte)(colour >> 24);
        pixels[at + 1] = (byte)(colour >> 16);
        pixels[at + 2] = (byte)(colour >> 8);
        pixels[at + 3] = (byte)colour;
    }

    private static uint Tone(int value)
    {
        var v = (uint)Math.Clamp(value, 0, 255);
        return v << 24 | v << 16 | v << 8 | 0xFFu;
    }

    private static byte[] Resample(byte[] source, int width, int height)
    {
        var size = GuiTextureSet.Size;
        var tile = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var from = (Math.Min(y * height / size, height - 1) * width
                + Math.Min(x * width / size, width - 1)) * 4;
            var to = (y * size + x) * 4;
            source.AsSpan(from, 4).CopyTo(tile.AsSpan(to, 4));
        }
        return tile;
    }
}
