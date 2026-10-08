using CodebaseGuardian.Git;

namespace CodebaseGuardian.Sources;

public enum RepositorySourceKind { Local, GitHub }

/// <summary>
/// What every watched repository offers, whether it is a checkout on disk or a GitHub repository read through its REST API.
/// Consumers depend on this and not on the checkout-only interface in the Git folder, which adds what only a checkout can do.
/// </summary>
public interface IRepositorySource
{
    RepositorySourceKind Kind { get; }

    /// <summary>Local: RootPath. GitHub: "github.com/owner/name" (the API host instead of github.com for GHES).</summary>
    string DisplayName { get; }

    Task<RepoStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Local: checked-out branch (null when detached/unborn). GitHub: the default branch.</summary>
    Task<string?> GetCurrentBranchAsync(CancellationToken ct = default);

    /// <summary>Null when the repository has no commits.</summary>
    Task<string?> GetHeadShaAsync(CancellationToken ct = default);

    /// <summary>Short branch name ("main") to full SHA.</summary>
    Task<IReadOnlyDictionary<string, string>> GetBranchHeadsAsync(CancellationToken ct = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<CommitInfo>> GetRecentCommitsAsync(int limit, string? revision = null, CancellationToken ct = default);

    /// <summary>Commits reachable from <paramref name="tip"/> but from none of <paramref name="excludeTips"/>; oldest first; at most <paramref name="limit"/> (the newest ones).</summary>
    Task<IReadOnlyList<CommitInfo>> GetNewCommitsAsync(string tip, IReadOnlyCollection<string> excludeTips, int limit, CancellationToken ct = default);

    /// <summary>(null,null) = working tree incl. staged vs HEAD; (from,null) = working tree vs from; (from,to) = from vs to. Untracked files excluded.</summary>
    Task<DiffSummary> GetDiffSummaryAsync(string? from, string? to, int maxPatchBytes, CancellationToken ct = default);

    /// <summary>Works for root commits.</summary>
    Task<DiffSummary> GetCommitDiffAsync(string sha, int maxPatchBytes, CancellationToken ct = default);

    /// <summary>One snapshot file (local: working tree; GitHub: default-branch head); null when absent. Path is '/'-separated and relative; "..", absolute paths and empty paths throw ArgumentException.</summary>
    Task<byte[]?> ReadFileAsync(string path, CancellationToken ct = default);

    /// <summary>Every regular, non-symlink file of the snapshot. Ends with a sentinel (Path == "") when the snapshot stopped early.</summary>
    IAsyncEnumerable<SnapshotFile> ReadSnapshotAsync(CancellationToken ct = default);
}

/// <summary>Content null => skipped, Skipped says why. Path "" => sentinel: the snapshot is incomplete, Skipped says why.</summary>
public sealed record SnapshotFile(string Path, ReadOnlyMemory<byte>? Content, string? Skipped)
{
    public bool IsIncompleteMarker => Path.Length == 0;
}

public static class SnapshotLimits
{
    /// <summary>Files larger than this are not read into a snapshot.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    /// <summary>The <see cref="SnapshotFile.Skipped"/> text of a file over <see cref="MaxFileBytes"/>.</summary>
    public const string TooLargeReason = "larger than 1 MB";
}
