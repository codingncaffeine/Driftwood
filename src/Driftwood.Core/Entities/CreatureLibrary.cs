using System.Text;
using Driftwood.Core.Textures;

namespace Driftwood.Core.Entities;

/// <summary>
/// Gathers creature skeletons off the user's own disk and matches them to our creatures.
/// </summary>
/// <remarks>
/// <para>⛔ <b>Nothing here is bundled and nothing is extracted.</b> The geometry stays in the
/// install it came with, the art stays in the pack it came with, and what ships is this reader. Same
/// posture as the texture pack import, and the same one OpenMW and GZDoom take.</para>
/// <para>The skeletons live in the game rather than in a pack — a pack only overrides shapes it
/// changes — so the two halves of a creature come from two different places, and either can be
/// missing. The report says which, per creature, because "no creatures appeared" and "no skins
/// appeared" look identical from the far side.</para>
/// </remarks>
public static class CreatureLibrary
{
    /// <summary>
    /// Where an installed Bedrock client keeps its resource packs, or null when there is not one.
    /// </summary>
    /// <remarks>
    /// The store install is versioned into its own folder name, so the newest is taken rather than a
    /// path being written down. ⚠ Deliberately quiet: a machine without the game is the normal case
    /// and is not a fault — it means our own creatures are the ones that get drawn.
    /// </remarks>
    /// <summary>Why the search came back empty, when it did. For the report, not for control flow.</summary>
    public static string LastLookupNote { get; private set; } = "";

    public static string? FindInstalledGeometry()
    {
        try
        {
            var apps = new DirectoryInfo(@"C:\Program Files\WindowsApps");
            if (!apps.Exists)
            {
                LastLookupNote = @"C:\Program Files\WindowsApps is not readable from here";
                return null;
            }

            DirectoryInfo? newest = null;
            foreach (var directory in apps.EnumerateDirectories("Microsoft.MinecraftUWP_*"))
                if (newest is null || string.CompareOrdinal(directory.Name, newest.Name) > 0) newest = directory;

            if (newest is null)
            {
                LastLookupNote = "no Microsoft.MinecraftUWP_* under WindowsApps";
                return null;
            }

            var packs = Path.Combine(newest.FullName, "data", "resource_packs");
            if (Directory.Exists(packs)) return packs;

            LastLookupNote = $"{newest.Name} has no data\\resource_packs";
            return null;
        }
        catch (Exception error)
        {
            // An unreadable Program Files is a machine we simply do not read geometry from — but it
            // is worth saying WHY, because "you have no Minecraft" and "I was not allowed to look"
            // are different problems with the same symptom.
            LastLookupNote = $"{error.GetType().Name}: {error.Message}";
            return null;
        }
    }

    /// <summary>Reads every geometry file under a folder, newest overlay last so it wins.</summary>
    public static List<CreatureModel> ReadFolder(string root, List<string> faults)
    {
        var models = new List<CreatureModel>();
        if (!Directory.Exists(root)) return models;

        var files = new List<string>();
        foreach (var pattern in (string[])["*.geo.json", "mobs.json"])
        {
            try { files.AddRange(Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)); }
            catch (Exception error) { faults.Add($"{root}: {error.Message}"); }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            try { models.AddRange(BedrockGeometry.Read(File.ReadAllText(file), faults)); }
            catch (Exception error) { faults.Add($"{Path.GetFileName(file)}: {error.Message}"); }
        }

