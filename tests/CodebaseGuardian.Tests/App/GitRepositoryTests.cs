using CodebaseGuardian.Git;
using CodebaseGuardian.Hosting;
using CodebaseGuardian.Processes;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Tests.App;

// Setup always goes through TempGitRepo (hermetic); GitRepository itself deliberately uses the ambient git configuration.
public sealed class GitRepositoryTests : IDisposable
{
    private readonly TempGitRepo _repo = TempGitRepo.Create();

    public void Dispose() => _repo.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GitRepository Open(string? path = null) =>
        new(new ProcessRunner(), Options.Create(new GuardianOptions { RepositoryPath = path ?? _repo.Path }));

    // ---- unborn repository

    [Fact]
    public async Task Empty_repository_reports_no_head_branch_commits_or_branches()
    {
        var git = Open();

        Assert.Null(await git.GetHeadShaAsync(Ct));
        Assert.Null(await git.GetCurrentBranchAsync(Ct)); // documented: an unborn branch reports null, not "main"
        Assert.Empty(await git.GetRecentCommitsAsync(10, ct: Ct));
        Assert.Empty(await git.GetBranchHeadsAsync(Ct));
    }

    [Fact]
    public async Task Empty_repository_status_has_no_head()
    {
        _repo.WriteFile("a.txt", "a\n");
        var status = await Open().GetStatusAsync(Ct);

        Assert.Null(status.HeadSha);
        Assert.Null(status.Branch);
        Assert.Equal(["a.txt"], status.Untracked);
    }

    [Fact]
    public async Task Empty_repository_diff_shows_staged_files_from_the_empty_tree()
    {
        _repo.WriteFile("a.txt", "one\ntwo\n");
        _repo.Git("add", "a.txt");

        var diff = await Open().GetDiffSummaryAsync(null, null, 10_000, Ct);

        Assert.Equal("(empty)", diff.From);
        var file = Assert.Single(diff.Files);
        Assert.Equal(new FileDiffStat("a.txt", 2, 0, false), file);
        Assert.Contains("+one", diff.Patch);
    }

    [Fact]
    public async Task Empty_repository_diff_with_explicit_from_fails()
    {
        await Assert.ThrowsAsync<GitException>(() => Open().GetDiffSummaryAsync("HEAD", null, 1000, Ct));
    }

    // ---- verbatim paths

    [Fact]
    public async Task Paths_with_spaces_and_non_ascii_characters_are_returned_verbatim()
    {
        const string path = "docs/naïve file.md";
        _repo.WriteFile(path, "hi\n");
        var git = Open();

        Assert.Equal([path], (await git.GetStatusAsync(Ct)).Untracked);
        Assert.Equal([path], await git.ListFilesAsync(Ct));

        var sha = _repo.Commit("add");
        var diff = await git.GetCommitDiffAsync(sha, 10_000, Ct);
        Assert.Equal(path, Assert.Single(diff.Files).Path);
    }

    // ---- status

    [Fact]
    public async Task Status_classifies_staged_unstaged_untracked_and_renamed_files()
    {
        _repo.WriteFile("keep.txt", "keep\n");
        _repo.WriteFile("edit.txt", "edit\n");
        _repo.WriteFile("old name.txt", "some content that is long enough to be detected as a rename\n");
        _repo.WriteFile("gone.txt", "gone\n");
        var head = _repo.Commit("initial");

        _repo.WriteFile("staged.txt", "new\n");
        _repo.Git("add", "staged.txt");
        _repo.Git("mv", "old name.txt", "new name.txt");
        _repo.WriteFile("edit.txt", "edited\n");
        _repo.DeleteFile("gone.txt");
        _repo.WriteFile("untracked.txt", "u\n");

        var status = await Open().GetStatusAsync(Ct);

        Assert.Equal("main", status.Branch);
        Assert.Equal(head, status.HeadSha);
        Assert.Null(status.Upstream);
        Assert.Equal((0, 0), (status.Ahead, status.Behind));
        Assert.Equivalent(
            new[] { new FileChange("new name.txt", "renamed"), new FileChange("staged.txt", "added") },
            status.Staged);
        Assert.Equivalent(
            new[] { new FileChange("edit.txt", "modified"), new FileChange("gone.txt", "deleted") },
            status.Unstaged);
        Assert.Equal(["untracked.txt"], status.Untracked);
        Assert.Empty(status.Conflicted);
    }

    [Fact]
    public async Task Status_reports_conflicted_files()
    {
        _repo.WriteFile("c.txt", "base\n");
        _repo.Commit("base");
        _repo.Git("checkout", "-b", "other");
        _repo.WriteFile("c.txt", "other\n");
        _repo.Commit("other");
        _repo.Git("checkout", "main");
        _repo.WriteFile("c.txt", "main\n");
        _repo.Commit("main");
        Assert.ThrowsAny<Exception>(() => _repo.Git("merge", "other"));

        var status = await Open().GetStatusAsync(Ct);

        Assert.Equal(["c.txt"], status.Conflicted);
    }

