using System.Numerics;
using Driftwood.Core.Physics;

namespace Driftwood.Core.Entities;

/// <summary>The boxes a humanoid is made of, in the order the renderer draws them.</summary>
public enum PlayerPart
{
    Head,
    Body,
    RightArm,
    LeftArm,
    RightLeg,
    LeftLeg,
}

/// <summary>
/// Arm width. Four texels of arm is the original proportion; three is the narrower build that
/// arrived later and that a large share of skins are now drawn for.
/// </summary>
public enum ArmStyle
{
    Classic,
    Slim,
}

/// <summary>One corner of the model's surface.</summary>
public readonly record struct ModelVertex(Vector3 Position, Vector3 Normal, Vector2 Uv);

/// <summary>
/// One box of the model: where it hangs, how big it is, and which patch of the skin sheet wraps it.
/// </summary>
/// <param name="Pivot">
/// Where the box turns, in model units measured from between the feet. Absolute rather than relative
/// to a parent, because the hierarchy is the renderer's business and this table reads better as a
/// set of measurements than as a tree.
/// </param>
/// <param name="Offset">The box's minimum corner relative to its own pivot.</param>
/// <param name="Width">Extent along X, the model's right.</param>
/// <param name="Height">Extent along Y, up.</param>
/// <param name="Depth">Extent along Z, backward.</param>
/// <param name="U">Left edge of this box's net on the skin sheet, in texels.</param>
/// <param name="Mirror">Whether the net is applied left-for-right. True for every left limb.</param>
/// <param name="Inflate">Units the box grows by on every side. Non-zero only for overlays.</param>
public readonly record struct ModelBox(
    PlayerPart Part,
    bool Overlay,
    Vector3 Pivot,
    Vector3 Offset,
    float Width,
    float Height,
    float Depth,
    int U,
    int V,
    bool Mirror,
    float Inflate);

/// <summary>
/// The blocky humanoid: what it is made of, and how a skin sheet wraps around it.
/// </summary>
/// <remarks>
/// <para><b>Model space.</b> −Z is forward, +Y is up, +X is the model's own right. That is the
/// conventional orientation for a character, and it is worth the one place it costs — putting the
/// model's facing on −Z means a limb swinging forward is a rotation about X, a turn is about Y and
/// an arm lifting outward is about Z. Any other assignment leaves the animator doing pitch about
/// the Z axis, which is exactly the sort of thing that reads fine and is wrong.</para>
/// <para><b>Units.</b> The model is 32 units tall and the body is 1.8 blocks, so a unit is
/// 0.05625 blocks. Everything here is in units and the scale is applied once, on the way into the
/// vertex buffer, so the part matrices stay pure rotation and translation — which matters, because
/// a matrix carrying a non-uniform scale cannot transform a normal.</para>
/// <para><b>The skin sheet.</b> Each box is unwrapped into the sheet as six rectangles in a fixed
/// arrangement, and each rectangle holds the face as seen from outside, u to the viewer's right and
/// v downward. That is why a skin's face looks back at you: the character's right eye lands on the
/// image's left. The one thing to know that does not fall out of that rule is the underside, which
/// the format reads back-to-front relative to the top.</para>
/// <para><b>Left limbs are mirrored.</b> Not a legacy quirk — even a modern sheet, which gives the
/// left arm and leg their own patches, applies them left-for-right. Getting it right means one flag
/// covers both cases: a modern sheet points a mirrored left arm at its own patch, and an old
/// 64×32 sheet, which has no left limbs at all, points a mirrored left arm at the right arm's.</para>
/// </remarks>
public static class PlayerModel
{
    /// <summary>Model height in units. Legs 12, torso 12, head 8.</summary>
    public const float UnitsTall = 32f;

    /// <summary>Blocks per model unit, pinned to the body the physics actually collides with.</summary>
    public const float Unit = PlayerBody.Height / UnitsTall;

    /// <summary>Skin sheets are addressed as 64×64 whatever they are stored at.</summary>
    public const int SheetSize = 64;

    /// <summary>
    /// How far an overlay box stands off the body. A quarter of a unit is the convention and it is
    /// not arbitrary: any less and the two surfaces z-fight, any more and a hat reads as a helmet
    /// floating off the head.
    /// </summary>
    public const float OverlayInflate = 0.25f;

    /// <summary>Shoulder height, used by the first-person arm to hang from the same point.</summary>
    public const float ShoulderHeight = 22f;