        // ⛔ Across the whole folder, not per file. A 1.8 model can name a parent that lives in
        // another file entirely — every zombie variant does — so resolving inside Read gave them all
        // nought bones and no complaint at all.
        BedrockGeometry.ResolveInheritance(models);
        return models;
    }

    /// <summary>
    /// Resolves every creature in <see cref="CreatureSet.All"/> against a geometry folder and a pack.
    /// </summary>
    /// <remarks>
    /// ⛔ The skin comes back through <c>TryLoadSheet</c>, at the shape it was painted. It went
    /// through the tile loader first, which squares everything — right for a block face and wrong
    /// for every creature, because a net addressed in texels of a 64×32 sheet lands on different
    /// pixels the moment the sheet is stretched to 64×64. Nothing would have thrown; every animal
    /// would simply have worn its own texture inside out.
    /// </remarks>
    public static List<CreatureSet.Resolved> Resolve(IReadOnlyList<CreatureModel> models, TexturePack? pack)
    {
        var resolved = new List<CreatureSet.Resolved>(CreatureSet.All.Length);

        foreach (var kind in CreatureSet.All)
        {
            var skeleton = CreatureSet.Match(models, kind.Skeleton);

            var from = "";
            var width = 0;
            var height = 0;

            if (pack is not null)
            {
                foreach (var path in kind.Skins)
                {
                    var sheet = pack.TryLoadSheet(path, out var where);
                    if (sheet is null) continue;

                    from = where;
                    width = sheet.Width;
                    height = sheet.Height;
                    break;
                }
            }

            resolved.Add(new CreatureSet.Resolved(
                kind, skeleton, skeleton?.Name ?? "", from, width, height));
        }

        return resolved;
    }

    /// <summary>A line per creature: what it found, how big it is, and whether its net is sound.</summary>
    public static string Report(IReadOnlyList<CreatureModel> models, IReadOnlyList<CreatureSet.Resolved> resolved)
    {
        var text = new StringBuilder();
        text.AppendLine($"{models.Count} skeletons read");
        text.AppendLine();
        text.AppendLine($"{"ours",-11} {"skeleton",-18} {"sheet",-9} {"bones",5} {"cubes",5}  {"size in blocks",-16} skin");

        var withSkeleton = 0;
        var withSkin = 0;
        var faulted = 0;

        foreach (var entry in resolved)
        {
            if (entry.Skeleton is not { } model)
            {
                text.AppendLine($"{entry.Kind.Name,-11} {"— none —",-18}");
                continue;
            }

            withSkeleton++;
            if (entry.SkinFrom.Length > 0) withSkin++;

            var (min, max) = model.Bounds();
            var extent = (max - min) / 16f;
            var faults = model.Validate();
            if (faults.Count > 0) faulted++;

            text.AppendLine(
                $"{entry.Kind.Name,-11} {entry.SkeletonFrom,-18} "
                + $"{model.SheetWidth,3}x{model.SheetHeight,-5} {model.Bones.Length,5} {model.CubeCount,5}  "
                + $"{extent.X,4:F2} x {extent.Y,4:F2} x {extent.Z,4:F2}  "
                + (entry.SkinFrom.Length > 0
                    ? $"{entry.SkinWidth}x{entry.SkinHeight} {entry.SkinFrom}"
                    : "— no skin —"));

            // ⛔ The sheet has to be the shape the skeleton says it is, or every patch on it is in
            // the wrong place. A 256-pixel pack paints a cow at 256x128 — the same 2:1 — so this
            // compares the RATIO, not the size.
            //
            // ⛳ It found two real cases on the first run, and they are different problems. Some of
            // Intermacgod's sheets are PADDED SQUARE (a cow at 1024x1024 where the net is 2:1), and
            // the answer there is to scale by the WIDTH alone and ignore the spare height — the art
            // is in the top of the image where the net says it is. Others are a genuine version
            // mismatch (its spider is 2:1 against a skeleton cut for 1:1), and no scaling fixes
            // that; it wants the older skeleton. Both look identical on screen — an animal wearing
            // its own texture inside out — which is why it is a line in a report and not a guess.
            if (entry.SkinWidth > 0
                && entry.SkinWidth * model.SheetHeight != entry.SkinHeight * model.SheetWidth)
            {
                text.AppendLine(
                    $"            ⚠ the sheet is {entry.SkinWidth}x{entry.SkinHeight}, which is not the "
                    + $"{model.SheetWidth}x{model.SheetHeight} shape the skeleton is cut for");
            }

            foreach (var fault in faults) text.AppendLine($"            ⚠ {fault}");
        }

        text.AppendLine();
        text.AppendLine(
            $"{withSkeleton} of {resolved.Count} creatures have a skeleton, {withSkin} have a skin, "
            + $"{faulted} have a net fault");

        return text.ToString();
    }
}
