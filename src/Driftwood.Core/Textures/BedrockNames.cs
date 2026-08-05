namespace Driftwood.Core.Textures;

/// <summary>
/// Which layout a pack was written for. The two are not the same format with a different extension.
/// </summary>
public enum PackDialect
{
    /// <summary>
    /// <c>pack.mcmeta</c> at the root and <c>assets/&lt;namespace&gt;/textures/block/…</c> under it.
    /// </summary>
    Java,

    /// <summary>
    /// <c>manifest.json</c> at the root and <c>textures/blocks/…</c> under it — no namespace at all.
    /// </summary>
    Bedrock,
}

/// <summary>
/// Translates a Java asset path into where a Bedrock pack keeps the same texture.
/// </summary>
/// <remarks>
/// <para><b>A <c>.mcpack</c> is a zip, and that is the least of it.</b> Accepting the extension gets
/// an archive open and then finds nothing, because a Bedrock pack is a different layout with
/// different file names: <c>manifest.json</c> rather than <c>pack.mcmeta</c>, <c>textures/</c> at
/// the root rather than under <c>assets/&lt;namespace&gt;/</c>, <c>blocks</c> and <c>items</c>
/// plural rather than singular, and — the part that actually costs work — names that never went
/// through the 2018 rename. It is still <c>log_oak</c> and <c>planks_oak</c> over there.</para>
/// <para>Every block entry below was <b>read out of a real Bedrock pack</b> rather than remembered.
/// Where an entry is missing the name is the same on both sides, which is true of most of them —
/// <c>stone</c>, <c>sand</c>, <c>gravel</c>, <c>clay</c>, <c>snow</c>, <c>glass</c>,
/// <c>bedrock</c> and every ore are unchanged, so a table of only the differences is short.</para>
/// <para>A pack is a sparse override set either way. A name with no Bedrock equivalent at all —
/// <c>deepslate</c> is one, in the pack this was checked against — simply keeps Driftwood's own art,
/// which is what happens for any texture a pack does not carry.</para>
/// </remarks>
public static class BedrockNames
{
    /// <summary>Java basename to Bedrock basename, where the two differ.</summary>
    private static readonly Dictionary<string, string> Renames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Wood never went through the flattening over there: the species is still a suffix.
        ["oak_log"] = "log_oak",
        ["oak_log_top"] = "log_oak_top",
        ["oak_planks"] = "planks_oak",
        ["oak_leaves"] = "leaves_oak",

        // Grass. The top and the side are the same idea under different names; the tinted cut-out
        // Java keeps as a separate overlay is Bedrock's "carried" side, which is a whole tinted face
        // rather than a fringe — laid over the base in the same pass it comes out as one.
        ["grass_block_top"] = "grass_top",
        ["grass_block_side"] = "grass_side",
        ["grass_block_side_overlay"] = "grass_side_carried",

        // The stone family kept its old prefix.
        ["granite"] = "stone_granite",
        ["andesite"] = "stone_andesite",
        ["diorite"] = "stone_diorite",

        ["bricks"] = "brick",
        ["torch"] = "torch_on",
        ["furnace_front"] = "furnace_front_off",

        // Ground cover and flowers.
        ["short_grass"] = "tallgrass",
        ["grass"] = "tallgrass",
        ["cornflower"] = "flower_cornflower",
        ["oxeye_daisy"] = "flower_oxeye_daisy",

        // ⚠ The item names below could NOT be checked: the pack this was built against ships no
        // items at all, only blocks. They follow the same pre-flattening pattern the blocks do and
        // cost nothing when wrong — a name that is not there keeps our own icon, exactly as a
        // missing texture always has. Verify them against a pack that carries items.
        ["wooden_pickaxe"] = "wood_pickaxe",
        ["wooden_axe"] = "wood_axe",
        ["wooden_shovel"] = "wood_shovel",
        ["wooden_sword"] = "wood_sword",
        ["golden_pickaxe"] = "gold_pickaxe",
        ["golden_axe"] = "gold_axe",
        ["golden_shovel"] = "gold_shovel",
        ["golden_sword"] = "gold_sword",
        ["lapis_lazuli"] = "dye_powder_blue",
    };

    /// <summary>
    /// Where a Bedrock pack keeps what a Java pack keeps at <paramref name="javaPath"/>.
    /// </summary>
    /// <remarks>
    /// Null when the path names something Bedrock has no folder for at all, so the caller can leave
    /// our own art in place rather than probing for a file that cannot exist.
    /// </remarks>
    public static string? Translate(string javaPath)
    {
        var slash = javaPath.LastIndexOf('/');
        if (slash < 0) return null;

        var folder = javaPath[..slash];
        var file = javaPath[(slash + 1)..];

        var dot = file.LastIndexOf('.');
        var stem = dot < 0 ? file : file[..dot];
        var extension = dot < 0 ? "" : file[dot..];

        if (Renames.TryGetValue(stem, out var renamed)) stem = renamed;

        // Plural over there, and only for these two. Everything else keeps its folder name.
        var target = folder switch
        {
            "textures/block" => "textures/blocks",
            "textures/item" => "textures/items",
            _ => folder,
        };

        return $"{target}/{stem}{extension}";
    }

    /// <summary>
    /// True for the companion maps a physically-based pack ships beside each colour texture.
    /// </summary>
    /// <remarks>
    /// <para>Half of a modern Bedrock pack is not colour: the one this was built against carries 516
    /// <c>_mer</c> maps, 320 <c>_normal</c> and 24 <c>_height</c> beside 1,336 actual textures. None
    /// of them is a picture of anything — a normal map read as colour is a lilac block.</para>
    /// <para>⚠ <b>Not usable as a blanket filter on reading.</b> Bedrock's plain sandstone side is
    /// genuinely called <c>sandstone_normal</c>, against <c>sandstone_carved</c> and
    /// <c>sandstone_smooth</c>. Every lookup here is by exact name so a companion is never reached
    /// by accident; this is for counting and reporting, where a suffix rule is all there is.</para>
    /// </remarks>
    public static bool IsCompanionMap(string path)
    {
        var dot = path.LastIndexOf('.');
        var stem = dot < 0 ? path : path[..dot];

        // Sandstone is the exception the rule would otherwise eat.
        if (stem.EndsWith("sandstone_normal", StringComparison.OrdinalIgnoreCase)) return false;

        return stem.EndsWith("_mer", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("_mers", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("_normal", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("_norm", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("_height", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("_heightmap", StringComparison.OrdinalIgnoreCase);
    }
}
