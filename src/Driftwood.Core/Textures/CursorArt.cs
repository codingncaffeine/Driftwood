using System.Reflection;

namespace Driftwood.Core.Textures;

/// <summary>
/// The pointers a player can pick (2026-10-26): the original graphite arrow, and the user's own
/// pixel-art arrows in four colours.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The user's drawings, cut from the sheets they supplied.</b> Each arrow is thirty by
/// thirty-nine art pixels, read off the sheet's own grid one cell at a time and keyed off its black
/// ground; the tip is the top-left texel, which is where the pointer points.</para>
/// <para>⚠ They are larger than every other overlay sprite, so they live in a texture of their own
/// at <see cref="Tile"/> square rather than squeezed into the sixteen-pixel icon array.</para>
/// </remarks>
public static class CursorArt
{
    /// <summary>The side of each cursor's tile: the arrow in its top-left corner, the rest clear.</summary>
    public const int Tile = 40;

    /// <summary>The art's own size inside its tile.</summary>
    public const int Width = 30;
    public const int Height = 39;

    /// <summary>
    /// Where the art's painted sparkles begin: every texel right of <see cref="SparkleLeft"/> and
    /// below <see cref="SparkleTop"/> is dust, not arrow. The live trail hangs from here.
    /// </summary>
    public const int SparkleLeft = 22;
    public const int SparkleTop = 33;

    /// <summary>Every choice, in the order the setting cycles them. The first is the original.</summary>
    public static readonly string[] Names = ["classic", "blue", "red", "green", "purple"];

    /// <summary>The one a new installation starts with.</summary>
    public const string Default = "blue";

    /// <summary>Which choice a saved name is, or the default's when it is none of them.</summary>
    public static int IndexOf(string name)
    {
        var at = Array.IndexOf(Names, name);
        return at >= 0 ? at : Array.IndexOf(Names, Default);
    }

    /// <summary>
    /// The drawn arrows as <see cref="Tile"/>-square RGBA tiles, one per name after "classic" —
    /// the classic arrow is the icon array's own and is not repeated here.
    /// </summary>
    public static byte[][] Tiles()
    {
        var tiles = new byte[Names.Length - 1][];
        for (var i = 1; i < Names.Length; i++)
        {
            var name = $"Driftwood.Core.cursor-{Names[i]}.png";
            using var stream = typeof(CursorArt).GetTypeInfo().Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"the cursor {name} is not embedded");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            if (!Png.TryDecode(memory.ToArray(), out var image, out var error))
                throw new InvalidOperationException($"the cursor {name} did not decode: {error}");

            var tile = new byte[Tile * Tile * 4];
            for (var y = 0; y < Math.Min(Tile, image.Height); y++)
            for (var x = 0; x < Math.Min(Tile, image.Width); x++)
                Array.Copy(image.Pixels, (y * image.Width + x) * 4, tile, (y * Tile + x) * 4, 4);
            tiles[i - 1] = tile;
        }

        return tiles;
    }

    /// <summary>A copy of <paramref name="tile"/> with its painted sparkles cleared, for the live trail.</summary>
    public static byte[] WithoutSparkles(byte[] tile)
    {
        var bare = (byte[])tile.Clone();
        for (var y = SparkleTop; y < Tile; y++)
        for (var x = SparkleLeft; x < Tile; x++)
            bare[(y * Tile + x) * 4 + 3] = 0;
        return bare;
    }

    /// <summary>The colours of <paramref name="tile"/>'s painted sparkles, 0–1, in reading order.</summary>
    public static (float R, float G, float B)[] SparkleColours(byte[] tile)
    {
        var colours = new List<(float, float, float)>();
        for (var y = SparkleTop; y < Tile; y++)
        for (var x = SparkleLeft; x < Tile; x++)
        {
            var at = (y * Tile + x) * 4;
            if (tile[at + 3] == 0) continue;
            colours.Add((tile[at] / 255f, tile[at + 1] / 255f, tile[at + 2] / 255f));
        }

        return colours.Count == 0 ? [(1f, 1f, 1f)] : [.. colours];
    }

    /// <summary>
    /// <paramref name="tile"/> resampled to <paramref name="size"/> square by area: each new texel
    /// averages the art pixels it covers, weighted by how much of each, with alpha premultiplied so
    /// clear texels don't darken an edge.
    /// </summary>
    public static byte[] Resample(byte[] tile, int size)
    {
        var scaled = new byte[size * size * 4];
        var step = Tile / (float)size;
        for (var oy = 0; oy < size; oy++)
        for (var ox = 0; ox < size; ox++)
        {
            float r = 0, g = 0, b = 0, a = 0;
            var y0 = oy * step;
            var x0 = ox * step;
            for (var sy = (int)y0; sy < Math.Min(Tile, (int)MathF.Ceiling(y0 + step)); sy++)
            {
                var wy = Math.Min(sy + 1, y0 + step) - Math.Max(sy, y0);
                for (var sx = (int)x0; sx < Math.Min(Tile, (int)MathF.Ceiling(x0 + step)); sx++)
                {
                    var w = wy * (Math.Min(sx + 1, x0 + step) - Math.Max(sx, x0));
                    if (w <= 0f) continue;
                    var at = (sy * Tile + sx) * 4;
                    var alpha = tile[at + 3] / 255f * w;
                    r += tile[at] * alpha;
                    g += tile[at + 1] * alpha;
                    b += tile[at + 2] * alpha;
                    a += alpha;
                }
            }

            var to = (oy * size + ox) * 4;
            var area = step * step;
            if (a <= 0f) continue;
            scaled[to] = (byte)Math.Clamp(MathF.Round(r / a), 0, 255);
            scaled[to + 1] = (byte)Math.Clamp(MathF.Round(g / a), 0, 255);
            scaled[to + 2] = (byte)Math.Clamp(MathF.Round(b / a), 0, 255);
            scaled[to + 3] = (byte)Math.Clamp(MathF.Round(a / area * 255f), 0, 255);
        }

        return scaled;
    }
}
