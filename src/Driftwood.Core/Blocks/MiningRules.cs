using Driftwood.Core.Items;

namespace Driftwood.Core.Blocks;

/// <summary>
/// Turns a block's hardness and whatever is swinging at it into how long it takes, and into
/// whether anything is left behind.
/// </summary>
/// <remarks>
/// <para>One formula in one place. Hardness stays a property of the block and speed stays a
/// property of the tool; the two only meet here, which is why adding a tier is a row in a table
/// rather than an edit to fifty block entries.</para>
/// <para>Both halves of the rule live together on purpose. "How long" and "do you get anything"
/// are the same question asked twice — a pickaxe that halves the time but yields nothing is a
/// different game from one that yields but takes as long — and splitting them across two files is
/// how they drift apart.</para>
/// </remarks>
public static class MiningRules
{
    /// <summary>How many steps of cracking show before a block goes. The genre's number.</summary>
    public const int Stages = 10;

    /// <summary>Seconds per hardness unit with the right tool at speed one.</summary>
    public const float SecondsPerHardness = 1.5f;

    /// <summary>
    /// Extra cost for going at a block bare-handed that wanted a tool.
    /// </summary>
    /// <remarks>
    /// The genre's own figure, restored now that there is something to craft. It was held at 1.5
    /// through P3 because a penalty this steep is a signal telling the player to go and make a
    /// pickaxe, and while there was no crafting it was only a long wait with no lesson in it.
    /// Stone by hand is seven and a half seconds; with the wooden pickaxe that is four planks and
    /// two sticks away, it is one and a bit.
    /// </remarks>
    public const float WithoutToolPenalty = 3.33f;

    /// <summary>Fastest anything can be taken, so a very soft block still reads as a swing.</summary>
    public const float FloorSeconds = 0.2f;

    /// <summary>True when this tool is good enough to bring the block up rather than only break it.</summary>
    /// <remarks>
    /// A block below the tier line comes apart and leaves nothing, which is the genre's rule and the
    /// reason a tier ladder is a ladder rather than a speed setting. Anything with no tier at all
    /// yields to a bare hand, so ordinary digging is never gated.
    /// </remarks>
    public static bool CanHarvest(BlockType block, ItemType? held) =>
        block.HarvestTier <= 0
        || (held is not null && held.Tool == block.HarvestClass && held.Tier >= block.HarvestTier);

    /// <summary>How long this block takes to break with that in hand. Infinite if it never does.</summary>
    public static float SecondsToBreak(BlockType block, ItemType? held)
    {
        if (block.Unbreakable) return float.PositiveInfinity;

        var seconds = block.Hardness * SecondsPerHardness;

        // The right class of tool speeds the work whether or not it is good enough to keep what
        // comes out. A wooden pickaxe on stormglass is quicker and still leaves rubble on the floor.
        if (held is not null && held.Tool != ToolClass.None && held.Tool == block.HarvestClass)
            seconds /= MathF.Max(held.MiningSpeed, 0.01f);

        if (!CanHarvest(block, held)) seconds *= WithoutToolPenalty;

        return MathF.Max(seconds, FloorSeconds);
    }

    /// <summary>Which cracking stage a part-broken block shows, 0 to <see cref="Stages"/> - 1.</summary>
    public static int StageFor(float progress) =>
        Math.Clamp((int)(progress * Stages), 0, Stages - 1);
}
