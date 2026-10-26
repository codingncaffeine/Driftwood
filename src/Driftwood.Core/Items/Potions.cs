using Driftwood.Core.Entities;

namespace Driftwood.Core.Items;

/// <summary>
/// One potion: what it does, how strongly and for how long, whether it is thrown, and its colour.
/// </summary>
/// <param name="Effect">Null for the bases — the water bottle and the awkward potion — which do nothing.</param>
/// <param name="Level">1, or 2 for a strengthened one.</param>
/// <param name="Seconds">How long it lasts; zero for the instant ones and the bases.</param>
public sealed record Potion(
    string Name, string Label, Ailment? Effect, int Level, float Seconds, bool Splash,
    (byte R, byte G, byte B) Colour);

/// <summary>
/// The brewing table: every potion there is, and what each ingredient turns each one into.
/// </summary>
/// <remarks>
/// <para>⛳ <b>The reference's brewing, with this world's ingredients.</b> A water bottle and ashcap
/// make the awkward base; then sugar is swiftness, cinder dust strength, a wail tear regeneration,
/// ember cream fire resistance, glowcap night vision, seagrass water breathing, sunwort healing and a
/// red toadstool (or a spider's eye) poison. Rotten flesh — or the fermented eye — corrupts — swiftness to slowness, healing and poison to
/// harming, and plain water to weakness — the way the reference's fermented eye does. Tidewire
/// draws a brew out longer (the reference's redstone), azurite makes it stronger (its glowstone),
/// and gunpowder makes any of them a splash potion to throw.</para>
/// <para>⚠ <b>Every potion is its own item.</b> This world's stacks carry no data, so a long
/// strength and a strong one are two names; they share one picture per colour, which is what the
/// reference shows too.</para>
/// </remarks>
public static class Potions
{
    /// <summary>What each effect is called and coloured, with its plain, long and strong timings.</summary>
    private readonly record struct Kind(
        Ailment Effect, string Name, (byte, byte, byte) Colour, float Plain, float Long, float Strong);

    private static readonly Kind[] Kinds =
    [
        new(Ailment.Swiftness, "swiftness", (124, 175, 198), 180f, 480f, 90f),
        new(Ailment.Slowness, "slowness", (90, 108, 129), 90f, 240f, 20f),
        new(Ailment.Strength, "strength", (147, 36, 35), 180f, 480f, 90f),
        new(Ailment.Weakness, "weakness", (72, 77, 72), 90f, 240f, 0f),
        new(Ailment.Regeneration, "regeneration", (205, 92, 171), 45f, 90f, 22f),
        new(Ailment.FireResistance, "fire_resistance", (228, 154, 58), 180f, 480f, 0f),
        new(Ailment.NightVision, "night_vision", (31, 31, 161), 180f, 480f, 0f),
        new(Ailment.WaterBreathing, "water_breathing", (46, 82, 153), 180f, 480f, 0f),
        new(Ailment.Poison, "poison", (78, 147, 49), 45f, 90f, 21f),

        // The two instant ones: no length to draw out, only a strength.
        new(Ailment.Healing, "healing", (248, 36, 35), 0f, -1f, 0f),
        new(Ailment.Harming, "harming", (67, 10, 9), 0f, -1f, 0f),
    ];

    private static readonly (byte, byte, byte) Water = (56, 93, 198);

    /// <summary>Every potion, the bases first.</summary>
    public static IReadOnlyList<Potion> All { get; } = Build();

    private static readonly Dictionary<string, Potion> ByName =
        All.ToDictionary(potion => potion.Name, StringComparer.Ordinal);

    public static bool TryGet(string name, out Potion potion) => ByName.TryGetValue(name, out potion!);

    /// <summary>The picture a potion is drawn with: one per colour, bottle or splash.</summary>
    public static int IconIndex(Potion potion)
    {
        if (potion.Effect is not { } effect) return potion.Splash ? 1 : 0;
        var kind = Array.FindIndex(Kinds, one => one.Effect == effect);
        return 2 + kind * 2 + (potion.Splash ? 1 : 0);
    }

    /// <summary>How many pictures <see cref="IconIndex"/> asks for.</summary>
    public static int IconCount => 2 + Kinds.Length * 2;

    /// <summary>The colour each picture is painted in, and whether it is a splash bottle.</summary>
    public static ((byte R, byte G, byte B) Colour, bool Splash) Icon(int index) =>
        index < 2 ? (Water, index == 1) : (Kinds[(index - 2) / 2].Colour, (index - 2) % 2 == 1);

