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
}
