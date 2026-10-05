using System.Text.Json.Nodes;
using CodebaseGuardian.Security;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CodebaseGuardian.Tests.App;

public class SecretScannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<InProcessMcpServer> StartAsync(TempGitRepo repo, Dictionary<string, string?>? configuration = null) =>
        GuardianTestHost.StartAsync(repo.Path, configuration, cancellationToken: Ct);

    private static ISecretScanner Scanner(InProcessMcpServer server) => server.Services.GetRequiredService<ISecretScanner>();

    private static TempGitRepo RepoWithCommit()
    {
        var repo = TempGitRepo.Create();
        repo.WriteFile("README.md", "x");
        repo.Commit("initial");
        return repo;
    }

    [Fact]
    public async Task Working_tree_scan_reports_secrets_with_their_line_and_never_the_full_value()
    {
        using var repo = RepoWithCommit();
        var secret = FakeSecrets.AwsAccessKeyId();
        repo.WriteFile("src/config.txt", $"line one\nkey = {secret}\n");
        await using var server = await StartAsync(repo);

        var finding = Assert.Single(await Scanner(server).ScanWorkingTreeAsync(Ct));

        Assert.Equal(new SecretFinding("aws-access-key-id", "src/config.txt", 2, Redactor.Redact(secret)), finding);
        Assert.DoesNotContain(secret, finding.Redacted);
    }

    [Fact]
    public async Task One_finding_per_rule_and_line()
    {
        using var repo = RepoWithCommit();
        repo.WriteFile("a.txt", FakeSecrets.AwsAccessKeyId() + " " + FakeSecrets.AwsAccessKeyId() + "\n");
        await using var server = await StartAsync(repo);

        Assert.Single(await Scanner(server).ScanWorkingTreeAsync(Ct));
    }

    [Fact]
    public async Task Guardianignore_excludes_matching_paths()
    {
        using var repo = RepoWithCommit();
        repo.WriteFile(".guardianignore", "# test data\nfixtures/**\n**/*.sample\n");
        repo.WriteFile("fixtures/deep/k.txt", FakeSecrets.AwsAccessKeyId());
        repo.WriteFile("a/b/k.sample", FakeSecrets.AwsAccessKeyId());
        repo.WriteFile("real.txt", FakeSecrets.AwsAccessKeyId());
        await using var server = await StartAsync(repo);

        var finding = Assert.Single(await Scanner(server).ScanWorkingTreeAsync(Ct));

        Assert.Equal("real.txt", finding.Path);
    }

    [Theory]
    [InlineData("fixtures/**", "fixtures/a/b.txt", true)]
    [InlineData("fixtures/**", "src/fixtures/b.txt", false)]
    [InlineData("*.md", "docs/a.md", false)]
    [InlineData("*.md", "a.md", true)]
    [InlineData("**/*.md", "a.md", true)]
    [InlineData("**/*.md", "docs/x/a.md", true)]
    [InlineData("a?.txt", "ab.txt", true)]
    [InlineData("a?.txt", "a/.txt", false)]
    public void Globs_match_relative_paths(string glob, string path, bool ignored) =>
        Assert.Equal(ignored, GuardianIgnore.Parse(glob).IsIgnored(path));

    [Fact]
    public async Task Binary_and_oversized_files_are_skipped_and_deleted_tracked_files_tolerated()
    {
        using var repo = RepoWithCommit();
        var secret = FakeSecrets.AwsAccessKeyId();
        repo.WriteFile("gone.txt", secret);
        repo.Commit("add gone");
        File.Delete(Path.Combine(repo.Path, "gone.txt"));
        File.WriteAllBytes(Path.Combine(repo.Path, "bin.dat"), [0, 1, .. System.Text.Encoding.ASCII.GetBytes(secret)]);
        repo.WriteFile("big.txt", secret + "\n" + new string('x', 1024 * 1024));
        repo.WriteFile("ok.txt", secret);
        await using var server = await StartAsync(repo);

        var finding = Assert.Single(await Scanner(server).ScanWorkingTreeAsync(Ct));

        Assert.Equal("ok.txt", finding.Path);
    }

    [Fact]
    public async Task Staged_scan_sees_only_staged_added_lines()
    {
        using var repo = RepoWithCommit();
        repo.WriteFile("staged.txt", "a\n" + FakeSecrets.GitHubToken() + "\n");
        repo.Git("add", "staged.txt");
        repo.WriteFile("unstaged.txt", FakeSecrets.AwsAccessKeyId());
        await using var server = await StartAsync(repo);

        var finding = Assert.Single(await Scanner(server).ScanStagedAsync(Ct));

        Assert.Equal(("github-token", "staged.txt", 2), (finding.RuleId, finding.Path, finding.Line));
    }

    [Fact]
    public async Task Commit_scan_ignores_removed_secrets()
    {
        using var repo = RepoWithCommit();
        repo.WriteFile("k.txt", FakeSecrets.AwsAccessKeyId());
        var added = repo.Commit("add");
        repo.WriteFile("k.txt", "clean");
        var removed = repo.Commit("remove");
        await using var server = await StartAsync(repo);

        Assert.Single(await Scanner(server).ScanCommitAsync(added, Ct));
        Assert.Empty(await Scanner(server).ScanCommitAsync(removed, Ct));
    }

    [Fact]
    public async Task Commit_scan_rejects_an_invalid_revision()
    {
        using var repo = RepoWithCommit();
        await using var server = await StartAsync(repo);

        await Assert.ThrowsAsync<ArgumentException>(() => Scanner(server).ScanCommitAsync("--output=x", Ct));
    }

    [Fact]
    public async Task Commit_handler_publishes_secret_detected_for_a_new_commit()
    {
        using var repo = RepoWithCommit();
        await using var server = await StartAsync(repo, new() { ["Guardian:WatchEnabled"] = "true", ["Guardian:WatchIntervalMs"] = "100" });
        var cursor = await EventsPolling.GetCursorAsync(server, Ct);

        repo.WriteFile("app.cfg", "a\nb\nkey = " + FakeSecrets.AwsAccessKeyId() + "\n");
        var sha = repo.Commit("leak");

        var e = await EventsPolling.WaitForAsync(server, "security.secret_detected", cursor, d => (string?)d["commitSha"] == sha, EventsPolling.DefaultTimeout, Ct);
        var data = e["data"]!.AsObject();
        Assert.Equal("commit", (string?)data["source"]);
        Assert.Equal("skill://security-audit/SKILL.md", (string?)data["suggestedSkill"]);
        var finding = Assert.Single(data["findings"]!.AsArray())!.AsObject();
        Assert.Equal("aws-access-key-id", (string?)finding["ruleId"]);
        Assert.Equal("app.cfg", (string?)finding["path"]);
        Assert.Equal(3, (int?)finding["line"]);
        Assert.DoesNotContain(FakeSecrets.AwsAccessKeyId(), data.ToJsonString());
    }

    [Fact]
    public async Task Commit_handler_stays_silent_for_clean_commits_and_survives_failures()
    {
        using var repo = RepoWithCommit();
        await using var server = await StartAsync(repo);
        var handler = server.Services.GetServices<CodebaseGuardian.Watching.IRepositoryChangeHandler>().OfType<SecretScanCommitHandler>().Single();
        var cursor = await EventsPolling.GetCursorAsync(server, Ct);
        var bogus = new CodebaseGuardian.Git.CommitInfo("0000000000000000000000000000000000000000", "0000000", "a", "a@b", DateTimeOffset.UtcNow, "s", []);

        await handler.OnNewCommitsAsync([bogus], Ct);

        Assert.Empty(await EventsPolling.CollectAsync(server, "security.secret_detected", cursor, TimeSpan.FromMilliseconds(200), Ct));
    }
}
