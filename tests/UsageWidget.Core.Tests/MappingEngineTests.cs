using UsageWidget.Core.Templating;
using Xunit;

namespace UsageWidget.Core.Tests;

public class MappingEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, string> NoHeaders = new();
    private readonly MappingEngine _engine = new();

    [Fact]
    public void Maps_claude_fixture_both_windows_and_identity()
    {
        var template = Fixtures.LoadTemplate("claude");
        var body = Fixtures.Read("claude", "response.json");

        var result = _engine.Extract(body, NoHeaders, template.Mappings, Now);

        Assert.Equal(42.5, result.Session!.Pct);
        Assert.Equal(88.0, result.Weekly!.Pct);
        Assert.Equal(new DateTimeOffset(2026, 6, 17, 20, 0, 0, TimeSpan.Zero), result.Session.ResetAt);
        Assert.Equal("demo-user@example.com", result.Identity);
    }

    [Fact]
    public void Converts_duration_seconds_to_absolute_reset()
    {
        var template = Fixtures.LoadTemplate("codex");
        var body = Fixtures.Read("codex", "response.json");

        var result = _engine.Extract(body, NoHeaders, template.Mappings, Now);

        Assert.Equal(30, result.Session!.Pct);
        Assert.Equal(Now.AddSeconds(3600), result.Session.ResetAt);
        Assert.Equal(Now.AddSeconds(86400), result.Weekly!.ResetAt);
    }

    [Fact]
    public void Derives_percent_from_counts_when_pct_absent()
    {
        var map = new MappingConfig { SessionUsed = "$.used", SessionLimit = "$.limit" };
        var result = _engine.Extract("""{"used": 30, "limit": 120}""", NoHeaders, map, Now);

        Assert.Equal(30, result.Session!.Used);
        Assert.Equal(120, result.Session.Limit);
        Assert.Equal(25.0, result.Session.Pct);
    }

    [Fact]
    public void Reads_values_from_response_headers()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic-ratelimit-unified-5h-utilization"] = "55",
        };
        var map = new MappingConfig { SessionPct = "header:anthropic-ratelimit-unified-5h-utilization" };

        var result = _engine.Extract("{}", headers, map, Now);

        Assert.Equal(55, result.Session!.Pct);
    }

    [Fact]
    public void Throws_mapping_exception_for_non_json_body()
    {
        var map = new MappingConfig { SessionPct = "$.x" };
        Assert.Throws<MappingException>(() => _engine.Extract("<html>nope</html>", NoHeaders, map, Now));
    }

    [Fact]
    public void Returns_null_window_when_nothing_matches()
    {
        var map = new MappingConfig { SessionPct = "$.missing" };
        var result = _engine.Extract("""{"other": 1}""", NoHeaders, map, Now);
        Assert.Null(result.Session);
    }
}
