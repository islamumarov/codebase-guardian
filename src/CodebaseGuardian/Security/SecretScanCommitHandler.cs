using System.Text.Json.Nodes;
using CodebaseGuardian.Git;
using CodebaseGuardian.Watching;
using Mcp.Events;
using Microsoft.Extensions.Logging;

namespace CodebaseGuardian.Security;

/// <summary>
/// Scans each new commit's patch (capped at 5 MB) and publishes <c>security.secret_detected</c> when it adds secrets.
/// Runs inline in the watcher's poll loop, which is fine for one capped patch per commit. A failure on one commit is
/// logged (never with secret values) and does not stop the others.
/// </summary>
public sealed class SecretScanCommitHandler(ISecretScanner scanner, IEventPublisher publisher, ILogger<SecretScanCommitHandler> logger)
    : IRepositoryChangeHandler
{
    public async Task OnNewCommitsAsync(IReadOnlyList<CommitInfo> commits, CancellationToken cancellationToken)
    {
        foreach (var commit in commits)
        {
            try
            {
                var outcome = await scanner.ScanCommitAsync(commit.Sha, cancellationToken);
                foreach (var warning in outcome.Warnings)
                {
                    logger.LogWarning("Secret scan of commit {Sha}: {Warning}", commit.Sha, warning);
                }

                var findings = outcome.Findings;
                if (findings.Count > 0)
                {
                    await publisher.PublishAsync(
                        GuardianEventNames.SecuritySecretDetected,
                        SecretEventPayload.Create("commit", commit.Sha, findings),
                        cancellationToken: cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Secret scan of commit {Sha} failed.", commit.Sha);
            }
        }
    }
}

internal static class SecretEventPayload
{
    public static JsonObject Create(string source, string? commitSha, IReadOnlyList<SecretFinding> findings) => new()
    {
        ["source"] = source,
        ["commitSha"] = commitSha,
        ["findings"] = new JsonArray([.. findings.Select(f => (JsonNode?)new JsonObject
        {
            ["ruleId"] = f.RuleId, ["path"] = f.Path, ["line"] = f.Line, ["redacted"] = f.Redacted,
        })]),
        ["suggestedSkill"] = SuggestedSkills.SecurityAudit,
    };
}
