using System.Formats.Tar;
using System.Text;
using CodebaseGuardian.Sources;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace CodebaseGuardian.Tests.App;

public class SnapshotTarReaderTests
{
    private static readonly SnapshotCaps Generous = new(SnapshotLimits.MaxFileBytes, 100_000_000, 20_000);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (string, TarEntryType, byte[], string?) File(string name, string content) =>
        (name, TarEntryType.RegularFile, Encoding.UTF8.GetBytes(content), null);

    private static async Task<List<SnapshotFile>> Read(byte[] archive, SnapshotCaps? caps = null, ILogger? logger = null, CancellationToken? ct = null)
    {
        await using var stream = new MemoryStream(archive);
        var files = new List<SnapshotFile>();
        await foreach (var file in SnapshotTarReader.ReadAsync(
            stream, caps ?? Generous, logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, ct ?? Ct))
        {
            files.Add(file);
        }

        return files;
    }

    [Fact]
    public async Task Regular_files_are_yielded_with_the_top_directory_stripped_and_their_exact_bytes()
    {
        byte[] binary = [0, 1, 2, 255, 254, 10, 13];
        var archive = TarballBuilder.Build([
            File("README.md", "# hello"),
            ("bin/data.bin", TarEntryType.RegularFile, binary, null),
            ("empty.txt", TarEntryType.RegularFile, [], null),
        ]);

        var files = await Read(archive);

        Assert.Equal(["README.md", "bin/data.bin", "empty.txt"], files.Select(f => f.Path));
        Assert.All(files, f => Assert.Null(f.Skipped));
        Assert.Equal("# hello", Encoding.UTF8.GetString(files[0].Content!.Value.Span));
        Assert.Equal(binary, files[1].Content!.Value.ToArray());
        Assert.Empty(files[2].Content!.Value.ToArray());
    }

    [Fact]
    public async Task A_non_ASCII_path_is_kept_verbatim()
    {
        var files = await Read(TarballBuilder.Build([File("docs/naïve file.md", "x")]));

        Assert.Equal("docs/naïve file.md", Assert.Single(files).Path);
    }

    [Fact]
    public async Task Symlinks_and_hard_links_are_not_yielded()
    {
        var archive = TarballBuilder.Build([
            File("real.txt", "real"),
            ("link.txt", TarEntryType.SymbolicLink, [], "/etc/passwd"),
            ("hard.txt", TarEntryType.HardLink, [], "real.txt"),
        ]);

        var files = await Read(archive);

        Assert.Equal("real.txt", Assert.Single(files).Path);
    }

    [Fact]
    public async Task Traversal_and_absolute_entries_are_never_yielded_and_are_logged()
    {
        var logs = new CapturingLoggerProvider();
        var archive = TarballBuilder.Build([
            File("../../etc/passwd", "root"),
            File("a/../../b", "x"),
            File("..\\evil", "x"),
            File("ok.txt", "ok"),
        ]);
        var raw = TarballBuilder.Build([
            ("acme-widgets-abc1234/../../etc/passwd", TarEntryType.RegularFile, "root"u8.ToArray(), null),
            ("/abs", TarEntryType.RegularFile, "abs"u8.ToArray(), null),
            ("acme-widgets-abc1234/fine.txt", TarEntryType.RegularFile, "fine"u8.ToArray(), null),
        ], rawNames: true);

        var files = await Read(archive, logger: logs.CreateLogger("t"));
        var rawFiles = await Read(raw, logger: logs.CreateLogger("t"));

        Assert.Equal(["ok.txt"], files.Select(f => f.Path));
        Assert.Equal(["fine.txt"], rawFiles.Select(f => f.Path));
        Assert.Contains(logs.Messages, message => message.Contains("etc/passwd"));
        Assert.Contains(logs.Messages, message => message.Contains("/abs"));
    }

