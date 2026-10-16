using Driftwood.Core.Entities;

namespace Driftwood.Core.Textures;

/// <summary>
/// A skin seen from the front, flat, as a small picture for a gallery card.
/// </summary>
/// <remarks>
/// <para>⛳ <b>Why a picture rather than a name.</b> Skins arrive from files and feeds with names
/// like a hash, a number or nothing at all, and the only thing that tells two of them apart at a
/// glance is how they look. So every card shows the figure itself.</para>
/// <para>Cut straight from the sheet, front faces only: head, body, both arms and both legs, each
/// with its outer layer laid over it, arranged 16 pixels wide by 32 tall — the proportions every
/// skin is drawn to. Slim arms are three pixels wide and are moved in by one so the figure stays
/// centred. An HD sheet is sampled at its own scale, one sample per figure pixel.</para>
/// </remarks>
public static class SkinThumbnail
{
    /// <summary>The edge of the square each figure is placed in.</summary>
    public const int Size = 32;

    /// <summary>Returns <see cref="Size"/>×<see cref="Size"/> RGBA, transparent around the figure.</summary>
    public static byte[] FrontFigure(PlayerSkinData skin)
    {
        var tile = new byte[Size * Size * 4];
        var scale = Math.Max(1, skin.Size / PlayerModel.SheetSize);
        var slim = skin.Arms == ArmStyle.Slim;
        var arm = slim ? 3 : 4;

        // (sheet x, sheet y, width, height, overlay x, overlay y, figure x, figure y), all in
        // sheet pixels; the figure sits eight in from the left of the square.
        ReadOnlySpan<(int X, int Y, int W, int H, int OX, int OY, int FX, int FY)> parts =
        [
            (8, 8, 8, 8, 40, 8, 4, 0),                 // head, hat
            (20, 20, 8, 12, 20, 36, 4, 8),             // body, jacket
            (44, 20, arm, 12, 44, 36, 4 - arm, 8),     // the figure's right arm, on our left
            (36, 52, arm, 12, 52, 52, 12, 8),          // its left arm
            (4, 20, 4, 12, 4, 36, 4, 20),              // right leg
            (20, 52, 4, 12, 4, 52, 8, 20),             // left leg
        ];

        foreach (var part in parts)
        {
            Copy(skin, scale, tile, part.X, part.Y, part.W, part.H, part.FX, part.FY, over: false);
            Copy(skin, scale, tile, part.OX, part.OY, part.W, part.H, part.FX, part.FY, over: true);
        }

        return tile;
    }

    private static void Copy(
        PlayerSkinData skin, int scale, byte[] tile, int sx, int sy, int w, int h, int fx, int fy, bool over)
    {
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var px = (sx + x) * scale;
            var py = (sy + y) * scale;
            if (px >= skin.Size || py >= skin.Size) continue;

            var from = (py * skin.Size + px) * 4;
            var alpha = skin.Pixels[from + 3];
            if (alpha == 0) continue;

            var tx = 8 + fx + x;
            var ty = fy + y;
            if (tx < 0 || tx >= Size || ty < 0 || ty >= Size) continue;

            var to = (ty * Size + tx) * 4;
            if (!over || alpha == 255)
            {
                tile[to] = skin.Pixels[from];
                tile[to + 1] = skin.Pixels[from + 1];
                tile[to + 2] = skin.Pixels[from + 2];
                tile[to + 3] = over ? (byte)255 : alpha;
                continue;
            }

            // A part-transparent overlay pixel blends over the base layer under it.
            var a = alpha / 255f;
            for (var c = 0; c < 3; c++)
                tile[to + c] = (byte)MathF.Round(skin.Pixels[from + c] * a + tile[to + c] * (1f - a));
            tile[to + 3] = (byte)Math.Max(tile[to + 3], alpha);
        }
    }
}
