using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.World;

namespace Driftwood.Core.Gen;

/// <summary>
/// The Core Hollow: one sealed cavern at the floor of the world, where the wyrm lives.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The reference's End, ours, and in the same world.</b> The Sanctum's gate does not open
/// onto another dimension; it carries whoever steps in down to a dome cut out of the rock above the
/// molten sea — a floor of polished deepstone, a ring of pillars with ember crystals on them, a
/// podium at the middle, and a landing at the south edge where the gate sets you down.</para>
/// <para>⚠ <b>Sealed.</b> Its shell is lined two cells thick, so no lava from the Emberdeep round it
/// can flow in, and nothing digs into it by accident: it is under the world's spawn, at the very
/// bottom, where nobody's existing edits are. The only way in is the gate.</para>
/// <para>A pure function of position like the rest of the generator — nothing is stored.</para>
/// </remarks>
public static class CoreHollow
{
    /// <summary>The middle of the floor.</summary>
    public static readonly Vector3 Centre = new(0.5f, Floor, 0.5f);

    /// <summary>The cell the floor's top is in, and how wide and high the dome is.</summary>
    public const int Floor = -216;
    public const int Radius = 44;
    public const int Height = 40;

    /// <summary>Where the gate sets a traveller down.</summary>
    public static readonly Vector3 Landing = new(0.5f, Floor + 1, 36.5f);

    /// <summary>The pillars the ember crystals stand on: where, and how tall.</summary>
    public static readonly (int X, int Z, int Tall)[] Pillars = Ring();

    private static (int X, int Z, int Tall)[] Ring()
    {
        var pillars = new (int, int, int)[8];
        for (var i = 0; i < pillars.Length; i++)
        {
            var angle = i * MathF.PI / 4f + MathF.PI / 8f;
            pillars[i] = ((int)MathF.Round(MathF.Cos(angle) * 28f), (int)MathF.Round(MathF.Sin(angle) * 28f),
                10 + (i * 5 % 4) * 3);
        }
        return pillars;
    }

    /// <summary>Where each pillar's crystal sits.</summary>
    public static IEnumerable<(int X, int Y, int Z)> Crystals =>
        Pillars.Select(p => (p.X, Floor + 1 + p.Tall, p.Z));

    /// <summary>The block box anything here can touch, for the generator's quick refusal.</summary>
    public static bool Touches(int minX, int minY, int minZ, int maxX, int maxY, int maxZ) =>
        maxX >= -Radius - 3 && minX <= Radius + 3 && maxZ >= -Radius - 3 && minZ <= Radius + 3
        && maxY >= Floor - 3 && minY <= Floor + Height + 3;

    /// <summary>
    /// What the Hollow puts in a cell, or null where it leaves the ground as the generator made it.
    /// </summary>
    public static BlockId? At(int x, int y, int z, StarterBlocks.Ids ids)
    {
        if (y < Floor - 2 || y > Floor + Height + 3) return null;
        var flat = MathF.Sqrt(x * x + z * z);
        if (flat > Radius + 3) return null;

        // The dome: an ellipse in section, flat on the floor.
        var up = MathF.Max(0f, y - Floor) / Height;
        var shape = (flat / Radius) * (flat / Radius) + up * up;

        if (y <= Floor)
        {
            if (flat > Radius + 2) return null;
            return y == Floor ? ids.PolishedDeepstone : ids.DeepstoneBricks;
        }

        if (shape >= 1.15f) return null;

        // ⛳ The shell, with emberstone set through it so the fight can be seen: the dome is the
        // only place in the world no daylight or torch has ever reached, and dark was unreadable.
        if (shape >= 1f) return Math.Abs(x * 7 + z * 13 + y * 3) % 17 == 0 ? ids.Emberstone : ids.DeepstoneBricks;

        // The podium, five across and two high, where the egg comes to rest.
        if (Math.Abs(x) <= 2 && Math.Abs(z) <= 2 && y <= Floor + 2) return ids.DeepstoneBricks;

        // The pillars, three across, and the crystal on each.
        foreach (var (px, pz, tall) in Pillars)
        {
            if (Math.Abs(x - px) > 1 || Math.Abs(z - pz) > 1) continue;
            if (y <= Floor + tall) return ids.DeepstoneBricks;
            if (y == Floor + tall + 1 && x == px && z == pz) return ids.EmberCrystal;
        }

        return BlockId.Air;
    }
}
