using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Driftwood.Core.Audio;

namespace Driftwood.Core.Textures;

public enum ResourcePackSort
{
    Relevance,
    Downloads,
    Newest,
    Updated,
}

public enum ResourcePackProviderFailure
{
    Offline,
    RateLimited,
    ApiRetired,
    MalformedMetadata,
    HostRefused,
    InvalidRequest,
    DownloadFailed,
}

public sealed class ResourcePackProviderException(
    ResourcePackProviderFailure failure,
    string message,
    TimeSpan? retryAfter = null) : Exception(message)
{
    public ResourcePackProviderFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed record RemoteResourcePack(
    string Id,
    string Slug,
    string Name,
    string Author,
    string License,
    long Downloads,
    string Description,
    Uri ProjectUri,
    Uri? IconUri,
    uint? Colour,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> GameVersions,
    DateTimeOffset Created,
    DateTimeOffset Updated,
    string LatestVersionId = "",
    IReadOnlyList<Uri>? Gallery = null)
{
    public IReadOnlyList<Uri> GalleryImages => Gallery ?? [];
}

public sealed record ResourcePackPage(
    IReadOnlyList<RemoteResourcePack> Packs,
    int Offset,
    int Total,
    bool HasPrevious,
    bool HasNext,
    bool Cached);

public sealed record RemoteResourcePackFile(
    string FileName,
    Uri Uri,
    long Size,
    string Sha512,
    string Sha1,
    bool Primary);

public sealed record RemoteResourcePackVersion(
    string Id,
    string ProjectId,
    string Name,
    string Number,
    string Channel,
    DateTimeOffset Published,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<PackLibrary.PackDependency> Dependencies,
    IReadOnlyList<RemoteResourcePackFile> Files)
{
    public RemoteResourcePackFile? PrimaryFile =>
        Files.FirstOrDefault(static file => file.Primary) ?? Files.FirstOrDefault();
}

public sealed record ResourcePackDownloadProgress(long BytesReceived, long TotalBytes)
{
    public float Fraction => TotalBytes <= 0 ? 0f
        : Math.Clamp((float)((double)BytesReceived / TotalBytes), 0f, 1f);
}

public sealed record DownloadedResourcePack(
    RemoteResourcePack Project,
    RemoteResourcePackVersion Version,
    RemoteResourcePackFile File,
    string ArchivePath,
    PackCompatibility.Summary Compatibility,
    bool Temporary = true) : IDisposable
{
    /// <summary>Atomically lands the verified archive and its provenance sidecar.</summary>
    public PackLibrary.Entry? Install(
        out string why,
        string? shelf = null,
        PackLibrary.Entry? replacing = null)
    {
        var provenance = new PackLibrary.Provenance(
            "Modrinth",
            Project.Id,
            Version.Id,
            Version.Number,
            Project.Author,
            Project.ProjectUri.ToString(),
            Project.License,
            File.Sha512,
            Version.Dependencies,
            Project.Name,
            Project.Description);
        var installName = replacing is { Path.Length: > 0 }
            ? System.IO.Path.GetFileName(replacing.Value.Path) : File.FileName;
        var installed = PackLibrary.Install(ArchivePath, out why, shelf, provenance, installName);
        if (installed is { } entry)
        {
            PackLibrary.UpdateMetadata(entry.Path, Compatibility.State, updateAvailable: false, provenance);
            return PackLibrary.List(shelf).FirstOrDefault(candidate => string.Equals(
                candidate.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    public void Dispose()
    {
        if (!Temporary) return;
        try { if (System.IO.File.Exists(ArchivePath)) System.IO.File.Delete(ArchivePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Keyless Modrinth v2 resource-pack catalog, version/dependency reader, bounded downloader and
/// offline metadata cache. Network activity is explicit; no method is called during game startup.
/// </summary>
public sealed class ModrinthResourcePackProvider
{
    public const int PageSize = 20;
    public const long MaximumArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumCatalogBytes = 2 * 1024 * 1024;
    private const int MaximumProjectBytes = 2 * 1024 * 1024;
    private const int MaximumVersionsBytes = 8 * 1024 * 1024;
    private const int MaximumIconBytes = 2 * 1024 * 1024;
    private const int MaximumGalleryBytes = 8 * 1024 * 1024;

    private static readonly Uri Api = new("https://api.modrinth.com/v2/");
    private readonly ISoundPackTransport _transport;
    private readonly string _cacheFolder;

    public ModrinthResourcePackProvider(
        ISoundPackTransport? transport = null,
        string? cacheFolder = null)
    {
        _transport = transport ?? new BoundedSoundPackTransport();
        _cacheFolder = System.IO.Path.GetFullPath(string.IsNullOrWhiteSpace(cacheFolder)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Driftwood", "cache", "modrinth-resource-packs")
            : cacheFolder);
    }

    public async Task<ResourcePackPage> SearchAsync(
        string query,
        ResourcePackSort sort = ResourcePackSort.Relevance,
        string? category = null,
        string? gameVersion = null,
        int offset = 0,
        bool offlineOnly = false,
        CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length > 100) throw Failure(ResourcePackProviderFailure.InvalidRequest,
            "keep the search under 100 characters");
        category = SafeFacet(category, "category");
        gameVersion = SafeFacet(gameVersion, "Minecraft version", allowDots: true);
        offset = Math.Clamp(offset, 0, 100_000);

        var facets = new List<string[]> { new[] { "project_type:resourcepack" } };
        if (category is not null) facets.Add([$"categories:{category}"]);
        if (gameVersion is not null) facets.Add([$"versions:{gameVersion}"]);
        var index = sort switch
        {
            ResourcePackSort.Downloads => "downloads",
            ResourcePackSort.Newest => "newest",
            ResourcePackSort.Updated => "updated",
            _ => "relevance",
        };
        var uri = new Uri(Api,
            "search?query=" + Uri.EscapeDataString(query)
            + "&facets=" + Uri.EscapeDataString(JsonSerializer.Serialize(facets))
            + "&index=" + index
            + "&offset=" + offset.ToString(CultureInfo.InvariantCulture)
            + "&limit=" + PageSize.ToString(CultureInfo.InvariantCulture));

        var fetched = await Fetch(uri, MaximumCatalogBytes, ParseSearch, offlineOnly, cancellationToken)
            .ConfigureAwait(false);
        var page = fetched.Value;
        return page with { Cached = fetched.Cached };
    }

    /// <summary>Fetches the project detail used by the right pane, including gallery/source license.</summary>
    public async Task<RemoteResourcePack> ProjectAsync(
        string projectId,
        bool offlineOnly = false,
        CancellationToken cancellationToken = default)
    {
        RequireId(projectId);
        var uri = new Uri(Api, "project/" + Uri.EscapeDataString(projectId));
        return (await Fetch(uri, MaximumProjectBytes, ParseProject, offlineOnly, cancellationToken)
            .ConfigureAwait(false)).Value;
    }

    public async Task<IReadOnlyList<RemoteResourcePackVersion>> VersionsAsync(
        string projectId,
        bool offlineOnly = false,
        CancellationToken cancellationToken = default)
    {
        RequireId(projectId);
        var uri = new Uri(Api,
            "project/" + Uri.EscapeDataString(projectId)
            + "/version?loaders=%5B%22minecraft%22%5D&include_changelog=false");
        return (await Fetch(uri, MaximumVersionsBytes, ParseVersions, offlineOnly, cancellationToken)
            .ConfigureAwait(false)).Value;
    }

    public async Task<byte[]> IconAsync(
        RemoteResourcePack project,
        bool offlineOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (project.IconUri is null) return [];
        return await ImageAsync(project.IconUri, MaximumIconBytes, "the Modrinth icon",
            offlineOnly, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fetches one project-gallery image through the same bounded CDN/cache path as icons.</summary>
    public async Task<byte[]> GalleryImageAsync(
        RemoteResourcePack project,
        int index = 0,
        bool offlineOnly = false,
        CancellationToken cancellationToken = default)
    {
        if ((uint)index >= (uint)project.GalleryImages.Count)
            throw Failure(ResourcePackProviderFailure.InvalidRequest,
                "that project gallery image does not exist");
        return await ImageAsync(project.GalleryImages[index], MaximumGalleryBytes,
            "the Modrinth gallery image", offlineOnly, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> ImageAsync(
        Uri uri,
        int maximumBytes,
        string subject,
        bool offlineOnly,
        CancellationToken cancellationToken)
    {
        RequireCdn(uri);
        var cache = CachePath(uri, ".image");
        if (offlineOnly) return ReadBytes(cache, maximumBytes) ?? throw Failure(
            ResourcePackProviderFailure.Offline, $"{subject} is not in the offline cache");

        try
        {
            var response = await _transport.GetAsync(uri, maximumBytes, cancellationToken)
                .ConfigureAwait(false);
            RequireCdn(response.FinalUri);
            EnsureSuccess(response.Status, response.RetryAfter, subject);
            if (!response.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                throw Failure(ResourcePackProviderFailure.MalformedMetadata,
                    $"Modrinth returned a non-image response for {subject}");
            WriteCache(cache, response.Body);
            return response.Body;
        }
        catch (ResourcePackProviderException error) when (error.Failure == ResourcePackProviderFailure.Offline)
        {
            var cached = ReadBytes(cache, maximumBytes);
            if (cached is not null) return cached;
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException)
        {
            return ReadBytes(cache, maximumBytes) ?? throw Failure(ResourcePackProviderFailure.Offline,
                "the machine is offline or Modrinth is unavailable");
        }
        catch (SoundPackProviderException error)
        {
            throw Failure(ResourcePackProviderFailure.MalformedMetadata,
                $"{subject} response was refused: {error.Message}");
        }
    }

    public static RemoteResourcePackVersion ChooseDefault(
        IEnumerable<RemoteResourcePackVersion> versions,
        string? gameVersion = null)
    {
        var candidates = versions.Where(version => gameVersion is null
            || version.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0) throw Failure(ResourcePackProviderFailure.InvalidRequest,
            "there is no downloadable version for that Minecraft version");
        var releases = candidates.Where(version => version.Channel.Equals(
            "release", StringComparison.OrdinalIgnoreCase)).ToArray();
        return (releases.Length > 0 ? releases : candidates).MaxBy(static version => version.Published)!;
    }

    /// <summary>Streams and validates one explicitly selected version; it does not install it.</summary>
    public async Task<DownloadedResourcePack> DownloadAsync(
        RemoteResourcePack project,
        RemoteResourcePackVersion version,
        IProgress<ResourcePackDownloadProgress>? progress = null,
        string? stagingFolder = null,
        string? compatibilityCache = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(project.Id, version.ProjectId, StringComparison.Ordinal))
            throw Failure(ResourcePackProviderFailure.InvalidRequest,
                "the selected version belongs to a different project");
        var file = version.PrimaryFile ?? throw Failure(ResourcePackProviderFailure.InvalidRequest,
            "that version has no resource-pack ZIP");
        RequireCdn(file.Uri);
        if (file.Size <= 0 || file.Size > MaximumArchiveBytes)
            throw Failure(ResourcePackProviderFailure.DownloadFailed,
                $"the archive size is outside the {SoundPackArchive.DescribeBytes(MaximumArchiveBytes)} limit");

        stagingFolder = System.IO.Path.GetFullPath(string.IsNullOrWhiteSpace(stagingFolder)
            ? System.IO.Path.GetTempPath() : stagingFolder);
        Directory.CreateDirectory(stagingFolder);
        var token = Guid.NewGuid().ToString("N");
        var part = System.IO.Path.Combine(stagingFolder, $"driftwood-resource-pack-{token}.part");
        var verified = System.IO.Path.Combine(stagingFolder, $"driftwood-resource-pack-{token}.zip");
        try
        {
            var bridge = progress is null ? null : new Progress<SoundPackDownloadProgress>(value =>
                progress.Report(new ResourcePackDownloadProgress(value.BytesReceived, value.TotalBytes)));
            SoundPackFileHttpResult response;
            try
            {
                response = await _transport.DownloadAsync(
                    file.Uri, part, MaximumArchiveBytes, file.Size, bridge, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                throw Failure(ResourcePackProviderFailure.Offline, "Modrinth timed out — try again");
            }
            catch (HttpRequestException)
            {
                throw Failure(ResourcePackProviderFailure.Offline,
                    "the machine is offline or Modrinth is unavailable");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                          or SoundPackProviderException or TimeoutException)
            {
                throw Failure(ResourcePackProviderFailure.DownloadFailed,
                    $"the resource-pack download could not be stored: {error.Message}");
            }

            RequireCdn(response.FinalUri);
            EnsureSuccess(response.Status, response.RetryAfter, $"'{project.Name}'");
            if (response.Length != file.Size)
                throw Failure(ResourcePackProviderFailure.DownloadFailed,
                    $"the download was {response.Length:N0} bytes, not Modrinth's {file.Size:N0}");
            if (!response.Sha512.Equals(file.Sha512, StringComparison.OrdinalIgnoreCase))
                throw Failure(ResourcePackProviderFailure.DownloadFailed,
                    "the download did not match Modrinth's SHA-512");

            File.Move(part, verified);
            using (var pack = TexturePack.Open(verified, out var why))
            {
                if (pack is null) throw Failure(ResourcePackProviderFailure.DownloadFailed,
                    why ?? "the downloaded archive is not a resource pack");
                if (pack.Dialect is not (PackDialect.Java or PackDialect.JavaLegacy))
                    throw Failure(ResourcePackProviderFailure.DownloadFailed,
                        "the Modrinth catalog is for Java packs; this archive has a different layout");
                if (!pack.WithinSafetyBounds(out var bounds))
                    throw Failure(ResourcePackProviderFailure.DownloadFailed, bounds);
            }

            var compatibility = PackCompatibility.Inspect(
                verified, version.Dependencies, file.Sha512, compatibilityCache);
            if (compatibility.State == PackCompatibility.Invalid)
                throw Failure(ResourcePackProviderFailure.DownloadFailed,
                    compatibility.Issues.FirstOrDefault() ?? "the downloaded pack is invalid");
            return new DownloadedResourcePack(project, version, file, verified, compatibility);
        }
        catch
        {
            TryDelete(part);
            TryDelete(verified);
            throw;
        }
    }

    public async Task<bool> UpdateAvailableAsync(
        PackLibrary.Entry installed,
        bool offlineOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (!installed.Provider.Equals("Modrinth", StringComparison.OrdinalIgnoreCase)
            || !ValidId(installed.ProjectId)) return false;
        var versions = await VersionsAsync(installed.ProjectId, offlineOnly, cancellationToken)
            .ConfigureAwait(false);
        var latest = ChooseDefault(versions);
        return !latest.Id.Equals(installed.VersionId, StringComparison.Ordinal);
    }

    internal static ResourcePackPage ParseSearch(byte[] json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("hits", out var hits)
            || hits.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("the response has no hits array");

        var total = Integer(root, "total_hits", 0);
        var offset = Integer(root, "offset", 0);
        var found = new List<RemoteResourcePack>();
        foreach (var hit in hits.EnumerateArray())
        {
            if (found.Count >= PageSize) break;
            if (hit.ValueKind != JsonValueKind.Object
                || !String(hit, "project_type").Equals("resourcepack", StringComparison.OrdinalIgnoreCase))
                continue;
            var project = ParseHit(hit);
            if (project is not null) found.Add(project);
        }
        return new ResourcePackPage(found, offset, Math.Max(total, found.Count), offset > 0,
            offset + found.Count < total, Cached: false);
    }

    internal static RemoteResourcePack ParseProject(byte[] json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("project is not an object");
        var id = String(root, "id");
        var slug = String(root, "slug");
        if (!ValidId(id) || !SafeSlug(slug)) throw new InvalidDataException("project identity is invalid");

        var gallery = new List<Uri>();
        if (root.TryGetProperty("gallery", out var galleryArray) && galleryArray.ValueKind == JsonValueKind.Array)
            foreach (var image in galleryArray.EnumerateArray())
                if (Uri.TryCreate(String(image, "raw_url") is { Length: > 0 } raw
                        ? raw : String(image, "url"), UriKind.Absolute, out var uri)
                    && AllowedCdn(uri)) gallery.Add(uri);

        var license = root.TryGetProperty("license", out var licenseObject)
            ? SafeText(String(licenseObject, "id"), "not supplied", 80) : "not supplied";
        var versions = Strings(root, "game_versions", 128);
        var categories = Strings(root, "categories", 128);
        return new RemoteResourcePack(
            id, slug, SafeText(String(root, "title"), "unnamed resource pack", 160),
            SafeText(String(root, "team"), "project team", 80), license,
            Math.Max(0, Long(root, "downloads", 0)),
            SafeText(String(root, "description"), "", 500), ProjectUri(slug),
            SafeUri(String(root, "raw_icon_url")) ?? SafeUri(String(root, "icon_url")),
            Colour(root), categories, versions,
            Date(root, "published"), Date(root, "updated"), Gallery: gallery);
    }

    internal static IReadOnlyList<RemoteResourcePackVersion> ParseVersions(byte[] json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 40 });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("the version response is not an array");
        var versions = new List<RemoteResourcePackVersion>();
        foreach (var version in document.RootElement.EnumerateArray())
        {
            if (versions.Count >= 512) throw new InvalidDataException("the project has too many versions");
            if (version.ValueKind != JsonValueKind.Object) continue;
            var id = String(version, "id");
            var project = String(version, "project_id");
            if (!ValidId(id) || !ValidId(project)) continue;
            var status = String(version, "status");
            if (status.Length > 0 && status is not ("listed" or "archived")) continue;

            var dependencies = new List<PackLibrary.PackDependency>();
            if (version.TryGetProperty("dependencies", out var dependencyArray)
                && dependencyArray.ValueKind == JsonValueKind.Array)
                foreach (var dependency in dependencyArray.EnumerateArray().Take(256))
                {
                    var type = String(dependency, "dependency_type");
                    if (type is not ("required" or "optional" or "incompatible" or "embedded")) continue;
                    dependencies.Add(new PackLibrary.PackDependency(type,
                        String(dependency, "project_id"), String(dependency, "version_id"),
                        SafeText(String(dependency, "file_name"), "", 160)));
                }

            var files = new List<RemoteResourcePackFile>();
            if (version.TryGetProperty("files", out var fileArray) && fileArray.ValueKind == JsonValueKind.Array)
                foreach (var file in fileArray.EnumerateArray().Take(32))
                {
                    var filename = SafeFileName(String(file, "filename"));
                    if (!filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Uri.TryCreate(String(file, "url"), UriKind.Absolute, out var uri) || !AllowedCdn(uri)) continue;
                    var size = Long(file, "size", -1);
                    if (size <= 0 || size > MaximumArchiveBytes) continue;
                    if (!file.TryGetProperty("hashes", out var hashes)) continue;
                    var sha512 = String(hashes, "sha512").ToLowerInvariant();
                    var sha1 = String(hashes, "sha1").ToLowerInvariant();
                    if (sha512.Length != 128 || !sha512.All(Uri.IsHexDigit)) continue;
                    files.Add(new RemoteResourcePackFile(filename, uri, size, sha512, sha1,
                        Boolean(file, "primary")));
                }
            if (files.Count == 0) continue;
            versions.Add(new RemoteResourcePackVersion(
                id, project,
                SafeText(String(version, "name"), String(version, "version_number"), 160),
                SafeText(String(version, "version_number"), "latest", 80),
                SafeText(String(version, "version_type"), "release", 20),
                Date(version, "date_published"), Strings(version, "game_versions", 128),
                dependencies, files));
        }
        if (versions.Count == 0) throw new InvalidDataException("there is no listed Minecraft ZIP to download");
        return versions.OrderByDescending(static version => version.Published).ToArray();
    }

    private async Task<(T Value, bool Cached)> Fetch<T>(
        Uri uri,
        int maximumBytes,
        Func<byte[], T> parse,
        bool offlineOnly,
        CancellationToken cancellationToken)
    {
        RequireApi(uri);
        var cache = CachePath(uri, ".json");
        if (offlineOnly)
        {
            var bytes = ReadBytes(cache, maximumBytes) ?? throw Failure(ResourcePackProviderFailure.Offline,
                "that catalog page is not in the offline cache");
            return (Parse(parse, bytes), true);
        }

        try
        {
            var response = await _transport.GetAsync(uri, maximumBytes, cancellationToken).ConfigureAwait(false);
            RequireApi(response.FinalUri);
            EnsureSuccess(response.Status, response.RetryAfter, "the Modrinth catalog");
            if (!response.ContentType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
                throw Failure(ResourcePackProviderFailure.MalformedMetadata,
                    $"Modrinth returned '{response.ContentType}' instead of JSON metadata");
            var value = Parse(parse, response.Body);
            WriteCache(cache, response.Body);
            return (value, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return CachedOrThrow(parse, cache, maximumBytes,
                "Modrinth timed out — showing cached data if available");
        }
        catch (TimeoutException)
        {
            return CachedOrThrow(parse, cache, maximumBytes,
                "Modrinth timed out — showing cached data if available");
        }
        catch (HttpRequestException)
        {
            return CachedOrThrow(parse, cache, maximumBytes,
                "the machine is offline or Modrinth is unavailable");
        }
        catch (SoundPackProviderException error)
        {
            throw Failure(ResourcePackProviderFailure.MalformedMetadata,
                $"the Modrinth metadata response was refused: {error.Message}");
        }
    }

    private static T Parse<T>(Func<byte[], T> parser, byte[] bytes)
    {
        try { return parser(bytes); }
        catch (Exception error) when (error is JsonException or InvalidDataException or FormatException
                                      or InvalidOperationException or UriFormatException)
        {
            throw Failure(ResourcePackProviderFailure.MalformedMetadata,
                $"Modrinth sent malformed metadata: {error.Message}");
        }
    }

    private static (T Value, bool Cached) CachedOrThrow<T>(
        Func<byte[], T> parse,
        string cache,
        int maximumBytes,
        string message)
    {
        var bytes = ReadBytes(cache, maximumBytes);
        return bytes is null
            ? throw Failure(ResourcePackProviderFailure.Offline, message)
            : (Parse(parse, bytes), true);
    }

    private static RemoteResourcePack? ParseHit(JsonElement hit)
    {
        var id = String(hit, "project_id");
        var slug = String(hit, "slug");
        if (!ValidId(id) || !SafeSlug(slug)) return null;
        var latest = String(hit, "latest_version");
        if (!ValidId(latest)) latest = "";
        return new RemoteResourcePack(
            id, slug, SafeText(String(hit, "title"), "unnamed resource pack", 160),
            SafeText(String(hit, "author"), "unknown", 80),
            SafeText(String(hit, "license"), "not supplied", 80),
            Math.Max(0, Long(hit, "downloads", 0)),
            SafeText(String(hit, "description"), "", 500), ProjectUri(slug),
            SafeUri(String(hit, "icon_url")), Colour(hit), Strings(hit, "categories", 128),
            Strings(hit, "versions", 128), Date(hit, "date_created"), Date(hit, "date_modified"), latest);
    }

    private static void EnsureSuccess(HttpStatusCode status, TimeSpan? retryAfter, string subject)
    {
        if (status == HttpStatusCode.TooManyRequests)
        {
            var wait = retryAfter is { } delay ? $" Retry after {Math.Ceiling(delay.TotalSeconds):N0} seconds." : "";
            throw Failure(ResourcePackProviderFailure.RateLimited,
                $"{subject} is rate limited.{wait}", retryAfter);
        }
        if (status == HttpStatusCode.Gone)
            throw Failure(ResourcePackProviderFailure.ApiRetired,
                "this Modrinth API version is no longer available; Driftwood needs an update");
        if ((int)status is < 200 or >= 300)
            throw Failure(ResourcePackProviderFailure.Offline,
                $"{subject} returned HTTP {(int)status}");
    }

    private static void RequireApi(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("api.modrinth.com", StringComparison.OrdinalIgnoreCase))
            throw Failure(ResourcePackProviderFailure.HostRefused,
                "the provider tried to leave api.modrinth.com's allowed HTTPS host");
    }

    private static void RequireCdn(Uri uri)
    {
        if (!AllowedCdn(uri)) throw Failure(ResourcePackProviderFailure.HostRefused,
            "the provider tried to leave cdn.modrinth.com's allowed HTTPS host");
    }

    private static bool AllowedCdn(Uri uri) => uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase);

    private static string? SafeFacet(string? value, string label, bool allowDots = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim().ToLowerInvariant();
        if (value.Length > 64 || value.Any(character => !char.IsAsciiLetterOrDigit(character)
            && character != '-' && character != '_' && (!allowDots || character != '.')))
            throw Failure(ResourcePackProviderFailure.InvalidRequest, $"that {label} filter is invalid");
        return value;
    }

    private static string SafeFileName(string value)
    {
        value = System.IO.Path.GetFileName(value);
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string(value.Where(character => !char.IsControl(character)
            && !invalid.Contains(character)).Take(120).ToArray()).Trim();
        if (!safe.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) safe += ".zip";
        return safe.Length > 4 ? safe : "resource-pack.zip";
    }

    private static string SafeText(string value, string fallback, int maximum)
    {
        var safe = new string(value.Where(character => !char.IsControl(character))
            .Take(maximum).ToArray()).Trim();
        return safe.Length > 0 ? safe : fallback;
    }

    private string CachePath(Uri uri, string extension)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            uri.AbsoluteUri)));
        return System.IO.Path.Combine(_cacheFolder, hash + extension);
    }

    private static byte[]? ReadBytes(string path, int maximumBytes)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 0 || stream.Length > maximumBytes) return null;
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return stream.ReadByte() < 0 ? bytes : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void WriteCache(string path, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var temporary = $"{path}.{Guid.NewGuid():N}.part";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, path, overwrite: true);
            }
            finally { TryDelete(temporary); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void RequireId(string id)
    {
        if (!ValidId(id)) throw Failure(ResourcePackProviderFailure.InvalidRequest,
            "that result has an invalid Modrinth ID");
    }

    private static bool ValidId(string value) => value.Length is >= 3 and <= 64
        && value.All(char.IsAsciiLetterOrDigit);

    private static bool SafeSlug(string value) => value.Length is > 0 and <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static Uri ProjectUri(string slug) => new(
        "https://modrinth.com/resourcepack/" + Uri.EscapeDataString(slug));

    private static Uri? SafeUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && AllowedCdn(uri) ? uri : null;

    private static string String(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int Integer(JsonElement item, string property, int fallback) =>
        item.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : fallback;

    private static long Long(JsonElement item, string property, long fallback) =>
        item.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : fallback;

    private static bool Boolean(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset Date(JsonElement item, string property) =>
        DateTimeOffset.TryParse(String(item, property), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date) ? date : DateTimeOffset.UnixEpoch;

    private static uint? Colour(JsonElement item, string property = "color") =>
        item.TryGetProperty(property, out var value) && value.TryGetUInt32(out var colour)
            ? colour & 0xFFFFFFu : null;

    private static IReadOnlyList<string> Strings(JsonElement item, string property, int maximum) =>
        item.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(static value => value.ValueKind == JsonValueKind.String)
                .Select(value => SafeText(value.GetString() ?? "", "", 80)).Where(static value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(maximum).ToArray()
            : [];

    private static ResourcePackProviderException Failure(
        ResourcePackProviderFailure failure,
        string message,
        TimeSpan? retryAfter = null) => new(failure, message, retryAfter);
}
