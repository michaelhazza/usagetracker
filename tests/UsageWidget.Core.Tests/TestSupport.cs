using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UsageWidget.Core.Config;
using UsageWidget.Core.Net;
using UsageWidget.Core.Templating;

namespace UsageWidget.Core.Tests;

/// <summary>Loads the normalized §0 fixtures that ship under tests/fixtures.</summary>
internal static class Fixtures
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "fixtures");

    public static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Root }.Concat(parts).ToArray()));

    public static IEnumerable<string> AllFiles() =>
        Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories);

    public static RequestTemplate LoadTemplate(string area) =>
        JsonConvert.DeserializeObject<RequestTemplate>(
            Read(area, "request.json"), ConfigStore.SerializerSettings)
        ?? throw new InvalidOperationException($"Could not load template for '{area}'.");
}

/// <summary>Test double for <see cref="IHttpSender"/> that records what it was asked to send.</summary>
internal sealed class FakeSender : IHttpSender
{
    private readonly HttpResponseData? _response;
    private readonly Exception? _throw;

    public FakeSender(HttpResponseData response) => _response = response;
    public FakeSender(Exception toThrow) => _throw = toThrow;

    public bool WasCalled { get; private set; }
    public Uri? LastUrl { get; private set; }
    public IReadOnlyDictionary<string, string>? LastHeaders { get; private set; }
    public string? LastBody { get; private set; }
    public bool LastFollowRedirects { get; private set; }

    public Task<HttpResponseData> SendAsync(
        string method, Uri url, IReadOnlyDictionary<string, string> headers, string? body,
        bool followRedirects, TimeSpan timeout, CancellationToken ct)
    {
        WasCalled = true;
        LastUrl = url;
        LastHeaders = headers;
        LastBody = body;
        LastFollowRedirects = followRedirects;

        if (_throw is not null) throw _throw;
        return Task.FromResult(_response!);
    }

    public static HttpResponseData Json(string body, int status = 200) =>
        new(status, "application/json", body, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public static HttpResponseData Html(string body, int status = 403) =>
        new(status, "text/html", body, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public static HttpResponseData WithHeaders(IReadOnlyDictionary<string, string> headers, int status = 200) =>
        new(status, "application/json", "{}", headers);
}

/// <summary>
/// The merge-gating fixture-safety scanner (§12). Flags committed fixtures that contain obvious
/// secrets/identifiers. Reserved example/test email domains (RFC 2606) are treated as safe redactions.
/// </summary>
internal static class FixtureSafety
{
    private static readonly string[] ReservedEmailDomains =
        { "example.com", "example.org", "example.net", "example.edu", "example.invalid", "test", "localhost" };

    private static readonly (string Label, Regex Rx)[] Detectors =
    {
        ("bearer token", new Regex(@"(?i)Bearer\s+[A-Za-z0-9._\-]{16,}", RegexOptions.Compiled)),
        ("api key", new Regex(@"\bsk-[A-Za-z0-9]{12,}", RegexOptions.Compiled)),
        ("jwt", new Regex(@"\beyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+", RegexOptions.Compiled)),
        ("token field", new Regex(
            @"(?i)""(access_token|refresh_token|sessionKey|session_id|client_secret)""\s*:\s*""(?!\{\{)[^""]{6,}""",
            RegexOptions.Compiled)),
        ("id field", new Regex(
            @"(?i)""(org_id|orgId|account_id|accountId)""\s*:\s*""(?!\{\{)(?!REDACTED)[A-Za-z0-9\-]{8,}""",
            RegexOptions.Compiled)),
    };

    private static readonly Regex EmailRx =
        new(@"[A-Za-z0-9._%+\-]+@([A-Za-z0-9.\-]+\.[A-Za-z]{2,})", RegexOptions.Compiled);

    public static IReadOnlyList<string> Scan(string text)
    {
        var findings = new List<string>();

        foreach (var (label, rx) in Detectors)
        {
            if (rx.IsMatch(text)) findings.Add(label);
        }

        foreach (Match m in EmailRx.Matches(text))
        {
            var domain = m.Groups[1].Value;
            if (!ReservedEmailDomains.Any(d => domain.Equals(d, StringComparison.OrdinalIgnoreCase)))
            {
                findings.Add($"email ({domain})");
            }
        }

        return findings;
    }
}
