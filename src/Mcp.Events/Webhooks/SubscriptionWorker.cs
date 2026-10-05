using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Mcp.Events;

/// <summary>
/// Delivers events to one subscription, in order, with bounded retries (wire-format B8, B9, B11). The watermark
/// (<see cref="WebhookSubscription.Position"/>) advances only past events that were acked or abandoned. Delivery never
/// runs under <see cref="WebhookSubscriptionStore.SyncRoot"/>; the lock is taken only to read or change fields.
/// </summary>
internal sealed class SubscriptionWorker
{
    private const int BatchSize = 100;
    private const int MaxBodyBytes = 256 * 1024;
    private static readonly TimeSpan ReadFailureBackoff = TimeSpan.FromSeconds(5);

    private enum Outcome { Acked, Abandoned, Interrupted }

    private readonly WebhookSubscriptionStore _store;
    private readonly IEventLog _log;
    private readonly IWebhookSender _sender;
    private readonly WebhookOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly DeliveryStats _stats;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _wakeGate = new();
    private TaskCompletionSource _wake = NewWake();
    private int _statsGeneration;

    public SubscriptionWorker(WebhookSubscription subscription, WebhookSubscriptionStore store, IEventLog log, IWebhookSender sender,
        WebhookOptions options, TimeProvider time, ILogger logger)
    {
        Subscription = subscription;
        _store = store;
        _log = log;
        _sender = sender;
        _options = options;
        _time = time;
        _logger = logger;
        _stats = new DeliveryStats(options, time);
    }

    public WebhookSubscription Subscription { get; }

    /// <summary>Tells the worker the subscription was refreshed.</summary>
    public void Signal()
    {
        lock (_wakeGate) _wake.TrySetResult();
    }

    public void Stop()
    {
        _stop.Cancel();
    }

