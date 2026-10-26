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
}
