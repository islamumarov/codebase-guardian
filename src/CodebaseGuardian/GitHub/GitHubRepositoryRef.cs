using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace CodebaseGuardian.GitHub;

public sealed partial record GitHubRepositoryRef(string Owner, string Name)
{
    internal const string SegmentPattern = "^[A-Za-z0-9_.-]{1,100}$";

    [GeneratedRegex(SegmentPattern)]
    private static partial Regex Segment();

    internal static bool IsValidSegment(string value) => Segment().IsMatch(value);

    /// <summary>
    /// Accepts <c>https://github.com/o/r(.git)</c> (also with a user), <c>git@github.com:o/r(.git)</c> and
    /// <c>ssh://git@github.com/o/r(.git)</c>; github.com only. Anything else is rejected.
    /// </summary>
    public static bool TryParseRemoteUrl(string url, [NotNullWhen(true)] out GitHubRepositoryRef? repository)
    {
        repository = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        string path;
        var trimmed = url.Trim();
        if (trimmed.StartsWith("git@", StringComparison.OrdinalIgnoreCase) && !trimmed.Contains("://", StringComparison.Ordinal))
        {
            // scp-like form: git@github.com:owner/repo.git
            var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0 || !trimmed[4..colon].Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            path = trimmed[(colon + 1)..];
        }
        else
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "ssh")
                || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                return false;
            }

            path = uri.AbsolutePath;
        }

        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        var parts = path.Split('/');
        if (parts.Length != 2 || !IsValidSegment(parts[0]) || !IsValidSegment(parts[1]))
        {
            return false;
        }

        repository = new GitHubRepositoryRef(parts[0], parts[1]);
        return true;
    }

    public override string ToString() => $"{Owner}/{Name}";
}
