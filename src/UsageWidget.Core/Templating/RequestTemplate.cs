namespace UsageWidget.Core.Templating;

/// <summary>
/// A fully config-driven description of the HTTP request to replay for one source type (§4).
/// Stored as plaintext in adapter-config.json — it must NEVER contain a real secret, only the
/// <see cref="TokenPlaceholder"/>, which is substituted from the secret store at send time.
/// </summary>
public sealed class RequestTemplate
{
    public const string TokenPlaceholder = "{{TOKEN}}";

    /// <summary>
    /// Header names that carry credentials, in priority order (cookie first — claude.ai session
    /// auth). Shared by the importer (which extracts these values into the secret store) and the
    /// re-paste guard (which must know when a template needs more than one credential).
    /// </summary>
    public static readonly string[] CredentialHeaderNames =
        { "cookie", "authorization", "x-api-key", "anthropic-api-key" };

    public static bool IsCredentialHeader(string name) =>
        CredentialHeaderNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many credential headers still carry the <see cref="TokenPlaceholder"/>. More than one
    /// means the stored secret must be a multi-credential envelope — a bare pasted token would be
    /// injected verbatim into EVERY one of these headers and break the account.
    /// (A method, not a property, so Newtonsoft never serializes it into the config file.)
    /// </summary>
    public int CountCredentialPlaceholders() =>
        Headers.Count(h => IsCredentialHeader(h.Key) &&
                           h.Value.Contains(TokenPlaceholder, StringComparison.Ordinal));

    public string Url { get; set; } = "";
    public string Method { get; set; } = "GET";

    /// <summary>Replay the FULL captured header set (R1), using {{TOKEN}} where the secret goes.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Body { get; set; }

    /// <summary>Contract #4 allowlist for this template. Fail-closed when empty.</summary>
    public List<string> AllowedHosts { get; set; } = new();

    /// <summary>Contract #4 / v3.1: redirects are off by default. Each enabled target is re-validated.</summary>
    public bool FollowRedirects { get; set; }

    public MappingConfig Mappings { get; set; } = new();

    /// <summary>True if this template still contains FILL ME IN scaffolding rather than a real endpoint.</summary>
    public bool IsPlaceholder =>
        string.IsNullOrWhiteSpace(Url) ||
        Url.Contains("FILL", StringComparison.OrdinalIgnoreCase) ||
        Url.Contains("ME IN", StringComparison.OrdinalIgnoreCase);

    public RequestTemplate Clone() => new()
    {
        Url = Url,
        Method = Method,
        Headers = new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase),
        Body = Body,
        AllowedHosts = new List<string>(AllowedHosts),
        FollowRedirects = FollowRedirects,
        Mappings = Mappings,
    };
}
