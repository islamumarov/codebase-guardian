namespace CodebaseGuardian.GitHub;

public sealed record GitHubRepositoryInfo(string DefaultBranch, bool Private);

public sealed record GitHubCommit(
    string Sha, string AuthorName, string AuthorEmail, DateTimeOffset CommittedAt, string Message, IReadOnlyList<string> ParentShas);

/// <summary>Status: "added" | "removed" | "modified" | "renamed" | "copied" | "changed" | "unchanged". Patch null when GitHub omits it.</summary>
public sealed record GitHubFileChange(string Path, string? PreviousPath, string Status, int Additions, int Deletions, string? Patch);

/// <param name="Commit">The commit itself.</param>
/// <param name="Files">The changed files that were read (at most five pages of them).</param>
/// <param name="FilesTruncated">True when the file page cap was reached while GitHub still had more pages: <paramref name="Files"/> is incomplete.</param>
public sealed record GitHubCommitDetail(GitHubCommit Commit, IReadOnlyList<GitHubFileChange> Files, bool FilesTruncated);

/// <summary>Status: "ahead" | "behind" | "identical" | "diverged". Commits oldest first (GitHub returns at most 250).</summary>
/// <param name="BaseSha">GitHub's <c>base_commit</c>: the tip of the base ref as it is now.</param>
/// <param name="MergeBaseSha">GitHub's <c>merge_base_commit</c>: the best common ancestor of base and head (the commit a diverged head branched from).</param>
public sealed record GitHubComparison(
    string Status, int AheadBy, int BehindBy, string BaseSha, string MergeBaseSha,
    IReadOnlyList<GitHubCommit> Commits, IReadOnlyList<GitHubFileChange> Files);
