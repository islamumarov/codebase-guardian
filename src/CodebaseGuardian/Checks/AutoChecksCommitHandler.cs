using CodebaseGuardian.Git;
using CodebaseGuardian.Watching;

namespace CodebaseGuardian.Checks;

/// <summary>Runs the checks once per batch of new commits, for the newest one. Registered only with <c>Guardian:AutoChecks</c>.</summary>
public sealed class AutoChecksCommitHandler(ICheckRunner runner, ICheckCommandResolver resolver) : IRepositoryChangeHandler
{
    public async Task OnNewCommitsAsync(IReadOnlyList<CommitInfo> commits, CancellationToken cancellationToken)
    {
        if (commits.Count == 0 || resolver.Resolve() is null)
        {
            return;
        }

        var newest = commits.OrderBy(c => c.CommittedAt).Last();
        await runner.RunAsync("commit", newest.Sha, null, cancellationToken);
    }
}
