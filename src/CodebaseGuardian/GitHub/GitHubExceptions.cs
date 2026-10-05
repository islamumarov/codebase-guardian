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
