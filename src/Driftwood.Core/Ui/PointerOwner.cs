using System.Numerics;

namespace Driftwood.Core.Ui;

/// <summary>
/// Who has the pointer: the controller or the mouse, and whether the mouse has been put to sleep.
/// </summary>
/// <remarks>
/// <para>⛔ <b>USER, 2026-10-29: "the mouse just goes away when the game detects controller movement
/// and stays in the mode until you move your mouse ... inventory management isn't currently
/// possible with a controller, the mouse fights for priority."</b> The pad steered the inventory by
/// moving the pointer and then warping the window's own cursor after it. Under Wayland a window may
/// not move the cursor at all, so the real one stayed wherever the mouse was left, and the next
/// report from it — a hand resting on the mouse, a sensor's one-pixel shiver — matched nothing the
/// pad had done, handed the screen back to the mouse and snapped the pointer off the square.</para>
/// <para>⛳ <b>So nothing is warped any more, and a sleeping mouse must travel to wake.</b> The pad,
/// or the arrow keys walking squares, put the mouse to sleep: the drawn arrow goes, and the spot the
/// window system last reported is kept as an anchor. Reports within <see cref="WakeDistance"/> of it
/// are ignored outright; one beyond it, a button or the wheel wakes the mouse, and the arrow comes
/// back where the mouse really is. A key gives the prompts back to the keyboard without waking the
/// arrow, because a key says nothing about where the mouse is.</para>
/// <para>⚠ <b>The anchor is forgotten whenever the cursor mode changes.</b> Playing, the window system
/// reports a virtual position that runs on without limit; under a screen it reports the real one.
/// The first report after a switch is in the other frame, so it is taken as the new anchor rather
/// than read as the mouse having crossed the room.</para>
/// </remarks>
public sealed class PointerOwner
{
    /// <summary>How far, in window pixels, a sleeping mouse must move before it counts.</summary>
    public const float WakeDistance = 12f;

    private Vector2 _anchor;
    private bool _anchored;

    /// <summary>True while the controller was the last thing touched: its prompts, its navigation.</summary>
    public bool Pad { get; private set; }

    /// <summary>True while the drawn arrow is hidden because something other than the mouse steers.</summary>
    public bool MouseAsleep { get; private set; }

    /// <summary>The last position the window system reported, in its own pixels, since the cursor
    /// mode last changed.</summary>
    public Vector2? Reported { get; private set; }

    /// <summary>The controller was touched: it takes the screen and the mouse goes to sleep.</summary>
    public void PadTouched()
    {
        Pad = true;
        Sleep();
    }

    /// <summary>Something steered the pointer without the mouse, such as the arrow keys.</summary>
    public void Steered() => Sleep();

    /// <summary>A key went down: the prompts are the keyboard's, the arrow stays where it is.</summary>
    public void KeyPressed() => Pad = false;

    /// <summary>A mouse button or the wheel: always deliberate, so always a wake.</summary>
    public void MouseClicked() => Wake();

    /// <summary>The cursor mode changed, so the next report is the start of a new frame of reference.</summary>
    public void Reanchor()
    {
        _anchored = false;
        Reported = null;
    }

    /// <summary>
    /// A position report from the window system. True when it counts as the mouse moving.
    /// </summary>
    public bool Moved(Vector2 at)
    {
        Reported = at;
        if (!MouseAsleep) return true;

        if (!_anchored)
        {
            _anchor = at;
            _anchored = true;
            return false;
        }

        if (Vector2.DistanceSquared(at, _anchor) < WakeDistance * WakeDistance) return false;

        Wake();
        return true;
    }

    private void Sleep()
    {
        if (MouseAsleep) return;
        MouseAsleep = true;
        _anchored = Reported is not null;
        if (Reported is { } at) _anchor = at;
    }

    private void Wake()
    {
        Pad = false;
        MouseAsleep = false;
    }

    /// <summary>
    /// That a shivering mouse cannot take the screen from the pad, and a moved one always can.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Both arms.</b> "Small reports are ignored" is satisfied by a build that ignores every
    /// report, which is a mouse that never comes back — so the positive control (a real move wakes
    /// it) runs on the same instance right after the negative one.
    /// </remarks>
    public static List<string> SelfTest(out string detail)
    {
        var faults = new List<string>();
        var owner = new PointerOwner();

        if (!owner.Moved(new Vector2(400, 300))) faults.Add("an awake mouse's report was ignored");

        owner.PadTouched();
        if (!owner.Pad || !owner.MouseAsleep) faults.Add("touching the pad did not take the pointer");

        // The user's report: a hand on the mouse, a sensor shiver, across many frames.
        var shivers = 0;
        for (var i = 0; i < 200; i++)
        {
            var jitter = new Vector2(400 + (i % 5) - 2, 300 + (i % 3) - 1);
            if (owner.Moved(jitter)) shivers++;
        }
        if (shivers > 0 || !owner.Pad) faults.Add($"{shivers} shivers of the mouse took the screen from the pad");

        if (!owner.Moved(new Vector2(400 + WakeDistance + 1, 300))) faults.Add("a real move did not wake the mouse");
        if (owner.Pad || owner.MouseAsleep) faults.Add("a woken mouse left the pad in charge");

        // A key hands the prompts over and leaves the arrow asleep; only the mouse wakes it.
        owner.PadTouched();
        owner.KeyPressed();
        if (owner.Pad || !owner.MouseAsleep) faults.Add("a key woke the arrow or kept the pad's prompts");
        owner.MouseClicked();
        if (owner.MouseAsleep) faults.Add("a click did not wake the mouse");

        // Arrow keys walking squares put the mouse to sleep without giving the pad the prompts.
        owner.Steered();
        if (owner.Pad || !owner.MouseAsleep) faults.Add("steering with keys did not sleep the mouse");

        // Across a cursor-mode change the first report is a new origin, however far away it is.
        owner.Reanchor();
        if (owner.Moved(new Vector2(5000, -4000))) faults.Add("the first report after a mode change woke the mouse");
        if (owner.Moved(new Vector2(5003, -4002))) faults.Add("a shiver after a mode change woke the mouse");
        if (!owner.Moved(new Vector2(5040, -4000))) faults.Add("a real move after a mode change did not wake the mouse");

        // A pad touched before the mouse ever reported anchors on the first report.
        var fresh = new PointerOwner();
        fresh.PadTouched();
        if (fresh.Moved(new Vector2(10, 10))) faults.Add("the very first report woke a sleeping mouse");
        if (!fresh.Moved(new Vector2(60, 10))) faults.Add("a move after the first report did not wake it");

        detail = $"200 shivers of ±2 px ignored, a {WakeDistance + 1:F0} px move wakes; "
            + "keys hand back prompts only, a mode change re-anchors";
        return faults;
    }
}
