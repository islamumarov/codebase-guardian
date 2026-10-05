using System.Text.Json.Serialization;
using CodebaseGuardian.Checks;
using CodebaseGuardian.Dependencies;
using CodebaseGuardian.Json;
using CodebaseGuardian.Security;

namespace CodebaseGuardian.Scanning;

/// <summary>The outcome of one <c>full_scan</c>. <paramref name="Markdown"/> is the rendered report and never holds a raw secret.</summary>
public sealed record ScanReport(
    string ScanId,
    [property: JsonConverter(typeof(UtcTimestampJsonConverter))] DateTimeOffset StartedAt,
    TimeSpan Duration,
    string? HeadSha,
    IReadOnlyList<SecretFinding> SecretFindings,
    DependencyAuditReport? Dependencies,
    CheckRun? Checks,
    string? ChecksSkippedReason,
    IReadOnlyList<string> StepErrors,
    string Markdown)
{
    public string ReportUri => $"guardian://scans/{ScanId}/report";

    public bool? ChecksPassed => Checks?.Passed;
}
