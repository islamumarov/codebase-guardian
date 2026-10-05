using System.Text.Json.Nodes;

namespace Mcp.Events;

public sealed class EventDefinition
{
    /// <summary>Dotted lowercase name, <c>^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$</c>.</summary>
    public required string Name { get; init; }
    public required string Description { get; init; }
    public JsonObject InputSchema { get; init; } = new() { ["type"] = "object" };
    public required JsonObject PayloadSchema { get; init; }
    /// <summary>(arguments, data) => deliver? Null means always deliver.</summary>
    public Func<JsonObject?, JsonObject, bool>? Matches { get; init; }
}
