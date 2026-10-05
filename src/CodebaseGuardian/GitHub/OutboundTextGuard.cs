using CodebaseGuardian.Security;
using ModelContextProtocol;

namespace CodebaseGuardian.GitHub;

/// <summary>Refuses to send text that contains a secret to GitHub.</summary>
public static class OutboundTextGuard
{
    /// <summary>Throws for the first finding; the message carries the redacted form only, never the raw value.</summary>
    public static void EnsureNoSecrets(ISecretScanner scanner, string field, string text)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(text);
        var findings = scanner.ScanText(field, text);
        if (findings.Count > 0)
        {
            var first = findings[0];
            throw new McpException(
                $"The {field} contains what looks like a secret ({first.RuleId} {first.Redacted} on line {first.Line}); remove it before sending.");
        }
    }
}
