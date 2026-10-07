using System.ComponentModel;
using System.Text.Json;
using CodebaseGuardian.Git;
using CodebaseGuardian.Sources;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodebaseGuardian.Tools;

/// <summary>Result of <c>recent_commits</c>; wrapped in an object because structured content must be one.</summary>
public sealed record RecentCommits(IReadOnlyList<CommitInfo> Commits);

[McpServerToolType]
public sealed class RepositoryTools(IRepositorySource git)
{
    private const int MaxCommits = 100;
    private const int MaxPatchBytes = 200_000;

    // Nulls are emitted: the advertised output schemas list every property (nullable ones included) as required.
    private static readonly JsonSerializerOptions StructuredOptions = new(JsonSerializerDefaults.Web);

    private static CallToolResult Structured<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, StructuredOptions);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = element.GetRawText() }],
            StructuredContent = element,
        };
    }

    [McpServerTool(Name = "repo_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(RepoStatus))]
    [Description("Current state of the watched Git repository: branch, HEAD commit, upstream and ahead/behind counts, staged and unstaged changes, untracked and conflicted files. Use it first to see what is going on in the working tree.")]
    public Task<CallToolResult> RepoStatus(CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync(async () => Structured(await git.GetStatusAsync(cancellationToken)));

    [McpServerTool(Name = "recent_commits", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Latest commits, newest first. Use it to see what changed recently or who changed it. Returns an empty list for a repository without commits.")]
    public Task<RecentCommits> RecentCommits(
        [Description("How many commits to return, 1 to 100 (default 10).")] int limit = 10,
        [Description("Branch, tag or revision to start from, for example 'main' or 'HEAD~3'. Defaults to the current HEAD.")] string? branch = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync(async () =>
        {
            if (branch is not null)
            {
                GitRevision.Require(branch, nameof(branch));
            }

            var commits = await git.GetRecentCommitsAsync(Math.Clamp(limit, 1, MaxCommits), branch, cancellationToken);
            return new RecentCommits(commits);
        });

    [McpServerTool(Name = "diff_summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(DiffSummary))]
    [Description("Per-file insertion and deletion counts plus a size-capped patch. With no arguments: working tree (staged and unstaged) against HEAD. With 'from': working tree against that revision. With 'from' and 'to': the changes between the two revisions. Untracked files are not included.")]
    public Task<CallToolResult> DiffSummary(
        [Description("Base revision (branch, tag or SHA). Omit to compare against HEAD.")] string? from = null,
        [Description("Target revision. Requires 'from'. Omit to compare against the working tree.")] string? to = null,
        [Description("Maximum patch size in bytes, 0 to 200000 (default 20000); 0 returns the statistics only.")] int maxPatchBytes = 20_000,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync(async () =>
        {
            if (to is not null && from is null)
            {
                throw new ArgumentException("`to` requires `from`.");
            }

            return Structured(await git.GetDiffSummaryAsync(from, to, Math.Clamp(maxPatchBytes, 0, MaxPatchBytes), cancellationToken));
        });
}
