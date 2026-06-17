using Xunit;

namespace UsageWidget.Core.Tests;

/// <summary>
/// §12 merge-gating safety: committed fixtures must never contain real secrets/identifiers.
/// </summary>
public class FixtureSafetyTests
{
    [Fact]
    public void All_committed_fixtures_are_free_of_secrets()
    {
        var offenders = new List<string>();
        foreach (var file in Fixtures.AllFiles())
        {
            var findings = FixtureSafety.Scan(File.ReadAllText(file));
            if (findings.Count > 0)
            {
                offenders.Add($"{Path.GetFileName(file)}: {string.Join(", ", findings)}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Fixtures contain secrets:\n" + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("Authorization: Bearer sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWx")]
    [InlineData("\"refresh_token\": \"rt-9f8e7d6c5b4a3f2e1d\"")]
    [InlineData("login as real.person@gmail.com")]
    [InlineData("token eyJhbGciOiJI.eyJzdWIiOi.SflKxwRJSME")]
    public void Scanner_flags_known_bad_samples(string bad)
    {
        Assert.NotEmpty(FixtureSafety.Scan(bad));
    }

    [Fact]
    public void Scanner_treats_reserved_example_emails_as_safe()
    {
        Assert.Empty(FixtureSafety.Scan("demo-user@example.com"));
    }
}
