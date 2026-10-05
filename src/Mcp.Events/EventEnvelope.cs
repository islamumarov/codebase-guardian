using System.Text.Json.Nodes;

namespace Mcp.Events;

public sealed record EventEnvelope(string EventId, string Name, DateTimeOffset Timestamp, JsonObject Data, long Sequence);
