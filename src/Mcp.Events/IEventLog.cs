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
    /// <summary>Completes when an event with Sequence &gt; afterSequence exists (immediately if one already does).</summary>
    Task WaitForEventsAfterAsync(long afterSequence, CancellationToken cancellationToken);
    /// <summary>Decodes a cursor to its sequence (for stream loops).</summary>
    /// <exception cref="InvalidCursorException">Malformed cursor.</exception>
    long GetSequence(string cursor);
}