    private static float ArmWidth(ArmStyle style) => style == ArmStyle.Slim ? 3f : 4f;

    /// <summary>Where an arm turns, in model units. The one place the shoulders are measured.</summary>
    /// <remarks>
    /// Read by <see cref="Build"/> and by whatever wants to hang something off a hand. Two copies of
    /// a shoulder is exactly how a held tool ends up half a limb away from the fist holding it.
    /// </remarks>
    public static Vector3 ArmPivot(bool right) => new(right ? 5f : -5f, ShoulderHeight, 0f);

    /// <summary>
    /// The middle of a fist, in that arm's own space, measured from its shoulder.
    /// </summary>
    /// <remarks>
    /// <para>Down the arm rather than at the end of it: the limb runs from +2 to −10 about its pivot
    /// and the fingers close a little short of the wrist.</para>
    /// <para>⚠ <b>Not on the arm's axis.</b> The limb box hangs off the shoulder toward the outside
    /// of the body — a classic arm spans −1 to +3 across its own x — so the middle of the hand is
    /// half an arm's width out, and a slim arm's is half a unit further in. A held thing centred on
    /// x=0 sits with a quarter of itself inside the sleeve.</para>
    /// </remarks>
    public static Vector3 FistInArm(ArmStyle arms, bool right)
    {
        var across = (ArmWidth(arms) - 2f) * 0.5f;
        return new Vector3(right ? across : -across, -9.6f, 0f);
    }

    /// <summary>
    /// Every box, base layer first in <see cref="PlayerPart"/> order, then the overlays.
    /// </summary>
    /// <param name="legacy">
    /// True for a 64×32 sheet, which carries no left limbs and no overlay but the hat.
    /// </param>
    public static ModelBox[] Build(ArmStyle arms, bool legacy)
    {
        var w = ArmWidth(arms);

        // A slim arm keeps its outer edge against the body and loses the texel on the inside, so
        // both builds hang from the same shoulder and only the sleeve gets narrower.
        var rightArmOffset = new Vector3(-1f, -10f, -2f);
        var leftArmOffset = new Vector3(-w + 1f, -10f, -2f);

        var boxes = new List<ModelBox>(12)
        {
            // Head turns at the neck. Its box sits entirely above the pivot.
            new(PlayerPart.Head, false, new Vector3(0f, 24f, 0f), new Vector3(-4f, 0f, -4f),
                8f, 8f, 8f, 0, 0, false, 0f),

            // Torso turns at the hip, not the neck, so leaning into a crouch tips it over the legs
            // instead of swinging the hips out behind.
            new(PlayerPart.Body, false, new Vector3(0f, 12f, 0f), new Vector3(-4f, 0f, -2f),
                8f, 12f, 4f, 16, 16, false, 0f),

            new(PlayerPart.RightArm, false, ArmPivot(true), rightArmOffset,
                w, 12f, 4f, 40, 16, false, 0f),

            new(PlayerPart.LeftArm, false, ArmPivot(false), leftArmOffset,
                w, 12f, 4f, legacy ? 40 : 32, legacy ? 16 : 48, true, 0f),

            // Legs overlap by a fifth of a unit down the middle. Butting them exactly together
            // leaves a hairline of daylight between them whenever they part.
            new(PlayerPart.RightLeg, false, new Vector3(1.9f, 12f, 0f), new Vector3(-2f, -12f, -2f),
                4f, 12f, 4f, 0, 16, false, 0f),

            new(PlayerPart.LeftLeg, false, new Vector3(-1.9f, 12f, 0f), new Vector3(-2f, -12f, -2f),
                4f, 12f, 4f, legacy ? 0 : 16, legacy ? 16 : 48, true, 0f),

            // Hair, hats and anything else drawn proud of the head. The one overlay an old sheet has.
            new(PlayerPart.Head, true, new Vector3(0f, 24f, 0f), new Vector3(-4f, 0f, -4f),
                8f, 8f, 8f, 32, 0, false, OverlayInflate),
        };

        if (!legacy)
        {
            boxes.Add(new ModelBox(PlayerPart.Body, true, new Vector3(0f, 12f, 0f), new Vector3(-4f, 0f, -2f),
                8f, 12f, 4f, 16, 32, false, OverlayInflate));

            boxes.Add(new ModelBox(PlayerPart.RightArm, true, ArmPivot(true), rightArmOffset,
                w, 12f, 4f, 40, 32, false, OverlayInflate));

            boxes.Add(new ModelBox(PlayerPart.LeftArm, true, ArmPivot(false), leftArmOffset,
                w, 12f, 4f, 48, 48, true, OverlayInflate));

            boxes.Add(new ModelBox(PlayerPart.RightLeg, true, new Vector3(1.9f, 12f, 0f), new Vector3(-2f, -12f, -2f),
                4f, 12f, 4f, 0, 32, false, OverlayInflate));

            boxes.Add(new ModelBox(PlayerPart.LeftLeg, true, new Vector3(-1.9f, 12f, 0f), new Vector3(-2f, -12f, -2f),
                4f, 12f, 4f, 0, 48, true, OverlayInflate));
        }

        return [.. boxes];
    }

