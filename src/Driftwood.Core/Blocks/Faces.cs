namespace Driftwood.Core.Blocks;

/// <summary>
/// The six cube faces, their normals, and their corner winding. This ordering is baked into
/// the packed vertex format and into the shader's normal table, so it must not be reshuffled.
/// </summary>
public static class Faces
{
    public const int PosX = 0;
    public const int NegX = 1;
    public const int PosY = 2;
    public const int NegY = 3;
    public const int PosZ = 4;
    public const int NegZ = 5;

    public const int Count = 6;

    /// <summary>Outward normal per face, in <see cref="PosX"/>..<see cref="NegZ"/> order.</summary>
    public static readonly (int X, int Y, int Z)[] Normals =
    [
        (1, 0, 0),
        (-1, 0, 0),
        (0, 1, 0),
        (0, -1, 0),
        (0, 0, 1),
        (0, 0, -1),
    ];

    /// <summary>
    /// The four corners of each face as unit offsets from the block's minimum corner, wound
    /// counter-clockwise as seen from outside the block. Front faces are CCW, so the renderer
    /// can cull back faces without a per-face orientation test.
    /// </summary>
    /// <remarks>
    /// Verified by <see cref="ValidateWinding"/>, not by eye. Deriving these from "screen right
    /// is +X, screen up is +Z" reasoning is how the +Y and -Y faces first shipped inverted: the
    /// horizontal faces have no natural screen-up, so the mental camera silently flipped between
    /// them. The cross-product test has no such ambiguity.
    /// </remarks>
    public static readonly (int X, int Y, int Z)[][] Corners =
    [
        // +X, viewed from +X: screen right is -Z, screen up is +Y.
        [(1, 0, 1), (1, 0, 0), (1, 1, 0), (1, 1, 1)],
        // -X, viewed from -X: screen right is +Z, screen up is +Y.
        [(0, 0, 0), (0, 0, 1), (0, 1, 1), (0, 1, 0)],
        // +Y, seen from above.
        [(0, 1, 1), (1, 1, 1), (1, 1, 0), (0, 1, 0)],
        // -Y, seen from below.
        [(0, 0, 0), (1, 0, 0), (1, 0, 1), (0, 0, 1)],
        // +Z, viewed from +Z: screen right is +X, screen up is +Y.
        [(0, 0, 1), (1, 0, 1), (1, 1, 1), (0, 1, 1)],
        // -Z, viewed from -Z: screen right is -X, screen up is +Y.
        [(1, 0, 0), (0, 0, 0), (0, 1, 0), (1, 1, 0)],
    ];

    /// <summary>
    /// Per face, per corner, the three neighbour offsets that occlude that corner — the two
    /// in-plane edge neighbours followed by the diagonal. All offsets are relative to the cell
    /// <em>in front of</em> the face (block + normal), which is the only cell the AO test cares
    /// about.
    /// </summary>
    /// <remarks>
    /// Derived rather than hand-written. For a face whose normal runs along one axis, a corner's
    /// two occluders lie along the other two axes, in the direction that corner sits relative to
    /// the face centre. Transcribing 72 offsets by hand is how sign errors get in.
    /// </remarks>
    public static readonly (int X, int Y, int Z)[][][] AoOffsets = BuildAoOffsets();

    /// <summary>
    /// Checks every face's winding against its declared normal, returning one line per fault and
    /// nothing when the table is sound.
    /// </summary>
    /// <remarks>
    /// A quad wound counter-clockwise as seen from outside has a right-hand-rule normal — the
    /// cross product of two consecutive edges — that points outward. If it points inward, the
    /// renderer culls the face you were meant to see and keeps the one you were not. There is no
    /// visual tell beyond "things look see-through", and it is invisible to a block census, so it
    /// needs its own check.
    /// </remarks>
    public static IReadOnlyList<string> ValidateWinding()
    {
        var faults = new List<string>();

        for (var face = 0; face < Count; face++)
        {
            var c = Corners[face];
            if (c.Length != 4)
            {
                faults.Add($"face {face}: {c.Length} corners, expected 4");
                continue;
            }

            var e1 = (X: c[1].X - c[0].X, Y: c[1].Y - c[0].Y, Z: c[1].Z - c[0].Z);
            var e2 = (X: c[2].X - c[1].X, Y: c[2].Y - c[1].Y, Z: c[2].Z - c[1].Z);

            var cross = (
                X: e1.Y * e2.Z - e1.Z * e2.Y,
                Y: e1.Z * e2.X - e1.X * e2.Z,
                Z: e1.X * e2.Y - e1.Y * e2.X);

            var n = Normals[face];
            if (cross.X != n.X || cross.Y != n.Y || cross.Z != n.Z)
                faults.Add($"face {face}: winding normal ({cross.X},{cross.Y},{cross.Z}) != declared ({n.X},{n.Y},{n.Z})");

            // All four corners must sit on the face's own plane, or the quad is not flat.
            var axis = n.X != 0 ? 0 : n.Y != 0 ? 1 : 2;
            var expected = (n.X + n.Y + n.Z) > 0 ? 1 : 0;
            for (var i = 0; i < 4; i++)
            {
                var v = axis == 0 ? c[i].X : axis == 1 ? c[i].Y : c[i].Z;
                if (v != expected)
                    faults.Add($"face {face} corner {i}: off-plane on axis {axis} ({v}, expected {expected})");
            }
        }

        return faults;
    }

    private static (int X, int Y, int Z)[][][] BuildAoOffsets()
    {
        var table = new (int X, int Y, int Z)[Count][][];

        for (var face = 0; face < Count; face++)
        {
            var n = Normals[face];
            var faceAxis = n.X != 0 ? 0 : n.Y != 0 ? 1 : 2;

            table[face] = new (int X, int Y, int Z)[4][];
            for (var corner = 0; corner < 4; corner++)
            {
                var c = Corners[face][corner];
                int[] comps = [c.X, c.Y, c.Z];

                // The two axes in the face's plane, and which way this corner leans on each.
                var inPlane = new (int X, int Y, int Z)[2];
                var found = 0;
                for (var axis = 0; axis < 3; axis++)
                {
                    if (axis == faceAxis) continue;
                    var sign = comps[axis] == 1 ? 1 : -1;
                    inPlane[found++] = axis switch
                    {
                        0 => (sign, 0, 0),
                        1 => (0, sign, 0),
                        _ => (0, 0, sign),
                    };
                }

                var o1 = inPlane[0];
                var o2 = inPlane[1];
                table[face][corner] =
                [
                    o1,
                    o2,
                    (o1.X + o2.X, o1.Y + o2.Y, o1.Z + o2.Z),
                ];
            }
        }

        return table;
    }
}
