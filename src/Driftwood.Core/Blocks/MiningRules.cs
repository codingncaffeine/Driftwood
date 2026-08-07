using Driftwood.Core.Items;

namespace Driftwood.Core.Blocks;

/// <summary>
/// Turns a block's hardness and whatever is swinging at it into how long it takes, whether anything
/// is left behind, and whether the swing does anything at all.
/// </summary>
/// <remarks>
/// <para>One formula in one place. Hardness stays a property of the block and speed stays a
/// property of the tool; the two only meet here, which is why adding a tier is a row in a table
/// rather than an edit to fifty block entries.</para>
/// <para>All three halves of the rule live together on purpose. "How long", "do you get anything"
/// and "does the rock move at all" are the same question asked three ways — a pickaxe that halves
/// the time but yields nothing is a different game from one that yields but takes as long — and
/// splitting them across files is how they drift apart.</para>
/// <para>⛔ <b>THE OLD RULE WAS SILENT AND THE LADDER RAN BACKWARDS.</b> Being under-tiered denied
/// the drop and nothing else, so a player learned they had the wrong pickaxe <em>after</em> spending
/// seven seconds on a rock that then crumbled to nothing. And every ore was Hardness 3 while tool
/// speed ran 2, 4, 6, 8 — so measured with the <em>minimum viable</em> pickaxe, coal took 2.25 s,
/// iron 1.13 s, gold 0.75 s and stormglass 0.56 s. <b>The deeper ore was the quicker one.</b></para>
/// </remarks>
public static class MiningRules
{
    /// <summary>How many steps of cracking show before a block goes. The genre's number.</summary>
    public const int Stages = 10;

    /// <summary>Seconds per hardness unit with the right tool at speed one.</summary>
    public const float SecondsPerHardness = 1.5f;

    /// <summary>
    /// How many tiers under a block a tool may be and still move it at all.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>One, which makes the whole ladder one sentence: you can always reach exactly one
    /// rung above your pickaxe, slowly.</b> Every block in the game is then in one of three states
    /// against what you are holding — yours, one-rung-slow, or refused — and a player learns which
    /// by swinging once.</para>
    /// <para>⛔ <b>This is the number that forced deepstone down to tier 1.</b> Deepstone is 98.6% of
    /// everything below y 0 and it is the rock you <em>tunnel through</em> rather than a prize. At
    /// tier 2 a player whose last pickaxe broke at y −200 could not break it bare-handed at all,
    /// which is not a difficulty setting but a way to lose a save. The tier ladder is for ORE.</para>
    /// </remarks>
    public const int MaxDeficit = 1;

    /// <summary>
    /// What each tier of deficit multiplies the time by. Index by the deficit itself.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>3.33 is the genre's own figure and it is kept.</b> It was already the penalty for going
    /// at a block bare-handed and it is already calibrated — stone by hand is seven and a half
    /// seconds against one and a bit with the first wooden pickaxe. What changed is that it is now
    /// keyed on <em>how far under</em> rather than on a yes-or-no.
    /// </remarks>
    public static readonly float[] DeficitPenalty = [1f, 3.33f];

    /// <summary>Fastest anything can be taken, so a very soft block still reads as a swing.</summary>
    public const float FloorSeconds = 0.2f;

    /// <summary>
    /// How many tiers short of this block the thing in hand is. Zero when it is enough.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The wrong class of tool counts as tier zero, not as its own tier.</b> An iron axe is not
    /// a nearly-good-enough pickaxe; it is not a pickaxe. That is what keeps "hold the right kind of
    /// tool" a separate lesson from "hold a good enough one", and it is why a bare hand and a sword
    /// answer the same here.
    /// </remarks>
    public static int Deficit(BlockType block, ItemType? held)
    {
        if (block.HarvestTier <= 0) return 0;

        var tier = held is not null && held.Tool == block.HarvestClass ? held.Tier : 0;
        return Math.Max(0, block.HarvestTier - tier);
    }

    /// <summary>
    /// True when this swing does nothing whatever — no progress, no wear, no drop.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>A refusal rather than a very long wait, at the user's own direction</b>, and the reason
    /// it costs nothing is theirs too: a player who cannot break a thing should not be spending a
    /// pickaxe finding that out. It is announced by pitching the block's own hit sound down, which
    /// is the difference between "this is too hard" and "the game has stopped responding".
    /// </remarks>
    public static bool TooHard(BlockType block, ItemType? held) =>
        block.Unbreakable || Deficit(block, held) > MaxDeficit;

    /// <summary>
    /// True when this tool is good enough to bring the block up rather than only break it.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>The CLASS decides the drop; the TIER only costs time.</b> That is the user's own call
    /// and it is a better rule than the one it replaced: being one rung short now means the ore
    /// takes fifteen seconds instead of three rather than crumbling to nothing, so the ladder is a
    /// cost you can choose to pay. Holding the wrong <em>kind</em> of tool still yields nothing,
    /// because "go and make a pickaxe" is the first lesson the game teaches and it needs to stay.
    /// </remarks>
    public static bool CanHarvest(BlockType block, ItemType? held) =>
        block.HarvestTier <= 0
        || (held is not null && held.Tool == block.HarvestClass && !TooHard(block, held));

    /// <summary>How long this block takes to break with that in hand. Infinite if it never does.</summary>
    public static float SecondsToBreak(BlockType block, ItemType? held)
    {
        if (TooHard(block, held)) return float.PositiveInfinity;

        var seconds = block.Hardness * SecondsPerHardness;

        // The right class of tool speeds the work whether or not it is good enough to keep what
        // comes out. A stone pickaxe on gold ore is quicker than a fist and still leaves the gold.
        if (held is not null && held.Tool != ToolClass.None && held.Tool == block.HarvestClass)
            seconds /= MathF.Max(held.MiningSpeed, 0.01f);

        var deficit = Math.Min(Deficit(block, held), DeficitPenalty.Length - 1);
        seconds *= DeficitPenalty[deficit];

        return MathF.Max(seconds, FloorSeconds);
    }

    /// <summary>
    /// The cheapest thing that would bring this block up, for a message that says so.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>Found by walking the items rather than written into a table.</b> The answer is already
    /// implied by the tool set — the lowest-tier tool of the right class that reaches the block's
    /// tier — and a second table saying the same thing is a second table to get wrong the day a
    /// tier is inserted. Null when nothing in the game is good enough, which should never happen and
    /// is checked.
    /// </remarks>
    public static ItemType? NeededFor(BlockType block, ItemRegistry items)
    {
        if (block.HarvestTier <= 0 || block.Unbreakable) return null;

        ItemType? best = null;

        foreach (var type in items.All)
        {
            if (type.Tool != block.HarvestClass || type.Tier < block.HarvestTier) continue;
            if (best is null || type.Tier < best.Tier) best = type;
        }

        return best;
    }

    /// <summary>Which cracking stage a part-broken block shows, 0 to <see cref="Stages"/> - 1.</summary>
    public static int StageFor(float progress) =>
        Math.Clamp((int)(progress * Stages), 0, Stages - 1);
}