    /// <summary>Face order used everywhere below: front, back, right, left, top, bottom.</summary>
    private static readonly Vector3[] Normals =
    [
        -Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY,
    ];

    /// <summary>Which way texture u runs across each face, before mirroring.</summary>
    private static readonly Vector3[] UAxes =
    [
        -Vector3.UnitX, Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitZ, -Vector3.UnitX, -Vector3.UnitX,
    ];

    /// <summary>Which way texture v runs down each face. Down the body for the four sides; for the
    /// top, toward the face; for the underside, toward the back — the format's one asymmetry.</summary>
    private static readonly Vector3[] VAxes =
    [
        -Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitZ, Vector3.UnitZ,
    ];

    /// <summary>
    /// Where each of a box's six faces lands on the sheet, in texels.
    /// </summary>
    /// <remarks>
    /// The middle row is a band around the box — right, front, left, back — and the two patches
    /// above it are the top and the underside. Mirroring swaps the two side patches, which is the
    /// other half of what applying a net left-for-right means.
    /// </remarks>
    public static (int X, int Y, int W, int H) FaceRect(in ModelBox box, int face) =>
        FaceRect(box.U, box.V, (int)MathF.Round(box.Width), (int)MathF.Round(box.Height),
                 (int)MathF.Round(box.Depth), box.Mirror, face);

    /// <summary>
    /// The same arithmetic against loose numbers, for a box that did not come from this table.
    /// </summary>
    /// <remarks>
    /// ⛳ The net layout is the format's, not the player's — every creature's boxes are cut from a
    /// sheet the same way. Sharing this is what lets <see cref="CreatureModel.Validate"/> hold an
    /// imported skeleton to the same claim without a second transcription of a rule that is already
    /// easy to get subtly wrong.
    /// </remarks>
    public static (int X, int Y, int W, int H) FaceRect(int u, int v, int w, int h, int d, bool mirror, int face)
    {
        var right = (u, v + d, d, h);
        var left = (u + d + w, v + d, d, h);
        if (mirror) (right, left) = (left, right);

        return face switch
        {
            0 => (u + d, v + d, w, h),           // front
            1 => (u + 2 * d + w, v + d, w, h),   // back
            2 => right,
            3 => left,
            4 => (u + d, v, w, d),               // top
            _ => (u + d + w, v, w, d),           // underside
        };
    }

    /// <summary>
    /// Writes one box's 24 vertices and 36 indices, in blocks, relative to the box's own pivot.
    /// </summary>
    public static void Emit(in ModelBox box, List<ModelVertex> vertices, List<uint> indices)
    {
        var size = new Vector3(box.Width, box.Height, box.Depth);
        var inflate = new Vector3(box.Inflate);

        EmitBox(box.Offset - inflate, box.Offset + size + inflate, size,
                box.U, box.V, box.Mirror, Unit, new Vector2(SheetSize, SheetSize),
                vertices, indices);
    }

