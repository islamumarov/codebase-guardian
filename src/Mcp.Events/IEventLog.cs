namespace Mcp.Events;

public interface IEventLog
{
    /// <summary>Position after the newest event.</summary>
    string HeadCursor { get; }
    /// <summary>Position before the oldest retained event.</summary>
    string OldestCursor { get; }
    /// <exception cref="InvalidCursorException">Malformed cursor.</exception>
    EventReadResult Read(EventQuery query);
    string CursorAfter(EventEnvelope envelope);
    /// <summary>The cursor positioned just after <paramref name="sequence"/>. Pass only sequences this log returned (<see cref="Seek"/>, <see cref="GetSequence"/> of a returned cursor).</summary>
    string CursorAt(long sequence);
    /// <summary>
    /// The sequence from which delivery should start for a client cursor, applying the same truncation rules as
    /// <see cref="Read"/>: a null cursor is (head, false); a foreign-epoch or evicted cursor starts at the oldest retained
    /// event and is truncated; a same-epoch cursor ahead of the head is (head, true); events older than
    /// <paramref name="maxAge"/> are skipped, which is truncation too.
    /// </summary>
    /// <exception cref="InvalidCursorException">Malformed cursor.</exception>
    (long Sequence, bool Truncated) Seek(string? cursor, TimeSpan? maxAge);
    /// <summary>Completes when an event with Sequence &gt; afterSequence exists (immediately if one already does).</summary>
    Task WaitForEventsAfterAsync(long afterSequence, CancellationToken cancellationToken);
    /// <summary>Decodes a cursor to its sequence (for stream loops).</summary>
    /// <exception cref="InvalidCursorException">Malformed cursor.</exception>
    long GetSequence(string cursor);
}
