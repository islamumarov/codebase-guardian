using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace Mcp.Events;

/// <summary>A validated events/poll, events/stream or events/subscribe subscription request.</summary>
/// <param name="MaxEvents">The requested cap clamped to 1..MaxEventsLimit, or null when the request had none.</param>
internal sealed record EventsRequest(EventDefinition Definition, JsonObject? Arguments, string? Cursor, TimeSpan? MaxAge, int? MaxEvents);

/// <summary>Shared parameter parsing and validation for poll, stream and subscribe (wire-format B5, B12).</summary>
internal static class EventsRequestParser
{
    // Keeps "now - maxAge" inside DateTimeOffset's range whatever the client sends.
    private static readonly TimeSpan MaxAgeCeiling = TimeSpan.FromDays(36_500);

    /// <exception cref="McpProtocolException">NotFound, Unsupported or InvalidParams per the ruling R2 codes.</exception>
    public static EventsRequest Parse(JsonObject? parameters, EventsOptions options, IEventLog log, EventDelivery mode)
    {
        if (parameters is null || !TryGetString(parameters, "name", out var name) || name is null)
        {
            throw InvalidParams("params.name must be a string.");
        }

        var definition = options.Definitions.FirstOrDefault(d => d.Name == name)
            ?? throw new McpProtocolException($"Unknown event '{name}'.", (McpErrorCode)EventsProtocol.NotFound)
            {
                Data = { ["kind"] = "event" },
            };

        var wireMode = EventJson.WireName(mode);
        if (!EventJson.DeliveryModes(options).Contains(wireMode))
        {
            throw new McpProtocolException($"Delivery mode '{wireMode}' is not supported.", (McpErrorCode)EventsProtocol.Unsupported)
            {
                Data = { ["feature"] = "deliveryMode", ["value"] = wireMode },
            };
        }

        JsonObject? arguments = null;
        if (parameters.TryGetPropertyValue("arguments", out var argumentsNode) && argumentsNode is not null)
        {
            arguments = argumentsNode as JsonObject ?? throw InvalidParams("params.arguments must be an object.");
        }

        if (ArgumentValidator.Validate(definition.InputSchema, arguments) is { } problem)
        {
            throw InvalidParams(problem);
        }

        string? cursor = null;
        if (parameters.TryGetPropertyValue("cursor", out var cursorNode) && cursorNode is not null)
        {
            cursor = cursorNode is JsonValue cv && cv.GetValueKind() == JsonValueKind.String && cv.TryGetValue<string>(out var c)
                ? c
                : throw InvalidParams("params.cursor must be a string or null.");
            try
            {
                log.GetSequence(cursor); // decodability only; epoch and range are the log's business
            }
            catch (InvalidCursorException)
            {
                throw InvalidParams("params.cursor is not a valid cursor.");
            }
        }

        TimeSpan? maxAge = null;
        if (parameters.TryGetPropertyValue("maxAgeMs", out var ageNode) && ageNode is not null)
        {
            var ms = ReadNumber(ageNode, "maxAgeMs");
            if (ms < 0)
            {
                throw InvalidParams("params.maxAgeMs must be >= 0.");
            }

            maxAge = ms >= MaxAgeCeiling.TotalMilliseconds ? MaxAgeCeiling : TimeSpan.FromMilliseconds(ms);
        }

        int? maxEvents = null;
        if (parameters.TryGetPropertyValue("maxEvents", out var maxNode) && maxNode is not null)
        {
            var requested = ReadNumber(maxNode, "maxEvents");
            if (requested != Math.Floor(requested))
            {
                throw InvalidParams("params.maxEvents must be an integer.");
            }

            maxEvents = ClampMaxEvents(options, requested);
        }

        return new EventsRequest(definition, arguments, cursor, maxAge, maxEvents);
    }

    /// <summary>Clamps a requested (or default) event count to 1..MaxEventsLimit.</summary>
    public static int ClampMaxEvents(EventsOptions options, double requested) =>
        (int)Math.Clamp(requested, 1, Math.Max(1, options.MaxEventsLimit));

    private static bool TryGetString(JsonObject obj, string key, out string? value)
    {
        value = null;
        return obj.TryGetPropertyValue(key, out var node)
            && node is JsonValue v
            && v.GetValueKind() == JsonValueKind.String
            && v.TryGetValue(out value);
    }

    private static double ReadNumber(JsonNode node, string key) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d) && double.IsFinite(d)
            ? d
            : throw InvalidParams($"params.{key} must be a number.");

    private static McpProtocolException InvalidParams(string message) => new(message, McpErrorCode.InvalidParams);
}
