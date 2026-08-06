using System.Numerics;

namespace Driftwood.Core.Entities;

/// <summary>One box of a creature: where it is, how big, and which patch of the sheet wraps it.</summary>
/// <param name="Origin">The box's minimum corner, in model units, in the model's own space.</param>
/// <param name="Size">Extent along x, y and z.</param>
/// <param name="U">Left edge of this box's net on the sheet, in texels.</param>
/// <param name="Inflate">Units the box grows by on every side. Non-zero only for overlays.</param>
public readonly record struct CreatureCube(
    Vector3 Origin, Vector3 Size, int U, int V, bool Mirror, float Inflate);

/// <summary>
/// One part of a creature: what turns, where it turns, and the boxes hanging off it.
/// </summary>
/// <param name="Parent">The bone this hangs from, or empty for a root.</param>
/// <param name="Pivot">Where it turns, in model units measured from between the feet.</param>
/// <param name="Rotation">
/// Degrees the bone is laid at before any animation. A quadruped's torso is modelled upright and
/// then tipped ninety degrees onto its side, so this is not decoration — a reader that drops it
/// builds every four-legged animal standing on its tail.
/// </param>
public readonly record struct CreatureBone(
    string Name, string Parent, Vector3 Pivot, Vector3 Rotation, CreatureCube[] Cubes);

/// <summary>
/// A creature's skeleton: the boxes it is made of and the sheet they are cut from.
/// </summary>
/// <remarks>
/// <para>The same shape as <see cref="PlayerModel"/>'s table, generalised. A player is one skeleton
/// written out by hand because there is exactly one of it and its layout is the format's most
/// documented; a cow, a wolf and a warden are ninety more, and none of them can be guessed at — see
/// <see cref="Validate"/> and the remark on <see cref="Bounds"/>.</para>
/// <para><b>Model space matches ours already:</b> +y up, −z forward, +x the creature's own right,
/// sixteen units to a block. Measured rather than assumed — a cow's head sits at z −14..−8 with its
/// body at −5..+5, so the head is in front at negative z, which is the convention
/// <see cref="PlayerModel"/> is written in.</para>
/// </remarks>
/// <param name="Inherits">
/// The model this one fills its gaps from, or empty. ⚠ Resolved across the whole folder rather than
/// within one file: a zombie's variants each sit in their own file and name a parent in another, so
/// a reader that resolves per file gives every one of them nought bones and no complaint.
/// </param>
public sealed record CreatureModel(
    string Name, int SheetWidth, int SheetHeight, CreatureBone[] Bones, string Inherits = "")
{
    public int CubeCount
    {
        get
        {
            var n = 0;
            foreach (var bone in Bones) n += bone.Cubes.Length;
            return n;
        }
    }

    /// <summary>The box every part of this creature fits inside, in model units.</summary>
    /// <remarks>
    /// Worth having for two reasons that are not display: it is what a collision box is sized from,
    /// and it is the cheapest smell test on a parse — a creature whose extent is four units or four
    /// hundred has been read wrong, whatever the file said.
    /// </remarks>
    public (Vector3 Min, Vector3 Max) Bounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var bone in Bones)
        foreach (var cube in bone.Cubes)
        {
            min = Vector3.Min(min, cube.Origin - new Vector3(cube.Inflate));
            max = Vector3.Max(max, cube.Origin + cube.Size + new Vector3(cube.Inflate));
        }

        return Bones.Length == 0 || CubeCount == 0 ? (Vector3.Zero, Vector3.Zero) : (min, max);
    }

    /// <summary>
    /// Checks every box's net lands on the sheet, in six patches of the right sizes.
    /// </summary>
    /// <remarks>
    /// <para>The same claim <see cref="PlayerModel.ValidateNet"/> makes, pointed at a parsed model
    /// rather than a hand-written one — and it is a stronger check here, because it is now testing a
    /// <em>reader</em>. A net that runs off the sheet samples whatever is beside it and a net whose
    /// patches are the wrong size puts an elbow on a kneecap; neither throws, neither shows up in a
    /// count of bones, and both draw perfectly happily.</para>
    /// <para>⚠ Overlapping patches are NOT a fault here, which is the one place this differs from the
    /// player's. Real models reuse a patch on purpose — a cow's two ears are one drawing mirrored,
    /// and every symmetric limb pair shares its net. The player's table can forbid it because it was
    /// written to; a reader cannot without failing correct files.</para>
    /// </remarks>
    public List<string> Validate()
    {
        var faults = new List<string>();

        foreach (var bone in Bones)
        {
            for (var c = 0; c < bone.Cubes.Length; c++)
            {
                var cube = bone.Cubes[c];
                var w = (int)MathF.Round(cube.Size.X);
                var h = (int)MathF.Round(cube.Size.Y);
                var d = (int)MathF.Round(cube.Size.Z);

                // A zero extent is legal — it is how the format draws a flat plane, and a warden's
                // ribbons and a fish's fins are made of them — so it is not a fault, but it has no
                // net either and asking about its patches is meaningless.
                if (w == 0 || h == 0 || d == 0) continue;

                for (var face = 0; face < 6; face++)
                {
                    var (rx, ry, rw, rh) = PlayerModel.FaceRect(cube.U, cube.V, w, h, d, cube.Mirror, face);

                    if (rx < 0 || ry < 0 || rx + rw > SheetWidth || ry + rh > SheetHeight)
                    {
                        faults.Add(
                            $"{Name}/{bone.Name}[{c}] face {face} at {rx},{ry} {rw}x{rh} runs off a "
                            + $"{SheetWidth}x{SheetHeight} sheet");
                        continue;
                    }

                    var expected = face switch
                    {
                        0 or 1 => (w, h),
                        2 or 3 => (d, h),
                        _ => (w, d),
                    };

                    if ((rw, rh) != expected)
                        faults.Add($"{Name}/{bone.Name}[{c}] face {face} is {rw}x{rh}, should be {expected.Item1}x{expected.Item2}");
                }
            }
        }

        // A bone naming a parent that is not in the file is a hierarchy with a hole in it, and the
        // part hangs off the world origin instead of off the creature.
        foreach (var bone in Bones)
        {
            if (bone.Parent.Length == 0) continue;
            if (Array.Exists(Bones, b => b.Name == bone.Parent)) continue;
            faults.Add($"{Name}/{bone.Name} hangs off '{bone.Parent}', which is not in the file");
        }

        return faults;
    }
}
