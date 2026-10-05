namespace Mcp.Events;

/// <summary>Wire-level constants of the draft MCP Events extension.</summary>
public static class EventsProtocol
{
    public const string ListMethod = "events/list", PollMethod = "events/poll", StreamMethod = "events/stream",
                        SubscribeMethod = "events/subscribe", UnsubscribeMethod = "events/unsubscribe";
    public const string ActiveNotification = "notifications/events/active", EventNotification = "notifications/events/event",
                        HeartbeatNotification = "notifications/events/heartbeat", ErrorNotification = "notifications/events/error",
                        TerminatedNotification = "notifications/events/terminated";
    public const string SubscriptionIdMetaKey = "io.modelcontextprotocol/subscriptionId";
    /// <summary>Emitted under <c>capabilities.experimental</c> (spec ruling R1).</summary>
    public const string CapabilityKey = "events";
    public const int NotFound = -32011, Forbidden = -32012, ResourceExhausted = -32013, Unsupported = -32014, CallbackEndpointError = -32015; // ruling R2
}
