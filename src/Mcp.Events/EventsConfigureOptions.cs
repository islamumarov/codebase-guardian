using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.Events;

internal sealed class EventsConfigureOptions(IOptions<EventsOptions> eventsOptions, IEventLog log)
    : IConfigureOptions<McpServerOptions>
{
    private readonly EventsOptions _options = eventsOptions.Value;

    public void Configure(McpServerOptions options)
    {
        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Experimental ??= new Dictionary<string, object>();
        options.Capabilities.Experimental[EventsProtocol.CapabilityKey] = new JsonObject { ["listChanged"] = false };

        options.RequestHandlers ??= [];
        options.RequestHandlers.Add(new McpServerRequestHandler { Method = EventsProtocol.ListMethod, Handler = HandleList });
        options.RequestHandlers.Add(new McpServerRequestHandler { Method = EventsProtocol.PollMethod, Handler = HandlePoll });
    }

    private ValueTask<JsonNode?> HandleList(JsonRpcRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult<JsonNode?>(EventJson.ListResult(_options));

    private ValueTask<JsonNode?> HandlePoll(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        var parsed = EventsRequestParser.Parse(request.Params as JsonObject, _options, log, EventDelivery.Poll);
        var query = new EventQuery(
            [parsed.Definition.Name],
            parsed.Arguments,
            parsed.Cursor,
            parsed.MaxAge,
            parsed.MaxEvents ?? Math.Clamp(_options.DefaultMaxEvents, 1, Math.Max(1, _options.MaxEventsLimit)));

        EventReadResult read;
        try
        {
            read = log.Read(query);
        }
        catch (InvalidCursorException)
        {
            throw new McpProtocolException("params.cursor is not a valid cursor.", McpErrorCode.InvalidParams);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A Matches predicate is application code; its failure is a server fault whose detail must not reach the client.
            throw new McpProtocolException($"Event '{parsed.Definition.Name}' could not be read.", McpErrorCode.InternalError);
        }

        return ValueTask.FromResult<JsonNode?>(EventJson.PollResult(read, _options.PollInterval));
    }
}
