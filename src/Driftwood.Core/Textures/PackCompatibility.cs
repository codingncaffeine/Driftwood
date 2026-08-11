using System.Text;
using System.Text.Json;
using Driftwood.Core.Audio;
using Driftwood.Core.Blocks;
using Driftwood.Core.Entities;
using Driftwood.Core.Items;

namespace Driftwood.Core.Textures;

/// <summary>Runtime compatibility of an exact pack, kept separate from content completeness.</summary>
public static class PackCompatibility
{
    public const string NotChecked = "NOT CHECKED";
    public const string Verified = "DRIFTWOOD VERIFIED";
    public const string WithOmissions = "WORKS WITH OMISSIONS";
    public const string RequiresExternal = "REQUIRES EXTERNAL FEATURE";
    public const string Invalid = "INVALID";

    public enum FamilyDisposition
    {
        Consumed,
        SupportedButNotUsed,
        NotApplicable,
        UnsupportedStandardFeature,
        OptionalExtension,
        Unknown,
    }

    public sealed record Family(
        string Name,
        FamilyDisposition Disposition,
        int Files,
        int Consumed,
        string Note,
        IReadOnlyList<string> Samples);

    /// <summary>
    /// The first fields preserve the original screen/report contract. <see cref="RuntimeOmissions"/>
    /// is deliberately independent from <see cref="ContentOpportunities"/>: art for a block the game
    /// does not own is roadmap evidence, not evidence that loading this pack failed.
    /// </summary>
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
        IReadOnlyList<string> Issues,
        string Hash = "",
        bool Cached = false,
        int RuntimeOmissions = 0,
        int ContentOpportunities = 0,
        IReadOnlyList<Family>? Inventory = null,
        IReadOnlyList<PackLibrary.PackDependency>? Dependencies = null)
    {
        public int StandardOmissions => RuntimeOmissions;
        public IReadOnlyList<Family> Families => Inventory ?? [];
        public IReadOnlyList<PackLibrary.PackDependency> PackDependencies => Dependencies ?? [];
    }

    private const int CacheSchema = 9;
    private const int MaximumCacheBytes = 1024 * 1024;
    private sealed record CacheRecord(int Schema, string Hash, string DependencySignature, Summary Summary);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string CacheFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Driftwood", "cache", "pack-compatibility");

    /// <summary>
    /// Walks the archive and the decoders for families Driftwood owns. A caller-provided verified
    /// hash avoids hashing a freshly downloaded archive twice; otherwise exact bytes are hashed here.
    /// </summary>
    public static Summary Inspect(
        string path,
        IReadOnlyList<PackLibrary.PackDependency>? dependencies = null,
        string? verifiedHash = null,
        string? cacheFolder = null,
        bool useCache = true)
    {
        dependencies ??= [];
        var dependencySignature = DependencySignature(dependencies);
        string hash;
        try
        {
            hash = NormalHash(verifiedHash) ?? PackLibrary.Fingerprint(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Empty(Invalid, error.Message, dependencies);
        }

        cacheFolder = System.IO.Path.GetFullPath(string.IsNullOrWhiteSpace(cacheFolder)
            ? CacheFolder : cacheFolder);
        if (useCache && ReadCache(cacheFolder, hash, dependencySignature) is { } cached)
            return cached with { Cached = true };

        var issues = new List<string>();
        var families = new List<Family>();
        using var pack = TexturePack.Open(path, out var why);
        if (pack is null)
        {
            issues.Add(why ?? "the pack could not be opened");
            return Save(Empty(Invalid, issues[0], dependencies) with { Hash = hash }, cacheFolder,
                dependencySignature, useCache);
        }

        if (!pack.WithinSafetyBounds(out var bounds))
        {
            issues.Add(bounds);
            return Save(Empty(Invalid, bounds, dependencies) with { Hash = hash }, cacheFolder,
                dependencySignature, useCache);
        }

        var entries = pack.Entries().ToArray();
        var unsafePath = entries.FirstOrDefault(IsUnsafePath);
        if (unsafePath is not null)
        {
            issues.Add($"unsafe archive path '{unsafePath}'");
            return Save(Empty(Invalid, issues[0], dependencies) with { Hash = hash }, cacheFolder,
                dependencySignature, useCache);
        }

        var coverage = PackCoverage.Tally(path);
        var runtimeOmissions = 0;

        var blockPaths = BlockTextureSet.Layers.SelectMany(static layer =>
                layer.PackPathAlt.Length > 0
                    ? new[] { layer.PackPath, layer.PackPathAlt }
                    : new[] { layer.PackPath })
            .Where(static candidate => candidate.Length > 0
                                       && !candidate.Contains("textures/particle/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var blockArt = ValidateMappedPngs(pack, entries, blockPaths, "block/item art", issues);
        runtimeOmissions += blockArt.Invalid;
        families.Add(FamilyOf("block and item art", blockArt, coverage.Art,
            "Mapped art overlays Driftwood layers; unrelated content remains a coverage opportunity."));

        var guiEntries = entries.Where(entry => IsPngUnder(entry, "gui")).ToArray();
        var guiPaths = GuiTextureSet.Entries.SelectMany(static mapping => mapping.Alternate.Length > 0
                ? new[] { mapping.Path, mapping.Alternate } : new[] { mapping.Path })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var gui = ValidateMappedPngs(pack, entries, guiPaths, "GUI", issues);
        runtimeOmissions += gui.Invalid;
        families.Add(FamilyOf("GUI sprites", gui, guiEntries.Length,
            "Known controls use pack sprites; screens Driftwood does not have are not load failures."));

        var fontEntries = entries.Where(entry =>
            entry.Contains("/font/", StringComparison.OrdinalIgnoreCase)
            || entry.StartsWith("font/", StringComparison.OrdinalIgnoreCase)).ToArray();
        var font = FontTextureSet.Load(pack);
        issues.AddRange(font.Faults.Take(16));
        runtimeOmissions += font.Faults.Count + (fontEntries.Length > 0 ? font.Omissions.Count : 0);
        families.Add(new Family("printable ASCII font",
            fontEntries.Length == 0 ? FamilyDisposition.SupportedButNotUsed
            : font.Faults.Count > 0 || font.Omissions.Count > 0
                ? FamilyDisposition.UnsupportedStandardFeature : FamilyDisposition.Consumed,
            fontEntries.Length, font.BitmapGlyphs,
            "Bitmap, space and reference providers are supported; other glyphs fall back individually.",
            Samples(fontEntries)));

        var sounds = 0;
        var soundSlots = 0;
        try
        {
            var audio = SoundPackArchive.Inspect(path, requireSounds: false);
            sounds = audio.Clips;
            soundSlots = audio.Covered;
            families.Add(new Family("sound events",
                sounds == 0 ? FamilyDisposition.SupportedButNotUsed : FamilyDisposition.Consumed,
                sounds, soundSlots,
                "Physical clips and sounds.json aliases used by existing Driftwood events are consumed; unrelated events are not applicable.",
                audio.Entries.Keys.Take(4).ToArray()));
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            issues.Add($"audio: {error.Message}");
            runtimeOmissions++;
            families.Add(new Family("sound events", FamilyDisposition.UnsupportedStandardFeature,
                0, 0, error.Message, []));
        }

        var particleEntries = entries.Where(entry => IsPngUnder(entry, "particle")).ToArray();
        var particlePaths = BlockTextureSet.Layers
            .SelectMany(static layer => layer.PackPathAlt.Length > 0
                ? new[] { layer.PackPath, layer.PackPathAlt } : new[] { layer.PackPath })
            .Where(static pathName => pathName.Contains("textures/particle/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var particles = ValidateMappedPngs(pack, entries, particlePaths, "particles", issues);
        runtimeOmissions += particles.Invalid;
        families.Add(FamilyOf("particle sprites", particles, particleEntries.Length,
            "Flame, smoke and block debris map to emitted behaviours; other particle art is not a missing mechanic."));

        var entityEntries = entries.Where(entry => IsPngUnder(entry, "entity")).ToArray();
        var entityPaths = CreatureSet.All.SelectMany(static kind => kind.Skins)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var entities = ValidateMappedPngs(pack, entries, entityPaths, "entities", issues);
        runtimeOmissions += entities.Invalid;
        families.Add(FamilyOf("owned creatures", entities, entityEntries.Length,
            "Only creature kinds Driftwood owns are applicable; other entity skins are content opportunities."));

        var armourEntries = entries.Where(IsArmour).ToArray();
        var armourPaths = Armour.Materials.SelectMany(static material =>
                Enumerable.Range(0, ArmourSheets.Layers).SelectMany(layer => ArmourSheets.Candidates(material, layer)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var armour = ValidateMappedPngs(pack, entries, armourPaths, "armour", issues);
        runtimeOmissions += armour.Invalid;
        families.Add(FamilyOf("owned armour", armour, armourEntries.Length,
            "Sheets for existing armour tiers are consumed; other equipment is a content opportunity."));

        var models = entries.Count(entry => IsJsonUnder(entry, "models"));
        var states = entries.Count(entry => IsJsonUnder(entry, "blockstates"));
        var currentItems = entries.Count(entry => IsJsonUnder(entry, "items"));
        if (pack.Dialect is PackDialect.Java or PackDialect.JavaLegacy
            && models + states + currentItems > 0)
        {
            var registry = new BlockRegistry();
            StarterBlocks.Register(registry);
            registry.Seal();
            var itemRegistry = StarterItems.Register(registry);
            var java = new JavaPackModels(pack);
            var application = java.Apply(registry, itemRegistry);
            issues.AddRange(application.Issues.Take(32));
            runtimeOmissions += application.Issues.Count;
            var relevant = application.BlocksApplied + application.ItemsApplied;
            families.Add(new Family("Java blockstates and models",
                application.Issues.Count > 0 ? FamilyDisposition.UnsupportedStandardFeature
                : relevant > 0 ? FamilyDisposition.Consumed : FamilyDisposition.NotApplicable,
                models + states + currentItems, relevant,
                "Owned blocks/items use parent inheritance, variables, UV/cull/tint, rotations, variants, multipart and item selection.",
                application.Issues.Count > 0 ? application.Issues.Take(4).ToArray()
                    : Samples(entries.Where(entry => IsJsonUnder(entry, "models")
                                                     || IsJsonUnder(entry, "blockstates")
                                                     || IsJsonUnder(entry, "items")))));
        }
        else
        {
            families.Add(new Family("Java blockstates and models",
                models + states + currentItems == 0 ? FamilyDisposition.SupportedButNotUsed
                    : FamilyDisposition.NotApplicable,
                models + states + currentItems, 0,
                "Java model data is supported for owned blocks and items.", []));
        }

        var companions = entries.Where(entry => PackLayouts.IsCompanionMap(entry)
                                                || RecognizedMaterialCompanion(entry)
                                                || entry.EndsWith(".texture_set.json",
                                                    StringComparison.OrdinalIgnoreCase)).ToArray();
        var companionsUsed = companions.Count(RecognizedMaterialCompanion);
        families.Add(new Family("material companion maps",
            companions.Length == 0 || companionsUsed == 0
                ? FamilyDisposition.SupportedButNotUsed : FamilyDisposition.Consumed,
            companions.Length, companionsUsed,
            "Mapped layers consume Java normal/specular/roughness/emissive suffixes and Bedrock normal/height/MER texture sets.",
            Samples(companions)));

        var environment = entries.Where(entry => IsPngUnder(entry, "environment")
                                                 || IsPngUnder(entry, "weather")).ToArray();
        var environmentUsed = environment.Count(RecognizedEnvironment);
        families.Add(new Family("environment and weather art",
            environment.Length == 0 || environmentUsed == 0
                ? FamilyDisposition.SupportedButNotUsed : FamilyDisposition.Consumed,
            environment.Length, environmentUsed,
            "Sun, moon, clouds, rain and snow use their standard pack paths; unfamiliar dimensions keep safe fallbacks.",
            Samples(environment)));

        var bedrockSpecific = entries.Where(entry =>
            entry.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
            || entry.Contains("/fogs/", StringComparison.OrdinalIgnoreCase)
            || entry.Contains("/particles/", StringComparison.OrdinalIgnoreCase)
               && entry.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray();
        families.Add(new Family("Bedrock-specific resources", FamilyDisposition.NotApplicable,
            bedrockSpecific.Length, 0, "TGA, fog and Bedrock particle definitions remain #54; texture sets are consumed by P9.",
            Samples(bedrockSpecific)));

        var externalEntries = entries.Where(IsExternal).ToArray();
        var hardDependencies = dependencies.Where(dependency =>
            dependency.Type.Equals("required", StringComparison.OrdinalIgnoreCase)
            || dependency.Type.Equals("incompatible", StringComparison.OrdinalIgnoreCase)).ToArray();
        // Catalog dependencies are authoritative when present, but a dependency-free listing is
        // not proof of a vanilla pack. Compare the staged archive's extension definitions with all
        // ordinary resource art, including content Driftwood does not own yet: extension-dominant
        // packs (CEM/CIT/CTM/shader packs in a resource-pack wrapper) need that external runtime for
        // their principal value even if a few familiar entity textures can fall back safely.
        var standardArchiveFiles = coverage.Art + guiEntries.Length + fontEntries.Length + sounds
                                   + particleEntries.Length + entityEntries.Length + armourEntries.Length;
        var principalExternal = externalEntries.Length > 0
            && (standardArchiveFiles == 0
                || externalEntries.Length >= Math.Max(32, standardArchiveFiles));
        families.Add(new Family("loader extensions",
            hardDependencies.Length > 0 || principalExternal
                ? FamilyDisposition.UnsupportedStandardFeature : FamilyDisposition.OptionalExtension,
            externalEntries.Length + dependencies.Count, 0,
            hardDependencies.Length > 0
                ? $"{hardDependencies.Length} required/incompatible catalog dependencies need an external feature."
                : "OptiFine/CIT/CTM/CEM/core-shader extras are visible but never presented as core support.",
            Samples(externalEntries.Concat(hardDependencies.Select(static dependency =>
                dependency.ProjectId.Length > 0 ? dependency.ProjectId : dependency.FileName)))));

        issues.AddRange(pack.Faults.Take(Math.Max(0, 32 - issues.Count)));
        runtimeOmissions += pack.Faults.Count;

        var state = hardDependencies.Length > 0 || principalExternal ? RequiresExternal
            : runtimeOmissions > 0 ? WithOmissions
            : Verified;
        var contentOpportunities = Math.Max(0, coverage.Art - coverage.Covered)
            + Math.Max(0, entityEntries.Length - entities.Present)
            + Math.Max(0, armourEntries.Length - armour.Present)
            + Math.Max(0, guiEntries.Length - gui.Present)
            + Math.Max(0, particleEntries.Length - particles.Present);

        var result = new Summary(
            state,
            coverage.Art,
            Math.Max(0, coverage.Covered - blockArt.Invalid),
            guiEntries.Length,
            gui.Valid,
            font.BitmapGlyphs,
            font.Providers,
            font.Omissions,
            sounds,
            soundSlots,
            particleEntries.Length,
            particles.Valid,
            entityEntries.Length,
            entities.Valid,
            armourEntries.Length,
            armour.Valid,
            models,
            states,
            externalEntries.Length,
            issues.Distinct(StringComparer.Ordinal).Take(64).ToArray(),
            hash,
            Cached: false,
            runtimeOmissions,
            contentOpportunities,
            families,
            dependencies);
        return Save(result, cacheFolder, dependencySignature, useCache);
    }

    private readonly record struct Mapped(int Present, int Valid, int Invalid, IReadOnlyList<string> Samples);

    private static Mapped ValidateMappedPngs(
        TexturePack pack,
        IReadOnlyList<string> entries,
        IReadOnlyList<string> candidates,
        string family,
        List<string> issues)
    {
        var found = entries.Where(entry => candidates.Any(candidate =>
                entry.EndsWith(candidate, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var valid = 0;
        foreach (var entry in found)
        {
            try
            {
                var bytes = pack.TryReadRootBytes(entry, 64 * 1024 * 1024);
                var error = bytes is null ? "could not be read" : "";
                if (bytes is not null && Png.TryReadDimensions(bytes, out var width, out var height, out error)
                    && width <= 16_384 && height <= 262_144)
                {
                    valid++;
                    continue;
                }
                issues.Add($"{family}: {entry}: {(error.Length > 0 ? error : "unsafe dimensions")}");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                issues.Add($"{family}: {entry}: {error.Message}");
            }
        }
        return new Mapped(found.Length, valid, found.Length - valid, Samples(found));
    }

    private static Family FamilyOf(string name, Mapped mapping, int supplied, string note) => new(
        name,
        mapping.Present == 0 ? FamilyDisposition.SupportedButNotUsed
        : mapping.Invalid > 0 ? FamilyDisposition.UnsupportedStandardFeature
        : FamilyDisposition.Consumed,
        supplied,
        mapping.Valid,
        note,
        mapping.Samples);

    private static Summary Empty(
        string state,
        string issue,
        IReadOnlyList<PackLibrary.PackDependency>? dependencies = null) =>
        new(state, 0, 0, 0, 0, 0, 0, [], 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, [issue], Dependencies: dependencies ?? []);

    private static Summary Save(
        Summary summary,
        string folder,
        string dependencySignature,
        bool enabled)
    {
        if (!enabled || summary.Hash.Length != 128) return summary;
        try
        {
            Directory.CreateDirectory(folder);
            var destination = CachePath(folder, summary.Hash);
            var temporary = $"{destination}.{Guid.NewGuid():N}.part";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(
                    new CacheRecord(CacheSchema, summary.Hash, dependencySignature,
                        summary with { Cached = false }), Json));
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return summary;
    }

    private static Summary? ReadCache(string folder, string hash, string dependencySignature)
    {
        try
        {
            var path = CachePath(folder, hash);
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 0 || stream.Length > MaximumCacheBytes) return null;
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() >= 0) return null;
            var record = JsonSerializer.Deserialize<CacheRecord>(bytes, Json);
            return record is not null
                   && record.Schema == CacheSchema
                   && record.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase)
                   && record.DependencySignature == dependencySignature
                ? record.Summary : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string CachePath(string folder, string hash) =>
        System.IO.Path.Combine(folder, $"{hash.ToLowerInvariant()}.json");

    private static string? NormalHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return null;
        hash = hash.Trim().ToLowerInvariant();
        return hash.Length == 128 && hash.All(Uri.IsHexDigit) ? hash : null;
    }

    private static string DependencySignature(IEnumerable<PackLibrary.PackDependency> dependencies)
    {
        var text = new StringBuilder();
        foreach (var dependency in dependencies.OrderBy(static dependency => dependency.Type, StringComparer.Ordinal)
                     .ThenBy(static dependency => dependency.ProjectId, StringComparer.Ordinal)
                     .ThenBy(static dependency => dependency.VersionId, StringComparer.Ordinal))
            text.Append(dependency.Type).Append('\0').Append(dependency.ProjectId).Append('\0')
                .Append(dependency.VersionId).Append('\0').Append(dependency.FileName).Append('\n');
        return text.ToString();
    }

    private static IReadOnlyList<string> Samples(IEnumerable<string> paths) =>
        paths.Where(static path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();

    private static bool IsUnsafePath(string path) => path.StartsWith('/') || path.StartsWith('\\')
        || path.Contains(':') || path.Split('/', '\\').Any(part => part is "." or "..");

    private static bool IsArmour(string entry) => entry.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        && (entry.Contains("/textures/models/armor/", StringComparison.OrdinalIgnoreCase)
            || entry.StartsWith("textures/models/armor/", StringComparison.OrdinalIgnoreCase)
            || entry.Contains("/armor/", StringComparison.OrdinalIgnoreCase)
            || entry.StartsWith("armor/", StringComparison.OrdinalIgnoreCase));

    private static bool IsPngUnder(string path, string folder) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        && (path.Contains($"/textures/{folder}/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith($"textures/{folder}/", StringComparison.OrdinalIgnoreCase));

    private static bool IsJsonUnder(string path, string folder) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        && (path.Contains($"/{folder}/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith($"{folder}/", StringComparison.OrdinalIgnoreCase));

    private static readonly string[] MaterialSuffixes =
    [
        "_heightmap", "_roughness", "_metalness", "_specular", "_emissive",
        "_normal", "_metallic", "_mers", "_height", "_rough", "_metal",
        "_spec", "_emit", "_norm", "_bump", "_mer", "_n", "_s", "_r", "_m", "_e",
    ];

    private static bool RecognizedMaterialCompanion(string entry)
    {
        entry = entry.Replace('\\', '/');
        string color;
        const string textureSet = ".texture_set.json";
        if (entry.EndsWith(textureSet, StringComparison.OrdinalIgnoreCase))
        {
            color = entry[..^textureSet.Length] + ".png";
        }
        else
        {
            var dot = entry.LastIndexOf('.');
            if (dot < 0 || !entry[dot..].Equals(".png", StringComparison.OrdinalIgnoreCase)) return false;
            var stem = entry[..dot];
            var suffix = MaterialSuffixes.FirstOrDefault(candidate =>
                stem.EndsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (suffix is null) return false;
            color = stem[..^suffix.Length] + ".png";
        }

        foreach (var row in BlockTextureSet.Layers)
        {
            if (row.PackPath.Length > 0 && color.EndsWith(row.PackPath, StringComparison.OrdinalIgnoreCase))
                return true;
            if (row.PackPathAlt.Length > 0 && color.EndsWith(row.PackPathAlt, StringComparison.OrdinalIgnoreCase))
                return true;
            if (row.PackPath.Length > 0 && PackLayouts.Legacy(row.PackPath).Any(candidate =>
                    color.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    private static bool RecognizedEnvironment(string entry)
    {
        entry = entry.Replace('\\', '/');
        return entry.EndsWith("/textures/environment/sun.png", StringComparison.OrdinalIgnoreCase)
               || entry.EndsWith("/textures/environment/moon.png", StringComparison.OrdinalIgnoreCase)
               || entry.EndsWith("/textures/environment/moon_phases.png", StringComparison.OrdinalIgnoreCase)
               || entry.EndsWith("/textures/environment/clouds.png", StringComparison.OrdinalIgnoreCase)
               || entry.EndsWith("/textures/environment/rain.png", StringComparison.OrdinalIgnoreCase)
               || entry.EndsWith("/textures/environment/snow.png", StringComparison.OrdinalIgnoreCase)
               || entry.EndsWith("/textures/weather/rain.png", StringComparison.OrdinalIgnoreCase)
               || entry.EndsWith("/textures/weather/snow.png", StringComparison.OrdinalIgnoreCase)
               || entry.StartsWith("textures/environment/", StringComparison.OrdinalIgnoreCase)
                  && entry[(entry.LastIndexOf('/') + 1)..] is "sun.png" or "moon.png" or "moon_phases.png"
                      or "clouds.png" or "rain.png" or "snow.png";
    }

    private static bool IsExternal(string path) =>
        path.Contains("/optifine/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("optifine/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/mcpatcher/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("mcpatcher/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/shaders/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/iris/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/polytone/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/citresewn/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/cem/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/cit/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/ctm/", StringComparison.OrdinalIgnoreCase);
}
