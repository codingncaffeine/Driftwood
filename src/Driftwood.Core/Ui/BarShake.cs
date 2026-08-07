namespace Driftwood.Core.Ui;

/// <summary>
/// The shiver a health or hunger bar takes on when there is almost nothing left of it.
/// </summary>
/// <remarks>
/// <para>⛳ <b>In Core rather than in the renderer, for the same reason <c>ScreenLayout</c> and
/// <c>HeldGrip</c> are.</b> It is arithmetic over four numbers — no texture, no window — and the way
/// it fails is invisible from a screenshot taken at one instant: a comparison the wrong way round
/// gives a bar that trembles from full health, which reads as the game being broken rather than as
/// the player being in trouble. That is a fault worth catching without starting the game.</para>
/// <para>⛔ <b>Whole pixels, and a square wave rather than a smooth one.</b> This project's pixel-art
/// rule is one whole screen pixel per layout unit and never a half — an icon nudged two thirds of a
/// pixel is one the sampler blurs, so a shake drawn that way reads as the bar going soft rather than
/// shaking. Snapping between two positions is also what a tremble looks like at nine pixels.</para>
/// </remarks>
public static class BarShake
{
    /// <summary>How many icons have to be left before a bar starts to shiver.</summary>
    /// <remarks>
    /// ⚠ Three, and it is the whole design. A row that trembles from the first scratch is a screen
    /// that looks broken; one that only trembles at the last icon says so too late to act on. Three
    /// is the point at which a player still has a choice about it.
    /// </remarks>
    public const int Within = 3;

    /// <summary>
    /// How far one icon is shifted this instant, in whole screen pixels. Zero for a healthy bar.
    /// </summary>
    /// <param name="drift">Seconds. The same value twice gives the same frame, which is what lets a
    /// check read a moving thing back off the framebuffer.</param>
    /// <param name="index">Which icon along the row, so they do not all jump together.</param>
    /// <param name="filled">How many icons still hold anything at all.</param>
    /// <remarks>
    /// ⛳ <b>Only the icons that still hold something shiver.</b> An empty socket jittering is noise;
    /// what the movement is for is drawing the eye to what is LEFT.
    /// ⚠ <b>Phase-shifted per icon</b>, or the group jumps as one and reads as the screen twitching.
    /// Quicker the less there is: three left is a shiver, one left is a shudder.
    /// </remarks>
    public static float Offset(float drift, int index, int filled)
    {
        if (filled <= 0 || filled > Within) return 0f;
        if (index >= filled) return 0f;

        var rate = 7f + (Within - filled) * 6f;
        return MathF.Sin(drift * rate + index * 2.3f) > 0f ? 1f : 0f;
    }

    /// <summary>
    /// That a full bar is still, a nearly-empty one moves, and only what is left of it does.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Both sides, and the still one is the arm that matters.</b> "It shakes when low" is
    /// satisfied by a build that shakes always — which is the version that would actually ship,
    /// because it is one comparison the wrong way round and it looks deliberate.</para>
    /// <para>⛔ <b>"Did it move" is asked by COMPARING SAMPLES, never by averaging them.</b> A square
    /// wave sampled across a second averages to about half its amplitude whether it is moving or
    /// frozen at that value, which is exactly the trap this project has paid for before. The offsets
    /// over a walk of time are collected and asked how many DISTINCT values appeared.</para>
    /// </remarks>
    public static List<string> Validate(out string detail)
    {
        var faults = new List<string>();

        // ── A bar with plenty left does not move, at any moment ────────────────────────────────
        var stillMoved = 0;
        for (var t = 0f; t < 4f; t += 1f / 60f)
        for (var i = 0; i < 10; i++)
            if (Offset(t, i, Within + 1) != 0f) stillMoved++;

        if (stillMoved > 0)
            faults.Add($"a bar with {Within + 1} icons left moved {stillMoved} times, so every bar "
                     + "in the game shivers permanently");

        // ── A bar down to its last few does move, and takes more than one position ─────────────
        foreach (var left in new[] { 1, 2, 3 })
        {
            var seen = new HashSet<float>();
            for (var t = 0f; t < 4f; t += 1f / 60f) seen.Add(Offset(t, 0, left));

            if (seen.Count < 2)
                faults.Add($"with {left} left the shiver held one position for four seconds, which "
                         + "is an icon that is not shaking but is drawn wrong");
        }

        // ── Only what is left shivers; the empty sockets past it stay put ──────────────────────
        var emptyMoved = 0;
        for (var t = 0f; t < 4f; t += 1f / 60f)
        for (var i = 2; i < 10; i++)
            if (Offset(t, i, 2) != 0f) emptyMoved++;

        if (emptyMoved > 0)
            faults.Add($"empty sockets past the last full icon moved {emptyMoved} times");

        // ── And it gets faster as it gets worse, which is the thing that reads as urgency ──────
        var rates = new int[Within + 1];
        for (var left = 1; left <= Within; left++)
        {
            var was = Offset(0f, 0, left);
            for (var t = 0f; t < 2f; t += 1f / 240f)
            {
                var now = Offset(t, 0, left);
                if (now != was) rates[left]++;
                was = now;
            }
        }

        if (rates[1] <= rates[Within])
            faults.Add($"one icon left flickers {rates[1]} times in two seconds against "
                     + $"{rates[Within]} at {Within}, so it does not quicken as it gets worse");

        detail = $"still above {Within} left; below it one whole pixel, {rates[Within]} flickers in "
               + $"two seconds at {Within} rising to {rates[1]} at one, and only the icons still "
               + "holding something move";

        return faults;
    }
}
