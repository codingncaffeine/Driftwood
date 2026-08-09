using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.World;

namespace Driftwood.Core.Items;

/// <summary>
/// What a bucket does: takes one source out of the world, or puts one back.
/// </summary>
/// <remarks>
/// <para>⛳ <b>In Core and not in the input handler, because the rules are the interesting part and
/// none of them need a window.</b> Which cell a bucket reaches, what it refuses, and what it leaves
/// behind are all arithmetic — the audit runs the whole thing headlessly, which is the only way the
/// one rule that matters gets tested at all.</para>
/// <para>⛔ <b>THE RULE THAT MATTERS: a bucket fills from a SOURCE and from nothing else.</b> Letting
/// it scoop a flowing cell would be a hole straight through the save. A save stores no flowing fluid,
/// because at rest the flow is a function of the sources and the solids — take a flowing cell out and
/// the flow puts it straight back, so the world on disk and the world on screen would disagree about
/// something the player just did. Refusing is also what the genre does, and for once the two reasons
/// point the same way.</para>
/// <para>⚠ <b>A ray of its own, because a fluid is not targetable.</b> The crosshair passes through
/// water to the sea bed — it has to, or nothing underwater could ever be mined — so a bucket aimed at
/// a lake would otherwise reach the sand under it. This walks the same line and stops at the first
/// fluid instead of the first solid.</para>
/// </remarks>
public static class Buckets
{
    /// <summary>How far a bucket reaches, in blocks. The arm's length, same as building.</summary>
    public const float Reach = 5f;

    /// <summary>Step along the ray. A fifth of a block cannot skip a cell at this reach.</summary>
    private const float Step = 0.2f;

    /// <summary>
    /// The first fluid source a ray meets, before it meets anything that stops it.
    /// </summary>
    /// <returns>False when the ray reaches solid ground, or the end of its reach, first.</returns>
    public static bool TrySource(
        VoxelWorld world, FluidTable fluids, bool[] solid,
        Vector3 eye, Vector3 forward, out (int X, int Y, int Z) cell, out FluidKind kind)
    {
        cell = default;
        kind = FluidKind.None;

        var last = (X: int.MinValue, Y: int.MinValue, Z: int.MinValue);

        for (var t = 0f; t <= Reach; t += Step)
        {
            var p = eye + forward * t;
            var here = (
                X: (int)MathF.Floor(p.X),
                Y: (int)MathF.Floor(p.Y),
                Z: (int)MathF.Floor(p.Z));

            if (here == last) continue;
            last = here;

            var block = world.GetBlock(here.X, here.Y, here.Z).Value;

            // ⚠ Asked before the solid stop, because a waterlogged block is both: a targetable
            // thing AND a full still source. Stopping first would make the water inside a wet
            // fence unscoopable for ever, silently — the caller decides whether taking it leaves
            // the dry block or nothing.
            if (fluids.IsSource(block))
            {
                cell = here;
                kind = fluids.KindOf(block);
                return true;
            }

            // Anything you could stand on stops the ray, exactly as it stops the crosshair.
            if (solid[block]) return false;
        }

        return false;
    }

    /// <summary>The item a bucket becomes when it is filled from this fluid, or null.</summary>
    public static string? Filled(FluidKind kind) => kind switch
    {
        FluidKind.Water => "water_bucket",
        FluidKind.Lava => "lava_bucket",
        _ => null,
    };

    /// <summary>The fluid a full bucket empties, or none.</summary>
    public static FluidKind Holds(string item) => item switch
    {
        "water_bucket" => FluidKind.Water,
        "lava_bucket" => FluidKind.Lava,
        _ => FluidKind.None,
    };
}
