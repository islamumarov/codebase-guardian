using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using CodebaseGuardian.Scanning;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodebaseGuardian.Tools;

/// <summary>Result of <c>full_scan</c>; the Markdown report is behind <paramref name="ReportUri"/>.</summary>
public sealed record FullScanResult(
    string ScanId, string ReportUri, int SecretFindings, int VulnerablePackages, int OutdatedPackages, bool? ChecksPassed, long DurationMs)
{
    public static FullScanResult From(ScanReport report) => new(
        report.ScanId, report.ReportUri, report.SecretFindings.Count,
        report.Dependencies?.VulnerableCount ?? 0, report.Dependencies?.OutdatedCount ?? 0,
        report.ChecksPassed, (long)report.Duration.TotalMilliseconds);
}

[McpServerToolType]
public sealed class ScanTools(IFullScanService scans, ILogger<ScanTools> logger)
{
    // Nulls are emitted (checksPassed): the advertised output schema lists every property as required.
    private static readonly JsonSerializerOptions StructuredOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "full_scan", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(FullScanResult))]
    [Description("Runs as an MCP task; clients without task support should call `scan_secrets`, `audit_dependencies` and `run_checks` instead. Scans the working tree for secrets, audits NuGet and npm dependencies and (optionally) runs the project's build and tests, then stores a Markdown report that is readable at the returned reportUri (guardian://scans/{scanId}/report) and publishes scan.completed. Returns counts and the report link. Can take minutes.")]
    public Task<CallToolResult> FullScan(
        IProgress<ProgressNotificationValue> progress,
        [Description("Also run the build and tests (default true). Set to false to scan only for secrets and vulnerable dependencies.")] bool includeChecks = true,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync(async () =>
        {
            var report = await scans.RunAsync(includeChecks, new ProgressRelay(progress, logger), cancellationToken);
            var result = FullScanResult.From(report);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = Summarize(result, report) }],
                StructuredContent = JsonSerializer.SerializeToElement(result, StructuredOptions),
            };
        });

    private static string Summarize(FullScanResult result, ScanReport report)
    {
        var checks = result.ChecksPassed switch
        {
            true => "checks passed",
            false => "checks failed",
            null => "checks not run",
        };
        var errors = report.StepErrors.Count > 0 ? $", {report.StepErrors.Count.ToString(CultureInfo.InvariantCulture)} step error(s)" : "";
        return $"{result.SecretFindings} secret findings, {result.VulnerablePackages} vulnerable and {result.OutdatedPackages} outdated packages, {checks}{errors}. Report: {result.ReportUri}";
    }
}
