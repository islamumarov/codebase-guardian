using System.Globalization;
using System.Text;
using CodebaseGuardian.Dependencies;

namespace CodebaseGuardian.Scanning;

/// <summary>Renders the Markdown of a scan report. Pure: the same report always gives the same text.</summary>
public static class ScanReportRenderer
{
    private const int MaxFailedTests = 20;

    public static string Render(ScanReport report, string repositoryPath)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        text.Append("# Guardian scan ").Append(report.ScanId).Append("\n\n");
        text.Append("- Repository: ").Append(repositoryPath).Append('\n');
        text.Append("- HEAD: ").Append(report.HeadSha ?? "none").Append('\n');
        text.Append("- Started: ").Append(report.StartedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("- Duration: ").Append(report.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s\n\n");

        RenderSecrets(text, report);
        RenderDependencies(text, report);
        RenderChecks(text, report);

        if (report.StepErrors.Count > 0)
        {
            text.Append("## Errors\n\n");
            foreach (var error in report.StepErrors)
            {
                text.Append("- ").Append(Inline(error)).Append('\n');
            }
        }

        return text.ToString();
    }

    private static void RenderSecrets(StringBuilder text, ScanReport report)
    {
        text.Append("## Secrets\n\n");
        if (report.SecretFindings.Count == 0)
        {
            text.Append("No secrets found.\n\n");
            return;
        }

        text.Append("| Rule | Path | Line | Value |\n|---|---|---|---|\n");
        foreach (var finding in report.SecretFindings)
        {
            text.Append($"| {Cell(finding.RuleId)} | {Cell(finding.Path)} | {finding.Line.ToString(CultureInfo.InvariantCulture)} | {Cell(finding.Redacted)} |\n");
        }

        text.Append('\n');
    }

    private static void RenderDependencies(StringBuilder text, ScanReport report)
    {
        text.Append("## Dependencies\n\n");
        if (report.Dependencies is null)
        {
            text.Append("Not available; see Errors.\n\n");
            return;
        }

        if (report.Dependencies.Ecosystems.Count == 0)
        {
            text.Append("No dependency manifests found.\n\n");
            return;
        }

        foreach (var ecosystem in report.Dependencies.Ecosystems)
        {
            text.Append("### ").Append(ecosystem.Ecosystem).Append(" (").Append(ecosystem.Status).Append(")\n\n");
            if (ecosystem.Reason is not null)
            {
                text.Append("Reason: ").Append(Inline(ecosystem.Reason)).Append("\n\n");
            }

            if (ecosystem.Vulnerable.Count > 0)
            {
                text.Append("| Package | Version | Severity | Advisory | Project |\n|---|---|---|---|---|\n");
                foreach (var package in ecosystem.Vulnerable)
                {
                    text.Append($"| {Cell(package.Package)} | {Cell(package.Version)} | {Cell(package.Severity)} | {Cell(package.AdvisoryUrl)} | {Cell(package.Project)} |\n");
                }

                text.Append('\n');
            }

            if (ecosystem.Status == "ok")
            {
                text.Append("Outdated packages: ").Append(ecosystem.Outdated.Count.ToString(CultureInfo.InvariantCulture)).Append("\n\n");
            }
        }
    }

    private static void RenderChecks(StringBuilder text, ScanReport report)
    {
        text.Append("## Checks\n\n");
        var run = report.Checks;
        if (run is null)
        {
            text.Append(report.ChecksSkippedReason ?? "No checks were run.").Append("\n\n");
            return;
        }

        text.Append("- Command: `").Append(Inline(run.Command).Replace("`", "'", StringComparison.Ordinal)).Append("`\n");
        text.Append("- Result: ").Append(run.TimedOut ? "timed out" : run.Passed ? "passed" : $"failed (exit code {run.ExitCode.ToString(CultureInfo.InvariantCulture)})").Append('\n');
        text.Append("- Summary: ").Append(Inline(run.Summary)).Append('\n');
        if (run.FailedTests.Count > 0)
        {
            text.Append("- Failed tests:\n");
            foreach (var test in run.FailedTests.Take(MaxFailedTests))
            {
                text.Append("  - ").Append(Inline(test)).Append('\n');
            }

            if (run.FailedTests.Count > MaxFailedTests)
            {
                text.Append("  - and ").Append((run.FailedTests.Count - MaxFailedTests).ToString(CultureInfo.InvariantCulture)).Append(" more\n");
            }
        }

        text.Append("- Log: ").Append(run.LogUri).Append('\n');
    }

    /// <summary>One table cell: no line breaks; backslash, <c>|</c> and a backtick are escaped so it cannot start another column or code span.</summary>
    private static string Cell(string? value) => Inline(value ?? "")
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal);

    private static string Inline(string value) => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
