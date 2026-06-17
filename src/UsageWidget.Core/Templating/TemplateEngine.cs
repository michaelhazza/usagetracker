namespace UsageWidget.Core.Templating;

/// <summary>
/// Substitutes the <see cref="RequestTemplate.TokenPlaceholder"/> with the real secret. Contract #3:
/// the secret is injected here, in-memory, at send time — it is never written back to the template.
/// Contract #4 / v3.1: the pipeline calls this ONLY after the destination host has cleared the
/// allowlist, so a copied credential can never leak to an unexpected host.
/// </summary>
public static class TemplateEngine
{
    public static IReadOnlyDictionary<string, string> InjectHeaders(
        IReadOnlyDictionary<string, string> headers, string secret)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            result[name] = Inject(value, secret);
        }

        return result;
    }

    public static string? InjectBody(string? body, string secret) =>
        body is null ? null : Inject(body, secret);

    private static string Inject(string value, string secret) =>
        value.Replace(RequestTemplate.TokenPlaceholder, secret, StringComparison.Ordinal);
}
