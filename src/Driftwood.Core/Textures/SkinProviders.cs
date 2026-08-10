using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Driftwood.Core.Entities;

namespace Driftwood.Core.Textures;

/// <summary>What a remote catalog can honestly offer.</summary>
public readonly record struct SkinProviderCapabilities(
    bool Paging, bool Search, bool Tags, bool ModelFilter);

/// <summary>One remote skin, still outside the local shelf.</summary>
public sealed record RemoteSkin(
    string Id, string Name, ArmStyle? Arms, string Provider,
    Uri SourceUri, Uri? ImageUri);

public sealed record SkinPage(IReadOnlyList<RemoteSkin> Skins, string? NextCursor);

/// <summary>Validated remote bytes ready to preview, but not yet written to disk.</summary>
public sealed record RemoteSkinFile(RemoteSkin Remote, byte[] Encoded, PlayerSkinData Skin);

public interface ISkinProvider
{
    string Name { get; }
    SkinProviderCapabilities Capabilities { get; }
    Task<SkinPage> PageAsync(string? cursor, CancellationToken cancellationToken = default);
    Task<RemoteSkinFile> PreviewAsync(RemoteSkin skin, CancellationToken cancellationToken = default);
}

public sealed class SkinProviderException(string message) : Exception(message);

/// <summary>A bounded HTTP answer, abstracted so provider failure paths can be tested offline.</summary>
public sealed record SkinHttpResult(
    HttpStatusCode Status, string ContentType, byte[] Body, Uri FinalUri,
    IReadOnlyDictionary<string, string> Headers)
{
    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : "";
}

public interface ISkinTransport
{
    Task<SkinHttpResult> GetAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken);
}

/// <summary>HTTPS transport with redirects disabled and a hard response-size ceiling.</summary>
public sealed class BoundedSkinTransport : ISkinTransport
{
    private static readonly HttpClient Client = BuildClient();

    private static HttpClient BuildClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Driftwood/1.0 (+https://github.com/codingncaffeine/Driftwood)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
        return client;
    }

    public async Task<SkinHttpResult> GetAsync(
        Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new SkinProviderException("the provider tried to use a connection that was not HTTPS");

        using var response = await Client.GetAsync(
            uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (response.Content.Headers.ContentLength is > 0 and var length && length > maximumBytes)
            throw new SkinProviderException($"the provider response was larger than {maximumBytes / 1024} KiB");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream(Math.Min(maximumBytes, 32 * 1024));
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (body.Length + read > maximumBytes)
                throw new SkinProviderException($"the provider response was larger than {maximumBytes / 1024} KiB");
            body.Write(buffer, 0, read);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers) headers[header.Key] = string.Join(",", header.Value);
        foreach (var header in response.Content.Headers) headers[header.Key] = string.Join(",", header.Value);

        return new SkinHttpResult(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType ?? "",
            body.ToArray(),
            response.RequestMessage?.RequestUri ?? uri,
            headers);
    }
}

/// <summary>The documented MineSkin V2 recent-public feed.</summary>
/// <remarks>
/// This is intentionally a feed, not invented search. The documented endpoint exposes paging;
/// names may be absent and the client says so. Driftwood does not generate skins, authenticate, or
/// carry a MineSkin secret. The full texture is fetched only for the selected result and is saved
/// only after the player's explicit download action.
/// </remarks>
public sealed class MineSkinProvider(ISkinTransport? transport = null) : ISkinProvider
{
    private const int MaximumEntries = 256;
    private const int MaximumCursorLength = 256;
    private readonly ISkinTransport _transport = transport ?? new BoundedSkinTransport();
    private static readonly Uri Base = new("https://api.mineskin.org/v2/skins");

    public string Name => "MineSkin";
    public SkinProviderCapabilities Capabilities => new(Paging: true, Search: false, Tags: false, ModelFilter: false);