    /// <summary>
    /// Writes any box wrapped in a net, for a caller whose boxes did not come from this table.
    /// </summary>
    /// <param name="net">
    /// The box's size <em>before</em> inflating, which is what the net was cut for. An overlay grows
    /// on every side and still reads the same patches.
    /// </param>
    /// <param name="unit">Blocks per model unit.</param>
    /// <param name="sheet">
    /// Texels across and down the sheet the net is addressed in. ⚠ Not always the sheet's own pixel
    /// size — see <see cref="CreatureMesh.NetHeight"/>, where a padded square is read as a net with
    /// spare room under it.
    /// </param>
    /// <remarks>
    /// ⛳ <b>Shared with the creatures on purpose.</b> The face arrangement, the two texture axes and
    /// the winding are the <em>format's</em>, not the player's — every creature's boxes are cut out
    /// the same way — and a second transcription of a table where the underside runs backwards is a
    /// second chance to get it subtly wrong. <see cref="FaceRect"/> was already shared for the same
    /// reason; this is the other half of it.
    /// </remarks>
    public static void EmitBox(
        Vector3 min, Vector3 max, Vector3 net, int u, int v, bool mirror,
        float unit, Vector2 sheet, List<ModelVertex> vertices, List<uint> indices)
    {
        var centre = (min + max) * 0.5f;
        var size = max - min;

        var nw = (int)MathF.Round(net.X);
        var nh = (int)MathF.Round(net.Y);
        var nd = (int)MathF.Round(net.Z);

        for (var face = 0; face < 6; face++)
        {
            var n = Normals[face];
            var uAxis = mirror ? -UAxes[face] : UAxes[face];
            var vAxis = VAxes[face];

            // Only the component along the face's own axis matters; the other two are zero.
            var faceCentre = centre + n * (Vector3.Dot(size, Vector3.Abs(n)) * 0.5f);
            var acrossU = Vector3.Dot(size, Vector3.Abs(uAxis));
            var acrossV = Vector3.Dot(size, Vector3.Abs(vAxis));

            // A box with no extent along one axis is how the format draws a flat plane — a fish's
            // fin, a warden's ribbon — and four of its six faces come out as nothing at all. The
            // two that remain are the plane, back to back, which is what makes it visible from
            // either side. Emitting the other four costs eight triangles of zero area apiece.
            if (acrossU <= 0f || acrossV <= 0f) continue;

            var (rx, ry, rw, rh) = FaceRect(u, v, nw, nh, nd, mirror, face);

            var first = (uint)vertices.Count;

            // Corners in texture order: (0,0), (1,0), (1,1), (0,1).
            for (var corner = 0; corner < 4; corner++)
            {
                var s = corner is 1 or 2 ? 1f : 0f;
                var t = corner is 2 or 3 ? 1f : 0f;

                var position = faceCentre
                             + uAxis * ((s - 0.5f) * acrossU)
                             + vAxis * ((t - 0.5f) * acrossV);

                var uv = new Vector2((rx + s * rw) / sheet.X, (ry + t * rh) / sheet.Y);
                vertices.Add(new ModelVertex(position * unit, n, uv));
            }

            // Mirroring reverses the u axis, which reverses the order the corners come out in and
            // with it the winding. Both orders are spelled out rather than one being derived, so
            // the validator below has something independent to disagree with.
            if (mirror)
            {
                indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
            }
            else
            {
                indices.AddRange([first, first + 2, first + 1, first, first + 3, first + 2]);
            }
        }
    }

    /// <summary>
    /// Checks every emitted triangle winds anticlockwise seen from outside.
    /// </summary>
    /// <remarks>
    /// The block mesher shipped with its top and bottom faces inverted once, and back-face culling
    /// ate them; the model has twice the faces and half of them are mirrored, which is a strictly
    /// better opportunity to make the same mistake. Cross product against the face's own normal is
    /// the only check that does not just restate the code it is checking.
    /// </remarks>
    public static List<string> ValidateWinding()
    {
        var faults = new List<string>();

        foreach (var arms in (ReadOnlySpan<ArmStyle>)[ArmStyle.Classic, ArmStyle.Slim])
        foreach (var legacy in (ReadOnlySpan<bool>)[false, true])
        {
            foreach (var box in Build(arms, legacy))
            {
                var vertices = new List<ModelVertex>();
                var indices = new List<uint>();
                Emit(box, vertices, indices);

                for (var t = 0; t < indices.Count; t += 3)
                {
                    var a = vertices[(int)indices[t]];
                    var b = vertices[(int)indices[t + 1]];
                    var c = vertices[(int)indices[t + 2]];

                    var cross = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
                    if (Vector3.Dot(cross, a.Normal) > 0f) continue;

                    faults.Add($"{arms} {(legacy ? "legacy " : "")}{box.Part}{(box.Overlay ? " overlay" : "")} "
                             + $"face {t / 6} winds inward");
                    break;
                }
            }
        }

        return faults;
    }

