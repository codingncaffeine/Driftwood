using System.Numerics;
using Driftwood.Core.Entities;

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

    /// <summary>How far a guest's arrow or spell may land: as far as it is told about creatures, and a little.</summary>
    public const float RangedReach = CreaturesMessage.Radius + 8f;

    /// <summary>The most a guest's single blow may claim: a little over the best weapon with every bonus.</summary>
    public const int MaxStrike = 40;

    /// <summary>
    /// A guest's blow on one of the host's creatures, landed through the herd's own <c>Hurt</c> when it
    /// is fair: the creature exists and lives, and is within <see cref="Reach"/> of the guest's last pose.
    /// </summary>
    /// <returns>True when the blow landed (the creature was not still ringing from the last).</returns>
    public static bool LandStrike(CreatureHerd? herd, CreatureRoster roster, Vector3 guestAt, StrikeMessage strike, out Creature? struck)
    {
        struck = null;
        if (herd is null || roster.Find(strike.Creature) is not { Alive: true } creature) return false;
        var reach = strike.Ranged ? RangedReach : Reach;
        if (Vector3.DistanceSquared(guestAt + new Vector3(0f, 1.6f, 0f), creature.Middle) > reach * reach) return false;

        var before = creature.Health;
        herd.Hurt(creature, Math.Clamp(strike.HalfHearts, 1, MaxStrike), strike.From);
        if (creature.Health == before) return false;
        struck = creature;
        return true;
    }
}
