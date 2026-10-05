using System.Text.Json;
using CodebaseGuardian.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodebaseGuardian.Tests.App;

public class RepositoryToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<CallToolResult> CallAsync(
        InProcessMcpServer server, string tool, Dictionary<string, object?>? arguments = null) =>
        await server.Client.CallToolAsync(tool, arguments, cancellationToken: Ct);

    private static string TextOf(CallToolResult result) =>
        string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public async Task Tools_list_contains_repository_tools_with_read_only_annotations()
    {
        using var repo = TempGitRepo.Create();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var tools = await server.Client.ListToolsAsync(cancellationToken: Ct);

        foreach (var name in new[] { "repo_status", "recent_commits", "diff_summary" })
        {
            var tool = Assert.Single(tools, t => t.Name == name);
            Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.False(tool.ProtocolTool.Annotations?.DestructiveHint);
            Assert.True(tool.ProtocolTool.Annotations?.IdempotentHint);
            Assert.False(tool.ProtocolTool.Annotations?.OpenWorldHint);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        }
    }

    [Fact]
    public async Task Repo_status_reports_branch_staged_and_untracked()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("a.txt", "a");
        repo.Commit("first");
        repo.WriteFile("staged.txt", "s");
        repo.Git("add", "staged.txt");
        repo.WriteFile("new.txt", "n");
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var result = await CallAsync(server, "repo_status");

        Assert.NotEqual(true, result.IsError);
        var status = result.StructuredContent!.Value;
        Assert.Equal("main", status.GetProperty("branch").GetString());
        Assert.Equal(1, status.GetProperty("staged").GetArrayLength());
        Assert.Equal("staged.txt", status.GetProperty("staged")[0].GetProperty("path").GetString());
        Assert.Contains(status.GetProperty("untracked").EnumerateArray(), e => e.GetString() == "new.txt");
    }

    [Fact]
    public async Task Recent_commits_limit_returns_newest()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("a.txt", "a");
        repo.Commit("older");
        repo.WriteFile("a.txt", "b");
        repo.Commit("newest");
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var result = await CallAsync(server, "recent_commits", new() { ["limit"] = 1 });

        Assert.NotEqual(true, result.IsError);
        var commits = Commits(result);
        var only = Assert.Single(commits);
        Assert.Equal("newest", only.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Recent_commits_clamps_limit_to_100()
    {
        using var repo = TempGitRepo.Create();
        for (var i = 0; i < 101; i++)
        {
            repo.Git("commit", "--allow-empty", "-m", $"c{i}");
        }

        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var result = await CallAsync(server, "recent_commits", new() { ["limit"] = 1000 });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(100, Commits(result).Length);
    }

    [Fact]
    public async Task Recent_commits_rejects_option_like_branch_as_tool_error()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("a.txt", "a");
        repo.Commit("first");
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var result = await CallAsync(server, "recent_commits", new() { ["branch"] = "--all" });

        Assert.True(result.IsError);
        Assert.Contains("--all", TextOf(result));
    }

    [Fact]
    public async Task Recent_commits_unknown_branch_is_tool_error()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("a.txt", "a");
        repo.Commit("first");
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var result = await CallAsync(server, "recent_commits", new() { ["branch"] = "no-such-branch" });

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Diff_summary_reports_insertions_for_modified_tracked_file()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("a.txt", "one\n");
        repo.Commit("first");
        repo.WriteFile("a.txt", "one\ntwo\nthree\n");
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var result = await CallAsync(server, "diff_summary");

        Assert.NotEqual(true, result.IsError);
        var diff = result.StructuredContent!.Value;
        Assert.Equal(2, diff.GetProperty("insertions").GetInt32());
        Assert.Equal("a.txt", diff.GetProperty("files")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Diff_summary_with_to_but_no_from_is_tool_error()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("a.txt", "a");
        repo.Commit("first");
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var result = await CallAsync(server, "diff_summary", new() { ["to"] = "HEAD" });

        Assert.True(result.IsError);
        Assert.Contains("`to` requires `from`", TextOf(result));
    }

    [Fact]
    public async Task Diff_summary_clamps_max_patch_bytes()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("a.txt", "one\n");
        repo.Commit("first");
        repo.WriteFile("a.txt", "one\n" + new string('x', 500) + "\n");
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var result = await CallAsync(server, "diff_summary", new() { ["maxPatchBytes"] = -5 });

        Assert.NotEqual(true, result.IsError);
        var diff = result.StructuredContent!.Value;
        Assert.Equal("", diff.GetProperty("patch").GetString());
        Assert.True(diff.GetProperty("patchTruncated").GetBoolean());
    }

    [Fact]
    public async Task Brand_new_repository_does_not_fail_any_tool()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("first.txt", "hello\n");
        repo.Git("add", "first.txt");
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var status = await CallAsync(server, "repo_status");
        var commits = await CallAsync(server, "recent_commits");
        var diff = await CallAsync(server, "diff_summary");

        Assert.NotEqual(true, status.IsError);
        // The SDK omits null properties from structured content.
        Assert.False(status.StructuredContent!.Value.TryGetProperty("headSha", out var headSha) && headSha.ValueKind != JsonValueKind.Null);
        Assert.NotEqual(true, commits.IsError);
        Assert.Empty(Commits(commits));
        Assert.NotEqual(true, diff.IsError);
        Assert.Equal("first.txt",
            diff.StructuredContent!.Value.GetProperty("files")[0].GetProperty("path").GetString());
    }

    private static JsonElement[] Commits(CallToolResult result) =>
        result.StructuredContent!.Value.GetProperty("commits").EnumerateArray().ToArray();
}
