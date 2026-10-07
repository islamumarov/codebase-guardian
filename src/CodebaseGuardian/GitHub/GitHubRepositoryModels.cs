namespace CodebaseGuardian.GitHub;

public sealed record GitHubRepositoryInfo(string DefaultBranch, bool Private);

public sealed record GitHubCommit(
    string Sha, string AuthorName, string AuthorEmail, DateTimeOffset CommittedAt, string Message, IReadOnlyList<string> ParentShas);

/// <summary>Status: "added" | "removed" | "modified" | "renamed" | "copied" | "changed" | "unchanged". Patch null when GitHub omits it.</summary>
public sealed record GitHubFileChange(string Path, string? PreviousPath, string Status, int Additions, int Deletions, string? Patch);

public sealed record GitHubCommitDetail(GitHubCommit Commit, IReadOnlyList<GitHubFileChange> Files);

/// <summary>Status: "ahead" | "behind" | "identical" | "diverged". Commits oldest first (GitHub returns at most 250).</summary>
public sealed record GitHubComparison(
    string Status, int AheadBy, int BehindBy, string BaseSha, IReadOnlyList<GitHubCommit> Commits, IReadOnlyList<GitHubFileChange> Files);
