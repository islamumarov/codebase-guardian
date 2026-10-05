namespace CodebaseGuardian.Dependencies;

public interface IDependencyAuditor
{
    /// <summary>
    /// Audits the ecosystems whose manifests the repository contains. A missing toolchain or a failing command is
    /// reported in that ecosystem's report; it never throws for them.
    /// </summary>
    Task<DependencyAuditReport> AuditAsync(bool includeOutdated, IProgress<string>? progress, CancellationToken cancellationToken);
}
