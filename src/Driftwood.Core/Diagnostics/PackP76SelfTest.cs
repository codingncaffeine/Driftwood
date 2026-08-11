using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Driftwood.Core.Audio;
using Driftwood.Core.Blocks;
using Driftwood.Core.Items;
using Driftwood.Core.Textures;

namespace Driftwood.Core.Diagnostics;

/// <summary>Offline, synthetic controls for the collection-scale P7.6 pack pipeline.</summary>
public static class PackP76SelfTest
{
    public static List<string> Run(out string detail)
    {
        var faults = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "driftwood-p76-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var valid = ValidPack();
            Shelf(root, valid, faults);
            Models(root, valid, faults);
            Sounds(root, valid, faults);
            CompatibilityAndMatrix(root, valid, faults);
            Provider(root, valid, faults);
        }
        catch (Exception error)
        {
            faults.Add($"P7.6 fixture stopped in {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        detail = "100-card shelf/search/sidecars and exact recovery; four compatibility outcomes and hash cache; "
                 + "sounds.json weights/references/cycles, decode fallback and large mixed packs; sparse Java vanilla fallbacks plus parents/UV/cull/tint/rotation/variants/multipart/items; "
                 + "Modrinth paging/detail/gallery/icon cache/429/410/hosts/length/hash/cancel/install all exercised offline";
        return faults;
    }

    private static void Shelf(string root, byte[] valid, List<string> faults)
    {
        var shelf = Path.Combine(root, "hundred-pack-shelf");
        Directory.CreateDirectory(shelf);
        for (var i = 0; i < 96; i++)
            File.WriteAllBytes(Path.Combine(shelf, $"pack-{i:000}.zip"), valid);

        File.WriteAllBytes(Path.Combine(shelf, "duplicate.zip"), valid);
        var duplicateFolder = Path.Combine(shelf, "duplicate");
        Directory.CreateDirectory(Path.Combine(duplicateFolder, "assets", "minecraft", "textures", "block"));
        File.WriteAllText(Path.Combine(duplicateFolder, "pack.mcmeta"), PackMeta("duplicate folder"));
        File.WriteAllBytes(Path.Combine(duplicateFolder, "assets", "minecraft", "textures", "block", "stone.png"),
            Pixel(90));
        File.WriteAllText(Path.Combine(shelf, "unreadable.zip"), "not a zip");

        var source = Path.Combine(root, "needle.zip");
        File.WriteAllBytes(source, valid);
        var provenance = new PackLibrary.Provenance(
            "Modrinth", "Needle1", "Version1", "1.0", "Ada", "https://modrinth.com/resourcepack/needle",
            "CC0-1.0", Convert.ToHexStringLower(SHA512.HashData(valid)), Title: "Needle Pack",
            Description: "the searchable card");
        if (PackLibrary.Install(source, out var why, shelf, provenance) is null)
            faults.Add($"the hundred-pack shelf refused its provenance card: {why}");

        var first = PackLibrary.List(shelf);
        var second = PackLibrary.List(shelf);
        if (first.Count != 100) faults.Add($"the synthetic shelf listed {first.Count} cards, not 100");
        if (first.Count(entry => !entry.Readable) != 1)
            faults.Add($"the shelf exposed {first.Count(entry => !entry.Readable)} unreadable cards, not 1");
        if (!first.Select(entry => entry.Path).SequenceEqual(second.Select(entry => entry.Path),
                StringComparer.OrdinalIgnoreCase))
            faults.Add("the same hundred-pack shelf changed order between reads");
        if (Directory.EnumerateFiles(shelf, "*.driftwood.json").Count() < 98)
            faults.Add("missing sidecars were not regenerated as disposable card metadata");

        var search = PackLibrary.Query(first, "Ada Modrinth");
        if (search.Count != 1 || search[0].DisplayTitle != "Needle Pack")
            faults.Add($"author/source search returned {search.Count} cards instead of Needle Pack");
        var worn = PackLibrary.Query(first, sort: PackLibrary.SortOrder.Source, worn: "pack-042");
        if (worn.Count == 0 || worn[0].Name != "pack-042")
            faults.Add("the worn pack was not pinned ahead of source sorting");

        var duplicates = first.Where(entry => entry.Name == "duplicate").ToArray();
        if (duplicates.Length != 2) faults.Add($"duplicate display names produced {duplicates.Length} cards, not 2");
        else
        {
            if (!PackLibrary.RemovePath(duplicates[0].Path, shelf))
                faults.Add("one exact duplicate card could not be removed");
            if (PackLibrary.List(shelf).Count(entry => entry.Name == "duplicate") != 1)
                faults.Add("exact duplicate removal removed both cards or neither");
        }
    }

    private static void Models(string root, byte[] valid, List<string> faults)
    {
        var path = Path.Combine(root, "java-models.zip");
        File.WriteAllBytes(path, valid);
        using (var pack = TexturePack.Open(path, out var why))
        {
            if (pack is null)
            {
                faults.Add($"the valid Java model fixture did not open: {why}");
                return;
            }

            var java = new JavaPackModels(pack);
            var stone = java.ResolveBlock(new JavaPackModels.BlockRequest("stone",
                new Dictionary<string, string> { ["snowy"] = "false" }));
            if (!stone.Found || stone.Model is null || stone.Model.Elements.Count < 2)
                faults.Add("weighted variant plus matching multipart did not resolve into both model parts");
            if (java.ResolveBlock(new JavaPackModels.BlockRequest("stone", new Dictionary<string, string>()))
                    .Issues.Count > 0)
                faults.Add("a standard inherited cube model reported an omission");

            var detail = java.ResolveBlock(new JavaPackModels.BlockRequest("detail",
                new Dictionary<string, string>()));
            var element = detail.Model?.Elements.FirstOrDefault();
            if (element is null || element.RotationAngle != 22.5f || element.Shade
                || element.AmbientOcclusion || !element.Rescale)
                faults.Add("element rotation/rescale/shade/ambient-occlusion model data did not survive resolution");
            var face = detail.Model?.Elements.SelectMany(candidate => candidate.Faces)
                .FirstOrDefault(candidate => candidate?.Uv is not null);
            if (face is null || face.CullFace < 0 || !face.Tinted || face.Uv is null || face.Rotation != 1)
                faults.Add("face UV/cullface/tint/rotation model data did not survive resolution");

            var item = java.ResolveItem("iron_pickaxe", new Dictionary<string, float> { ["damaged"] = 1f });
            if (!item.Found || item.FlatLayer is null)
                faults.Add("a current condition item definition did not reach its inherited generated model");

            var registry = new BlockRegistry();
            StarterBlocks.Register(registry);
            registry.Seal();
            var items = StarterItems.Register(registry);
            var applied = java.Apply(registry, items);
            if (applied.BlocksApplied == 0 || applied.ItemsApplied == 0 || applied.WeightedChoices == 0)
                faults.Add($"Java application reported {applied.BlocksApplied} blocks, {applied.ItemsApplied} items and "
                           + $"{applied.WeightedChoices} weighted choices");
        }

        var sparsePath = Path.Combine(root, "java-sparse-vanilla.zip");
        File.WriteAllBytes(sparsePath, Pack(new Dictionary<string, byte[]>
        {
            ["assets/minecraft/textures/block/stone.png"] = Pixel(124),
            ["assets/minecraft/textures/block/dirt.png"] = Pixel(123),
            ["assets/minecraft/textures/block/furnace_top.png"] = Pixel(125),
            ["assets/minecraft/textures/block/furnace_side.png"] = Pixel(126),
            ["assets/minecraft/textures/block/furnace_front.png"] = Pixel(127),
            ["assets/minecraft/textures/item/iron_pickaxe.png"] = Pixel(128),
            ["assets/minecraft/blockstates/stone.json"] = Utf8("""
                {"variants":{"age=0":{"model":"minecraft:block/stone"}}}
                """),
            ["assets/minecraft/blockstates/furnace.json"] = Utf8("""
                {"variants":{"facing=east,lit=false":{"model":"minecraft:block/furnace","y":90}}}
                """),
            ["assets/minecraft/blockstates/dirt.json"] = Utf8("""
                {"variants":{"":{"model":"minecraft:block/dirt_without_cullface"}}}
                """),
            ["assets/minecraft/models/block/dirt_without_cullface.json"] = Utf8("""
                {"textures":{"all":"minecraft:block/dirt"},"elements":[
                  {"from":[0,0,0],"to":[16,16,16],"faces":{
                    "north":{"texture":"#all"},"south":{"texture":"#all"},
                    "east":{"texture":"#all"},"west":{"texture":"#all"},
                    "up":{"texture":"#all"},"down":{"texture":"#all"}}}]}
                """),
            ["assets/minecraft/models/block/furnace.json"] = Utf8("""
                {"parent":"minecraft:block/orientable","textures":{"top":"minecraft:block/furnace_top",
                 "side":"minecraft:block/furnace_side","front":"minecraft:block/furnace_front"}}
                """),
            ["assets/minecraft/items/iron_pickaxe.json"] = Utf8("""
                {"model":{"type":"minecraft:model","model":"minecraft:item/iron_pickaxe"}}
                """),
        }));
        using (var sparsePack = TexturePack.Open(sparsePath, out var sparseWhy))
        {
            if (sparsePack is null) faults.Add($"the sparse vanilla fixture did not open: {sparseWhy}");
            else
            {
                var sparse = new JavaPackModels(sparsePack);
                var inheritedStone = sparse.ResolveBlock(new JavaPackModels.BlockRequest(
                    "stone", new Dictionary<string, string>()));
                var inheritedFurnace = sparse.ResolveBlock(new JavaPackModels.BlockRequest(
                    "furnace", new Dictionary<string, string>
                    {
                        ["facing"] = "east", ["lit"] = "false",
                    }));
                var uncullableDirt = sparse.ResolveBlock(new JavaPackModels.BlockRequest(
                    "dirt", new Dictionary<string, string>()));
                var inheritedItem = sparse.ResolveItem("iron_pickaxe");
                if (inheritedStone.Model?.Quads.Length != 6 || inheritedStone.Issues.Count > 0)
                    faults.Add("a sparse blockstate could not inherit its mapped vanilla cube");
                if (inheritedFurnace.Model?.Quads.Length != 6 || inheritedFurnace.Issues.Count > 0)
                    faults.Add("a sparse model could not inherit vanilla's orientable template");
                if (uncullableDirt.Model is not { OccludesCell: true })
                    faults.Add("a complete cube whose faces omit cullface was treated as visually open");
                if (inheritedItem.FlatLayer is null || inheritedItem.Issues.Count > 0)
                    faults.Add("a current item could not inherit its mapped vanilla generated model");
            }
        }

        var cyclePath = Path.Combine(root, "java-cycle.zip");
        File.WriteAllBytes(cyclePath, Pack(new Dictionary<string, byte[]>
        {
            ["assets/minecraft/textures/block/stone.png"] = Pixel(110),
            ["assets/minecraft/blockstates/stone.json"] = Utf8("""
                {"variants":{"":{"model":"minecraft:block/cycle_a"}}}
                """),
            ["assets/minecraft/models/block/cycle_a.json"] = Utf8("{" +
                "\"parent\":\"minecraft:block/cycle_b\"}"),
            ["assets/minecraft/models/block/cycle_b.json"] = Utf8("{" +
                "\"parent\":\"minecraft:block/cycle_a\"}"),
        }));
        using var cyclePack = TexturePack.Open(cyclePath, out _);
        var cycle = cyclePack is null ? null : new JavaPackModels(cyclePack).ResolveBlock(
            new JavaPackModels.BlockRequest("stone", new Dictionary<string, string>()));
        if (cycle is null || !cycle.Issues.Any(issue => issue.Contains("cycle", StringComparison.OrdinalIgnoreCase)))
            faults.Add("a Java model parent cycle was not bounded and named");

        var rotationPath = Path.Combine(root, "java-bad-face-rotation.zip");
        File.WriteAllBytes(rotationPath, Pack(new Dictionary<string, byte[]>
        {
            ["assets/minecraft/textures/block/stone.png"] = Pixel(120),
            ["assets/minecraft/blockstates/stone.json"] = Utf8("{" +
                "\"variants\":{\"\":{\"model\":\"minecraft:block/bad\"}}}"),
            ["assets/minecraft/models/block/bad.json"] = Utf8("""
                {"textures":{"all":"minecraft:block/stone"},"elements":[
                  {"from":[0,0,0],"to":[16,16,16],"faces":{"north":{"texture":"#all","rotation":45}}}
                ]}
                """),
        }));
        using var rotationPack = TexturePack.Open(rotationPath, out _);
        var rotation = rotationPack is null ? null : new JavaPackModels(rotationPack).ResolveBlock(
            new JavaPackModels.BlockRequest("stone", new Dictionary<string, string>()));
        if (rotation is null || !rotation.Issues.Any(issue => issue.Contains("0, 90, 180 or 270")))
            faults.Add("a 45-degree face rotation was accepted as zero degrees");
    }

    private static void Sounds(string root, byte[] valid, List<string> faults)
    {
        var path = Path.Combine(root, "sounds-json.zip");
        File.WriteAllBytes(path, valid);
        var inspected = SoundPackArchive.Inspect(path, requireSounds: true);
        var target = MaterialSounds.For(SoundMaterial.Stone, SoundEvent.Break)[0];
        if (inspected.Variants is null || !inspected.Variants.TryGetValue(target, out var choices)
            || choices.Count != 2 || choices.Max(choice => choice.Weight) != 6
            || choices.Min(choice => choice.Weight) != 1)
            faults.Add("sounds.json event references did not multiply and preserve weights (wanted 6 and 1)");
        if (inspected.Covered < MaterialSounds.For(SoundMaterial.Stone, SoundEvent.Break).Count)
            faults.Add("a standard block sound event did not cover Driftwood's numbered fallback slots");

        var largeMixedPath = Path.Combine(root, "large-mostly-visual.zip");
        File.WriteAllBytes(largeMixedPath, LargeMostlyVisualPack());
        var largeMixed = SoundPackArchive.Inspect(largeMixedPath, requireSounds: true);
        if (largeMixed.Clips != 1)
            faults.Add($"a >8,192-file visual pack exposed {largeMixed.Clips} usable sounds, not 1");

        var fallbackPath = Path.Combine(root, "broken-higher-audio.zip");
        File.WriteAllBytes(fallbackPath, Pack(new Dictionary<string, byte[]>
        {
            ["assets/minecraft/sounds/frog_bad.ogg"] = [0x4f, 0x67, 0x67, 0x53, 0],
            ["assets/minecraft/sounds.json"] = Utf8("""
                {"entity.frog.ambient":{"sounds":["frog_bad"]}}
                """),
        }));
        var fallback = new SoundLibrary(Path.Combine(root, "no-local-audio"),
            texturePackPath: fallbackPath, soundPackPath: null);
        if (fallback.Load("animals/frog") is null
            || !fallback.Faults.Any(fault => fault.Contains("frog_bad", StringComparison.OrdinalIgnoreCase)))
            faults.Add("a malformed higher-priority event did not fall through to Driftwood's recording");

        var cyclePath = Path.Combine(root, "sounds-cycle.zip");
        File.WriteAllBytes(cyclePath, Pack(new Dictionary<string, byte[]>
        {
            ["assets/minecraft/sounds/a.ogg"] = [0x4f, 0x67, 0x67, 0x53, 0],
            ["assets/minecraft/sounds.json"] = Utf8("""
                {"a":{"sounds":[{"name":"b","type":"event"}]},
                 "b":{"sounds":[{"name":"a","type":"event"}]}}
                """),
        }));
        try
        {
            SoundPackArchive.Inspect(cyclePath);
            faults.Add("a sounds.json event cycle was accepted");
        }
        catch (InvalidDataException error)
        {
            if (!error.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase))
                faults.Add($"a sounds.json cycle was refused as '{error.Message}'");
        }
    }

