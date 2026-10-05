using System.Threading.Channels;
using CodebaseGuardian.Git;
using CodebaseGuardian.Watching;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodebaseGuardian.Checks;

/// <summary>
/// Runs the checks once per batch of new commits, for the newest one. Registered only with <c>Guardian:AutoChecks</c>.
/// The handler only queues the commit and returns, so a long check never stalls the watcher's poll loop; this service
/// runs the queue in the background. Commits that arrive while a run is in progress collapse into the newest one.
/// </summary>
public sealed class AutoChecksCommitHandler(ICheckRunner runner, ILogger<AutoChecksCommitHandler> logger)
    : BackgroundService, IRepositoryChangeHandler
{
    private readonly Channel<string> _pending = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public Task OnNewCommitsAsync(IReadOnlyList<CommitInfo> commits, CancellationToken cancellationToken)
    {
        if (commits.Count > 0)
        {
            _pending.Writer.TryWrite(commits.OrderBy(c => c.CommittedAt).Last().Sha);
        }

        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var sha in _pending.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await runner.RunAsync("commit", sha, null, stoppingToken);
                }
                catch (NoCheckCommandException)
                {
                    logger.LogDebug("Auto-checks skipped: no check command configured or detected.");
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(exception, "Automatic check run for commit {Sha} failed.", sha);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown: the running check was cancelled with the host.
        }
    }
}
