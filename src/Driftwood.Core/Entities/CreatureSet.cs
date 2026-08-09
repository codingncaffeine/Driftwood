using System.Numerics;
using Driftwood.Core.Textures;

namespace Driftwood.Core.Entities;

/// <summary>How a kind crosses the world: on legs, in hops, on the wing, or through water.</summary>
public enum CreatureMove
{
    Walk,

    /// <summary>Its walk IS hops — grounded it only sits and aims; the travel happens in the air.</summary>
    Hop,

    /// <summary>Never touches down. The herd flies it instead of standing it on ground.</summary>
    Fly,

    /// <summary>Lives in water and drifts through it; out of water it can only sink.</summary>
    Swim,
}

/// <summary>What a creature is for, which is also what decides how it behaves.</summary>
public enum CreatureFamily
{
    /// <summary>Wanders, flees, and leaves something the recipe tree needs.</summary>
    Beast,

    /// <summary>Comes at you in the dark.</summary>
    Hostile,

    /// <summary>
    /// Lives underground and harms nobody.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The bat was a <see cref="Beast"/>, and so it spawned in fields with the cows.</b> That
    /// is a real fault, and it was found by asking where things live rather than by anybody seeing
    /// it: a spawner with one axis cannot tell a cave animal from a meadow animal, so it put both
    /// everywhere. Underground and harmless is a third answer rather than a shade of either.
    /// </remarks>
    Cave,
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
        new("bat", "bat", CreatureFamily.Cave, "bat",
            ["textures/entity/bat.png", "textures/entity/bat/bat.png"]),

        // ── Hostiles. Plain English words stay; coined ones become ours. ──
        new("zombie", "zombie", CreatureFamily.Hostile, "zombie",
            ["textures/entity/zombie/zombie.png"]),
        new("skeleton", "skeleton", CreatureFamily.Hostile, "skeleton",
            ["textures/entity/skeleton/skeleton.png"]),
        new("spider", "spider", CreatureFamily.Hostile, "spider",
            ["textures/entity/spider/spider.png"]),
        new("slime", "slime", CreatureFamily.Hostile, "slime",
            ["textures/entity/slime/slime.png", "textures/entity/slime.png"]),

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

    /// <summary>
    /// How each kind carries itself. Anything not named walks.
    /// </summary>
    /// <remarks>
    /// ⛳ Here rather than on <see cref="CreatureKind"/>'s rows, for the same reason the vitals are
    /// their own table: a kind that moves oddly and a kind we have colours for are different claims,
    /// and fusing them means one cannot exist without the other.
    /// </remarks>
    private static readonly Dictionary<string, CreatureMove> Moves = new(StringComparer.Ordinal)
    {
        ["slime"] = CreatureMove.Hop,
    };

    public static CreatureMove MoveFor(string kind) => Moves.GetValueOrDefault(kind, CreatureMove.Walk);

    /// <summary>
    /// How big each kind is drawn against its authored units. Anything not named is 1.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The net is the contract, so the boxes cannot shrink — the drawing scales instead.</b>
    /// A box's size is also its patch's size on the sheet: author a bat at bat size and every pack's
    /// bat lands on the wrong texels. The reference has the same problem and solves it the same way
    /// — its client scales several models at draw time (a bat to about a third, a slime by its size
    /// class) — so these numbers are that table, ours. TryMeasure reports the scaled size, which is
    /// what makes the hitbox and the drawing one thing.
    /// </remarks>
    private static readonly Dictionary<string, float> DrawScales = new(StringComparer.Ordinal)
    {
        // The reference's medium size class: an eight-unit cube drawn at two is a one-block slime.
        ["slime"] = 2f,
    };

    public static float DrawScaleFor(string kind) => DrawScales.GetValueOrDefault(kind, 1f);

    /// <summary>What a creature resolved to, or why it did not.</summary>
    /// <param name="SkinWidth">
    /// The sheet's own width, and <paramref name="SkinHeight"/> its own height. ⛔ <b>Not squared.</b>
    /// A creature's skin is a net, not a tile — a cow's is 64×32 — and putting one through the tile
    /// loader moves every patch on it, so the model wears the right texture with every face reading
    /// from the wrong place. See <c>TexturePack.TryLoadSheet</c>.
    /// </param>
    public readonly record struct Resolved(
        CreatureKind Kind, CreatureModel? Skeleton, string SkeletonFrom, string SkinFrom,
        int SkinWidth, int SkinHeight);

