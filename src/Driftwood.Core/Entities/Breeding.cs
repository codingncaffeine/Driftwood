namespace Driftwood.Core.Entities;

/// <summary>
/// What a field feeds — which food courts which animal, and the clocks round a birth.
/// </summary>
/// <remarks>
/// <para>⛳ <b>This is the more valuable half of farming.</b> A crop that only feeds the player is
/// a meal; a crop that turns a found herd into a kept one is a reason the field exists. Wheat is
/// what the genre feeds a cow, and the rest follow the reference's own pairings where we have the
/// food — the pig eats the root crops, the fox is bought with berries.</para>
/// <para>⛳ <b>In Core, for the sowing rule's reason:</b> the pairings, the courtship, the meeting
/// and the calf all run under <c>--audit</c> with no window. The client's part is one gesture and
/// a noise.</para>
/// </remarks>
public static class Breeding
{
    /// <summary>Seconds a fed adult stays courting, waiting for another.</summary>
    public const float CourtSeconds = 30f;

    /// <summary>Seconds after a birth before either parent will court again.</summary>
    public const float RestSeconds = 300f;

    /// <summary>Seconds a newborn takes to reach full size on its own.</summary>
    public const float GrowSeconds = 1200f;

    /// <summary>How much of the way to grown one meal moves a young one.</summary>
    public const float FeedGrowth = 0.1f;

    /// <summary>Blocks within which two courting animals find each other.</summary>
    public const float PairRange = 8f;

    /// <summary>And how close they have to stand before there is a calf.</summary>
    public const float MeetRange = 1.6f;

    /// <summary>
    /// Which foods court which animal. A kind not listed is not bred by hand at all.
    /// </summary>
    /// <remarks>
    /// ⚠ The wolf is deliberately absent: feeding a wolf is taming, which is ownership and a
    /// faction and a saved companion — M3's work, not a breeding table's. The cat waits on fish,
    /// the squid on nobody.
    /// </remarks>
    private static readonly Dictionary<string, string[]> Foods = new(StringComparer.Ordinal)
    {
        ["cow"] = ["wheat"],
        ["sheep"] = ["wheat"],
        ["chicken"] = ["seeds"],
        ["pig"] = ["carrot", "potato", "beetroot"],
        ["rabbit"] = ["carrot"],
        ["fox"] = ["berries"],
    };

    /// <summary>True when this item is this kind's courting food.</summary>
    public static bool Takes(string kind, string item) =>
        Foods.TryGetValue(kind, out var foods) && Array.IndexOf(foods, item) >= 0;

    /// <summary>Every kind a player can breed, for the checks.</summary>
    public static IEnumerable<string> Fed => Foods.Keys;

    /// <summary>Every food the table names, for the check that they are all real items.</summary>
    public static IEnumerable<string> AllFoods()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var foods in Foods.Values)
        foreach (var food in foods)
            if (seen.Add(food)) yield return food;
    }
}
