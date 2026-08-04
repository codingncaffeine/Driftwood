using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.World;

namespace Driftwood.Core.Physics;

/// <summary>What a ray ran into.</summary>
/// <param name="X">Block hit.</param>
/// <param name="Face">Index into <see cref="Faces"/> of the side entered through.</param>
/// <param name="Distance">How far along the ray the hit was.</param>
public readonly record struct RayHit(int X, int Y, int Z, int Face, float Distance)
{
    /// <summary>The empty cell the ray was in when it hit — where a placed block goes.</summary>
    public (int X, int Y, int Z) Adjacent
    {
        get
        {
            var n = Faces.Normals[Face];
            return (X + n.X, Y + n.Y, Z + n.Z);
        }
    }
}

/// <summary>
/// Walks a ray through the voxel grid, one cell at a time, and reports the first one that stops it.
/// </summary>
/// <remarks>
/// <para>Grid traversal rather than stepping along the ray in small increments. Sampling at fixed
/// intervals is the obvious thing and it is wrong in both directions at once: too coarse and the
/// ray skips a block at a glancing angle, too fine and most of the work is spent testing the same
/// cell repeatedly. Advancing to the next grid plane visits every cell the ray actually crosses,
/// exactly once, however long the ray is.</para>
/// <para>It also falls out of the algorithm which <em>face</em> was entered through, because the
/// axis whose plane was crossed last is the axis of the face. That matters: placing a block needs
/// the empty cell in front of the hit, not the hit itself, and reconstructing that afterwards from
/// a hit position alone is guesswork at a corner.</para>
/// </remarks>
public static class BlockRay
{
    /// <summary>
    /// Casts against blocks the predicate calls solid. Unloaded space is empty, so a ray fired at
    /// a chunk that has not arrived reports a miss rather than a hit on nothing.
    /// </summary>
    public static bool TryCast(
        VoxelWorld world, bool[] stops, Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit)
    {
        hit = default;

        var lengthSquared = direction.LengthSquared();
        if (lengthSquared < 1e-8f) return false;
        direction /= MathF.Sqrt(lengthSquared);

        var x = (int)MathF.Floor(origin.X);
        var y = (int)MathF.Floor(origin.Y);
        var z = (int)MathF.Floor(origin.Z);

        var stepX = direction.X > 0 ? 1 : -1;
        var stepY = direction.Y > 0 ? 1 : -1;
        var stepZ = direction.Z > 0 ? 1 : -1;

        // Distance along the ray between successive planes of each axis. Infinite for an axis the
        // ray does not move along, which then simply never wins the comparison below.
        var deltaX = direction.X != 0f ? MathF.Abs(1f / direction.X) : float.PositiveInfinity;
        var deltaY = direction.Y != 0f ? MathF.Abs(1f / direction.Y) : float.PositiveInfinity;
        var deltaZ = direction.Z != 0f ? MathF.Abs(1f / direction.Z) : float.PositiveInfinity;

        var nextX = DistanceToFirstPlane(origin.X, direction.X, x, stepX, deltaX);
        var nextY = DistanceToFirstPlane(origin.Y, direction.Y, y, stepY, deltaY);
        var nextZ = DistanceToFirstPlane(origin.Z, direction.Z, z, stepZ, deltaZ);

        // The cell the eye is already inside counts. Standing with your head in a block and
        // looking at it should target it, not the one behind.
        if (stops[world.GetBlock(x, y, z).Value])
        {
            hit = new RayHit(x, y, z, Faces.PosY, 0f);
            return true;
        }

        var travelled = 0f;
        while (travelled <= maxDistance)
        {
            int face;

            if (nextX < nextY && nextX < nextZ)
            {
                x += stepX;
                travelled = nextX;
                nextX += deltaX;
                face = stepX > 0 ? Faces.NegX : Faces.PosX;
            }
            else if (nextY < nextZ)
            {
                y += stepY;
                travelled = nextY;
                nextY += deltaY;
                face = stepY > 0 ? Faces.NegY : Faces.PosY;
            }
            else
            {
                z += stepZ;
                travelled = nextZ;
                nextZ += deltaZ;
                face = stepZ > 0 ? Faces.NegZ : Faces.PosZ;
            }

            if (travelled > maxDistance) return false;
            if (!stops[world.GetBlock(x, y, z).Value]) continue;

            hit = new RayHit(x, y, z, face, travelled);
            return true;
        }

        return false;
    }

    /// <summary>How far along the ray the first grid plane on this axis is.</summary>
    private static float DistanceToFirstPlane(float origin, float direction, int cell, int step, float delta)
    {
        if (float.IsInfinity(delta)) return float.PositiveInfinity;

        var boundary = step > 0 ? cell + 1 : cell;
        return (boundary - origin) / direction;
    }
}
