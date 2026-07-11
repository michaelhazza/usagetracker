using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UsageWidget.Core.Templating;

/// <summary>
/// Substitutes the <see cref="RequestTemplate.TokenPlaceholder"/> with the real secret. Contract #3:
/// the secret is injected here, in-memory, at send time — it is never written back to the template.
/// Contract #4 / v3.1: the pipeline calls this ONLY after the destination host has cleared the
/// allowlist, so a copied credential can never leak to an unexpected host.
///
/// Secrets come in two shapes:
///   - a plain value (one credential) — replaces the placeholder wherever it appears;
///   - a <see cref="MultiSecret"/> envelope (a capture that carried BOTH a Cookie and an
///     Authorization header, e.g. Codex behind Cloudflare) — each credential header receives its
///     own value, keyed by header name. A placeholder with no matching credential is dropped
///     entirely rather than sent as a literal.
/// </summary>
public static class TemplateEngine
{
    public static IReadOnlyDictionary<string, string> InjectHeaders(
        IReadOnlyDictionary<string, string> headers, string secret)
    {
        var multi = MultiSecret.TryParse(secret);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            if (multi is null)
            {
                result[name] = Inject(value, secret);
                continue;
            }

            if (!value.Contains(RequestTemplate.TokenPlaceholder, StringComparison.Ordinal))
            {
                result[name] = value;
            }
            else if (multi.TryGetValue(name, out var credential))
            {
                result[name] = Inject(value, credential);
            }
            // else: credential header with no stored value — never send the literal placeholder.
        }

        return result;
    }

    public static string? InjectBody(string? body, string secret)
    {
        if (body is null) return null;

        // Multi-credential secrets are header-scoped; a body placeholder (never produced by the
        // importer for these captures) is blanked rather than sent as a literal marker.
        return MultiSecret.TryParse(secret) is not null
            ? body.Replace(RequestTemplate.TokenPlaceholder, "", StringComparison.Ordinal)
            : Inject(body, secret);
    }

    private static string Inject(string value, string secret) =>
        value.Replace(RequestTemplate.TokenPlaceholder, Sanitize(secret), StringComparison.Ordinal);

    /// <summary>
    /// Pasted tokens routinely arrive with a trailing newline or soft line-wraps from the copy
    /// source. A CR/LF inside a header value makes every send throw (header injection guard) —
    /// which then surfaces as a baffling "network error" — so strip them at the single choke point.
    /// </summary>
    internal static string Sanitize(string secret) =>
        secret.Replace("\r", "", StringComparison.Ordinal)
              .Replace("\n", "", StringComparison.Ordinal)
              .Trim();
}

/// <summary>
/// Envelope for accounts whose capture carried more than one credential header. Serialized as a
/// single opaque secret so the store keeps exactly one entry per account and nothing credential-
/// shaped ever lands in the plaintext template (contract #3).
/// </summary>
public static class MultiSecret
{
    private const string MarkerKey = "__uwMultiSecret";

    public static string Serialize(IReadOnlyDictionary<string, string> credentialsByHeader)
    {
        var root = new JObject { [MarkerKey] = 1 };
        foreach (var (header, value) in credentialsByHeader)
        {
            root[header.ToLowerInvariant()] = value;
        }

        return root.ToString(Formatting.None);
    }

    /// <summary>Returns the credential map (header name → value) or null if this is a plain secret.</summary>
    public static IReadOnlyDictionary<string, string>? TryParse(string secret)
    {
        var trimmed = secret.TrimStart();
        if (!trimmed.StartsWith('{') || !trimmed.Contains(MarkerKey, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var root = JObject.Parse(secret);
            if (root[MarkerKey] is null) return null;

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in root.Properties())
            {
                if (prop.Name == MarkerKey || prop.Value.Type != JTokenType.String) continue;
                result[prop.Name] = prop.Value.Value<string>()!;
            }

            return result;
        }
        catch (JsonException)
        {
            return null; // a plain secret that merely resembles JSON
        }
    }
}
