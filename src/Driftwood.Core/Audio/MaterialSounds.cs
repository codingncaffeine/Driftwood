namespace Driftwood.Core.Audio;

/// <summary>What a block sounds like when it is walked on, struck, broken or put down.</summary>
/// <remarks>
/// Coarser than the block list on purpose. Fifty-one blocks share about a dozen surfaces, and a
/// table keyed on the surface is one a person can read and check; keyed on the block it would be
/// fifty rows saying "stone" and one of them would be wrong.
/// </remarks>
public enum SoundMaterial
{
    Stone,
    Dirt,
    Grass,
    Sand,
    Gravel,
    Snow,
    Wood,
    Leaves,
    Plant,
    Metal,
    Glass,
    Cloth,
    Water,
}

/// <summary>Which of a material's sound sets is wanted.</summary>
public enum SoundEvent
{
    /// <summary>A foot landing on it.</summary>
    Step,

    /// <summary>A blow landing on it while it is being mined.</summary>
    Hit,

    /// <summary>It giving way.</summary>
    Break,

    /// <summary>It being put down.</summary>
    Place,
}

/// <summary>
/// The one place a material is turned into file names.
/// </summary>
/// <remarks>
/// <para>Every entry here is a real file in <c>assets/sounds</c>, and the audit refuses a build
/// where one is not — a table pointing at a sound nobody shipped is silent in exactly the way a
/// working game is silent, and there is no other way to notice.</para>
/// <para>Several are stand-ins and say so. The pack has no dirt or sand footsteps and nothing at
/// all for breaking soft ground, which is the most common action in the whole game; grass and
/// gravel are the nearest things it does have. They are marked in the plan's sound tally so the
/// shopping list stays honest rather than quietly closing once something plays.</para>
/// </remarks>
public static class MaterialSounds
{
    private static readonly string[] GrassSteps =
        ["digital_footstep_grass_1", "digital_footstep_grass_2", "digital_footstep_grass_3", "digital_footstep_grass_4"];

    private static readonly string[] GravelSteps =
        ["digital_footstep_gravel_1", "digital_footstep_gravel_2", "digital_footstep_gravel_3", "digital_footstep_gravel_4"];

    private static readonly string[] WoodSteps =
        ["digital_footstep_wood_1", "digital_footstep_wood_2", "digital_footstep_wood_3", "digital_footstep_wood_4"];

    private static readonly string[] SnowSteps =
        ["digital_footstep_snow_1", "digital_footstep_snow_2", "digital_footstep_snow_3", "digital_footstep_snow_4"];

    private static readonly string[] StoneSteps =
        ["foley_footstep_concrete_1", "foley_footstep_concrete_2", "foley_footstep_concrete_3", "foley_footstep_concrete_4"];

    private static readonly string[] ClothSteps =
        ["foley_footstep_carpet_1", "foley_footstep_carpet_2", "foley_footstep_carpet_3", "foley_footstep_carpet_4"];

    private static readonly string[] Fists = ["punch", "punch_2", "punch_3"];

    /// <summary>Every sound one material makes, in the four situations it makes any.</summary>
    private sealed record Set(string[] Step, string[] Hit, string[] Break, string[] Place);

    private static readonly Dictionary<SoundMaterial, Set> Table = new()
    {
        // Stone covers every rock and every ore. Breaking ore is really breaking the stone it is in.
        [SoundMaterial.Stone] = new(
            StoneSteps,
            ["metal_blunt_tap", "concrete_scrape"],
            ["stone_push_short", "stone_push_medium"],
            ["stone_push_short"]),

        // Soft ground has no coverage at all in the pack. Grass steps and a short crunch are the
        // nearest, and both are stand-ins.
        [SoundMaterial.Dirt] = new(GrassSteps, Fists, ["crunch_quick"], ["crunch_quick"]),
        [SoundMaterial.Grass] = new(GrassSteps, Fists, ["crunch_quick"], ["crunch_quick"]),

        // Sand is very distinctive and there is nothing like it here; gravel is the closest.
        [SoundMaterial.Sand] = new(GravelSteps, Fists, ["crunch"], ["crunch_quick"]),
        [SoundMaterial.Gravel] = new(GravelSteps, Fists, ["crunch"], ["crunch_quick"]),
        [SoundMaterial.Snow] = new(SnowSteps, Fists, ["digital_footstep_snow_1"], ["digital_footstep_snow_3"]),

        [SoundMaterial.Wood] = new(
            WoodSteps,
            ["punch_2", "punch_3"],
            ["wood_small_gather"],
            ["wood_small_drop", "wood_small_hollow"]),

        [SoundMaterial.Leaves] = new(GrassSteps, ["swipe"], ["paper_scrunch"], ["paper_move"]),
        [SoundMaterial.Plant] = new(GrassSteps, ["swipe"], ["paper_tear_1", "paper_tear_2"], ["paper_move"]),

        [SoundMaterial.Metal] = new(StoneSteps, ["metal_blunt_tap"], ["metal_clang"], ["metal_blunt_tap"]),
        [SoundMaterial.Glass] = new(StoneSteps, ["metal_blunt_tap"], ["glass_ping_big", "glass_ping_small"], ["glass_ping_small"]),
        [SoundMaterial.Cloth] = new(ClothSteps, ["slap"], ["clothing_1", "clothing_2"], ["clothing_thud"]),

        // Water is never broken or placed; it is waded through and fallen into.
        [SoundMaterial.Water] = new(["water_splashing"], ["water_drop_medium"], ["water_splashing"], ["water_splashing"]),
    };

    /// <summary>The names one material offers for one situation. Never empty.</summary>
    public static IReadOnlyList<string> For(SoundMaterial material, SoundEvent which)
    {
        var set = Table.TryGetValue(material, out var found) ? found : Table[SoundMaterial.Stone];
        return which switch
        {
            SoundEvent.Step => set.Step,
            SoundEvent.Hit => set.Hit,
            SoundEvent.Break => set.Break,
            _ => set.Place,
        };
    }

    /// <summary>Every material the table knows, for the check that walks all of them.</summary>
    public static IEnumerable<SoundMaterial> Materials => Table.Keys;

    /// <summary>Every distinct file name the table refers to.</summary>
    public static IEnumerable<string> AllNames()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in Table.Values)
        foreach (var group in (IReadOnlyList<string>[])[set.Step, set.Hit, set.Break, set.Place])
        foreach (var name in group)
            if (seen.Add(name)) yield return name;
    }
}
