using System.Text.Json.Nodes;

namespace Mcp.Events;

public interface IEventPublisher
{
    /// <summary>
    /// Validates the name is defined; <paramref name="eventId"/> (optional, <c>^[A-Za-z0-9_-]{1,128}$</c>, so no '.')
    /// overrides the generated id; a duplicate eventId still retained returns the existing envelope without appending.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown event name or invalid eventId.</exception>
    ValueTask<EventEnvelope> PublishAsync(string name, JsonObject data, string? eventId = null,
        DateTimeOffset? timestamp = null, CancellationToken cancellationToken = default);
}
