namespace CodebaseGuardian.Security;

/// <summary>One detected secret. <paramref name="Redacted"/> never contains the full secret; <paramref name="Line"/> is 1-based.</summary>
public sealed record SecretFinding(string RuleId, string Path, int Line, string Redacted);
