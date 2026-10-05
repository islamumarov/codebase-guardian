using System.Text.Json.Nodes;

namespace Mcp.Events;

public sealed record EventQuery(IReadOnlyCollection<string>? Names, JsonObject? Arguments, string? Cursor, TimeSpan? MaxAge, int MaxEvents)
{
    /// <summary>Tail mode: when more than MaxEvents match, keep the newest MaxEvents.</summary>
    public bool NewestFirst { get; init; }
}
