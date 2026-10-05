using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mcp.Events;

/// <summary>
/// Keeps one <see cref="SubscriptionWorker"/> per subscription in the store: started when one appears, woken when it is
/// refreshed, stopped when it is removed. The store is the source of truth; a worker never exists for a subscription
/// that is not in it. Workers stop with the host and send nothing on shutdown (wire-format B7).
/// </summary>
internal sealed class WebhookDispatcher(
    IOptions<EventsOptions> options,
    IEventLog log,
    WebhookSubscriptionStore store,
    IWebhookSender sender,
    TimeProvider time,
    ILogger<WebhookDispatcher> logger) : BackgroundService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (SubscriptionWorker Worker, Task Run)> _workers = new(StringComparer.Ordinal);
    private CancellationToken _stopping;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WebhooksEnabled)
        {
            return;
        }

        _stopping = stoppingToken;
        store.Changed += OnChanged;
        try
        {
            foreach (var subscription in store.Snapshot())
            {
                OnChanged(subscription);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
        finally
        {
            store.Changed -= OnChanged;
            List<(SubscriptionWorker Worker, Task Run)> running;
            lock (_gate)
            {
                running = [.. _workers.Values];
                _workers.Clear();
            }

            foreach (var (worker, _) in running)
            {
                worker.Stop();
            }

            await Task.WhenAll(running.Select(r => r.Run)).ConfigureAwait(false);
        }
    }

    private void OnChanged(WebhookSubscription changed)
    {
        // Decide from the store, not from the event: a Changed for a subscription that has since been removed must not
        // start (or keep) a worker.
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            var present = store.TryGet(changed.Id, out var current);
            if (_workers.TryGetValue(changed.Id, out var existing))
            {
                if (present && ReferenceEquals(existing.Worker.Subscription, current))
                {
                    existing.Worker.Signal();
                    return;
                }

                _workers.Remove(changed.Id);
                existing.Worker.Stop();
            }

            if (!present)
            {
                return;
            }

            var worker = new SubscriptionWorker(current!, store, log, sender, options.Value.Webhooks, time, logger);
            _workers[changed.Id] = (worker, RunAsync(worker));
        }
    }

    private async Task RunAsync(SubscriptionWorker worker)
    {
        await Task.Yield(); // lets the caller finish registering the worker before it can end
        try
        {
            await worker.RunAsync(_stopping).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (_workers.TryGetValue(worker.Subscription.Id, out var entry) && ReferenceEquals(entry.Worker, worker))
                {
                    _workers.Remove(worker.Subscription.Id);
                }
            }

            // A subscription that is still stored must keep a worker (unless the last one crashed).
            if (!worker.Faulted && !_stopping.IsCancellationRequested
                && store.TryGet(worker.Subscription.Id, out var stored) && ReferenceEquals(stored, worker.Subscription))
            {
                OnChanged(stored);
            }
        }
    }
}
