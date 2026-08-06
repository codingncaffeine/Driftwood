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

    /// <summary>The most hostiles the dark may hold at once.</summary>
    public const int HostileCap = 5;

    /// <summary>The most one attempt may place, however empty the night is.</summary>
    /// <remarks>
    /// ⛔ <b>This is the whole of "scale it back".</b> The spawner used to refill the entire deficit
    /// in one go, once a second: kill one and it was back before the body had faded. A cap on the
    /// batch turns a set point with infinite gain into a rate, so a night <em>builds</em> instead of
    /// arriving complete, and clearing the ground round a house buys real quiet.
    /// </remarks>
    public const int HostileBatch = 2;

    /// <summary>How far away the nearest one may appear, in blocks.</summary>
    /// <remarks>
    /// ⚠ <b>Twelve was too close.</b> At twelve blocks a thing is inside the fog and already coming
    /// — an ambush rather than a threat. Far enough out to be seen crossing the ground toward you is
    /// what makes a torch and a wall feel like decisions instead of decoration.
    /// </remarks>
    public const float HostileMinRadius = 24f;

    /// <summary>Seconds between attempts, at the two ends of the roll.</summary>
    private const float TryLow = 5f;
    private const float TryHigh = 14f;

    /// <summary>
    /// How likely an attempt is to place anything at all, given how many are already about.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>A rate that falls as the night fills, rather than a deficit corrected instantly.</b> One
    /// at zero and none at the cap, straight in between — so the first hour of dark is quiet, the
    /// middle of the night is busy, and a player who has just cleared their doorstep is not handed
    /// the whole set back on the next tick.
    /// </remarks>
    public static float Pressure(int living, int cap)
    {
        if (cap <= 0) return 0f;
        return Math.Clamp(1f - living / (float)cap, 0f, 1f);
    }

    /// <summary>Seconds until the next attempt, from a roll in 0..1.</summary>
    /// <remarks>
    /// ⚠ <b>Rolled fresh every time rather than fixed.</b> A spawner on a metronome is a thing a
    /// player learns the beat of; the whole point of the dark is not knowing when.
    /// </remarks>
    public static float NextAttempt(double roll) =>
        TryLow + (float)Math.Clamp(roll, 0.0, 1.0) * (TryHigh - TryLow);
}
