using Driftwood.Core.Entities;

namespace Driftwood.Core.Textures;

/// <summary>A skin sheet ready to upload: square, RGBA, top row first.</summary>
/// <param name="Size">Edge in pixels. Always a multiple of 64; a sheet may be drawn at any scale.</param>
/// <param name="Legacy">
/// True when the sheet came in at 64×32 and has no left limbs of its own. The model answers this
/// by pointing a mirrored left arm and leg at the right ones' patches.
/// </param>
public sealed record PlayerSkinData(byte[] Pixels, int Size, ArmStyle Arms, bool Legacy, string Summary);

/// <summary>
/// Driftwood's own skin, and the loader for one the player already owns.
/// </summary>
/// <remarks>
/// <para>The default is drawn in code for the same reason the block tiles are: it is unambiguously
/// ours, it ships with the game, and a build with no art folder still has a player in it. It also
/// means the model is never being tested against a blank texture, which hides every UV mistake
/// there is.</para>
/// <para>Loading follows the same posture as texture packs — read a file the player already has,
/// never bundle or redistribute one. The sheet format is a format, and reading it is interop.</para>
/// </remarks>
public static class PlayerSkin
{
    private const int Sheet = PlayerModel.SheetSize;

    /// <summary>
    /// Builds the sheet to use: the player's if they named one, ours otherwise.
    /// </summary>
    /// <param name="forcedArms">
    /// Set to override arm width instead of detecting it. Auto-detection reads the sheet, and a
    /// sheet can be drawn ambiguously; an explicit answer is always available rather than the
    /// player having to repaint pixels to be believed.
    /// </param>
    public static PlayerSkinData Build(string? path, ArmStyle? forcedArms)
    {
        if (string.IsNullOrWhiteSpace(path)) return Paint(forcedArms ?? ArmStyle.Classic);

        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException($"no skin at '{full}'");

        if (!Png.TryDecode(File.ReadAllBytes(full), out var image, out var error))
            throw new InvalidDataException($"skin '{Path.GetFileName(full)}': {error}");

        var legacy = image.Height * 2 == image.Width;
        if (!legacy && image.Width != image.Height)
            throw new InvalidDataException(
                $"skin '{Path.GetFileName(full)}' is {image.Width}x{image.Height}; wanted a square sheet or a 2:1 one");

        if (image.Width % Sheet != 0)
            throw new InvalidDataException(
                $"skin '{Path.GetFileName(full)}' is {image.Width} wide; wanted a multiple of {Sheet}");

        var size = image.Width;
        var pixels = new byte[size * size * 4];

        // A 2:1 sheet is squared up rather than special-cased downstream. Everything below this
        // point then addresses one 64×64 layout, and "no left limbs" is a flag on the model rather
        // than a second set of coordinates for every part.
        Array.Copy(image.Pixels, pixels, image.Pixels.Length);

        var arms = forcedArms ?? DetectArms(pixels, size, legacy);
        var scale = size / Sheet;

        var summary = $"{Path.GetFileName(full)} {size}x{image.Height}"
                    + (scale > 1 ? $" ({scale}x)" : "")
                    + $", {(arms == ArmStyle.Slim ? "slim" : "classic")} arms"
                    + (forcedArms is null ? "" : ", forced")
                    + (legacy ? ", 64x32 layout" : "");

        return new PlayerSkinData(pixels, size, arms, legacy, summary);
    }

    /// <summary>
    /// Works out arm width by asking whether the texels only a four-wide arm uses were ever drawn.
    /// </summary>
    /// <remarks>
    /// A three-wide arm leaves two columns of its patch unreachable, and every tool that writes a
    /// slim sheet leaves them clear. Reading the geometry out of the art is the only option
    /// available to a file on disk: the width is carried as account metadata, not in the PNG.
    /// </remarks>
    private static ArmStyle DetectArms(byte[] pixels, int size, bool legacy)
    {
        var scale = size / Sheet;

        // Right arm: the two columns past a slim underside, and past a slim back panel.
        var slim = IsClear(pixels, size, scale, 50, 16, 2, 4)
                && IsClear(pixels, size, scale, 54, 20, 2, 12);

        // A 64×32 sheet predates slim arms entirely and has nothing at the left arm's coordinates,
        // so asking about them would read every old sheet as slim.
        if (!legacy)
        {
            slim &= IsClear(pixels, size, scale, 42, 48, 2, 4)
                 && IsClear(pixels, size, scale, 46, 52, 2, 12);
        }

        return slim ? ArmStyle.Slim : ArmStyle.Classic;
    }

