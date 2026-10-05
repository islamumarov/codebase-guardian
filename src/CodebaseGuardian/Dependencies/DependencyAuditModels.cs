namespace CodebaseGuardian.Dependencies;

public sealed record VulnerablePackage(string Ecosystem, string Package, string Version, string Severity, string? AdvisoryUrl, string? Project);

public sealed record OutdatedPackage(string Ecosystem, string Package, string Current, string Latest, string? Project);

/// <summary><paramref name="Status"/> is "ok", "skipped" (toolchain missing) or "failed".</summary>
public sealed record EcosystemReport(
    string Ecosystem, string Status, string? Reason,
    IReadOnlyList<VulnerablePackage> Vulnerable, IReadOnlyList<OutdatedPackage> Outdated)
{
    public static EcosystemReport Skipped(string ecosystem, string reason) => new(ecosystem, "skipped", reason, [], []);

    public static EcosystemReport Failed(string ecosystem, string reason) => new(ecosystem, "failed", reason, [], []);
}

public sealed record DependencyAuditReport(IReadOnlyList<EcosystemReport> Ecosystems)
{
    public int VulnerableCount => Ecosystems.Sum(e => e.Vulnerable.Count);

    public int OutdatedCount => Ecosystems.Sum(e => e.Outdated.Count);
}
