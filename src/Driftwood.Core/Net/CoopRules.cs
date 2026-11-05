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

    /// <summary>
    /// How far from a guest's last pose a stack may be picked up (N5): the magnet's reach, and slack
    /// for a pose a tenth of a second old at a sprint and a stack still settling.
    /// </summary>
    public const float PickupReach = 6f;

    /// <summary>How far from a guest's last pose a stack it threw may appear: a throw, a spill, a block it broke.</summary>
    public const float DropReach = Reach + 2f;

    /// <summary>How far from a guest's last pose a container may be opened: a hand's reach and the same slack.</summary>
    public const float ContainerReach = Reach;

    /// <summary>A guest walked into a stack on the host's ground: lifted off it when it is near enough and ready.</summary>
    public static bool TakeDrop(Items.DroppedItems ground, Vector3 guestAt, uint id, out Items.ItemStack stack) =>
        ground.TryTake(id, guestAt + new Vector3(0f, 0.9f, 0f), PickupReach, out stack);

    /// <summary>
    /// A stack a guest threw, laid on the host's ground: where the guest says when that is beside the
    /// guest, at the guest's middle otherwise; never more than one slot of it, never a wild speed.
    /// </summary>
    public static bool PlaceDrop(Items.DroppedItems ground, Items.ItemRegistry items, Vector3 guestAt, DropItemMessage drop)
    {
        var stack = drop.Stack;
        if (stack.IsEmpty || stack.Item.Value >= items.Count) return false;
        stack = stack with { Count = Math.Min(stack.Count, items[stack.Item].MaxStack) };

        var middle = guestAt + new Vector3(0f, 0.9f, 0f);
        var at = float.IsFinite(drop.Position.X + drop.Position.Y + drop.Position.Z)
                 && Vector3.DistanceSquared(drop.Position, middle) <= DropReach * DropReach
            ? drop.Position
            : middle;
        var velocity = float.IsFinite(drop.Velocity.X + drop.Velocity.Y + drop.Velocity.Z)
            ? Vector3.Clamp(drop.Velocity, new Vector3(-12f), new Vector3(12f))
            : Vector3.Zero;
        ground.Place(stack, at, velocity);
        return true;
    }

    /// <summary>A sign's lines as anyone may write them: four, each no longer than a sign holds, nothing unprintable.</summary>
    public static string[] CleanSign(IReadOnlyList<string> lines)
    {
        var clean = new string[Blocks.SignBank.Lines];
        for (var i = 0; i < clean.Length; i++)
        {
            var line = i < lines.Count ? lines[i] ?? "" : "";
            line = new string(line.Where(c => !char.IsControl(c)).ToArray());
            clean[i] = line.Length > Blocks.SignBank.MaxChars ? line[..Blocks.SignBank.MaxChars] : line;
        }
        return clean;
    }

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
