namespace CodebaseGuardian.GitHub;

/// <summary>Read-only, token-optional access to one GitHub repository; the only thing remote mode needs from GitHub.</summary>
public interface IGitHubRepositoryApi
{
    /// <summary>Default branch and visibility. Conditional GET.</summary>
    Task<GitHubRepositoryInfo> GetRepositoryAsync(CancellationToken ct);

    /// <summary>Branch name to head SHA (at most the first 500 branches). Conditional first page; an empty repository yields an empty map.</summary>
    Task<IReadOnlyDictionary<string, string>> GetBranchHeadsAsync(CancellationToken ct);

    /// <summary>Newest first, at most <paramref name="limit"/> (clamped to 1..100) commits reachable from <paramref name="sha"/> (default branch when null). An empty repository yields an empty list.</summary>
    Task<IReadOnlyList<GitHubCommit>> ListCommitsAsync(string? sha, int limit, CancellationToken ct);

    /// <summary>The commit with its changed files. Throws <see cref="GitHubRevisionNotFoundException"/> for an unknown SHA.</summary>
    Task<GitHubCommitDetail> GetCommitAsync(string sha, CancellationToken ct);

    /// <summary>Compares <paramref name="base"/> with <paramref name="head"/>. Throws <see cref="GitHubRevisionNotFoundException"/> when either side is unknown.</summary>
    Task<GitHubComparison> CompareAsync(string @base, string head, CancellationToken ct);

    /// <summary>The raw content of a file at a ref, or null when it does not exist.</summary>
    Task<byte[]?> GetFileAsync(string path, string @ref, CancellationToken ct);

    /// <summary>The repository snapshot at a ref as a gzip tarball stream.</summary>
    Task<Stream> OpenTarballAsync(string @ref, CancellationToken ct);
}
