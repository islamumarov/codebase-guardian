using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace CodebaseGuardian.Sources;

/// <param name="MaxFileBytes">A larger regular file is reported as skipped and its data is not read.</param>
/// <param name="MaxTotalBytes">Uncompressed bytes read from the archive (headers, padding and skipped files included); the snapshot stops before exceeding it.</param>
/// <param name="MaxFiles">The number of files (read or skipped) yielded; the snapshot stops before exceeding it.</param>
public sealed record SnapshotCaps(long MaxFileBytes, long MaxTotalBytes, int MaxFiles);

internal static class SnapshotTarReader
{
    private const int MaxLoggedNameLength = 200;

    /// <summary>
    /// Reads a gzip-compressed tar as a stream (one file in memory at a time, never the whole archive); never throws for
    /// archive content, only for cancellation. The caller keeps ownership of <paramref name="gzipTar"/>.
    /// </summary>
    public static async IAsyncEnumerable<SnapshotFile> ReadAsync(
        Stream gzipTar, SnapshotCaps caps, ILogger logger, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var gzip = new GZipStream(gzipTar, CompressionMode.Decompress, leaveOpen: true);
        var counted = new LimitedReadStream(gzip, caps.MaxTotalBytes);
        await using var tar = new TarReader(counted, leaveOpen: true);
        var files = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            TarEntry? entry = null;
            SnapshotFile? file = null;
            string? failure = null;
            var stop = false;
            try
            {
                entry = await tar.GetNextEntryAsync(copyData: false, ct);
                if (entry is not null && Describe(entry, logger) is { } path)
                {
                    if (files + 1 > caps.MaxFiles)
                    {
                        stop = true;
                    }
                    else if (entry.Length > caps.MaxFileBytes)
                    {
                        file = new SnapshotFile(path, null, SnapshotLimits.TooLargeReason);
                    }
                    else if (counted.Count + entry.Length > caps.MaxTotalBytes)
                    {
                        stop = true;
                    }
                    else
                    {
                        file = new SnapshotFile(path, await ReadDataAsync(entry, ct), null);
                    }
                }
            }
            catch (ByteLimitReachedException)
            {
                stop = true;
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException or InvalidOperationException)
            {
                // InvalidOperationException: the BCL reader's answer to a header whose size field exceeds what it can hold.
                failure = ex.Message;
            }

            if (failure is not null)
            {
                yield return Incomplete($"the archive could not be read: {failure}");
                yield break;
            }

            if (stop)
            {
                yield return Incomplete($"stopped after {files} files and {counted.Count} bytes (Guardian:Remote:MaxSnapshotFiles / MaxSnapshotBytes)");
                yield break;
            }

            if (entry is null)
            {
                yield break;
            }

            if (file is not null)
            {
                files++;
                yield return file;
            }
        }
    }

    private sealed class ByteLimitReachedException : IOException;

    /// <summary>Counts the bytes read and throws <see cref="ByteLimitReachedException"/> instead of delivering more than the limit.</summary>
    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        public long Count { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Take(await inner.ReadAsync(buffer[..Allowed(buffer.Length)], cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(Span<byte> buffer) => Take(inner.Read(buffer[..Allowed(buffer.Length)]));

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // Ask for at most one byte beyond the limit: reaching it proves the archive is larger without decompressing more.
        private int Allowed(int requested) => (int)Math.Min(requested, Math.Max(limit - Count, 0) + 1);

        private int Take(int read)
        {
            Count += read;
            if (Count > limit)
            {
                Count = limit;
                throw new ByteLimitReachedException();
            }

            return read;
        }
    }

    private static SnapshotFile Incomplete(string reason) => new(string.Empty, null, reason);

    private static async Task<ReadOnlyMemory<byte>> ReadDataAsync(TarEntry entry, CancellationToken ct)
    {
        if (entry.DataStream is null || entry.Length == 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        var buffer = new byte[entry.Length]; // already checked against MaxFileBytes
        await entry.DataStream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    /// <summary>The snapshot path of a regular file entry; null for everything that is not one, or that is unsafe (logged).</summary>
    private static string? Describe(TarEntry entry, ILogger logger)
    {
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
        {
            return null; // global/extended headers, directories, symlinks, hard links, devices, FIFOs
        }

        var name = entry.Name;
        if (name.StartsWith('/') || name.Contains('\\') || name.Split('/').Contains(".."))
        {
            logger.LogWarning("Skipping archive entry with an unsafe path: {Name}", Sanitize(name));
            return null;
        }

        // Drop the top directory ("owner-repo-sha7"); empty and "." segments carry no meaning.
        var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(segment => segment != ".").Skip(1).ToArray();
        return segments.Length == 0 ? null : string.Join('/', segments);
    }

    private static string Sanitize(string name)
    {
        var clean = new string([.. name.Select(c => char.IsControl(c) ? '?' : c)]);
        return clean.Length > MaxLoggedNameLength ? clean[..MaxLoggedNameLength] + "..." : clean;
    }
}