    public async Task<SkinPage> PageAsync(string? cursor, CancellationToken cancellationToken = default)
    {
        if (cursor?.Length > MaximumCursorLength)
            throw new SkinProviderException("the MineSkin page cursor was unreasonably long");

        var uri = string.IsNullOrWhiteSpace(cursor)
            ? Base
            : new Uri(Base + "?after=" + Uri.EscapeDataString(cursor));

        var response = await Request(uri, SkinLibrary.MaximumBytes, cancellationToken).ConfigureAwait(false);
        RequireHost(response.FinalUri, "api.mineskin.org");
        EnsureSuccess(response, "the MineSkin catalog");
        RequireJson(response);

        try { return ParsePage(response.Body); }
        catch (Exception error) when (error is JsonException or InvalidDataException
            or FormatException or InvalidOperationException)
        {
            throw new SkinProviderException($"MineSkin sent malformed catalog data: {error.Message}");
        }
    }

    public async Task<RemoteSkinFile> PreviewAsync(
        RemoteSkin skin, CancellationToken cancellationToken = default)
    {
        if (skin.Provider != Name) throw new SkinProviderException("that result did not come from MineSkin");

        var resolved = skin;
        if (resolved.ImageUri is null)
        {
            RequireHost(resolved.SourceUri, "api.mineskin.org");
            var detail = await Request(resolved.SourceUri, 256 * 1024, cancellationToken).ConfigureAwait(false);
            RequireHost(detail.FinalUri, "api.mineskin.org");
            EnsureSuccess(detail, "that MineSkin entry");
            RequireJson(detail);
            try { resolved = ResolveDetails(resolved, detail.Body); }
            catch (Exception error) when (error is JsonException or InvalidDataException
                or FormatException or InvalidOperationException)
            {
                throw new SkinProviderException($"MineSkin sent malformed skin data: {error.Message}");
            }
        }

        if (resolved.ImageUri is null) throw new SkinProviderException("MineSkin did not provide a texture URL");
        RequireHost(resolved.ImageUri, "textures.minecraft.net");

        var image = await Request(resolved.ImageUri, SkinLibrary.MaximumBytes, cancellationToken).ConfigureAwait(false);
        RequireHost(image.FinalUri, "textures.minecraft.net");
        EnsureSuccess(image, "that skin image");
        RequirePng(image);

        if (!PlayerSkin.TryBuild(image.Body, resolved.Name + ".png", resolved.Arms, exactSize: true,
                out var built, out var why))
            throw new SkinProviderException($"MineSkin returned a rejected image: {why}");

        return new RemoteSkinFile(resolved, image.Body, built!);
    }

    internal static SkinPage ParsePage(byte[] json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 20 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("skins", out var skins)
            || skins.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("the response has no skins array");

        var found = new List<RemoteSkin>();
        foreach (var item in skins.EnumerateArray())
        {
            if (found.Count >= MaximumEntries)
                throw new InvalidDataException($"the response has more than {MaximumEntries} skin entries");
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("a skin entry is not an object");
            var id = String(item, "uuid");
            if (!IsHexId(id)) throw new InvalidDataException("a skin entry has no valid UUID");

            var shortId = OptionalString(item, "shortId");
            var name = OptionalString(item, "name");
            if (string.IsNullOrWhiteSpace(name)) name = $"unnamed {(!string.IsNullOrWhiteSpace(shortId) ? shortId : id[..8])}";
            name = SafeLabel(name!);

            var arms = Variant(item);
            var image = TextureUri(item);
            found.Add(new RemoteSkin(
                id, name!, arms, "MineSkin",
                new Uri($"https://api.mineskin.org/v2/skins/{id}"), image));
        }

        string? next = null;
        if (root.TryGetProperty("pagination", out var pagination)
            && pagination.TryGetProperty("next", out var nextObject)
            && nextObject.TryGetProperty("after", out var after)
            && after.ValueKind == JsonValueKind.String)
            next = after.GetString();

        if (next is null
            && root.TryGetProperty("links", out var links)
            && links.TryGetProperty("next", out var link)
            && link.ValueKind == JsonValueKind.String)
            next = CursorFromLink(link.GetString());

        if (string.IsNullOrWhiteSpace(next)) next = null;
        else if (next.Length > MaximumCursorLength)
            throw new InvalidDataException("the next-page cursor is unreasonably long");

        return new SkinPage(found, next);
    }