    private static void CompatibilityAndMatrix(string root, byte[] valid, List<string> faults)
    {
        var corpus = Path.Combine(root, "matrix");
        var cache = Path.Combine(root, "compatibility-cache");
        Directory.CreateDirectory(corpus);
        var verifiedPath = Path.Combine(corpus, "verified.zip");
        File.WriteAllBytes(verifiedPath, valid);

        var omissionPath = Path.Combine(corpus, "omission.zip");
        File.WriteAllBytes(omissionPath, Pack(new Dictionary<string, byte[]>
        {
            ["assets/minecraft/textures/block/stone.png"] = [1, 2, 3, 4],
        }));
        var externalPath = Path.Combine(corpus, "external.zip");
        File.WriteAllBytes(externalPath, Pack(new Dictionary<string, byte[]>
        {
            ["assets/minecraft/optifine/cit/principal.properties"] = Utf8("type=item"),
        }));
        var invalidPath = Path.Combine(corpus, "invalid.zip");
        File.WriteAllText(invalidPath, "not a zip");

        var verified = PackCompatibility.Inspect(verifiedPath, cacheFolder: cache);
        var cached = PackCompatibility.Inspect(verifiedPath, cacheFolder: cache);
        var omitted = PackCompatibility.Inspect(omissionPath, cacheFolder: cache);
        var external = PackCompatibility.Inspect(externalPath, cacheFolder: cache);
        var invalid = PackCompatibility.Inspect(invalidPath, cacheFolder: cache);
        using var sparseJava = TexturePack.Open(externalPath);
        if (sparseJava?.Dialect != PackDialect.Java)
            faults.Add("an entity/extension-only pack with pack.mcmeta was not recognised as Java");
        if (verified.State != PackCompatibility.Verified)
            faults.Add($"the valid standard fixture classified as {verified.State}: {verified.Issues.FirstOrDefault()}");
        if (!cached.Cached || cached.Hash != verified.Hash)
            faults.Add("a second exact compatibility inspection did not use its SHA-512 cache");
        if (omitted.State != PackCompatibility.WithOmissions)
            faults.Add($"the malformed mapped layer classified as {omitted.State}, not WORKS WITH OMISSIONS");
        if (external.State != PackCompatibility.RequiresExternal)
            faults.Add($"an extension-principal pack classified as {external.State}, not REQUIRES EXTERNAL FEATURE");
        if (invalid.State != PackCompatibility.Invalid)
            faults.Add($"a broken archive classified as {invalid.State}, not INVALID");

        var matrix = PackMatrix.Build(corpus, cache);
        if (!matrix.Passed || matrix.Packs != 4 || matrix.Invalid != 1
            || !matrix.Report.Contains(PackCompatibility.Verified)
            || !matrix.Report.Contains("#54") || !matrix.Report.Contains("#42"))
            faults.Add("the four-pack matrix did not report outcomes, prevalence and roadmap routing");
    }

