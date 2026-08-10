using Driftwood.Core.Audio;
using Driftwood.Core.Entities;
using Driftwood.Core.Items;

namespace Driftwood.Core.Textures;

/// <summary>
/// What Driftwood can actually consume from one installed resource pack, separate from how much art
/// the pack happens to contain.
/// </summary>
/// <remarks>
/// A filename count is not a compatibility badge. This inventory matches the archive itself against
/// the runtime tables for block/item art, GUI sprites, creatures, armour, particles, sounds and the
/// printable-ASCII font. Standard models that Driftwood does not interpret and loader-specific
/// extensions are named explicitly, so “partial” never masquerades as “verified.”
/// </remarks>
public static class PackCompatibility
{
    public readonly record struct Summary(
        string State,
        int Art,
        int ArtUsed,
        int Gui,
        int GuiUsed,
        int FontGlyphs,
        int FontProviders,
        IReadOnlyList<string> FontOmissions,
        int Sounds,
        int SoundSlots,
        int Particles,
        int ParticlesUsed,
        int Entities,
        int EntitiesUsed,
        int Armour,
        int ArmourUsed,
        int Models,
        int BlockStates,
        int External,
        IReadOnlyList<string> Issues)
    {
        public int StandardOmissions =>
            Math.Max(0, Art - ArtUsed)
            + Math.Max(0, Sounds - SoundSlots)
            + Math.Max(0, Particles - ParticlesUsed)
            + Math.Max(0, Entities - EntitiesUsed)
            + Math.Max(0, Armour - ArmourUsed)
            + Models + BlockStates;
    }

    public static Summary Inspect(string path)
    {
        var issues = new List<string>();
        using var pack = TexturePack.Open(path, out var why);
        if (pack is null)
        {
            issues.Add(why ?? "the pack could not be opened");
            return new Summary("invalid", 0, 0, 0, 0, 0, 0, [], 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, issues);
        }

        var coverage = PackCoverage.Tally(path);
        var entries = pack.Entries().ToArray();

        var guiEntries = entries.Where(entry => IsPngUnder(entry, "gui")).ToArray();
        var guiUsed = GuiTextureSet.Entries.Count(mapping =>
            Has(entries, mapping.Path) || mapping.Alternate.Length > 0 && Has(entries, mapping.Alternate));

        var particleEntries = entries.Where(entry => IsPngUnder(entry, "particle")).ToArray();
        var particlePaths = BlockTextureSet.Layers
            .SelectMany(layer => layer.PackPathAlt.Length > 0
                ? new[] { layer.PackPath, layer.PackPathAlt }
                : new[] { layer.PackPath })
            .Where(pathName => pathName.Contains("textures/particle/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var particlesUsed = particlePaths.Count(pathName => Has(entries, pathName));

        var entityEntries = entries.Where(entry => IsPngUnder(entry, "entity")).ToArray();
        var entityPaths = CreatureSet.All
            .SelectMany(kind => kind.Skins)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var entitiesUsed = entityPaths.Count(pathName => Has(entries, pathName));

        var armourEntries = entries.Where(entry =>
            entry.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            && (entry.Contains("/textures/models/armor/", StringComparison.OrdinalIgnoreCase)
                || entry.StartsWith("textures/models/armor/", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("/armor/", StringComparison.OrdinalIgnoreCase)
                || entry.StartsWith("armor/", StringComparison.OrdinalIgnoreCase))).ToArray();
        var armourPaths = Armour.Materials
            .SelectMany(material => Enumerable.Range(0, ArmourSheets.Layers)
                .SelectMany(layer => ArmourSheets.Candidates(material, layer)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var armourUsed = armourPaths.Count(pathName => Has(entries, pathName));

        var font = FontTextureSet.Load(pack);
        issues.AddRange(font.Faults.Take(4));

        var sounds = 0;
        var soundSlots = 0;
        try
        {
            var audio = SoundPackArchive.Inspect(path, requireSounds: false);
            sounds = audio.Clips;
            soundSlots = audio.Covered;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            issues.Add($"audio: {error.Message}");
        }

        var models = entries.Count(entry => IsJsonUnder(entry, "models"));
        var states = entries.Count(entry => IsJsonUnder(entry, "blockstates"));
        var external = entries.Count(IsExternal);

        issues.AddRange(pack.Faults.Take(Math.Max(0, 4 - issues.Count)));

        var omitted = Math.Max(0, coverage.Art - coverage.Covered)
            + Math.Max(0, sounds - soundSlots)
            + Math.Max(0, particleEntries.Length - particlesUsed)
            + Math.Max(0, entityEntries.Length - entitiesUsed)
            + Math.Max(0, armourEntries.Length - armourUsed)
            + models + states;
        var state = issues.Count > 0 ? "issues"
            : external > 0 ? "external features"
            : omitted > 0 || font.Omissions.Count > 0 ? "partial"
            : "compatible";

        return new Summary(
            state,
            coverage.Art,
            coverage.Covered,
            guiEntries.Length,
            guiUsed,
            font.BitmapGlyphs,
            font.Providers,
            font.Omissions,
            sounds,
            soundSlots,
            particleEntries.Length,
            particlesUsed,
            entityEntries.Length,
            entitiesUsed,
            armourEntries.Length,
            armourUsed,
            models,
            states,
            external,
            issues);
    }

    private static bool Has(IEnumerable<string> entries, string assetPath) =>
        entries.Any(entry => entry.EndsWith(assetPath, StringComparison.OrdinalIgnoreCase));

    private static bool IsPngUnder(string path, string folder) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        && (path.Contains($"/textures/{folder}/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith($"textures/{folder}/", StringComparison.OrdinalIgnoreCase));

    private static bool IsJsonUnder(string path, string folder) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        && (path.Contains($"/{folder}/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith($"{folder}/", StringComparison.OrdinalIgnoreCase));

    private static bool IsExternal(string path) =>
        path.Contains("/optifine/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("optifine/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/mcpatcher/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("mcpatcher/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/shaders/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/iris/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/polytone/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/citresewn/", StringComparison.OrdinalIgnoreCase);
}
