using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mcp.Events;

/// <summary>
/// Bounded in-memory event log. One lock guards all state. Sequences are global, start at 1 and are contiguous
/// among retained events (eviction only removes from the front), so a retained event's list index is
/// <c>sequence - firstSequence</c>.
/// <para>
/// Cursor policy: a cursor from another epoch (restart, other process) or older than the retained window yields
/// <c>Truncated=true</c> and a read from the oldest retained event. A cursor of the current epoch whose sequence is
/// greater than the head cannot have been issued by this log (forged or corrupted); per wire-format B11 that is a gap
/// too: no events, <c>Truncated=true</c> and the head cursor. <see cref="InvalidCursorException"/> is reserved for
/// undecodable cursors.
/// </para>
/// </summary>
public sealed partial class InMemoryEventLog : IEventLog, IEventPublisher
{
    private readonly EventsOptions _options;
    private readonly TimeProvider _time;
    private readonly Guid _epoch = Guid.NewGuid();
    private readonly string _idPrefix;
    private readonly object _gate = new();
    private readonly List<EventEnvelope> _events = [];
    private readonly Dictionary<string, EventEnvelope> _byId = new(StringComparer.Ordinal);
    private long _head;   // sequence of the newest event ever appended (0 = none)
    private TaskCompletionSource _signal = NewSignal();

    public InMemoryEventLog(EventsOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
                ArgumentOutOfRangeException.ThrowIfLessThan(options.Capacity, 1);
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _idPrefix = "evt_" + _epoch.ToString("N")[..8] + "_";
    }

    public string HeadCursor { get { lock (_gate) return EventCursor.Encode(_epoch, _head); } }

    public string OldestCursor
    {
        get { lock (_gate) { Prune(); return EventCursor.Encode(_epoch, OldestSequenceMinusOne()); } }
    }

    public string CursorAfter(EventEnvelope envelope) => EventCursor.Encode(_epoch, envelope.Sequence);

    public string CursorAt(long sequence) => EventCursor.Encode(_epoch, sequence);

    public (long Sequence, bool Truncated) Seek(string? cursor, TimeSpan? maxAge)
    {
        (Guid Epoch, long Sequence)? decoded = cursor is null ? null : EventCursor.Decode(cursor);
        lock (_gate)
        {
            Prune();
            if (decoded is null) return (_head, false);

            var floor = OldestSequenceMinusOne();
            long after;
            var truncated = false;
            if (decoded.Value.Epoch != _epoch)
            {
                truncated = true;
                after = floor;
            }
            else
            {
                after = decoded.Value.Sequence;
                if (after > _head) return (_head, true);
                if (after < floor) { truncated = true; after = floor; }
            }

            if (maxAge is { } age)
            {
                var cutoff = _time.GetUtcNow() - age;
                for (var i = _events.Count - 1; i >= 0 && _events[i].Sequence > after; i--)
                {
                    if (_events[i].Timestamp >= cutoff) continue;
                    after = _events[i].Sequence; // everything up to the newest too-old event is skipped
                    truncated = true;
                    break;
                }
            }

            return (after, truncated);
        }
    }

    /// <summary>
    /// Decodes the sequence only; the epoch is NOT checked. Stream loops must pass a cursor returned by
    /// <see cref="Read"/> (which is epoch-checked and normalised), not a raw client cursor.
    /// </summary>
    public long GetSequence(string cursor) => EventCursor.Decode(cursor).Sequence;

    public ValueTask<EventEnvelope> PublishAsync(string name, JsonObject data, string? eventId = null,
        DateTimeOffset? timestamp = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Definitions.Any(d => d.Name == name))
            throw new ArgumentException($"Event '{name}' is not defined.", nameof(name));
        if (eventId is not null && !EventIdPattern().IsMatch(eventId))
            throw new ArgumentException("eventId must match ^[A-Za-z0-9_-]{1,128}$.", nameof(eventId));
        if (eventId is not null && eventId.StartsWith(_idPrefix, StringComparison.Ordinal))
            throw new ArgumentException($"eventId must not start with the reserved prefix '{_idPrefix}'.", nameof(eventId));

