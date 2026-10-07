using CodebaseGuardian.Git;

namespace CodebaseGuardian.Sources;

/// <summary>Validation for revisions that reach a GitHub API URL: the API resolves names and SHAs, not git's revision syntax.</summary>
public static class RemoteRevision
{
    public const string InvalidMessage = "Remote mode accepts branch names, tag names and commit SHAs only.";

    private static readonly string[] Forbidden = ["~", "^", "@{", ":"];

    /// <summary>
    /// <see cref="GitRevision.IsValid"/> and none of "~", "^", "@{", ":". Else
    /// <c>ArgumentException("Remote mode accepts branch names, tag names and commit SHAs only.", parameterName)</c>.
    /// </summary>
    public static string Require(string revision, string parameterName) =>
        revision is not null
        && GitRevision.IsValid(revision)
        && !Forbidden.Any(token => revision.Contains(token, StringComparison.Ordinal))
            ? revision
            : throw new ArgumentException(InvalidMessage, parameterName);
}
