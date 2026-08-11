using System.Numerics;
using Driftwood.Core.World;

namespace Driftwood.Core.Physics;

/// <summary>
/// Asking a block's actual shape whether something is inside it.
/// </summary>
/// <remarks>
/// ⛳ <b>One implementation, because there were three copies of the wrong one.</b> A dropped item, a
/// particle and a player each decided what they had hit by reading a <c>bool[]</c> keyed on the
/// block id — so a chip of stone came to rest at the bottom of a slab rather than on top of it, and
/// a dropped plank sat half inside one. The shapes come from
/// <see cref="Blocks.BlockRegistry.BuildCollisionTable"/>, which is the same table the player walks
/// on; the day a shape changes, all three follow.
/// </remarks>
public static class BlockShapes
{
    /// <summary>The first actual collision box crossed by a swept point.</summary>
    /// <param name="Fraction">Where along the segment the hit landed, from zero to one.</param>
    /// <param name="Normal">The outward normal of the face that was entered.</param>
    /// <param name="Face">That normal as a <see cref="Blocks.Faces"/> index.</param>
    public readonly record struct PointSweepHit(
        Vector3 Position, Vector3 Normal, float Fraction, int X, int Y, int Z, int Face);

    /// <summary>True when a point is inside some box of the block it is standing in.</summary>
    /// <remarks>
    /// A point rather than a box, which is what a grain of dust or a spinning item is. The cell is
    /// the one the point is in and no other: every collision box is clamped into its own cell, and
    /// the one thing that reaches past its own — a fence, half a block above itself — is not
    /// something a chip of stone needs to land on the top of.
    /// </remarks>
    public static bool Inside(
        (Vector3 Min, Vector3 Max)[][] shapes, VoxelWorld world, Vector3 at)
    {
        var x = (int)MathF.Floor(at.X);
        var y = (int)MathF.Floor(at.Y);
        var z = (int)MathF.Floor(at.Z);

        var boxes = shapes[world.GetBlock(x, y, z).Value];
        if (boxes.Length == 0) return false;

        foreach (var (min, max) in boxes)
        {
            if (at.X >= x + min.X && at.X <= x + max.X &&
                at.Y >= y + min.Y && at.Y <= y + max.Y &&
                at.Z >= z + min.Z && at.Z <= z + max.Z)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Sweeps a point through every collision box its segment can cross and returns the nearest.
    /// </summary>
    /// <remarks>
    /// <para>This is deliberately a segment/box test rather than a series of point samples. An arrow
    /// can travel several blocks in one slow frame; sampling its destination is how it passes through
    /// a wall, while choosing a very small step merely exchanges that bug for frame-time-dependent
    /// work. The segment is exact however long the frame was.</para>
    /// <para>The candidate cells are the segment's integer bounds, extended by one. Collision boxes
    /// normally stay inside their own cell, but fences are allowed to stand above it; the margin
    /// keeps that overhang part of the same physical shape for a projectile too.</para>
    /// </remarks>
    public static bool SweepPoint(
        (Vector3 Min, Vector3 Max)[][] shapes,
        VoxelWorld world,
        Vector3 from,
        Vector3 to,
        out PointSweepHit hit)
    {
        hit = default;
        var motion = to - from;
        if (motion.LengthSquared() < 1e-10f) return false;

        var low = Vector3.Min(from, to);
        var high = Vector3.Max(from, to);
        var minX = (int)MathF.Floor(low.X) - 1;
        var minY = (int)MathF.Floor(low.Y) - 1;
        var minZ = (int)MathF.Floor(low.Z) - 1;
        var maxX = (int)MathF.Floor(high.X) + 1;
        var maxY = (int)MathF.Floor(high.Y) + 1;
        var maxZ = (int)MathF.Floor(high.Z) + 1;

        var nearest = float.PositiveInfinity;
        var nearestNormal = Vector3.Zero;
        var nearestX = 0;
        var nearestY = 0;
        var nearestZ = 0;

        for (var z = minZ; z <= maxZ; z++)
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var boxes = shapes[world.GetBlock(x, y, z).Value];
            if (boxes.Length == 0) continue;

            var cell = new Vector3(x, y, z);
            foreach (var (localMin, localMax) in boxes)
            {
                if (!SegmentBox(
                        from, motion, cell + localMin, cell + localMax,
                        out var fraction, out var normal)
                    || fraction >= nearest)
                    continue;

                nearest = fraction;
                nearestNormal = normal;
                nearestX = x;
                nearestY = y;
                nearestZ = z;
            }
        }

        if (float.IsPositiveInfinity(nearest)) return false;

        hit = new PointSweepHit(
            from + motion * nearest,
            nearestNormal,
            nearest,
            nearestX,
            nearestY,
            nearestZ,
            FaceOf(nearestNormal));
        return true;
    }

    /// <summary>The slab intersection, restricted to one finite segment.</summary>
    private static bool SegmentBox(
        Vector3 origin,
        Vector3 motion,
        Vector3 min,
        Vector3 max,
        out float fraction,
        out Vector3 normal)
    {
        var enter = 0f;
        var leave = 1f;
        normal = Vector3.Zero;

        if (!ClipAxis(origin.X, motion.X, min.X, max.X, Vector3.UnitX,
                ref enter, ref leave, ref normal)
            || !ClipAxis(origin.Y, motion.Y, min.Y, max.Y, Vector3.UnitY,
                ref enter, ref leave, ref normal)
            || !ClipAxis(origin.Z, motion.Z, min.Z, max.Z, Vector3.UnitZ,
                ref enter, ref leave, ref normal))
        {
            fraction = 0f;
            return false;
        }

        fraction = enter;
        if (normal == Vector3.Zero) normal = -Vector3.Normalize(motion);
        return leave >= enter && enter is >= 0f and <= 1f;
    }

    private static bool ClipAxis(
        float origin,
        float motion,
        float min,
        float max,
        Vector3 axis,
        ref float enter,
        ref float leave,
        ref Vector3 normal)
    {
        if (MathF.Abs(motion) < 1e-8f) return origin >= min && origin <= max;

        var near = (min - origin) / motion;
        var far = (max - origin) / motion;
        var nearNormal = -axis;

        if (near > far)
        {
            (near, far) = (far, near);
            nearNormal = axis;
        }

        if (near > enter)
        {
            enter = near;
            normal = nearNormal;
        }

        leave = MathF.Min(leave, far);
        return leave >= enter;
    }

    private static int FaceOf(Vector3 normal)
    {
        if (normal.X > 0.5f) return Blocks.Faces.PosX;
        if (normal.X < -0.5f) return Blocks.Faces.NegX;
        if (normal.Y > 0.5f) return Blocks.Faces.PosY;
        if (normal.Y < -0.5f) return Blocks.Faces.NegY;
        if (normal.Z > 0.5f) return Blocks.Faces.PosZ;
        return Blocks.Faces.NegZ;
    }
}
