using System.Text.Json.Nodes;
using CodebaseGuardian.Security;
using CodebaseGuardian.Watching;
using Mcp.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.GitHub;

/// <summary>
/// Polls the GitHub API and publishes <c>github.issue.opened</c>, <c>github.pr.comment.created</c> and
/// <c>github.ci.failed</c>. Only things created after the first cycle are published (no backfill), relayed text is
/// redacted and capped, and published ids are remembered so overlapping query windows never duplicate an event.
/// </summary>
public sealed class GitHubEventPoller(
    IGitHubClient client,
    IEventPublisher publisher,
    ISecretScanner scanner,
    IOptions<GitHubOptions> options,
    TimeProvider timeProvider,
    ILogger<GitHubEventPoller> logger) : BackgroundService
{
    internal const int MaxBodyLength = 2000;
    internal const int MaxRememberedIds = 10_000;
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    // A re-run keeps the created_at of its first attempt, and the runs API can only filter by creation time, so
    // runs are queried further back. UpdatedAt >= startedAt and the published-id set keep that from republishing.
    internal static readonly TimeSpan RunLookback = TimeSpan.FromHours(24);

    private readonly HashSet<string> _published = [];
    private readonly Queue<string> _publishedOrder = new();
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastCycleStart;
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;
    private bool _warnedUnavailable;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnceAsync(stoppingToken);
            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One polling cycle. Failures are logged and absorbed; only cancellation propagates.</summary>
    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now < _pausedUntil)
        {
            return;
        }

        var startedAt = _startedAt ??= now;
        var since = _lastCycleStart is { } last && last - Overlap > startedAt ? last - Overlap : startedAt;
        try
        {
            await PublishIssuesAsync(since, startedAt, cancellationToken);
            await PublishCommentsAsync(since, startedAt, cancellationToken);
            await PublishRunsAsync(since < now - RunLookback ? since : now - RunLookback, startedAt, cancellationToken);
            _lastCycleStart = now;
            _warnedUnavailable = false;
        }
        catch (GitHubRateLimitException exception)
        {
            _pausedUntil = exception.ResetAt;
            logger.LogWarning("GitHub rate limit reached; polling resumes at {ResetAt:O}.", exception.ResetAt);
        }
        catch (GitHubUnavailableException exception)
        {
            if (!_warnedUnavailable)
            {
                _warnedUnavailable = true;
                logger.LogWarning("GitHub events are idle: {Reason}", exception.Message);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "GitHub polling cycle failed; the next cycle retries.");
        }
    }

    private async Task PublishIssuesAsync(DateTimeOffset since, DateTimeOffset startedAt, CancellationToken ct)
    {
        foreach (var issue in await client.ListIssuesSinceAsync(since, ct))
        {
            if (issue.IsPullRequest || issue.CreatedAt < startedAt)
            {
                continue;
            }

            var data = new JsonObject
            {
                ["number"] = issue.Number,
                ["title"] = Relay(issue.Title, MaxBodyLength),
                ["author"] = issue.User.Login,
                ["url"] = issue.HtmlUrl,
                ["labels"] = new JsonArray([.. issue.Labels.Select(label => (JsonNode?)label)]),
                ["body"] = Relay(issue.Body, MaxBodyLength),
                ["suggestedSkill"] = SuggestedSkills.BugTriage,
            };
            await PublishOnceAsync(GuardianEventNames.GithubIssueOpened, $"gh_issue_{issue.Id}", data, issue.CreatedAt, ct);
        }
    }

    private async Task PublishCommentsAsync(DateTimeOffset since, DateTimeOffset startedAt, CancellationToken ct)
    {
        var comments = (await client.ListIssueCommentsSinceAsync(since, ct)).Concat(await client.ListReviewCommentsSinceAsync(since, ct));
        foreach (var comment in comments)
        {
            if (!comment.OnPullRequest || comment.CreatedAt < startedAt)
            {
                continue;
            }

            var data = new JsonObject
            {
                ["prNumber"] = comment.IssueNumber,
                ["commentId"] = comment.Id,
                ["author"] = comment.User.Login,
                ["body"] = Relay(comment.Body, MaxBodyLength),
                ["url"] = comment.HtmlUrl,
                ["path"] = comment.Path,
                ["line"] = comment.Line,
                ["suggestedSkill"] = SuggestedSkills.PrReview,
            };
            var prefix = comment.Kind == "review" ? "gh_reviewcomment_" : "gh_comment_";
            await PublishOnceAsync(GuardianEventNames.GithubPrCommentCreated, prefix + comment.Id, data, comment.CreatedAt, ct);
        }
    }

    private async Task PublishRunsAsync(DateTimeOffset since, DateTimeOffset startedAt, CancellationToken ct)
    {
        foreach (var run in await client.ListFailedWorkflowRunsSinceAsync(since, ct))
        {
            if (run.Conclusion != "failure" || run.UpdatedAt < startedAt)
            {
                continue;
            }

            var data = new JsonObject
            {
                ["runId"] = run.Id,
                ["workflowName"] = run.Name,
                ["branch"] = run.HeadBranch,
                ["headSha"] = run.HeadSha,
                ["url"] = run.HtmlUrl,
                ["conclusion"] = run.Conclusion,
                ["suggestedSkill"] = SuggestedSkills.BugTriage,
            };
            await PublishOnceAsync(GuardianEventNames.GithubCiFailed, $"gh_run_{run.Id}_{run.RunAttempt}", data, run.UpdatedAt, ct);
        }
    }

    private async Task PublishOnceAsync(string name, string eventId, JsonObject data, DateTimeOffset timestamp, CancellationToken ct)
    {
        if (_published.Contains(eventId))
        {
            return;
        }

        await publisher.PublishAsync(name, data, eventId, timestamp, ct);
        _published.Add(eventId);
        _publishedOrder.Enqueue(eventId);
        while (_publishedOrder.Count > MaxRememberedIds)
        {
            _published.Remove(_publishedOrder.Dequeue());
        }
    }

    /// <summary>Redacts secrets first, then cuts to <paramref name="maxLength"/> characters (the last one is an ellipsis when cut).</summary>
    private string Relay(string? text, int maxLength)
    {
        var redacted = scanner.RedactSecrets(text ?? string.Empty);
        if (redacted.Length <= maxLength)
        {
            return redacted;
        }

        var keep = maxLength - 1;
        if (char.IsHighSurrogate(redacted[keep - 1]))
        {
            keep--;
        }

        return redacted[..keep] + "…";
    }
}
