using System.Numerics;

namespace Driftwood.Core.Net;

/// <summary>What the host allows a guest to do, in one place both the game and the checks call.</summary>
public static class CoopRules
{
    /// <summary>
    /// How far from a guest's last pose a cell may be changed. A hand reaches about five blocks;
    /// the rest is slack for a pose that is a tenth of a second old at a sprint.
    /// </summary>
    public const float Reach = 10f;

    public static bool InReach(Vector3 eye, int x, int y, int z) =>
        Vector3.DistanceSquared(eye, new Vector3(x + 0.5f, y + 0.5f, z + 0.5f)) <= Reach * Reach;
}
