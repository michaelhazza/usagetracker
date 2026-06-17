using Newtonsoft.Json.Linq;
using UsageWidget.Core.Security;
using UsageWidget.Core.Templating;

namespace UsageWidget.Core.Config;

public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ValidationResult Ok { get; } = new(true, Array.Empty<string>());

    public static ValidationResult Fail(IEnumerable<string> errors)
    {
        var list = errors.ToList();
        return new ValidationResult(list.Count == 0, list);
    }
}

/// <summary>
/// §4 "validate before save". The Request Template editor refuses to persist a template until all of
/// these hold, so a broken or secret-bearing template never reaches disk.
/// </summary>
public static class TemplateValidator
{
    private static readonly HashSet<string> SupportedMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "PATCH", "DELETE" };

    // Header names where a {{TOKEN}} placeholder is legitimately expected.
    private static readonly HashSet<string> AuthHeaderNames =
        new(StringComparer.OrdinalIgnoreCase) { "Authorization", "Cookie", "X-Api-Key", "Anthropic-Api-Key" };

    public static ValidationResult Validate(RequestTemplate template)
    {
        var errors = new List<string>();

        // URL parses.
        if (!Uri.TryCreate(template.Url, UriKind.Absolute, out var uri))
        {
            errors.Add("URL is not a valid absolute URL.");
        }
        else if (HostnameAllowlist.IsAllowed(uri, template.AllowedHosts) is false)
        {
            // Hostname must be covered by the template's own allowlist.
            errors.Add($"URL host '{uri.Host}' is not covered by AllowedHosts.");
        }

        // Method supported.
        if (!SupportedMethods.Contains(template.Method))
        {
            errors.Add($"HTTP method '{template.Method}' is not supported.");
        }

        // {{TOKEN}} only in approved auth/header fields (or body); never in the URL.
        if (template.Url.Contains(RequestTemplate.TokenPlaceholder, StringComparison.Ordinal))
        {
            errors.Add("Token placeholder must not appear in the URL.");
        }

        foreach (var (name, value) in template.Headers)
        {
            if (value.Contains(RequestTemplate.TokenPlaceholder, StringComparison.Ordinal) &&
                !AuthHeaderNames.Contains(name))
            {
                errors.Add($"Token placeholder is only allowed in auth headers, not '{name}'.");
            }
        }

        // No REAL secret may be present — only the placeholder.
        foreach (var (name, value) in template.Headers)
        {
            if (LooksLikeRealSecret(name, value))
            {
                errors.Add($"Header '{name}' appears to contain a real secret; use {RequestTemplate.TokenPlaceholder}.");
            }
        }

        // Mappings syntactically valid JSONPath (header: mappings are always fine).
        foreach (var (field, path) in EnumerateMappingPaths(template.Mappings))
        {
            if (!IsValidPath(path))
            {
                errors.Add($"Mapping '{field}' is not a valid JSONPath expression.");
            }
        }

        return errors.Count == 0 ? ValidationResult.Ok : ValidationResult.Fail(errors);
    }

    private static bool LooksLikeRealSecret(string name, string value)
    {
        if (value.Contains(RequestTemplate.TokenPlaceholder, StringComparison.Ordinal)) return false;
        if (!AuthHeaderNames.Contains(name)) return false;
        // An auth header that carries a non-placeholder, non-trivial value is suspect.
        var trimmed = value.Trim();
        return trimmed.Length >= 12 &&
               (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("sk-", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains('=') ||
                trimmed.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'));
    }

    private static IEnumerable<(string Field, string Path)> EnumerateMappingPaths(MappingConfig m)
    {
        foreach (var pair in new (string, string?)[]
                 {
                     (nameof(m.SessionPct), m.SessionPct),
                     (nameof(m.SessionUsed), m.SessionUsed),
                     (nameof(m.SessionLimit), m.SessionLimit),
                     (nameof(m.SessionReset), m.SessionReset),
                     (nameof(m.WeeklyPct), m.WeeklyPct),
                     (nameof(m.WeeklyUsed), m.WeeklyUsed),
                     (nameof(m.WeeklyLimit), m.WeeklyLimit),
                     (nameof(m.WeeklyReset), m.WeeklyReset),
                     (nameof(m.Identity), m.Identity),
                 })
        {
            if (!string.IsNullOrWhiteSpace(pair.Item2)) yield return (pair.Item1, pair.Item2!);
        }
    }

    private static bool IsValidPath(string path)
    {
        if (path.StartsWith("header:", StringComparison.OrdinalIgnoreCase))
        {
            return path.Length > "header:".Length;
        }

        try
        {
            // JObject.SelectToken validates JSONPath syntax; an empty doc just yields no match.
            new JObject().SelectToken(path, errorWhenNoMatch: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