    private static RemoteSkin ResolveDetails(RemoteSkin original, byte[] json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 20 });
        var root = document.RootElement;
        if (root.TryGetProperty("skin", out var wrapped)) root = wrapped;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("skin details are not an object");

        return original with
        {
            Arms = Variant(root) ?? original.Arms,
            ImageUri = TextureUri(root) ?? original.ImageUri,
        };
    }

    private static Uri? TextureUri(JsonElement item)
    {
        if (!item.TryGetProperty("texture", out var texture)) return null;

        if (texture.ValueKind == JsonValueKind.String)
            return TextureHash(texture.GetString());

        if (texture.ValueKind != JsonValueKind.Object) return null;

        if (texture.TryGetProperty("url", out var url))
        {
            var value = url.ValueKind == JsonValueKind.String
                ? url.GetString()
                : url.ValueKind == JsonValueKind.Object && url.TryGetProperty("skin", out var skinUrl)
                    ? skinUrl.GetString()
                    : null;
            if (value?.Length <= 2_048 && Uri.TryCreate(value, UriKind.Absolute, out var uri)) return uri;
        }

        if (texture.TryGetProperty("hash", out var hash))
        {
            var value = hash.ValueKind == JsonValueKind.String
                ? hash.GetString()
                : hash.ValueKind == JsonValueKind.Object && hash.TryGetProperty("skin", out var skinHash)
                    ? skinHash.GetString()
                    : null;
            return TextureHash(value);
        }

        return null;
    }

    private static Uri? TextureHash(string? hash) =>
        IsHex(hash, 32, 128) ? new Uri($"https://textures.minecraft.net/texture/{hash}") : null;

    private static ArmStyle? Variant(JsonElement item) =>
        OptionalString(item, "variant")?.ToLowerInvariant() switch
        {
            "classic" => ArmStyle.Classic,
            "slim" => ArmStyle.Slim,
            _ => null,
        };

    private static string String(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string? OptionalString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? CursorFromLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link) || link.Length > 4_096) return null;
        var at = link.IndexOf("after=", StringComparison.Ordinal);
        if (at < 0) return null;
        var value = link[(at + 6)..];
        var amp = value.IndexOf('&');
        if (amp >= 0) value = value[..amp];
        return Uri.UnescapeDataString(value);
    }

    private static string SafeLabel(string value)
    {
        var label = new string(value.Where(c => !char.IsControl(c)).Take(128).ToArray()).Trim();
        return label.Length == 0 ? "unnamed skin" : label;
    }

    private async Task<SkinHttpResult> Request(Uri uri, int max, CancellationToken cancellationToken)
    {
        try { return await _transport.GetAsync(uri, max, cancellationToken).ConfigureAwait(false); }
        catch (SkinProviderException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new SkinProviderException("the provider timed out — try again"); }
        catch (TimeoutException) { throw new SkinProviderException("the provider timed out — try again"); }
        catch (HttpRequestException) { throw new SkinProviderException("the machine is offline or the provider is unavailable"); }
    }

    internal static void EnsureSuccess(SkinHttpResult response, string subject)
    {
        if (response.Status == HttpStatusCode.TooManyRequests)
            throw new SkinProviderException($"{subject} is rate limited — try again later");
        if (response.Status == HttpStatusCode.NotFound)
            throw new SkinProviderException($"{subject} was not found");
        if ((int)response.Status is < 200 or >= 300)
            throw new SkinProviderException($"{subject} returned HTTP {(int)response.Status}");
    }

    internal static void RequirePng(SkinHttpResult response)
    {
        if (!response.ContentType.Equals("image/png", StringComparison.OrdinalIgnoreCase))
            throw new SkinProviderException($"the provider returned '{response.ContentType}' instead of a PNG");
    }

    internal static void RequireJson(SkinHttpResult response)
    {
        if (!response.ContentType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
            throw new SkinProviderException(
                $"the provider returned '{response.ContentType}' instead of JSON metadata");
    }

    internal static void RequireHost(Uri uri, string host)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
            throw new SkinProviderException($"the provider tried to leave its allowed HTTPS host ({host})");
    }

    private static bool IsHexId(string? value) =>
        value is not null
        && (Guid.TryParseExact(value, "N", out _) || Guid.TryParseExact(value, "D", out _));

    private static bool IsHex(string? value, int minimum, int maximum) =>
        value is { Length: >= 1 }
        && value.Length >= minimum && value.Length <= maximum
        && value.All(char.IsAsciiHexDigit);
}

