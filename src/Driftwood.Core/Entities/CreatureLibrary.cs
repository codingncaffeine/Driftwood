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
    public static List<CreatureSet.Resolved> Resolve(
        IReadOnlyList<CreatureModel> models, TexturePack? pack, int skinSize)
    {
        var resolved = new List<CreatureSet.Resolved>(CreatureSet.All.Length);

        foreach (var kind in CreatureSet.All)
        {
            var skeleton = CreatureSet.Match(models, kind.Skeleton);

            var from = "";
            var size = 0;

            if (pack is not null)
            {
                foreach (var path in kind.Skins)
                {
                    var tile = pack.TryLoadTile(path, skinSize, out var where);
                    if (tile is null) continue;

                    from = where;
                    size = skinSize;
                    break;
                }
            }

            resolved.Add(new CreatureSet.Resolved(
                kind, skeleton, skeleton?.Name ?? "", from, size));
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
                + (entry.SkinFrom.Length > 0 ? entry.SkinFrom : "— no skin —"));

            foreach (var fault in faults) text.AppendLine($"            ⚠ {fault}");
        }

        text.AppendLine();
        text.AppendLine(
            $"{withSkeleton} of {resolved.Count} creatures have a skeleton, {withSkin} have a skin, "
            + $"{faulted} have a net fault");

        return text.ToString();
    }
}
