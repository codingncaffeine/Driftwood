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
    public static readonly (int X, int Y, int Z)[][] Corners =
    [
        // +X, viewed from +X: screen right is -Z, screen up is +Y.
        [(1, 0, 1), (1, 0, 0), (1, 1, 0), (1, 1, 1)],
        // -X, viewed from -X: screen right is +Z, screen up is +Y.
        [(0, 0, 0), (0, 0, 1), (0, 1, 1), (0, 1, 0)],
        // +Y, viewed from above: screen right is +X, screen up is +Z.
        [(0, 1, 0), (1, 1, 0), (1, 1, 1), (0, 1, 1)],
        // -Y, viewed from below: screen right is +X, screen up is -Z.
        [(0, 0, 1), (1, 0, 1), (1, 0, 0), (0, 0, 0)],
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
