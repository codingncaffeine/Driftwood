using Driftwood.Core.Textures;

namespace Driftwood.Core.Entities;

/// <summary>What a creature is for, which is also what decides how it behaves.</summary>
public enum CreatureFamily
{
    /// <summary>Wanders, flees, and leaves something the recipe tree needs.</summary>
    Beast,

    /// <summary>Comes at you in the dark.</summary>
    Hostile,
}

/// <summary>
/// One creature: our name for it, and where its skeleton and its skin are found.
/// </summary>
/// <param name="Skeleton">
/// The stem of the geometry to wear. Matched loosely — the files carry version suffixes
/// (<c>cow.v1.8</c>) and legacy siblings (<c>cow_v1.0</c>) that nobody would guess at.
/// </param>
/// <param name="Skins">Texture paths to try, in order. Java and Bedrock disagree on several.</param>
public readonly record struct CreatureKind(
    string Name, string Label, CreatureFamily Family, string Skeleton, string[] Skins);

/// <summary>
/// Driftwood's creatures, and the correspondence that lets somebody else's art wear them.
/// </summary>
/// <remarks>
/// <para>⛔ <b>The whole reason this table exists, in the user's words:</b> <i>"we can't make these
/// texture packs work if our enemies and animals don't match, its that simple"</i> — and then
/// <i>"just use different but similar names for some stuff, as long as the texture packs work
/// properly that's what matters"</i>. So the left column is ours and the right column is theirs, and
/// it is the same shape as <see cref="BlockTextureSet.Layers"/> for exactly the same reason: our
/// vocabulary is deliberately not theirs, so nothing can be matched automatically and the
/// correspondence has to be written down once, in one place.</para>
/// <para><b>Naming follows the rule the blocks already use.</b> A real animal keeps its real name —
/// nobody owns "cow", "wolf" or "spider", and renaming them costs legibility for nothing. Where the
/// reference's name is <em>coined</em>, ours is too, in the same plain-compound coastal register as
/// driftoak and stormglass: a creeper is a <b>crawler</b>, an enderman a <b>farwalker</b>. The
/// handful in between are judgement and are marked.</para>
/// <para>⚠ <b>The SKELETON is the compatibility contract, not the name.</b> A pack paints a sheet
/// against a specific net; wear a different one and the art lands on the wrong faces. That is why
/// the skeleton is read rather than invented — see <see cref="BedrockGeometry"/>.</para>
/// </remarks>
public static class CreatureSet
{
    /// <summary>
    /// The first set: the five drops that unblock the most recipes, and the core threats.
    /// </summary>
    /// <remarks>
    /// Deliberately eighteen rather than ninety. Leather, wool, feather, bone and string are the
    /// components the whole recipe tree is waiting on, and they come from five of these; the rest is
    /// enough of a bestiary that night means something. Adding a row is a row.
    /// </remarks>
    public static readonly CreatureKind[] All =
    [
        // ── Beasts. Every name here is a real English word and stays one. ──
        new("cow", "cow", CreatureFamily.Beast, "cow",
            ["textures/entity/cow/cow.png"]),
        new("pig", "pig", CreatureFamily.Beast, "pig",
            ["textures/entity/pig/pig.png"]),
        new("sheep", "sheep", CreatureFamily.Beast, "sheep",
            ["textures/entity/sheep/sheep.png", "textures/entity/sheep/sheep_fur.png"]),
        new("chicken", "chicken", CreatureFamily.Beast, "chicken",
            ["textures/entity/chicken.png", "textures/entity/chicken/chicken.png"]),
        new("rabbit", "rabbit", CreatureFamily.Beast, "rabbit",
            ["textures/entity/rabbit/brown.png", "textures/entity/rabbit/rabbit_brown.png"]),
        new("wolf", "wolf", CreatureFamily.Beast, "wolf",
            ["textures/entity/wolf/wolf.png"]),
        new("fox", "fox", CreatureFamily.Beast, "fox",
            ["textures/entity/fox/fox.png"]),
        // ⚠ More than one candidate because packs disagree about which coat is the default one.
        // Intermacgod ships a tabby and no red; Vintage ships a red and no tabby.
        new("cat", "cat", CreatureFamily.Beast, "cat",
            ["textures/entity/cat/tabby.png", "textures/entity/cat/cat_tabby.png",
             "textures/entity/cat/red.png", "textures/entity/cat/black.png"]),
        new("squid", "squid", CreatureFamily.Beast, "squid",
            ["textures/entity/squid.png", "textures/entity/squid/squid.png"]),
        new("bat", "bat", CreatureFamily.Beast, "bat",
            ["textures/entity/bat.png", "textures/entity/bat/bat.png"]),

        // ── Hostiles. Plain English words stay; coined ones become ours. ──
        new("zombie", "zombie", CreatureFamily.Hostile, "zombie",
            ["textures/entity/zombie/zombie.png"]),
        new("skeleton", "skeleton", CreatureFamily.Hostile, "skeleton",
            ["textures/entity/skeleton/skeleton.png"]),
        new("spider", "spider", CreatureFamily.Hostile, "spider",
            ["textures/entity/spider/spider.png"]),
        new("slime", "slime", CreatureFamily.Hostile, "slime",
            ["textures/entity/slime/slime.png"]),

        // ⛳ Ours. 'Creeper' and 'enderman' are coined, so these are too — same register as
        // driftoak and stormglass. The skeleton and the sheet stay theirs, which is the point.
        new("crawler", "crawler", CreatureFamily.Hostile, "creeper",
            ["textures/entity/creeper/creeper.png"]),
        new("farwalker", "farwalker", CreatureFamily.Hostile, "enderman",
            ["textures/entity/enderman/enderman.png"]),

        // ⚠ Judgement: 'drowned' and 'husk' are ordinary English words used as names. Kept, on the
        // same argument that keeps 'copper' and 'sandstone'.
        // ⛔ Their SKELETONS are filed under the zombie's namespace — `geometry.zombie.drowned` —
        // which is why the stem is not simply the creature's own name. Found by looking, after both
        // came back with no skeleton at all.
        new("drowned", "drowned", CreatureFamily.Hostile, "zombie.drowned",
            ["textures/entity/zombie/drowned.png"]),
        new("husk", "husk", CreatureFamily.Hostile, "zombie.husk",
            ["textures/entity/zombie/husk.png"]),
    ];

