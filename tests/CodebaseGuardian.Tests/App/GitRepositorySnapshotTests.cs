using System.Text;
using CodebaseGuardian.Git;
using CodebaseGuardian.Hosting;
using CodebaseGuardian.Processes;
using CodebaseGuardian.Sources;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Tests.App;

public sealed class GitRepositorySnapshotTests : IDisposable
{
    private readonly TempGitRepo _repo = TempGitRepo.Create();

    public void Dispose() => _repo.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GitRepository Open() =>
        new(new ProcessRunner(), Options.Create(new GuardianOptions { RepositoryPath = _repo.Path }));

    private static async Task<List<SnapshotFile>> SnapshotAsync(IRepositorySource source)
    {
        var files = new List<SnapshotFile>();
        await foreach (var file in source.ReadSnapshotAsync(Ct))
        {
            files.Add(file);
        }

        return files;
    }

    [Fact]
    public void Kind_is_local_and_the_display_name_is_the_repository_root()
    {
        var git = Open();

        Assert.Equal(RepositorySourceKind.Local, git.Kind);
        Assert.Equal(git.RootPath, git.DisplayName);
    }

    [Fact]
    public async Task The_snapshot_lists_tracked_and_untracked_files_with_their_content_and_omits_ignored_ones()
    {
        _repo.WriteFile(".gitignore", "ignored.log\n");
        _repo.WriteFile("README.md", "# hello\n");
        _repo.WriteFile("src/app.cs", "class A {}\n");
        _repo.Commit("initial");
        _repo.WriteFile("untracked.txt", "new\n");
        _repo.WriteFile("ignored.log", "noise\n");

        var files = await SnapshotAsync(Open());

        Assert.Equal(
            [".gitignore", "README.md", "src/app.cs", "untracked.txt"],
            files.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.All(files, f => Assert.Null(f.Skipped));
        Assert.Equal("# hello\n", Encoding.UTF8.GetString(files.Single(f => f.Path == "README.md").Content!.Value.Span));
        Assert.Equal("new\n", Encoding.UTF8.GetString(files.Single(f => f.Path == "untracked.txt").Content!.Value.Span));
        Assert.DoesNotContain(files, f => f.IsIncompleteMarker); // a local snapshot is never cut short
    }

    [Fact]
    public async Task A_symlink_is_absent_from_the_snapshot()
    {
        _repo.WriteFile("real.txt", "real\n");
        var outside = Path.Combine(Path.GetTempPath(), $"guardian-outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "secret\n", Ct);
        try
        {
            try
            {
                File.CreateSymbolicLink(Path.Combine(_repo.Path, "link.txt"), outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Assert.Skip("Symbolic links cannot be created here.");
            }

            var files = await SnapshotAsync(Open());

            Assert.Equal(["real.txt"], files.Select(f => f.Path));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task A_file_over_one_megabyte_is_reported_as_skipped_without_content()
    {
        await File.WriteAllBytesAsync(Path.Combine(_repo.Path, "big.bin"), new byte[SnapshotLimits.MaxFileBytes + 1], Ct);
        await File.WriteAllBytesAsync(Path.Combine(_repo.Path, "limit.bin"), new byte[SnapshotLimits.MaxFileBytes], Ct);

        var files = await SnapshotAsync(Open());

        var big = files.Single(f => f.Path == "big.bin");
        Assert.Null(big.Content);
        Assert.Equal("larger than 1 MB", big.Skipped);
        var limit = files.Single(f => f.Path == "limit.bin");
        Assert.Equal((int)SnapshotLimits.MaxFileBytes, limit.Content!.Value.Length);
        Assert.Null(limit.Skipped);
    }

    [Fact]
    public async Task A_path_with_non_ascii_characters_and_spaces_appears_verbatim()
    {
        _repo.WriteFile("docs/naïve file.md", "hi\n");

        var files = await SnapshotAsync(Open());

        Assert.Equal(["docs/naïve file.md"], files.Select(f => f.Path));
    }

    [Fact]
    public async Task A_tracked_file_deleted_from_the_working_tree_is_left_out()
    {
        _repo.WriteFile("gone.txt", "x\n");
        _repo.WriteFile("kept.txt", "y\n");
        _repo.Commit("initial");
        _repo.DeleteFile("gone.txt");

        var files = await SnapshotAsync(Open());

        Assert.Equal(["kept.txt"], files.Select(f => f.Path));
    }

    [Fact]
    public async Task ReadFileAsync_returns_the_bytes_of_a_file_and_null_when_it_is_missing()
    {
        _repo.WriteFile("README.md", "# hello\n");

        var git = Open();

        Assert.Equal("# hello\n", Encoding.UTF8.GetString((await git.ReadFileAsync("README.md", Ct))!));
        Assert.Null(await git.ReadFileAsync("missing.txt", Ct));
    }

    [Fact]
    public async Task ReadFileAsync_refuses_a_symlink()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"guardian-outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "secret\n", Ct);
        try
        {
            try
            {
                File.CreateSymbolicLink(Path.Combine(_repo.Path, "link.txt"), outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Assert.Skip("Symbolic links cannot be created here.");
            }

            Assert.Null(await Open().ReadFileAsync("link.txt", Ct));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public async Task ReadFileAsync_rejects_paths_that_leave_the_snapshot(string path)
    {
        _repo.WriteFile("README.md", "# hello\n");

        await Assert.ThrowsAsync<ArgumentException>(() => Open().ReadFileAsync(path, Ct));
    }
}
