using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodebaseGuardian.Json;
using Mcp.Events;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace CodebaseGuardian.Tools;

public sealed record PolledEvent(
    string EventId,
    string Name,
    [property: JsonConverter(typeof(UtcTimestampJsonConverter))] DateTimeOffset Timestamp,
    JsonObject Data);

public sealed record PollEventsResult(IReadOnlyList<PolledEvent> Events, string Cursor, bool Truncated, bool HasMore);

[McpServerToolType]
public sealed class EventTools(IEventLog log, IOptions<EventsOptions> options)
{
    [McpServerTool(Name = "poll_events", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Use when your client cannot subscribe to MCP events; pass the returned cursor next time. Returns Guardian events (new commits, branch switches, file edits, dependency changes, check results, secret findings). Without a cursor it returns the newest events so you can see recent activity; with the cursor from the previous call it returns everything that happened since.")]
    public Task<PollEventsResult> PollEvents(
        [Description("Cursor returned by the previous call. Omit it on the first call.")] string? cursor = null,
        [Description("Only return these event names, for example ['repo.commit.created']. Omit for all events.")] string[]? names = null,
        [Description("Maximum number of events (default 50; capped by the server).")] int maxEvents = 50) =>
        ToolErrors.RunAsync(() =>
        {
            var eventOptions = options.Value;
            var requested = names is { Length: > 0 } ? names : null;
            if (requested is not null)
            {
                var known = eventOptions.Definitions.Select(d => d.Name).ToHashSet();
                var unknown = requested.Where(n => !known.Contains(n)).ToList();
                if (unknown.Count > 0)
                {
                    throw new ArgumentException(
                        $"Unknown event name(s): {string.Join(", ", unknown)}. Known events: {string.Join(", ", known.Order())}.");
                }
            }

            EventReadResult result;
            try
            {
                result = log.Read(new EventQuery(requested, null, cursor ?? log.OldestCursor, null, Math.Clamp(maxEvents, 1, eventOptions.MaxEventsLimit))
                {
                    NewestFirst = cursor is null,
                });
            }
            catch (InvalidCursorException)
            {
                throw new ArgumentException("`cursor` is not a valid cursor; omit it to start from the newest events.");
            }

            var events = result.Events.Select(e => new PolledEvent(e.EventId, e.Name, e.Timestamp, e.Data)).ToList();
            return Task.FromResult(new PollEventsResult(events, result.Cursor, result.Truncated, result.HasMore));
        });
}
