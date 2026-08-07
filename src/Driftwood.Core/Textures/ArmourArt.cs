using Driftwood.Core.Entities;
using Driftwood.Core.Items;

namespace Driftwood.Core.Textures;

/// <summary>
/// Paints the two sheets a suit of armour is worn out of, in code, like <see cref="CreatureArt"/>
/// paints an animal.
/// </summary>
/// <remarks>
/// <para>⛔ <b>Ours as well as theirs, and that is the user's own call carried forward.</b> Their
/// words about the packs, when the creatures landed: <em>the texture packs are really just there to
/// give us an idea of what things are supposed to look like — we need a default look because we
/// cannot ship them with the game.</em> A suit of armour that only had a picture when somebody else's
/// install was on the machine would be the same hole the animals nearly shipped with.</para>
/// <para><b>Two sheets and it is not a stylistic choice.</b> 64×32 each, addressed as exactly the net
/// a 64×32 skin uses — head at 0,0, body at 16,16, one arm at 40,16, one leg at 0,16 — because that
/// is the layout every pack in the genre paints armour in, which is what lets a pack's own sheet be
/// worn with nothing translated. One sheet cannot hold four slots: leggings and a chestplate both
/// want the torso patch, so the leggings live on a second one. That is why the format has two, and
/// it is why we do.</para>
/// <para>⚠ <b>The two are worn at different inflations</b> and that is the other half of the same
/// problem — see <see cref="ArmourModel"/>. Layer two sits tighter than layer one or the leggings
/// come out through the front of the chestplate.</para>
/// </remarks>
public static class ArmourArt
{
    /// <summary>Texels across and down one armour sheet.</summary>
    public const int Width = 64;
    public const int Height = 32;

    /// <summary>Where each part's net begins on the sheet. The 64×32 skin layout, exactly.</summary>
    public static (int U, int V) NetOf(PlayerPart part) => part switch
    {
        PlayerPart.Head => (0, 0),
        PlayerPart.Body => (16, 16),
        PlayerPart.RightArm or PlayerPart.LeftArm => (40, 16),
        _ => (0, 16),
    };

    /// <summary>
    /// Both sheets for one material: layer one, then layer two.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The plate is painted per FACE rather than over the sheet as a rectangle.</b> A net is six
    /// unrelated patches packed edge to edge, so a gradient run down the image lights the top of the
    /// head and the back of the same head differently by an amount that has nothing to do with where
    /// either is in space. Asking each face for its own rectangle is what makes the shading agree
    /// with the shape — the same reason <see cref="CreatureArt"/> works back from a pixel to the
    /// point on the animal it belongs to.
    /// </remarks>
    public static byte[][] Build(in Armour.Material material)
    {
        var one = new byte[Width * Height * 4];
        var two = new byte[Width * Height * 4];

        // Layer one: the helmet, the chestplate and its sleeves, and the boots.
        Plate(one, PlayerPart.Head, 8, 8, 8, material, 0f, 1f);
        Plate(one, PlayerPart.Body, 8, 12, 4, material, 0f, 1f);
        Plate(one, PlayerPart.RightArm, ArmWidth, 12, 4, material, 0f, 1f);

        // ⛳ A boot is the bottom third of the leg's net and nothing above it. The box drawn is the
        // whole leg — there is no separate foot to hang geometry off — so what makes it a boot
        // rather than a trouser leg is where the paint stops.
        Plate(one, PlayerPart.RightLeg, 4, 12, 4, material, 0.66f, 1f);

        // Layer two: a belt round the waist and the legs under it, both tighter.
        Plate(two, PlayerPart.Body, 8, 12, 4, material, 0.72f, 1f);
        Plate(two, PlayerPart.RightLeg, 4, 12, 4, material, 0f, 0.72f);

        return [one, two];
    }

    /// <summary>
    /// Armour arms are always the wide build.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Not the wearer's arm width, and this is a decision rather than an oversight.</b> A slim
    /// arm is three texels across and the format's armour net gives the sleeve four, so an armour box
    /// cut to a slim arm would read a four-wide patch across three texels and come out stretched by a
    /// third — on one build of the model and not the other, which is the sort of fault that gets
    /// blamed on the skin. The plate stands proud of the arm in any case, so a wide sleeve over a
    /// narrow arm is what armour looks like.
    /// </remarks>
    public const int ArmWidth = 4;