    private static List<Potion> Build()
    {
        var all = new List<Potion>
        {
            new("water_bottle", "water bottle", null, 0, 0f, false, Water),
            new("awkward_potion", "awkward potion", null, 0, 0f, false, Water),
        };

        foreach (var splash in (ReadOnlySpan<bool>)[false, true])
        foreach (var kind in Kinds)
        {
            var stem = splash ? $"splash_potion_{kind.Name}" : $"potion_{kind.Name}";
            var words = (splash ? "splash potion of " : "potion of ") + kind.Name.Replace('_', ' ');
            all.Add(new(stem, words, kind.Effect, 1, kind.Plain, splash, kind.Colour));
            if (kind.Long > 0f) all.Add(new(stem + "_long", words + " (long)", kind.Effect, 1, kind.Long, splash, kind.Colour));
            if (kind.Strong > 0f || kind.Long < 0f)
                all.Add(new(stem + "_strong", words + " II", kind.Effect, 2, kind.Strong, splash, kind.Colour));
        }

        return all;
    }

    /// <summary>The base each effect is brewed from the awkward potion with.</summary>
    private static readonly Dictionary<string, string> FromAwkward = new(StringComparer.Ordinal)
    {
        ["sugar"] = "swiftness",
        ["cinder_dust"] = "strength",
        ["wail_tear"] = "regeneration",
        ["ember_cream"] = "fire_resistance",
        ["glowcap"] = "night_vision",
        ["seagrass"] = "water_breathing",
        ["sunwort"] = "healing",
        ["mushroom_red"] = "poison",
        ["spider_eye"] = "poison",
    };

    /// <summary>What rotten flesh turns each effect into.</summary>
    private static readonly Dictionary<string, string> Corrupted = new(StringComparer.Ordinal)
    {
        ["swiftness"] = "slowness",
        ["healing"] = "harming",
        ["poison"] = "harming",
    };

    /// <summary>Every item that does something in the top of a brewing stand.</summary>
    public static IEnumerable<string> Ingredients =>
        FromAwkward.Keys.Concat(["ashcap", "rotten_flesh", "fermented_spider_eye", "tidewire", "azurite", "gunpowder"]);

    /// <summary>The corruptors: rotten flesh, and the reference's own fermented eye (2026-10-26).</summary>
    private static bool Corrupts(string ingredient) => ingredient is "rotten_flesh" or "fermented_spider_eye";

    /// <summary>
    /// What one bottle becomes with one ingredient added, or null when the two do nothing together.
    /// </summary>
    public static string? Brew(string bottle, string ingredient)
    {
        if (bottle == "water_bottle")
            return ingredient switch
            {
                "ashcap" => "awkward_potion",
                _ when Corrupts(ingredient) => "potion_weakness",
                _ => null,
            };

        if (bottle == "awkward_potion")
            return FromAwkward.TryGetValue(ingredient, out var made) ? "potion_" + made : null;

        if (!TryGet(bottle, out var potion) || potion.Effect is null) return null;

        var splash = potion.Splash ? "splash_potion_" : "potion_";
        var rest = bottle[splash.Length..];
        var variant = rest.EndsWith("_long", StringComparison.Ordinal) ? "_long"
            : rest.EndsWith("_strong", StringComparison.Ordinal) ? "_strong" : "";
        var effect = rest[..(rest.Length - variant.Length)];

        string? Exists(string name) => ByName.ContainsKey(name) ? name : null;

        return ingredient switch
        {
            "gunpowder" when !potion.Splash => Exists("splash_potion_" + rest),
            "tidewire" when variant != "_long" => Exists(splash + effect + "_long"),
            "azurite" when variant != "_strong" => Exists(splash + effect + "_strong"),
            _ when Corrupts(ingredient) && Corrupted.TryGetValue(effect, out var turned) =>
                Exists(splash + turned + variant) ?? Exists(splash + turned),
            _ => null,
        };
    }

    /// <summary>The table's own checks: every rule lands on a real potion, and every potion can be brewed.</summary>
    public static List<string> Validate(ItemRegistry items)
    {
        var faults = new List<string>();

        foreach (var potion in All)
            if (!items.TryByName(potion.Name, out _)) faults.Add($"potion {potion.Name} is not an item");
        foreach (var ingredient in Ingredients)
            if (!items.TryByName(ingredient, out _)) faults.Add($"brewing ingredient {ingredient} is not an item");

        // Everything is reachable from a water bottle.
        var made = new HashSet<string>(StringComparer.Ordinal) { "water_bottle" };
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var bottle in made.ToArray())
            foreach (var ingredient in Ingredients)
                if (Brew(bottle, ingredient) is { } next && made.Add(next)) changed = true;
        }

        foreach (var potion in All)
            if (!made.Contains(potion.Name)) faults.Add($"{potion.Name} cannot be brewed from a water bottle");

        return faults;
    }
}
