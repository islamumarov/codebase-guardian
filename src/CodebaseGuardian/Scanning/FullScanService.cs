using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CodebaseGuardian.Checks;
using CodebaseGuardian.Dependencies;
using CodebaseGuardian.Git;
using CodebaseGuardian.Security;
using CodebaseGuardian.Watching;
using Mcp.Events;

namespace CodebaseGuardian.Scanning;

public sealed class FullScanService(
    ISecretScanner secrets,
    IDependencyAuditor dependencies,
    ICheckRunner checks,
    ICheckCommandResolver resolver,
    IGitRepository git,
    IEventPublisher publisher,
    ScanReportStore store,
    TimeProvider time) : IFullScanService
{
    public async Task<ScanReport> RunAsync(bool includeChecks, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var startedAt = time.GetUtcNow();
        var clock = time.GetTimestamp();
        var errors = new List<string>();

        string? headSha = null;
        await Step("head", async () => headSha = await git.GetHeadShaAsync(cancellationToken));

        progress?.Report("secrets");
        IReadOnlyList<SecretFinding> findings = [];
        await Step("secrets", async () => findings = await secrets.ScanWorkingTreeAsync(cancellationToken));

        progress?.Report("dependencies");
        DependencyAuditReport? audit = null;
        await Step("dependencies", async () => audit = await dependencies.AuditAsync(true, progress: null, cancellationToken));

        CheckRun? run = null;
        string? skipped = null;
        if (includeChecks)
        {
            progress?.Report("checks");
            if (resolver.Resolve() is null)
            {
                skipped = "No check command configured or detected.";
            }
            else
            {
                await Step("checks", async () => run = await checks.RunAsync("scan", headSha, progress: null, cancellationToken));
            }
        }
        else
        {
            skipped = "Checks not requested.";
        }

        cancellationToken.ThrowIfCancellationRequested();
        var draft = new ScanReport(
            NewScanId(startedAt), startedAt, time.GetElapsedTime(clock), headSha, findings, Redact(audit), Redact(run), skipped, errors, "");
        var report = draft with { Markdown = ScanReportRenderer.Render(draft, git.RootPath) };

        store.Add(report);
        await publisher.PublishAsync(GuardianEventNames.ScanCompleted, Payload(report), cancellationToken: cancellationToken);
        return report;

        async Task Step(string name, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                errors.Add($"{name}: {secrets.RedactSecrets(exception.Message)}");
            }
        }
    }

    // Free text from tools and process output can contain a secret; the renderer stays pure, so redact here.
    private CheckRun? Redact(CheckRun? run) => run is null ? null : run with
    {
        Summary = secrets.RedactSecrets(run.Summary),
        FailedTests = [.. run.FailedTests.Select(secrets.RedactSecrets)],
        Command = secrets.RedactSecrets(run.Command),
    };

    private DependencyAuditReport? Redact(DependencyAuditReport? report) => report is null ? null : new DependencyAuditReport(
        [.. report.Ecosystems.Select(e => e with { Reason = e.Reason is null ? null : secrets.RedactSecrets(e.Reason) })]);

    private static JsonObject Payload(ScanReport report)
    {
        var vulnerable = report.Dependencies?.VulnerableCount ?? 0;
        var data = new JsonObject
        {
            ["scanId"] = report.ScanId,
            ["reportUri"] = report.ReportUri,
            ["secretFindings"] = report.SecretFindings.Count,
            ["vulnerablePackages"] = vulnerable,
            ["checksPassed"] = report.ChecksPassed is { } passed ? JsonValue.Create(passed) : null,
        };
        if (report.SecretFindings.Count > 0)
        {
            data["suggestedSkill"] = SuggestedSkills.SecurityAudit;
        }
        else if (vulnerable > 0)
        {
            data["suggestedSkill"] = SuggestedSkills.DependencyHygiene;
        }

        return data;
    }

    private static string NewScanId(DateTimeOffset startedAt) =>
        $"scan_{startedAt.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}";
}
