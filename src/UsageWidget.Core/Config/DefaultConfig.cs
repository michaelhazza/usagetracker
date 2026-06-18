using UsageWidget.Core.Accounts;
using UsageWidget.Core.Templating;

namespace UsageWidget.Core.Config;

/// <summary>
/// Ships the starter <see cref="AdapterConfig"/>. Per §5, the Claude and Codex endpoints are
/// undocumented, so their templates are deliberate FILL ME IN scaffolds — the owner pastes the real
/// URL/headers/mappings from their captured request (§0) into the Request Template editor. The
/// allowlists and the Anthropic API fallback shape ARE pre-filled because those hosts are known.
/// </summary>
public static class DefaultConfig
{
    public const string FillMeIn = "FILL ME IN — paste from your DevTools Network capture (see README)";

    public static AdapterConfig Create()
    {
        var config = new AdapterConfig();

        config.TemplatesBySource[AccountSource.ClaudeWebToken.ToString()] = new RequestTemplate
        {
            Url = FillMeIn, // e.g. https://claude.ai/api/.../usage  — copy from Network tab
            Method = "GET",
            AllowedHosts = { "claude.ai", ".claude.ai" },
            FollowRedirects = false,
            Headers =
            {
                ["Authorization"] = $"Bearer {RequestTemplate.TokenPlaceholder}",
                ["Cookie"] = RequestTemplate.TokenPlaceholder, // for the httpOnly session-cookie case
                ["User-Agent"] = "FILL ME IN — copy the exact UA from the captured request (R1)",
                ["Accept"] = "application/json",
            },
            Mappings = new MappingConfig
            {
                // Verified against a live claude.ai /usage response.
                SessionPct = "$.five_hour.utilization",
                SessionReset = "$.five_hour.resets_at",
                WeeklyPct = "$.seven_day.utilization",
                WeeklyReset = "$.seven_day.resets_at",
            },
        };

        config.TemplatesBySource[AccountSource.CodexPastedToken.ToString()] = new RequestTemplate
        {
            Url = FillMeIn,
            Method = "GET",
            AllowedHosts = { "chatgpt.com", ".chatgpt.com", "api.openai.com" },
            FollowRedirects = false,
            Headers =
            {
                ["Authorization"] = $"Bearer {RequestTemplate.TokenPlaceholder}",
                ["Accept"] = "application/json",
            },
            Mappings = new MappingConfig
            {
                SessionPct = "$.FILL_ME_IN.primary.used_percent",
                SessionReset = "$.FILL_ME_IN.primary.reset_after_seconds",
                SessionResetKind = ResetKind.DurationSeconds,
                WeeklyPct = "$.FILL_ME_IN.secondary.used_percent",
                WeeklyReset = "$.FILL_ME_IN.secondary.reset_after_seconds",
                WeeklyResetKind = ResetKind.DurationSeconds,
            },
        };

        // Anthropic API fallback: OFF by default, opt-in per account. Hosts are known; the
        // utilization is returned as response headers on a billed /v1/messages call (§5).
        config.TemplatesBySource[AccountSource.AnthropicApiKey.ToString()] = new RequestTemplate
        {
            Url = "https://api.anthropic.com/v1/messages",
            Method = "POST",
            AllowedHosts = { "api.anthropic.com" },
            FollowRedirects = false,
            Headers =
            {
                ["x-api-key"] = RequestTemplate.TokenPlaceholder,
                ["anthropic-version"] = "2023-06-01",
                ["content-type"] = "application/json",
            },
            Body = "{\"model\":\"claude-haiku-4-5-20251001\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}",
            Mappings = new MappingConfig
            {
                SessionPct = "header:anthropic-ratelimit-unified-5h-utilization",
                SessionReset = "header:anthropic-ratelimit-unified-5h-reset",
                WeeklyPct = "header:anthropic-ratelimit-unified-7d-utilization",
                WeeklyReset = "header:anthropic-ratelimit-unified-7d-reset",
            },
        };

        return config;
    }
}
