using System.Text.RegularExpressions;

namespace CodebaseGuardian.Security;

/// <summary>The built-in rules. The ids are part of the contract; skills refer to them.</summary>
public static class SecretRules
{
    private const RegexOptions Options = RegexOptions.CultureInvariant;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    public static IReadOnlyList<SecretRule> All { get; } =
    [
        Rule("aws-access-key-id", "AWS access key id", @"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b"),
        Rule("github-token", "GitHub token", @"\bgh[pousr]_[A-Za-z0-9]{36,255}\b"),
        Rule("github-fine-grained-pat", "GitHub fine-grained personal access token", @"\bgithub_pat_[A-Za-z0-9_]{82}\b"),
        Rule("slack-token", "Slack token", @"\bxox[baprs]-[A-Za-z0-9-]{10,}\b"),
        Rule("stripe-live-key", "Stripe live key", @"\b(?:sk|rk)_live_[A-Za-z0-9]{20,}\b"),
        Rule("private-key", "Private key header", @"-----BEGIN (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY(?: BLOCK)?-----"),
        Rule("jwt", "JSON Web Token", @"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b"),
        Rule("generic-secret-assignment", "Hard-coded secret assigned to a key, secret, token or password",
            @"(?i)\b(?:api[_-]?key|secret|token|passw(?:or)?d)\b\s*[:=]\s*['""]?([A-Za-z0-9_\-+/=]{16,})", secretGroup: 1, minEntropy: 3.5),
    ];

    private static SecretRule Rule(string id, string description, string pattern, int secretGroup = 0, double? minEntropy = null) =>
        new(id, description, new Regex(pattern, Options, Timeout), secretGroup, minEntropy);

    /// <summary>Shannon entropy in bits per character.</summary>
    public static double Entropy(string value)
    {
        if (value.Length == 0)
        {
            return 0;
        }

        double entropy = 0;
        foreach (var count in value.GroupBy(c => c).Select(g => g.Count()))
        {
            var p = (double)count / value.Length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }
}