    private static void Provider(string root, byte[] archive, List<string> faults)
    {
        var cache = Path.Combine(root, "provider-cache");
        var staging = Path.Combine(root, "provider-staging");
        var shelf = Path.Combine(root, "provider-shelf");
        var hash = Convert.ToHexStringLower(SHA512.HashData(archive));
        var apiSearch = new Uri("https://api.modrinth.com/v2/search");
        var catalog = Utf8("""
            {"hits":[{"project_id":"Pack123","slug":"fixture-pack","title":"Fixture Pack",
              "author":"Ada","license":"CC0-1.0","downloads":123,"description":"fixture",
              "project_type":"resourcepack","categories":["16x"],"versions":["1.21.8"],
              "latest_version":"Version2","date_created":"2026-01-01T00:00:00Z",
              "date_modified":"2026-08-01T00:00:00Z"}],"offset":0,"total_hits":25}
            """);
        var empty = Utf8("{" + "\"hits\":[],\"offset\":0,\"total_hits\":0}");
        var versions = JsonSerializer.SerializeToUtf8Bytes(new object[]
        {
            VersionJson("Version1", "1.0", "beta", "2026-07-01T00:00:00Z", archive, hash),
            VersionJson("Version2", "2.0", "release", "2026-08-01T00:00:00Z", archive, hash,
                new[] { new { dependency_type = "optional", project_id = "Optional1" } }),
        });

        try
        {
            var transport = new FakeTransport(Json(HttpStatusCode.OK, catalog, apiSearch));
            var provider = new ModrinthResourcePackProvider(transport, cache);
            var page = provider.SearchAsync("fixture", ResourcePackSort.Downloads, "16x", "1.21.8")
                .GetAwaiter().GetResult();
            if (page.Packs.Count != 1 || !page.HasNext || page.Total != 25)
                faults.Add("resource-pack search did not preserve lazy paging totals");
            var query = transport.Requests.Single().Query;
            if (!query.Contains("project_type", StringComparison.OrdinalIgnoreCase)
                || !query.Contains("downloads", StringComparison.OrdinalIgnoreCase))
                faults.Add("resource-pack search omitted its resourcepack facet or requested sort");

            var offline = new ModrinthResourcePackProvider(new FakeTransport { Offline = true }, cache)
                .SearchAsync("fixture", ResourcePackSort.Downloads, "16x", "1.21.8", offlineOnly: true)
                .GetAwaiter().GetResult();
            if (!offline.Cached || offline.Packs.Count != 1)
                faults.Add("an offline search did not return the exact cached catalog page");
        }
        catch (Exception error) { faults.Add($"good/cached catalog path threw {error.GetType().Name}: {error.Message}"); }

        try
        {
            var page = new ModrinthResourcePackProvider(
                    new FakeTransport(Json(HttpStatusCode.OK, empty, apiSearch)), Path.Combine(root, "empty-cache"))
                .SearchAsync("").GetAwaiter().GetResult();
            if (page.Packs.Count != 0 || page.Total != 0) faults.Add("an empty Modrinth result was not first-class");
        }
        catch (Exception error) { faults.Add($"empty catalog path threw {error.GetType().Name}: {error.Message}"); }

        var versionUri = new Uri("https://api.modrinth.com/v2/project/Pack123/version");
        try
        {
            var provider = new ModrinthResourcePackProvider(
                new FakeTransport(Json(HttpStatusCode.OK, versions, versionUri)), Path.Combine(root, "version-cache"));
            var parsed = provider.VersionsAsync("Pack123").GetAwaiter().GetResult();
            var chosen = ModrinthResourcePackProvider.ChooseDefault(parsed, "1.21.8");
            if (chosen.Id != "Version2" || chosen.Dependencies.Count != 1)
                faults.Add("newest stable version choice lost its dependency metadata");
        }
        catch (Exception error) { faults.Add($"version selection threw {error.GetType().Name}: {error.Message}"); }

        try
        {
            var projectUri = new Uri("https://api.modrinth.com/v2/project/Pack123");
            var iconUri = new Uri("https://cdn.modrinth.com/data/Pack123/icon.png");
            var galleryUri = new Uri("https://cdn.modrinth.com/data/Pack123/images/gallery.png");
            var projectJson = Utf8("""
                {"id":"Pack123","slug":"fixture-pack","title":"Fixture Pack","team":"Team123",
                 "license":{"id":"CC0-1.0"},"downloads":123,"description":"project detail",
                 "icon_url":"https://cdn.modrinth.com/data/Pack123/icon_96.webp",
                 "raw_icon_url":"https://cdn.modrinth.com/data/Pack123/icon.png","color":12345,
                 "categories":["16x"],"game_versions":["1.21.8"],
                 "published":"2026-01-01T00:00:00Z","updated":"2026-08-01T00:00:00Z",
                 "gallery":[{"url":"https://cdn.modrinth.com/data/Pack123/images/gallery_350.webp",
                             "raw_url":"https://cdn.modrinth.com/data/Pack123/images/gallery.png"}]}
                """);
            var icon = Pixel(144);
            var gallery = Pixel(188);
            var detailCache = Path.Combine(root, "project-cache");
            var provider = new ModrinthResourcePackProvider(new FakeTransport(
                Json(HttpStatusCode.OK, projectJson, projectUri),
                new SoundPackHttpResult(HttpStatusCode.OK, "image/png", icon, iconUri),
                new SoundPackHttpResult(HttpStatusCode.OK, "image/png", gallery, galleryUri)), detailCache);
            var projectDetail = provider.ProjectAsync("Pack123").GetAwaiter().GetResult();
            var downloadedIcon = provider.IconAsync(projectDetail).GetAwaiter().GetResult();
            var downloadedGallery = provider.GalleryImageAsync(projectDetail).GetAwaiter().GetResult();
            var cachedIcon = new ModrinthResourcePackProvider(new FakeTransport { Offline = true }, detailCache)
                .IconAsync(projectDetail, offlineOnly: true).GetAwaiter().GetResult();
            var cachedGallery = new ModrinthResourcePackProvider(new FakeTransport { Offline = true }, detailCache)
                .GalleryImageAsync(projectDetail, offlineOnly: true).GetAwaiter().GetResult();
            if (projectDetail.GalleryImages.Count != 1 || projectDetail.GalleryImages[0] != galleryUri
                || !downloadedIcon.AsSpan().SequenceEqual(icon) || !cachedIcon.AsSpan().SequenceEqual(icon)
                || !downloadedGallery.AsSpan().SequenceEqual(gallery)
                || !cachedGallery.AsSpan().SequenceEqual(gallery))
                faults.Add("project detail lost its safe cached icon or gallery bytes");
        }
        catch (Exception error) { faults.Add($"project detail/icon path threw {error.GetType().Name}: {error.Message}"); }

        ExpectProviderFailure(faults, ResourcePackProviderFailure.RateLimited, TimeSpan.FromSeconds(9), () =>
            new ModrinthResourcePackProvider(new FakeTransport(new SoundPackHttpResult(
                    HttpStatusCode.TooManyRequests, "application/json", [], apiSearch, TimeSpan.FromSeconds(9))),
                Path.Combine(root, "rate-cache")).SearchAsync("").GetAwaiter().GetResult());
        ExpectProviderFailure(faults, ResourcePackProviderFailure.ApiRetired, null, () =>
            new ModrinthResourcePackProvider(new FakeTransport(Json(HttpStatusCode.Gone, [], apiSearch)),
                Path.Combine(root, "gone-cache")).SearchAsync("").GetAwaiter().GetResult());
        ExpectProviderFailure(faults, ResourcePackProviderFailure.MalformedMetadata, null, () =>
            new ModrinthResourcePackProvider(new FakeTransport(Json(HttpStatusCode.OK, Utf8("{"), apiSearch)),
                Path.Combine(root, "bad-json-cache")).SearchAsync("").GetAwaiter().GetResult());
        ExpectProviderFailure(faults, ResourcePackProviderFailure.HostRefused, null, () =>
            new ModrinthResourcePackProvider(new FakeTransport(Json(HttpStatusCode.OK, catalog,
                    new Uri("https://example.invalid/search"))), Path.Combine(root, "host-cache"))
                .SearchAsync("").GetAwaiter().GetResult());

        var project = new RemoteResourcePack("Pack123", "fixture-pack", "Fixture Pack", "Ada", "CC0-1.0",
            123, "fixture", new Uri("https://modrinth.com/resourcepack/fixture-pack"), null, null,
            ["16x"], ["1.21.8"], DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);
        var file = new RemoteResourcePackFile("fixture.zip", new Uri("https://cdn.modrinth.com/data/file.zip"),
            archive.LongLength, hash, "", true);
        var version = new RemoteResourcePackVersion("Version2", "Pack123", "2.0", "2.0", "release",
            DateTimeOffset.UtcNow, ["1.21.8"], [], [file]);
        try
        {
            var progress = new CaptureProgress();
            var provider = new ModrinthResourcePackProvider(
                new FakeTransport(new SoundPackHttpResult(HttpStatusCode.OK, "application/zip", archive, file.Uri)),
                Path.Combine(root, "download-cache"));
            using var downloaded = provider.DownloadAsync(project, version, progress, staging,
                Path.Combine(root, "download-compat")).GetAwaiter().GetResult();
            if (progress.Values.Count < 2 || progress.Values[^1].BytesReceived != archive.LongLength)
                faults.Add("the resource-pack download did not report determinate byte progress");
            var installed = downloaded.Install(out var why, shelf);
            if (installed is null || installed.Value.ProjectId != project.Id || installed.Value.Sha512 != hash)
                faults.Add($"the verified Modrinth archive did not land with provenance: {why}");
            else AtomicRecovery(root, shelf, installed.Value, faults);
        }
        catch (Exception error) { faults.Add($"good resource-pack download threw {error.GetType().Name}: {error.Message}"); }

        DownloadControls(root, project, version, archive, hash, staging, faults);
    }

