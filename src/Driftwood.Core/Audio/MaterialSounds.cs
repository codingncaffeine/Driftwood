namespace Driftwood.Core.Audio;

/// <summary>What a block sounds like when it is walked on, struck, broken or put down.</summary>
/// <remarks>
/// Coarser than the block list on purpose. Seven hundred blocks share about a dozen surfaces, and a
/// table keyed on the surface is one a person can read and check; keyed on the block it would be
/// seven hundred rows saying "stone" and one of them would be wrong.
/// </remarks>
public enum SoundMaterial
{
    Stone,

    /// <summary>The deep world's rock. 318 of the world's 384 cells are under the sea and most of
    /// what is down there is deepstone; a place that big deserves a voice of its own.</summary>
    Deepstone,

    Dirt,
    Grass,
    Sand,
    Gravel,
    Snow,
    Wood,
    Leaves,
    Plant,

    /// <summary>The berry bush, whose coming apart is its own recording in the pack.</summary>
    BerryBush,

    /// <summary>The cobweb, which is stepped through rather than on, and tears rather than breaks.</summary>
    Cobweb,

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
/// <para>Every entry is a slot a selected sound pack may fill. Driftwood's embedded recordings are
/// a deliberately sparse offline fallback; the archive audit proves these names are structurally
/// valid, and <c>--audio-check pack.zip</c> can require and decode a pack's complete table.</para>
/// <para>Names are paths from the sounds folder because the pack repeats bare names on purpose:
/// <c>dig/stone1</c> and <c>step/stone1</c> are different recordings of the same rock. The layout
/// is the pack author's and is kept as shipped — these tables are the translation, exactly as
/// <c>BlockTextureSet.Layers</c> is for art.</para>
/// <para>The old stand-in problem is over: the pack has purpose-made sets for every surface in
/// this enum, including the dirt, sand and stone strikes the plan's shopping list ranked first.
/// Breaking and placing share a recording set where the pack ships one (its <c>dig/</c> folder
/// serves both, which is the genre's own convention).</para>
/// </remarks>
public static class MaterialSounds
{
    /// <summary>Numbered variants of one recording: <c>stem1, stem2, …</c>.</summary>
    private static string[] Run(string stem, int count)
    {
        var names = new string[count];
        for (var i = 0; i < count; i++) names[i] = $"{stem}{i + 1}";
        return names;
    }

    /// <summary>Every sound one material makes, in the four situations it makes any.</summary>
    private sealed record Set(string[] Step, string[] Hit, string[] Break, string[] Place);

    private static readonly Dictionary<SoundMaterial, Set> Table = new()
    {
        // Stone covers every rock and every ore above the deep. Breaking ore is really breaking
        // the stone it is in.
        [SoundMaterial.Stone] = new(
            Run("step/stone", 6),
            Run("block/stone/hit", 8),
            Run("dig/stone", 4),
            Run("dig/stone", 4)),

        [SoundMaterial.Deepstone] = new(
            Run("block/deepslate/step", 6),
            Run("block/deepslate/hit", 4),
            Run("block/deepslate/break", 4),
            Run("block/deepslate/place", 6)),

        [SoundMaterial.Dirt] = new(
            Run("block/rooted_dirt/step", 6),
            Run("block/rooted_dirt/hit", 4),
            Run("block/rooted_dirt/break", 4),
            Run("block/rooted_dirt/break", 4)),

        [SoundMaterial.Grass] = new(
            Run("step/grass", 6),
            Run("block/grass/hit", 4),
            Run("block/grass/break", 4),
            Run("dig/grass", 4)),

        [SoundMaterial.Sand] = new(
            Run("step/sand", 5),
            Run("block/sand/hit", 4),
            Run("block/sand/break", 4),
            Run("dig/sand", 4)),

        [SoundMaterial.Gravel] = new(
            Run("step/gravel", 4),
            Run("step/gravel", 4),
            Run("block/gravel/break", 4),
            Run("dig/gravel", 4)),

        [SoundMaterial.Snow] = new(
            Run("step/snow", 4),
            Run("block/snow/hit", 4),
            Run("block/snow/break", 4),
            Run("dig/snow", 4)),

        [SoundMaterial.Wood] = new(
            Run("step/wood", 6),
            Run("block/wood/hit", 4),
            Run("dig/wood", 4),
            Run("dig/wood", 4)),

        // Foliage rustles rather than crunches; the wet-grass set is the pack's leafier cousin of
        // plain grass and keeps a hedge from sounding like a lawn.
        [SoundMaterial.Leaves] = new(
            Run("step/grass", 6),
            Run("dig/wet_grass", 4),
            Run("dig/wet_grass", 4),
            Run("dig/wet_grass", 4)),

        [SoundMaterial.Plant] = new(
            Run("step/grass", 6),
            Run("dig/wet_grass", 4),
            Run("dig/grass", 4),
            Run("item/plant/crop", 6)),

        // A bush rustles like any plant until it comes apart, which the pack recorded on its own.
        // Placing one reuses the same recording — the pack ships no separate planting for it, and
        // a rustle-and-snap is what pushing a seedling into ground sounds like anyway.
        [SoundMaterial.BerryBush] = new(
            Run("step/grass", 6),
            Run("dig/wet_grass", 4),
            Run("block/sweet_berry_bush/break", 4),
            Run("block/sweet_berry_bush/break", 4)),

        // The web has the pack's own full set: stepped THROUGH, and torn rather than broken.
        // Nothing places one, so Place reuses the step — a table row cannot be empty.
        [SoundMaterial.Cobweb] = new(
            Run("block/cobweb/step", 6),
            Run("block/cobweb/break", 8),
            Run("block/cobweb/break", 8),
            Run("block/cobweb/step", 6)),

        [SoundMaterial.Metal] = new(
            Run("block/iron/step", 6),
            Run("block/iron/place", 4),
            Run("block/iron/break", 8),
            Run("block/iron/place", 4)),

        [SoundMaterial.Glass] = new(
            Run("step/glass", 4),
            Run("block/glass/hit", 4),
            Run("dig/glass", 4),
            Run("step/glass", 4)),

        [SoundMaterial.Cloth] = new(
            Run("step/cloth", 4),
            Run("dig/cloth", 4),
            Run("dig/cloth", 4),
            Run("dig/cloth", 4)),

        // Water is never broken or placed; it is waded through and fallen into.
        [SoundMaterial.Water] = new(
            Run("liquid/swim", 6),
            ["liquid/splash", "liquid/splash2"],
            ["liquid/splash", "liquid/splash2"],
            ["liquid/splash", "liquid/splash2"]),
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
