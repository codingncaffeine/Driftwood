using System.Text;

namespace Driftwood.Core.Textures;

/// <summary>
/// Reads a texture pack and reports what it carries art for that Driftwood has nothing to put it on.
/// </summary>
/// <remarks>
/// <para>A pack is a complete inventory of the reference game's content: every block, item, entity
/// and screen it has, one file each, organised by its author into folders that mean something. That
/// makes a pack the clearest available statement of what a game in this genre contains — and by
/// subtraction, the clearest statement of what this one is missing.</para>
/// <para>Generated rather than written down, which is the whole point. A hand-kept list of missing
/// content is out of date the day after it is written and nobody ever notices; this one is produced
/// from the pack and from our own texture table, so it shrinks by itself as blocks are added and it
/// cannot claim credit for something that is not there.</para>
/// <para>Grouped by morphology rather than by a list of the reference's names. A rule that says
/// "anything ending in _ore" holds for every ore that has ever existed and every one that will; a
/// list of forty ore names is a transcription that goes stale and that we have no business keeping.
/// </para>
/// </remarks>
public static class PackCoverage
{
    /// <summary>One family of content, and how to recognise a file belonging to it.</summary>
    /// <param name="Label">What to call it in the report.</param>
    /// <param name="Note">What building it would mean, for the reader deciding what to do next.</param>
    private sealed record Family(string Label, string Note, Func<string, bool> Matches);

