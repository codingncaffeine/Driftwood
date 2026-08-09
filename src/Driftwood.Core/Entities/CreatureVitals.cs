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
        ["wolf"] = 16,

        // The hostiles, for when they are placed. Written now because the numbers belong beside
        // each other — a table where the dangerous half is added later is a table that gets a
        // zombie with a cow's health in it.
        ["bat"] = 12,
        ["spider"] = 32,
        ["zombie"] = 40,
        ["drowned"] = 40,
        ["husk"] = 40,
        ["skeleton"] = 40,

        // ⛔ These two were filed under the REFERENCE'S names — creeper, enderman — which our
        // lookup would never ask for, so both would have spawned with a cow's default health.
        // Ours by our names, the whole table's own rule.
        ["crawler"] = 40,
        ["farwalker"] = 80,

        // ⚠ Soft on purpose: it closes slowly and in the open, so it is the hostile a new player
        // can actually beat — two swings of a bare fist. Its threat is arithmetic, not health.
        ["slime"] = 8,
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

        // The one beast with teeth. Only lands once provoked — see Retaliates.
        ["wolf"] = 3,
    };

    /// <summary>
    /// The kinds whose blow is a blast: they never swing, they light a fuse.
    /// </summary>
    /// <remarks>
    /// ⛳ A set for the same reason <see cref="Retaliators"/> is one — it is a fact about a kind,
    /// asked on the hunt path, and a bool on every creature would be a second copy of it.
    /// </remarks>
    private static readonly HashSet<string> Exploders = new(StringComparer.Ordinal) { "crawler" };

    /// <summary>
    /// Kinds that answer a blow instead of running from it.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>The third answer to being hit.</b> A beast flees, a hostile was already coming, and a
    /// wolf does neither until struck — the first creature in the game whose danger is entirely the
    /// player's own doing. Its packmates take it as personally as it does, which is what makes
    /// swinging at one of four a different decision from swinging at a stray.
    /// </remarks>
    private static readonly HashSet<string> Retaliators = new(StringComparer.Ordinal)
    {
        "wolf",

        // ⛳ The farwalker is the dark's wolf: it stands in the night harming nobody, and striking
        // one buys a fight with something that hits for 7 and does not forget. Spawned through the
        // hostile door but never born angry — see the client's spawn rule.
        "farwalker",
    };

    /// <summary>Kinds that answer trouble by being somewhere else — a teleport step.</summary>
    private static readonly HashSet<string> Blinkers = new(StringComparer.Ordinal) { "farwalker" };

    /// <summary>
    /// The kinds the sun does not agree with.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>The undead and only the undead — except the husk, and that exception IS the husk.</b>
    /// A spider that burned would make daylight a total answer rather than a partial one — the
    /// point of leaving kinds alive through the morning is that going outside is safer rather than
    /// safe. ⛔ The husk was in this set, which unmade it: a zombie that walks through noon is the
    /// whole of what a husk is, read off the reference the way the user asked. The drowned burns
    /// on land like its cousin — its shelter is the water it spawns in.
    /// </remarks>
    private static readonly HashSet<string> Burns =
        new(StringComparer.Ordinal) { "zombie", "drowned", "skeleton" };

    /// <summary>Half-hearts this kind starts with.</summary>
    public static int HealthFor(string kind) => Health.GetValueOrDefault(kind, DefaultHealth);

    /// <summary>True when this kind leaves something behind on its own.</summary>
    public static bool Sheds(string kind) => Shedders.Contains(kind);

    /// <summary>Half-hearts one of its blows takes off the player.</summary>
    public static int DamageFor(string kind) => Damage.GetValueOrDefault(kind, 0);

    /// <summary>True when a blow makes this kind come back at whoever struck it.</summary>
    public static bool Retaliates(string kind) => Retaliators.Contains(kind);

    /// <summary>True when this kind's attack is a fuse and a blast rather than a swing.</summary>
    public static bool Explodes(string kind) => Exploders.Contains(kind);

    /// <summary>True when this kind steps through space rather than being cornered.</summary>
    public static bool Blinks(string kind) => Blinkers.Contains(kind);

    /// <summary>True when full daylight sets this one alight.</summary>
    public static bool BurnsInDaylight(string kind) => Burns.Contains(kind);

    /// <summary>Every kind with a number written for it, for the check that they are all sensible.</summary>
    public static IEnumerable<string> Named => Health.Keys;
}
