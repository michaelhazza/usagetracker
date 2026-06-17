using Newtonsoft.Json.Linq;
using UsageWidget.Core.Accounts;
using UsageWidget.Core.Config;
using UsageWidget.Core.Templating;
using Xunit;

namespace UsageWidget.Core.Tests;

public class ConfigMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uw-cfg-" + Guid.NewGuid().ToString("N"));

    public ConfigMigrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Legacy_v0_file_migrates_to_current_and_backs_up()
    {
        var path = Path.Combine(_dir, "adapter-config.json");
        // v0 shape: no schemaVersion, templates under "templates".
        File.WriteAllText(path, """
        {
          "templates": { "ClaudeWebToken": { "url": "https://claude.ai/x", "allowedHosts": ["claude.ai"] } },
          "accounts": []
        }
        """);

        var config = new ConfigStore(path).Load();

        Assert.Equal(AdapterConfig.CurrentSchemaVersion, config.SchemaVersion);
        Assert.True(config.TemplatesBySource.ContainsKey("ClaudeWebToken"));

        // The prior file is backed up as adapter-config.json.0.bak ...
        Assert.True(File.Exists(path + ".0.bak"));
        // ... and the rewritten file is now current with the renamed key.
        var rewritten = JObject.Parse(File.ReadAllText(path));
        Assert.NotNull(rewritten["templatesBySource"]);
        Assert.Null(rewritten["templates"]);
    }

    [Fact]
    public void Missing_file_yields_default_config_without_writing()
    {
        var path = Path.Combine(_dir, "none.json");
        var config = new ConfigStore(path).Load();
        Assert.Equal(AdapterConfig.CurrentSchemaVersion, config.SchemaVersion);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Roundtrips_default_config_without_secrets()
    {
        var path = Path.Combine(_dir, "adapter-config.json");
        var store = new ConfigStore(path);
        store.Save(DefaultConfig.Create());

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("Bearer ey", text); // no real token, only {{TOKEN}}
        Assert.Contains("{{TOKEN}}", text);

        var reloaded = store.Load();
        Assert.True(reloaded.TemplatesBySource.ContainsKey(nameof(AccountSource.ClaudeWebToken)));
    }
}

public class TemplateValidatorTests
{
    private static RequestTemplate Valid() => new()
    {
        Url = "https://claude.ai/api/usage",
        Method = "GET",
        AllowedHosts = { "claude.ai" },
        Headers = { ["Authorization"] = $"Bearer {RequestTemplate.TokenPlaceholder}" },
        Mappings = new MappingConfig { SessionPct = "$.five_hour.utilization" },
    };

    [Fact]
    public void Accepts_a_well_formed_template()
    {
        Assert.True(TemplateValidator.Validate(Valid()).IsValid);
    }

    [Fact]
    public void Rejects_host_not_in_allowlist()
    {
        var t = Valid();
        t.AllowedHosts.Clear();
        t.AllowedHosts.Add("other.com");
        Assert.False(TemplateValidator.Validate(t).IsValid);
    }

    [Fact]
    public void Rejects_unsupported_method()
    {
        var t = Valid();
        t.Method = "TRACE";
        Assert.False(TemplateValidator.Validate(t).IsValid);
    }

    [Fact]
    public void Rejects_token_placeholder_in_non_auth_header()
    {
        var t = Valid();
        t.Headers["X-Custom"] = RequestTemplate.TokenPlaceholder;
        Assert.False(TemplateValidator.Validate(t).IsValid);
    }

    [Fact]
    public void Rejects_real_secret_in_auth_header()
    {
        var t = Valid();
        t.Headers["Authorization"] = "Bearer sk-ant-real-secret-1234567890";
        Assert.False(TemplateValidator.Validate(t).IsValid);
    }

    [Fact]
    public void Rejects_invalid_jsonpath_mapping()
    {
        var t = Valid();
        t.Mappings.SessionPct = "$[[[broken";
        Assert.False(TemplateValidator.Validate(t).IsValid);
    }
}

public class DefaultConfigTests
{
    [Fact]
    public void Ships_claude_and_codex_as_fill_me_in_placeholders()
    {
        var config = DefaultConfig.Create();
        Assert.True(config.TemplateFor(AccountSource.ClaudeWebToken)!.IsPlaceholder);
        Assert.True(config.TemplateFor(AccountSource.CodexPastedToken)!.IsPlaceholder);
    }

    [Fact]
    public void Anthropic_fallback_has_a_concrete_known_endpoint()
    {
        var t = DefaultConfig.Create().TemplateFor(AccountSource.AnthropicApiKey)!;
        Assert.False(t.IsPlaceholder);
        Assert.Contains("api.anthropic.com", t.AllowedHosts);
    }
}
