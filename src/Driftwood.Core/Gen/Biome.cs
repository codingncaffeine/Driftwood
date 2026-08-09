namespace Driftwood.Core.Gen;

/// <summary>
/// The named regions of the surface — what a column of the world <em>is</em>, by name.
/// </summary>
/// <remarks>
/// <para>⛳ <b>A biome is a classification, not a cause.</b> Every name here is derived from the
/// same shipped fields and lines the generator already places material by — temperature, downfall,
/// the forest-density field, the grove field, the snow and ice lines, altitude. Nothing generates
/// <em>from</em> a biome, so naming the regions changed not one cell of any world that already
/// exists, and the classifier can never disagree with the terrain unless somebody edits one of
/// them without the other — which is exactly what the audit's agreement check is for.</para>
/// <para>The value of the name is everything that wants to ask "where am I": the per-biome census
/// that P4 ships, and later the map screen, region-keyed music, and spawn tables. They all want
/// one answer, decided in one place.</para>
/// </remarks>
public enum Biome : byte
{
    /// <summary>Open water deeper than the shore band.</summary>
    Sea,

    /// <summary>Sea whose surface the cold has lidded with ice.</summary>
    FrozenSea,

    /// <summary>The beach band: land low enough that the generator surfaces it in sand.</summary>
    Shore,

    /// <summary>Hot, dry shore — the arid fringe where the desert kit grows.</summary>
    Dunes,

    /// <summary>Soaked shore — the wet fringe the reeds stand on.</summary>
    Marsh,

    /// <summary>Ground past the snow line, surfaced in snow.</summary>
    Snowfield,

    /// <summary>The dusting fringe: too warm for a snowfield, cold enough for a layer over the grass.</summary>
    Tundra,

    /// <summary>Mild ground under the grove field, where the stands grow cherry.</summary>
    CherryGrove,

    /// <summary>Ground where the forest-density field packs the trees into a wood.</summary>
    Woods,

    /// <summary>Hot, dry open ground — the arid fringe inland of the sand.</summary>
    Drylands,

    /// <summary>High ground, still short of the snow.</summary>
    Highlands,

    /// <summary>Open grass in the forest field's low tail — clearings, flowers, the odd tree.</summary>
    Meadow,

    /// <summary>Everything else: the scattered-tree ground most of the world is made of.</summary>
    /// <remarks>
    /// The default on purpose. The forest field's middle is broad and well-treed — the woods and
    /// the meadows are its tails — so the everyday name belongs to the everyday ground, and both
    /// of the marked names stay claims a check can hold: a wood must out-tree this, a meadow must
    /// be more open than it.
    /// </remarks>
    Woodland,
}

/// <summary>The biome table's own constants, one place.</summary>
public static class Biomes
{
    public const int Count = 13;

    /// <summary>The name a report prints — block-name casing, ours.</summary>
    public static string NameOf(Biome biome) => biome switch
    {
        Biome.Sea => "sea",
        Biome.FrozenSea => "frozen_sea",
        Biome.Shore => "shore",
        Biome.Dunes => "dunes",
        Biome.Marsh => "marsh",
        Biome.Snowfield => "snowfield",
        Biome.Tundra => "tundra",
        Biome.CherryGrove => "cherry_grove",
        Biome.Woods => "woods",
        Biome.Drylands => "drylands",
        Biome.Highlands => "highlands",
        Biome.Meadow => "meadow",
        _ => "woodland",
    };
}
