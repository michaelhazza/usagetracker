namespace UsageWidget.Core.Templating;

/// <summary>How a reset value is expressed in the provider response.</summary>
public enum ResetKind
{
    /// <summary>An absolute ISO-8601 timestamp (parsed as UTC).</summary>
    Timestamp,

    /// <summary>A number of seconds from "now"; converted to an absolute time at refresh (contract #6).</summary>
    DurationSeconds,
}

/// <summary>
/// JSONPath (or header-name) mappings that pull normalized fields out of a provider response.
/// Any field may be null/absent; §3 validation only requires that at least one window resolves.
/// A path beginning with "header:" reads a response header instead of the JSON body.
/// </summary>
public sealed class MappingConfig
{
    public string? SessionPct { get; set; }
    public string? SessionUsed { get; set; }
    public string? SessionLimit { get; set; }
    public string? SessionReset { get; set; }
    public ResetKind SessionResetKind { get; set; } = ResetKind.Timestamp;

    public string? WeeklyPct { get; set; }
    public string? WeeklyUsed { get; set; }
    public string? WeeklyLimit { get; set; }
    public string? WeeklyReset { get; set; }
    public ResetKind WeeklyResetKind { get; set; } = ResetKind.Timestamp;

    /// <summary>Optional identity (email/org) for the row label, when the response exposes it.</summary>
    public string? Identity { get; set; }
}
