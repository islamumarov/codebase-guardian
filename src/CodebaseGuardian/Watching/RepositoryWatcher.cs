using System.Globalization;
using System.Text.Json.Nodes;
using CodebaseGuardian.Dependencies;
using CodebaseGuardian.Git;
using CodebaseGuardian.Hosting;
using CodebaseGuardian.Json;
using Mcp.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Watching;

/// <summary>
/// Turns repository activity into events: new commits on any local branch, branch switches, working-tree edits and
/// dependency-manifest changes. Every failure is logged and the loop carries on; the watcher stops with the host.
/// </summary>
public sealed class RepositoryWatcher : BackgroundService
{
    private const int MaxNewCommitsPerBranch = 50;
    private const int MaxAnnouncedShas = 10_000;
    private const int MaxFilesInCommitEvent = 100;
    private const int MaxPathsInFilesEvent = 200;

    private readonly IGitRepository _git;
    private readonly IEventPublisher _publisher;
    private readonly IReadOnlyList<IRepositoryChangeHandler> _handlers;
    private readonly GuardianOptions _options;
    private readonly ILogger<RepositoryWatcher> _logger;

    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private readonly Queue<string> _announcedOrder = new();
    private IReadOnlyDictionary<string, string>? _heads;
    private string? _currentBranch;
    private WorkingTreeWatcher? _workingTree;

