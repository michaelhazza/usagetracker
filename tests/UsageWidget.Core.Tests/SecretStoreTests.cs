using UsageWidget.Core.Accounts;
using UsageWidget.Core.Secrets;
using Xunit;

namespace UsageWidget.Core.Tests;

public class SecretStoreTests
{
    [Fact]
    public void Key_uses_stable_account_id_and_source_not_nickname()
    {
        var store = new InMemorySecretStore();
        Assert.Equal("UsageWidget/abc123/ClaudeWebToken",
            store.KeyFor("abc123", AccountSource.ClaudeWebToken));
    }

    [Fact]
    public void Set_get_delete_roundtrip()
    {
        var store = new InMemorySecretStore();
        store.Set("id1", AccountSource.ClaudeWebToken, "tok");
        Assert.Equal("tok", store.Get("id1", AccountSource.ClaudeWebToken));

        Assert.True(store.Delete("id1", AccountSource.ClaudeWebToken));
        Assert.Null(store.Get("id1", AccountSource.ClaudeWebToken));
    }

    [Fact]
    public void Renaming_an_account_does_not_move_or_orphan_the_secret()
    {
        var store = new InMemorySecretStore();
        var account = new Account { Source = AccountSource.ClaudeWebToken, Nickname = "Work" };
        store.Set(account.Id, account.Source, "tok");

        account.Nickname = "Personal"; // rename only changes metadata

        // Same stable id => same secret, no orphan.
        Assert.Equal("tok", store.Get(account.Id, account.Source));
    }

    [Fact]
    public void Different_sources_for_same_id_are_separate_entries()
    {
        var store = new InMemorySecretStore();
        store.Set("id1", AccountSource.ClaudeWebToken, "web");
        store.Set("id1", AccountSource.AnthropicApiKey, "api");

        Assert.Equal("web", store.Get("id1", AccountSource.ClaudeWebToken));
        Assert.Equal("api", store.Get("id1", AccountSource.AnthropicApiKey));
    }
}
