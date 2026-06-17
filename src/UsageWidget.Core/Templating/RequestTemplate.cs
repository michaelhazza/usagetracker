namespace UsageWidget.Core.Templating;

/// <summary>
/// A fully config-driven description of the HTTP request to replay for one source type (§4).
/// Stored as plaintext in adapter-config.json — it must NEVER contain a real secret, only the
/// <see cref="TokenPlaceholder"/>, which is substituted from the secret store at send time.
/// </summary>
public sealed class RequestTemplate
{
    public const string TokenPlaceholder = "{{TOKEN}}";

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
