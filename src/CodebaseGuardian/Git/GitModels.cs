namespace CodebaseGuardian.Git;

/// <summary>Kind is one of "added", "modified", "deleted", "renamed", "copied", "type-changed", "unmerged".</summary>
public sealed record FileChange(string Path, string Kind);

public sealed record RepoStatus(
    string? Branch,
    string? HeadSha,
    string? Upstream,
    int Ahead,
    int Behind,
    IReadOnlyList<FileChange> Staged,
    IReadOnlyList<FileChange> Unstaged,
    IReadOnlyList<string> Untracked,
    IReadOnlyList<string> Conflicted);

public sealed record CommitInfo(
    string Sha,
    string ShortSha,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset CommittedAt,
    string Subject,
    IReadOnlyList<string> ParentShas);

/// <summary>Insertions and Deletions are null for binary files.</summary>
public sealed record FileDiffStat(string Path, int? Insertions, int? Deletions, bool Binary);

public sealed record DiffSummary(
    string From,
    string To,
    IReadOnlyList<FileDiffStat> Files,
    int Insertions,
    int Deletions,
    string Patch,
    bool PatchTruncated);
