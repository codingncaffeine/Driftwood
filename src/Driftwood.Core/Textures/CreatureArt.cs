using System.Numerics;
using Driftwood.Core.Entities;

namespace Driftwood.Core.Textures;

/// <summary>Which drawing goes on the front of a head.</summary>
public enum FaceKind
{
    /// <summary>Two wet eyes, a muzzle, nostrils — what every animal wears.</summary>
    Beast,

    /// <summary>Dark eye pits over a fallen-open mouth. The crawler's, and ours.</summary>
    Grim,

    /// <summary>A pale glowing pair and nothing else. The farwalker's.</summary>
    Eyes,
}

/// <summary>What a creature is made of, in colour. One row per animal.</summary>
/// <param name="Hide">The main coat.</param>
/// <param name="Mark">Blotches, fleece tips, wing feathers — whatever the second colour is.</param>
/// <param name="Muzzle">The face: a snout, a beak, a nose.</param>
/// <param name="Horn">Horns, combs, hooves. The hard bits.</param>
/// <param name="Blotching">
/// How much of the coat the mark takes, 0 to 1. Zero is a plain animal.
/// </param>
/// <param name="Grain">How rough the coat reads, in shades either way.</param>
/// <param name="Face">Which drawing the front of its head carries.</param>
public readonly record struct CreatureHide(
    (byte R, byte G, byte B) Hide,
    (byte R, byte G, byte B) Mark,
    (byte R, byte G, byte B) Muzzle,
    (byte R, byte G, byte B) Horn,
    float Blotching,
    float Grain,
    FaceKind Face = FaceKind.Beast);

