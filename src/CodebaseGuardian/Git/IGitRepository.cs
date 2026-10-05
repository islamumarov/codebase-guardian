namespace CodebaseGuardian.Git;

public interface IGitRepository
{
    /// <summary><c>git rev-parse --show-toplevel</c> of the configured repository path, resolved once and cached.</summary>
    string RootPath { get; }

    Task<RepoStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Null when HEAD is detached or the branch is unborn (no commits yet).</summary>
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

    Task<DiffSummary> GetStagedDiffAsync(int maxPatchBytes, CancellationToken ct = default);

    /// <summary>Tracked plus untracked-not-ignored files, '/' separators, relative to <see cref="RootPath"/>.</summary>
    Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken ct = default);

    /// <summary>Null when there is no such remote.</summary>
    Task<string?> GetRemoteUrlAsync(string remote = "origin", CancellationToken ct = default);
}