    private static void AtomicRecovery(
        string root, string shelf, PackLibrary.Entry installed, List<string> faults)
    {
        var before = File.ReadAllBytes(installed.Path);
        var update = Path.Combine(root, "replacement.zip");
        File.WriteAllBytes(update, Pack(new Dictionary<string, byte[]>
        {
            ["assets/minecraft/textures/block/stone.png"] = Pixel(201),
        }, "replacement"));

        var sidecar = PackLibrary.MetadataPath(installed.Path);
        if (File.Exists(sidecar)) File.Delete(sidecar);
        Directory.CreateDirectory(sidecar); // forces metadata commit to fail after the new archive lands.
        var replaced = PackLibrary.Install(update, out _, shelf, installName: Path.GetFileName(installed.Path));
        if (replaced is not null) faults.Add("a replacement whose metadata destination was a directory succeeded");
        if (!File.Exists(installed.Path) || !File.ReadAllBytes(installed.Path).AsSpan().SequenceEqual(before))
            faults.Add("a failed atomic replacement did not restore the old archive bytes");
        Directory.Delete(sidecar);

        if (PackLibrary.Install(update, out var why, shelf, installName: Path.GetFileName(installed.Path)) is null)
            faults.Add($"a replacement could not succeed after the fault was cleared: {why}");
        if (Directory.EnumerateFileSystemEntries(shelf).Any(path =>
                Path.GetFileName(path).Contains(".backup", StringComparison.Ordinal)
                || Path.GetFileName(path).Contains(".staging", StringComparison.Ordinal)))
            faults.Add("a successful replacement left staging or backup debris on the shelf");
    }

