namespace Mcp.Events;

public sealed record EventReadResult(IReadOnlyList<EventEnvelope> Events, string Cursor, bool Truncated, bool HasMore);