    /// <summary>
    /// Checks every box's net lands on the sheet, in six distinct patches of the right sizes.
    /// </summary>
    /// <remarks>
    /// A net that runs off the sheet samples whatever is next to it, and a net whose patches overlap
    /// puts an elbow on a kneecap. Neither shows up as an error anywhere — the model draws, it is
    /// just wearing the wrong pixels — so the arithmetic gets checked rather than looked at.
    /// </remarks>
    public static List<string> ValidateNet()
    {
        var faults = new List<string>();

        foreach (var arms in (ReadOnlySpan<ArmStyle>)[ArmStyle.Classic, ArmStyle.Slim])
        foreach (var legacy in (ReadOnlySpan<bool>)[false, true])
        {
            foreach (var box in Build(arms, legacy))
            {
                var seen = new HashSet<(int, int, int, int)>();
                var label = $"{arms} {(legacy ? "legacy " : "")}{box.Part}{(box.Overlay ? " overlay" : "")}";

                var w = (int)box.Width;
                var d = (int)box.Depth;

                // The top row of a net is the lid and the underside side by side, starting a depth
                // in from the left edge, which leaves a square of dead sheet at each end of it.
                var deadLeft = (box.U, box.V, d, d);
                var deadRight = (box.U + d + 2 * w, box.V, d, d);

                for (var face = 0; face < 6; face++)
                {
                    var rect = FaceRect(box, face);

                    if (rect.X < 0 || rect.Y < 0 || rect.X + rect.W > SheetSize || rect.Y + rect.H > SheetSize)
                    {
                        faults.Add($"{label} face {face} runs off the sheet at {rect.X},{rect.Y} {rect.W}x{rect.H}");
                        continue;
                    }

                    var expected = face switch
                    {
                        0 or 1 => ((int)box.Width, (int)box.Height),
                        2 or 3 => ((int)box.Depth, (int)box.Height),
                        _ => ((int)box.Width, (int)box.Depth),
                    };

                    if ((rect.W, rect.H) != expected)
                        faults.Add($"{label} face {face} is {rect.W}x{rect.H}, should be {expected.Item1}x{expected.Item2}");

                    if (!seen.Add(rect))
                        faults.Add($"{label} face {face} reuses the patch at {rect.X},{rect.Y}");

                    // Nothing may reach into that dead sheet. It is the one test here that notices
                    // a whole row shifted sideways — every other one is happy as long as the
                    // patches are the right size and do not collide with each other.
                    if (Overlaps(rect, deadLeft) || Overlaps(rect, deadRight))
                        faults.Add($"{label} face {face} at {rect.X},{rect.Y} reaches into the net's dead corner");
                }
            }
        }

        return faults;
    }

    private static bool Overlaps((int X, int Y, int W, int H) a, (int X, int Y, int W, int H) b) =>
        a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    /// <summary>
    /// Checks a left limb reads its net backwards from the right one.
    /// </summary>
    /// <remarks>
    /// On an old 64×32 sheet both arms share one patch, so if mirroring quietly did nothing the two
    /// arms would still be textured, still be the right size, and still pass every check above —
    /// they would simply both be right arms. The test is deliberately asymmetric: it asks which way
    /// u runs across the front of each arm, and the answers have to be opposite.
    /// </remarks>
    public static List<string> ValidateMirror()
    {
        var faults = new List<string>();

        foreach (var legacy in (ReadOnlySpan<bool>)[false, true])
        {
            var boxes = Build(ArmStyle.Classic, legacy);
            var right = Array.Find(boxes, b => b is { Part: PlayerPart.RightArm, Overlay: false });
            var left = Array.Find(boxes, b => b is { Part: PlayerPart.LeftArm, Overlay: false });

            var rightVerts = new List<ModelVertex>();
            var leftVerts = new List<ModelVertex>();
            var scratch = new List<uint>();
            Emit(right, rightVerts, scratch);
            scratch.Clear();
            Emit(left, leftVerts, scratch);

            // Face 0 is the front, corners 0 and 1 are its two top corners, u = 0 then u = 1.
            var rightRuns = rightVerts[1].Position.X - rightVerts[0].Position.X;
            var leftRuns = leftVerts[1].Position.X - leftVerts[0].Position.X;

            if (rightRuns * leftRuns >= 0f)
                faults.Add($"{(legacy ? "legacy" : "modern")}: both arms run u the same way ({rightRuns:F2} and {leftRuns:F2})");

            if (legacy && (left.U, left.V) != (right.U, right.V))
                faults.Add($"legacy left arm reads {left.U},{left.V} instead of the right arm's {right.U},{right.V}");

            if (!legacy && (left.U, left.V) == (right.U, right.V))
                faults.Add("modern left arm shares the right arm's patch instead of using its own");
        }

        return faults;
    }
}