    public RepositoryWatcher(
        IGitRepository git,
        IEventPublisher publisher,
        IEnumerable<IRepositoryChangeHandler> handlers,
        IOptions<GuardianOptions> options,
        ILogger<RepositoryWatcher> logger)
    {
        _git = git;
        _publisher = publisher;
        _handlers = [.. handlers];
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Takes the baseline snapshot before the host reports itself started, so a commit made right after start-up is never
    /// mistaken for pre-existing history.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.WatchEnabled)
        {
            await TryTakeSnapshotAsync(cancellationToken);
            StartWorkingTreeWatcher();
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WatchEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.WatchIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (_heads is null)
                    {
                        await TryTakeSnapshotAsync(stoppingToken);
                    }
                    else
                    {
                        await PollAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Repository poll failed; trying again at the next interval.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    public override void Dispose()
    {
        _workingTree?.Dispose();
        base.Dispose();
    }

    private async Task TryTakeSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            var heads = await _git.GetBranchHeadsAsync(cancellationToken);
            _currentBranch = await _git.GetCurrentBranchAsync(cancellationToken);
            foreach (var sha in heads.Values)
            {
                Remember(sha);
            }

            _heads = heads;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Could not read the repository baseline; will retry at the next interval.");
        }
    }

    private void StartWorkingTreeWatcher()
    {
        try
        {
            _workingTree = new WorkingTreeWatcher(
                _git.RootPath, TimeSpan.FromMilliseconds(_options.FileChangeDebounceMs), OnFilesChangedAsync, _logger);
            _workingTree.Start();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not watch the working tree; file-change events are disabled.");
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var previous = _heads!;
        var heads = await _git.GetBranchHeadsAsync(cancellationToken);
        var current = await _git.GetCurrentBranchAsync(cancellationToken);

        if (current != _currentBranch)
        {
            // An unborn repository gaining its first branch is not a switch.
            var fromUnborn = _currentBranch is null && previous.Count == 0;
            if (!fromUnborn)
            {
                await PublishSafelyAsync(GuardianEventNames.RepoBranchChanged, new JsonObject
                {
                    ["from"] = _currentBranch,
                    ["to"] = current,
                    ["headSha"] = current is not null && heads.TryGetValue(current, out var headSha) ? headSha : null,
                }, cancellationToken);
            }

            _currentBranch = current;
        }

        var announced = new List<CommitInfo>();
        var next = new Dictionary<string, string>(heads);
        var changedBranches = heads
            .Where(h => !previous.TryGetValue(h.Key, out var old) || old != h.Value)
            .OrderBy(h => h.Key == current ? 0 : 1)
            .ThenBy(h => h.Key, StringComparer.Ordinal);

        foreach (var (branch, head) in changedBranches)
        {
            try
            {
                var commits = await GetNewCommitsAsync(branch, head, previous, heads, cancellationToken);
                foreach (var commit in commits)
                {
                    if (!Remember(commit.Sha))
                    {
                        continue;
                    }

                    await AnnounceCommitAsync(commit, branch, cancellationToken);
                    announced.Add(commit);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Keep the old head so the branch is examined again at the next interval; announced commits are not repeated.
                _logger.LogError(exception, "Could not examine new commits on branch '{Branch}'.", branch);
                if (previous.TryGetValue(branch, out var old))
                {
                    next[branch] = old;
                }
                else
                {
                    next.Remove(branch);
                }
            }
        }

        _heads = next;

        if (announced.Count > 0)
        {
            await NotifyHandlersAsync(announced, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<CommitInfo>> GetNewCommitsAsync(
        string branch, string head, IReadOnlyDictionary<string, string> previous,
        IReadOnlyDictionary<string, string> heads, CancellationToken cancellationToken)
    {
        try
        {
            return await _git.GetNewCommitsAsync(head, [.. previous.Values.Distinct()], MaxNewCommitsPerBranch, cancellationToken);
        }
        catch (GitException exception)
        {
            // A previous tip may be gone (pruned after a force-push or branch deletion): exclude only the tips that exist now.
            _logger.LogWarning(exception, "Git rejected a previous branch tip; retrying with the current tips of the other branches.");
            var others = heads.Where(h => h.Key != branch).Select(h => h.Value).Distinct().ToList();
            return await _git.GetNewCommitsAsync(head, others, MaxNewCommitsPerBranch, cancellationToken);
        }
    }

    private async Task AnnounceCommitAsync(CommitInfo commit, string branch, CancellationToken cancellationToken)
    {
        DiffSummary? diff = null;
        try
        {
            diff = await _git.GetCommitDiffAsync(commit.Sha, 0, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not read the diff of commit {Sha}; announcing it without statistics.", commit.ShortSha);
        }

        var paths = diff?.Files.Select(f => f.Path).ToList() ?? [];
        await PublishSafelyAsync(GuardianEventNames.RepoCommitCreated, new JsonObject
        {
            ["sha"] = commit.Sha,
            ["shortSha"] = commit.ShortSha,
            ["branch"] = branch,
            ["author"] = new JsonObject { ["name"] = commit.AuthorName, ["email"] = commit.AuthorEmail },
            ["committedAt"] = commit.CommittedAt.UtcDateTime.ToString(UtcTimestampJsonConverter.Format, CultureInfo.InvariantCulture),
            ["subject"] = commit.Subject,
            ["filesChanged"] = paths.Count,
            ["insertions"] = diff?.Insertions ?? 0,
            ["deletions"] = diff?.Deletions ?? 0,
            ["files"] = new JsonArray([.. paths.Take(MaxFilesInCommitEvent).Select(p => (JsonNode?)p)]),
            ["suggestedSkill"] = SuggestedSkills.PrReview,
        }, cancellationToken, eventId: $"commit-{commit.Sha}");

        await PublishDependencyChangeAsync(paths, commit.Sha, cancellationToken);
    }

    private async Task OnFilesChangedAsync(IReadOnlyList<string> paths)
    {
        var cancellationToken = CancellationToken.None;
        await PublishSafelyAsync(GuardianEventNames.RepoFilesChanged, new JsonObject
        {
            ["paths"] = new JsonArray([.. paths.Take(MaxPathsInFilesEvent).Select(p => (JsonNode?)p)]),
            ["count"] = paths.Count,
        }, cancellationToken);

        await PublishDependencyChangeAsync(paths, null, cancellationToken);
    }

    private async Task PublishDependencyChangeAsync(IReadOnlyList<string> paths, string? commitSha, CancellationToken cancellationToken)
    {
        var manifests = paths.Where(DependencyManifests.IsManifest).Order(StringComparer.Ordinal).ToList();
        if (manifests.Count == 0)
        {
            return;
        }

        await PublishSafelyAsync(GuardianEventNames.RepoDependenciesChanged, new JsonObject
        {
            ["manifests"] = new JsonArray([.. manifests.Take(MaxPathsInFilesEvent).Select(p => (JsonNode?)p)]),
            ["ecosystems"] = new JsonArray([.. manifests.Select(m => DependencyManifests.GetEcosystem(m)!).Distinct().Order(StringComparer.Ordinal).Select(e => (JsonNode?)e)]),
            ["commitSha"] = commitSha,
            ["suggestedSkill"] = SuggestedSkills.DependencyHygiene,
        }, cancellationToken);
    }

    private async Task NotifyHandlersAsync(IReadOnlyList<CommitInfo> commits, CancellationToken cancellationToken)
    {
        foreach (var handler in _handlers)
        {
            try
            {
                await handler.OnNewCommitsAsync(commits, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(exception, "Change handler {Handler} failed.", handler.GetType().Name);
            }
        }
    }

    private async Task PublishSafelyAsync(string name, JsonObject data, CancellationToken cancellationToken, string? eventId = null)
    {
        try
        {
            await _publisher.PublishAsync(name, data, eventId, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Publishing {Event} failed.", name);
        }
    }

    /// <summary>Remembers a SHA in the bounded announced set; false when it was already there.</summary>
    private bool Remember(string sha)
    {
        if (!_announced.Add(sha))
        {
            return false;
        }

        _announcedOrder.Enqueue(sha);
        while (_announcedOrder.Count > MaxAnnouncedShas)
        {
            _announced.Remove(_announcedOrder.Dequeue());
        }

        return true;
    }
}
