using CodebaseGuardian.Checks;
using CodebaseGuardian.Dependencies;
using CodebaseGuardian.Scanning;
using CodebaseGuardian.Security;

namespace CodebaseGuardian.Tests.App;

public class ScanReportRendererTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static ScanReport Report(
        IReadOnlyList<SecretFinding>? secrets = null, DependencyAuditReport? dependencies = null, CheckRun? checks = null,
        string? skipped = null, IReadOnlyList<string>? errors = null) =>
        new("scan_20261005120000_ab12", Started, TimeSpan.FromMilliseconds(1500), "abc123",
            secrets ?? [], dependencies, checks, skipped, errors ?? [], "");

    [Fact]
    public void A_small_report_renders_exactly_the_golden_markdown()
    {
        var report = Report(
            [new SecretFinding("aws-access-key-id", ".env", 3, "AKIA…MPLE")],
            new DependencyAuditReport([new EcosystemReport("npm", "ok", null,
                [new VulnerablePackage("npm", "lodash", "4.17.0", "high", "https://example.com/advisory", "package.json")],
                [new OutdatedPackage("npm", "left-pad", "1.0.0", "2.0.0", "package.json")])]),
            new CheckRun("run_20261005120001_cd34", "dotnet test", 1, false, false, Started, TimeSpan.FromSeconds(2),
                "Failed: 2, Passed: 5", ["Tests.A", "Tests.B"], "log", "scan", "abc123"));

        var markdown = ScanReportRenderer.Render(report, "/work/repo");

        Assert.Equal(
            """
            # Guardian scan scan_20261005120000_ab12

            - Repository: /work/repo
            - HEAD: abc123
            - Started: 2026-10-05T12:00:00.000Z
            - Duration: 1.5 s

            ## Secrets

            | Rule | Path | Line | Value |
            |---|---|---|---|
            | aws-access-key-id | .env | 3 | AKIA…MPLE |

            ## Dependencies

            ### npm (ok)

            | Package | Version | Severity | Advisory | Project |
            |---|---|---|---|---|
            | lodash | 4.17.0 | high | https://example.com/advisory | package.json |

            Outdated packages: 1

            ## Checks

            - Command: `dotnet test`
            - Result: failed (exit code 1)
            - Summary: Failed: 2, Passed: 5
            - Failed tests:
              - Tests.A
              - Tests.B
            - Log: guardian://checks/run_20261005120001_cd34/log

            """.ReplaceLineEndings("\n"),
            markdown.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Empty_sections_say_so_and_skipped_checks_and_errors_are_explained()
    {
        var markdown = ScanReportRenderer.Render(
            Report(dependencies: new DependencyAuditReport([EcosystemReport.Skipped("nuget", "dotnet not found")]),
                skipped: "Checks not requested.", errors: ["dependencies: boom"]),
            "/r");

        Assert.Contains("No secrets found.", markdown);
        Assert.Contains("### nuget (skipped)", markdown);
        Assert.Contains("Reason: dotnet not found", markdown);
        Assert.Contains("Checks not requested.", markdown);
        Assert.Contains("## Errors", markdown);
        Assert.Contains("- dependencies: boom", markdown);
    }

    [Fact]
    public void The_errors_section_is_absent_without_errors_and_failed_tests_are_capped_at_twenty()
    {
        var tests = Enumerable.Range(1, 25).Select(i => $"T{i}").ToList();
        var run = new CheckRun("run_1", "dotnet test", 1, false, false, Started, TimeSpan.Zero, "s", tests, "", "scan", null);

        var markdown = ScanReportRenderer.Render(Report(checks: run), "/r");

        Assert.DoesNotContain("## Errors", markdown);
        Assert.Contains("  - T20", markdown);
        Assert.DoesNotContain("  - T21", markdown);
        Assert.Contains("  - and 5 more", markdown);
    }

    [Fact]
    public void Table_cells_cannot_break_out_of_their_row()
    {
        var markdown = ScanReportRenderer.Render(Report([new SecretFinding("r", "a|b\nc.txt", 1, "x")]), "/r");

        Assert.Contains("| r | a\\|b c.txt | 1 | x |", markdown);
    }

    [Fact]
    public void Backslashes_and_backticks_are_escaped_in_cells_and_the_command_cannot_close_its_code_span()
    {
        var run = new CheckRun("run_1", "dotnet `test`", 0, true, false, Started, TimeSpan.Zero, "ok", [], "", "scan", null);

        var markdown = ScanReportRenderer.Render(Report([new SecretFinding("r", "a\\|b`c", 1, "x")], checks: run), "/r");

        Assert.Contains("| r | a\\\\\\|b\\`c | 1 | x |", markdown);
        Assert.Contains("- Command: `dotnet 'test'`", markdown);
    }
}
