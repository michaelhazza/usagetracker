using UsageWidget.Core.Security;
using Xunit;

namespace UsageWidget.Core.Tests;

public class RedactorTests
{
    [Theory]
    [InlineData("Authorization: Bearer sk-ant-abcdef123456", "sk-ant-abcdef123456")]
    [InlineData("Cookie: sessionKey=secret-value-here", "secret-value-here")]
    [InlineData("Set-Cookie: __Secure-x=abc123; HttpOnly", "abc123")]
    [InlineData("contact me at jane.doe@gmail.com please", "jane.doe@gmail.com")]
    [InlineData("token eyJhbGci.eyJzdWIi.sigPART here", "eyJhbGci")]
    [InlineData("\"access_token\": \"abcdef-123456\"", "abcdef-123456")]
    [InlineData("org_id=org-9988776655", "org-9988776655")]
    public void Redacts_every_secret_class(string input, string secretFragment)
    {
        var redacted = Redactor.Redact(input);
        Assert.DoesNotContain(secretFragment, redacted);
        Assert.Contains(Redactor.Placeholder, redacted);
    }

    [Fact]
    public void Masks_sensitive_header_value_entirely()
    {
        Assert.Equal(Redactor.Placeholder, Redactor.RedactHeaderValue("Authorization", "Bearer xyz"));
        Assert.Equal(Redactor.Placeholder, Redactor.RedactHeaderValue("Cookie", "a=b"));
    }

    [Fact]
    public void Drops_url_query_which_may_carry_tokens()
    {
        var redacted = Redactor.RedactUrl("https://claude.ai/api/usage?session=abc123&t=9");
        Assert.StartsWith("https://claude.ai/api/usage", redacted);
        Assert.DoesNotContain("abc123", redacted);
    }

    [Fact]
    public void Leaves_innocuous_text_untouched()
    {
        const string input = "Session 42% used, resets in 3h 12m";
        Assert.Equal(input, Redactor.Redact(input));
    }
}

public class HostnameAllowlistTests
{
    [Fact]
    public void Empty_allowlist_fails_closed()
    {
        Assert.False(HostnameAllowlist.IsAllowed("https://claude.ai/x", Array.Empty<string>()));
    }

    [Fact]
    public void Exact_host_matches_case_insensitively()
    {
        Assert.True(HostnameAllowlist.IsAllowed("https://Claude.AI/x", new[] { "claude.ai" }));
    }

    [Fact]
    public void Unlisted_host_is_rejected()
    {
        Assert.False(HostnameAllowlist.IsAllowed("https://evil.com/x", new[] { "claude.ai" }));
    }

    [Fact]
    public void Leading_dot_entry_matches_subdomains_only_within_suffix()
    {
        var allow = new[] { ".claude.ai" };
        Assert.True(HostnameAllowlist.IsAllowed("https://api.claude.ai/x", allow));
        Assert.True(HostnameAllowlist.IsAllowed("https://claude.ai/x", allow));
        Assert.False(HostnameAllowlist.IsAllowed("https://notclaude.ai/x", allow));
    }

    [Fact]
    public void Unparseable_url_fails_closed()
    {
        Assert.False(HostnameAllowlist.IsAllowed("not a url", new[] { "claude.ai" }));
    }
}

public class ChallengeDetectorTests
{
    [Fact]
    public void Detects_cloudflare_interstitial_fixture()
    {
        var html = Fixtures.Read("challenge", "cloudflare.html");
        Assert.True(ChallengeDetector.IsChallenge("text/html", html));
    }

    [Fact]
    public void Json_response_is_never_a_challenge()
    {
        Assert.False(ChallengeDetector.IsChallenge("application/json", """{"just a moment": true}"""));
    }

    [Fact]
    public void Plain_non_challenge_html_is_not_flagged()
    {
        Assert.False(ChallengeDetector.IsChallenge("text/html", "<html><body>hello</body></html>"));
    }
}