    public async Task RunAsync(CancellationToken hostStopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostStopping, _stop.Token);
        try
        {
            await LoopAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Stopped: removed, replaced or the host is shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook worker for subscription {SubscriptionId} failed and stopped.", Subscription.Id);
        }
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task LoopAsync(CancellationToken ct)
    {
        var sub = Subscription;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task wake;
            lock (_wakeGate)
            {
                // Reset before reading state: a refresh that already changed state has signalled and is consumed here.
                if (_wake.Task.IsCompleted) _wake = NewWake();
                wake = _wake.Task;
            }

            if (!_store.TryGet(sub.Id, out var current) || !ReferenceEquals(current, sub))
            {
                return; // removed or replaced
            }

            bool active;
            long position;
            DateTimeOffset refreshBefore;
            lock (_store.SyncRoot)
            {
                active = sub.Active;
                position = sub.Position;
                refreshBefore = sub.RefreshBefore;
            }

            var now = _time.GetUtcNow();
            if (now >= refreshBefore)
            {
                _store.TryRemoveIfExpired(sub, now); // lapsed; no envelope
                return;
            }

            if (!active)
            {
                await WaitAsync(null, wake, refreshBefore - now, ct).ConfigureAwait(false);
                continue;
            }

            EventReadResult read;
            try
            {
                read = _log.Read(new EventQuery([sub.Name], sub.Arguments, _log.CursorAt(position), null, BatchSize));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Reading events for webhook subscription {SubscriptionId} failed; retrying.", sub.Id);
                await Task.Delay(ReadFailureBackoff, _time, ct).ConfigureAwait(false);
                continue;
            }

            if (read.Truncated)
            {
                // Events were lost: tell the endpoint where servable events start and continue from there.
                string gapCursor;
                long servableStart;
                if (read.Events.Count > 0)
                {
                    servableStart = read.Events[0].Sequence - 1;
                    gapCursor = _log.CursorAt(servableStart);
                }
                else
                {
                    gapCursor = read.Cursor;
                    servableStart = _log.GetSequence(read.Cursor);
                }

                var gap = new JsonObject { ["type"] = "gap", ["cursor"] = gapCursor };
                var gapId = "msg_gap_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
                if (await DeliverAsync(gapId, Encoding.UTF8.GetBytes(gap.ToJsonString()), ct).ConfigureAwait(false) == Outcome.Interrupted)
                {
                    continue; // suspended or lapsed: the gap is reported again once delivery resumes
                }

                Advance(servableStart);
            }

            foreach (var envelope in read.Events)
            {
                if (envelope.Sequence <= CurrentPosition())
                {
                    continue; // a refresh with a cursor already moved past it
                }

                var body = Encoding.UTF8.GetBytes(Body(envelope).ToJsonString());
                Outcome outcome;
                if (body.Length > MaxBodyBytes)
                {
                    _logger.LogWarning("Event {EventId} exceeds {Limit} bytes as a webhook body and is skipped for subscription {SubscriptionId}.",
                        envelope.EventId, MaxBodyBytes, sub.Id);
                    outcome = Outcome.Abandoned;
                }
                else
                {
                    outcome = await DeliverAsync(envelope.EventId, body, ct).ConfigureAwait(false);
                }

                if (outcome == Outcome.Interrupted)
                {
                    break; // suspended or lapsed: the event stays pending; the loop top decides
                }

                Advance(envelope.Sequence);
            }

            if (read.HasMore)
            {
                continue;
            }

            if (read.Events.Count == 0)
            {
                // Nothing matched up to the head of the log; move past it so waiting does not return immediately.
                Advance(_log.GetSequence(read.Cursor));
                await WaitAsync(_log, wake, refreshBefore - _time.GetUtcNow(), ct).ConfigureAwait(false);
            }
        }
    }

    private JsonObject Body(EventEnvelope envelope)
    {
        var body = EventJson.Envelope(envelope);
        body["cursor"] = _log.CursorAt(envelope.Sequence);
        return body;
    }

    private long CurrentPosition()
    {
        lock (_store.SyncRoot) return Subscription.Position;
    }

    private void Advance(long sequence)
    {
        lock (_store.SyncRoot)
        {
            if (sequence > Subscription.Position) Subscription.Position = sequence;
        }
    }

    /// <summary>Waits for new events (when <paramref name="log"/> is given), a refresh signal, or expiry.</summary>
    private async Task WaitAsync(IEventLog? log, Task wake, TimeSpan untilExpiry, CancellationToken ct)
    {
        if (untilExpiry <= TimeSpan.Zero) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var expiry = Task.Delay(untilExpiry, _time, cts.Token);
        var events = log is null ? Task.Delay(Timeout.InfiniteTimeSpan, cts.Token) : log.WaitForEventsAfterAsync(CurrentPosition(), cts.Token);
        await Task.WhenAny(events, wake, expiry).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        // Observe the losers so nothing is left unobserved.
        foreach (var task in new[] { events, expiry })
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: cancelled above.
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Counts an attempt towards suspension, unless a refresh happened since it started (its outcome is stale then).
    /// Returns true when this failure suspended the subscription; <see cref="WebhookSubscription.Active"/> is cleared under
    /// the lock only if no refresh intervened.
    /// </summary>
    private bool RecordAttempt(int generation, bool succeeded)
    {
        lock (_store.SyncRoot)
        {
            var current = Subscription.RefreshGeneration;
            if (_statsGeneration != current)
            {
                _stats.Clear();
                _statsGeneration = current;
            }

            if (generation != current)
            {
                return false;
            }

            _stats.Record(succeeded);
            if (succeeded || !_stats.ShouldSuspend())
            {
                return false;
            }

            Subscription.Active = false;
            return true;
        }
    }

    private async Task<Outcome> DeliverAsync(string messageId, byte[] body, CancellationToken ct)
    {
        var sub = Subscription;
        var first = _time.GetUtcNow();
        for (var attempt = 0; ; attempt++)
        {
            IReadOnlyList<byte[]> keys;
            int generation;
            lock (_store.SyncRoot)
            {
                keys = sub.SigningKeys(_time.GetUtcNow());
                generation = sub.RefreshGeneration;
            }

            var result = await _sender.SendAsync(sub.Url, messageId, body, sub.Id, keys, ct).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            if (result.Delivered)
            {
                lock (_store.SyncRoot)
                {
                    sub.LastDeliveryAt = now;
                    sub.LastError = null;
                    sub.FailedSince = null;
                }

                RecordAttempt(generation, true);
                return Outcome.Acked;
            }

            var category = result.ErrorCategory ?? "http_5xx";
            if (result.StatusCode is 410 or 413)
            {
                // The endpoint refuses this event; retrying cannot help and the subscription is unaffected.
                lock (_store.SyncRoot) sub.LastError = "http_4xx";
                return Outcome.Abandoned;
            }

            lock (_store.SyncRoot)
            {
                sub.LastError = category;
                sub.FailedSince ??= now;
            }

            if (RecordAttempt(generation, false))
            {
                _logger.LogWarning("Webhook subscription {SubscriptionId} suspended after repeated delivery failures.", sub.Id);
                return Outcome.Interrupted; // the event stays pending until a refresh
            }

            DateTimeOffset refreshBefore;
            lock (_store.SyncRoot) refreshBefore = sub.RefreshBefore;
            if (now >= refreshBefore)
            {
                return Outcome.Interrupted;
            }

            if (attempt >= _options.RetryDelays.Count)
            {
                return Outcome.Abandoned;
            }

            var delay = _options.RetryDelays[attempt];
            if (now + delay - first > _options.RetryWindow)
            {
                return Outcome.Abandoned;
            }

            await Task.Delay(delay, _time, ct).ConfigureAwait(false);
        }
    }
}
