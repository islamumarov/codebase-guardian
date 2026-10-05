using System.Globalization;
using System.Text.Json.Nodes;

namespace Mcp.Events;

/// <summary>Builders for the JSON shapes of events/list and events/poll results (wire-format B2, B4, B5).</summary>
internal static class EventJson
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public const string ResultTypeComplete = "complete";
    public static readonly TimeSpan ListTtl = TimeSpan.FromHours(1);

    public static string WireName(EventDelivery mode) => mode switch
    {
        EventDelivery.Poll => "poll",
        EventDelivery.Push => "push",
        EventDelivery.Webhook => "webhook",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static IReadOnlyList<string> DeliveryModes(EventsOptions options) =>
        options.WebhooksEnabled ? ["poll", "push", "webhook"] : ["poll", "push"];

    /// <summary>UTC <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>, invariant culture.</summary>
    public static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    public static JsonObject Descriptor(EventDefinition definition, EventsOptions options)
    {
        var delivery = new JsonArray();
        foreach (var mode in DeliveryModes(options))
        {
            delivery.Add(mode);
        }

        return new JsonObject
        {
            ["name"] = definition.Name,
            ["description"] = definition.Description,
            ["delivery"] = delivery,
            ["inputSchema"] = definition.InputSchema.DeepClone(),
            ["payloadSchema"] = definition.PayloadSchema.DeepClone(),
        };
    }

    public static JsonObject ListResult(EventsOptions options)
    {
        var events = new JsonArray();
        foreach (var definition in options.Definitions.OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            events.Add(Descriptor(definition, options));
        }

        return new JsonObject
        {
            ["resultType"] = ResultTypeComplete,
            ["events"] = events,
            ["ttlMs"] = (long)ListTtl.TotalMilliseconds,
            ["cacheScope"] = "public",
        };
    }

    /// <summary>Poll envelope: no per-event cursor (poll carries the cursor at the response level).</summary>
    public static JsonObject Envelope(EventEnvelope envelope) => new()
    {
        ["eventId"] = envelope.EventId,
        ["name"] = envelope.Name,
        ["timestamp"] = FormatTimestamp(envelope.Timestamp),
        ["data"] = envelope.Data.DeepClone(),
    };

    public static JsonObject PollResult(EventReadResult read, TimeSpan pollInterval)
    {
        var events = new JsonArray();
        foreach (var envelope in read.Events)
        {
            events.Add(Envelope(envelope));
        }

        return new JsonObject
        {
            ["resultType"] = ResultTypeComplete,
            ["events"] = events,
            ["cursor"] = read.Cursor,
            ["truncated"] = read.Truncated,
            ["hasMore"] = read.HasMore,
            ["nextPollMs"] = (long)pollInterval.TotalMilliseconds,
        };
    }
}
