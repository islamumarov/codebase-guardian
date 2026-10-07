namespace CodebaseGuardian.GitHub;

public class GitHubApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class GitHubRateLimitException(DateTimeOffset resetAt)
    : GitHubApiException(429, $"GitHub rate limit reached; resets at {resetAt:O}.")
{
    public DateTimeOffset ResetAt { get; } = resetAt;
}

/// <summary>The integration cannot be used: disabled, no token, or no GitHub repository. The message says which.</summary>
public sealed class GitHubUnavailableException(string reason) : Exception(reason);

/// <summary>The repository does not exist or the (possibly absent) token cannot read it.</summary>
public sealed class GitHubNotFoundException(GitHubRepositoryRef repository)
    : GitHubApiException(404, $"Repository {repository} was not found, or the token cannot read it (private repositories need contents:read).");

/// <summary>A branch, tag or commit does not exist (any more) in the repository.</summary>
public sealed class GitHubRevisionNotFoundException(string revision, GitHubRepositoryRef repository)
    : GitHubApiException(404, $"Revision '{revision}' was not found in {repository}.")
{
    public string Revision { get; } = revision;
}
