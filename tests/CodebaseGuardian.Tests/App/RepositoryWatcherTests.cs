using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CodebaseGuardian.Git;
using CodebaseGuardian.Tests.Infrastructure;
using CodebaseGuardian.Watching;
using Microsoft.Extensions.DependencyInjection;

namespace CodebaseGuardian.Tests.App;

public class RepositoryWatcherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Timeout = EventsPolling.DefaultTimeout;
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(1200);

    private static readonly Dictionary<string, string?> Fast = new()
    {
        ["Guardian:WatchEnabled"] = "true",
        ["Guardian:WatchIntervalMs"] = "100",
        ["Guardian:FileChangeDebounceMs"] = "100",
    };

    private static Task<InProcessMcpServer> StartAsync(TempGitRepo repo, Action<IServiceCollection>? configure = null) =>
        GuardianTestHost.StartAsync(repo.Path, Fast, configure, cancellationToken: Ct);

    private static Task<JsonObject> WaitAsync(InProcessMcpServer host, string name, string cursor, Func<JsonObject, bool>? predicate = null, JsonObject? arguments = null) =>
        EventsPolling.WaitForAsync(host, name, cursor, predicate, Timeout, Ct, arguments);

    private static Func<JsonObject, bool> Sha(string sha) => d => (string?)d["sha"] == sha;

    private static TempGitRepo RepoWithCommit()
    {
        var repo = TempGitRepo.Create();
        repo.WriteFile("README.md", "hello");
        repo.Commit("initial");
        return repo;
    }

    [Fact]
    public async Task A_new_commit_is_announced_with_its_details_and_pre_existing_history_is_not()
    {
        using var repo = RepoWithCommit();
        await using var host = await StartAsync(repo);
        var cursor = await EventsPolling.GetCursorAsync(host, Ct);

        repo.WriteFile("src/a.txt", "one\ntwo\n");
        var sha = repo.Commit("add a");
        var e = await WaitAsync(host, "repo.commit.created", cursor, Sha(sha));

        var data = e["data"]!;
        Assert.Equal(sha, (string?)data["sha"]);
        Assert.Equal(sha[..7], ((string)data["shortSha"]!)[..7]);
        Assert.Equal("main", (string?)data["branch"]);
        Assert.Equal("add a", (string?)data["subject"]);
        Assert.Equal(TempGitRepo.AuthorName, (string?)data["author"]!["name"]);
        Assert.Equal(TempGitRepo.AuthorEmail, (string?)data["author"]!["email"]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", (string)data["committedAt"]!);
        Assert.Equal(1, (int)data["filesChanged"]!);
        Assert.Equal(2, (int)data["insertions"]!);
        Assert.Equal(0, (int)data["deletions"]!);
        Assert.Equal(["src/a.txt"], data["files"]!.AsArray().Select(f => (string)f!).ToList());
        Assert.Equal("skill://pr-review/SKILL.md", (string?)data["suggestedSkill"]);

        var all = await EventsPolling.CollectAsync(host, "repo.commit.created", cursor, Settle, Ct);
        Assert.Equal([sha], all.Select(x => (string)x["data"]!["sha"]!).ToList());
    }

    [Fact]
    public async Task A_commit_touching_a_manifest_also_announces_a_dependency_change()
    {
        using var repo = RepoWithCommit();
        await using var host = await StartAsync(repo);
        var cursor = await EventsPolling.GetCursorAsync(host, Ct);

        repo.WriteFile("web/package.json", "{}");
        repo.WriteFile("web/index.js", "x");
        var sha = repo.Commit("add package.json");
        var e = await WaitAsync(host, "repo.dependencies.changed", cursor, d => (string?)d["commitSha"] == sha);

        Assert.Equal(["npm"], e["data"]!["ecosystems"]!.AsArray().Select(x => (string)x!).ToList());
        Assert.Equal(["web/package.json"], e["data"]!["manifests"]!.AsArray().Select(x => (string)x!).ToList());
        Assert.Equal("skill://dependency-hygiene/SKILL.md", (string?)e["data"]!["suggestedSkill"]);
    }

    [Fact]
    public async Task A_new_branch_is_announced_once_and_merging_it_does_not_repeat_its_commits()
    {
        using var repo = RepoWithCommit();
        await using var host = await StartAsync(repo);
        var cursor = await EventsPolling.GetCursorAsync(host, Ct);

        var mainSha = repo.Git("rev-parse", "HEAD");
        repo.Git("checkout", "-b", "feature");
        repo.WriteFile("f.txt", "f");
        var sha = repo.Commit("feature work");

        // A poll may land between the checkout and the commit, so the head is either the old or the new one.
        var switched = await WaitAsync(host, "repo.branch.changed", cursor, d => (string?)d["to"] == "feature");
        Assert.Equal("main", (string?)switched["data"]!["from"]);
        Assert.Contains((string?)switched["data"]!["headSha"], new[] { mainSha, sha });
        var commit = await WaitAsync(host, "repo.commit.created", cursor, Sha(sha));
        Assert.Equal("feature", (string?)commit["data"]!["branch"]);

        repo.Git("checkout", "main");
        repo.Git("merge", "--ff-only", "feature");
        await WaitAsync(host, "repo.branch.changed", cursor, d => (string?)d["to"] == "main");

        var all = await EventsPolling.CollectAsync(host, "repo.commit.created", cursor, Settle, Ct);
        Assert.Equal([sha], all.Select(x => (string)x["data"]!["sha"]!).ToList());
    }

    [Fact]
    public async Task An_edit_is_reported_as_changed_files_and_ignored_directories_are_not()
    {
        using var repo = RepoWithCommit();
        await using var host = await StartAsync(repo);
        var cursor = await EventsPolling.GetCursorAsync(host, Ct);

        repo.WriteFile("bin/out.dll", "x");
        repo.WriteFile("node_modules/pkg/index.js", "x");
        repo.WriteFile("src/obj/cache.txt", "x");
        repo.WriteFile("src/edited.txt", "x");
        repo.WriteFile("package.json", "{}");

        var e = await WaitAsync(host, "repo.files.changed", cursor, d => d["paths"]!.AsArray().Any(p => (string?)p == "src/edited.txt"));
        var paths = e["data"]!["paths"]!.AsArray().Select(p => (string)p!).ToList();
        Assert.DoesNotContain(paths, p => p.StartsWith("bin/") || p.Contains("node_modules") || p.Contains("/obj/"));
        Assert.Equal(paths.Count, (int)e["data"]!["count"]!);
        Assert.Equal(paths.Order(StringComparer.Ordinal).ToList(), paths);

        var deps = await WaitAsync(host, "repo.dependencies.changed", cursor, d => d["commitSha"] is null);
        Assert.Equal(["package.json"], deps["data"]!["manifests"]!.AsArray().Select(x => (string)x!).ToList());
    }

    [Fact]
    public async Task Handlers_receive_new_commits_and_a_throwing_handler_does_not_stop_later_events()
    {
        using var repo = RepoWithCommit();
        var recorder = new RecordingHandler();
        await using var host = await StartAsync(repo, services =>
        {
            services.AddSingleton<IRepositoryChangeHandler>(new ThrowingHandler());
            services.AddSingleton<IRepositoryChangeHandler>(recorder);
        });
        var cursor = await EventsPolling.GetCursorAsync(host, Ct);

        repo.WriteFile("a.txt", "a");
        var first = repo.Commit("first");
        repo.WriteFile("b.txt", "b");
        var second = repo.Commit("second");
        await WaitAsync(host, "repo.commit.created", cursor, Sha(second));

        var deadline = DateTime.UtcNow + Timeout;
        while (recorder.Shas.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, Ct);
        }

        Assert.Equal([first, second], recorder.Shas.ToList()); // oldest first, each exactly once

        repo.WriteFile("c.txt", "c");
        var third = repo.Commit("third");
        await WaitAsync(host, "repo.commit.created", cursor, Sha(third));
    }

    [Fact]
    public async Task A_repository_without_commits_announces_its_first_commit_on_main()
    {
        using var repo = TempGitRepo.Create();
        await using var host = await StartAsync(repo);
        var cursor = await EventsPolling.GetCursorAsync(host, Ct);

        repo.WriteFile("a.txt", "a");
        var sha = repo.Commit("root");
        var e = await WaitAsync(host, "repo.commit.created", cursor, Sha(sha));
        Assert.Equal("main", (string?)e["data"]!["branch"]);
        Assert.Equal("root", (string?)e["data"]!["subject"]);
    }

    [Fact]
    public async Task Amending_announces_exactly_one_new_commit()
    {
        using var repo = RepoWithCommit();
        await using var host = await StartAsync(repo);
        var cursor = await EventsPolling.GetCursorAsync(host, Ct);

        repo.Git("commit", "--amend", "-m", "reworded");
        var amended = repo.Git("rev-parse", "HEAD");
        var e = await WaitAsync(host, "repo.commit.created", cursor, Sha(amended));
        Assert.Equal("reworded", (string?)e["data"]!["subject"]);

        var all = await EventsPolling.CollectAsync(host, "repo.commit.created", cursor, Settle, Ct);
        Assert.Equal([amended], all.Select(x => (string)x["data"]!["sha"]!).ToList());
    }

    [Fact]
    public async Task A_rebase_announces_each_rewritten_commit_once()
    {
        using var repo = RepoWithCommit();
        repo.Git("checkout", "-b", "feature");
        repo.WriteFile("f.txt", "f");
        repo.Commit("feature work");
        repo.Git("checkout", "main");
        repo.WriteFile("m.txt", "m");
        repo.Commit("main work");
        repo.Git("checkout", "feature");
        await using var host = await StartAsync(repo);
        var cursor = await EventsPolling.GetCursorAsync(host, Ct);

        repo.Git("rebase", "main");
        var rebased = repo.Git("rev-parse", "HEAD");
        await WaitAsync(host, "repo.commit.created", cursor, Sha(rebased));

        var all = await EventsPolling.CollectAsync(host, "repo.commit.created", cursor, Settle, Ct);
        Assert.Equal([rebased], all.Select(x => (string)x["data"]!["sha"]!).ToList());
    }

    [Fact]
    public async Task Deleting_a_branch_neither_repeats_announcements_nor_stops_the_watcher()
    {
        using var repo = RepoWithCommit();
        await using var host = await StartAsync(repo);
        var cursor = await EventsPolling.GetCursorAsync(host, Ct);

        repo.Git("checkout", "-b", "feature");
        repo.WriteFile("f.txt", "f");
        var featureSha = repo.Commit("feature work");
        await WaitAsync(host, "repo.commit.created", cursor, Sha(featureSha));

        repo.Git("checkout", "main");
        await WaitAsync(host, "repo.branch.changed", cursor, d => (string?)d["to"] == "main");
        repo.Git("branch", "-D", "feature");
        repo.Git("gc", "--prune=now");
        repo.WriteFile("m.txt", "m");
        var mainSha = repo.Commit("main work");
        await WaitAsync(host, "repo.commit.created", cursor, Sha(mainSha));

        var all = await EventsPolling.CollectAsync(host, "repo.commit.created", cursor, Settle, Ct);
        Assert.Equal([featureSha, mainSha], all.Select(x => (string)x["data"]!["sha"]!).ToList());

        var afterEdit = await EventsPolling.GetCursorAsync(host, Ct);
        repo.WriteFile("later.txt", "x");
        await WaitAsync(host, "repo.files.changed", afterEdit, d => d["paths"]!.AsArray().Any(p => (string?)p == "later.txt"));
    }

    [Fact]
    public async Task The_branch_argument_filters_commit_events()
    {
        using var repo = RepoWithCommit();
        await using var host = await StartAsync(repo);
        var cursor = await EventsPolling.GetCursorAsync(host, Ct);

        repo.Git("checkout", "-b", "feature");
        repo.WriteFile("f.txt", "f");
        var featureSha = repo.Commit("feature work");
        await WaitAsync(host, "repo.commit.created", cursor, Sha(featureSha));
        repo.Git("checkout", "main");
        repo.WriteFile("m.txt", "m");
        var mainSha = repo.Commit("main work");
        await WaitAsync(host, "repo.commit.created", cursor, Sha(mainSha));

        var (events, _) = await EventsPolling.PollAsync(host, "repo.commit.created", cursor, Ct, new JsonObject { ["branch"] = "feature" });
        Assert.Equal([featureSha], events.Select(x => (string)x["data"]!["sha"]!).ToList());
    }

    [Fact]
    public async Task A_disabled_watcher_publishes_nothing()
    {
        using var repo = RepoWithCommit();
        await using var host = await GuardianTestHost.StartAsync(repo.Path, new Dictionary<string, string?> { ["Guardian:WatchIntervalMs"] = "100" }, cancellationToken: Ct);
        var cursor = await EventsPolling.GetCursorAsync(host, Ct);

        repo.WriteFile("a.txt", "a");
        repo.Commit("ignored");
        Assert.Empty(await EventsPolling.CollectAsync(host, "repo.commit.created", cursor, Settle, Ct));
    }

    [Fact]
    public async Task A_rejected_exclusion_tip_is_retried_with_only_the_current_heads_of_other_branches()
    {
        var root = Directory.CreateTempSubdirectory("guardian-fake-").FullName;
        try
        {
            var git = new FakeGit(root)
            {
                Heads = new Dictionary<string, string> { ["main"] = "a1", ["other"] = "o1", ["gone"] = "x1" },
            };
            var publisher = new RecordingPublisher();
            var options = Microsoft.Extensions.Options.Options.Create(new CodebaseGuardian.Hosting.GuardianOptions
            {
                RepositoryPath = root, WatchEnabled = true, WatchIntervalMs = 100, FileChangeDebounceMs = 100,
            });
            using var watcher = new RepositoryWatcher(git, publisher, [], options, Microsoft.Extensions.Logging.Abstractions.NullLogger<RepositoryWatcher>.Instance);
            await watcher.StartAsync(Ct);
            try
            {
                git.Heads = new Dictionary<string, string> { ["main"] = "a2", ["other"] = "o1" }; // 'gone' deleted, its tip pruned
                var deadline = DateTime.UtcNow + Timeout;
                while (publisher.CommitShas.Count == 0 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(25, Ct);
                }

                await Task.Delay(500, Ct); // further polls must not announce anything again
            }
            finally
            {
                await watcher.StopAsync(CancellationToken.None);
            }

            Assert.Equal(["a2"], publisher.CommitShas.ToList());
            Assert.Equal(2, git.Calls.Count);
            Assert.Equal(["a1", "o1", "x1"], git.Calls[0].Order().ToList()); // first attempt excludes every previous tip
            Assert.Equal(["o1"], git.Calls[1]);                             // retry: only the other branches' current heads
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingPublisher : Mcp.Events.IEventPublisher
    {
        private readonly ConcurrentQueue<string> _commitShas = new();
        public IReadOnlyCollection<string> CommitShas => _commitShas;

        public ValueTask<Mcp.Events.EventEnvelope> PublishAsync(string name, JsonObject data, string? eventId = null,
            DateTimeOffset? timestamp = null, CancellationToken cancellationToken = default)
        {
            if (name == "repo.commit.created")
            {
                _commitShas.Enqueue((string)data["sha"]!);
            }

            return ValueTask.FromResult(new Mcp.Events.EventEnvelope(eventId ?? "id", name, DateTimeOffset.UtcNow, data, 1));
        }
    }

    private sealed class FakeGit(string root) : IGitRepository
    {
        public volatile IReadOnlyDictionary<string, string> Heads = new Dictionary<string, string>();
        public List<string[]> Calls { get; } = [];

        public string RootPath => root;
        public Task<string?> GetCurrentBranchAsync(CancellationToken ct = default) => Task.FromResult<string?>("main");
        public Task<IReadOnlyDictionary<string, string>> GetBranchHeadsAsync(CancellationToken ct = default) => Task.FromResult(Heads);

        public Task<IReadOnlyList<CommitInfo>> GetNewCommitsAsync(string tip, IReadOnlyCollection<string> excludeTips, int limit, CancellationToken ct = default)
        {
            lock (Calls)
            {
                Calls.Add([.. excludeTips]);
            }

            if (excludeTips.Contains("x1"))
            {
                throw new GitException("bad object", 128, "fatal: bad object x1");
            }

            return Task.FromResult<IReadOnlyList<CommitInfo>>([new CommitInfo(tip, tip, "n", "e", DateTimeOffset.UtcNow, "s", [])]);
        }

        public Task<DiffSummary> GetCommitDiffAsync(string sha, int maxPatchBytes, CancellationToken ct = default) =>
            Task.FromResult(new DiffSummary("", sha, [], 0, 0, "", false));

        public Task<RepoStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetHeadShaAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CommitInfo>> GetRecentCommitsAsync(int limit, string? revision = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DiffSummary> GetDiffSummaryAsync(string? from, string? to, int maxPatchBytes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DiffSummary> GetStagedDiffAsync(int maxPatchBytes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetRemoteUrlAsync(string remote = "origin", CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingHandler : IRepositoryChangeHandler
    {
        private readonly ConcurrentQueue<string> _shas = new();
        public IReadOnlyCollection<string> Shas => _shas;

        public Task OnNewCommitsAsync(IReadOnlyList<CommitInfo> commits, CancellationToken cancellationToken)
        {
            foreach (var c in commits)
            {
                _shas.Enqueue(c.Sha);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : IRepositoryChangeHandler
    {
        public Task OnNewCommitsAsync(IReadOnlyList<CommitInfo> commits, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("handler failure (expected by the test)");
    }
}
