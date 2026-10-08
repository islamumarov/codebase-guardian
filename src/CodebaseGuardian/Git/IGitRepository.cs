using CodebaseGuardian.Sources;

namespace CodebaseGuardian.Git;

/// <summary>A repository checkout on disk: everything an <see cref="IRepositorySource"/> offers plus what only a checkout can do.</summary>
public interface IGitRepository : IRepositorySource
{
    /// <summary><c>git rev-parse --show-toplevel</c> of the configured repository path, resolved once and cached.</summary>
    string RootPath { get; }

    Task<DiffSummary> GetStagedDiffAsync(int maxPatchBytes, CancellationToken ct = default);

    /// <summary>Tracked plus untracked-not-ignored files, '/' separators, relative to <see cref="RootPath"/>.</summary>
    Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken ct = default);

    /// <summary>Null when there is no such remote.</summary>
    Task<string?> GetRemoteUrlAsync(string remote = "origin", CancellationToken ct = default);
}