    /// <summary>
    /// Recognisers, in order. First match wins, so the specific ones come before the general.
    /// </summary>
    /// <remarks>
    /// Written against word shapes — a suffix, a prefix, a stem — because those are what the format
    /// actually guarantees. Every wooden set in the game ends in the same handful of suffixes
    /// whatever the species is called, so one rule covers the species that exist and the ones added
    /// later.
    /// </remarks>
    private static readonly Family[] Families =
    [
        // Items first. A material prefix is stronger than a material rule — copper_axe is a tool,
        // not a copper mechanic, and putting the metal rule first quietly files every tool it
        // makes under the wrong heading.
        new("armour and tools", "tools and shears exist; armour is the whole of what is missing",
            n => n.EndsWith("_helmet") || n.EndsWith("_chestplate") || n.EndsWith("_leggings")
              || n.EndsWith("_boots") || n.EndsWith("_sword") || n.EndsWith("_pickaxe")
              || n.EndsWith("_axe") || n.EndsWith("_shovel") || n.EndsWith("_hoe")
              || n.StartsWith("bow") || n.Contains("shield") || n.Contains("arrow")
              || n.Contains("spawn_egg") || n.Contains("horse_armor")),

        new("food", "eating and hunger are unbuilt",
            n => n.Contains("bread") || n.Contains("apple") || n.Contains("beef")
              || n.Contains("porkchop") || n.Contains("chicken") || n.Contains("mutton")
              || n.Contains("rabbit") || n.Contains("cod") || n.Contains("salmon")
              || n.Contains("stew") || n.Contains("soup") || n.Contains("cookie")
              || n.Contains("cake") || n.Contains("honey") || n.Contains("milk")
              || n.Contains("berries") || n.Contains("_seeds")),

        new("shapes: fences, walls, gates", "wants neighbour updates",
            n => n.Contains("fence") || n.EndsWith("_wall") || n.Contains("_gate")),

        new("shapes: doors and trapdoors", "wants an open state and a use action",
            n => n.Contains("door")),

        new("signals", "levers, buttons, plates, wire, logic — a whole system",
            n => n.Contains("redstone") || n.Contains("lever") || n.Contains("button")
              || n.Contains("pressure_plate") || n.Contains("repeater") || n.Contains("comparator")
              || n.Contains("observer") || n.Contains("piston") || n.Contains("dispenser")
              || n.Contains("dropper") || n.Contains("hopper") || n.Contains("target")),

        new("rails and carts", "track shapes plus the first ridden entity",
            n => n.Contains("rail") || n.Contains("minecart")),

        new("wood: logs and planks", "one species today, driftoak",
            n => n.EndsWith("_log") || n.EndsWith("_wood") || n.EndsWith("_planks")
              || n.StartsWith("stripped_")),

        new("wood: the rest of a species set", "signs, boats, bookshelves, ladders",
            n => n.Contains("sign") || n.Contains("boat") || n.Contains("ladder")
              || n.Contains("bookshelf") || n.Contains("sapling") || n.EndsWith("_leaves")),

        new("ores and raw metal", "seven ores and their ingots; the deep bands are new",
            n => n.EndsWith("_ore") || n.StartsWith("raw_") || n.EndsWith("_ingot")
              || n.EndsWith("_nugget") || n.EndsWith("_block")),

        new("stone families", "five rocks, each worked and bonded; no cobbled or mossy forms",
            n => n.Contains("stone") || n.Contains("granite") || n.Contains("andesite")
              || n.Contains("diorite") || n.Contains("basalt") || n.Contains("tuff")
              || n.Contains("calcite") || n.Contains("cobble") || n.Contains("brick")),

        new("soil and ground", "dirt, sand, gravel, clay, snow are in",
            n => n.Contains("dirt") || n.Contains("grass") || n.Contains("sand")
              || n.Contains("gravel") || n.Contains("clay") || n.Contains("mud")
              || n.Contains("podzol") || n.Contains("mycelium") || n.Contains("farmland")),

        new("plants and crops", "five blooms and a tuft; farming and growth are still a system on their own",
            n => n.Contains("flower") || n.Contains("_bush") || n.Contains("wheat")
              || n.Contains("carrot") || n.Contains("potato") || n.Contains("beetroot")
              || n.Contains("melon") || n.Contains("pumpkin") || n.Contains("mushroom")
              || n.Contains("vine") || n.Contains("fern") || n.Contains("grass")
              || n.Contains("seagrass") || n.Contains("kelp") || n.Contains("cactus")
              || n.Contains("bamboo") || n.Contains("sugar_cane")),

        new("wool, dye and cloth", "wool, carpet and dye in sixteen colours; beds and banners are not started",
            n => n.Contains("wool") || n.Contains("carpet") || n.Contains("_dye")
              || n.Contains("bed") || n.Contains("banner") || n.Contains("terracotta")
              || n.Contains("concrete") || n.Contains("glazed")),

        new("glass and light", "glass, smokeglass, lanterns, campfires and a lamp; no stained glass",
            n => n.Contains("glass") || n.Contains("torch") || n.Contains("lantern")
              || n.Contains("lamp") || n.Contains("candle") || n.Contains("glowstone")
              || n.Contains("campfire") || n.Contains("fire")),

        new("storage and workstations", "the whole crafting and smelting surface",
            n => n.Contains("chest") || n.Contains("barrel") || n.Contains("furnace")
              || n.Contains("table") || n.Contains("anvil") || n.Contains("cauldron")
              || n.Contains("brewing") || n.Contains("beacon") || n.Contains("lectern")
              || n.Contains("loom") || n.Contains("smoker") || n.Contains("grindstone")
              || n.Contains("stonecutter") || n.Contains("composter") || n.Contains("shulker")),

        new("water, ice and the sea", "water and lava both flow now; no ice, no coral",
            n => n.Contains("water") || n.Contains("ice") || n.Contains("coral")
              || n.Contains("prismarine") || n.Contains("sponge") || n.Contains("conduit")
              || n.Contains("bubble")),

        new("other dimensions", "one world today",
            n => n.Contains("nether") || n.StartsWith("end_") || n.Contains("warped")
              || n.Contains("crimson") || n.Contains("soul") || n.Contains("blackstone")
              || n.Contains("purpur") || n.Contains("chorus") || n.Contains("obsidian")
              || n.Contains("portal") || n.Contains("lava") || n.Contains("magma")),

        new("copper and its weathering", "a whole mechanic: blocks that change over time",
            n => n.Contains("copper") || n.Contains("oxidized") || n.Contains("weathered")
              || n.Contains("exposed")),

        new("newer stone mechanics", "sculk, amethyst, geodes, froglights",
            n => n.Contains("sculk") || n.Contains("amethyst") || n.Contains("budding")
              || n.Contains("froglight") || n.Contains("deepslate")),

        new("brewing and enchanting", "two systems, neither started",
            n => n.Contains("potion") || n.Contains("enchant") || n.Contains("experience")
              || n.Contains("book") || n.Contains("blaze") || n.Contains("ghast")),
    ];

    /// <summary>One family of art a pack carries, and how much of it we have anything to put on.</summary>
    public readonly record struct Gap(string Label, int Files, int Covered);

    /// <summary>What a pack carries, in numbers rather than in prose.</summary>
    /// <param name="Art">Block and item pictures in the pack, companion maps excluded.</param>
    /// <param name="Covered">How many of those we actually consume.</param>
    /// <param name="Biggest">The families with the most art we have nothing for, largest first.</param>
    public readonly record struct Summary(int Art, int Covered, IReadOnlyList<Gap> Biggest);