    private static void DownloadControls(
        string root,
        RemoteResourcePack project,
        RemoteResourcePackVersion goodVersion,
        byte[] archive,
        string hash,
        string staging,
        List<string> faults)
    {
        var file = goodVersion.PrimaryFile!;
        ExpectProviderFailure(faults, ResourcePackProviderFailure.DownloadFailed, null, () =>
        {
            var shortBody = archive[..^1];
            var provider = DownloadProvider(root, "short", shortBody, file.Uri);
            provider.DownloadAsync(project, goodVersion, stagingFolder: staging).GetAwaiter().GetResult();
        });

        ExpectProviderFailure(faults, ResourcePackProviderFailure.DownloadFailed, null, () =>
        {
            var wrong = file with { Sha512 = new string('0', 128) };
            var version = goodVersion with { Files = [wrong] };
            DownloadProvider(root, "hash", archive, file.Uri).DownloadAsync(
                project, version, stagingFolder: staging).GetAwaiter().GetResult();
        });

        var invalid = Utf8("not a resource pack");
        ExpectProviderFailure(faults, ResourcePackProviderFailure.DownloadFailed, null, () =>
        {
            var invalidFile = file with
            {
                Size = invalid.LongLength,
                Sha512 = Convert.ToHexStringLower(SHA512.HashData(invalid)),
            };
            DownloadProvider(root, "invalid", invalid, file.Uri).DownloadAsync(
                project, goodVersion with { Files = [invalidFile] }, stagingFolder: staging)
                .GetAwaiter().GetResult();
        });

        ExpectProviderFailure(faults, ResourcePackProviderFailure.DownloadFailed, null, () =>
        {
            var oversized = file with { Size = ModrinthResourcePackProvider.MaximumArchiveBytes + 1 };
            DownloadProvider(root, "oversized", archive, file.Uri).DownloadAsync(
                project, goodVersion with { Files = [oversized] }, stagingFolder: staging)
                .GetAwaiter().GetResult();
        });

        ExpectProviderFailure(faults, ResourcePackProviderFailure.HostRefused, null, () =>
        {
            var hostile = file with { Uri = new Uri("https://example.invalid/file.zip") };
            DownloadProvider(root, "hostile", archive, file.Uri).DownloadAsync(
                project, goodVersion with { Files = [hostile] }, stagingFolder: staging)
                .GetAwaiter().GetResult();
        });

        try
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            DownloadProvider(root, "cancel", archive, file.Uri).DownloadAsync(
                project, goodVersion, stagingFolder: staging, cancellationToken: cancelled.Token)
                .GetAwaiter().GetResult();
            faults.Add("a cancelled resource-pack download completed");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { faults.Add($"cancel surfaced as {error.GetType().Name}, not cancellation"); }

        if (Directory.Exists(staging) && Directory.EnumerateFiles(staging, "*.part").Any())
            faults.Add("a failed/cancelled download left a .part file behind");
    }