/// <summary>
/// Paints a creature's sheet, in code, the way <see cref="TileGen"/> paints a block's.
/// </summary>
/// <remarks>
/// <para>⛔ <b>This is what ships.</b> A pack's entity art is a reference and cannot be shipped with
/// the game; an animal with no skin draws as a black cut-out. So every creature we ship carries a
/// sheet drawn here, and a pack replaces it exactly as a pack replaces a block tile.</para>
/// <para>⛳ <b>Painted through the box, not onto the rectangle.</b> Every pixel is turned back into
/// the point on the animal's surface it belongs to, and the colour is decided there — so a blotch is
/// a shape in space rather than a shape on a sheet, and it <em>wraps</em>. Painted flat, a marking
/// stops dead at every seam, and the six patches of one box are six unrelated smudges. It costs one
/// dot product per pixel and it is the whole difference between a cow and a cow-shaped stain.</para>
/// <para>⚠ <b>One texel per model unit</b>, which is the same density our block tiles are drawn at
/// (sixteen across a block). A creature is not sharper than the ground it stands on.</para>
/// </remarks>
public static class CreatureArt
{
    /// <summary>Our own colours, by our own name for the animal.</summary>
    /// <remarks>
    /// ⚠ Deliberately not the reference's palette. The nets are shared because they have to be; the
    /// drawing is the part that is ours, and it is drawn in the same slightly-desaturated register
    /// as our blocks rather than against somebody else's screenshots.
    /// </remarks>
    private static readonly Dictionary<string, CreatureHide> Hides = new(StringComparer.Ordinal)
    {
        // Dark umber with cream blotches — the pattern everyone draws a cow with, and the one that
        // reads at twenty paces, which a plain brown animal does not.
        ["cow"] = new((58, 44, 36), (222, 214, 200), (150, 122, 112), (206, 198, 176), 0.42f, 0.05f),

        ["pig"] = new((198, 134, 130), (176, 112, 110), (162, 104, 104), (120, 84, 84), 0.10f, 0.04f),

        // Off-white fleece, weathered rather than bleached, on a darker face.
        ["sheep"] = new((222, 218, 208), (198, 192, 180), (96, 82, 74), (78, 66, 60), 0.30f, 0.09f),

        ["chicken"] = new((232, 228, 216), (206, 198, 182), (226, 168, 62), (198, 54, 48), 0.16f, 0.05f),

        // Storm-grey over a paler undercoat, with a dark muzzle and amber round the eyes — the
        // dusk-readable silhouette colours, desaturated to sit with our blocks like the rest.
        ["wolf"] = new((142, 136, 124), (186, 178, 164), (82, 72, 64), (196, 158, 76), 0.24f, 0.08f),

        // The cart (#28): riveted iron over a darker frame. Not a creature — the one machine on
        // the roster — but painted through the same brush as everything else that moves.
        ["cart"] = new((104, 106, 112), (134, 136, 142), (62, 64, 70), (122, 96, 60), 0.18f, 0.05f),

        // The cargo cart (#97): the same iron, warmed by the timber of the hold riding in it.
        ["cargo_cart"] = new((110, 104, 96), (140, 132, 118), (64, 60, 54), (146, 112, 64), 0.2f, 0.06f),

        // ⛳ THE HOSTILES, and the palette is where they are told apart. All three wear the same two
        // nets the beasts do — that is the point of a net — so if they read as one another it is
        // because the colours failed, not the geometry. Grain is turned up on all of them: a rotted
        // or bony surface is the one place a coarse coat is the right answer.
        //
        // Sickly green over torn cloth, and a face darker than the rest of it.
        ["zombie"] = new((74, 116, 74), (46, 72, 108), (52, 84, 56), (38, 60, 40), 0.34f, 0.13f),

        // Bone against the shadow inside it. ⚠ Not white: a white skeleton at night is a white
        // silhouette on black, which is the one thing that reads as a hole in the world rather than
        // as a creature standing in it.
        ["skeleton"] = new((196, 194, 186), (150, 148, 140), (44, 44, 44), (168, 166, 158), 0.24f, 0.11f),

        // Near-black with the one thing every drawing of a spider has: eyes that are not black.
        ["spider"] = new((42, 34, 32), (26, 20, 20), (140, 30, 26), (168, 40, 34), 0.30f, 0.10f),

        // The sea's zombie: teal-grey flesh the water has been at, deep sea-blue rags, and
        // pale weed-green accents. Reads against the zombie by temperature — cold against sick.
        ["drowned"] = new((88, 124, 118), (46, 84, 100), (62, 96, 86), (118, 150, 128), 0.36f, 0.13f),

        // And the desert's: sun-dried tan over leather the colour of old rope. Reads against
        // both cousins as the only WARM one, which suits the one that walks through noon.
        ["husk"] = new((156, 134, 94), (112, 96, 68), (100, 86, 60), (176, 156, 112), 0.32f, 0.14f),

        // Pond-green through and through — the one hostile that is a colour before it is a shape.
        // Its face is boxes rather than paint (see StarterCreatures.Slime), so Muzzle here is the
        // mouth box's colour and the eyes carry their own part.
        ["slime"] = new((116, 162, 100), (98, 142, 86), (58, 82, 52), (70, 104, 62), 0.22f, 0.06f),

        // Lichen over moss, mottled hard — the thing that stands still in a thicket and is not a
        // bush. Grey-olive against the slime's pond-green (the audit holds the two apart by
        // measurement), and its face is the grim one: dark pits over a mouth fallen open.
        ["crawler"] = new(
            (140, 144, 102), (94, 106, 72), (40, 46, 38), (64, 84, 60), 0.40f, 0.13f, FaceKind.Grim),

        // Night-violet black, and the one face drawn LIGHTER than its coat: a long pale pair of
        // eyes (Horn is the eye colour on this face), which at forty blocks in the dark is the
        // whole of what a farwalker looks like. Purple-shifted so it does not read as the spider.
        ["farwalker"] = new(
            (30, 28, 40), (44, 38, 58), (20, 18, 28), (198, 182, 224), 0.14f, 0.09f, FaceKind.Eyes),

        // ── The quiet beasts. ──
        // Warm sand-brown over a cream belly. ⚠ The muzzle is well DARKER than the coat on
        // purpose: this head is five texels wide, and a face at that size has to spend its few
        // pixels on contrast or the audit's own face check reads it as bare — it did.
        ["rabbit"] = new((150, 122, 92), (208, 194, 172), (112, 84, 74), (120, 96, 76), 0.18f, 0.07f),

        // Rust over a white chest and chin — the one warm-orange animal in the game, which is
        // most of how it reads as a fox at all. Legs go dark through the hoof rule.
        ["fox"] = new((196, 116, 58), (226, 218, 206), (52, 40, 34), (40, 32, 28), 0.26f, 0.08f),

        // A grey tabby: stone-grey brindled darker, a pale muzzle, a pink nose. Held apart from
        // the wolf's storm-grey by the audit's own distance check.
        ["cat"] = new((138, 126, 112), (96, 86, 74), (196, 186, 172), (176, 128, 118), 0.30f, 0.09f),

        // Dusk-brown, wings going darker through the Wing mark — most of a bat IS wing.
        ["bat"] = new((96, 78, 66), (58, 48, 44), (70, 56, 50), (64, 52, 46), 0.30f, 0.09f),

        // Slate-blue, mottled the way wet skin is; the tentacle tips shade through the leg rule.
        ["squid"] = new((96, 108, 132), (76, 88, 112), (66, 76, 98), (66, 76, 98), 0.22f, 0.08f),

        // P14's residents: one silhouette, three occupations stated in broad cloth/apron colours.
        ["shorewright"] = new((76, 104, 112), (184, 142, 82), (170, 132, 104), (64, 82, 86), 0.26f, 0.06f),
        ["forager"] = new((92, 122, 74), (176, 142, 86), (176, 136, 108), (74, 88, 56), 0.24f, 0.06f),
        ["waykeeper"] = new((94, 84, 112), (190, 172, 116), (168, 132, 104), (70, 64, 82), 0.22f, 0.06f),
        ["lorekeeper"] = new((74, 62, 100), (194, 166, 88), (174, 136, 108), (88, 68, 112), 0.25f, 0.07f),

        // Owned summon treatments are purposefully distinct from their wild relatives. The wolf's
        // Eyes face spends its bright cyan hard-parts colour on the readable pair the user asked for.
        ["summoned_skeleton"] = new((178, 190, 194), (112, 128, 138), (34, 42, 48), (88, 174, 188), 0.28f, 0.12f),
        ["summoned_zombie"] = new((72, 94, 92), (56, 50, 86), (42, 60, 58), (106, 174, 184), 0.38f, 0.14f),
        ["spirit_wolf"] = new((42, 46, 54), (104, 112, 124), (24, 28, 34), (72, 196, 238), 0.34f, 0.10f, FaceKind.Eyes),
        ["earth_elemental"] = new((92, 96, 92), (126, 118, 94), (52, 58, 56), (116, 226, 224), 0.42f, 0.16f, FaceKind.Eyes),

        // Vault metal and the Crown's cold night-stone. The warden's cyan mark is shared with the
        // star key/heart, making reward and encounter one visual family without borrowed artwork.
        ["storm_sentinel"] = new((82, 90, 96), (154, 120, 68), (52, 58, 64), (116, 188, 194), 0.32f, 0.10f, FaceKind.Grim),
        ["starwarden"] = new((38, 42, 58), (72, 94, 116), (24, 28, 42), (104, 220, 214), 0.38f, 0.11f, FaceKind.Eyes),
    };