/// <summary>Keyless player-name/UUID lookup through mcskin.me's documented raw-skin endpoint.</summary>
public sealed class PlayerSkinLookup(ISkinTransport? transport = null)
{
    private readonly ISkinTransport _transport = transport ?? new BoundedSkinTransport();

    public async Task<RemoteSkinFile> LookupAsync(
        string player, CancellationToken cancellationToken = default)
    {
        var uri = PlayerUri(player);
        SkinHttpResult response;
        try { response = await _transport.GetAsync(uri, SkinLibrary.MaximumBytes, cancellationToken).ConfigureAwait(false); }
        catch (SkinProviderException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new SkinProviderException("player lookup timed out — try again"); }
        catch (TimeoutException) { throw new SkinProviderException("player lookup timed out — try again"); }
        catch (HttpRequestException) { throw new SkinProviderException("the machine is offline or mcskin.me is unavailable"); }

        MineSkinProvider.RequireHost(response.FinalUri, "api.mcskin.me");
        MineSkinProvider.EnsureSuccess(response, $"player '{player.Trim()}'");
        MineSkinProvider.RequirePng(response);

        var arms = response.Header("X-Skin-Model").ToLowerInvariant() switch
        {
            "slim" => ArmStyle.Slim,
            "classic" => ArmStyle.Classic,
            _ => (ArmStyle?)null,
        };

        if (!PlayerSkin.TryBuild(response.Body, player.Trim() + ".png", arms, exactSize: true,
                out var built, out var why))
            throw new SkinProviderException($"mcskin.me returned a rejected image: {why}");

        var remote = new RemoteSkin(
            player.Trim(), player.Trim(), built!.Arms, "mcskin.me", uri, uri);
        return new RemoteSkinFile(remote, response.Body, built);
    }

    public static Uri PlayerUri(string player)
    {
        player = player.Trim();
        if (player.Length > 36)
            throw new SkinProviderException("enter a 1–16 character username or a UUID");
        var username = player.Length is >= 1 and <= 16 && player.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        var uuid = Guid.TryParseExact(player, "N", out _) || Guid.TryParseExact(player, "D", out _);

        if (!username && !uuid)
            throw new SkinProviderException("enter a 1–16 character username or a UUID");

        return new Uri("https://api.mcskin.me/skin/" + Uri.EscapeDataString(player));
    }
}

