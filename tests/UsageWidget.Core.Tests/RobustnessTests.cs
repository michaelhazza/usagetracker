using Newtonsoft.Json.Linq;
using UsageWidget.Core.Accounts;
using UsageWidget.Core.Adapters;
using UsageWidget.Core.Config;
using UsageWidget.Core.Model;
using UsageWidget.Core.Net;
using UsageWidget.Core.Polling;
using UsageWidget.Core.Secrets;
using UsageWidget.Core.Time;
using Xunit;

namespace UsageWidget.Core.Tests;

/// <summary>
/// An <see cref="IHttpSender"/> whose calls resolve to a scripted sequence of responses/exceptions,
/// for exercising the retry decorator and polling paths.
/// </summary>
internal sealed class SequenceSender : IHttpSender
{
    private readonly Queue<object> _script;

    public SequenceSender(params object[] script) => _script = new Queue<object>(script);

    public int Calls { get; private set; }

    public Task<HttpResponseData> SendAsync(
        string method, Uri url, IReadOnlyDictionary<string, string> headers, string? body,
        bool followRedirects, TimeSpan timeout, CancellationToken ct)
    {
        Calls++;
        var next = _script.Dequeue();
        if (next is Exception ex) throw ex;
        return Task.FromResult((HttpResponseData)next);
    }
}

public class RetryingHttpSenderTests
{
    private static readonly Uri Url = new("https://claude.ai/api/usage");
    private static readonly Dictionary<string, string> NoHeaders = new();

    private static Task<HttpResponseData> Send(RetryingHttpSender sender, CancellationToken ct = default) =>
        sender.SendAsync("GET", Url, NoHeaders, null, false, TimeSpan.FromSeconds(10), ct);

