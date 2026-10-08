using System.Text;
using CodebaseGuardian.Git;
using CodebaseGuardian.GitHub;
using CodebaseGuardian.Hosting;
using CodebaseGuardian.Security;
using CodebaseGuardian.Sources;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Tests.App;

public class GitHubApiSourceTests
{
    private const string RevisionMessage = "Remote mode accepts branch names, tag names and commit SHAs only.";
    private const string BothEndsMessage = "Remote mode has no working tree: pass both 'from' and 'to'.";

    private static readonly string A = Sha('a');
    private static readonly string B = Sha('b');
    private static readonly string C = Sha('c');
    private static readonly string D = Sha('d');
    private static readonly string X = Sha('e');
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sha(char c) => new(c, 40);

    private sealed record Setup(GitHubApiSource Source, FakeGitHubApi Api, FakeGitHubRepository Repository, CapturingLoggerProvider Logs);

    private static Setup Create(RemoteOptions? remote = null, Func<IGitHubRepositoryApi, IGitHubRepositoryApi>? wrap = null)
    {
        var api = new FakeGitHubApi();
        var repository = new FakeGitHubRepository(api);
        var services = new ServiceCollection();
        services.AddGitHubIntegration(new ConfigurationBuilder().Build());
        api.Install(services);
        var repositoryApi = services.BuildServiceProvider().GetRequiredService<IGitHubRepositoryApi>();

        var logs = new CapturingLoggerProvider();
        var logger = LoggerFactory.Create(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Debug)).CreateLogger<GitHubApiSource>();
        var source = new GitHubApiSource(
            wrap?.Invoke(repositoryApi) ?? repositoryApi,
            new RepositoryLocation.GitHub(new GitHubRepositoryRef("acme", "widgets")),
            Options.Create(remote ?? new RemoteOptions()),
            Options.Create(new GitHubOptions()),
            logger);
        return new Setup(source, api, repository, logs);
    }

    private static GitHubFileChange Added(string path, params string[] lines) =>
        new(path, null, "added", lines.Length, 0, $"@@ -0,0 +1,{lines.Length} @@\n" + string.Join('\n', lines.Select(line => "+" + line)));

    /// <summary>A ← B ← C (main), B ← D (feature).</summary>
    private static Setup Seeded(RemoteOptions? remote = null, Func<IGitHubRepositoryApi, IGitHubRepositoryApi>? wrap = null)
    {
        var setup = Create(remote, wrap);
        setup.Repository
            .Commit(A, "first commit\n\nbody", [], Added("README.md", "hello"))
            .Commit(B, "second commit\r\n\r\ndetails", [A], Added("src/app.cs", "class App {}", "// end"))
            .Commit(C, "third commit", [B], new GitHubFileChange("src/app.cs", null, "modified", 1, 1, "@@ -2 +2 @@\n-// end\n+// done"))
            .Commit(D, "feature work", [B], Added("feature.txt", "token = 1", "second line"), Added("docs/f.md", "# F"))
            .SetBranch("main", C)
            .SetBranch("feature", D)
            .File(".guardianignore", "*.log\n")
            .File("README.md", "hello\n")
            .File("src/app.cs", "class App {}\n// done\n");
        return setup;
    }

    private static async Task<List<SnapshotFile>> ReadAllAsync(IAsyncEnumerable<SnapshotFile> snapshot)
    {
        var files = new List<SnapshotFile>();
        await foreach (var file in snapshot)
        {
            files.Add(file);
        }

        return files;
    }

    // ---- identity ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_display_name_is_the_repository_on_github_com()
    {
        var source = Create().Source;

        Assert.Equal(RepositorySourceKind.GitHub, source.Kind);
        Assert.Equal("github.com/acme/widgets", source.DisplayName);
    }

    [Fact]
    public void On_github_enterprise_the_display_name_uses_the_api_host()
    {
        var source = new GitHubApiSource(
            new NoApi(), new RepositoryLocation.GitHub(new GitHubRepositoryRef("acme", "widgets")), Options.Create(new RemoteOptions()),
            Options.Create(new GitHubOptions { ApiBaseUrl = "https://ghe.example.com/api/v3" }), NullLogger<GitHubApiSource>.Instance);

        Assert.Equal("ghe.example.com/acme/widgets", source.DisplayName);
    }

    [Fact]
    public void A_local_location_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new GitHubApiSource(
            new NoApi(), new RepositoryLocation.Local("/tmp/repo"), Options.Create(new RemoteOptions()),
            Options.Create(new GitHubOptions()), NullLogger<GitHubApiSource>.Instance));
    }

    // ---- Review Focus 1: empty repository ----------------------------------------------------------------------------

    [Fact]
    public async Task An_empty_repository_reports_its_default_branch_without_a_head_and_reads_nothing()
    {
        var (source, api, _, _) = Create();

        var status = await source.GetStatusAsync(Ct);

        Assert.Equal("main", status.Branch);
        Assert.Null(status.HeadSha);
        Assert.Null(status.Upstream);
        Assert.Equal(0, status.Ahead);
        Assert.Equal(0, status.Behind);
        Assert.Empty(status.Staged);
        Assert.Empty(status.Unstaged);
        Assert.Empty(status.Untracked);
        Assert.Empty(status.Conflicted);
        Assert.Null(await source.GetHeadShaAsync(Ct));
        Assert.Equal("main", await source.GetCurrentBranchAsync(Ct));
        Assert.Empty(await source.GetRecentCommitsAsync(10, ct: Ct));
        Assert.Empty(await source.GetBranchHeadsAsync(Ct));
        Assert.Null(await source.ReadFileAsync(".guardianignore", Ct));
        Assert.Empty(await ReadAllAsync(source.ReadSnapshotAsync(Ct)));

        // Nothing that answers 409 for an empty repository was asked.
        Assert.DoesNotContain(api.Requests, request =>
            request.PathAndQuery.Contains("/compare/", StringComparison.Ordinal)
            || request.PathAndQuery.Contains("/commits/", StringComparison.Ordinal)
            || request.PathAndQuery.Contains("/tarball/", StringComparison.Ordinal)
            || request.PathAndQuery.Contains("/contents/", StringComparison.Ordinal));
    }

    // ---- status, head, branches ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_head_and_branches_come_from_the_default_branch()
    {
        var source = Seeded().Source;

        var status = await source.GetStatusAsync(Ct);

        Assert.Equal("main", status.Branch);
        Assert.Equal(C, status.HeadSha);
        Assert.Null(status.Upstream);
        Assert.Empty(status.Staged);
        Assert.Equal(C, await source.GetHeadShaAsync(Ct));
        Assert.Equal("main", await source.GetCurrentBranchAsync(Ct));
        var heads = await source.GetBranchHeadsAsync(Ct);
        Assert.Equal(new Dictionary<string, string> { ["feature"] = D, ["main"] = C }, heads.OrderBy(h => h.Key).ToDictionary());
    }

    // ---- commits ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Recent_commits_of_head_are_the_newest_of_the_default_branch_with_every_field()
    {
        var source = Seeded().Source;

        var commits = await source.GetRecentCommitsAsync(2, "HEAD", Ct);

        Assert.Equal(
            [
                new CommitInfo(C, C[..7], "Test Author", "author@example.com", Epoch.AddMinutes(2), "third commit", [B]),
                new CommitInfo(B, B[..7], "Test Author", "author@example.com", Epoch.AddMinutes(1), "second commit", [A]),
            ],
            commits,
            CommitComparer.Instance);
    }

    [Fact]
    public async Task Recent_commits_take_the_subject_from_the_first_line_and_honour_a_branch()
    {
        var source = Seeded().Source;

        var commits = await source.GetRecentCommitsAsync(10, "feature", Ct);

        Assert.Equal([D, B, A], commits.Select(c => c.Sha));
        Assert.Equal(["feature work", "second commit", "first commit"], commits.Select(c => c.Subject));
        Assert.Empty(commits[^1].ParentShas);
    }

    [Fact]
    public async Task Ancestry_syntax_is_rejected_before_any_request()
    {
        var (source, api, _, _) = Seeded();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => source.GetRecentCommitsAsync(10, "main~1", Ct));

        Assert.StartsWith(RevisionMessage, exception.Message, StringComparison.Ordinal);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task New_commits_on_a_linear_branch_exclude_the_old_tip()
    {
        var source = Seeded().Source;

        var commits = await source.GetNewCommitsAsync(C, [A], 10, Ct);

        Assert.Equal([B, C], commits.Select(c => c.Sha));
    }

    [Fact]
    public async Task New_commits_on_a_feature_branch_exclude_the_default_branch()
    {
        var source = Seeded().Source;

        Assert.Equal([D], (await source.GetNewCommitsAsync(D, [C], 10, Ct)).Select(c => c.Sha));
        Assert.Equal([D], (await source.GetNewCommitsAsync(D, [A, C], 10, Ct)).Select(c => c.Sha));
        Assert.Empty(await source.GetNewCommitsAsync(D, [D], 10, Ct));
    }

    [Fact]
    public async Task A_forgotten_exclude_tip_is_dropped_with_a_warning()
    {
        var (source, _, repository, logs) = Seeded();
        repository.Commit(X, "force-pushed away", [B]).Forget(X);

        var commits = await source.GetNewCommitsAsync(D, [X, C], 10, Ct);

        Assert.Equal([D], commits.Select(c => c.Sha));
        Assert.Contains(logs.Messages, message => message.Contains(X, StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_every_exclude_tip_is_forgotten_the_tip_history_is_returned()
    {
        var (source, _, repository, _) = Seeded();
        repository.Commit(X, "force-pushed away", [B]).Forget(X);

        var commits = await source.GetNewCommitsAsync(C, [X], 10, Ct);

        Assert.Equal([A, B, C], commits.Select(c => c.Sha));
    }

    [Fact]
    public async Task Without_exclude_tips_the_history_is_oldest_first_and_limited_to_the_newest()
    {
        var source = Seeded().Source;

        Assert.Equal([A, B, C], (await source.GetNewCommitsAsync("main", [], 10, Ct)).Select(c => c.Sha));
        Assert.Equal([B, C], (await source.GetNewCommitsAsync("main", [], 2, Ct)).Select(c => c.Sha));
    }

    [Fact]
    public async Task New_commits_keep_only_the_newest_limit()
    {
        var source = Seeded().Source;

        Assert.Equal([C], (await source.GetNewCommitsAsync(C, [A], 1, Ct)).Select(c => c.Sha));
    }

    [Fact]
    public async Task More_than_five_exclude_tips_are_logged_as_approximate()
    {
        var (source, _, _, logs) = Seeded();

        var commits = await source.GetNewCommitsAsync(D, [C, A, B, Sha('1'), Sha('2'), Sha('3')], 10, Ct);

        Assert.Equal([D], commits.Select(c => c.Sha));
        Assert.Contains(logs.Messages, message => message.Contains("approximate", StringComparison.Ordinal));
    }

    // ---- diffs --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "main")]
    [InlineData("main", null)]
    [InlineData(null, null)]
    public async Task A_diff_needs_both_ends(string? from, string? to)
    {
        var source = Seeded().Source;

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => source.GetDiffSummaryAsync(from, to, 1000, Ct));

        Assert.StartsWith(BothEndsMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_diff_between_branches_uses_the_merge_base_and_parses_back_to_the_added_lines()
    {
        var source = Seeded().Source;

        var diff = await source.GetDiffSummaryAsync("main", "feature", 100_000, Ct);

        Assert.Equal(B, diff.From); // the merge base, not main's tip
        Assert.Equal(D, diff.To);
        Assert.Equal([new FileDiffStat("feature.txt", 2, 0, false), new FileDiffStat("docs/f.md", 1, 0, false)], diff.Files.OrderByDescending(f => f.Path.Length));
        Assert.Equal(3, diff.Insertions);
        Assert.Equal(0, diff.Deletions);
        Assert.False(diff.PatchTruncated);
        Assert.Equal(
            [new AddedLine("docs/f.md", 1, "# F"), new AddedLine("feature.txt", 1, "token = 1"), new AddedLine("feature.txt", 2, "second line")],
            PatchParser.AddedLines(diff.Patch).OrderBy(line => line.Path, StringComparer.Ordinal).ThenBy(line => line.Line));
    }

    [Fact]
    public async Task A_diff_with_no_commits_ends_at_the_to_argument()
    {
        var source = Seeded().Source;

        var diff = await source.GetDiffSummaryAsync("main", "main", 100_000, Ct);

        Assert.Equal(C, diff.From);
        Assert.Equal("main", diff.To);
        Assert.Empty(diff.Files);
        Assert.Equal(string.Empty, diff.Patch);
    }

    [Fact]
    public async Task A_diff_rejects_ancestry_syntax()
    {
        var source = Seeded().Source;

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => source.GetDiffSummaryAsync("main^", "feature", 1000, Ct));

        Assert.StartsWith(RevisionMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_root_commit_diff_starts_at_the_empty_tree()
    {
        var source = Seeded().Source;

        var diff = await source.GetCommitDiffAsync(A, 100_000, Ct);

        Assert.Equal("(empty)", diff.From);
        Assert.Equal(A, diff.To);
        Assert.Equal([new FileDiffStat("README.md", 1, 0, false)], diff.Files);
        Assert.Equal([new AddedLine("README.md", 1, "hello")], PatchParser.AddedLines(diff.Patch));
    }

    [Fact]
    public async Task A_commit_diff_starts_at_its_first_parent()
    {
        var source = Seeded().Source;

        var diff = await source.GetCommitDiffAsync(C, 100_000, Ct);

        Assert.Equal(B, diff.From);
        Assert.Equal(C, diff.To);
        Assert.Equal([new FileDiffStat("src/app.cs", 1, 1, false)], diff.Files);
        Assert.Equal([new AddedLine("src/app.cs", 2, "// done")], PatchParser.AddedLines(diff.Patch));
    }

    // ---- snapshot -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_file_is_read_from_the_contents_api_at_the_head_sha()
    {
        var (source, api, _, _) = Seeded();

        var content = await source.ReadFileAsync(".guardianignore", Ct);

        Assert.Equal("*.log\n", Encoding.UTF8.GetString(content!));
        Assert.Contains(api.Requests, request => request.PathAndQuery == $"/repos/acme/widgets/contents/.guardianignore?ref={C}");
        Assert.Null(await source.ReadFileAsync("missing.txt", Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData("../outside")]
    [InlineData("a/../../b")]
    public async Task A_file_path_outside_the_snapshot_is_rejected(string path)
    {
        var (source, api, _, _) = Seeded();

        await Assert.ThrowsAsync<ArgumentException>(() => source.ReadFileAsync(path, Ct));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task The_snapshot_yields_the_files_of_the_head_tarball()
    {
        var (source, api, _, _) = Seeded();

        var files = await ReadAllAsync(source.ReadSnapshotAsync(Ct));

        Assert.Equal([".guardianignore", "README.md", "src/app.cs"], files.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.All(files, file => Assert.Null(file.Skipped));
        Assert.Equal("hello\n", Encoding.UTF8.GetString(files.Single(f => f.Path == "README.md").Content!.Value.Span));
        Assert.Contains(api.Requests, request => request.PathAndQuery == $"/repos/acme/widgets/tarball/{C}");
    }

    [Fact]
    public async Task The_snapshot_applies_the_remote_caps()
    {
        var source = Seeded(new RemoteOptions { MaxSnapshotFiles = 1 }).Source;

        var files = await ReadAllAsync(source.ReadSnapshotAsync(Ct));

        Assert.Equal(2, files.Count);
        Assert.False(files[0].IsIncompleteMarker);
        Assert.True(files[1].IsIncompleteMarker);
    }

    [Fact]
    public async Task The_tarball_stream_is_disposed_when_the_reader_stops_early()
    {
        TrackingApi? tracking = null;
        var source = Seeded(wrap: inner => tracking = new TrackingApi(inner)).Source;

        await foreach (var _ in source.ReadSnapshotAsync(Ct))
        {
            break;
        }

        Assert.True(tracking!.Opened!.Disposed);
    }

    [Fact]
    public async Task The_tarball_stream_is_disposed_when_the_read_is_cancelled()
    {
        TrackingApi? tracking = null;
        var source = Seeded(wrap: inner => tracking = new TrackingApi(inner)).Source;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in source.ReadSnapshotAsync(cancellation.Token))
            {
                await cancellation.CancelAsync();
            }
        });

        Assert.True(tracking!.Opened!.Disposed);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private sealed class CommitComparer : IEqualityComparer<CommitInfo>
    {
        public static readonly CommitComparer Instance = new();

        public bool Equals(CommitInfo? x, CommitInfo? y) =>
            x is not null && y is not null
            && x with { ParentShas = [] } == y with { ParentShas = [] }
            && x.ParentShas.SequenceEqual(y.ParentShas);

        public int GetHashCode(CommitInfo obj) => obj.Sha.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>The real API, with the tarball stream wrapped so the test can see whether it was disposed.</summary>
    private sealed class TrackingApi(IGitHubRepositoryApi inner) : IGitHubRepositoryApi
    {
        public TrackingStream? Opened { get; private set; }

        public Task<GitHubRepositoryInfo> GetRepositoryAsync(CancellationToken ct) => inner.GetRepositoryAsync(ct);

        public Task<IReadOnlyDictionary<string, string>> GetBranchHeadsAsync(CancellationToken ct) => inner.GetBranchHeadsAsync(ct);

        public Task<IReadOnlyList<GitHubCommit>> ListCommitsAsync(string? sha, int limit, CancellationToken ct) => inner.ListCommitsAsync(sha, limit, ct);

        public Task<GitHubCommitDetail> GetCommitAsync(string sha, CancellationToken ct) => inner.GetCommitAsync(sha, ct);

        public Task<GitHubComparison> CompareAsync(string @base, string head, CancellationToken ct) => inner.CompareAsync(@base, head, ct);

        public Task<byte[]?> GetFileAsync(string path, string @ref, CancellationToken ct) => inner.GetFileAsync(path, @ref, ct);

        public async Task<Stream> OpenTarballAsync(string @ref, CancellationToken ct)
        {
            Opened = new TrackingStream(await inner.OpenTarballAsync(@ref, ct));
            return Opened;
        }
    }

    private sealed class TrackingStream(Stream inner) : Stream
    {
        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>For tests that never reach the API.</summary>
    private sealed class NoApi : IGitHubRepositoryApi
    {
        public Task<GitHubRepositoryInfo> GetRepositoryAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string>> GetBranchHeadsAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<GitHubCommit>> ListCommitsAsync(string? sha, int limit, CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubCommitDetail> GetCommitAsync(string sha, CancellationToken ct) => throw new NotSupportedException();

        public Task<GitHubComparison> CompareAsync(string @base, string head, CancellationToken ct) => throw new NotSupportedException();

        public Task<byte[]?> GetFileAsync(string path, string @ref, CancellationToken ct) => throw new NotSupportedException();

        public Task<Stream> OpenTarballAsync(string @ref, CancellationToken ct) => throw new NotSupportedException();
    }
}
