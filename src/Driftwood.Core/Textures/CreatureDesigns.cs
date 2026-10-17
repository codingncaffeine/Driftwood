using System.Numerics;

namespace Driftwood.Core.Textures;

/// <summary>One texel a design is asked about: which part, which face of which box, and where on it.</summary>
/// <param name="Bone">The bone's name.</param>
/// <param name="Cube">Which of the bone's boxes, in order.</param>
/// <param name="Face">Front, back, right, left, top, bottom — 0 to 5, the net's own order.</param>
/// <param name="X">Across the face in sheet texels, left to right as the sheet is drawn.</param>
/// <param name="Y">Down the face in sheet texels.</param>
/// <param name="W">The face's width in texels, and <paramref name="H"/> its height.</param>
/// <param name="At">The texel's centre as a point on the creature, for anything that should wrap.</param>
public readonly record struct DesignTexel(string Bone, int Cube, int Face, int X, int Y, int W, int H, Vector3 At)
{
    public bool Front => Face == 0;
    public bool Back => Face == 1;
    public bool Side => Face is 2 or 3;
    public bool Top => Face == 4;
    public bool Bottom => Face == 5;
}

/// <summary>A colour a design puts down, and how much grain the painter may add to it.</summary>
public readonly record struct Ink(byte R, byte G, byte B, float Grain = 0.05f)
{
    public Ink Darker(float by) => new((byte)(R * (1f - by)), (byte)(G * (1f - by)), (byte)(B * (1f - by)), Grain);
    public Ink Lighter(float by) =>
        new((byte)(R + (255 - R) * by), (byte)(G + (255 - G) * by), (byte)(B + (255 - B) * by), Grain);
}

/// <summary>
/// The hostiles' own drawings: faces, clothes, ribs and markings, laid out on the reference's nets.
/// </summary>
/// <remarks>
/// <para>⛔ <b>Why these exist.</b> Every hostile used to be one of <see cref="CreatureArt"/>'s
/// hides — a coat colour, blotches of a second, a generic face — so a zombie was a green-and-blue
/// smudge and a skeleton a grey one. The reference's designs are a shirt and trousers on the zombie,
/// a ribcage you can see through on the skeleton, the green mottle and the one face on the creeper.
/// A player knows those at a glance; a hide is not any of them.</para>
/// <para>⛔ <b>Ours, drawn against the reference rather than copied from it.</b> The layouts — what
/// goes on which face of which box — are the genre's and have to be, because a pack's art and ours
/// share these nets. Every colour and every pixel is decided here, in the same slightly muted
/// register as our blocks, and checked side by side against a vanilla-like pack before it ships.</para>
/// <para>Coordinates are the sheet's: on a front face X runs left to right as the sheet is drawn
/// and Y runs down, which is how a pack artist reads a net. A design returns null for a texel that
/// should be empty — the cutout shader discards it, which is how a ribcage is drawn.</para>
/// </remarks>
public static class CreatureDesigns
{
    public static bool TryGet(string name, out Func<DesignTexel, Ink?> design) =>
        Designs.TryGetValue(name, out design!);

    public static IEnumerable<string> Names => Designs.Keys;


    // ── Noise, in space so markings wrap round an edge ─────────────────────────────────────────

