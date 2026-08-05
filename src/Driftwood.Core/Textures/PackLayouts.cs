namespace Driftwood.Core.Textures;

/// <summary>
/// Which layout a pack was written for. There are more than two, and they are not the same format
/// with a different extension.
/// </summary>
public enum PackDialect
{
    /// <summary>
    /// The current one: <c>pack.mcmeta</c>, and <c>assets/&lt;namespace&gt;/textures/block/</c> —
    /// singular, with the names the 2018 flattening left behind.
    /// </summary>
    Java,

    /// <summary>
    /// The same shape before that rename: <c>textures/blocks/</c> and <c>textures/items/</c> plural,
    /// holding <c>log_oak</c> and <c>planks_oak</c>. Five years of packs are in this layout.
    /// </summary>
    JavaLegacy,

    /// <summary>
    /// <c>manifest.json</c> and <c>textures/</c> at the root, no namespace, and the old names.
    /// What a <c>.mcpack</c> holds.
    /// </summary>
    Bedrock,

    /// <summary>Nothing recognisable. Read as best it can be and reported as such.</summary>
    Unknown,
}

/// <summary>
/// Where each layout keeps the texture that a modern Java pack keeps at a given path.
/// </summary>
/// <remarks>
/// <para><b>Candidates rather than a translation.</b> Every caller in the project names one path —
/// the modern Java one — and this turns that into an ordered list of places it might actually be.
/// The first that exists wins. That is what makes a fourth layout a few lines rather than another
/// branch through everything: nothing upstream knows how many there are.</para>
/// <para>It also means detection does not have to be right, only useful. A pack that is half one
/// thing and half another — and they exist, because people merge packs — resolves per texture
/// rather than per pack, and the report says which file each layer actually came off.</para>
/// <para><b>The rename table is shared between the two old layouts on purpose.</b> Bedrock and
/// pre-flattening Java are the same vocabulary: the names diverged when Java renamed everything in
/// 2018 and Bedrock did not follow. Where the two old layouts genuinely differ — the grass side
/// overlay is the one in our set — both are listed and both are tried.</para>
/// <para>⚠ Every Bedrock name here was read out of a real pack. The pre-flattening Java names are
/// the same table, which is the claim this shares its evidence with: there was no 1.12 pack to hand
/// to check them against, and a name that is wrong costs nothing but our own art staying put.</para>
/// </remarks>
public static class PackLayouts
{
    /// <summary>What the old layouts call a file the modern one has renamed.</summary>
    private static readonly Dictionary<string, string[]> Renames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Wood never went through the flattening: the species is still a suffix.
        ["oak_log"] = ["log_oak"],
        ["oak_log_top"] = ["log_oak_top"],
        ["oak_planks"] = ["planks_oak"],
        ["oak_leaves"] = ["leaves_oak"],

        // Grass, and the one place the candidate list earns its keep. Measured on a real Bedrock
        // pack: grass_side_carried is the OPAQUE face (mean alpha 255), grass_side there is mostly
        // transparent (62), and grass_side_overlay is the near-colourless cut-out the tint runs
        // through (mean rgb 18,18,18 at alpha 62) — which is the same file, under the same name,
        // as pre-flattening Java's. So the opaque one is asked for first and an old Java pack, which
        // has no "carried" anything, falls straight through to its own grass_side.
        ["grass_block_top"] = ["grass_top"],
        ["grass_block_side"] = ["grass_side_carried", "grass_side"],
        ["grass_block_side_overlay"] = ["grass_side_overlay"],

        // The stone family kept its old prefix.
        ["granite"] = ["stone_granite"],
        ["andesite"] = ["stone_andesite"],
        ["diorite"] = ["stone_diorite"],

        ["bricks"] = ["brick"],
        ["torch"] = ["torch_on"],
        ["furnace_front"] = ["furnace_front_off"],

        // Ground cover and flowers.
        ["short_grass"] = ["tallgrass"],
        ["grass"] = ["tallgrass"],
        ["cornflower"] = ["flower_cornflower"],
        ["oxeye_daisy"] = ["flower_oxeye_daisy"],