    /// <summary>What a creature resolved to, or why it did not.</summary>
    public readonly record struct Resolved(
        CreatureKind Kind, CreatureModel? Skeleton, string SkeletonFrom, string SkinFrom, int SkinSize);

    /// <summary>
    /// Matches one of our creatures to a skeleton among everything that was read.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Loosely, and it has to be.</b> The file that draws a cow declares itself
    /// <c>geometry.cow.v1.8</c>, and beside it sits <c>cow_v1.0</c> from an older overlay. An exact
    /// match finds neither. Exact first, then the version suffix, then the legacy sibling — and
    /// which one was taken is reported, because "it found something" and "it found the right thing"
    /// are different claims.
    /// </remarks>
    public static CreatureModel? Match(IReadOnlyList<CreatureModel> models, string stem)
    {
        foreach (var model in models) if (model.Name == stem) return model;

        CreatureModel? versioned = null;
        CreatureModel? legacy = null;

        foreach (var model in models)
        {
            if (model.Name.StartsWith(stem + ".", StringComparison.Ordinal))
            {
                // ⛔ ONLY when what follows is a VERSION. The names are namespaced, so asking for
                // "zombie" and taking anything beginning "zombie." also offers zombie.drowned,
                // zombie.husk and zombie.villager — and the first run took one of those and reported
                // a zombie with no bones. `zombie.v1.8` is the zombie; `zombie.drowned.v1.16` is a
                // different creature that happens to be filed under it.
                if (!IsVersion(model.Name[(stem.Length + 1)..])) continue;

                // The most specific version wins, which is the longest — v1.12 beats v1.8, and the
                // report says which was taken because "found something" is not "found the right one".
                if (versioned is null || model.Name.Length > versioned.Name.Length) versioned = model;
            }
            else if (model.Name.StartsWith(stem + "_", StringComparison.Ordinal))
            {
                legacy ??= model;
            }
        }

        return versioned ?? legacy;
    }

    /// <summary>True for "v1.8", "v1.12" and the like — a version suffix and nothing else.</summary>
    private static bool IsVersion(string suffix)
    {
        if (suffix.Length < 2 || suffix[0] != 'v') return false;

        foreach (var c in suffix[1..]) if (!char.IsDigit(c) && c != '.') return false;
        return true;
    }
}
