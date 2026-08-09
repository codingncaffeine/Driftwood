namespace Driftwood.Core.Blocks;

/// <summary>
/// The bed's rulebook: when sleep is allowed, where a night's sleep lands the clock, and the
/// names the two halves go by.
/// </summary>
/// <remarks>
/// <para>⛳ Sleep is an act on the CLOCK, and the clock is a pure function of one number — so the
/// whole mechanic is a window test and a constant, both here where the audit can walk every
/// minute of the day. The client's part is a click, a word of refusal, and remembering where
/// the bed stood.</para>
/// <para>⛔ The spawn a bed sets is only as good as the bed: a respawn asks whether a bed half
/// still stands at the remembered cell, and a missing bed falls back to the world spawn with a
/// word — silently waking somewhere unexpected is the genre's oldest bug report.</para>
/// </remarks>
public static class Beds
{
    /// <summary>The two halves' name stems; the facing runs foot → head.</summary>
    public const string FootStem = "bed_foot";

    public const string HeadStem = "bed_head";

    /// <summary>
    /// True in the window where a click on a bed means sleep rather than a refusal.
    /// </summary>
    /// <remarks>
    /// The clock puts sunrise at 0.25 and sunset at 0.75; the window opens a little after the
    /// sun is truly down and closes a little before it is truly up, so "you can only sleep at
    /// night" agrees with what the sky is doing rather than with a boundary nobody can see.
    /// </remarks>
    public static bool CanSleep(float timeOfDay) => timeOfDay is > 0.78f or < 0.22f;

    /// <summary>Where a night's sleep lands the clock: just after sunrise.</summary>
    public const float Morning = 0.26f;

    /// <summary>True of either half, whatever way it faces.</summary>
    public static bool IsBed(string name) =>
        name.StartsWith(FootStem, StringComparison.Ordinal)
        || name.StartsWith(HeadStem, StringComparison.Ordinal);
}