    /// <summary>
    /// Matches one of our creatures to a skeleton among everything that was read.
    /// </summary>
    /// <param name="sheetWidth">
    /// The skin the pack actually painted, or zero. ⛳ <b>It gets a vote, and it has to.</b> One
    /// creature is often modelled several times over and the versions are cut for different sheets;
    /// the pack's own art is the only thing on the machine that says which of them the paint was
    /// mixed for.
    /// </param>
    /// <remarks>
    /// <para>⛔ <b>An exact match is the WORST candidate, not the best, and that was the fault.</b>
    /// The bare name is the oldest model in the install — the era before skeletons, when the engine
    /// posed each animal in hardened code — so it is a flat list of bones with no parents, no bind
    /// pose and nothing in the file saying how the pieces go together. <c>geometry.cow</c> is six
    /// bones with the torso stood on end and no way to lay it down; <c>geometry.zombie</c> is a
    /// single box. The real models are <c>cow.v1.8</c> and <c>cow.v2</c> beside them. Preferring the
    /// exact name put every one of our eighteen creatures on one of those stubs.</para>
    /// <para>⚠ <b>And "most specific version" cannot mean the longest name.</b> That reading makes
    /// <c>cow.v1.8</c> beat <c>cow.v2</c>, which is a version older by two. Compared component by
    /// component as numbers, v2 beats v1.8 and v1.16 still beats v1.8.</para>
    /// <para>So the candidates are ranked, and which one was taken is reported — "it found
    /// something" and "it found the right thing" are different claims.</para>
    /// </remarks>
    public static CreatureModel? Match(
        IReadOnlyList<CreatureModel> models, string stem, int sheetWidth = 0, int sheetHeight = 0)
    {
        // The named model and its versions first; the underscored sibling is a different naming
        // altogether and is only worth having when nothing else answered at all.
        return Best(models, stem, sheetWidth, sheetHeight, sibling: false)
            ?? Best(models, stem, sheetWidth, sheetHeight, sibling: true);
    }

    private static CreatureModel? Best(
        IReadOnlyList<CreatureModel> models, string stem, int sheetWidth, int sheetHeight, bool sibling)
    {
        CreatureModel? best = null;
        var bestRank = (Assembled: -1, Fits: -1, Version: -1L);

        foreach (var model in models)
        {
            var version = 0L;

            if (sibling)
            {
                if (!model.Name.StartsWith(stem + "_", StringComparison.Ordinal)) continue;
            }
            else if (model.Name != stem)
            {
                if (!model.Name.StartsWith(stem + ".", StringComparison.Ordinal)) continue;

                // ⛔ ONLY when what follows is a VERSION. The names are namespaced, so asking for
                // "zombie" and taking anything beginning "zombie." also offers zombie.drowned,
                // zombie.husk and zombie.villager — and the first run took one of those and reported
                // a zombie with no bones. `zombie.v1.8` is the zombie; `zombie.drowned.v1.16` is a
                // different creature that happens to be filed under it.
                var suffix = model.Name[(stem.Length + 1)..];
                if (!IsVersion(suffix)) continue;

                version = VersionOf(suffix);
            }

            // ⛔ FIRST, and above what the sheet says: does this file pose the creature itself? Two
            // of the three eras in an install do not. The oldest predates skeletons and was
            // assembled by the engine in code; the newest moved the rest pose out into an animation
            // file. Both parse, both have the right bones and nets, and both come out as a heap.
            // A skeleton nothing here can stand up is no use however well its net matches.
            var assembled = CreatureMesh.Build(model).Assembled() ? 1 : 0;

            // Then: does the pack's own sheet have the shape this net is cut for? Nothing else on
            // the machine can tell a cow modelled against a 64x32 sheet from one modelled against a
            // 64x64 one, and both are sitting in the same install.
            var fits = sheetWidth > 0 && sheetHeight > 0
                    && model.SheetWidth * sheetHeight == model.SheetHeight * sheetWidth
                ? 1 : 0;

            var rank = (assembled, fits, version);
            if (best is not null && rank.CompareTo(bestRank) <= 0) continue;

            best = model;
            bestRank = rank;
        }

        return best;
    }

    /// <summary>"v1.16" as a number that sorts after "v1.8" and before "v2".</summary>
    /// <remarks>
    /// ⚠ <b>Each component lands in a slot of its own rather than being accumulated.</b> Multiplying
    /// up as it goes makes a version with more components larger than one with fewer, so v1.8 comes
    /// out above v2 — which is the arithmetic version of the bug this replaced.
    /// </remarks>
    private static long VersionOf(string suffix)
    {
        var value = 0L;
        var slot = 1_000_000_000_000L;

        foreach (var part in suffix[1..].Split('.'))
        {
            if (slot == 0L) break;

            value += (int.TryParse(part, out var number) ? Math.Clamp(number, 0, 9999) : 0) * slot;
            slot /= 10_000L;
        }

        return value;
    }

