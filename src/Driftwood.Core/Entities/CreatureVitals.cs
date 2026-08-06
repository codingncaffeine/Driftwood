namespace Driftwood.Core.Entities;

/// <summary>
/// How much punishment each creature takes, and which of them leave something behind on their own.
/// </summary>
/// <remarks>
/// <para>⛳ <b>A table keyed on our name, kept apart from the model and apart from the drops.</b>
/// Three questions get asked about a creature and they belong to three different things: what shape
/// it is (<see cref="StarterCreatures"/>), how much it can take (here), and what it leaves
/// (<c>CreatureDrops</c>). Fusing any two of them means a kind that has one and not the others
/// cannot exist — and a bat with no drops, a chicken with no model of ours, and a spider we have
/// art for and no numbers for are all states this has been in.</para>
/// <para>Half-hearts, the same unit <see cref="PlayerVitals"/> keeps the player's in. Anything else
/// would mean a conversion between what hits a cow and what a cow hits back with.</para>
/// </remarks>
public static class CreatureVitals
{
    /// <summary>What anything not named below has. A middling animal.</summary>
    public const int DefaultHealth = 20;

    /// <summary>Roughly how often one that sheds does, in seconds.</summary>
    /// <remarks>
    /// Five minutes, varied per animal. Long enough that a coop is a thing worth building and short
    /// enough that one is worth building at all — a player who fences four birds in gets an egg
    /// about every eighty seconds, which is a reason to walk back and look.
    /// </remarks>
    public const float ShedsEvery = 300f;

    /// <summary>Seconds a fleece takes to grow back after it has been taken.</summary>
    public const float RegrowSeconds = 240f;

    private static readonly Dictionary<string, int> Health = new(StringComparer.Ordinal)
    {
        // The beasts. A chicken is the one everything can kill in a swing, which is what makes it
        // the first thing a player with bare hands actually goes for.
        ["cow"] = 20,
        ["pig"] = 20,
        ["sheep"] = 16,
        ["chicken"] = 8,
        ["frog"] = 6,
        ["rabbit"] = 6,
        ["squid"] = 20,

        // The hostiles, for when they are placed. Written now because the numbers belong beside
        // each other — a table where the dangerous half is added later is a table that gets a
        // zombie with a cow's health in it.
        ["bat"] = 12,
        ["spider"] = 32,
        ["zombie"] = 40,
        ["drowned"] = 40,
        ["husk"] = 40,
        ["skeleton"] = 40,
        ["creeper"] = 40,
        ["enderman"] = 80,
    };

    /// <summary>Kinds that leave something behind without being touched.</summary>
    /// <remarks>
    /// ⛔ <b>A set rather than a flag on every creature.</b> Nearly nothing sheds, and a clock
    /// running on every animal in the world to answer "no" once every frame is the cost of asking
    /// the question in the wrong place.
    /// </remarks>
    private static readonly HashSet<string> Shedders = new(StringComparer.Ordinal) { "chicken" };

    /// <summary>Half-hearts a blow from each takes off. Anything absent hits for nothing.</summary>
    /// <remarks>
    /// ⚠ <b>Against a player's twenty.</b> A zombie needs seven blows and a spider nine, which is
    /// between four and seven seconds of standing still — long enough that being caught is a mistake
    /// rather than an accident, and short enough that three of them at once is a night indoors.
    /// </remarks>
    private static readonly Dictionary<string, int> Damage = new(StringComparer.Ordinal)
    {
        ["zombie"] = 3,
        ["drowned"] = 3,
        ["husk"] = 3,
        ["skeleton"] = 2,
        ["spider"] = 2,
        ["crawler"] = 6,
        ["farwalker"] = 7,
        ["slime"] = 2,
    };

    /// <summary>
    /// The kinds the sun does not agree with.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>The undead and only the undead.</b> A spider that burned would make daylight a total
    /// answer rather than a partial one — the point of leaving one kind alive through the morning is
    /// that going outside is safer rather than safe, which is a more interesting world than either
    /// extreme. It is also the genre's own line and it is drawn in the right place.
    /// </remarks>
    private static readonly HashSet<string> Burns =
        new(StringComparer.Ordinal) { "zombie", "husk", "skeleton" };

    /// <summary>Half-hearts this kind starts with.</summary>
    public static int HealthFor(string kind) => Health.GetValueOrDefault(kind, DefaultHealth);

    /// <summary>True when this kind leaves something behind on its own.</summary>
    public static bool Sheds(string kind) => Shedders.Contains(kind);

    /// <summary>Half-hearts one of its blows takes off the player.</summary>
    public static int DamageFor(string kind) => Damage.GetValueOrDefault(kind, 0);

    /// <summary>True when full daylight sets this one alight.</summary>
    public static bool BurnsInDaylight(string kind) => Burns.Contains(kind);

    /// <summary>Every kind with a number written for it, for the check that they are all sensible.</summary>
    public static IEnumerable<string> Named => Health.Keys;
}
