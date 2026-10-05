using System.Text.RegularExpressions;

namespace CodebaseGuardian.Security;

/// <summary>A detection rule. <paramref name="SecretGroup"/> is the regex group holding the secret value (0 = whole match).</summary>
public sealed record SecretRule(string Id, string Description, Regex Pattern, int SecretGroup = 0, double? MinEntropy = null);