    [Fact]
    public async Task Status_reports_upstream_and_ahead_behind()
    {
        _repo.WriteFile("a.txt", "a\n");
        _repo.Commit("one");
        _repo.Git("branch", "base");
        _repo.Git("branch", "--set-upstream-to=base");
        _repo.WriteFile("b.txt", "b\n");
        _repo.Commit("two");

        var status = await Open().GetStatusAsync(Ct);

        Assert.Equal("base", status.Upstream);
        Assert.Equal((1, 0), (status.Ahead, status.Behind));
    }

    [Fact]
    public async Task Detached_head_has_no_branch()
    {
        _repo.WriteFile("a.txt", "a\n");
        var sha = _repo.Commit("one");
        _repo.Git("checkout", "--detach");
        var git = Open();

        Assert.Null(await git.GetCurrentBranchAsync(Ct));
        var status = await git.GetStatusAsync(Ct);
        Assert.Null(status.Branch);
        Assert.Equal(sha, status.HeadSha);
        Assert.Equal("main", await CurrentBranchAfter("checkout", "main"));

        async Task<string?> CurrentBranchAfter(params string[] args)
        {
            _repo.Git(args);
            return await git.GetCurrentBranchAsync(Ct);
        }
    }

    // ---- commits

    [Fact]
    public async Task Recent_commits_are_newest_first_with_parsed_fields()
    {
        _repo.WriteFile("a.txt", "a\n");
        var first = _repo.Commit("first");
        _repo.WriteFile("a.txt", "b\n");
        var second = _repo.Commit("second: with | separators");
        _repo.WriteFile("a.txt", "c\n");
        var third = _repo.Commit("third");

        var commits = await Open().GetRecentCommitsAsync(2, ct: Ct);

        Assert.Equal([third, second], commits.Select(c => c.Sha));
        var newest = commits[0];
        Assert.Equal(third[..7], newest.ShortSha[..7]);
        Assert.Equal(TempGitRepo.AuthorName, newest.AuthorName);
        Assert.Equal(TempGitRepo.AuthorEmail, newest.AuthorEmail);
        Assert.Equal("third", newest.Subject);
        Assert.Equal([second], newest.ParentShas);
        Assert.Equal("second: with | separators", commits[1].Subject);
        Assert.Equal([first], commits[1].ParentShas);
        Assert.True(DateTimeOffset.UtcNow - newest.CommittedAt < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Committed_at_is_parsed_from_the_strict_iso_date_including_its_offset()
    {
        _repo.WriteFile("a.txt", "a\n");
        _repo.Commit("tz");
        var iso = _repo.Git("log", "-1", "--format=%cI");
        var expected = DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

        var committed = (await Open().GetRecentCommitsAsync(1, ct: Ct)).Single().CommittedAt;

        Assert.Equal(expected, committed);
        Assert.Equal(expected.Offset, committed.Offset);
    }

    [Fact]
    public async Task New_commits_are_oldest_first_and_limit_keeps_the_newest()
    {
        _repo.WriteFile("a.txt", "a\n");
        var mainTip = _repo.Commit("base");
        _repo.Git("checkout", "-b", "feature");
        var shas = new List<string>();
        foreach (var n in new[] { "f1", "f2", "f3" })
        {
            _repo.WriteFile($"{n}.txt", n);
            shas.Add(_repo.Commit(n));
        }

        var git = Open();
        var all = await git.GetNewCommitsAsync("feature", [mainTip], 10, Ct);
        var limited = await git.GetNewCommitsAsync("feature", [mainTip], 2, Ct);
        var none = await git.GetNewCommitsAsync("feature", ["feature"], 10, Ct);

        Assert.Equal(shas, all.Select(c => c.Sha));
        Assert.Equal(shas.Skip(1), limited.Select(c => c.Sha));
        Assert.Empty(none);
    }

    [Fact]
    public async Task New_commits_without_exclusions_lists_the_whole_history()
    {
        _repo.WriteFile("a.txt", "a\n");
        var one = _repo.Commit("one");
        _repo.WriteFile("a.txt", "b\n");
        var two = _repo.Commit("two");

        var commits = await Open().GetNewCommitsAsync("main", [], 10, Ct);

        Assert.Equal([one, two], commits.Select(c => c.Sha));
    }

    [Fact]
    public async Task Branch_heads_use_short_names()
    {
        _repo.WriteFile("a.txt", "a\n");
        var mainSha = _repo.Commit("base");
        _repo.Git("checkout", "-b", "feature");
        _repo.WriteFile("b.txt", "b\n");
        var featureSha = _repo.Commit("feat");
        _repo.Git("checkout", "-b", "release/1.0");

        var heads = await Open().GetBranchHeadsAsync(Ct);

        Assert.Equal(3, heads.Count);
        Assert.Equal(mainSha, heads["main"]);
        Assert.Equal(featureSha, heads["feature"]);
        Assert.Equal(featureSha, heads["release/1.0"]);
    }

    // ---- diffs

    [Fact]
    public async Task Working_tree_diff_reports_stats_binary_files_and_excludes_untracked()
    {
        _repo.WriteFile("a.txt", "1\n2\n3\n");
        File.WriteAllBytes(Path.Combine(_repo.Path, "img.bin"), [0, 1, 2, 0, 3]);
        _repo.Commit("base");
        _repo.WriteFile("a.txt", "1\nchanged\n3\n4\n");
        File.WriteAllBytes(Path.Combine(_repo.Path, "img.bin"), [0, 9, 9, 0, 0, 0]);
        _repo.WriteFile("untracked.txt", "x\n");

        var diff = await Open().GetDiffSummaryAsync(null, null, 100_000, Ct);

        Assert.Equal("HEAD", diff.From);
        Assert.Equal(2, diff.Files.Count);
        Assert.Equal(new FileDiffStat("a.txt", 2, 1, false), diff.Files.Single(f => f.Path == "a.txt"));
        Assert.Equal(new FileDiffStat("img.bin", null, null, true), diff.Files.Single(f => f.Path == "img.bin"));
        Assert.Equal((2, 1), (diff.Insertions, diff.Deletions));
        Assert.Contains("+changed", diff.Patch);
        Assert.False(diff.PatchTruncated);
        Assert.DoesNotContain("untracked.txt", diff.Patch);
    }

    [Fact]
    public async Task Working_tree_diff_includes_staged_changes()
    {
        _repo.WriteFile("a.txt", "a\n");
        _repo.Commit("base");
        _repo.WriteFile("a.txt", "staged\n");
        _repo.Git("add", "a.txt");
        _repo.WriteFile("a.txt", "staged\nunstaged\n");

        var diff = await Open().GetDiffSummaryAsync(null, null, 10_000, Ct);

        Assert.Equal(new FileDiffStat("a.txt", 2, 1, false), Assert.Single(diff.Files));
    }

    [Fact]
    public async Task Diff_between_two_revisions_and_from_revision_to_working_tree()
    {
        _repo.WriteFile("a.txt", "a\n");
        var first = _repo.Commit("one");
        _repo.WriteFile("b.txt", "b\n");
        var second = _repo.Commit("two");
        _repo.WriteFile("c.txt", "c\n");
        _repo.Git("add", "c.txt");
        var git = Open();

        var between = await git.GetDiffSummaryAsync(first, second, 10_000, Ct);
        var toWorktree = await git.GetDiffSummaryAsync(first, null, 10_000, Ct);

        Assert.Equal((first, second), (between.From, between.To));
        Assert.Equal(["b.txt"], between.Files.Select(f => f.Path));
        Assert.Equal(["b.txt", "c.txt"], toWorktree.Files.Select(f => f.Path).Order());
        await Assert.ThrowsAsync<ArgumentException>(() => git.GetDiffSummaryAsync(null, second, 100, Ct));
    }

    [Fact]
    public async Task Patch_is_truncated_at_a_utf8_byte_limit_without_splitting_characters()
    {
        _repo.WriteFile("a.txt", "a\n");
        _repo.Commit("base");
        _repo.WriteFile("a.txt", string.Concat(Enumerable.Repeat("héllo wörld\n", 200)));

        var diff = await Open().GetDiffSummaryAsync(null, null, 101, Ct);

        Assert.True(diff.PatchTruncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(diff.Patch) <= 101);
        Assert.DoesNotContain('�', diff.Patch);
        Assert.Equal(200, Assert.Single(diff.Files).Insertions); // stats cover the whole diff, not the cut patch
    }

    [Fact]
    public async Task Commit_diff_works_for_the_root_commit_and_for_later_commits()
    {
        _repo.WriteFile("a.txt", "a\nb\n");
        _repo.WriteFile("dir/b.txt", "b\n");
        var root = _repo.Commit("root");
        _repo.WriteFile("a.txt", "a\n");
        var second = _repo.Commit("second");
        var git = Open();

        var rootDiff = await git.GetCommitDiffAsync(root, 10_000, Ct);
        var secondDiff = await git.GetCommitDiffAsync(second, 10_000, Ct);

        Assert.Equal("(empty)", rootDiff.From);
        Assert.Equal(root, rootDiff.To);
        Assert.Equal(["a.txt", "dir/b.txt"], rootDiff.Files.Select(f => f.Path).Order());
        Assert.Equal((3, 0), (rootDiff.Insertions, rootDiff.Deletions));
        Assert.Contains("+++ b/dir/b.txt", rootDiff.Patch);
        Assert.Equal(root, secondDiff.From);
        Assert.Equal(new FileDiffStat("a.txt", 0, 1, false), Assert.Single(secondDiff.Files));
    }

    [Fact]
    public async Task Staged_diff_sees_only_staged_changes()
    {
        _repo.WriteFile("a.txt", "a\n");
        _repo.WriteFile("b.txt", "b\n");
        _repo.Commit("base");
        _repo.WriteFile("a.txt", "staged\n");
        _repo.Git("add", "a.txt");
        _repo.WriteFile("b.txt", "unstaged\n");

        var diff = await Open().GetStagedDiffAsync(10_000, Ct);

        Assert.Equal(["a.txt"], diff.Files.Select(f => f.Path));
        Assert.DoesNotContain("b.txt", diff.Patch);
    }

    // ---- files, remotes

    [Fact]
    public async Task List_files_includes_untracked_and_excludes_ignored()
    {
        _repo.WriteFile(".gitignore", "ignored.log\nbuild/\n");
        _repo.WriteFile("tracked.txt", "t\n");
        _repo.Commit("base");
        _repo.WriteFile("new.txt", "n\n");
        _repo.WriteFile("ignored.log", "i\n");
        _repo.WriteFile("build/out.txt", "o\n");

        var files = await Open().ListFilesAsync(Ct);

        Assert.Equal([".gitignore", "new.txt", "tracked.txt"], files.Order());
    }

    [Fact]
    public async Task Remote_url_is_null_without_a_remote_and_the_configured_url_otherwise()
    {
        var git = Open();
        Assert.Null(await git.GetRemoteUrlAsync(ct: Ct));

        _repo.Git("remote", "add", "origin", "https://github.com/acme/widgets.git");

        Assert.Equal("https://github.com/acme/widgets.git", await git.GetRemoteUrlAsync(ct: Ct));
        Assert.Null(await git.GetRemoteUrlAsync("upstream", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => git.GetRemoteUrlAsync("--evil", Ct));
    }

    // ---- validation and failure modes

    [Fact]
    public async Task Invalid_revisions_are_rejected_before_git_runs()
    {
        _repo.WriteFile("a.txt", "a\n");
        _repo.Commit("one");
        var git = Open();

        await Assert.ThrowsAsync<ArgumentException>(() => git.GetRecentCommitsAsync(5, "--upload-pack=x", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => git.GetNewCommitsAsync("main", ["--upload-pack=x"], 5, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => git.GetCommitDiffAsync("--upload-pack=x", 10, Ct));
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => git.GetDiffSummaryAsync("main..HEAD", null, 10, Ct));
        Assert.Equal("from", ex.ParamName);
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("feature/x-1.0", true)]
    [InlineData("HEAD~3", true)]
    [InlineData("HEAD^", true)]
    [InlineData("@{u}", true)]
    [InlineData("v1.0.0", true)]
    [InlineData("", false)]
    [InlineData("-x", false)]
    [InlineData("a..b", false)]
    [InlineData("a b", false)]
    [InlineData("a;b", false)]
    public void GitRevision_validates(string revision, bool valid) => Assert.Equal(valid, GitRevision.IsValid(revision));

    [Fact]
    public void GitRevision_rejects_overlong_values_and_names_the_parameter()
    {
        Assert.False(GitRevision.IsValid(new string('a', 201)));
        Assert.True(GitRevision.IsValid(new string('a', 200)));
        var ex = Assert.Throws<ArgumentException>(() => GitRevision.Require("-x", "sha"));
        Assert.Equal("sha", ex.ParamName);
    }

    [Fact]
    public async Task A_directory_that_is_not_a_repository_fails_with_GitException()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"guardian-norepo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var git = Open(dir);

            var ex = await Assert.ThrowsAsync<GitException>(() => git.GetStatusAsync(Ct));
            Assert.NotEqual(0, ex.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(ex.StandardError));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Opening_through_a_subdirectory_keeps_paths_root_relative()
    {
        _repo.WriteFile("src/code.cs", "x\n");
        _repo.WriteFile("top.txt", "t\n");
        _repo.Commit("base");
        _repo.WriteFile("src/new.cs", "n\n");

        var git = Open(Path.Combine(_repo.Path, "src"));

        Assert.Equal(Path.GetFullPath(_repo.Path), Path.GetFullPath(git.RootPath));
        Assert.Equal(["src/code.cs", "src/new.cs", "top.txt"], (await git.ListFilesAsync(Ct)).Order());
        Assert.Equal(["src/new.cs"], (await git.GetStatusAsync(Ct)).Untracked);
    }
}
