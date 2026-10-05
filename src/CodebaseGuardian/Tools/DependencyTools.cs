using System.ComponentModel;
using System.Text.Json;
using CodebaseGuardian.Dependencies;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodebaseGuardian.Tools;

[McpServerToolType]
public sealed class DependencyTools(IDependencyAuditor auditor)
{
    // Nulls are emitted: the advertised output schema lists every property (nullable ones included) as required.
    private static readonly JsonSerializerOptions StructuredOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "audit_dependencies", ReadOnly = true, Destructive = false, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(DependencyAuditReport))]
    [Description("Audits the project's NuGet and npm dependencies for known vulnerabilities and, optionally, outdated versions. Returns one report per ecosystem found in the repository with its status (ok, skipped when the toolchain is not installed, or failed with a reason), the vulnerable packages with severity and advisory link, and the outdated packages with current and latest versions. Queries the package feeds, so it needs network access and can take a while.")]
    public Task<CallToolResult> AuditDependencies(
        IProgress<ProgressNotificationValue> progress,
        [Description("Also list outdated packages (default true). Set to false for a faster vulnerability-only audit.")] bool includeOutdated = true,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync(async () =>
        {
            var report = await auditor.AuditAsync(includeOutdated, new ProgressRelay(progress), cancellationToken);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = Summarize(report) }],
                StructuredContent = JsonSerializer.SerializeToElement(report, StructuredOptions),
            };
        });

    private static string Summarize(DependencyAuditReport report)
    {
        if (report.Ecosystems.Count == 0)
        {
            return "No dependency manifests found.";
        }

        var parts = report.Ecosystems.Select(e => e.Status == "ok" ? e.Ecosystem : $"{e.Ecosystem} {e.Status}");
        return $"{report.VulnerableCount} vulnerable, {report.OutdatedCount} outdated packages ({string.Join(", ", parts)}).";
    }
}
