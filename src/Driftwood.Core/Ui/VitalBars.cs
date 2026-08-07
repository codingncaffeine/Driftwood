using Driftwood.Core.Entities;
using Driftwood.Core.Items;

namespace Driftwood.Core.Ui;

/// <summary>Which row of icons over the hotbar, of the four there are.</summary>
public enum VitalBar
{
    /// <summary>Hearts, bottom left of the pair.</summary>
    Health,

    /// <summary>Armour, directly over the hearts.</summary>
    Armour,

    /// <summary>Drumsticks, bottom right.</summary>
    Food,

    /// <summary>Air, directly over the food.</summary>
    Breath,
}

/// <summary>
/// Where the four rows of vitals sit over the hotbar.
/// </summary>
/// <remarks>
/// <para>⛳ <b>In Core, and it is arithmetic rather than drawing</b> — the same argument as
/// <c>ScreenLayout</c>: what can go wrong here is two rows occupying the same pixels, and that is a
/// question about four numbers rather than about a framebuffer.</para>
/// <para>⛔ <b>It went wrong exactly once and nothing caught it.</b> Hunger was added measured from
/// the centre rightward, which is precisely where breath already ran — so air and food were drawn
/// over each other, and neither of them reports that. It was found by reading the code, which is not
/// a method. This exists so the next one is found by the gate.</para>
/// <para>⛳ <b>Two columns, not one row.</b> Health and armour on the left, food and air on the
/// right, each pair sharing an edge — so what threatens you is on one side and what sustains you is
/// on the other, and the crosshair keeps clear air round it.</para>
/// </remarks>
public static class VitalBars
{
    /// <summary>One notch of any bar, in layout units.</summary>
    public const float Icon = 9f;

    /// <summary>One pocket of the hotbar. The bars are measured against the rack, not the middle.</summary>
    public const float HotbarSlot = 22f;

    /// <summary>
    /// How many icons each bar is drawn as. Ten, every one of them.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>A bar is a PROPORTION of its maximum, not a tally of it — and armour proved why.</b> Its
    /// cap is twenty-four points, so one plate per two points made it twelve icons where the other
    /// three are ten; anchored to the same left edge that ran it past the middle of the screen and
    /// into the air bubbles. Ten across the board keeps the two columns the same width, which is what
    /// makes them read as a pair, and the torn fill already draws any fraction of an icon.
    /// ⚠ Written out rather than derived from the maxima for exactly that reason: the day one of
    /// those caps changes, the LAYOUT must not move.
    /// </remarks>
    public static int Icons(VitalBar bar) => 10;

    /// <summary>True for the two that hang off the right-hand end.</summary>
    public static bool OnTheRight(VitalBar bar) => bar is VitalBar.Food or VitalBar.Breath;

    /// <summary>True for the two on the upper row, over the other two.</summary>
    public static bool Upper(VitalBar bar) => bar is VitalBar.Armour or VitalBar.Breath;

    /// <summary>
    /// How far from the middle the outer end of either column sits: half the hotbar's own width.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Derived from the hotbar rather than written down.</b> The rack is what a player's eye
    /// lines these up against, so a hand-typed 99 is an alignment that silently stops being true the
    /// day the hotbar gains a tenth pocket — and nothing anywhere would fail.
    /// </remarks>
    public static float Span => Inventory.HotbarSlots * HotbarSlot / 2f;

    /// <summary>Where one bar starts, in layout units from the left of the screen.</summary>
    public static float Left(VitalBar bar, float width)
    {
        var middle = MathF.Round(width / 2f);
        return OnTheRight(bar)
            ? middle + Span - Icons(bar) * Icon
            : middle - Span;
    }

    /// <summary>And where it ends.</summary>
    public static float Right(VitalBar bar, float width) => Left(bar, width) + Icons(bar) * Icon;

    /// <summary>How far up from the bottom of the screen a bar's row sits.</summary>
    public static float FromBottom(VitalBar bar) => Upper(bar) ? 53f : 44f;

    /// <summary>
    /// That the four rows keep out of each other's way, and that the middle stays clear.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Overlap is checked between bars that share a ROW, which is the only place it can
    /// happen</b> — armour over hearts is not a collision, it is the arrangement. Asked as "do these
    /// two spans intersect" rather than as "is this one left of that one", because the second is true
    /// of a bar that has been moved so far it has wrapped past the other.
    /// </remarks>
    public static List<string> Validate(out string detail)
    {
        var faults = new List<string>();
        const float Width = 1280f;

        var bars = (VitalBar[])[VitalBar.Health, VitalBar.Armour, VitalBar.Food, VitalBar.Breath];

        foreach (var a in bars)
        foreach (var b in bars)
        {
            if (a >= b) continue;
            if (Upper(a) != Upper(b)) continue;

            var overlap = MathF.Min(Right(a, Width), Right(b, Width))
                        - MathF.Max(Left(a, Width), Left(b, Width));

            if (overlap > 0f)
                faults.Add($"{a} and {b} share a row and overlap by {overlap:F0} units, so one is "
                         + "drawn over the other");
        }

        // ⛳ THE GAP THE USER ASKED FOR. Not merely "they do not touch": a pair separated by one
        // pixel satisfies that and looks exactly like the pair they said was too close.
        var middle = MathF.Round(Width / 2f);
        var gapLow = Left(VitalBar.Food, Width) - Right(VitalBar.Health, Width);
        var gapHigh = Left(VitalBar.Breath, Width) - Right(VitalBar.Armour, Width);

        if (gapLow < Icon)
            faults.Add($"hearts and drumsticks are {gapLow:F0} units apart, under one icon's worth");

        if (gapHigh < Icon)
            faults.Add($"armour and air are {gapHigh:F0} units apart, under one icon's worth");

        // And the crosshair is in that gap rather than under a bar.
        if (Right(VitalBar.Health, Width) > middle || Left(VitalBar.Food, Width) < middle)
            faults.Add("a bar runs under the middle of the screen, where the crosshair is");

        // ⚠ The outer ends line up with the rack, which is the thing that makes it read as one block.
        var rack = Inventory.HotbarSlots * HotbarSlot;
        if (MathF.Abs(Right(VitalBar.Food, Width) - Left(VitalBar.Health, Width) - rack) > 0.5f)
            faults.Add("the bars no longer span the same width as the hotbar under them");

        detail = $"two columns {Span * 2f:F0} wide, the hotbar's own; {gapLow:F0} units of clear air "
               + $"down the middle and {gapHigh:F0} on the upper row, with the crosshair in it";

        return faults;
    }
}
