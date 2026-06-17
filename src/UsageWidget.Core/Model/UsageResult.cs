namespace UsageWidget.Core.Model;

/// <summary>
/// Normalized adapter result (§3). A discriminated success/failure — on failure it carries a
/// taxonomy <see cref="RefreshErrorKind"/> plus an already-redacted message, never a raw body.
/// </summary>
public sealed record UsageResult
{
    public bool IsSuccess { get; private init; }
    public string AccountLabel { get; private init; } = "";
    public UsageWindow? Session { get; private init; }
    public UsageWindow? Weekly { get; private init; }
    public DateTimeOffset FetchedAt { get; private init; }

    public RefreshErrorKind? ErrorKind { get; private init; }

    /// <summary>Already-redacted, user-safe message. Never contains secrets or raw bodies (contract #7).</summary>
    public string? ErrorMessage { get; private init; }

    public static UsageResult Success(
        string accountLabel,
        UsageWindow? session,
        UsageWindow? weekly,
        DateTimeOffset fetchedAt)
    {
        return new UsageResult
        {
            IsSuccess = true,
            AccountLabel = accountLabel,
            Session = session,
            Weekly = weekly,
            FetchedAt = fetchedAt,
        };
    }

    public static UsageResult Failure(
        RefreshErrorKind kind,
        string accountLabel,
        string? redactedMessage,
        DateTimeOffset fetchedAt)
    {
        return new UsageResult
        {
            IsSuccess = false,
            AccountLabel = accountLabel,
            ErrorKind = kind,
            ErrorMessage = redactedMessage,
            FetchedAt = fetchedAt,
        };
    }

    /// <summary>
    /// §3 validation: a result is usable only if at least one window resolved data. Used both to
    /// validate freshly-added accounts and to downgrade an all-empty 200 to <see cref="RefreshErrorKind.ParseFailed"/>.
    /// </summary>
    public bool HasUsableData => (Session?.HasData ?? false) || (Weekly?.HasData ?? false);
}