    private static RetryingHttpSender NoDelay(IHttpSender inner, int attempts = 2) =>
        new(inner, attempts, delay: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task Retries_thrown_transport_fault_and_succeeds()
    {
        var inner = new SequenceSender(new HttpRequestException("reset"), FakeSender.Json("{}"));
        var response = await Send(NoDelay(inner));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Retries_retryable_5xx_and_succeeds()
    {
        var inner = new SequenceSender(FakeSender.Json("busy", 503), FakeSender.Json("{}"));
        var response = await Send(NoDelay(inner));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(429)] // backoff owns rate limits — an instant retry would make them worse
    public async Task Real_answers_are_never_retried(int status)
    {
        var inner = new SequenceSender(FakeSender.Json("{}", status));
        var response = await Send(NoDelay(inner));

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Challenge_responses_are_not_retried_even_on_retryable_status()
    {
        var challenge = FakeSender.Html(Fixtures.Read("challenge", "cloudflare.html"), 503);
        var inner = new SequenceSender(challenge);
        var response = await Send(NoDelay(inner));

        Assert.Equal(503, response.StatusCode);
        Assert.Equal(1, inner.Calls); // hammering a challenge escalates the block
    }

    [Fact]
    public async Task Gives_up_after_max_attempts_and_surfaces_last_failure()
    {
        var inner = new SequenceSender(
            new HttpRequestException("one"), new HttpRequestException("two"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Send(NoDelay(inner)));
        Assert.Equal("two", ex.Message);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Exhausted_retries_return_the_last_retryable_response()
    {
        var inner = new SequenceSender(FakeSender.Json("a", 502), FakeSender.Json("b", 504));
        var response = await Send(NoDelay(inner));

        Assert.Equal(504, response.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_immediately_without_retry()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var inner = new SequenceSender(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Send(NoDelay(inner), cts.Token));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Waits_between_attempts_using_the_backoff_schedule()
    {
        var delays = new List<TimeSpan>();
        var inner = new SequenceSender(new HttpRequestException("x"), FakeSender.Json("{}"));
        var sender = new RetryingHttpSender(
            inner,
            attempts: 2,
            backoff: attempt => TimeSpan.FromMilliseconds(100 * (attempt + 1)),
            delay: (wait, _) => { delays.Add(wait); return Task.CompletedTask; });

        await Send(sender);

        Assert.Equal(new[] { TimeSpan.FromMilliseconds(100) }, delays);
    }
}

public class HttpRequestConstructionTests
{
    private static readonly Uri Url = new("https://claude.ai/api/usage");

    [Fact]
    public void Transport_headers_from_a_capture_are_stripped()
    {
        // A browser capture advertises encodings .NET can't decode (zstd) and carries
        // connection-scoped headers; replaying them corrupts the server-side request.
        var headers = new Dictionary<string, string>
        {
            ["Accept-Encoding"] = "gzip, deflate, br, zstd",
            ["Host"] = "claude.ai",
            ["Content-Length"] = "42",
            ["Connection"] = "keep-alive",
            ["Expect"] = "100-continue",
            ["Proxy-Authorization"] = "Basic abc",
            ["User-Agent"] = "Mozilla/5.0",
            ["Cookie"] = "sessionKey=abc",
        };

        using var request = HttpClientSender.BuildRequest("GET", Url, headers, null);

        Assert.False(request.Headers.Contains("Accept-Encoding"));
        Assert.Null(request.Headers.Host);
        Assert.False(request.Headers.Contains("Connection"));
        Assert.False(request.Headers.Contains("Expect"));
        Assert.False(request.Headers.Contains("Proxy-Authorization"));
        Assert.Equal("Mozilla/5.0", string.Join("", request.Headers.GetValues("User-Agent")));
        Assert.Equal("sessionKey=abc", string.Join("", request.Headers.GetValues("Cookie")));
    }

    [Fact]
    public void Replayed_content_type_replaces_the_default_instead_of_stacking()
    {
        var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };

        using var request = HttpClientSender.BuildRequest("POST", Url, headers, "{\"a\":1}");

        var contentType = Assert.Single(request.Content!.Headers.GetValues("Content-Type"));
        Assert.Equal("application/json", contentType);
    }

    [Fact]
    public void Requests_prefer_http2_with_downgrade()
    {
        using var request = HttpClientSender.BuildRequest("GET", Url, new Dictionary<string, string>(), null);

        Assert.Equal(new Version(2, 0), request.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, request.VersionPolicy);
    }
}

public class ResponseClassifierTests
{
    private static Classification? Classify(int status, string body = "{}", string contentType = "application/json") =>
        ResponseClassifier.Classify(new HttpResponseData(
            status, contentType, body, new Dictionary<string, string>()));

    [Fact]
    public void Ok_passes_through_to_mapping() => Assert.Null(Classify(200));

    [Fact]
    public void Proxy_auth_407_is_transient_and_names_the_proxy()
    {
        var c = Classify(407)!;
        Assert.Equal(RefreshErrorKind.NetworkTimeout, c.Kind);
        Assert.Contains("proxy", c.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redirect_means_the_session_expired_not_a_config_problem()
    {
        // Redirects are off (contract #4); a dead web session answers with a login redirect.
        Assert.Equal(RefreshErrorKind.Unauthorized, Classify(302)!.Kind);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void Server_side_trouble_is_transient(int status) =>
        Assert.Equal(RefreshErrorKind.NetworkTimeout, Classify(status)!.Kind);

    [Fact]
    public void Other_4xx_points_at_the_template()
    {
        var c = Classify(404)!;
        Assert.Equal(RefreshErrorKind.ParseFailed, c.Kind);
        Assert.Contains("404", c.Message);
    }

    [Fact]
    public void Challenge_wins_over_status_classification()
    {
        var c = Classify(503, Fixtures.Read("challenge", "cloudflare.html"), "text/html")!;
        Assert.Equal(RefreshErrorKind.Challenge, c.Kind);
    }

    [Fact]
    public void Transient_cloudflare_error_page_is_not_a_challenge()
    {
        // Origin-down pages (502/520-526) brand themselves "cloudflare" but carry no challenge
        // markers. They must classify as transient, NOT trigger the 5–60 min challenge backoff.
        const string errorPage = """
            <html><head><title>claude.ai | 502: Bad gateway</title></head>
            <body><h1>Bad gateway</h1><p>Error code 502</p>
            <p>Performance &amp; security by Cloudflare</p></body></html>
            """;
        var c = Classify(502, errorPage, "text/html")!;

        Assert.Equal(RefreshErrorKind.NetworkTimeout, c.Kind);
    }
}

public class HtmlLoginPageTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Ok_html_login_page_reports_expired_session_not_config_problem()
    {
        // A dead web session is often answered with 200 + the provider's login/app page.
        var template = Fixtures.LoadTemplate("claude");
        var sender = new FakeSender(new HttpResponseData(
            200, "text/html", "<!DOCTYPE html><html><body>Sign in to continue</body></html>",
            new Dictionary<string, string>()));

        var adapter = new UsageWidget.Core.Adapters.TemplateAdapter(
            AccountSource.ClaudeWebToken, new UsageWidget.Core.Templating.MappingEngine());
        var account = new Account { Source = AccountSource.ClaudeWebToken, Nickname = "Acct" };

        var result = await adapter.FetchAsync(account, template, "secret", sender, Now, default);

        Assert.Equal(RefreshErrorKind.Unauthorized, result.ErrorKind);
        Assert.Contains("expired", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unmapped_json_stays_a_config_problem()
    {
        var template = Fixtures.LoadTemplate("claude");
        var sender = new FakeSender(FakeSender.Json("""{"unrelated": true}"""));

        var adapter = new UsageWidget.Core.Adapters.TemplateAdapter(
            AccountSource.ClaudeWebToken, new UsageWidget.Core.Templating.MappingEngine());
        var account = new Account { Source = AccountSource.ClaudeWebToken, Nickname = "Acct" };

        var result = await adapter.FetchAsync(account, template, "secret", sender, Now, default);

        Assert.Equal(RefreshErrorKind.ParseFailed, result.ErrorKind);
    }
}

public class StaggerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

    private static Account Acct(string nick) => new() { Nickname = nick, Source = AccountSource.ClaudeWebToken };

    [Fact]
    public async Task Accounts_start_staggered_not_simultaneously()
    {
        var delays = new List<TimeSpan>();
        var coordinator = new RefreshCoordinator
        {
            Delay = (wait, _) => { lock (delays) delays.Add(wait); return Task.CompletedTask; },
        };

        await coordinator.RefreshAllAsync(
            new[] { Acct("a"), Acct("b"), Acct("c") },
            (a, _) => Task.FromResult(UsageResult.Success(a.Nickname!, null, null, Now)),
            Now,
            stagger: TimeSpan.FromSeconds(2));

        delays.Sort();
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, delays);
    }

    [Fact]
    public async Task Zero_stagger_adds_no_delays()
    {
        var delayCalls = 0;
        var coordinator = new RefreshCoordinator
        {
            Delay = (_, _) => { Interlocked.Increment(ref delayCalls); return Task.CompletedTask; },
        };

        await coordinator.RefreshAllAsync(
            new[] { Acct("a"), Acct("b") },
            (a, _) => Task.FromResult(UsageResult.Success(a.Nickname!, null, null, Now)),
            Now);

        Assert.Equal(0, delayCalls);
    }
}

public class AccountRefresherTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

    private static (AccountRefresher Refresher, AdapterConfig Config, Account Account, InMemorySecretStore Secrets)
        Setup(IHttpSender sender)
    {
        var account = new Account { Nickname = "acct", Source = AccountSource.ClaudeWebToken };
        var config = new AdapterConfig
        {
            Accounts = { account },
            Polling = new PollingSettings { StaggerInterval = TimeSpan.Zero },
        };
        config.TemplatesBySource[account.Source.ToString()] = Fixtures.LoadTemplate("claude");

        var secrets = new InMemorySecretStore();
        secrets.Set(account.Id, account.Source, "secret");

        var refresher = new AccountRefresher(new AdapterFactory(config.Polling), secrets, sender);
        return (refresher, config, account, secrets);
    }

    [Fact]
    public async Task Challenge_puts_account_into_backoff_and_backoff_keeps_the_challenge_cause()
    {
        var challenge = FakeSender.Html(Fixtures.Read("challenge", "cloudflare.html"));
        var (refresher, config, account, _) = Setup(new FakeSender(challenge));

        var first = await refresher.RefreshAllAsync(config, Now, default);
        Assert.Equal(RefreshErrorKind.Challenge, first[account.Id].ErrorKind);
        Assert.True(refresher.BackoffFor(account.Id).IsInBackoff(Now));

        // Next cycle lands inside the backoff window: the row must still say "security check",
        // not degrade into a generic rate-limit, and must say when the retry comes.
        var second = await refresher.RefreshAllAsync(config, Now.AddMinutes(1), default);
        Assert.Equal(RefreshErrorKind.Challenge, second[account.Id].ErrorKind);
        Assert.Contains("retrying in", second[account.Id].ErrorMessage!);
    }

    [Fact]
    public async Task Success_clears_backoff()
    {
        var (refresher, config, account, _) = Setup(
            new FakeSender(FakeSender.Json(Fixtures.Read("claude", "response.json"))));

        refresher.BackoffFor(account.Id).OnRateLimited(Now.AddMinutes(-10));
        var results = await refresher.RefreshAllAsync(config, Now, default);

        Assert.True(results[account.Id].IsSuccess);
        Assert.False(refresher.BackoffFor(account.Id).IsInBackoff(Now));
    }

    [Fact]
    public async Task Missing_secret_reports_unauthorized_without_sending()
    {
        var sender = new FakeSender(FakeSender.Json("{}"));
        var (refresher, config, account, secrets) = Setup(sender);
        secrets.Delete(account.Id, account.Source);

        var results = await refresher.RefreshAllAsync(config, Now, default);

        Assert.Equal(RefreshErrorKind.Unauthorized, results[account.Id].ErrorKind);
        Assert.False(sender.WasCalled);
    }

    [Fact]
    public void Backoff_state_survives_concurrent_first_touch()
    {
        var (refresher, _, _, _) = Setup(new FakeSender(FakeSender.Json("{}")));

        var policies = new BackoffPolicy[64];
        Parallel.For(0, policies.Length, i => policies[i] = refresher.BackoffFor("same-account"));

        Assert.All(policies, p => Assert.Same(policies[0], p));
    }
}

public class PollingLoopTests
{
    [Fact]
    public async Task Manual_refresh_never_overlaps_a_running_cycle()
    {
        var firstCallStarted = new TaskCompletionSource();
        var releaseFirstCall = new TaskCompletionSource();
        var calls = 0;

        var account = new Account { Nickname = "acct", Source = AccountSource.ClaudeWebToken };
        var config = new AdapterConfig
        {
            Accounts = { account },
            Polling = new PollingSettings { StaggerInterval = TimeSpan.Zero },
        };
        config.TemplatesBySource[account.Source.ToString()] = Fixtures.LoadTemplate("claude");

        var secrets = new InMemorySecretStore();
        secrets.Set(account.Id, account.Source, "secret");

        var blockingSender = new BlockingSender(async () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstCallStarted.SetResult();
                await releaseFirstCall.Task;
            }
        });

        var refresher = new AccountRefresher(new AdapterFactory(config.Polling), secrets, blockingSender);
        using var loop = new PollingLoop(refresher, () => config);

        var first = loop.RefreshNowAsync();
        await firstCallStarted.Task;

        var second = loop.RefreshNowAsync();
        await Task.Delay(50); // give the second cycle every chance to (wrongly) start
        Assert.Equal(1, calls); // the gate held: no double-fetch of the same account

        releaseFirstCall.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, calls); // and the queued manual refresh still ran afterwards
    }

    private sealed class BlockingSender : IHttpSender
    {
        private readonly Func<Task> _onCall;

        public BlockingSender(Func<Task> onCall) => _onCall = onCall;

        public async Task<HttpResponseData> SendAsync(
            string method, Uri url, IReadOnlyDictionary<string, string> headers, string? body,
            bool followRedirects, TimeSpan timeout, CancellationToken ct)
        {
            await _onCall();
            return FakeSender.Json("{}");
        }
    }
}

public class BackoffCauseTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Remembers_challenge_cause_and_resets_on_success()
    {
        var policy = new BackoffPolicy();
        policy.OnRateLimited(Now, RefreshErrorKind.Challenge);
        Assert.Equal(RefreshErrorKind.Challenge, policy.Cause);

        policy.OnSuccess();
        Assert.Equal(RefreshErrorKind.RateLimited, policy.Cause);
    }
}

public class TransportErrorTests
{
    [Fact]
    public void Dns_failures_name_dns()
    {
        var described = TransportError.Describe(
            new HttpRequestException(HttpRequestError.NameResolutionError, "no such host"));
        Assert.Contains("DNS", described);
    }

    [Fact]
    public void Proxy_tunnel_failures_name_the_proxy()
    {
        var described = TransportError.Describe(
            new HttpRequestException(HttpRequestError.ProxyTunnelError, "tunnel refused"));
        Assert.Contains("proxy", described, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_errors_are_redacted()
    {
        var described = TransportError.Describe(new Exception("token Bearer abcdef0123456789abcdef leaked"));
        Assert.DoesNotContain("abcdef0123456789abcdef", described);
    }
}

public class CadenceMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uw-cfg-" + Guid.NewGuid().ToString("N"));

    public CadenceMigrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void V1_default_cadences_are_raised_to_spec_values()
    {
        var path = Path.Combine(_dir, "adapter-config.json");
        File.WriteAllText(path, """
        {
          "schemaVersion": 1,
          "templatesBySource": {},
          "accounts": [],
          "polling": { "defaultCadence": "00:01:00", "idleCadence": "00:05:00" }
        }
        """);

        var config = new ConfigStore(path).Load();

        Assert.Equal(TimeSpan.FromMinutes(3), config.Polling.DefaultCadence);
        Assert.Equal(TimeSpan.FromMinutes(15), config.Polling.IdleCadence);
        Assert.True(File.Exists(path + ".1.bak"));
    }

    [Fact]
    public void Hand_edited_cadence_is_respected()
    {
        var path = Path.Combine(_dir, "adapter-config.json");
        File.WriteAllText(path, """
        {
          "schemaVersion": 1,
          "templatesBySource": {},
          "accounts": [],
          "polling": { "defaultCadence": "00:10:00" }
        }
        """);

        var config = new ConfigStore(path).Load();

        Assert.Equal(TimeSpan.FromMinutes(10), config.Polling.DefaultCadence);
    }

    [Fact]
    public void Migration_handles_the_store_own_pascal_case_output()
    {
        var path = Path.Combine(_dir, "adapter-config.json");
        var store = new ConfigStore(path);
        store.Save(DefaultConfig.Create());

        // Rewind the saved (current, PascalCase) file to look like a v1-era config.
        var raw = File.ReadAllText(path);
        var root = JObject.Parse(raw);
        root["SchemaVersion"] = 1;
        root["Polling"]!["DefaultCadence"] = "00:01:00";
        File.WriteAllText(path, root.ToString());

        var config = store.Load();

        Assert.Equal(TimeSpan.FromMinutes(3), config.Polling.DefaultCadence);
    }
}

public class MultiCredentialTests
{
    private const string CurlWithBothCredentials =
        "curl 'https://chatgpt.com/backend-api/codex/usage' \\\n" +
        "  -H 'authorization: Bearer EXAMPLEBEARER123' \\\n" +
        "  -H 'cookie: __cf_bm=EXAMPLEEDGECOOKIE; _account=EXAMPLE' \\\n" +
        "  -H 'accept: application/json'";

    [Fact]
    public void Import_keeps_every_credential_header_instead_of_dropping_one()
    {
        var imported = Import.CurlAccountImport.Build(CurlWithBothCredentials, AccountSource.CodexPastedToken);

        // Both credential headers survive as placeholders — the replay needs BOTH (bearer for the
        // API, cookies for the edge); the old behavior silently dropped whichever came second.
        Assert.Equal(UsageWidget.Core.Templating.RequestTemplate.TokenPlaceholder, imported.Template.Headers["authorization"]);
        Assert.Equal(UsageWidget.Core.Templating.RequestTemplate.TokenPlaceholder, imported.Template.Headers["cookie"]);

        // And neither real value leaks into the plaintext template.
        Assert.DoesNotContain("EXAMPLEBEARER123", string.Join("|", imported.Template.Headers.Values));
        Assert.DoesNotContain("EXAMPLEEDGECOOKIE", string.Join("|", imported.Template.Headers.Values));
    }

    [Fact]
    public void Injection_routes_each_credential_to_its_own_header()
    {
        var imported = Import.CurlAccountImport.Build(CurlWithBothCredentials, AccountSource.CodexPastedToken);

        var injected = UsageWidget.Core.Templating.TemplateEngine.InjectHeaders(
            imported.Template.Headers, imported.Secret);

        Assert.Equal("Bearer EXAMPLEBEARER123", injected["authorization"]);
        Assert.Equal("__cf_bm=EXAMPLEEDGECOOKIE; _account=EXAMPLE", injected["cookie"]);
        Assert.Equal("application/json", injected["accept"]);
    }

    [Fact]
    public void Codex_import_ships_working_mappings_not_an_empty_config()
    {
        var imported = Import.CurlAccountImport.Build(CurlWithBothCredentials, AccountSource.CodexPastedToken);

        Assert.Equal("$.rate_limits.primary.used_percent", imported.Template.Mappings.SessionPct);
        Assert.Equal("$.rate_limits.secondary.used_percent", imported.Template.Mappings.WeeklyPct);
    }

    [Fact]
    public void Plain_secret_that_looks_like_json_is_not_mistaken_for_an_envelope()
    {
        var headers = new Dictionary<string, string> { ["authorization"] = "Bearer {{TOKEN}}" };
        var injected = UsageWidget.Core.Templating.TemplateEngine.InjectHeaders(
            headers, """{"kind":"jwt-ish","value":"x"}""");

        Assert.Equal("""Bearer {"kind":"jwt-ish","value":"x"}""", injected["authorization"]);
    }
}

public class SecretSanitizationTests
{
    [Fact]
    public void Pasted_newlines_are_stripped_before_header_injection()
    {
        // A trailing newline (or a soft wrap) in a pasted token turns into a header-injection
        // exception on every send, misreported as a network error.
        var headers = new Dictionary<string, string> { ["cookie"] = "{{TOKEN}}" };
        var injected = UsageWidget.Core.Templating.TemplateEngine.InjectHeaders(
            headers, "sessionKey=abc\r\ndef;\n other=1\n");

        Assert.Equal("sessionKey=abcdef; other=1", injected["cookie"]);
    }
}

public class ConfigResilienceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uw-cfg-" + Guid.NewGuid().ToString("N"));

    public ConfigResilienceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Corrupt_config_is_preserved_as_backup_and_defaults_load()
    {
        var path = Path.Combine(_dir, "adapter-config.json");
        File.WriteAllText(path, """{"schemaVersion": 2, "accounts": [ TRUNCATED-BY-POWER-LOSS""");

        var config = new ConfigStore(path).Load();

        Assert.Empty(config.Accounts); // defaults, not a crash that bricks startup forever
        Assert.True(File.Exists(path + ".corrupt.bak"));
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        var path = Path.Combine(_dir, "adapter-config.json");
        new ConfigStore(path).Save(DefaultConfig.Create());

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }
}

public class EdgeBlockClassificationTests
{
    [Fact]
    public void Markerless_403_html_block_page_backs_off_instead_of_demanding_repaste()
    {
        var c = ResponseClassifier.Classify(new HttpResponseData(
            403, "text/html", "<html><body><h1>Access denied</h1></body></html>",
            new Dictionary<string, string>()))!;

        Assert.Equal(RefreshErrorKind.Challenge, c.Kind);
    }

    [Fact]
    public void Json_403_is_a_real_authorization_failure()
    {
        var c = ResponseClassifier.Classify(new HttpResponseData(
            403, "application/json", """{"error":"unauthorized"}""",
            new Dictionary<string, string>()))!;

        Assert.Equal(RefreshErrorKind.Unauthorized, c.Kind);
    }
}

public class MigratingSecretStoreTests
{
    private sealed class ThrowingStore : ISecretStore
    {
        public string KeyFor(string accountId, AccountSource source) => SecretKey.For(accountId, source);
        public void Set(string accountId, AccountSource source, string secret) => throw new InvalidOperationException();
        public string? Get(string accountId, AccountSource source) => throw new InvalidOperationException();
        public bool Delete(string accountId, AccountSource source) => throw new InvalidOperationException();
    }

    [Fact]
    public void Legacy_secret_is_found_and_migrated_forward()
    {
        var primary = new InMemorySecretStore();
        var legacy = new InMemorySecretStore();
        legacy.Set("id1", AccountSource.ClaudeWebToken, "old-secret");

        var store = new MigratingSecretStore(primary, legacy);

        Assert.Equal("old-secret", store.Get("id1", AccountSource.ClaudeWebToken));
        // Migrated: the next read no longer depends on the legacy store existing.
        Assert.Equal("old-secret", primary.Get("id1", AccountSource.ClaudeWebToken));
    }

    [Fact]
    public void Primary_secret_wins_over_legacy()
    {
        var primary = new InMemorySecretStore();
        var legacy = new InMemorySecretStore();
        primary.Set("id1", AccountSource.ClaudeWebToken, "new");
        legacy.Set("id1", AccountSource.ClaudeWebToken, "old");

        Assert.Equal("new", new MigratingSecretStore(primary, legacy).Get("id1", AccountSource.ClaudeWebToken));
    }

    [Fact]
    public void Broken_legacy_store_reads_as_a_miss_not_a_crash()
    {
        var store = new MigratingSecretStore(new InMemorySecretStore(), new ThrowingStore());
        Assert.Null(store.Get("id1", AccountSource.ClaudeWebToken));
    }

    [Fact]
    public void Delete_wipes_both_stores()
    {
        var primary = new InMemorySecretStore();
        var legacy = new InMemorySecretStore();
        primary.Set("id1", AccountSource.ClaudeWebToken, "new");
        legacy.Set("id1", AccountSource.ClaudeWebToken, "old");

        Assert.True(new MigratingSecretStore(primary, legacy).Delete("id1", AccountSource.ClaudeWebToken));

        Assert.Null(primary.Get("id1", AccountSource.ClaudeWebToken));
        Assert.Null(legacy.Get("id1", AccountSource.ClaudeWebToken));
    }
}

public class FormatAgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0.5, "just now")]
    [InlineData(5, "5 min ago")]
    [InlineData(90, "1h ago")]
    [InlineData(60 * 26, "1 day ago")]
    [InlineData(60 * 72, "3 days ago")]
    public void Ages_render_human_readably(double minutes, string expected) =>
        Assert.Equal(expected, TimeMath.FormatAge(Now.AddMinutes(-minutes), Now));
}