    /// <summary>True for "v1.8", "v1.12" and the like — a version suffix and nothing else.</summary>
    private static bool IsVersion(string suffix)
    {
        if (suffix.Length < 2 || suffix[0] != 'v') return false;

        foreach (var c in suffix[1..]) if (!char.IsDigit(c) && c != '.') return false;
        return true;
    }

    /// <summary>
    /// Checks the right skeleton is taken when one creature has been modelled several times.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>The pair is the whole check.</b> Two sheets of different shapes have to pick two
    /// different skeletons off the same shelf — and no fixed preference, however it is written, can
    /// do that. A rule that always takes the newest passes one of them; one that always takes the
    /// bare name passes neither; the old one, which took the exact name, passed neither and had been
    /// putting every creature in the game on a stub that cannot be posed.</para>
    /// <para>The rest of it is the ordering: a jointed model beats a flat one, and v2 beats v1.8,
    /// which the length of a name says the other way round.</para>
    /// </remarks>
    public static List<string> Validate()
    {
        var faults = new List<string>();

        CreatureBone Bone(string name, string parent) =>
            new(name, parent, Vector3.Zero, Vector3.Zero, Vector3.Zero,
                [new CreatureCube(Vector3.Zero, new Vector3(4f, 4f, 4f), 0, 0, false, 0f)]);

        // A part authored a long way from every other one, which is what a file that leaves the
        // posing to somebody else looks like from here.
        CreatureBone Adrift(string name) =>
            new(name, "", Vector3.Zero, Vector3.Zero, Vector3.Zero,
                [new CreatureCube(new Vector3(0f, 40f, 0f), new Vector3(4f, 4f, 4f), 0, 0, false, 0f)]);

        // One creature, four skeletons, exactly as an install carries them: the pre-skeleton stub
        // under the bare name, the one that poses itself, a later rebuild cut for a taller sheet,
        // and a newer one still whose rest pose has moved out into an animation file.
        var shelf = new List<CreatureModel>
        {
            new("beast", 64, 32, [Bone("body", ""), Bone("head", "")]),
            new("beast.v1.8", 64, 32, [Bone("body", ""), Bone("head", "body")]),
            new("beast.v2", 64, 64, [Bone("body", ""), Bone("head", "body")]),
            new("beast.v3", 64, 64, [Bone("body", ""), Adrift("head")]),

            // ⛔ Filed under the same first word and a different creature entirely. It has the most
            // bones and the highest version on the shelf, so anything that matches on the stem alone
            // takes it.
            new("beast.pale.v9", 64, 32, [Bone("body", ""), Bone("head", "body"), Bone("tail", "body")]),
        };

        var wide = Match(shelf, "beast", 256, 128);
        if (wide?.Name != "beast.v1.8")
            faults.Add($"a 2:1 sheet chose '{wide?.Name ?? "nothing"}' rather than the 2:1 skeleton beast.v1.8");

        // ⛔ The cow's own case. beast.v3 is newer AND cut for exactly this sheet, and it still must
        // lose, because a skeleton whose parts are flung apart is one nothing here can stand up.
        var square = Match(shelf, "beast", 256, 256);
        if (square?.Name != "beast.v2")
            faults.Add($"a 1:1 sheet chose '{square?.Name ?? "nothing"}' rather than the 1:1 skeleton beast.v2");

        // With nothing painted, the sheet has no vote and the newest jointed model wins. ⚠ This is
        // the one that says v2 beats v1.8: by the length of a name it does not.
        var bare = Match(shelf, "beast");
        if (bare?.Name != "beast.v2")
            faults.Add($"with no sheet to go on, '{bare?.Name ?? "nothing"}' was chosen rather than the newest, beast.v2");

        // And the stub never wins, whatever the sheet says — it is the only candidate cut for 64x32
        // once v1.8 is taken away, and it still must not be preferred over a jointed one.
        foreach (var (label, w, h) in (ReadOnlySpan<(string, int, int)>)
                 [("no sheet", 0, 0), ("2:1", 256, 128), ("1:1", 256, 256), ("an odd one", 100, 30)])
        {
            var picked = Match(shelf, "beast", w, h);
            if (picked is null) { faults.Add($"{label}: nothing matched at all"); continue; }

            if (picked.Name == "beast")
                faults.Add($"{label}: the pre-skeleton stub was chosen over a model that poses itself");

            if (picked.Name == "beast.v3")
                faults.Add($"{label}: a skeleton whose parts do not touch was chosen for being newer");

            if (picked.Name.StartsWith("beast.pale", StringComparison.Ordinal))
                faults.Add($"{label}: '{picked.Name}' is a different creature filed under the same first word");
        }

        return faults;
    }
}