    /// <summary>
    /// The same walk as <see cref="Report"/>, answered as numbers so a screen can say it.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>The user asked for it in these words:</b> <i>"if there's things that the pack
    /// provides textures for that we don't have yet, we should have a tally of that stuff
    /// somewhere"</i>. The walk existed and was a command-line flag — which is to say it existed for
    /// me and not for them. This is the same answer with the prose taken off it.</para>
    /// <para>⚠ <b>Paths only, never pixels.</b> It reads the archive's index and nothing else, so a
    /// six-hundred-megabyte pack costs about what a small one does and a screen can ask on the way
    /// in rather than making somebody wait for it.</para>
    /// <para>⛔ Off the same <see cref="Families"/> table <see cref="Report"/> uses. A second list of
    /// rules would agree with it until the day one of them was edited, and the symptom would be two
    /// instruments quietly disagreeing about what the game is missing.</para>
    /// </remarks>
    public static Summary Tally(string packPath)
    {
        using var pack = TexturePack.Open(packPath);
        if (pack is null) return new Summary(0, 0, []);

        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in BlockTextureSet.Layers)
        {
            if (layer.PackPath.Length > 0)
                foreach (var stem in PackLayouts.AllStems(layer.PackPath)) consumed.Add(stem);

            if (layer.PackPathAlt.Length > 0)
                foreach (var stem in PackLayouts.AllStems(layer.PackPathAlt)) consumed.Add(stem);
        }

        var byFamily = new Dictionary<string, (int Files, int Covered)>(StringComparer.Ordinal);
        int art = 0, covered = 0;

        foreach (var entry in pack.Entries())
        {
            if (!entry.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Contains("/models/") || entry.Contains("/blockstates/")) continue;
            if (PackLayouts.IsCompanionMap(entry)) continue;

            // Both spellings and both roots, for the reason Report gives: one layout reaches the
            // folder through assets/ and a namespace, the other has it at the pack's own root.
            var wanted = Folder(entry, "block") || Folder(entry, "blocks")
                      || Folder(entry, "item") || Folder(entry, "items");

            if (!wanted) continue;

            art++;

            var stem = Path.GetFileNameWithoutExtension(entry);
            var mine = consumed.Contains(stem) ? 1 : 0;
            covered += mine;

            foreach (var family in Families)
            {
                if (!family.Matches(stem)) continue;

                byFamily.TryGetValue(family.Label, out var run);
                byFamily[family.Label] = (run.Files + 1, run.Covered + mine);
                break;
            }
        }

        var gaps = byFamily
            .Select(f => new Gap(f.Key, f.Value.Files, f.Value.Covered))
            .Where(g => g.Covered < g.Files)
            .OrderByDescending(g => g.Files - g.Covered)
            .Take(6)
            .ToArray();

        return new Summary(art, covered, gaps);