        var clone = (JsonObject)data.DeepClone();
        TaskCompletionSource toComplete;
        EventEnvelope stored;
        lock (_gate)
        {
            Prune();
            if (eventId is not null && _byId.TryGetValue(eventId, out var existing))
                return ValueTask.FromResult(Copy(existing));

            var sequence = _head + 1;
            stored = new EventEnvelope(eventId ?? _idPrefix + sequence, name, timestamp ?? _time.GetUtcNow(), clone, sequence);
            _events.Add(stored);
            _byId[stored.EventId] = stored;
            _head = sequence;
            Prune();
            toComplete = _signal;
            _signal = NewSignal();
        }
        toComplete.TrySetResult();
        return ValueTask.FromResult(Copy(stored));
    }

    public EventReadResult Read(EventQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.MaxEvents, 1);
        // Decode outside the lock; it is pure.
        (Guid Epoch, long Sequence)? decoded = query.Cursor is null ? null : EventCursor.Decode(query.Cursor);

        lock (_gate)
        {
            Prune();
            var headCursor = EventCursor.Encode(_epoch, _head);
            if (decoded is null) return new EventReadResult([], headCursor, false, false);

            var floor = OldestSequenceMinusOne();   // cursors below this have lost events
            var truncated = false;
            long after;
            if (decoded.Value.Epoch != _epoch)
            {
                truncated = true;
                after = floor;
            }
            else
            {
                after = decoded.Value.Sequence;
                if (after > _head) return new EventReadResult([], headCursor, true, false);
                if (after < floor) { truncated = true; after = floor; }
            }

            var cutoff = query.MaxAge is { } age ? _time.GetUtcNow() - age : (DateTimeOffset?)null;
            var matchDef = query.Names is { Count: 1 }
                ? _options.Definitions.FirstOrDefault(d => d.Name == query.Names.First())
                : null;
            var matcher = matchDef?.Matches;

            // Without NewestFirst only the first MaxEvents+1 matches matter (the extra one proves HasMore). Keep scanning
            // past that only while an age-skip could still flip Truncated.
            var canStopEarly = !query.NewestFirst;
            var matches = new List<EventEnvelope>();
            var first = floor + 1;
            for (var i = (int)(after - first) + 1; i < _events.Count; i++)
            {
                var e = _events[i];
                if (cutoff is { } c && e.Timestamp < c) { truncated = true; continue; }
                if (query.Names is not null && !query.Names.Contains(e.Name)) continue;
                if (matcher is not null && !matcher((JsonObject?)query.Arguments?.DeepClone(), (JsonObject)e.Data.DeepClone())) continue;
                matches.Add(e);
                if (canStopEarly && matches.Count > query.MaxEvents && (cutoff is null || truncated)) break;
            }

            IEnumerable<EventEnvelope> page;
            bool hasMore;
            string cursor;
            if (matches.Count <= query.MaxEvents)
            {
                page = matches; hasMore = false; cursor = headCursor;
            }
            else if (query.NewestFirst)
            {
                page = matches.Skip(matches.Count - query.MaxEvents); hasMore = false; cursor = headCursor;
            }
            else
            {
                var taken = matches.Take(query.MaxEvents).ToList();
                page = taken; hasMore = true; cursor = EventCursor.Encode(_epoch, taken[^1].Sequence);
            }
            return new EventReadResult(page.Select(Copy).ToList(), cursor, truncated, hasMore);
        }
    }

    public async Task WaitForEventsAfterAsync(long afterSequence, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task signal;
            lock (_gate)
            {
                if (_head > afterSequence) return;
                signal = _signal.Task;
            }
            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Must hold _gate.
    private long OldestSequenceMinusOne() => _events.Count == 0 ? _head : _events[0].Sequence - 1;

    // Must hold _gate. Drops oldest events beyond Capacity or older than Retention.
    private void Prune()
    {
        var drop = Math.Max(0, _events.Count - _options.Capacity);
        var cutoff = _time.GetUtcNow() - _options.Retention;
        while (drop < _events.Count && _events[drop].Timestamp < cutoff) drop++;
        if (drop == 0) return;
        for (var i = 0; i < drop; i++) _byId.Remove(_events[i].EventId);
        _events.RemoveRange(0, drop);
    }

    private static EventEnvelope Copy(EventEnvelope e) => e with { Data = (JsonObject)e.Data.DeepClone() };

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,128}\z")]
    private static partial Regex EventIdPattern();
}