        // ⚠ Items could NOT be checked: the pack this was built against ships none at all. They
        // follow the same pre-flattening pattern and cost nothing when wrong — a name that is not
        // there keeps our own icon, exactly as a missing texture always has.
        ["wooden_pickaxe"] = ["wood_pickaxe"],
        ["wooden_axe"] = ["wood_axe"],
        ["wooden_shovel"] = ["wood_shovel"],
        ["wooden_sword"] = ["wood_sword"],
        ["golden_pickaxe"] = ["gold_pickaxe"],
        ["golden_axe"] = ["gold_axe"],
        ["golden_shovel"] = ["gold_shovel"],
        ["golden_sword"] = ["gold_sword"],
        ["lapis_lazuli"] = ["dye_powder_blue"],
    };

    /// <summary>
    /// Where the old layouts keep what modern Java keeps at <paramref name="javaPath"/>.
    /// </summary>
    /// <remarks>
    /// Relative to the pack's own root — which is under <c>assets/&lt;namespace&gt;/</c> for
    /// pre-flattening Java and straight off the root for Bedrock, a difference the caller adds
    /// rather than this. Folders go plural; the file goes through the rename table if it is in it.
    /// </remarks>
    public static IEnumerable<string> Legacy(string javaPath)
    {
        var slash = javaPath.LastIndexOf('/');
        if (slash < 0) yield break;

        var folder = javaPath[..slash] switch
        {
            "textures/block" => "textures/blocks",
            "textures/item" => "textures/items",
            var same => same,
        };

        var file = javaPath[(slash + 1)..];
        var dot = file.LastIndexOf('.');
        var stem = dot < 0 ? file : file[..dot];
        var extension = dot < 0 ? "" : file[dot..];

        if (Renames.TryGetValue(stem, out var others))
        {
            foreach (var other in others) yield return $"{folder}/{other}{extension}";
            yield break;
        }

        // Same name, different folder. Most of the set is here — stone, sand, gravel, clay, snow,
        // glass, bedrock and every ore are called the same thing in every layout there has been.
        yield return $"{folder}/{stem}{extension}";
    }

    /// <summary>Every name a texture might be filed under, for matching a pack's own file list.</summary>
    /// <remarks>
    /// The coverage report asks "did we consume this file", and on an old pack the answer depends on
    /// a name we never wrote down anywhere but the table above.
    /// </remarks>
    public static IEnumerable<string> AllStems(string javaPath)
    {
        var slash = javaPath.LastIndexOf('/');
        var file = slash < 0 ? javaPath : javaPath[(slash + 1)..];
        var dot = file.LastIndexOf('.');

        yield return dot < 0 ? file : file[..dot];

        foreach (var legacy in Legacy(javaPath))
        {
            var at = legacy.LastIndexOf('/');
            var name = at < 0 ? legacy : legacy[(at + 1)..];
            var stop = name.LastIndexOf('.');
            yield return stop < 0 ? name : name[..stop];
        }
    }

    /// <summary>
    /// True for the companion maps a physically-based pack ships beside each colour texture.
    /// </summary>
    /// <remarks>
    /// <para>Half of a modern Bedrock pack is not colour: the one this was built against carries 516
    /// <c>_mer</c> maps, 320 <c>_normal</c> and 24 <c>_height</c> beside 1,336 actual textures. None
    /// of them is a picture of anything — a normal map read as colour is a lilac block, and counted
    /// as art it doubles every number in a coverage report.</para>
    /// <para>⚠ <b>Not usable as a filter on reading.</b> Bedrock's plain sandstone side is genuinely
    /// called <c>sandstone_normal</c>, against <c>sandstone_carved</c> and <c>sandstone_smooth</c>.
    /// Every lookup is by exact name so a companion is never reached by accident; this is for
    /// counting, where a suffix rule is all there is to go on.</para>
    /// </remarks>
    public static bool IsCompanionMap(string path)
    {
        var dot = path.LastIndexOf('.');
        var stem = dot < 0 ? path : path[..dot];

        // The exception the rule would otherwise eat.
        if (stem.EndsWith("sandstone_normal", StringComparison.OrdinalIgnoreCase)) return false;

        foreach (var suffix in Companions)
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    // Whole words only. "_s" and "_n" are used by some shader packs for the same thing and are not
    // in this list on purpose: they would swallow every real name ending in those letters.
    private static readonly string[] Companions =
        ["_mer", "_mers", "_normal", "_norm", "_height", "_heightmap", "_bump", "_spec"];
}