    private static bool IsClear(byte[] pixels, int size, int scale, int x, int y, int w, int h)
    {
        for (var py = y * scale; py < (y + h) * scale; py++)
        for (var px = x * scale; px < (x + w) * scale; px++)
        {
            if (py >= size || px >= size) continue;
            if (pixels[(py * size + px) * 4 + 3] >= 8) return false;
        }

        return true;
    }

    /// <summary>
    /// Checks a sheet actually carries art everywhere the model reads from.
    /// </summary>
    /// <remarks>
    /// A missed patch draws as a hole, not as an error: the box is still there, the alpha test just
    /// throws it away. Walking every face of every box and demanding an opaque texel is the only
    /// thing that notices a limb whose underside was never painted.
    /// </remarks>
    public static List<string> Validate(PlayerSkinData skin)
    {
        var faults = new List<string>();
        var scale = skin.Size / Sheet;

        foreach (var box in PlayerModel.Build(skin.Arms, skin.Legacy))
        {
            if (box.Overlay) continue;   // overlays are meant to be mostly empty

            for (var face = 0; face < 6; face++)
            {
                var (x, y, w, h) = PlayerModel.FaceRect(box, face);
                if (!IsClear(skin.Pixels, skin.Size, scale, x, y, w, h)) continue;

                faults.Add($"{box.Part} face {face} at {x},{y} is blank");
            }
        }

        return faults;
    }

    private readonly record struct Rgb(byte R, byte G, byte B);

    private static readonly Rgb Skin = new(222, 176, 138);
    private static readonly Rgb SkinShade = new(190, 145, 110);
    private static readonly Rgb Hair = new(78, 54, 36);
    private static readonly Rgb Tunic = new(96, 118, 76);
    private static readonly Rgb TunicShade = new(74, 94, 58);
    private static readonly Rgb Belt = new(88, 62, 40);
    private static readonly Rgb Trousers = new(80, 70, 60);
    private static readonly Rgb Boot = new(60, 45, 33);
    private static readonly Rgb EyeWhite = new(232, 232, 228);
    private static readonly Rgb Iris = new(58, 92, 120);
    private static readonly Rgb Mouth = new(150, 100, 88);

    /// <summary>
    /// Draws the default skin: a drifter in a belted tunic and boots.
    /// </summary>
    /// <remarks>
    /// Painted face by face off the model's own net rather than as a picture of a sheet, so a
    /// change to the model cannot leave a patch of the texture behind pointing at nothing.
    /// </remarks>
    public static PlayerSkinData Paint(ArmStyle arms)
    {
        var pixels = new byte[Sheet * Sheet * 4];   // transparent everywhere nothing is drawn

        foreach (var box in PlayerModel.Build(arms, legacy: false))
        {
            if (box.Overlay) continue;

            var part = box.Part;
            PaintBox(pixels, box, (face, x, y, w, h) => part switch
            {
                PlayerPart.Head => HeadTexel(face, x, y, w, h),
                PlayerPart.Body => BodyTexel(face, x, y, w, h),
                PlayerPart.RightArm or PlayerPart.LeftArm => ArmTexel(face, x, y, w, h),
                _ => LegTexel(face, x, y, w, h),
            });
        }

        return new PlayerSkinData(pixels, Sheet, arms, Legacy: false,
            $"Driftwood drifter, {(arms == ArmStyle.Slim ? "slim" : "classic")} arms");
    }

    private static void PaintBox(byte[] pixels, in ModelBox box, Func<int, int, int, int, int, Rgb> shade)
    {
        for (var face = 0; face < 6; face++)
        {
            var (rx, ry, rw, rh) = PlayerModel.FaceRect(box, face);

            for (var y = 0; y < rh; y++)
            for (var x = 0; x < rw; x++)
            {
                var c = shade(face, x, y, rw, rh);
                var i = ((ry + y) * Sheet + rx + x) * 4;
                pixels[i] = c.R;
                pixels[i + 1] = c.G;
                pixels[i + 2] = c.B;
                pixels[i + 3] = 255;
            }
        }
    }