    private static float Hash(Vector3 at, int seed)
    {
        unchecked
        {
            var h = seed;
            h ^= (int)MathF.Floor(at.X * 16f) * 0x27D4EB2D;
            h ^= (int)MathF.Floor(at.Y * 16f) * unchecked((int)0x9E3779B1);
            h ^= (int)MathF.Floor(at.Z * 16f) * 0x165667B1;
            h = (h ^ (h >> 15)) * 0x2C1B3C6D;
            h = (h ^ (h >> 12)) * 0x297A2D39;
            h ^= h >> 15;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }

    private static float Hash(DesignTexel t, int seed) => Hash(t.At, seed);

    /// <summary>The same noise in cells three texels across: patches rather than speckle.</summary>
    private static float Patch(DesignTexel t, int seed) => Hash(t.At * (1f / 3f), seed);

    // ── The walking dead: skin, a shirt, trousers, shoes ───────────────────────────────────────

    private sealed record WalkerKit(
        Ink Skin, Ink Hair, Ink Shirt, Ink Trousers, Ink Shoes, Ink Eye, Ink Mouth, bool Drips);

    private static readonly WalkerKit ZombieKit = new(
        Skin: new(84, 138, 70), Hair: new(46, 82, 40), Shirt: new(36, 142, 140),
        Trousers: new(68, 62, 134), Shoes: new(98, 98, 96), Eye: new(22, 22, 22, 0f),
        Mouth: new(44, 72, 36), Drips: true);

    private static readonly WalkerKit HuskKit = new(
        Skin: new(184, 158, 110), Hair: new(108, 90, 64), Shirt: new(154, 128, 88),
        Trousers: new(76, 62, 44), Shoes: new(96, 90, 80), Eye: new(30, 24, 18, 0f),
        Mouth: new(112, 90, 60), Drips: false);

    private static Ink? Walker(DesignTexel t, WalkerKit k)
    {
        switch (t.Bone)
        {
            case "head":
                return WalkerHead(t, k);

            case "body":
            {
                if (t.Top) return t.X is >= 3 and <= 4 && t.Y is >= 1 and <= 2 ? k.Skin : k.Shirt;
                if (t.Bottom) return k.Trousers;

                // The belt line: the last two rows are the trousers' top, and a zombie's shirt hangs
                // ragged over it in drips.
                var belt = t.Y >= t.H - 2;
                if (belt)
                {
                    if (k.Drips && t.Y == t.H - 2 && Hash(t, 5) > 0.6f) return k.Shirt.Darker(0.12f);
                    return t.Y == t.H - 1 ? k.Trousers.Darker(0.1f) : k.Trousers;
                }

                // The collar, open in a V on the front.
                if (t.Front && (t.Y == 0 && t.X is >= 2 and <= 5 || t.Y == 1 && t.X is >= 3 and <= 4))
                    return k.Skin;

                // Folds: a darker crease now and then, more of them low down.
                var fold = Hash(t, 9) > 0.86f - t.Y * 0.008f;
                return fold ? k.Shirt.Darker(0.16f) : k.Shirt;
            }

            case "arm0" or "arm1":
            {
                if (t.Top) return k.Shirt;
                if (t.Bottom) return k.Skin.Darker(0.12f);

                // A short sleeve, then bare arm to a darker hand.
                if (t.Y < 4) return t.Y == 3 && Hash(t, 3) > 0.5f ? k.Shirt.Darker(0.15f) : k.Shirt;
                if (t.Y >= t.H - 2) return k.Skin.Darker(0.1f);
                return Hash(t, 4) > 0.9f ? k.Skin.Darker(0.14f) : k.Skin;
            }

            case "leg0" or "leg1":
            {
                if (t.Top) return k.Trousers;
                if (t.Bottom) return k.Shoes.Darker(0.15f);

                if (t.Y >= t.H - 3) return t.Y == t.H - 3 ? k.Shoes.Lighter(0.08f) : k.Shoes;

                // A seam down the inside of each leg.
                var seam = t.Side && t.X == (t.Bone == "leg0" ? t.W - 1 : 0);
                return seam || Hash(t, 6) > 0.92f ? k.Trousers.Darker(0.18f) : k.Trousers;
            }
        }

        return k.Skin;
    }

    private static Ink WalkerHead(DesignTexel t, WalkerKit k)
    {
        if (t.Top) return Hash(t, 1) > 0.8f ? k.Hair.Lighter(0.1f) : k.Hair;
        if (t.Bottom) return k.Skin.Darker(0.18f);

        if (t.Back) return t.Y < t.H - 2 ? (Hash(t, 2) > 0.85f ? k.Hair.Darker(0.12f) : k.Hair) : k.Skin.Darker(0.1f);

        if (t.Side)
        {
            // Hair down the back half of each side, longer at the back.
            var backward = t.Face == 2 ? t.X : t.W - 1 - t.X;
            return t.Y < 2 || (t.Y < 5 && backward >= 4) ? k.Hair : k.Skin;
        }

        // The face. Hair at the brow, a heavy ridge, two dark eyes, a nose's shadow, a slack mouth.
        if (t.Y == 0) return k.Hair;
        if (t.Y == 1) return t.X is 0 or 7 || Hash(t, 7) > 0.6f ? k.Hair : k.Skin;
        if (t.Y == 3 && t.X is 1 or 2 or 5 or 6) return k.Skin.Darker(0.2f);
        if (t.Y == 4 && t.X is 1 or 2 or 5 or 6) return k.Eye;
        if (t.Y == 5 && t.X is 3 or 4) return k.Skin.Darker(0.25f);
        if (t.Y == 6 && t.X is >= 2 and <= 5) return k.Mouth;
        if (t.Y == 7 && t.X is 3 or 4) return k.Mouth.Darker(0.1f);
        return k.Skin;
    }

    /// <summary>The sea's: drowned flesh, rags, and the glow the deep leaves in it.</summary>
    private static Ink? Drowned(DesignTexel t)
    {
        var flesh = new Ink(46, 92, 84);
        var rag = new Ink(40, 72, 76);
        var glow = new Ink(110, 214, 202, 0f);

        // The deep's light, sparse, everywhere but the face.
        if (!(t.Bone == "head" && t.Front) && Hash(t, 21) > 0.95f) return glow;

        switch (t.Bone)
        {
            case "head":
                if (t.Top || t.Back) return Hash(t, 22) > 0.7f ? flesh.Darker(0.18f) : flesh.Darker(0.1f);
                if (!t.Front) return flesh;
                // Wide glowing eyes, brightest on the upper row — the deep's own light.
                if (t.Y == 3 && t.X is 1 or 2 or 5 or 6) return glow.Lighter(0.35f);
                if (t.Y == 4 && t.X is 1 or 2 or 5 or 6) return glow;
                if (t.Y == 6 && t.X is >= 2 and <= 5) return flesh.Darker(0.35f);
                return flesh;

            case "body":
                // The shirt is mostly gone: rags at the shoulders and the hem, flesh between.
                if (t.Y < 3 || t.Y >= t.H - 3) return Hash(t, 23) > 0.25f ? rag : flesh;
                return Hash(t, 24) > 0.7f ? rag : flesh;

            case "leg0" or "leg1":
                return t.Y < t.H - 4 || Hash(t, 25) > 0.5f ? rag : flesh.Darker(0.1f);

            default:
                return t.Y < 3 ? rag : flesh;
        }
    }

    // ── The ones that are all bone ─────────────────────────────────────────────────────────────

    private sealed record BoneKit(Ink Bone, Ink Socket, Ink? Cloth, Ink? Moss, Ink? Cap);

    private static readonly BoneKit SkeletonKit = new(new(192, 190, 182), new(44, 44, 44, 0f), null, null, null);

    // The cold one: frost-blue bone under a ragged grey cloak.
    private static readonly BoneKit StrayKit = new(new(178, 190, 194), new(40, 48, 52, 0f),
        Cloth: new(116, 132, 138), Moss: null, Cap: null);

    // The marsh's: bone gone green, moss in the joints, a toadstool on the crown.
    private static readonly BoneKit BoggedKit = new(new(170, 172, 134), new(40, 44, 30, 0f),
        Cloth: null, Moss: new(84, 116, 56), Cap: new(164, 52, 42));

    private static Ink? Bones(DesignTexel t, BoneKit k)
    {
        var bone = Hash(t, 31) > 0.88f ? k.Bone.Darker(0.14f) : k.Bone;
        if (k.Moss is { } moss && Hash(t, 32) > 0.82f) bone = moss;

        switch (t.Bone)
        {
            case "head":
            {
                if (t.Top && k.Cap is { } cap)
                {
                    // A cap on the crown with pale spots, off-centre.
                    var dx = t.X - 5;
                    var dy = t.Y - 3;
                    if (dx * dx + dy * dy <= 5) return (t.X + t.Y) % 3 == 0 ? cap.Lighter(0.55f) : cap;
                }

                if (k.Cloth is { } hood && (t.Top || t.Back || (t.Side && t.Y < 4) || (t.Front && t.Y == 0)))
                    return Hash(t, 33) > 0.8f ? hood.Darker(0.15f) : hood;

                if (!t.Front) return bone;

                // Two slit sockets, the nose's shadow, and a straight dark mouth: the plainest face in
                // the dark, which is what makes it read as a skull rather than a mask.
                if (t.Y == 4 && t.X is 1 or 2 or 5 or 6) return k.Socket;
                if (t.Y == 5 && t.X is 3 or 4) return k.Socket.Lighter(0.3f);
                if (t.Y == 6 && t.X is >= 2 and <= 5) return k.Socket;
                return t.Y == 3 && t.X is 1 or 2 or 5 or 6 ? bone.Darker(0.1f) : bone;
            }

            case "body":
            {
                // A cloak over the cage, ragged at the hem and gaping in places.
                if (k.Cloth is { } cloak && !t.Top)
                {
                    var hem = t.Y >= t.H - 2 && Hash(t, 34) > 0.45f;
                    if (!hem && Hash(t, 35) > 0.12f) return Hash(t, 36) > 0.82f ? cloak.Darker(0.18f) : cloak;
                }

                // ⛳ The cage: a collar bar, ribs on every other row, a spine down the middle, and the
                // pelvis at the foot. Everything else is air — the shader throws it away.
                var spine = t.X == t.W / 2 - 1 || t.X == t.W / 2;
                if (t.Top) return t.X <= 1 || t.X >= t.W - 2 || spine ? bone : null;
                if (t.Bottom) return bone;
                if (t.Y == 0 || t.Y == t.H - 1) return bone;
                if (t.Y == t.H - 2) return t.X is 0 or 1 || t.X >= t.W - 2 || spine ? bone : null;

                if (t.Front || t.Back)
                {
                    if (spine) return t.Front ? bone.Darker(0.12f) : bone;
                    var rib = t.Y is >= 2 and <= 7 && t.Y % 2 == 0;
                    return rib ? bone : null;
                }

                // The sides carry the rib ends.
                return t.Y is >= 2 and <= 7 && t.Y % 2 == 0 ? bone : null;
            }

            default:
                // Limbs: a darker joint halfway, the knee and the elbow.
                if (t.Top || t.Bottom) return bone;
                return t.Y == t.H / 2 ? bone.Darker(0.18f) : bone;
        }
    }

    // ── The eight-legged ones ──────────────────────────────────────────────────────────────────

    private sealed record LegsKit(Ink Body, Ink Mark, Ink Eye, Ink Fang);

    private static readonly LegsKit SpiderKit = new(new(72, 58, 46), new(98, 80, 62), new(196, 36, 30, 0f), new(150, 146, 140));

    // Smaller, bluer, and the colour of the deep: the mines' own.
    private static readonly LegsKit CaveSpiderKit = new(new(34, 62, 70), new(56, 96, 104), new(190, 40, 34, 0f), new(140, 150, 150));

    private static Ink? Spider(DesignTexel t, LegsKit k)
    {
        var body = Hash(t, 41) > 0.8f ? k.Body.Darker(0.25f) : k.Body;

        switch (t.Bone)
        {
            case "head":
                if (!t.Front) return Hash(t, 42) > 0.85f ? k.Mark : body;

                // Two big eyes, six small ones round them, and the fangs.
                if (t.Y is 3 or 4 && t.X is 2 or 5) return t.Y == 3 ? k.Eye.Lighter(0.3f) : k.Eye;
                if (t.Y == 2 && t.X is 1 or 3 or 4 or 6) return k.Eye;
                if (t.Y == 5 && t.X is 1 or 6) return k.Eye.Darker(0.2f);
                if (t.Y == 7 && t.X is 2 or 5) return k.Fang;
                if (t.Y == 6 && t.X is 2 or 5) return k.Fang.Darker(0.2f);
                return body;

            case "body":
                // The abdomen: chevrons down the back, hair on the flanks.
                if (t.Top)
                {
                    var centre = MathF.Abs(t.X - (t.W - 1) * 0.5f);
                    var chevron = ((int)(t.Y - centre * 0.8f) % 4 + 4) % 4 == 0 && centre < 4f;
                    return chevron ? k.Mark : body;
                }
                return Hash(t, 43) > 0.75f ? k.Mark.Darker(0.1f) : body;

            case "neck":
                return body.Darker(0.1f);

            default:
                // The legs, with a pale band at each joint.
                if (t.Face is 2 or 3 or 0 or 1 && (t.X % 5 == 4 || t.Y % 5 == 4)) return k.Mark;
                return body;
        }
    }

    // ── The crawler: the mottle and the one face ───────────────────────────────────────────────

    private static Ink? Crawler(DesignTexel t)
    {
        var mid = new Ink(84, 156, 70);
        var light = new Ink(128, 192, 104);
        var dark = new Ink(56, 112, 48);
        var pale = new Ink(176, 208, 160);
        var black = new Ink(20, 26, 20, 0f);

        if (t.Bone == "head" && t.Front)
        {
            // Two square eyes high, and the mouth that is the whole creature: a stem from between
            // the eyes widening to a jaw with two fangs down.
            if (t.Y is 2 or 3 && t.X is 1 or 2 or 5 or 6) return black;
            if (t.Y == 4 && t.X is 3 or 4) return black;
            if (t.Y is 5 or 6 && t.X is >= 2 and <= 5) return black;
            if (t.Y == 7 && t.X is 2 or 5) return black;
        }

        // The legs' feet: the bottom row darker.
        if (t.Bone.StartsWith("leg", StringComparison.Ordinal) && !t.Top && t.Y == t.H - 1)
            return dark.Darker(0.25f);

        // ⚠ Mostly the one green. The first pass split it near evenly four ways and read as camouflage
        // netting beside the reference's even mottle.
        var n = Hash(t, 51);
        return n switch
        {
            > 0.985f => pale,
            > 0.82f => light,
            < 0.12f => dark,
            _ => mid,
        };
    }

    // ── The villager net, twice: the one the dead took and the marsh's brewer ──────────────────

    /// <summary>The face every villager-net head wears: a heavy brow, two eyes, the mouth under the nose.</summary>
    private static Ink VillagerFace(DesignTexel t, Ink skin, Ink brow, Ink white, Ink pupil)
    {
        if (t.Y == 3 && t.X is >= 1 and <= 6) return brow;
        if (t.Y == 4 && t.X is 1 or 6) return white;
        if (t.Y == 4 && t.X is 2 or 5) return pupil;
        if (t.Y == 8 && t.X is >= 2 and <= 5) return skin.Darker(0.3f);
        return skin;
    }

    private static Ink? ZombieVillager(DesignTexel t)
    {
        var skin = new Ink(86, 140, 70);
        var robe = new Ink(102, 92, 90);
        var belt = new Ink(70, 54, 46);

        switch (t.Bone)
        {
            case "helmet" or "brim":
                return null;

            case "head":
                // Hair over the crown and most of the way down the back.
                if (t.Top || t.Back) return t.Y < 6 || t.Top ? skin.Darker(0.32f) : skin;
                if (!t.Front) return t.Y < 2 ? skin.Darker(0.32f) : skin;
                return t.Y < 2 ? skin.Darker(0.32f)
                    : VillagerFace(t, skin, new(58, 40, 30, 0f), new(228, 228, 220, 0f), new(150, 30, 26, 0f));

            case "nose":
                return skin.Darker(0.12f);

            case "body":
                // The robe in tatters, the flesh through it.
                if (t.Cube == 1 && t.Y >= t.H - 6 && t.Y < t.H - 4) return belt;
                return Patch(t, 71) > 0.74f ? skin : robe;

            case "arm0" or "arm1":
                return t.Y < 4 && !t.Bottom ? robe : skin;

            default:
                // Legs: the robe's colour, darker, with bare feet.
                return t.Y >= t.H - 2 || t.Bottom ? skin.Darker(0.1f) : robe.Darker(0.15f);
        }
    }

    private static Ink? Witch(DesignTexel t)
    {
        var skin = new Ink(168, 152, 132);
        var shirt = new Ink(90, 70, 46);
        var robe = new Ink(66, 36, 82);
        var stripe = new Ink(52, 98, 40);
        var hat = new Ink(36, 34, 38);

        switch (t.Bone)
        {
            case "head":
                if (t.Top) return skin.Darker(0.08f);
                if (!t.Front) return skin;
                return VillagerFace(t, skin, new(46, 36, 30, 0f), new(230, 228, 222, 0f), new(110, 60, 140, 0f));

            case "nose":
                return new Ink(150, 92, 72);

            case "wart":
                return new Ink(80, 112, 60);

            case "hat":
                // A band round the brim tier, and a green buckle on the front of it.
                if (t.Cube == 1 && t.Y == 0 && !t.Top && !t.Bottom)
                    return t.Front && t.X == t.W / 2 ? new Ink(80, 200, 90, 0f) : hat.Lighter(0.18f);
                return hat;

            case "body":
                if (t.Cube == 0) return t.Front && t.Y < 3 && t.X is >= 3 and <= 4 ? skin : shirt;

                // The robe, with the green stripe down its front.
                if (t.Front && t.X is 3 or 4) return stripe;
                return Hash(t, 72) > 0.9f ? robe.Darker(0.15f) : robe;

            case "arms":
                if (t.Cube == 0) return robe.Lighter(0.06f);
                return t.Y >= t.H - 2 || t.Bottom ? skin : robe;

            default:
                // Legs, under the robe: brown, with grey boots.
                return t.Y >= t.H - 2 || t.Bottom ? new Ink(98, 96, 94) : shirt;
        }
    }

    // ── The small things: the stone's and the night sky's ──────────────────────────────────────

    private static Ink? Silverfish(DesignTexel t)
    {
        var grey = new Ink(136, 138, 142);
        var dark = new Ink(96, 98, 104);

        // The tufts are a scatter of bristles, mostly air.
        if (t.Bone.StartsWith("tuft", StringComparison.Ordinal))
            return Hash(t, 81) > 0.86f ? (Hash(t, 82) > 0.5f ? grey.Lighter(0.1f) : new Ink(110, 124, 118)) : null;

        if (t.Bone == "head" && t.Front && t.Y == 0 && t.X is 0 or 2) return new Ink(30, 30, 34, 0f);

        // A darker band at the seam of every segment.
        if (!t.Top && !t.Bottom && (t.X == 0 || t.X == t.W - 1) && t.Side) return dark;
        return Hash(t, 83) > 0.7f ? dark.Lighter(0.1f) : grey;
    }

    private static Ink? Phantom(DesignTexel t)
    {
        var hide = new Ink(70, 86, 136);
        var bone = new Ink(206, 186, 150);
        var eye = new Ink(120, 230, 110, 0f);

        switch (t.Bone)
        {
            case "head":
                if (t.Front && t.Y == 1 && t.X is 1 or 5) return eye;
                return hide;

            case "body":
                // A pale spine down the back and ribs off it.
                if (t.Top) return t.X == t.W / 2 || (t.Y % 2 == 0 && t.X is >= 1 and <= 3) ? bone : hide;
                return hide;

            case "wing0" or "wing1" or "wingtip0" or "wingtip1":
            {
                // The membrane between bone struts, ragged along the trailing edge.
                if (t.Top || t.Bottom)
                {
                    var trailing = t.Y >= t.H - 2 && Hash(t, 91) > 0.55f;
                    if (trailing) return null;
                    return t.X % 4 == 1 ? bone : hide;
                }
                return hide.Darker(0.12f);
            }

            default:
                return t.Top && t.X == t.W / 2 ? bone : hide;
        }
    }

    // ── The farwalker: black on black, and the eyes ────────────────────────────────────────────

    private static Ink? Farwalker(DesignTexel t)
    {
        var black = new Ink(24, 20, 30);
        var deep = new Ink(34, 26, 42);

        if (t.Bone == "head" && t.Front && t.Y == 4)
        {
            // A long violet pair, brightest at the inner ends.
            if (t.X is 0 or 1 or 2 or 5 or 6 or 7)
                return t.X is 2 or 5 ? new Ink(244, 196, 252, 0f) : new Ink(204, 110, 226, 0f);
        }

        return Hash(t, 61) > 0.8f ? deep : black;
    }

    // ⚠ Last in the class: the kits above are static fields, and a table built before them would
    // capture nothing.
    private static readonly Dictionary<string, Func<DesignTexel, Ink?>> Designs = new(StringComparer.Ordinal)
    {
        ["zombie"] = t => Walker(t, ZombieKit),
        ["husk"] = t => Walker(t, HuskKit),
        ["drowned"] = Drowned,
        ["skeleton"] = t => Bones(t, SkeletonKit),
        ["stray"] = t => Bones(t, StrayKit),
        ["bogged"] = t => Bones(t, BoggedKit),
        ["spider"] = t => Spider(t, SpiderKit),
        ["cave_spider"] = t => Spider(t, CaveSpiderKit),
        ["crawler"] = Crawler,
        ["farwalker"] = Farwalker,
        ["zombie_villager"] = ZombieVillager,
        ["witch"] = Witch,
        ["silverfish"] = Silverfish,
        ["phantom"] = Phantom,
    };
}
