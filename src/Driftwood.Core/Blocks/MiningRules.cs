namespace Driftwood.Core.Blocks;

/// <summary>
/// Turns a block's hardness into how long it takes to break.
/// </summary>
/// <remarks>
/// <para>One formula in one place, so tool tiers at P6 arrive as an argument rather than as a
/// rewrite. Hardness stays a property of the block and speed stays a property of whatever is
/// swinging; the two only meet here.</para>
/// <para>The genre's own numbers are a base rate and a much steeper penalty for using the wrong
/// thing — steep enough that stone by hand is most of ten seconds, which works because a pickaxe is
/// four blocks of wood away. Nothing can be crafted here yet, so the penalty is softened until
/// there is a tool to reward: see <see cref="WithoutToolPenalty"/>.</para>
/// </remarks>
public static class MiningRules
{
    /// <summary>How many steps of cracking show before a block goes. The genre's number.</summary>
    public const int Stages = 10;

    /// <summary>Seconds per hardness unit when the right thing is being used.</summary>
    public const float SecondsPerHardness = 1.5f;

    /// <summary>
    /// Extra cost for going at a block bare-handed that wanted a tool.
    /// </summary>
    /// <remarks>
    /// The genre uses about 3.3x here. This is deliberately gentler, and only until P6: a penalty
    /// that severe is a signal telling the player to go and craft something, and while there is
    /// nothing to craft it is just a long wait with no lesson in it. Restore it with tool tiers.
    /// </remarks>
    public const float WithoutToolPenalty = 1.5f;

    /// <summary>Fastest anything can be taken, so a very soft block still reads as a swing.</summary>
    public const float FloorSeconds = 0.2f;

    /// <summary>How long this block takes to break bare-handed. Infinite if it never does.</summary>
    public static float SecondsToBreak(BlockType type)
    {
        if (type.Unbreakable) return float.PositiveInfinity;

        var seconds = type.Hardness * SecondsPerHardness;
        if (type.NeedsTool) seconds *= WithoutToolPenalty;

        return MathF.Max(seconds, FloorSeconds);
    }

    /// <summary>Which cracking stage a part-broken block shows, 0 to <see cref="Stages"/> - 1.</summary>
    public static int StageFor(float progress) =>
        Math.Clamp((int)(progress * Stages), 0, Stages - 1);
}
