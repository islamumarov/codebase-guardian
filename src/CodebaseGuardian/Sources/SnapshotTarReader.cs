using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace CodebaseGuardian.Sources;

/// <param name="MaxFileBytes">A larger regular file is reported as skipped and its data is not read.</param>
/// <param name="MaxTotalBytes">The content bytes of all yielded files together; the snapshot stops before exceeding it.</param>
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
        await using var tar = new TarReader(gzip, leaveOpen: true);
        var files = 0;
        long bytes = 0;

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
                    else if (bytes + entry.Length > caps.MaxTotalBytes)
                    {
                        stop = true;
                    }
                    else
                    {
                        file = new SnapshotFile(path, await ReadDataAsync(entry, ct), null);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException)
            {
                failure = ex.Message;
            }

            if (failure is not null)
            {
                yield return Incomplete($"the archive could not be read: {failure}");
                yield break;
            }

            if (entry is null)
            {
                yield break;
            }

            if (stop)
            {
                yield return Incomplete($"stopped after {files} files and {bytes} bytes (Guardian:Remote:MaxSnapshotFiles / MaxSnapshotBytes)");
                yield break;
            }

            if (file is not null)
            {
                files++;
                bytes += file.Content?.Length ?? 0;
                yield return file;
            }
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
