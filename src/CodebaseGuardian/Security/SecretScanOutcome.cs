namespace CodebaseGuardian.Security;

/// <summary>What a secret scan found and whether it saw everything: an incomplete scan must never read as an all-clear.</summary>
public sealed record SecretScanOutcome(IReadOnlyList<SecretFinding> Findings, bool Complete, IReadOnlyList<string> Warnings);
