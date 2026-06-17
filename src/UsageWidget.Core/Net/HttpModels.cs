namespace UsageWidget.Core.Net;

/// <summary>A provider response, transport-neutral so the pipeline is unit-testable with fakes.</summary>
public sealed record HttpResponseData(
    int StatusCode,
    string ContentType,
    string Body,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Abstraction over the actual HTTP transport. Implementations must honor <paramref name="timeout"/>
/// and <paramref name="followRedirects"/>; tests substitute a fake. Note: credentials are already
/// injected into <paramref name="headers"/> by the pipeline AFTER the allowlist check (contract #4).
/// </summary>
public interface IHttpSender
{
    Task<HttpResponseData> SendAsync(
        string method,
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        bool followRedirects,
        TimeSpan timeout,
        CancellationToken ct);
}

/// <summary>
/// Thrown when a request targets a host outside the adapter's allowlist (contract #4, fail closed).
/// Raised BEFORE any credential is injected or any bytes are sent.
/// </summary>
public sealed class HostNotAllowedException : Exception
{
    public HostNotAllowedException(string host)
        : base($"Host '{host}' is not in the adapter allowlist.") => Host = host;

    public string Host { get; }
}
