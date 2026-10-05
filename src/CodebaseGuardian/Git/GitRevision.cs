using System.Text.RegularExpressions;

namespace CodebaseGuardian.Git;

/// <summary>Validation for revision strings that reach the git command line, so they can never be read as options.</summary>
public static partial class GitRevision
{
    private const int MaxLength = 200;

    [GeneratedRegex("^[A-Za-z0-9_./~^@{}-]+$")]
    private static partial Regex AllowedCharacters();

    /// <summary>
    /// Accepts branch and tag names, SHAs, <c>HEAD~n</c>, <c>HEAD^</c>. Ranges (<c>A..B</c>) are not accepted;
    /// callers pass the two ends separately.
    /// </summary>
    public static bool IsValid(string revision) =>
        !string.IsNullOrEmpty(revision)
        && revision.Length <= MaxLength
        && revision[0] != '-'
        && !revision.Contains("..", StringComparison.Ordinal)
        && AllowedCharacters().IsMatch(revision);

    public static string Require(string revision, string parameterName) =>
        IsValid(revision)
            ? revision
            : throw new ArgumentException($"'{revision}' is not a valid git revision.", parameterName);
}