    private static ModrinthResourcePackProvider DownloadProvider(
        string root, string name, byte[] bytes, Uri finalUri) => new(
        new FakeTransport(new SoundPackHttpResult(HttpStatusCode.OK, "application/zip", bytes, finalUri)),
        Path.Combine(root, name + "-cache"));

    private static void ExpectProviderFailure(
        List<string> faults,
        ResourcePackProviderFailure wanted,
        TimeSpan? retryAfter,
        Action action)
    {
        try
        {
            action();
            faults.Add($"a {wanted} provider control succeeded");
        }
        catch (ResourcePackProviderException error)
        {
            if (error.Failure != wanted)
                faults.Add($"a {wanted} control surfaced as {error.Failure}: {error.Message}");
            if (retryAfter is { } wantedRetry && error.RetryAfter != wantedRetry)
                faults.Add($"Retry-After changed from {wantedRetry.TotalSeconds:F0}s to "
                           + $"{error.RetryAfter?.TotalSeconds:F0}s");
        }
        catch (Exception error)
        {
            faults.Add($"a {wanted} control threw {error.GetType().Name}: {error.Message}");
        }
    }

    private static object VersionJson(
        string id,
        string number,
        string type,
        string published,
        byte[] archive,
        string hash,
        object[]? dependencies = null) => new
    {
        id,
        project_id = "Pack123",
        name = number,
        version_number = number,
        version_type = type,
        status = "listed",
        date_published = published,
        game_versions = new[] { "1.21.8" },
        dependencies = dependencies ?? [],
        files = new[]
        {
            new
            {
                filename = "fixture.zip",
                url = "https://cdn.modrinth.com/data/file.zip",
                primary = true,
                size = archive.Length,
                hashes = new { sha512 = hash, sha1 = "" },
            },
        },
    };

