using System.ComponentModel;
using System.Text.Json.Serialization;
using CodebaseGuardian.Git;
using CodebaseGuardian.Security;
using CodebaseGuardian.Watching;
using Mcp.Events;
using ModelContextProtocol.Server;

namespace CodebaseGuardian.Tools;

[JsonConverter(typeof(JsonStringEnumConverter<SecretScanScope>))]
public enum SecretScanScope
{
    [JsonStringEnumMemberName("working_tree")] WorkingTree,
    [JsonStringEnumMemberName("staged")] Staged,
    [JsonStringEnumMemberName("commit")] Commit,
}

public sealed record SecretScanResult(string Scope, IReadOnlyList<SecretFinding> Findings, int Count, bool Complete, IReadOnlyList<string> Warnings);

[McpServerToolType]
public sealed class SecurityTools(ISecretScanner scanner, IEventPublisher publisher)
{
    [McpServerTool(Name = "scan_secrets", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Scans for committed or pending secrets (cloud keys, tokens, private keys, hard-coded passwords) and reports each finding with its rule id, file, line and a redacted value; the full secret is never returned. Scope: working_tree (default) scans the files in the repository, staged scans what is staged for the next commit, commit scans one commit's changes and needs the commit argument. Files matched by .guardianignore are skipped in a working-tree scan.")]
    public Task<SecretScanResult> ScanSecrets(
        [Description("What to scan: working_tree (default), staged or commit.")] SecretScanScope scope = SecretScanScope.WorkingTree,
        [Description("The commit to scan (SHA, branch, tag or HEAD~n). Required when scope is commit.")] string? commit = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync(async () =>
        {
            string name;
            SecretScanOutcome outcome;
            switch (scope)
            {
                case SecretScanScope.Staged:
                    name = "staged";
                    outcome = await scanner.ScanStagedAsync(cancellationToken);
                    break;
                case SecretScanScope.Commit:
                    name = "commit";
                    if (string.IsNullOrWhiteSpace(commit))
                    {
                        throw new ArgumentException("The commit argument is required when scope is commit.", nameof(commit));
                    }

                    outcome = await scanner.ScanCommitAsync(GitRevision.Require(commit, nameof(commit)), cancellationToken);
                    break;
                default:
                    name = "working_tree";
                    outcome = await scanner.ScanWorkingTreeAsync(cancellationToken);
                    break;
            }

            var findings = outcome.Findings;
            if (findings.Count > 0)
            {
                await publisher.PublishAsync(
                    GuardianEventNames.SecuritySecretDetected,
                    SecretEventPayload.Create("scan", scope == SecretScanScope.Commit ? commit : null, findings),
                    cancellationToken: cancellationToken);
            }

            return new SecretScanResult(name, findings, findings.Count, outcome.Complete, outcome.Warnings);
        });
}