    /// <summary>
    /// Paints one box's six faces, between two heights down the part.
    /// </summary>
    /// <param name="from">Where the plate starts, 0 at the top of the box and 1 at the bottom.</param>
    /// <param name="to">And where it stops. Everything outside is left clear.</param>
    private static void Plate(
        byte[] sheet, PlayerPart part, int w, int h, int d, in Armour.Material material,
        float from, float to)
    {
        var (u, v) = NetOf(part);

        for (var face = 0; face < 6; face++)
        {
            var rect = PlayerModel.FaceRect(u, v, w, h, d, mirror: false, face);
            var lid = face is 4 or 5;

            for (var y = 0; y < rect.H; y++)
            for (var x = 0; x < rect.W; x++)
            {
                // ⚠ The top and the underside are seen from above, so "how far down the part" is not
                // a thing their rows measure. A lid belongs to whichever end of the plate it caps:
                // the top of the box is painted when the plate reaches the top, and vice versa.
                var down = lid ? (face == 4 ? 0f : 1f) : (y + 0.5f) / rect.H;
                if (down < from || down > to) continue;

                // Lit from above, the same direction everything else in this game is.
                var shade = lid ? (face == 4 ? 26 : -30) : (int)((0.5f - down) * 26f);

                // The bands that make a plate read as beaten metal rather than as a painted box,
                // plus a stud line down each side.
                if (!lid && ((y + 1) % 4 == 0)) shade -= 16;
                if ((x + 1) % 5 == 0 && (y + 2) % 4 == 0) shade += 24;

                shade += (int)((TileGen.Noise(rect.X + x, rect.Y + y, 90210) * 2f - 1f) * 7f);

                var at = ((rect.Y + y) * Width + rect.X + x) * 4;
                sheet[at] = TileGen.Clamp(material.R + shade);
                sheet[at + 1] = TileGen.Clamp(material.G + shade);
                sheet[at + 2] = TileGen.Clamp(material.B + shade);
                sheet[at + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Checks the sheets are painted where they should be and clear where they should not.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The clear half is the whole check.</b> "Something was painted" is true of a sheet filled
    /// edge to edge, which is exactly what a boot that forgot to stop looks like — a trouser leg in
    /// iron, drawn over the leggings, on a player wearing no leggings at all. So it asks that the
    /// leg's net is painted at the BOTTOM on layer one and at the TOP on layer two, and that neither
    /// reaches the other's end.
    /// </remarks>
    public static List<string> Validate()
    {
        var faults = new List<string>();

        foreach (var material in Armour.Materials)
        {
            var sheets = Build(material);
            if (sheets.Length != 2)
            {
                faults.Add($"{material.Name} came back with {sheets.Length} sheets rather than two");
                continue;
            }

            // The leg's front face, which is where a boot and a legging are told apart.
            var leg = PlayerModel.FaceRect(0, 16, 4, 12, 4, mirror: false, 0);

            var bootTop = Painted(sheets[0], leg.X + 1, leg.Y + 1);
            var bootFoot = Painted(sheets[0], leg.X + 1, leg.Y + leg.H - 1);
            var legTop = Painted(sheets[1], leg.X + 1, leg.Y + 1);
            var legFoot = Painted(sheets[1], leg.X + 1, leg.Y + leg.H - 1);

            if (!bootFoot) faults.Add($"{material.Name} boots are not painted on the foot");
            if (bootTop) faults.Add($"{material.Name} boots reach the top of the leg, so they are trousers");
            if (!legTop) faults.Add($"{material.Name} leggings are not painted at the top of the leg");
            if (legFoot) faults.Add($"{material.Name} leggings reach the foot, so they cover the boots");

            // And the two patches layer two must leave entirely alone, or a helmet appears the
            // moment somebody puts trousers on.
            //
            // ⛔ PROBED THROUGH FaceRect RATHER THAN AT A CORNER OF THE NET, and the first version of
            // this was a check that could not fail. A net's top row is the lid and the underside
            // side by side, starting a depth in from the left — so the square at the net's own
            // origin is DEAD SHEET on every box in the format, and "is anything painted five texels
            // in from the head's origin" is asking about a corner nothing ever paints. It reported
            // the helmet missing on a sheet that had one, and it would have reported layer two clean
            // whatever layer two contained.
            foreach (var part in (ReadOnlySpan<PlayerPart>)[PlayerPart.Head, PlayerPart.RightArm])
            {
                var (u, v) = NetOf(part);
                var size = part == PlayerPart.Head ? (8, 8, 8) : (ArmWidth, 12, 4);
                var front = PlayerModel.FaceRect(u, v, size.Item1, size.Item2, size.Item3, false, 0);

                if (Painted(sheets[1], front.X + front.W / 2, front.Y + front.H / 2))
                    faults.Add($"{material.Name} layer two paints the {part}, which the leggings do not cover");
            }

            var head = PlayerModel.FaceRect(0, 0, 8, 8, 8, mirror: false, face: 0);
            if (!Painted(sheets[0], head.X + 4, head.Y + 4))
                faults.Add($"{material.Name} has no helmet on it");
        }

        return faults;
    }

    private static bool Painted(byte[] sheet, int x, int y) =>
        (uint)x < Width && (uint)y < Height && sheet[(y * Width + x) * 4 + 3] >= 128;
}