    private static byte[] ValidPack() => Pack(new Dictionary<string, byte[]>
    {
        ["assets/minecraft/textures/block/stone.png"] = Pixel(128),
        ["assets/minecraft/textures/item/iron_pickaxe.png"] = Pixel(180),
        ["assets/minecraft/sounds/block/stone/break1.ogg"] = [0x4f, 0x67, 0x67, 0x53, 0, 1],
        ["assets/minecraft/sounds/block/stone/break2.ogg"] = [0x4f, 0x67, 0x67, 0x53, 0, 2],
        ["assets/minecraft/sounds.json"] = Utf8("""
            {"shared.stone":{"sounds":[{"name":"block/stone/break1","weight":2}]},
             "block.stone.break":{"sounds":[
               {"name":"shared.stone","type":"event","weight":3},
               {"name":"block/stone/break2","weight":1}]}}
            """),
        ["assets/minecraft/blockstates/stone.json"] = Utf8("""
            {"variants":{" ":[{"model":"minecraft:block/stone_parent","weight":1},
                                {"model":"minecraft:block/stone_parent","y":90,"uvlock":true,"weight":3}],
                         "":[{"model":"minecraft:block/stone_parent","weight":1},
                              {"model":"minecraft:block/stone_parent","y":90,"uvlock":true,"weight":3}]},
             "multipart":[{"when":{"snowy":"false"},"apply":{"model":"minecraft:block/stone_parent","x":90}}]}
            """),
        ["assets/minecraft/models/block/stone_parent.json"] = Utf8("""
            {"parent":"minecraft:block/cube_all","textures":{"all":"minecraft:block/stone"},
             "display":{"gui":{"rotation":[30,45,0],"translation":[1,2,3],"scale":[1,1,1]}}}
            """),
        ["assets/minecraft/blockstates/detail.json"] = Utf8("""
            {"variants":{"":{"model":"minecraft:block/detail","y":90,"uvlock":true}}}
            """),
        ["assets/minecraft/models/block/detail.json"] = Utf8("""
            {"ambientocclusion":false,"textures":{"all":"minecraft:block/stone"},"elements":[
             {"from":[0,0,0],"to":[16,16,16],"shade":false,
              "rotation":{"origin":[8,8,8],"axis":"y","angle":22.5,"rescale":true},
              "faces":{"north":{"texture":"#all","uv":[0,0,16,16],"cullface":"north",
                                  "rotation":90,"tintindex":0},
                       "south":{"texture":"#all","cullface":"south"},
                       "east":{"texture":"#all","cullface":"east"},
                       "west":{"texture":"#all","cullface":"west"},
                       "up":{"texture":"#all","cullface":"up"},
                       "down":{"texture":"#all","cullface":"down"}}}]}
            """),
        ["assets/minecraft/items/iron_pickaxe.json"] = Utf8("""
            {"model":{"type":"minecraft:condition","property":"damaged",
              "on_true":{"type":"minecraft:model","model":"minecraft:item/iron_pickaxe"},
              "on_false":{"type":"minecraft:model","model":"minecraft:item/iron_pickaxe"}}}
            """),
        ["assets/minecraft/models/item/iron_pickaxe.json"] = Utf8("""
            {"parent":"minecraft:item/generated","textures":{"layer0":"minecraft:item/iron_pickaxe"},
             "overrides":[{"predicate":{"damage":0.5},"model":"minecraft:item/iron_pickaxe"}]}
            """),
    });

