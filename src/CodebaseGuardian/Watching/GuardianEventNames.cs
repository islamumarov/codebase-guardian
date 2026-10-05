namespace CodebaseGuardian.Watching;

/// <summary>Every event name in the catalog (spec section 5.5), including the ones later epics publish.</summary>
public static class GuardianEventNames
{
    public const string RepoCommitCreated = "repo.commit.created";
    public const string RepoBranchChanged = "repo.branch.changed";
    public const string RepoFilesChanged = "repo.files.changed";
    public const string RepoDependenciesChanged = "repo.dependencies.changed";
    public const string ChecksCompleted = "checks.completed";
    public const string ChecksFailed = "checks.failed";
    public const string SecuritySecretDetected = "security.secret_detected";

    // Epic 2
    public const string GithubIssueOpened = "github.issue.opened";
    public const string GithubPrCommentCreated = "github.pr.comment.created";
    public const string GithubCiFailed = "github.ci.failed";

    // Epic 3
    public const string ScanCompleted = "scan.completed";
}