    private static Rgb HeadTexel(int face, int x, int y, int w, int h)
    {
        // Hair over the crown and down the back; a fringe at the front, and enough down the sides
        // that the join reads as a haircut rather than a cap.
        var hairDepth = face switch
        {
            0 => 2,       // front fringe
            1 => 6,       // back of the head
            2 or 3 => 3,  // sides
            4 => h,       // crown
            _ => 0,       // under the chin
        };

        if (y < hairDepth) return Vary(Hair, x, y, 12);
        if (face == 5) return Vary(SkinShade, x, y, 6);

        if (face == 0)
        {
            // Eyes on the fourth row, a socket shadow above them, mouth two rows down. Drawn by
            // coordinate rather than by a bitmap so the face survives a change of head size.
            if (y == h / 2 - 1 && (x == 1 || x == 2 || x == w - 3 || x == w - 2)) return Vary(SkinShade, x, y, 4);
            if (y == h / 2)
            {
                if (x == 1 || x == w - 2) return EyeWhite;
                if (x == 2 || x == w - 3) return Iris;
            }
            if (y == h / 2 + 2 && x >= 2 && x <= w - 3) return Mouth;
        }

        return Vary(Skin, x, y, 7);
    }

    private static Rgb BodyTexel(int face, int x, int y, int w, int h)
    {
        if (face == 4) return Vary(TunicShade, x, y, 5);      // shoulders, in the head's shadow
        if (face == 5) return Vary(Trousers, x, y, 5);        // the tunic stops above the hem

        // Belt two thirds down, with the tunic falling loose below it.
        var belt = h * 2 / 3;
        if (y == belt || y == belt + 1) return Vary(Belt, x, y, 6);
        if (y > belt) return Vary(TunicShade, x, y, 8);

        // A lighter panel down the front so the torso has a front and a back.
        if (face == 0 && x > 1 && x < w - 2) return Vary(Tunic, x, y, 9);

        return Vary(face == 1 ? TunicShade : Tunic, x, y, 8);
    }

    private static Rgb ArmTexel(int face, int x, int y, int w, int h)
    {
        if (face == 4) return Vary(TunicShade, x, y, 5);   // shoulder cap
        if (face == 5) return Vary(SkinShade, x, y, 6);    // palm

        // Sleeve to just past the elbow, bare forearm and hand below it.
        var cuff = h * 5 / 8;
        if (y < cuff) return Vary(y == cuff - 1 ? TunicShade : Tunic, x, y, 8);
        return Vary(Skin, x, y, 7);
    }

    private static Rgb LegTexel(int face, int x, int y, int w, int h)
    {
        if (face == 4) return Vary(Trousers, x, y, 5);
        if (face == 5) return Vary(Boot, x, y, 4);          // sole

        var bootTop = h * 5 / 8;
        if (y >= bootTop) return Vary(y == bootTop ? Boot : Boot, x, y, 7);
        return Vary(Trousers, x, y, 8);
    }

    /// <summary>Roughens a flat colour so the model is not four solid rectangles.</summary>
    private static Rgb Vary(Rgb c, int x, int y, int spread)
    {
        var d = (int)((Noise(x, y) * 2f - 1f) * spread);
        return new Rgb(Clamp(c.R + d), Clamp(c.G + d), Clamp(c.B + d));
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    /// <summary>Deterministic 0..1 hash noise, so the same build always paints the same skin.</summary>
    private static float Noise(int x, int y)
    {
        unchecked
        {
            var h = 0x51ED270B;
            h ^= x * 0x27D4EB2D;
            h ^= y * (int)0x9E3779B1;
            h = (h ^ (h >> 15)) * 0x2C1B3C6D;
            h = (h ^ (h >> 12)) * 0x297A2D39;
            h ^= h >> 15;
            return (h & 0x00FFFFFF) / (float)0x01000000;
        }
    }
}