    private static byte[] LargeMostlyVisualPack()
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "pack.mcmeta", Utf8(PackMeta("large mixed fixture")));
            for (var i = 0; i < SoundPackArchive.MaximumSoundEntries + 8; i++)
                Write(archive, $"assets/minecraft/lang/fixture-{i:00000}.json", []);
            Write(archive, "assets/minecraft/sounds/block/stone/break1.ogg",
                [0x4f, 0x67, 0x67, 0x53, 0, 1]);
            Write(archive, "assets/minecraft/sounds/empty.ogg", []);
        }
        return bytes.ToArray();
    }

    private static byte[] Pack(Dictionary<string, byte[]> entries, string description = "P7.6 fixture")
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "pack.mcmeta", Utf8(PackMeta(description)));
            foreach (var (path, content) in entries) Write(archive, path, content);
        }
        return bytes.ToArray();
    }

    private static void Write(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var output = entry.Open();
        output.Write(bytes);
    }

    private static string PackMeta(string description) => JsonSerializer.Serialize(new
    {
        pack = new { pack_format = 64, description },
    });

    private static byte[] Pixel(byte tone) => Png.Encode(new Image(1, 1, [tone, tone, tone, 255]));
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static SoundPackHttpResult Json(HttpStatusCode status, byte[] body, Uri finalUri) =>
        new(status, "application/json", body, finalUri);

    private sealed class FakeTransport(params SoundPackHttpResult[] responses) : ISoundPackTransport
    {
        private readonly Queue<SoundPackHttpResult> _responses = new(responses);
        public List<Uri> Requests { get; } = [];
        public bool Offline { get; init; }

        public Task<SoundPackHttpResult> GetAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
        {
            Requests.Add(uri);
            cancellationToken.ThrowIfCancellationRequested();
            if (Offline || _responses.Count == 0) throw new HttpRequestException("offline fixture");
            var response = _responses.Dequeue();
            if (response.Body.Length > maximumBytes) throw new SoundPackProviderException("fixture too large");
            return Task.FromResult(response);
        }

        public Task<SoundPackFileHttpResult> DownloadAsync(
            Uri uri,
            string destination,
            long maximumBytes,
            long expectedBytes,
            IProgress<SoundPackDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            Requests.Add(uri);
            cancellationToken.ThrowIfCancellationRequested();
            if (Offline || _responses.Count == 0) throw new HttpRequestException("offline fixture");
            var response = _responses.Dequeue();
            if (response.Body.LongLength > maximumBytes) throw new SoundPackProviderException("fixture too large");
            if ((int)response.Status is >= 200 and < 300)
            {
                progress?.Report(new SoundPackDownloadProgress(0, expectedBytes));
                File.WriteAllBytes(destination, response.Body);
                progress?.Report(new SoundPackDownloadProgress(response.Body.LongLength, expectedBytes));
            }
            return Task.FromResult(new SoundPackFileHttpResult(
                response.Status, response.ContentType, response.Body.LongLength,
                Convert.ToHexStringLower(SHA512.HashData(response.Body)), response.FinalUri,
                response.RetryAfter));
        }
    }

    private sealed class CaptureProgress : IProgress<ResourcePackDownloadProgress>
    {
        public List<ResourcePackDownloadProgress> Values { get; } = [];
        public void Report(ResourcePackDownloadProgress value) => Values.Add(value);
    }
}
