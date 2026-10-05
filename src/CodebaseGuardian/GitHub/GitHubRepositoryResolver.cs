using CodebaseGuardian.Git;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.GitHub;

public sealed class GitHubRepositoryResolver(IGitRepository git, IOptions<GitHubOptions> options) : IGitHubRepositoryResolver
{
    private volatile GitHubRepositoryRef? _resolved;

    public async ValueTask<GitHubRepositoryRef?> ResolveAsync(CancellationToken cancellationToken)
    {
        if (_resolved is { } cached)
        {
            return cached;
        }

        var configured = options.Value;
        if (!string.IsNullOrWhiteSpace(configured.Owner) && !string.IsNullOrWhiteSpace(configured.Repository))
        {
            return _resolved = new GitHubRepositoryRef(configured.Owner, configured.Repository);
        }

        var remote = await git.GetRemoteUrlAsync("origin", cancellationToken);
        if (remote is not null && GitHubRepositoryRef.TryParseRemoteUrl(remote, out var parsed))
        {
            return _resolved = parsed;
        }

        return null;
    }
}
