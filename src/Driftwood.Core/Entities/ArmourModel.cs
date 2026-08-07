using System.Numerics;
using Driftwood.Core.Items;
using Driftwood.Core.Textures;

namespace Driftwood.Core.Entities;

/// <summary>One box of worn armour: where it hangs, which sheet it wears, and which slot puts it there.</summary>
/// <param name="Slot">The worn slot that makes this box appear at all.</param>
/// <param name="Sheet">0 for layer one, 1 for layer two.</param>
public readonly record struct ArmourBox(
    EquipSlot Slot,
    int Sheet,
    PlayerPart Part,
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
/// The boxes a suit of armour is drawn as, hung off the same joints the body is.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The player's own boxes at a bigger inflation, not a second model.</b> The body already
/// hangs off six pivots and animates about them; armour that had geometry of its own would be a
/// second thing to keep in step with a swing, and it would fall out of step exactly when somebody is
/// moving, which is the only time anybody is looking.</para>
/// <para>⛔ <b>Two inflations, and the reason is the reason the format has two sheets.</b> Leggings
/// and a chestplate both cover the waist. Drawn at the same stand-off they occupy the same surface
/// and z-fight down the middle of the player; the second layer sits tighter so it goes underneath.
/// A quarter of a unit is what the skin's own overlay uses, so the numbers here are above that: one
/// unit for the outer plate and half for the inner.</para>
/// <para>⚠ <b>Every left limb reads the right one's patch, mirrored.</b> The armour sheet is 64×32
/// and a 64×32 net has no left limbs at all — which is the format saying that a suit of armour is
/// symmetrical, and it is.</para>
/// </remarks>
public static class ArmourModel
{
    /// <summary>How far the outer plate stands off the body, in model units.</summary>
    /// <remarks>
    /// ⚠ Dialled by eye against the model rather than derived. Under about three quarters of a unit
    /// the plate and the skin z-fight where the two are parallel; much past one and a helmet reads
    /// as a bucket held over somebody's head.
    /// </remarks>
    public const float Outer = 1.0f;

    /// <summary>And the inner one, which has to fit under it.</summary>
    public const float Inner = 0.5f;

    /// <summary>Every box, in the order they are drawn.</summary>
    /// <remarks>
    /// Built rather than written out, off <see cref="ArmourArt.NetOf"/>, so the sheet the painter
    /// fills and the sheet the mesh reads are addressed from one table. Two copies of a net layout
    /// is how a sleeve ends up on a kneecap.
    /// </remarks>
    public static ArmourBox[] Build()
    {
        var boxes = new List<ArmourBox>(9);

        void Add(EquipSlot slot, int sheet, PlayerPart part, Vector3 pivot, Vector3 offset,
                 float w, float h, float d, bool mirror, float inflate)
        {
            var (u, v) = ArmourArt.NetOf(part);
            boxes.Add(new ArmourBox(slot, sheet, part, pivot, offset, w, h, d, u, v, mirror, inflate));
        }

        const float Arm = ArmourArt.ArmWidth;
        var rightArm = new Vector3(-1f, -10f, -2f);
        var leftArm = new Vector3(-Arm + 1f, -10f, -2f);
        var rightLeg = new Vector3(1.9f, 12f, 0f);
        var leftLeg = new Vector3(-1.9f, 12f, 0f);
        var legOffset = new Vector3(-2f, -12f, -2f);
        var torso = new Vector3(0f, 12f, 0f);
        var torsoOffset = new Vector3(-4f, 0f, -2f);

        // The helmet.
        Add(EquipSlot.Head, 0, PlayerPart.Head,
            new Vector3(0f, 24f, 0f), new Vector3(-4f, 0f, -4f), 8f, 8f, 8f, false, Outer);

        // The chestplate, which is a torso and two sleeves — one slot, three boxes.
        Add(EquipSlot.Chest, 0, PlayerPart.Body, torso, torsoOffset, 8f, 12f, 4f, false, Outer);
        Add(EquipSlot.Chest, 0, PlayerPart.RightArm,
            PlayerModel.ArmPivot(true), rightArm, Arm, 12f, 4f, false, Outer);
        Add(EquipSlot.Chest, 0, PlayerPart.LeftArm,
            PlayerModel.ArmPivot(false), leftArm, Arm, 12f, 4f, true, Outer);

        // The leggings: a belt on the torso and both legs, all on the tighter sheet.
        Add(EquipSlot.Legs, 1, PlayerPart.Body, torso, torsoOffset, 8f, 12f, 4f, false, Inner);
        Add(EquipSlot.Legs, 1, PlayerPart.RightLeg, rightLeg, legOffset, 4f, 12f, 4f, false, Inner);
        Add(EquipSlot.Legs, 1, PlayerPart.LeftLeg, leftLeg, legOffset, 4f, 12f, 4f, true, Inner);

        // And the boots, which are the same two legs on the outer sheet — painted only at the foot.
        Add(EquipSlot.Feet, 0, PlayerPart.RightLeg, rightLeg, legOffset, 4f, 12f, 4f, false, Outer);
        Add(EquipSlot.Feet, 0, PlayerPart.LeftLeg, leftLeg, legOffset, 4f, 12f, 4f, true, Outer);

        return [.. boxes];
    }

    /// <summary>Writes one box's corners, in blocks, relative to its own pivot.</summary>
    public static void Emit(in ArmourBox box, List<ModelVertex> vertices, List<uint> indices)
    {
        var size = new Vector3(box.Width, box.Height, box.Depth);
        var inflate = new Vector3(box.Inflate);

        PlayerModel.EmitBox(
            box.Offset - inflate, box.Offset + size + inflate, size,
            box.U, box.V, box.Mirror, PlayerModel.Unit,
            new Vector2(ArmourArt.Width, ArmourArt.Height), vertices, indices);
    }

    /// <summary>
    /// Checks every box covers its part, reads a patch that is on the sheet, and stands clear of the
    /// skin without swallowing the body.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The stand-off pair is what a check has to assert, and neither half alone says anything.</b>
    /// Armour drawn at the body's own size z-fights and flickers; armour drawn too proud is a barrel.
    /// Both are "it renders" from every angle a count can see, so the numbers are compared against
    /// the skin's own overlay inflation, which is the one stand-off in this project known to work.
    /// </remarks>
    public static List<string> Validate()
    {
        var faults = new List<string>();
        var boxes = Build();

        var covered = new HashSet<EquipSlot>();

        foreach (var box in boxes)
        {
            covered.Add(box.Slot);

            if (box.Inflate <= PlayerModel.OverlayInflate)
                faults.Add($"the {box.Slot} plate on the {box.Part} stands off {box.Inflate}, "
                         + $"no further than the skin's own overlay at {PlayerModel.OverlayInflate}");

            if (box.Inflate > 1.5f)
                faults.Add($"the {box.Slot} plate on the {box.Part} stands off {box.Inflate}, which is a barrel");

            if (box.Sheet is < 0 or > 1) faults.Add($"the {box.Slot} plate reads sheet {box.Sheet}");

            var w = (int)MathF.Round(box.Width);
            var h = (int)MathF.Round(box.Height);
            var d = (int)MathF.Round(box.Depth);

            for (var face = 0; face < 6; face++)
            {
                var rect = PlayerModel.FaceRect(box.U, box.V, w, h, d, box.Mirror, face);
                if (rect.X >= 0 && rect.Y >= 0
                    && rect.X + rect.W <= ArmourArt.Width && rect.Y + rect.H <= ArmourArt.Height)
                    continue;

                faults.Add($"the {box.Slot} plate's face {face} runs off the "
                         + $"{ArmourArt.Width}x{ArmourArt.Height} sheet at {rect.X},{rect.Y}");
                break;
            }
        }

        // ⛔ The leggings must be TIGHTER than the chestplate, in the one place both cover.
        var chest = Array.Find(boxes, b => b is { Slot: EquipSlot.Chest, Part: PlayerPart.Body });
        var belt = Array.Find(boxes, b => b is { Slot: EquipSlot.Legs, Part: PlayerPart.Body });

        if (belt.Inflate >= chest.Inflate)
            faults.Add($"the leggings stand off {belt.Inflate} against the chestplate's {chest.Inflate}, "
                     + "so the two fight over the waist");

        foreach (var slot in (ReadOnlySpan<EquipSlot>)
                 [EquipSlot.Head, EquipSlot.Chest, EquipSlot.Legs, EquipSlot.Feet])
            if (!covered.Contains(slot)) faults.Add($"nothing is drawn for the {slot} slot");

        // And the winding, which is the fault the model itself shipped once and culling ate.
        foreach (var box in boxes)
        {
            var vertices = new List<ModelVertex>();
            var indices = new List<uint>();
            Emit(box, vertices, indices);

            for (var t = 0; t < indices.Count; t += 3)
            {
                var a = vertices[(int)indices[t]];
                var b = vertices[(int)indices[t + 1]];
                var c = vertices[(int)indices[t + 2]];

                if (Vector3.Dot(Vector3.Cross(b.Position - a.Position, c.Position - a.Position), a.Normal) > 0f)
                    continue;

                faults.Add($"the {box.Slot} plate on the {box.Part} winds inward");
                break;
            }
        }

        return faults;
    }
}
