using System.Text;

namespace Driftwood.Core.Textures;

/// <summary>A read-only compatibility census for an ignored folder of real resource packs.</summary>
public static class PackMatrix
{
    public sealed record Result(string Report, bool Passed, int Packs, int Invalid);

    public static Result Build(string folder, string? cacheFolder = null)
    {
        var report = new StringBuilder();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return new Result($"pack matrix  folder does not exist: {folder}", false, 0, 0);

        folder = Path.GetFullPath(folder);
        var paths = Directory.EnumerateFileSystemEntries(folder)
            .Where(path => !Path.GetFileName(path).StartsWith('.', StringComparison.Ordinal)
                           && (Directory.Exists(path) || TexturePack.Extensions.Contains(
                               Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        report.AppendLine($"pack matrix  {folder}");
        report.AppendLine($"packs        {paths.Length:N0}");
        report.AppendLine();

        var rows = new List<(string Name, PackDialect Dialect, int Resolution,
            PackCompatibility.Summary Compatibility)>();
        foreach (var path in paths)
        {
            var dialect = PackDialect.Unknown;
            var resolution = 0;
            using (var pack = TexturePack.Open(path, out _))
            {
                if (pack is not null)
                {
                    dialect = pack.Dialect;
                    resolution = pack.DetectResolution();
                }
            }

            var compatibility = PackCompatibility.Inspect(path, cacheFolder: cacheFolder);
            var name = Path.GetFileName(path.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            rows.Add((name, dialect, resolution, compatibility));
            report.Append("  ").Append(Fit(name, 34)).Append(' ')
                .Append(Fit(compatibility.State, 25)).Append(' ')
                .Append(Fit(dialect.ToString(), 11)).Append(' ')
                .Append(resolution > 0 ? $"{resolution,4}px" : "   — ")
                .Append("  runtime ").Append(compatibility.RuntimeOmissions)
                .Append("  content ").Append(compatibility.ContentOpportunities)
                .AppendLine(compatibility.Cached ? "  cached" : "");
            foreach (var issue in compatibility.Issues.Take(3))
                report.Append("      ! ").AppendLine(issue);
            if (compatibility.Issues.Count > 3)
                report.Append("      ! … and ").Append(compatibility.Issues.Count - 3)
                    .AppendLine(" more named omissions");
        }

        report.AppendLine();
        report.AppendLine("outcomes");
        foreach (var state in new[]
                 {
                     PackCompatibility.Verified, PackCompatibility.WithOmissions,
                     PackCompatibility.RequiresExternal, PackCompatibility.Invalid,
                 })
            report.Append("  ").Append(Fit(state, 28)).Append(' ')
                .AppendLine(rows.Count(row => row.Compatibility.State == state).ToString("N0"));

        report.AppendLine();
        report.AppendLine("feature families              packs   files   used  disposition");
        foreach (var family in rows.SelectMany(row => row.Compatibility.Families)
                     .GroupBy(family => family.Name, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var present = family.Count(item => item.Files > 0);
            var files = family.Sum(item => item.Files);
            var used = family.Sum(item => item.Consumed);
            var disposition = family.GroupBy(item => item.Disposition)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .Select(group => $"{group.Key} {group.Count()}");
            report.Append("  ").Append(Fit(family.Key, 28))
                .Append(present.ToString().PadLeft(6)).Append(files.ToString().PadLeft(8))
                .Append(used.ToString().PadLeft(7)).Append("  ")
                .AppendLine(string.Join(", ", disposition));
        }

        var material = Files("material companion maps");
        var environment = Files("environment and weather art");
        var bedrock = Files("Bedrock-specific resources");
        var extensions = Files("loader extensions");
        var content = rows.Sum(row => row.Compatibility.ContentOpportunities);
        report.AppendLine();
        report.AppendLine("routing");
        report.AppendLine("  P7.6/#110  GUI, sounds.json, emitted particles and verified-cache runtime paths");
        report.AppendLine("  #56        standard Java blockstates/models/items (closed by this runtime gate)");
        report.AppendLine($"  P9         {material + environment:N0} material/environment files routed through the live renderers");
        report.AppendLine($"  #54        {bedrock:N0} Bedrock-specific files await Bedrock resource semantics");
        report.AppendLine($"  #42        {content:N0} owned-content analogue opportunities across the corpus");
        report.AppendLine($"  external   {extensions:N0} loader-extension/dependency markers stay explicitly optional or required");

        var invalid = rows.Count(row => row.Compatibility.State == PackCompatibility.Invalid);
        return new Result(report.ToString().TrimEnd(), true, rows.Count, invalid);

        int Files(string familyName) => rows.SelectMany(row => row.Compatibility.Families)
            .Where(family => family.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase))
            .Sum(family => family.Files);
    }

    private static string Fit(string text, int width) => text.Length <= width
        ? text.PadRight(width)
        : string.Concat(text.AsSpan(0, Math.Max(1, width - 1)), "…");
}
