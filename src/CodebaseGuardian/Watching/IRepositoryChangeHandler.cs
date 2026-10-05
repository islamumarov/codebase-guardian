using CodebaseGuardian.Git;

namespace CodebaseGuardian.Watching;

public interface IRepositoryChangeHandler
{
    /// <summary>
    /// Called by <see cref="RepositoryWatcher"/> after it has published <c>repo.commit.created</c> for these commits (oldest first).
    /// Exceptions are logged by the watcher and never stop it.
    /// </summary>
    Task OnNewCommitsAsync(IReadOnlyList<CommitInfo> commits, CancellationToken cancellationToken);
}