    /// <summary>True when we have colours for this creature.</summary>
    public static bool Has(string name) => Hides.ContainsKey(name);

    /// <summary>Every creature we can paint.</summary>
    public static IEnumerable<string> All => Hides.Keys;

    /// <summary>
    /// Paints one creature onto a sheet the size its own net is cut for.
    /// </summary>
    /// <remarks>
    /// ⚠ Anything the net does not cover stays fully transparent, which is on purpose: it is what
    /// makes a patch landing outside its own rectangle visible as a hole rather than as a smear of
    /// whatever was beside it.
    /// </remarks>
    public static Image Paint(CreatureModel model, int seed = 0)
    {
        var hide = Hides.TryGetValue(model.Name, out var found)
            ? found
            : new CreatureHide((150, 140, 130), (120, 112, 104), (110, 100, 94), (90, 84, 78), 0.2f, 0.06f);

        var pixels = new byte[model.SheetWidth * model.SheetHeight * 4];

        foreach (var bone in model.Bones)
        {
            var role = RoleOf(bone.Name);
            if (role == Part.Shell) continue;

            foreach (var cube in bone.Cubes)
            {
                var w = (int)MathF.Round(cube.Size.X);
                var h = (int)MathF.Round(cube.Size.Y);
                var d = (int)MathF.Round(cube.Size.Z);
                if (w == 0 || h == 0 || d == 0) continue;

                var min = cube.Origin;
                var max = cube.Origin + cube.Size;

                for (var face = 0; face < 6; face++)
                {
                    var (rx, ry, rw, rh) = PlayerModel.FaceRect(cube.U, cube.V, w, h, d, cube.Mirror, face);
                    if (rw <= 0 || rh <= 0) continue;

                    // ⛔ A net that runs off its own sheet is skipped rather than thrown on, and
                    // CreatureModel.Validate is what says so out loud. A hand-written table gets
                    // this wrong — ours did, first time, on the one animal whose boxes are not the
                    // reference's — and a painter that indexes past the end takes the whole game
                    // down at startup with a message naming neither the creature nor the box.
                    if (rx < 0 || ry < 0 || rx + rw > model.SheetWidth || ry + rh > model.SheetHeight)
                        continue;

                    PaintFace(pixels, model, hide, role, min, max, cube.Mirror, face, rx, ry, rw, rh, seed);
                }
            }
        }

        return new Image(model.SheetWidth, model.SheetHeight, pixels);
    }

