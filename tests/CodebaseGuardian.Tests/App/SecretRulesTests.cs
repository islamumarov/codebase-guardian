using CodebaseGuardian.Security;
using CodebaseGuardian.Tests.Infrastructure;

namespace CodebaseGuardian.Tests.App;

public class SecretRulesTests
{
    public static TheoryData<string, string, string> Positives => new()
    {
        { "aws-access-key-id", FakeSecrets.AwsAccessKeyId(), "key = " },
        { "github-token", FakeSecrets.GitHubToken(), "x " },
        { "github-fine-grained-pat", FakeSecrets.GitHubFineGrainedPat(), "x " },
        { "slack-token", FakeSecrets.SlackToken(), "x " },
        { "stripe-live-key", FakeSecrets.StripeLiveKey(), "x " },
        { "private-key", FakeSecrets.PrivateKeyHeader(), "" },
        { "jwt", FakeSecrets.Jwt(), "Bearer " },
        { "generic-secret-assignment", "secret: " + "Zx9fQ2mK8vLp4TnR7sW1", "" },
    };

    public static TheoryData<string, string> Negatives => new()
    {
        { "aws-access-key-id", "AKIA" + "SHORT" },
        { "github-token", "ghp_" + "tooShort" },
        { "github-fine-grained-pat", "github_" + "pat_" + "short" },
        { "slack-token", "xoxb-" + "1" },
        { "stripe-live-key", "sk_" + "test_" + "4eC39HqLyjWDarjtT1zdp7dc" },
        { "private-key", "-----BEGIN " + "CERTIFICATE-----" },
        { "jwt", "eyJ" + "short.eyJ" + "short.sig" },
        { "generic-secret-assignment", "tokens = " + "Zx9fQ2mK8vLp4TnR7sW1" },
    };

    private static IReadOnlyList<SecretFinding> Scan(string text) =>
        new SecretScanner(null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<SecretScanner>.Instance).ScanText("f.txt", text);

    [Fact]
    public void The_rule_ids_are_the_contract()
    {
        Assert.Equal(
            ["aws-access-key-id", "github-token", "github-fine-grained-pat", "slack-token", "stripe-live-key", "private-key", "jwt", "generic-secret-assignment"],
            SecretRules.All.Select(r => r.Id));
    }

    [Theory]
    [MemberData(nameof(Positives))]
    public void Each_rule_detects_its_secret(string ruleId, string secret, string prefix)
    {
        var finding = Assert.Single(Scan(prefix + secret), f => f.RuleId == ruleId);

        Assert.Equal(1, finding.Line);
        Assert.DoesNotContain(secret, finding.Redacted);
    }

    [Theory]
    [MemberData(nameof(Negatives))]
    public void Each_rule_ignores_look_alikes(string ruleId, string text) =>
        Assert.DoesNotContain(Scan(text), f => f.RuleId == ruleId);

    [Fact]
    public void The_generic_rule_rejects_a_low_entropy_value() =>
        Assert.Empty(Scan(FakeSecrets.LowEntropyAssignment()));

    [Fact]
    public void The_generic_rule_accepts_a_high_entropy_value() =>
        Assert.Equal("generic-secret-assignment", Assert.Single(Scan(FakeSecrets.GenericAssignment())).RuleId);

    [Theory]
    [InlineData("", "****")]
    [InlineData("short", "****")]
    [InlineData("12345678901", "****")]
    [InlineData("123456789012", "1234****9012")]
    public void Redaction_keeps_the_ends_of_long_values_only(string secret, string expected) =>
        Assert.Equal(expected, Redactor.Redact(secret));

    [Fact]
    public void Entropy_is_bits_per_character()
    {
        Assert.Equal(0, SecretRules.Entropy("aaaa"));
        Assert.Equal(2, SecretRules.Entropy("abcd"), 6);
    }
}