        static bool Folder(string path, string folder) =>
            path.Contains($"/textures/{folder}/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith($"textures/{folder}/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads a pack and returns the report.</summary>
    public static string Report(string packPath)
    {
        var sb = new StringBuilder();

        using var pack = TexturePack.Open(packPath);
        if (pack is null) return $"no pack at '{packPath}'";

        // What we already consume, so a family we have covered does not read as a gap.
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in BlockTextureSet.Layers)
        {
            // Every name the layer could be filed under, not just the modern one. On an old pack
            // our oak log is their log_oak, and counting only the modern stem reported a pack we
            // read forty layers out of as one we had consumed nothing from.
            if (layer.PackPath.Length > 0)
                foreach (var stem in PackLayouts.AllStems(layer.PackPath)) consumed.Add(stem);

            if (layer.PackPathAlt.Length > 0)
                foreach (var stem in PackLayouts.AllStems(layer.PackPathAlt)) consumed.Add(stem);
        }

        var byFamily = new Dictionary<string, (int Total, int Have, List<string> Sample)>(StringComparer.Ordinal);
        int blocks = 0, items = 0, entities = 0, models = 0, states = 0, other = 0, ungrouped = 0, have = 0;
        var ungroupedNames = new List<string>();

        // A texture folder, however the pack spells the path to it. One layout reaches it through
        // assets/ and a namespace, another has it at the root, so "contains /textures/x/" misses
        // half of them and "starts with textures/x/" misses the other half.
        static bool Under(string path, string folder) =>
            path.Contains($"/textures/{folder}/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith($"textures/{folder}/", StringComparison.OrdinalIgnoreCase);

        var companions = 0;

        foreach (var entry in pack.Entries())
        {
            if (entry.Contains("/models/")) { models++; continue; }
            if (entry.Contains("/blockstates/")) { states++; continue; }

            if (!entry.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { other++; continue; }

            // A normal or roughness map is not a picture of anything. Counting them as art doubles
            // every number below and reads as a pack twice the size it is.
            if (PackLayouts.IsCompanionMap(entry)) { companions++; continue; }

            // Both spellings. The folder went plural before 2018 and stayed plural in the other
            // layout, and the leading slash cannot be relied on either — a Bedrock pack's textures
            // sit at its root, so the path starts with the folder rather than reaching it.
            var isBlock = Under(entry, "block") || Under(entry, "blocks");
            var isItem = Under(entry, "item") || Under(entry, "items");
            var isEntity = Under(entry, "entity");

            if (isEntity) { entities++; continue; }
            if (!isBlock && !isItem) { other++; continue; }

            if (isBlock) blocks++; else items++;

            var name = Stem(entry);
            var family = Classify(name) ?? Classify(WithoutFace(name));
            if (family is null)
            {
                ungrouped++;
                if (ungroupedNames.Count < 24) ungroupedNames.Add(name);
                continue;
            }

            var mine = consumed.Contains(name) ? 1 : 0;
            have += mine;

            if (!byFamily.TryGetValue(family.Label, out var tally)) tally = (0, 0, []);
            if (mine == 0 && tally.Sample.Count < 5) tally.Sample.Add(name);
            byFamily[family.Label] = (tally.Total + 1, tally.Have + mine, tally.Sample);
        }

        sb.AppendLine(
            $"pack          {pack.Name} ({pack.Dialect.ToString().ToLowerInvariant()} layout, format {pack.Format})");
        sb.AppendLine($"files         {blocks:N0} block, {items:N0} item, {entities:N0} entity textures; "
                    + $"{models:N0} models, {states:N0} blockstates, {other:N0} other"
                    + (companions > 0 ? $"; {companions:N0} normal and roughness maps set aside" : ""));
        sb.AppendLine($"we read       {have} of them");
        sb.AppendLine();
        sb.AppendLine("Every entity texture is a creature, and there are no creatures here at all —");
        sb.AppendLine("that count is the size of the mob work on its own. Stairs, slabs, fences and walls");
        sb.AppendLine("appear in no family below because they carry no art of their own: a shape wears its");
        sb.AppendLine("parent block's texture, which is why the shape work needed no new tiles.");
        sb.AppendLine();
        sb.AppendLine("families the pack carries art for, largest first");
        sb.AppendLine();

        var ordered = new List<KeyValuePair<string, (int Total, int Have, List<string> Sample)>>(byFamily);
        ordered.Sort((a, b) => b.Value.Total.CompareTo(a.Value.Total));

        foreach (var (label, tally) in ordered)
        {
            var note = Array.Find(Families, f => f.Label == label)?.Note ?? "";
            var share = tally.Have * 100.0 / tally.Total;

            sb.AppendLine($"  {label,-32} {tally.Total,5} files, {tally.Have,3} covered ({share,4:F0}%)   {note}");
            if (tally.Sample.Count > 0)
                sb.AppendLine($"  {"",-32} e.g. {string.Join(", ", tally.Sample)}");
        }

        sb.AppendLine();
        sb.AppendLine($"ungrouped     {ungrouped:N0} files matched no family rule");
        if (ungroupedNames.Count > 0)
            sb.AppendLine($"              e.g. {string.Join(", ", ungroupedNames)}");

        sb.AppendLine();
        sb.AppendLine("A family at 0% is a system with nothing behind it. A family with a handful covered");
        sb.AppendLine("is one block of a set. Sorted by size because a group of forty files is a system and");
        sb.AppendLine("a group of one is a decoration.");

        return sb.ToString();
    }

    private static Family? Classify(string name) => Array.Find(Families, f => f.Matches(name));

    /// <summary>The bare file name, lower-cased, without folders or extension.</summary>
    private static string Stem(string path)
    {
        var slash = path.LastIndexOf('/');
        var name = slash < 0 ? path : path[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        return (dot < 0 ? name : name[..dot]).ToLowerInvariant();
    }

    /// <summary>
    /// The faces and states a block's several files describe, which say nothing about what it is.
    /// </summary>
    /// <remarks>
    /// A furnace is four files — front, side, top, and a lit front — describing one block. Grouping
    /// them separately would put three quarters of a pack under "no rule matched" and would count
    /// one furnace as four missing things. Stripped only for classification; the names printed as
    /// examples keep their suffix so they can still be found on disk.
    /// </remarks>
    private static readonly string[] FaceSuffixes =
    [
        "_top", "_bottom", "_side", "_front", "_back", "_end", "_inner", "_outer",
        "_on", "_off", "_lit", "_stage0", "_stage1", "_stage2", "_stage3",
    ];

    private static string WithoutFace(string name)
    {
        foreach (var suffix in FaceSuffixes)
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
                return name[..^suffix.Length];

        return name;
    }
}