/// <summary>Offline controls for catalog parsing, paging, failures, lookup and remote validation.</summary>
public static class SkinProviderSelfTest
{
    public static List<string> Run(out string detail)
    {
        var faults = new List<string>();
        var png = SolidPng(64, 64);

        var catalog = Encoding.UTF8.GetBytes("""
            {"success":true,"skins":[
              {"uuid":"0123456789abcdef0123456789abcdef","shortId":"abc12345","name":null,
               "texture":"abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd"},
              {"uuid":"fedcba9876543210fedcba9876543210","name":"named","variant":"slim"}
            ],"pagination":{"next":{"after":"fedcba9876543210fedcba9876543210"}}}
            """);

        var feedTransport = new FakeTransport(
            OkJson(catalog), OkPng(png), OkJson(catalog));
        var provider = new MineSkinProvider(feedTransport);

        try
        {
            var page = provider.PageAsync(null).GetAwaiter().GetResult();
            if (page.Skins.Count != 2) faults.Add($"catalog returned {page.Skins.Count} entries, not 2");
            if (!page.Skins[0].Name.StartsWith("unnamed", StringComparison.Ordinal))
                faults.Add("an unnamed public skin did not receive an honest fallback label");
            if (page.NextCursor != "fedcba9876543210fedcba9876543210")
                faults.Add("the next-page cursor was lost");
            if (!provider.Capabilities.Paging || provider.Capabilities.Search
                || provider.Capabilities.Tags || provider.Capabilities.ModelFilter)
                faults.Add("MineSkin capabilities claim features its feed does not expose");

            var preview = provider.PreviewAsync(page.Skins[0]).GetAwaiter().GetResult();
            if (preview.Skin.Size != 64 || preview.Skin.Legacy)
                faults.Add("a valid remote modern skin did not reach preview intact");
            if (feedTransport.Limits.Count < 2
                || feedTransport.Limits.Take(2).Any(limit => limit != SkinLibrary.MaximumBytes))
                faults.Add("the catalog or selected image was fetched without the 512 KiB ceiling");

            _ = provider.PageAsync(page.NextCursor).GetAwaiter().GetResult();
            if (!feedTransport.Seen.Last().Query.Contains("after=fedcba", StringComparison.Ordinal))
                faults.Add("paging did not safely carry the cursor into the next request");
        }
        catch (Exception error) { faults.Add($"the valid provider path threw: {error.Message}"); }

        ExpectFailure(faults, "timeout", new MineSkinProvider(new FakeTransport(new TaskCanceledException())),
            message => message.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        ExpectFailure(faults, "offline", new MineSkinProvider(new FakeTransport(new HttpRequestException())),
            message => message.Contains("offline", StringComparison.OrdinalIgnoreCase));
        ExpectFailure(faults, "429", new MineSkinProvider(new FakeTransport(Result(HttpStatusCode.TooManyRequests, "application/json", []))),
            message => message.Contains("rate limited", StringComparison.OrdinalIgnoreCase));
        ExpectFailure(faults, "malformed JSON", new MineSkinProvider(new FakeTransport(OkJson(Encoding.UTF8.GetBytes("{")))),
            message => message.Contains("malformed", StringComparison.OrdinalIgnoreCase));
        ExpectFailure(faults, "wrong metadata type", new MineSkinProvider(new FakeTransport(
                Result(HttpStatusCode.OK, "text/plain", catalog))),
            message => message.Contains("JSON", StringComparison.OrdinalIgnoreCase));

        try
        {
            var wrong = SolidPng(32, 32);
            var page = MineSkinProvider.ParsePage(catalog);
            var rejected = new MineSkinProvider(new FakeTransport(OkPng(wrong)));
            _ = rejected.PreviewAsync(page.Skins[0]).GetAwaiter().GetResult();
            faults.Add("a 32x32 remote image was accepted");
        }
        catch (SkinProviderException error)
        {
            if (!error.Message.Contains("rejected", StringComparison.OrdinalIgnoreCase))
                faults.Add($"a wrong-size remote image failed unclearly: {error.Message}");
        }

        try
        {
            var page = MineSkinProvider.ParsePage(catalog);
            var malformed = new MineSkinProvider(new FakeTransport(OkPng("not a PNG"u8.ToArray())));
            _ = malformed.PreviewAsync(page.Skins[0]).GetAwaiter().GetResult();
            faults.Add("malformed remote PNG bytes were accepted");
        }
        catch (SkinProviderException error)
        {
            if (!error.Message.Contains("rejected", StringComparison.OrdinalIgnoreCase))
                faults.Add($"malformed remote PNG bytes failed unclearly: {error.Message}");
        }

        try
        {
            var page = MineSkinProvider.ParsePage(catalog);
            var oversized = new MineSkinProvider(new FakeTransport(
                OkPng(new byte[SkinLibrary.MaximumBytes + 1])));
            _ = oversized.PreviewAsync(page.Skins[0]).GetAwaiter().GetResult();
            faults.Add("an oversized remote image was accepted");
        }
        catch (SkinProviderException error)
        {
            if (!error.Message.Contains("larger", StringComparison.OrdinalIgnoreCase))
                faults.Add($"an oversized remote image failed unclearly: {error.Message}");
        }

        try
        {
            var page = MineSkinProvider.ParsePage(catalog);
            var escaped = page.Skins[0] with { ImageUri = new Uri("https://example.com/skin.png") };
            _ = new MineSkinProvider(new FakeTransport()).PreviewAsync(escaped).GetAwaiter().GetResult();
            faults.Add("a remote texture on an unknown host was fetched");
        }
        catch (SkinProviderException error)
        {
            if (!error.Message.Contains("allowed HTTPS host", StringComparison.OrdinalIgnoreCase))
                faults.Add($"an unknown texture host failed unclearly: {error.Message}");
        }

        try
        {
            var lookupTransport = new FakeTransport(OkPng(png, new Dictionary<string, string>
            {
                ["X-Skin-Model"] = "slim",
            }));
            var looked = new PlayerSkinLookup(lookupTransport).LookupAsync("Player_1").GetAwaiter().GetResult();
            if (looked.Skin.Arms != ArmStyle.Slim) faults.Add("player lookup lost the provider's slim-model hint");
            if (!lookupTransport.Seen.Single().AbsolutePath.EndsWith("/Player_1", StringComparison.Ordinal))
                faults.Add("the player name did not occupy exactly one encoded path segment");
            var uuid = "01234567-89ab-cdef-0123-456789abcdef";
            if (!PlayerSkinLookup.PlayerUri(uuid).AbsolutePath.EndsWith("/" + uuid, StringComparison.Ordinal))
                faults.Add("a UUID did not occupy exactly one player-lookup path segment");
            try { _ = PlayerSkinLookup.PlayerUri("name/../../elsewhere"); faults.Add("an unsafe player path was accepted"); }
            catch (SkinProviderException) { }
        }
        catch (Exception error) { faults.Add($"player lookup threw: {error.Message}"); }

        detail = "paged and unnamed MineSkin entries, strict JSON/PNG and host validation, safe player lookup "
               + "with model hint, and timeout/offline/429/malformed controls all exercised without network";
        return faults;
    }

    private static void ExpectFailure(
        List<string> faults, string label, MineSkinProvider provider, Func<string, bool> correct)
    {
        try { _ = provider.PageAsync(null).GetAwaiter().GetResult(); faults.Add($"the {label} control succeeded"); }
        catch (SkinProviderException error)
        {
            if (!correct(error.Message)) faults.Add($"the {label} control said '{error.Message}'");
        }
    }

    private static SkinHttpResult OkJson(byte[] body) => Result(HttpStatusCode.OK, "application/json", body);
    private static SkinHttpResult OkPng(byte[] body, IReadOnlyDictionary<string, string>? headers = null) =>
        Result(HttpStatusCode.OK, "image/png", body, headers);

    private static SkinHttpResult Result(
        HttpStatusCode status, string type, byte[] body,
        IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, type, body, new Uri(type == "image/png"
            ? "https://textures.minecraft.net/texture/test"
            : "https://api.mineskin.org/v2/skins"),
            headers ?? new Dictionary<string, string>());

    private static byte[] SolidPng(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 96; pixels[i + 1] = 132; pixels[i + 2] = 78; pixels[i + 3] = 255;
        }
        return Png.Encode(new Image(width, height, pixels));
    }

    private sealed class FakeTransport : ISkinTransport
    {
        private readonly Queue<object> _answers;
        public readonly List<Uri> Seen = [];
        public readonly List<int> Limits = [];

        public FakeTransport(params object[] answers) => _answers = new Queue<object>(answers);

        public Task<SkinHttpResult> GetAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
        {
            Seen.Add(uri);
            Limits.Add(maximumBytes);
            if (_answers.Count == 0) throw new InvalidOperationException("fake provider ran out of answers");
            var answer = _answers.Dequeue();
            if (answer is Exception error) return Task.FromException<SkinHttpResult>(error);

            var result = (SkinHttpResult)answer;
            if (result.Body.Length > maximumBytes)
                throw new SkinProviderException(
                    $"the provider response was larger than {maximumBytes / 1024} KiB");
            // Player lookup has a different allowlisted host from a MineSkin texture. Preserve the
            // requested URI for that fake while retaining all of the response's other fields.
            if (uri.Host == "api.mcskin.me") result = result with { FinalUri = uri };
            return Task.FromResult(result);
        }
    }
}