    [Fact]
    public async Task A_logged_entry_name_has_control_characters_removed()
    {
        var logs = new CapturingLoggerProvider();
        var archive = TarballBuilder.Build([File("../evil\r\u001b[2Jforged", "x")]);

        await Read(archive, logger: logs.CreateLogger("t"));

        Assert.Contains(logs.Messages, message => message.Contains("evil"));
        Assert.DoesNotContain(logs.Messages, message => message.Any(char.IsControl));
    }

    [Fact]
    public async Task An_over_cap_file_is_reported_as_skipped_without_content()
    {
        var archive = TarballBuilder.Build([File("big.bin", new string('x', 20)), File("small.txt", "ok")]);

        var files = await Read(archive, new SnapshotCaps(10, 1000, 100));

        Assert.Equal(["big.bin", "small.txt"], files.Select(f => f.Path));
        Assert.Null(files[0].Content);
        Assert.Equal("larger than 1 MB", files[0].Skipped);
        Assert.Equal(SnapshotLimits.TooLargeReason, files[0].Skipped);
        Assert.NotNull(files[1].Content);
    }

    [Fact]
    public async Task More_files_than_MaxFiles_yield_the_cap_then_a_sentinel()
    {
        var archive = TarballBuilder.Build([File("a", "1"), File("b", "2"), File("c", "3")]);

        var files = await Read(archive, new SnapshotCaps(1000, 1000, 2));

        Assert.Equal(["a", "b", ""], files.Select(f => f.Path));
        var sentinel = files[2];
        Assert.True(sentinel.IsIncompleteMarker);
        Assert.Null(sentinel.Content);
        Assert.Equal("stopped after 2 files and 2 bytes (Guardian:Remote:MaxSnapshotFiles / MaxSnapshotBytes)", sentinel.Skipped);
    }

    [Fact]
    public async Task Exactly_MaxFiles_files_end_without_a_sentinel()
    {
        var files = await Read(TarballBuilder.Build([File("a", "1"), File("b", "2")]), new SnapshotCaps(1000, 1000, 2));

        Assert.Equal(["a", "b"], files.Select(f => f.Path));
    }

    [Fact]
    public async Task More_bytes_than_MaxTotalBytes_yield_files_so_far_then_a_sentinel()
    {
        var archive = TarballBuilder.Build([File("a", "12345678"), File("b", "abcdefgh")]);

        var files = await Read(archive, new SnapshotCaps(1000, 10, 100));

        Assert.Equal(["a", ""], files.Select(f => f.Path));
        Assert.Equal("stopped after 1 files and 8 bytes (Guardian:Remote:MaxSnapshotFiles / MaxSnapshotBytes)", files[1].Skipped);
    }

    [Fact]
    public async Task Bytes_that_are_not_gzip_yield_only_a_sentinel()
    {
        var random = new byte[2048];
        new Random(42).NextBytes(random);

        var files = await Read(random);

        var sentinel = Assert.Single(files);
        Assert.True(sentinel.IsIncompleteMarker);
        Assert.StartsWith("the archive could not be read: ", sentinel.Skipped);
    }

    [Fact]
    public async Task A_truncated_archive_yields_what_was_complete_then_a_sentinel()
    {
        var archive = TarballBuilder.Build([File("a", new string('a', 5000)), File("b", new string('b', 5000))]);

        var files = await Read(archive[..(archive.Length / 2)]);

        Assert.True(files[^1].IsIncompleteMarker);
        Assert.StartsWith("the archive could not be read: ", files[^1].Skipped);
        Assert.DoesNotContain(files.SkipLast(1), f => f.Path.Length == 0);
    }

    [Fact]
    public async Task A_cancelled_token_surfaces_as_cancellation_not_as_a_sentinel()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Read(TarballBuilder.Build([File("a", "1")]), ct: cts.Token));
    }

    [Fact]
    public async Task Cancelling_during_enumeration_surfaces_as_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await using var stream = new MemoryStream(TarballBuilder.Build([File("a", "1"), File("b", "2")]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var file in SnapshotTarReader.ReadAsync(
                stream, Generous, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, cts.Token))
            {
                await cts.CancelAsync();
            }
        });
    }
}
