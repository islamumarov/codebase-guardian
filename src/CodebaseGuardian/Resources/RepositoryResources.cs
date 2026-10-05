using System.ComponentModel;
using System.Text.Json;
using CodebaseGuardian.Git;
using CodebaseGuardian.Tools;
using ModelContextProtocol.Server;

namespace CodebaseGuardian.Resources;

[McpServerResourceType]
public sealed class RepositoryResources(IGitRepository git)
{
    private const int RecentCommitCount = 20;

    // Same shape the tools produce: camelCase, null properties emitted.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerResource(UriTemplate = "guardian://repo/status", Name = "repo-status", MimeType = "application/json")]
    [Description("Current repository status: branch, HEAD, upstream, staged, unstaged, untracked and conflicted files. Same content as the repo_status tool.")]
    public async Task<string> Status(CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await ToolErrors.RunAsync(() => git.GetStatusAsync(cancellationToken)), Json);

    [McpServerResource(UriTemplate = "guardian://repo/commits/recent", Name = "recent-commits", MimeType = "application/json")]
    [Description("The 20 most recent commits on the current branch, newest first.")]
    public async Task<string> RecentCommitsResource(CancellationToken cancellationToken = default)
    {
        var commits = await ToolErrors.RunAsync(() => git.GetRecentCommitsAsync(RecentCommitCount, null, cancellationToken));
        return JsonSerializer.Serialize(new RecentCommits(commits), Json);
    }
}
