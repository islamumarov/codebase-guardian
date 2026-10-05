namespace CodebaseGuardian.Scanning;

public interface IFullScanService
{
    /// <summary>
    /// Scans for secrets, audits dependencies and (optionally) runs the checks, stores the Markdown report and publishes
    /// <c>scan.completed</c>. A failing step is recorded in the report and the scan continues; cancellation propagates and
    /// stores and publishes nothing. Progress messages are <c>secrets</c>, <c>dependencies</c> and <c>checks</c>.
    /// </summary>
    Task<ScanReport> RunAsync(bool includeChecks, IProgress<string>? progress, CancellationToken cancellationToken);
}