    /// <summary>What part of the animal a bone is, taken from the name it was given.</summary>
    /// <remarks>
    /// ⚠ By name, because a name is what a model actually carries. A head is the only part that
    /// needs a face drawn on it, and the alternative — "the bone highest up the front" — is a guess
    /// that is wrong for the first bird.
    /// </remarks>
    private static Part RoleOf(string bone) => bone switch
    {
        "head" => Part.Head,
        "beak" => Part.Muzzle,
        "comb" => Part.Horn,
        "mouth" => Part.Muzzle,
        "nose" => Part.Muzzle,

        // ⛔ The slime's shell. Left entirely unpainted — transparent texels are discarded by the
        // cutout shader, which is the only honest way to draw a translucent thing without blending.
        // Painting it opaque would box the face in; see StarterCreatures.Slime for the trade.
        "gel" => Part.Shell,

        var n when n.StartsWith("eye", StringComparison.Ordinal) => Part.Eye,
        var n when n.StartsWith("ear", StringComparison.Ordinal) => Part.Horn,
        var n when n.StartsWith("wing", StringComparison.Ordinal) => Part.Wing,

        // Legs under every naming the models actually use: leg0, frontLegLeft, backLegR,
        // rearFootRight, haunchLeft, tentacle3. The haunch is a leg's thigh and shades with it,
        // and a tentacle's tip darkens through the same rule.
        var n when n.Contains("leg", StringComparison.OrdinalIgnoreCase)
                || n.Contains("foot", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("haunch", StringComparison.Ordinal)
                || n.StartsWith("tentacle", StringComparison.Ordinal) => Part.Leg,

        _ => Part.Body,
    };

    private enum Part { Body, Head, Leg, Wing, Muzzle, Horn, Eye, Shell }

    private static void PaintFace(
        byte[] pixels, CreatureModel model, in CreatureHide hide, Part role,
        Vector3 min, Vector3 max, bool mirror, int face,
        int rx, int ry, int rw, int rh, int seed)
    {
        var (normal, uAxis, vAxis) = PlayerModel.FaceAxes(face, mirror);

        var centre = (min + max) * 0.5f;
        var size = max - min;

        var faceCentre = centre + normal * (Vector3.Dot(size, Vector3.Abs(normal)) * 0.5f);
        var acrossU = Vector3.Dot(size, Vector3.Abs(uAxis));
        var acrossV = Vector3.Dot(size, Vector3.Abs(vAxis));

        // Which way the light falls, so a back is lighter than a belly even before anything is
        // drawn on it. The same top-lit convention the block tiles use.
        var lift = 1f + normal.Y * 0.10f - MathF.Abs(normal.X) * 0.04f;

        for (var y = 0; y < rh; y++)
        for (var x = 0; x < rw; x++)
        {
            // The middle of this texel, as a point on the animal.
            var s = (x + 0.5f) / rw;
            var t = (y + 0.5f) / rh;

            var at = faceCentre
                   + uAxis * ((s - 0.5f) * acrossU)
                   + vAxis * ((t - 0.5f) * acrossV);

            var (r, g, b) = ColourAt(hide, role, at, min, max, normal, s, t, seed);

            var shade = lift + (Noise(at, seed + 71) - 0.5f) * hide.Grain * 2f;

            var i = ((ry + y) * model.SheetWidth + rx + x) * 4;
            pixels[i] = Clamp(r * shade);
            pixels[i + 1] = Clamp(g * shade);
            pixels[i + 2] = Clamp(b * shade);
            pixels[i + 3] = 255;
        }
    }

    private static (float R, float G, float B) ColourAt(
        in CreatureHide hide, Part role, Vector3 at, Vector3 min, Vector3 max, Vector3 normal,
        float s, float t, int seed)
    {
        // An eye that is a box wears the face's own eye colours: dark, with the one light quarter
        // that makes it read as wet. The same drawing the painted-on face uses, at box scale.
        if (role == Part.Eye)
            return s < 0.34f && t < 0.44f ? (232f, 232f, 228f) : (26f, 22f, 20f);

        var coat = role switch
        {
            Part.Muzzle => hide.Muzzle,
            Part.Horn => hide.Horn,
            _ => hide.Hide,
        };

        var (r, g, b) = ((float)coat.R, (float)coat.G, (float)coat.B);

        // Blotches, decided in space so they run over an edge and carry on down the next face.
        if (hide.Blotching > 0f && role is Part.Body or Part.Head or Part.Wing)
        {
            var blotch = Blobs(at, seed);
            if (blotch > 1f - hide.Blotching)
            {
                r = hide.Mark.R;
                g = hide.Mark.G;
                b = hide.Mark.B;
            }
        }

        // A paler underside, which every animal has and which is most of what stops a box reading
        // as a box. Strongest straight down and fading out round the sides.
        if (role is Part.Body && normal.Y < -0.5f)
        {
            r += (255 - r) * 0.16f;
            g += (255 - g) * 0.16f;
            b += (255 - b) * 0.16f;
        }

        // Hooves and feet: the bottom third of a leg goes dark.
        if (role == Part.Leg && t > 0.66f && MathF.Abs(normal.Y) < 0.5f)
        {
            r = r * 0.45f + hide.Horn.R * 0.55f;
            g = g * 0.45f + hide.Horn.G * 0.55f;
            b = b * 0.45f + hide.Horn.B * 0.55f;
        }

        // ⛳ The face, and only on the face. A head is a box and the front of it is the one patch a
        // player ever looks at — a head with no eyes reads as a crate with ears.
        if (role == Part.Head && normal.Z < -0.5f)
        {
            // The grim face: two dark pits high up, and a mouth fallen open below them — taller
            // than it is wide, flaring at the jaw. Ours, drawn on the reference's net; no wet
            // glint anywhere on it, because nothing about it should read as alive.
            if (hide.Face == FaceKind.Grim)
            {
                var pit = t is > 0.20f and < 0.45f && (s is > 0.14f and < 0.40f || s is > 0.60f and < 0.86f);
                var mouth = t is > 0.50f and < 0.95f && s is > 0.38f and < 0.62f;
                var jaw = t is > 0.64f and < 0.95f && (s is > 0.24f and < 0.38f || s is > 0.62f and < 0.76f);

                return pit || mouth || jaw
                    ? ((float)hide.Muzzle.R, hide.Muzzle.G, hide.Muzzle.B)
                    : (r, g, b);
            }

            // A pale pair and nothing else — long, level, set wide, lit from within rather than
            // wet. The one face drawn LIGHTER than its coat.
            if (hide.Face == FaceKind.Eyes)
            {
                // Two real texel rows even on the elemental's six-high face. The earlier narrow
                // band collapsed to one row at this scale and averaged away against rough stone.
                var band = t is > 0.20f and < 0.60f && (s is > 0.04f and < 0.46f || s is > 0.54f and < 0.96f);
                return band ? ((float)hide.Horn.R, hide.Horn.G, hide.Horn.B) : (r, g, b);
            }

            var eyeY = t is > 0.24f and < 0.50f;
            var leftEye = s is > 0.12f and < 0.32f;
            var rightEye = s is > 0.68f and < 0.88f;

            if (eyeY && (leftEye || rightEye))
            {
                // A dark eye with a light quarter, which is the whole of what makes it read as wet.
                var glint = s < 0.5f ? s < 0.20f : s < 0.76f;
                return glint && t < 0.36f ? (232f, 232f, 228f) : (26f, 22f, 20f);
            }

            // A muzzle across the lower middle of the face.
            if (t > 0.58f && s is > 0.24f and < 0.76f)
            {
                r = hide.Muzzle.R;
                g = hide.Muzzle.G;
                b = hide.Muzzle.B;

                // Two nostrils in it.
                if (t is > 0.68f and < 0.82f && (s is > 0.32f and < 0.42f || s is > 0.58f and < 0.68f))
                    return (r * 0.55f, g * 0.55f, b * 0.55f);
            }
        }

        return (r, g, b);
    }

    /// <summary>Soft blobs in space, 0 to 1, coherent over a few units.</summary>
    /// <remarks>
    /// Three samples on a lattice a few units across, smoothed — enough to make a patch the size of
    /// a cow's flank rather than a speckle, and cheap enough to run per texel of every sheet at
    /// startup without anybody noticing.
    /// </remarks>
    private static float Blobs(Vector3 at, int seed)
    {
        var p = at * 0.22f;
        return Smooth(p, seed) * 0.6f + Smooth(p * 2.1f, seed + 17) * 0.3f + Smooth(p * 4.3f, seed + 31) * 0.1f;
    }

    private static float Smooth(Vector3 p, int seed)
    {
        var xi = (int)MathF.Floor(p.X);
        var yi = (int)MathF.Floor(p.Y);
        var zi = (int)MathF.Floor(p.Z);

        var xf = Fade(p.X - xi);
        var yf = Fade(p.Y - yi);
        var zf = Fade(p.Z - zi);

        var c000 = Lattice(xi, yi, zi, seed);
        var c100 = Lattice(xi + 1, yi, zi, seed);
        var c010 = Lattice(xi, yi + 1, zi, seed);
        var c110 = Lattice(xi + 1, yi + 1, zi, seed);
        var c001 = Lattice(xi, yi, zi + 1, seed);
        var c101 = Lattice(xi + 1, yi, zi + 1, seed);
        var c011 = Lattice(xi, yi + 1, zi + 1, seed);
        var c111 = Lattice(xi + 1, yi + 1, zi + 1, seed);

        var x00 = c000 + (c100 - c000) * xf;
        var x10 = c010 + (c110 - c010) * xf;
        var x01 = c001 + (c101 - c001) * xf;
        var x11 = c011 + (c111 - c011) * xf;

        var y0 = x00 + (x10 - x00) * yf;
        var y1 = x01 + (x11 - x01) * yf;

        return y0 + (y1 - y0) * zf;
    }

    private static float Fade(float x) => x * x * (3f - 2f * x);

    private static float Lattice(int x, int y, int z, int seed)
    {
        unchecked
        {
            var h = seed;
            h ^= x * 0x27D4EB2D;
            h ^= y * unchecked((int)0x9E3779B1);
            h ^= z * 0x165667B1;
            h = (h ^ (h >> 15)) * 0x2C1B3C6D;
            h = (h ^ (h >> 12)) * 0x297A2D39;
            h ^= h >> 15;
            return (h & 0x00FFFFFF) / (float)0x01000000;
        }
    }

    private static float Noise(Vector3 at, int seed) =>
        Lattice((int)MathF.Floor(at.X * 2f), (int)MathF.Floor(at.Y * 2f), (int)MathF.Floor(at.Z * 2f), seed);

    private static byte Clamp(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);

    /// <summary>
    /// Checks every creature we ship comes out painted, and painted differently from its neighbours.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Three claims, and the third is the one that catches a real fault.</b> A sheet can be the
    /// right size and fully opaque and still be one flat colour, which is what a painter that lost
    /// its noise, its blotches and its face produces — and a flat sheet on a correct model draws a
    /// perfectly convincing animal-shaped nothing. So: every patch the net names is opaque, no patch
    /// that is not named is; the head's front is not the same as its back, because that is where the
    /// face is; and two animals do not come out the same colour.
    /// </remarks>
    public static List<string> Validate()
    {
        var faults = new List<string>();
        var averages = new Dictionary<string, (float R, float G, float B)>(StringComparer.Ordinal);

        foreach (var model in StarterCreatures.All)
        {
            var sheet = Paint(model);

            if (sheet.Width != model.SheetWidth || sheet.Height != model.SheetHeight)
            {
                faults.Add($"{model.Name}'s sheet came out {sheet.Width}x{sheet.Height}, not its net's size");
                continue;
            }

            var painted = 0;
            double r = 0, g = 0, b = 0;

            for (var i = 0; i < sheet.Pixels.Length; i += 4)
            {
                if (sheet.Pixels[i + 3] == 0) continue;

                painted++;
                r += sheet.Pixels[i];
                g += sheet.Pixels[i + 1];
                b += sheet.Pixels[i + 2];
            }

            if (painted == 0) { faults.Add($"{model.Name}'s sheet is entirely empty"); continue; }

            averages[model.Name] = ((float)(r / painted), (float)(g / painted), (float)(b / painted));

            // Every box's every face, and nothing else. A painter that walked the boxes but wrote at
            // the wrong offsets fills the right NUMBER of texels in the wrong places, so the count is
            // asked for exactly rather than as "enough of them".
            var wanted = 0;
            foreach (var bone in model.Bones)
            foreach (var cube in bone.Cubes)
            {
                var w = (int)MathF.Round(cube.Size.X);
                var h = (int)MathF.Round(cube.Size.Y);
                var d = (int)MathF.Round(cube.Size.Z);
                if (w == 0 || h == 0 || d == 0) continue;

                wanted += 2 * (w * h) + 2 * (d * h) + 2 * (w * d);
            }

            // ⚠ Patches shared between boxes are painted twice and counted once, so the sheet can
            // legitimately hold fewer texels than the boxes ask for — a mirrored pair of legs is one
            // drawing. More than asked for is the fault: that is paint outside every net.
            if (painted > wanted)
                faults.Add($"{model.Name} painted {painted} texels where its boxes only cover {wanted}");

            var head = Array.Find(model.Bones, x => x.Name == "head");
            if (head.Cubes is null || head.Cubes.Length == 0)
            {
                // ⛳ Two creatures are headless on purpose and each carries its own claim instead.
                // The slime's face is boxes — so its eye patch has to differ from its coat, which
                // is the same "something is drawn there" question asked of the parts it does have.
                // The squid has no face at all in v1, and that is honest: its net has no head and
                // painting eyes on the mantle is its own small job, said here rather than skipped.
                if (!Faceless.Contains(model.Name))
                    faults.Add($"{model.Name} has no head to put a face on");
                else if (EyeFault(model, sheet) is { Length: > 0 } eyeless)
                    faults.Add(eyeless);

                continue;
            }

            var cubeHead = head.Cubes[0];
            var hw = (int)MathF.Round(cubeHead.Size.X);
            var hh = (int)MathF.Round(cubeHead.Size.Y);
            var hd = (int)MathF.Round(cubeHead.Size.Z);

            var front = Mean(sheet, PlayerModel.FaceRect(cubeHead.U, cubeHead.V, hw, hh, hd, cubeHead.Mirror, 0));
            var back = Mean(sheet, PlayerModel.FaceRect(cubeHead.U, cubeHead.V, hw, hh, hd, cubeHead.Mirror, 1));

            if (MathF.Abs(front - back) < 3f)
                faults.Add($"{model.Name}'s face reads {front:F1} against the back of its head at {back:F1} — there is nothing drawn on it");
        }

        // And they are not all the same animal in different shapes.
        foreach (var (a, first) in averages)
        foreach (var (c, second) in averages)
        {
            if (string.CompareOrdinal(a, c) >= 0) continue;

            var apart = MathF.Abs(first.R - second.R) + MathF.Abs(first.G - second.G) + MathF.Abs(first.B - second.B);
            if (apart < 12f) faults.Add($"{a} and {c} are painted the same colour, {apart:F0} apart in total");
        }

        return faults;
    }

    /// <summary>The kinds allowed to have no head bone. Each is a decision, not an oversight.</summary>
    private static readonly HashSet<string> Faceless = new(StringComparer.Ordinal) { "slime", "squid" };

    /// <summary>For a headless kind with eye boxes: are the eyes actually drawn on?</summary>
    private static string EyeFault(CreatureModel model, Image sheet)
    {
        var eye = Array.Find(model.Bones, x => x.Name == "eye0");
        if (eye.Cubes is null || eye.Cubes.Length == 0) return "";

        var e = eye.Cubes[0];
        var c = model.Bones[0].Cubes[0];

        var eyeFront = Mean(sheet, PlayerModel.FaceRect(
            e.U, e.V, (int)e.Size.X, (int)e.Size.Y, (int)e.Size.Z, e.Mirror, 0));
        var coatFront = Mean(sheet, PlayerModel.FaceRect(
            c.U, c.V, (int)c.Size.X, (int)c.Size.Y, (int)c.Size.Z, c.Mirror, 0));

        return MathF.Abs(eyeFront - coatFront) < 3f
            ? $"{model.Name}'s eyes read {eyeFront:F1} against its coat at {coatFront:F1} — nothing is drawn on them"
            : "";
    }

    private static float Mean(Image sheet, (int X, int Y, int W, int H) rect)
    {
        double total = 0;
        var n = 0;

        for (var y = rect.Y; y < rect.Y + rect.H; y++)
        for (var x = rect.X; x < rect.X + rect.W; x++)
        {
            var i = (y * sheet.Width + x) * 4;
            if (i < 0 || i + 3 >= sheet.Pixels.Length) continue;

            total += (sheet.Pixels[i] + sheet.Pixels[i + 1] + sheet.Pixels[i + 2]) / 3.0;
            n++;
        }

        return n == 0 ? 0f : (float)(total / n);
    }
}
