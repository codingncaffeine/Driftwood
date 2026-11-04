namespace Driftwood.Net.Eos;

/// <summary>The game's Epic product credentials, from the Developer Portal.</summary>
public sealed record EosConfig(string ProductId, string SandboxId, string DeploymentId, string ClientId, string ClientSecret)
{
    /// <summary>
    /// Reads <c>eos.txt</c>. Takes either <c>key=value</c> lines (product_id, sandbox_id,
    /// deployment_id, client_id, client_secret) or the portal's labels copied as "label: value"
    /// (product ID, sandbox ID, deployment ID, client: id, ... client secret: secret).
    /// </summary>
    /// <returns>Null, with what is missing, when any of the five is not there.</returns>
    public static EosConfig? Read(string path, out string why)
    {
        string? product = null, sandbox = null, deployment = null, client = null, secret = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var eq = line.IndexOf('=');
            if (eq > 0)
            {
                var value = Unwrap(line[(eq + 1)..]);
                switch (line[..eq].Trim().ToLowerInvariant())
                {
                    case "product_id": product = value; break;
                    case "sandbox_id": sandbox = value; break;
                    case "deployment_id": deployment = value; break;
                    case "client_id": client = value; break;
                    case "client_secret": secret = value; break;
                }
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            // ⚠ "BPT: client secret: …" has a label in front of the label: the value is after the LAST colon.
            var loose = Unwrap(line[(line.LastIndexOf(':') + 1)..]);
            var label = line[..line.LastIndexOf(':')].ToLowerInvariant();
            if (loose.Length == 0) continue;
            if (label.Contains("secret")) secret = loose;
            else if (label.Contains("product")) product = loose;
            else if (label.Contains("sandbox")) sandbox = loose;
            else if (label.Contains("deployment")) deployment = loose;
            // "Client: Driftwood" is the client's NAME; the id is the one that looks like an id.
            else if (label.Contains("client") && LooksLikeId(loose)) client = loose;
        }

        var missing = new List<string>();
        if (product is null) missing.Add("product id");
        if (sandbox is null) missing.Add("sandbox id");
        if (deployment is null) missing.Add("deployment id");
        if (client is null) missing.Add("client id");
        if (secret is null) missing.Add("client secret");
        why = missing.Count == 0 ? "" : "eos.txt has no " + string.Join(", ", missing);
        return missing.Count == 0 ? new EosConfig(product!, sandbox!, deployment!, client!, secret!) : null;
    }

    private static bool LooksLikeId(string value) => value.Length >= 24 && value.All(char.IsAsciiLetterOrDigit);

    /// <summary>One surrounding pair of brackets or quotes from a pasted placeholder.</summary>
    private static string Unwrap(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && (value[0] == '<' && value[^1] == '>' || value[0] == '"' && value[^1] == '"')
            ? value[1..^1].Trim()
            : value;
    }
}
