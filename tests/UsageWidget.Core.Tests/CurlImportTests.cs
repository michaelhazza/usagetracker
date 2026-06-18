using UsageWidget.Core.Accounts;
using UsageWidget.Core.Adapters;
using UsageWidget.Core.Config;
using UsageWidget.Core.Import;
using UsageWidget.Core.Templating;
using Xunit;

namespace UsageWidget.Core.Tests;

public class CurlImportTests
{
    // A realistic Chrome "Copy as cURL (bash)" for the Claude usage request (secret is a placeholder).
    private const string BashCurl =
        "curl 'https://claude.ai/api/organizations/05ef440a-df48-4d50-915a-b380a2ecad40/usage' \\\n" +
        "  -H 'accept: */*' \\\n" +
        "  -H 'accept-encoding: gzip, deflate, br, zstd' \\\n" +
        "  -H 'anthropic-client-platform: web_claude_ai' \\\n" +
        "  -H 'cookie: sessionKey=EXAMPLECOOKIEVALUE123; intercom=1' \\\n" +
        "  --compressed";

    private const string CmdCurl =
        "curl \"https://claude.ai/api/organizations/ORG/usage\" ^\n" +
        "  -H \"accept: */*\" ^\n" +
        "  -H \"cookie: sessionKey=EXAMPLECOOKIEVALUE123\"";

    [Fact]
    public void Parses_url_method_and_headers_from_bash_curl()
    {
        var parsed = CurlParser.Parse(BashCurl);

        Assert.Equal("GET", parsed.Method);
        Assert.Equal("https://claude.ai/api/organizations/05ef440a-df48-4d50-915a-b380a2ecad40/usage", parsed.Url);
        Assert.Equal("*/*", parsed.Headers["accept"]);
        Assert.Equal("sessionKey=EXAMPLECOOKIEVALUE123; intercom=1", parsed.Headers["cookie"]);
    }

    [Fact]
    public void Parses_cmd_style_with_caret_continuations()
    {
        var parsed = CurlParser.Parse(CmdCurl);
        Assert.Equal("https://claude.ai/api/organizations/ORG/usage", parsed.Url);
        Assert.Equal("sessionKey=EXAMPLECOOKIEVALUE123", parsed.Headers["cookie"]);
    }

    [Fact]
    public void Import_extracts_secret_and_replaces_cookie_with_placeholder()
    {
        var imported = CurlAccountImport.Build(BashCurl, AccountSource.ClaudeWebToken);

        Assert.Equal("sessionKey=EXAMPLECOOKIEVALUE123; intercom=1", imported.Secret);
        Assert.Equal(RequestTemplate.TokenPlaceholder, imported.Template.Headers["cookie"]);
        Assert.DoesNotContain("EXAMPLECOOKIEVALUE123",
            string.Join("|", imported.Template.Headers.Values)); // no secret left in the template
    }

    [Fact]
    public void Import_strips_accept_encoding_and_bakes_claude_mappings()
    {
        var imported = CurlAccountImport.Build(BashCurl, AccountSource.ClaudeWebToken);

        Assert.False(imported.Template.Headers.ContainsKey("accept-encoding"));
        Assert.Contains("claude.ai", imported.Template.AllowedHosts);
        Assert.Equal("$.five_hour.utilization", imported.Template.Mappings.SessionPct);
        Assert.Equal("$.seven_day.utilization", imported.Template.Mappings.WeeklyPct);
    }

    [Fact]
    public void Imported_template_passes_validation()
    {
        var imported = CurlAccountImport.Build(BashCurl, AccountSource.ClaudeWebToken);
        var result = TemplateValidator.Validate(imported.Template);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("not a curl command")]
    [InlineData("curl 'https://claude.ai/usage' -H 'accept: */*'")] // no cookie/auth
    public void Import_throws_helpful_error_on_bad_input(string curl)
    {
        Assert.Throws<FormatException>(() => CurlAccountImport.Build(curl, AccountSource.ClaudeWebToken));
    }

    [Fact]
    public async Task End_to_end_imported_template_parses_the_real_claude_response()
    {
        // Trimmed copy of an actual claude.ai /usage response (numbers only — no secrets).
        const string realResponse = """
        {
          "five_hour": { "utilization": 0.0, "resets_at": null },
          "seven_day": { "utilization": 99.0, "resets_at": "2026-06-18T11:00:00.205342+00:00" },
          "seven_day_sonnet": { "utilization": 49.0, "resets_at": "2026-06-18T11:00:00.205357+00:00" }
        }
        """;

        var imported = CurlAccountImport.Build(BashCurl, AccountSource.ClaudeWebToken);
        var account = new Account { Source = AccountSource.ClaudeWebToken, Nickname = "Work", Template = imported.Template };
        var adapter = new TemplateAdapter(AccountSource.ClaudeWebToken, new MappingEngine());
        var sender = new FakeSender(FakeSender.Json(realResponse));
        var now = new DateTimeOffset(2026, 6, 18, 1, 0, 0, TimeSpan.Zero);

        var result = await adapter.FetchAsync(account, imported.Template, imported.Secret, sender, now, default);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(0.0, result.Session!.Pct);
        Assert.Equal(99.0, result.Weekly!.Pct);
        Assert.Equal("Work", result.AccountLabel);
        // The cookie secret was injected into the replayed request, not left as a placeholder.
        Assert.Equal("sessionKey=EXAMPLECOOKIEVALUE123; intercom=1", sender.LastHeaders!["cookie"]);
    }
}
