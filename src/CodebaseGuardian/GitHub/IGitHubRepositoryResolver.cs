namespace CodebaseGuardian.GitHub;

public interface IGitHubRepositoryResolver
{
    /// <summary>The configured owner and repository, else the origin remote; null when neither is a GitHub repository. Cached once resolved.</summary>
    ValueTask<GitHubRepositoryRef?> ResolveAsync(CancellationToken cancellationToken);
}
