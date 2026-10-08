using System.Text.Json;
using CodebaseGuardian.Tests.Infrastructure;
using ModelContextProtocol.Protocol;

namespace CodebaseGuardian.Tests.App;

public class SecurityToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TempGitRepo RepoWithCommit()
    {
        var repo = TempGitRepo.Create();
        repo.WriteFile("README.md", "x");
        repo.Commit("initial");
        return repo;
    }

    private static Dictionary<string, object?> Args(string? scope = null, string? commit = null)
    {
        var args = new Dictionary<string, object?>();
        if (scope is not null) { args["scope"] = scope; }
        if (commit is not null) { args["commit"] = commit; }
        return args;
    }

    private static string TextOf(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public async Task Scan_secrets_is_read_only_and_declares_the_scope_enum()
    {
        using var repo = RepoWithCommit();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var tool = Assert.Single(await server.Client.ListToolsAsync(cancellationToken: Ct), t => t.Name == "scan_secrets");

        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        var scope = tool.JsonSchema.GetProperty("properties").GetProperty("scope");
        Assert.Equal(["working_tree", "staged", "commit"], scope.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Default_scope_scans_the_working_tree_and_publishes_a_scan_event()
    {
        using var repo = RepoWithCommit();
        repo.WriteFile("k.txt", FakeSecrets.SlackToken());
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);
        var cursor = await EventsPolling.GetCursorAsync(server, Ct);

        var result = await server.Client.CallToolAsync("scan_secrets", Args(), cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        var json = result.StructuredContent!.Value;
        Assert.Equal("working_tree", json.GetProperty("scope").GetString());
        Assert.Equal(1, json.GetProperty("count").GetInt32());
        var finding = json.GetProperty("findings")[0];
        Assert.Equal("slack-token", finding.GetProperty("ruleId").GetString());
        Assert.Equal("k.txt", finding.GetProperty("path").GetString());
        Assert.DoesNotContain(FakeSecrets.SlackToken(), json.GetRawText());

        var e = await EventsPolling.WaitForAsync(server, "security.secret_detected", cursor, null, EventsPolling.DefaultTimeout, Ct);
        var data = e["data"]!.AsObject();
        Assert.Equal("scan", (string?)data["source"]);
        Assert.Null((string?)data["commitSha"]);
    }

    [Fact]
    public async Task Clean_scan_returns_zero_and_publishes_nothing()
    {
        using var repo = RepoWithCommit();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);
        var cursor = await EventsPolling.GetCursorAsync(server, Ct);

        var result = await server.Client.CallToolAsync("scan_secrets", Args(), cancellationToken: Ct);

        var json = result.StructuredContent!.Value;
        Assert.Equal(0, json.GetProperty("count").GetInt32());
        Assert.True(json.GetProperty("complete").GetBoolean());
        Assert.Equal(JsonValueKind.Array, json.GetProperty("warnings").ValueKind);
        Assert.Equal(0, json.GetProperty("warnings").GetArrayLength());
        Assert.Empty(await EventsPolling.CollectAsync(server, "security.secret_detected", cursor, TimeSpan.FromMilliseconds(200), Ct));
    }

    [Fact]
    public async Task Staged_scope_sees_a_staged_secret()
    {
        using var repo = RepoWithCommit();
        repo.WriteFile("s.txt", FakeSecrets.GitHubToken());
        repo.Git("add", "s.txt");
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var result = await server.Client.CallToolAsync("scan_secrets", Args("staged"), cancellationToken: Ct);

        var json = result.StructuredContent!.Value;
        Assert.Equal("staged", json.GetProperty("scope").GetString());
        Assert.Equal("github-token", json.GetProperty("findings")[0].GetProperty("ruleId").GetString());
    }

    [Fact]
    public async Task Commit_scope_scans_the_commit_and_carries_its_revision_into_the_event()
    {
        using var repo = RepoWithCommit();
        repo.WriteFile("c.txt", FakeSecrets.AwsAccessKeyId());
        var sha = repo.Commit("leak");
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);
        var cursor = await EventsPolling.GetCursorAsync(server, Ct);

        var result = await server.Client.CallToolAsync("scan_secrets", Args("commit", sha), cancellationToken: Ct);

        Assert.Equal(1, result.StructuredContent!.Value.GetProperty("count").GetInt32());
        var e = await EventsPolling.WaitForAsync(server, "security.secret_detected", cursor, null, EventsPolling.DefaultTimeout, Ct);
        Assert.Equal(sha, (string?)e["data"]!["commitSha"]);
    }

    [Fact]
    public async Task Commit_scope_without_a_commit_is_a_tool_error()
    {
        using var repo = RepoWithCommit();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var result = await server.Client.CallToolAsync("scan_secrets", Args("commit"), cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("commit", TextOf(result));
    }

    [Theory]
    [InlineData("--output=/tmp/x")]
    [InlineData("a..b")]
    [InlineData("does-not-exist")]
    public async Task An_invalid_or_unknown_revision_is_a_tool_error(string revision)
    {
        using var repo = RepoWithCommit();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var result = await server.Client.CallToolAsync("scan_secrets", Args("commit", revision), cancellationToken: Ct);

        Assert.True(result.IsError);
    }
}
