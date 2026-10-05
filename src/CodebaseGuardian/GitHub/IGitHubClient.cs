namespace CodebaseGuardian.GitHub;

public interface IGitHubClient
{
    /// <summary>Throws <see cref="GitHubUnavailableException"/> when disabled, without a token, or without a github.com origin.</summary>
    Task<GitHubRepositoryRef> GetRepositoryAsync(CancellationToken ct);

    Task<string> GetDefaultBranchAsync(CancellationToken ct);

    Task<bool> BranchExistsAsync(string branch, CancellationToken ct);

    Task<GitHubIssue> CreateIssueAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken ct);

    Task<GitHubComment> CreateIssueCommentAsync(int number, string body, CancellationToken ct);

    Task<GitHubPullRequest> CreatePullRequestAsync(string head, string @base, string title, string body, bool draft, CancellationToken ct);

    /// <summary>Includes pull requests, flagged <see cref="GitHubIssue.IsPullRequest"/>.</summary>
    Task<IReadOnlyList<GitHubIssue>> ListIssuesSinceAsync(DateTimeOffset since, CancellationToken ct);

    Task<IReadOnlyList<GitHubComment>> ListIssueCommentsSinceAsync(DateTimeOffset since, CancellationToken ct);

    Task<IReadOnlyList<GitHubComment>> ListReviewCommentsSinceAsync(DateTimeOffset since, CancellationToken ct);

    Task<IReadOnlyList<GitHubWorkflowRun>> ListFailedWorkflowRunsSinceAsync(DateTimeOffset since, CancellationToken ct);
}
