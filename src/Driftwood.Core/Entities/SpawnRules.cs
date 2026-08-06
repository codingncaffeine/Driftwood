using Driftwood.Core.Lighting;

namespace Driftwood.Core.Entities;

/// <summary>
/// Where a thing may appear: how dark it is, and whether the sky can reach it.
/// </summary>
/// <remarks>
/// <para>⛔ <b>Two axes, and until fluids landed there was only one.</b> "Is this cell dark" put any
/// hostile anywhere dark and put the bat in fields with the cows, because darkness alone cannot tell
/// a cave from a meadow at midnight. The second axis is free: sky light is what a cell would get at
/// <em>noon</em>, independent of the clock, so it already answers "is there a way out above you".
/// </para>
/// <para>⛳ <b>In Core rather than in the input host, because the interesting part is a truth table
/// and it needs no window.</b> A field at midnight and a cave at noon are the two cases the whole
/// design turns on and they are one line each to check here; through the renderer they would need a
/// world, a clock and somebody watching.</para>
/// </remarks>
public static class SpawnRules
{
    /// <summary>Light at or below which something will appear in a cell. The genre's own line.</summary>
    public const int Darkness = 7;

    /// <summary>The brightest of the three block channels — what "is there a torch here" means.</summary>
    public static int BlockLight(ushort packed) => LightValue.BlockPeak(packed);

    /// <summary>
    /// True where it is dark <em>now</em>, wherever that is.
    /// </summary>
    /// <param name="daylight">How far up the sun is, 0 at night to 1 with it properly up.</param>
    /// <remarks>
    /// ⚠ <b>Sky light scaled by the sun, because raw it says a field is bright at midnight</b> and
    /// nothing would ever appear above ground. A torch and the sun are compared as the brighter of
    /// the two, or one of the two kinds of light stops mattering.
    /// </remarks>
    public static bool Dark(ushort packed, float daylight) =>
        MathF.Max(LightValue.Sky(packed) * daylight, BlockLight(packed)) <= Darkness;

    /// <summary>
    /// True where the sky cannot reach at all, whatever the hour.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>RAW sky light, not the scaled kind.</b> The scaled one answers "is it dark here now",
    /// which at midnight is true of an open field; this answers "could the sun ever reach here",
    /// which is a fact about the place rather than about the time. Using the scaled one is exactly
    /// how a cave animal ends up in a meadow.
    /// </remarks>
    public static bool Buried(ushort packed) =>
        LightValue.Sky(packed) == 0 && BlockLight(packed) <= Darkness;
}
