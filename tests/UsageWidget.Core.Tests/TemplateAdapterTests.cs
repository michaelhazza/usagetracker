using UsageWidget.Core.Accounts;
using UsageWidget.Core.Adapters;
using UsageWidget.Core.Model;
using UsageWidget.Core.Templating;
using Xunit;

namespace UsageWidget.Core.Tests;

public class TemplateAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

    private static TemplateAdapter Adapter() =>
        new(AccountSource.ClaudeWebToken, new MappingEngine());

    private static Account Account() => new() { Source = AccountSource.ClaudeWebToken, Nickname = "Acct" };

    [Fact]
    public async Task Success_maps_fixture_and_resolves_identity_label()
    {
        var template = Fixtures.LoadTemplate("claude");
        var sender = new FakeSender(FakeSender.Json(Fixtures.Read("claude", "response.json")));

        var result = await Adapter().FetchAsync(Account(), template, "secret", sender, Now, default);

        Assert.True(result.IsSuccess);
        Assert.Equal("demo-user@example.com", result.AccountLabel);
        Assert.Equal(42.5, result.Session!.Pct);
        Assert.Equal(88.0, result.Weekly!.Pct);
    }

    [Fact]
    public async Task Injects_secret_into_headers_only_after_allowlist_passes()
    {
        var template = Fixtures.LoadTemplate("claude");
        var sender = new FakeSender(FakeSender.Json("{}"));

        await Adapter().FetchAsync(Account(), template, "SECRET-VALUE", sender, Now, default);

        Assert.True(sender.WasCalled);
        Assert.Equal("Bearer SECRET-VALUE", sender.LastHeaders!["Authorization"]);
        Assert.DoesNotContain(RequestTemplate.TokenPlaceholder, sender.LastHeaders["Authorization"]);
    }

    [Fact]
    public async Task Disallowed_host_fails_closed_and_never_sends_or_injects()
    {
        var template = Fixtures.LoadTemplate("claude");
        template.Url = "https://evil.example.net/usage"; // not in allowlist
        var sender = new FakeSender(FakeSender.Json("{}"));

        var result = await Adapter().FetchAsync(Account(), template, "secret", sender, Now, default);

        Assert.False(result.IsSuccess);
        Assert.Equal(RefreshErrorKind.ParseFailed, result.ErrorKind);
        Assert.False(sender.WasCalled); // credential never left the process
    }

    [Theory]
    [InlineData(401, RefreshErrorKind.Unauthorized)]
    [InlineData(403, RefreshErrorKind.Unauthorized)]
    [InlineData(429, RefreshErrorKind.RateLimited)]
    [InlineData(500, RefreshErrorKind.NetworkTimeout)]
    [InlineData(404, RefreshErrorKind.ParseFailed)]
    public async Task Status_codes_map_to_taxonomy(int status, RefreshErrorKind expected)
    {
        var template = Fixtures.LoadTemplate("claude");
        var sender = new FakeSender(FakeSender.Json("{}", status));

        var result = await Adapter().FetchAsync(Account(), template, "secret", sender, Now, default);

        Assert.Equal(expected, result.ErrorKind);
    }

    [Fact]
    public async Task Cloudflare_challenge_classifies_as_challenge_not_unauthorized()
    {
        var template = Fixtures.LoadTemplate("claude");
        // 403 + HTML challenge body: must be Challenge, not Unauthorized.
        var sender = new FakeSender(FakeSender.Html(Fixtures.Read("challenge", "cloudflare.html")));

        var result = await Adapter().FetchAsync(Account(), template, "secret", sender, Now, default);

        Assert.Equal(RefreshErrorKind.Challenge, result.ErrorKind);
    }

    [Fact]
    public async Task Empty_200_downgrades_to_parsefailed()
    {
        var template = Fixtures.LoadTemplate("claude");
        var sender = new FakeSender(FakeSender.Json("""{"unrelated": true}"""));

        var result = await Adapter().FetchAsync(Account(), template, "secret", sender, Now, default);

        Assert.Equal(RefreshErrorKind.ParseFailed, result.ErrorKind);
    }

    [Fact]
    public async Task Timeout_is_isolated_as_networktimeout()
    {
        var template = Fixtures.LoadTemplate("claude");
        var sender = new FakeSender(new TaskCanceledException("simulated timeout"));

        var result = await Adapter().FetchAsync(Account(), template, "secret", sender, Now, default);

        Assert.Equal(RefreshErrorKind.NetworkTimeout, result.ErrorKind);
    }

    [Fact]
    public async Task Error_message_is_redacted()
    {
        var template = Fixtures.LoadTemplate("claude");
        var sender = new FakeSender(new Exception("boom for user@secret.com Bearer abcdef0123456789abcdef"));

        var result = await Adapter().FetchAsync(Account(), template, "secret", sender, Now, default);

        Assert.DoesNotContain("user@secret.com", result.ErrorMessage);
        Assert.DoesNotContain("abcdef0123456789abcdef", result.ErrorMessage);
    }
}
