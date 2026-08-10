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
    Uri ProjectUri);

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
    byte[] Encoded);

public sealed class SoundPackProviderException(string message) : Exception(message);

public sealed record SoundPackHttpResult(
    HttpStatusCode Status,
    string ContentType,
    byte[] Body,
    Uri FinalUri);

public interface ISoundPackTransport
{
    Task<SoundPackHttpResult> GetAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken);
}

/// <summary>HTTPS transport with redirects disabled and a hard response-size ceiling.</summary>
public sealed class BoundedSoundPackTransport : ISoundPackTransport
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly HttpClient Client = BuildClient();

    private static HttpClient BuildClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        var version = typeof(ModrinthSoundPackProvider).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"codingncaffeine-Driftwood/{version} (+https://github.com/codingncaffeine/Driftwood)");
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
            throw new SoundPackProviderException($"the provider response was larger than {Size(maximumBytes)}");

        await using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
        using var body = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[32 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), requestToken).ConfigureAwait(false);
            if (read == 0) break;
            if (body.Length + read > maximumBytes)
                throw new SoundPackProviderException($"the provider response was larger than {Size(maximumBytes)}");
            body.Write(buffer, 0, read);
        }

        return new SoundPackHttpResult(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType ?? "",
            body.ToArray(),
            response.RequestMessage?.RequestUri ?? uri);
    }

    private static string Size(int bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024 / 1024} MiB"
        : $"{bytes / 1024} KiB";
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

        try { return ParseSearch(response.Body, offset); }
        catch (Exception error) when (
            error is JsonException or InvalidDataException or FormatException or InvalidOperationException)
        {
            throw new SoundPackProviderException($"Modrinth sent malformed catalog data: {error.Message}");
        }
    }

    public async Task<RemoteSoundPackFile> DownloadAsync(
        RemoteSoundPack pack, CancellationToken cancellationToken = default)
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
        var file = await Request(choice.Uri, checked((int)SoundPackArchive.MaximumArchiveBytes), cancellationToken)
            .ConfigureAwait(false);
        RequireHost(file.FinalUri, "cdn.modrinth.com");
        EnsureSuccess(file, $"'{pack.Name}'");

        if (file.Body.LongLength != choice.Size)
            throw new SoundPackProviderException(
                $"the download was {file.Body.LongLength:N0} bytes, not Modrinth's {choice.Size:N0}");

        var actual = Convert.ToHexString(SHA512.HashData(file.Body)).ToLowerInvariant();
        if (!actual.Equals(choice.Sha512, StringComparison.OrdinalIgnoreCase))
            throw new SoundPackProviderException("the download did not match Modrinth's SHA-512");

        return new RemoteSoundPackFile(
            pack, choice.VersionId, choice.Version, choice.FileName, actual, file.Body);
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

            found.Add(new RemoteSoundPack(
                id, slug, name, author, license, Math.Max(0, downloads), description,
                new Uri("https://modrinth.com/resourcepack/" + Uri.EscapeDataString(slug))));
        }

        return new SoundPackPage(
            found, offset, Math.Max(total, found.Count),
            HasPrevious: offset > 0,
            HasNext: offset + found.Count < total);
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

    private static void EnsureSuccess(SoundPackHttpResult response, string subject)
    {
        if (response.Status == HttpStatusCode.TooManyRequests)
            throw new SoundPackProviderException($"{subject} is rate limited — try again later");
        if (response.Status == HttpStatusCode.NotFound)
            throw new SoundPackProviderException($"{subject} was not found");
        if ((int)response.Status is < 200 or >= 300)
            throw new SoundPackProviderException($"{subject} returned HTTP {(int)response.Status}");
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
               "description":"open audio","project_type":"resourcepack","categories":["audio","minecraft"]},
              {"project_id":"NoAudio1","slug":"pictures","title":"Pictures",
               "author":"maker","license":"MIT","downloads":1,
               "description":"not audio","project_type":"resourcepack","categories":["blocks"]}
            ],"offset":0,"limit":10,"total_hits":1}
            """);

        var versions = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new
            {
                id = "Version1", version_number = "r1", version_type = "release", status = "listed",
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
                Json(versions, new Uri("https://api.modrinth.com/v2/project/Ab12Cd34/version")),
                new SoundPackHttpResult(HttpStatusCode.OK, "application/zip", archive,
                    new Uri("https://cdn.modrinth.com/data/file.zip")));
            var provider = new ModrinthSoundPackProvider(transport);
            var page = provider.SearchAsync("clear", openSourceOnly: true, 0).GetAwaiter().GetResult();
            if (page.Packs.Count != 1) faults.Add($"catalog kept {page.Packs.Count} usable rows, not 1");
            if (!transport.Requests[0].Query.Contains("open_source%3Atrue", StringComparison.OrdinalIgnoreCase))
                faults.Add("the open-source search did not send its facet");

            if (page.Packs.Count == 1)
            {
                var file = provider.DownloadAsync(page.Packs[0]).GetAwaiter().GetResult();
                if (!file.Encoded.AsSpan().SequenceEqual(archive)) faults.Add("the verified download changed bytes");
                if (!file.Sha512.Equals(hash, StringComparison.Ordinal)) faults.Add("the verified hash changed");
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

        detail = "audio-only search, open-license facet, newest release, bounded CDN and SHA-512 all exercised";
        return faults;
    }

    private static byte[] TinyArchive()
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("assets/minecraft/sounds/step/stone1.ogg");
            using var stream = entry.Open();
            stream.Write([0x4f, 0x67, 0x67, 0x53, 1, 2, 3, 4]);
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
    }
}
