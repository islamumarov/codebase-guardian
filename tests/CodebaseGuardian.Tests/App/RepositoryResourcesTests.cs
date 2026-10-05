using System.Text.Json;
using CodebaseGuardian.Tests.Infrastructure;
using ModelContextProtocol.Protocol;

namespace CodebaseGuardian.Tests.App;

public class RepositoryResourcesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Resources_list_contains_repository_resources_as_json()
    {
        using var repo = TempGitRepo.Create();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var resources = await server.Client.ListResourcesAsync(cancellationToken: Ct);

        foreach (var uri in new[] { "guardian://repo/status", "guardian://repo/commits/recent" })
        {
            var resource = Assert.Single(resources, r => r.Uri == uri);
            Assert.Equal("application/json", resource.MimeType);
        }
    }

    [Fact]
    public async Task Status_resource_returns_status_json()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("a.txt", "a");
        var sha = repo.Commit("first");
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var read = await server.Client.ReadResourceAsync("guardian://repo/status", cancellationToken: Ct);

        var text = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));
        Assert.Equal("application/json", text.MimeType);
        using var json = JsonDocument.Parse(text.Text);
        Assert.Equal(sha, json.RootElement.GetProperty("headSha").GetString());
        Assert.Equal("main", json.RootElement.GetProperty("branch").GetString());
    }

    [Fact]
    public async Task Recent_commits_resource_returns_last_20_commits_newest_first()
    {
        using var repo = TempGitRepo.Create();
        for (var i = 0; i < 22; i++)
        {
            repo.Git("commit", "--allow-empty", "-m", $"c{i}");
        }

        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var read = await server.Client.ReadResourceAsync("guardian://repo/commits/recent", cancellationToken: Ct);

        var text = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));
        using var json = JsonDocument.Parse(text.Text);
        var commits = json.RootElement.GetProperty("commits");
        Assert.Equal(20, commits.GetArrayLength());
        Assert.Equal("c21", commits[0].GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Recent_commits_resource_emits_committed_at_as_utc_with_z()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("a.txt", "a");
        repo.CommitAt("zoned", "2026-01-02T03:04:05+02:00");
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var read = await server.Client.ReadResourceAsync("guardian://repo/commits/recent", cancellationToken: Ct);

        var text = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));
        using var json = JsonDocument.Parse(text.Text);
        Assert.Equal("2026-01-02T01:04:05.000Z",
            json.RootElement.GetProperty("commits")[0].GetProperty("committedAt").GetString());
    }

    [Fact]
    public async Task Status_resource_on_unborn_repository_has_no_head_sha()
    {
        using var repo = TempGitRepo.Create();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var read = await server.Client.ReadResourceAsync("guardian://repo/status", cancellationToken: Ct);

        var text = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));
        using var json = JsonDocument.Parse(text.Text);
        Assert.False(json.RootElement.TryGetProperty("headSha", out var head) && head.ValueKind != JsonValueKind.Null);
    }
}
