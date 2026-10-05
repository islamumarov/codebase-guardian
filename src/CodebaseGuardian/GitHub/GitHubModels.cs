namespace CodebaseGuardian.GitHub;

public sealed record GitHubUser(string Login);

public sealed record GitHubIssue(
    long Id, int Number, string Title, string? Body, string HtmlUrl, GitHubUser User,
    IReadOnlyList<string> Labels, DateTimeOffset CreatedAt, bool IsPullRequest);

/// <param name="Kind">"issue" or "review".</param>
public sealed record GitHubComment(
    long Id, string Kind, int IssueNumber, string? Body, string HtmlUrl, GitHubUser User,
    DateTimeOffset CreatedAt, string? Path, int? Line, bool OnPullRequest);

public sealed record GitHubPullRequest(long Id, int Number, string Title, string HtmlUrl, string Head, string Base, bool Draft);

public sealed record GitHubWorkflowRun(
    long Id, int RunAttempt, string Name, string HeadBranch, string HeadSha, string HtmlUrl,
    string? Conclusion, DateTimeOffset UpdatedAt);
