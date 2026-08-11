using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Driftwood.Core.Audio;

public sealed record RemoteSoundPack(
    string Id,
    string Slug,
    string Name,
    string Author,
    string License,
    long Downloads,
    string Description,
    Uri ProjectUri,
    string LatestVersionId = "",
    long ArchiveBytes = 0);

public sealed record SoundPackPage(
    IReadOnlyList<RemoteSoundPack> Packs,
    int Offset,
    int Total,
    bool HasPrevious,
    bool HasNext);

public sealed record RemoteSoundPackFile(
    RemoteSoundPack Remote,
    string VersionId,
    string Version,
    string FileName,
    string Sha512,
    string ArchivePath,
    long Length,
    bool Temporary = false) : IDisposable
{
    public void Dispose()
    {
        if (!Temporary) return;
        try { if (File.Exists(ArchivePath)) File.Delete(ArchivePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Bytes received against the immutable size Modrinth published for the selected file.</summary>
public sealed record SoundPackDownloadProgress(long BytesReceived, long TotalBytes)
{
    public float Fraction => TotalBytes <= 0
        ? 0f
        : Math.Clamp((float)((double)BytesReceived / TotalBytes), 0f, 1f);
}

public sealed class SoundPackProviderException(string message) : Exception(message);

public sealed record SoundPackHttpResult(
    HttpStatusCode Status,
    string ContentType,
    byte[] Body,
    Uri FinalUri,
    TimeSpan? RetryAfter = null);

public sealed record SoundPackFileHttpResult(
    HttpStatusCode Status,
    string ContentType,
    long Length,
    string Sha512,
    Uri FinalUri,
    TimeSpan? RetryAfter = null);

public interface ISoundPackTransport
{
    Task<SoundPackHttpResult> GetAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken);

    Task<SoundPackFileHttpResult> DownloadAsync(
        Uri uri,
        string destination,
        long maximumBytes,
        long expectedBytes,
        IProgress<SoundPackDownloadProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>HTTPS transport with redirects disabled and separate metadata/file size ceilings.</summary>
public sealed class BoundedSoundPackTransport : ISoundPackTransport
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);
    private static readonly HttpClient Client = BuildClient();

    private static HttpClient BuildClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        // Each operation owns a linked deadline. HttpClient's timeout cannot remain at sixty
        // seconds once a legitimate audio archive can be hundreds of megabytes.
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var version = typeof(ModrinthSoundPackProvider).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"codingncaffeine.Driftwood/{version} (+https://github.com/codingncaffeine/Driftwood)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return client;
    }

    public async Task<SoundPackHttpResult> GetAsync(
        Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new SoundPackProviderException("the provider tried to use a connection that was not HTTPS");

        // ResponseHeadersRead makes GetAsync finish before the body has arrived, so HttpClient's
        // own timeout would cover only the headers. Keep one deadline around the stream reads too.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        var requestToken = deadline.Token;

        using var response = await Client.GetAsync(
            uri, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);

        if (response.Content.Headers.ContentLength is > 0 and var length && length > maximumBytes)
            throw new SoundPackProviderException(
                $"the provider response was larger than {SoundPackArchive.DescribeBytes(maximumBytes)}");

        await using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
        using var body = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[32 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), requestToken).ConfigureAwait(false);
            if (read == 0) break;
            if (body.Length + read > maximumBytes)
                throw new SoundPackProviderException(
                    $"the provider response was larger than {SoundPackArchive.DescribeBytes(maximumBytes)}");
            body.Write(buffer, 0, read);
        }

        return new SoundPackHttpResult(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType ?? "",
            body.ToArray(),
            response.RequestMessage?.RequestUri ?? uri,
            RetryAfter(response));
    }

    public async Task<SoundPackFileHttpResult> DownloadAsync(
        Uri uri,
        string destination,
        long maximumBytes,
        long expectedBytes,
        IProgress<SoundPackDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new SoundPackProviderException("the provider tried to use a connection that was not HTTPS");
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (File.Exists(destination)) throw new IOException("the sound-pack staging file already exists");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DownloadTimeout);
        var requestToken = deadline.Token;

        try
        {
            using var response = await Client.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

            if ((int)response.StatusCode is < 200 or >= 300)
                return new SoundPackFileHttpResult(response.StatusCode, contentType, 0, "", finalUri,
                    RetryAfter(response));

            if (response.Content.Headers.ContentLength is > 0 and var advertised
                && advertised > maximumBytes)
                throw new SoundPackProviderException(
                    $"the provider response was larger than {SoundPackArchive.DescribeBytes(maximumBytes)}");

            var total = expectedBytes > 0
                ? expectedBytes
                : response.Content.Headers.ContentLength.GetValueOrDefault();
            progress?.Report(new SoundPackDownloadProgress(0, total));

            await using var input = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
            await using var output = new FileStream(
                destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            var buffer = new byte[128 * 1024];
            long received = 0;

            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), requestToken).ConfigureAwait(false);
                if (read == 0) break;
                if (received + read > maximumBytes)
                    throw new SoundPackProviderException(
                        $"the provider response was larger than {SoundPackArchive.DescribeBytes(maximumBytes)}");

                await output.WriteAsync(buffer.AsMemory(0, read), requestToken).ConfigureAwait(false);
                digest.AppendData(buffer, 0, read);
                received += read;
                progress?.Report(new SoundPackDownloadProgress(received, total));
            }

            await output.FlushAsync(requestToken).ConfigureAwait(false);
            return new SoundPackFileHttpResult(
                response.StatusCode,
                contentType,
                received,
                Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant(),
                finalUri,
                RetryAfter(response));
        }
        catch
        {
            try { if (File.Exists(destination)) File.Delete(destination); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (retry?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }
}

/// <summary>Keyless Modrinth resource-pack search and player-initiated download.</summary>
public sealed class ModrinthSoundPackProvider(ISoundPackTransport? transport = null)
{
    public const int PageSize = 10;
    private const int MaximumCatalogBytes = 1024 * 1024;
    private const int MaximumVersionsBytes = 2 * 1024 * 1024;
    private readonly ISoundPackTransport _transport = transport ?? new BoundedSoundPackTransport();

    private static readonly Uri Api = new("https://api.modrinth.com/v2/");

    public async Task<SoundPackPage> SearchAsync(
        string query, bool openSourceOnly, int offset,
        CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length > 80) throw new SoundPackProviderException("keep the search under 80 characters");
        offset = Math.Clamp(offset, 0, 10_000);

        var facets = openSourceOnly
            ? "[[\"project_type:resourcepack\"],[\"categories:audio\"],[\"open_source:true\"]]"
            : "[[\"project_type:resourcepack\"],[\"categories:audio\"]]";
        var uri = new Uri(Api,
            "search?query=" + Uri.EscapeDataString(query)
            + "&facets=" + Uri.EscapeDataString(facets)
            + "&index=downloads&offset=" + offset.ToString(CultureInfo.InvariantCulture)
            + "&limit=" + PageSize.ToString(CultureInfo.InvariantCulture));

        var response = await Request(uri, MaximumCatalogBytes, cancellationToken).ConfigureAwait(false);
        RequireHost(response.FinalUri, "api.modrinth.com");
        EnsureSuccess(response, "the Modrinth catalog");
        RequireJson(response);

        SoundPackPage page;
        try { page = ParseSearch(response.Body, offset); }
        catch (Exception error) when (
            error is JsonException or InvalidDataException or FormatException or InvalidOperationException)
        {
            throw new SoundPackProviderException($"Modrinth sent malformed catalog data: {error.Message}");
        }

        // Search hits name their latest version but do not carry its file bytes. The bulk endpoint
        // resolves all ten visible rows in one extra request, so the browser can show size before
        // somebody commits to a download. A catalog remains useful if this optional enrichment is
        // temporarily rate-limited or malformed; those rows say "size unknown" instead.
        var versionIds = page.Packs
            .Select(pack => pack.LatestVersionId)
            .Where(ValidId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (versionIds.Length == 0) return page;

        try
        {
            var ids = JsonSerializer.Serialize(versionIds);
            var sizesUri = new Uri(Api, "versions?ids=" + Uri.EscapeDataString(ids));
            var sizes = await Request(sizesUri, MaximumVersionsBytes, cancellationToken).ConfigureAwait(false);
            RequireHost(sizes.FinalUri, "api.modrinth.com");
            EnsureSuccess(sizes, "the Modrinth version catalog");
            RequireJson(sizes);
            return AddArchiveSizes(page, sizes.Body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SoundPackProviderException) { return page; }
        catch (Exception error) when (
            error is JsonException or InvalidDataException or FormatException or InvalidOperationException)
        {
            return page;
        }
    }

    public async Task<RemoteSoundPackFile> DownloadAsync(
        RemoteSoundPack pack, CancellationToken cancellationToken = default)
        => await DownloadAsync(pack, progress: null, cancellationToken).ConfigureAwait(false);

    public async Task<RemoteSoundPackFile> DownloadAsync(
        RemoteSoundPack pack,
        IProgress<SoundPackDownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!ValidId(pack.Id)) throw new SoundPackProviderException("that result has an invalid Modrinth ID");

        var versionsUri = new Uri(Api,
            "project/" + Uri.EscapeDataString(pack.Id)
            + "/version?loaders=%5B%22minecraft%22%5D&include_changelog=false");
        var versions = await Request(versionsUri, MaximumVersionsBytes, cancellationToken).ConfigureAwait(false);
        RequireHost(versions.FinalUri, "api.modrinth.com");
        EnsureSuccess(versions, $"versions of '{pack.Name}'");
        RequireJson(versions);

        DownloadChoice choice;
        try { choice = ParseVersion(versions.Body); }
        catch (Exception error) when (
            error is JsonException or InvalidDataException or FormatException or InvalidOperationException)
        {
            throw new SoundPackProviderException($"Modrinth sent malformed version data: {error.Message}");
        }

        RequireHost(choice.Uri, "cdn.modrinth.com");
        var temporary = Path.Combine(
            Path.GetTempPath(), "driftwood-sound-pack-" + Guid.NewGuid().ToString("N") + ".download");

        try
        {
            var file = await Download(
                    choice.Uri, temporary, SoundPackArchive.MaximumArchiveBytes, choice.Size,
                    progress, cancellationToken)
                .ConfigureAwait(false);
            RequireHost(file.FinalUri, "cdn.modrinth.com");
            EnsureSuccess(file.Status, $"'{pack.Name}'");

            if (file.Length != choice.Size)
                throw new SoundPackProviderException(
                    $"the download was {file.Length:N0} bytes, not Modrinth's {choice.Size:N0}");
            if (!file.Sha512.Equals(choice.Sha512, StringComparison.OrdinalIgnoreCase))
                throw new SoundPackProviderException("the download did not match Modrinth's SHA-512");

            return new RemoteSoundPackFile(
                pack, choice.VersionId, choice.Version, choice.FileName, file.Sha512,
                temporary, file.Length, Temporary: true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static SoundPackPage ParseSearch(byte[] json, int requestedOffset)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 20 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("hits", out var hits)
            || hits.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("the response has no hits array");

        var total = Integer(root, "total_hits", 0);
        var offset = Integer(root, "offset", requestedOffset);
        var found = new List<RemoteSoundPack>();

        foreach (var hit in hits.EnumerateArray())
        {
            if (found.Count >= PageSize) break;
            if (hit.ValueKind != JsonValueKind.Object) continue;
            if (!String(hit, "project_type").Equals("resourcepack", StringComparison.OrdinalIgnoreCase)) continue;
            if (!HasCategory(hit, "audio")) continue;

            var id = String(hit, "project_id");
            var slug = String(hit, "slug");
            if (!ValidId(id) || string.IsNullOrWhiteSpace(slug) || slug.Length > 128) continue;

            var name = SafeLabel(String(hit, "title"), "unnamed sound pack");
            var author = SafeLabel(String(hit, "author"), "unknown");
            var license = SafeLabel(String(hit, "license"), "not supplied");
            var description = SafeLabel(String(hit, "description"), "");
            var downloads = Long(hit, "downloads", 0);
            var latestVersion = String(hit, "latest_version");
            if (!ValidId(latestVersion)) latestVersion = "";

            found.Add(new RemoteSoundPack(
                id, slug, name, author, license, Math.Max(0, downloads), description,
                new Uri("https://modrinth.com/resourcepack/" + Uri.EscapeDataString(slug)),
                latestVersion));
        }

        return new SoundPackPage(
            found, offset, Math.Max(total, found.Count),
            HasPrevious: offset > 0,
            HasNext: offset + found.Count < total);
    }

    internal static SoundPackPage AddArchiveSizes(SoundPackPage page, byte[] json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("the response is not a version array");

        var bytesByVersion = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var version in document.RootElement.EnumerateArray())
        {
            if (bytesByVersion.Count >= PageSize * 2)
                throw new InvalidDataException("the size response contains too many versions");
            if (version.ValueKind != JsonValueKind.Object) continue;

            var id = String(version, "id");
            if (!ValidId(id)
                || !version.TryGetProperty("files", out var files)
                || files.ValueKind != JsonValueKind.Array) continue;

            var candidates = files.EnumerateArray()
                .Where(file => file.ValueKind == JsonValueKind.Object)
                .Where(file => String(file, "filename").EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (candidates.Length == 0) continue;

            var selected = candidates.FirstOrDefault(file => Boolean(file, "primary")) is var primary
                           && primary.ValueKind == JsonValueKind.Object
                ? primary
                : candidates[0];
            var size = Long(selected, "size", -1);
            if (size > 0) bytesByVersion[id] = size;
        }

        var packs = page.Packs
            .Select(pack => bytesByVersion.TryGetValue(pack.LatestVersionId, out var bytes)
                ? pack with { ArchiveBytes = bytes }
                : pack)
            .ToArray();
        return page with { Packs = packs };
    }

    private sealed record DownloadChoice(
        string VersionId, string Version, string FileName, Uri Uri, long Size, string Sha512,
        DateTimeOffset Published, bool Release);

    private static DownloadChoice ParseVersion(byte[] json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("the response is not a version array");

        var choices = new List<DownloadChoice>();
        foreach (var version in document.RootElement.EnumerateArray())
        {
            if (choices.Count >= 256) throw new InvalidDataException("the project has too many versions");
            if (version.ValueKind != JsonValueKind.Object) continue;
            if (String(version, "status") is { Length: > 0 } status
                && !status.Equals("listed", StringComparison.OrdinalIgnoreCase)) continue;

            var versionId = String(version, "id");
            var versionName = SafeLabel(String(version, "version_number"), "latest");
            if (!ValidId(versionId)) continue;
            if (!DateTimeOffset.TryParse(
                    String(version, "date_published"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var published)) continue;

            if (!version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) continue;
            var candidates = files.EnumerateArray()
                .Where(file => file.ValueKind == JsonValueKind.Object)
                .Where(file => String(file, "filename").EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (candidates.Length == 0) continue;

            var selected = candidates.FirstOrDefault(file => Boolean(file, "primary")) is var primary
                           && primary.ValueKind == JsonValueKind.Object
                ? primary
                : candidates[0];

            var fileName = SafeLabel(String(selected, "filename"), "sound-pack.zip");
            var url = String(selected, "url");
            var size = Long(selected, "size", -1);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            if (size <= 0 || size > SoundPackArchive.MaximumArchiveBytes) continue;
            if (!selected.TryGetProperty("hashes", out var hashes)) continue;
            var sha512 = String(hashes, "sha512").ToLowerInvariant();
            if (sha512.Length != 128 || !sha512.All(char.IsAsciiHexDigit)) continue;

            choices.Add(new DownloadChoice(
                versionId, versionName, fileName, uri, size, sha512, published,
                String(version, "version_type").Equals("release", StringComparison.OrdinalIgnoreCase)));
        }

        if (choices.Count == 0) throw new InvalidDataException("there is no listed Minecraft ZIP to download");
        var releases = choices.Where(choice => choice.Release).ToArray();
        IEnumerable<DownloadChoice> eligible = releases.Length > 0 ? releases : choices;
        return eligible.MaxBy(choice => choice.Published)!;
    }

    private async Task<SoundPackHttpResult> Request(
        Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        try { return await _transport.GetAsync(uri, maximumBytes, cancellationToken).ConfigureAwait(false); }
        catch (SoundPackProviderException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new SoundPackProviderException("Modrinth timed out — try again"); }
        catch (TimeoutException) { throw new SoundPackProviderException("Modrinth timed out — try again"); }
        catch (HttpRequestException)
        {
            throw new SoundPackProviderException("the machine is offline or Modrinth is unavailable");
        }
    }

    private async Task<SoundPackFileHttpResult> Download(
        Uri uri,
        string destination,
        long maximumBytes,
        long expectedBytes,
        IProgress<SoundPackDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _transport.DownloadAsync(
                    uri, destination, maximumBytes, expectedBytes, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SoundPackProviderException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new SoundPackProviderException("Modrinth timed out — try again"); }
        catch (TimeoutException) { throw new SoundPackProviderException("Modrinth timed out — try again"); }
        catch (HttpRequestException)
        {
            throw new SoundPackProviderException("the machine is offline or Modrinth is unavailable");
        }
        catch (IOException error)
        {
            throw new SoundPackProviderException($"the sound-pack download could not be stored: {error.Message}");
        }
        catch (UnauthorizedAccessException error)
        {
            throw new SoundPackProviderException($"the sound-pack download could not be stored: {error.Message}");
        }
    }

    private static void EnsureSuccess(SoundPackHttpResult response, string subject)
        => EnsureSuccess(response.Status, subject);

    private static void EnsureSuccess(HttpStatusCode status, string subject)
    {
        if (status == HttpStatusCode.TooManyRequests)
            throw new SoundPackProviderException($"{subject} is rate limited — try again later");
        if (status == HttpStatusCode.NotFound)
            throw new SoundPackProviderException($"{subject} was not found");
        if ((int)status is < 200 or >= 300)
            throw new SoundPackProviderException($"{subject} returned HTTP {(int)status}");
    }

    private static void RequireJson(SoundPackHttpResult response)
    {
        if (!response.ContentType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
            throw new SoundPackProviderException(
                $"Modrinth returned '{response.ContentType}' instead of JSON metadata");
    }

    private static void RequireHost(Uri uri, string host)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
            throw new SoundPackProviderException($"the provider tried to leave its allowed HTTPS host ({host})");
    }

    private static bool HasCategory(JsonElement hit, string wanted) =>
        hit.TryGetProperty("categories", out var categories)
        && categories.ValueKind == JsonValueKind.Array
        && categories.EnumerateArray().Any(value =>
            value.ValueKind == JsonValueKind.String
            && wanted.Equals(value.GetString(), StringComparison.OrdinalIgnoreCase));

    private static string String(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int Integer(JsonElement item, string property, int fallback) =>
        item.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : fallback;

    private static long Long(JsonElement item, string property, long fallback) =>
        item.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : fallback;

    private static bool Boolean(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool ValidId(string value) => value.Length is >= 3 and <= 64 && value.All(char.IsAsciiLetterOrDigit);

    private static string SafeLabel(string value, string fallback)
    {
        var safe = new string(value.Where(character => !char.IsControl(character)).Take(160).ToArray()).Trim();
        return safe.Length == 0 ? fallback : safe;
    }
}

/// <summary>Offline controls for catalog filtering, version choice, host bounds and hashes.</summary>
public static class ModrinthSoundPackSelfTest
{
    public static List<string> Run(out string detail)
    {
        var faults = new List<string>();
        var archive = TinyArchive();
        var hash = Convert.ToHexString(SHA512.HashData(archive)).ToLowerInvariant();

        var catalog = Encoding.UTF8.GetBytes("""
            {"hits":[
              {"project_id":"Ab12Cd34","slug":"clear-sounds","title":"Clear Sounds",
               "author":"maker","license":"CC0-1.0","downloads":42,
               "description":"open audio","project_type":"resourcepack","categories":["audio","minecraft"],
               "latest_version":"Version1"},
              {"project_id":"NoAudio1","slug":"pictures","title":"Pictures",
               "author":"maker","license":"MIT","downloads":1,
               "description":"not audio","project_type":"resourcepack","categories":["blocks"]}
            ],"offset":0,"limit":10,"total_hits":1}
            """);

        var versions = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new
            {
                id = "Version1", project_id = "Ab12Cd34",
                version_number = "r1", version_type = "release", status = "listed",
                date_published = "2026-08-01T12:00:00Z",
                files = new[]
                {
                    new
                    {
                        filename = "Clear Sounds.zip", url = "https://cdn.modrinth.com/data/file.zip",
                        primary = true, size = archive.Length, hashes = new { sha512 = hash }
                    }
                }
            }
        });

        try
        {
            var transport = new FakeTransport(
                Json(catalog, new Uri("https://api.modrinth.com/v2/search")),
                Json(versions, new Uri("https://api.modrinth.com/v2/versions")),
                Json(versions, new Uri("https://api.modrinth.com/v2/project/Ab12Cd34/version")),
                new SoundPackHttpResult(HttpStatusCode.OK, "application/zip", archive,
                    new Uri("https://cdn.modrinth.com/data/file.zip")));
            var provider = new ModrinthSoundPackProvider(transport);
            var page = provider.SearchAsync("clear", openSourceOnly: true, 0).GetAwaiter().GetResult();
            if (page.Packs.Count != 1) faults.Add($"catalog kept {page.Packs.Count} usable rows, not 1");
            if (!transport.Requests[0].Query.Contains("open_source%3Atrue", StringComparison.OrdinalIgnoreCase))
                faults.Add("the open-source search did not send its facet");
            if (page.Packs.Count == 1 && page.Packs[0].ArchiveBytes != archive.LongLength)
                faults.Add($"the catalog showed {page.Packs[0].ArchiveBytes:N0} bytes, not {archive.LongLength:N0}");

            if (page.Packs.Count == 1)
            {
                var reports = new CaptureProgress();
                using var file = provider.DownloadAsync(page.Packs[0], reports).GetAwaiter().GetResult();
                if (!File.ReadAllBytes(file.ArchivePath).AsSpan().SequenceEqual(archive))
                    faults.Add("the verified download changed bytes");
                if (!file.Sha512.Equals(hash, StringComparison.Ordinal)) faults.Add("the verified hash changed");
                if (reports.Values.Count < 2
                    || reports.Values[0] != new SoundPackDownloadProgress(0, archive.LongLength)
                    || reports.Values[^1] != new SoundPackDownloadProgress(archive.LongLength, archive.LongLength))
                    faults.Add("the streamed download did not report determinate byte progress");
            }
        }
        catch (Exception error)
        {
            faults.Add($"the good provider path threw {error.GetType().Name}: {error.Message}");
        }

        try
        {
            var badVersions = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(versions).Replace(hash, new string('0', 128), StringComparison.Ordinal));
            var transport = new FakeTransport(
                Json(badVersions, new Uri("https://api.modrinth.com/v2/project/Ab12Cd34/version")),
                new SoundPackHttpResult(HttpStatusCode.OK, "application/zip", archive,
                    new Uri("https://cdn.modrinth.com/data/file.zip")));
            var provider = new ModrinthSoundPackProvider(transport);
            var pack = new RemoteSoundPack(
                "Ab12Cd34", "clear-sounds", "Clear Sounds", "maker", "CC0-1.0", 42, "",
                new Uri("https://modrinth.com/resourcepack/clear-sounds"));
            provider.DownloadAsync(pack).GetAwaiter().GetResult();
            faults.Add("a download with the wrong SHA-512 was accepted");
        }
        catch (SoundPackProviderException error)
        {
            if (!error.Message.Contains("SHA-512", StringComparison.Ordinal))
                faults.Add($"a bad hash was refused as '{error.Message}'");
        }
        catch (Exception error)
        {
            faults.Add($"a bad hash threw {error.GetType().Name} instead of a provider error");
        }

        try
        {
            var transport = new FakeTransport(
                Json(catalog, new Uri("https://example.invalid/v2/search")));
            var provider = new ModrinthSoundPackProvider(transport);
            provider.SearchAsync("clear", openSourceOnly: false, 0).GetAwaiter().GetResult();
            faults.Add("catalog metadata that left api.modrinth.com was accepted");
        }
        catch (SoundPackProviderException error)
        {
            if (!error.Message.Contains("allowed HTTPS host", StringComparison.Ordinal))
                faults.Add($"a catalog host escape was refused as '{error.Message}'");
        }
        catch (Exception error)
        {
            faults.Add($"a catalog host escape threw {error.GetType().Name} instead of a provider error");
        }

        try
        {
            var hostileVersions = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(versions).Replace(
                    "https://cdn.modrinth.com/", "https://example.invalid/", StringComparison.Ordinal));
            var transport = new FakeTransport(
                Json(hostileVersions, new Uri("https://api.modrinth.com/v2/project/Ab12Cd34/version")));
            var provider = new ModrinthSoundPackProvider(transport);
            var pack = new RemoteSoundPack(
                "Ab12Cd34", "clear-sounds", "Clear Sounds", "maker", "CC0-1.0", 42, "",
                new Uri("https://modrinth.com/resourcepack/clear-sounds"));
            provider.DownloadAsync(pack).GetAwaiter().GetResult();
            faults.Add("a file URL that left cdn.modrinth.com was accepted");
        }
        catch (SoundPackProviderException error)
        {
            if (!error.Message.Contains("allowed HTTPS host", StringComparison.Ordinal))
                faults.Add($"a CDN host escape was refused as '{error.Message}'");
        }
        catch (Exception error)
        {
            faults.Add($"a CDN host escape threw {error.GetType().Name} instead of a provider error");
        }

        try
        {
            new BoundedSoundPackTransport().GetAsync(
                    new Uri("http://api.modrinth.com/v2/search"), 1024, CancellationToken.None)
                .GetAwaiter().GetResult();
            faults.Add("the HTTP transport accepted a non-HTTPS metadata request");
        }
        catch (SoundPackProviderException error)
        {
            if (!error.Message.Contains("not HTTPS", StringComparison.Ordinal))
                faults.Add($"a plain-HTTP request was refused as '{error.Message}'");
        }
        catch (Exception error)
        {
            faults.Add($"a plain-HTTP request threw {error.GetType().Name} instead of a provider error");
        }

        detail = "audio-only search, visible size, open-license facet, newest release, streamed progress, HTTPS host bounds and SHA-512 all exercised";
        return faults;
    }

    private static byte[] TinyArchive()
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("assets/minecraft/sounds/step/stone1.ogg");
            using var stream = entry.Open();
            stream.Write([0x4f, 0x67, 0x67, 0x53, 0, 1, 2, 3, 4]);
        }
        return bytes.ToArray();
    }

    private static SoundPackHttpResult Json(byte[] body, Uri uri) =>
        new(HttpStatusCode.OK, "application/json", body, uri);

    private sealed class FakeTransport(params SoundPackHttpResult[] results) : ISoundPackTransport
    {
        private readonly Queue<SoundPackHttpResult> _results = new(results);
        public List<Uri> Requests { get; } = [];

        public Task<SoundPackHttpResult> GetAsync(
            Uri uri, int maximumBytes, CancellationToken cancellationToken)
        {
            Requests.Add(uri);
            if (_results.Count == 0) throw new HttpRequestException("no planted response");
            var result = _results.Dequeue();
            if (result.Body.Length > maximumBytes) throw new SoundPackProviderException("planted answer too large");
            return Task.FromResult(result);
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
            if (_results.Count == 0) throw new HttpRequestException("no planted response");
            var result = _results.Dequeue();
            if (result.Body.LongLength > maximumBytes)
                throw new SoundPackProviderException("planted answer too large");
            if ((int)result.Status is >= 200 and < 300)
            {
                progress?.Report(new SoundPackDownloadProgress(0, expectedBytes));
                File.WriteAllBytes(destination, result.Body);
                progress?.Report(new SoundPackDownloadProgress(result.Body.LongLength, expectedBytes));
            }

            return Task.FromResult(new SoundPackFileHttpResult(
                result.Status,
                result.ContentType,
                result.Body.LongLength,
                Convert.ToHexString(SHA512.HashData(result.Body)).ToLowerInvariant(),
                result.FinalUri));
        }
    }

    private sealed class CaptureProgress : IProgress<SoundPackDownloadProgress>
    {
        public List<SoundPackDownloadProgress> Values { get; } = [];
        public void Report(SoundPackDownloadProgress value) => Values.Add(value);
    }
}
