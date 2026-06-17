using System.Globalization;
using Newtonsoft.Json.Linq;
using UsageWidget.Core.Model;
using UsageWidget.Core.Time;

namespace UsageWidget.Core.Templating;

/// <summary>
/// Resolves a <see cref="MappingConfig"/> against a provider response (JSON body + headers) into
/// the normalized <see cref="UsageWindow"/>s. Uses Newtonsoft JSONPath (<c>SelectToken</c>); a path
/// prefixed with <c>header:</c> reads a response header instead of the body. The Anthropic API
/// fallback returns utilization as headers, which this supports without special-casing.
/// </summary>
public sealed class MappingEngine
{
    private const string HeaderPrefix = "header:";

    public sealed record Extracted(UsageWindow? Session, UsageWindow? Weekly, string? Identity);

    /// <summary>
    /// Parse <paramref name="body"/> and resolve the mappings. Throws <see cref="MappingException"/>
    /// if the body is not valid JSON (callers treat that as ParseFailed unless it's a challenge).
    /// </summary>
    public Extracted Extract(
        string body,
        IReadOnlyDictionary<string, string> headers,
        MappingConfig map,
        DateTimeOffset now)
    {
        JToken root;
        try
        {
            root = string.IsNullOrWhiteSpace(body) ? new JObject() : JToken.Parse(body);
        }
        catch (Exception ex)
        {
            throw new MappingException("Response body was not valid JSON.", ex);
        }

        var session = BuildWindow(
            root, headers,
            map.SessionUsed, map.SessionLimit, map.SessionPct, map.SessionReset, map.SessionResetKind, now);

        var weekly = BuildWindow(
            root, headers,
            map.WeeklyUsed, map.WeeklyLimit, map.WeeklyPct, map.WeeklyReset, map.WeeklyResetKind, now);

        var identity = ReadString(root, headers, map.Identity);

        return new Extracted(session, weekly, identity);
    }

    private static UsageWindow? BuildWindow(
        JToken root,
        IReadOnlyDictionary<string, string> headers,
        string? usedPath,
        string? limitPath,
        string? pctPath,
        string? resetPath,
        ResetKind resetKind,
        DateTimeOffset now)
    {
        var used = ReadLong(root, headers, usedPath);
        var limit = ReadLong(root, headers, limitPath);
        var pct = ReadDouble(root, headers, pctPath);

        DateTimeOffset? resetAt = null;
        var resetRaw = ReadString(root, headers, resetPath);
        if (resetRaw is not null)
        {
            resetAt = resetKind == ResetKind.DurationSeconds
                ? (double.TryParse(resetRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var secs)
                    ? TimeMath.ResetFromDurationSeconds(secs, now)
                    : null)
                : TimeMath.ParseResetTimestamp(resetRaw);
        }

        // If nothing for this window resolved at all, return null so §3 validation can see "no window".
        if (used is null && limit is null && pct is null && resetAt is null) return null;

        return UsageWindow.Create(used, limit, pct, resetAt);
    }

    private static JToken? Resolve(JToken root, string path)
    {
        // SelectToken with errorWhenNoMatch:false returns null for a missing path.
        return root.SelectToken(path, errorWhenNoMatch: false);
    }

    private static string? ReadString(
        JToken root, IReadOnlyDictionary<string, string> headers, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (path.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = path[HeaderPrefix.Length..].Trim();
            return headers.TryGetValue(name, out var v) ? v : null;
        }

        var token = Resolve(root, path);
        return token?.Type switch
        {
            null => null,
            JTokenType.Null => null,
            _ => token.ToString(),
        };
    }

    private static long? ReadLong(
        JToken root, IReadOnlyDictionary<string, string> headers, string? path)
    {
        var raw = ReadString(root, headers, path);
        if (raw is null) return null;
        return long.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v
            : double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                ? (long)Math.Round(d)
                : null;
    }

    private static double? ReadDouble(
        JToken root, IReadOnlyDictionary<string, string> headers, string? path)
    {
        var raw = ReadString(root, headers, path);
        if (raw is null) return null;
        return double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}

/// <summary>Thrown when a response body cannot be parsed as JSON for mapping.</summary>
public sealed class MappingException : Exception
{
    public MappingException(string message, Exception? inner = null) : base(message, inner) { }
}
