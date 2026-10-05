namespace CodebaseGuardian.GitHub;

public interface IGitHubTokenProvider
{
    /// <summary>
    /// The <c>GITHUB_TOKEN</c> environment variable (when non-empty), else the output of <c>gh auth token</c>; null when
    /// neither yields a token. A found token is cached for the process lifetime and is never logged.
    /// </summary>
    ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken);
}
