namespace UsageWidget.Core.Security;

/// <summary>
/// Contract #4. Per-adapter hostname allowlist that fails closed: an empty list allows nothing,
/// and any host not explicitly listed is rejected. Matching is case-insensitive and exact-host
/// (no implicit subdomain wildcards) unless an entry is written as a leading-dot suffix.
/// </summary>
public static class HostnameAllowlist
{
    /// <summary>True only if <paramref name="uri"/>'s host is explicitly permitted.</summary>
    public static bool IsAllowed(Uri uri, IReadOnlyCollection<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (allowedHosts is null || allowedHosts.Count == 0) return false; // fail closed

        var host = uri.Host;
        foreach (var entry in allowedHosts)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var allowed = entry.Trim();

            // ".example.com" => match the domain and any subdomain.
            if (allowed.StartsWith('.'))
            {
                var suffix = allowed; // includes leading dot
                if (host.Equals(allowed.TrimStart('.'), StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (host.Equals(allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Convenience overload for a raw URL string. Unparseable URLs fail closed.</summary>
    public static bool IsAllowed(string url, IReadOnlyCollection<string> allowedHosts)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAllowed(uri, allowedHosts);
    }
}
