using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.Events;

internal sealed class EventsConfigureOptions(
    IOptions<EventsOptions> eventsOptions,
    IEventLog log,
    WebhookSubscribeHandler webhooks,
    TimeProvider time,
    ILogger<EventsConfigureOptions>? logger = null,
    IHostApplicationLifetime? lifetime = null)
    : IConfigureOptions<McpServerOptions>
{
    private readonly EventsOptions _options = eventsOptions.Value;
    private readonly ILogger _logger = logger ?? NullLogger<EventsConfigureOptions>.Instance;

    public void Configure(McpServerOptions options)
    {
        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Experimental ??= new Dictionary<string, object>();
        options.Capabilities.Experimental[EventsProtocol.CapabilityKey] = new JsonObject { ["listChanged"] = false };

        options.RequestHandlers ??= [];
        options.RequestHandlers.Add(new McpServerRequestHandler { Method = EventsProtocol.ListMethod, Handler = HandleList });
        options.RequestHandlers.Add(new McpServerRequestHandler { Method = EventsProtocol.PollMethod, Handler = HandlePoll });
        var stream = new EventStreamHandler(_options, log, time, lifetime, _logger);
        options.RequestHandlers.Add(new McpServerRequestHandler { Method = EventsProtocol.StreamMethod, Handler = stream.HandleAsync });
        options.RequestHandlers.Add(new McpServerRequestHandler { Method = EventsProtocol.SubscribeMethod, Handler = webhooks.SubscribeAsync });
        options.RequestHandlers.Add(new McpServerRequestHandler { Method = EventsProtocol.UnsubscribeMethod, Handler = webhooks.UnsubscribeAsync });
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
            parsed.MaxEvents ?? EventsRequestParser.ClampMaxEvents(_options, _options.DefaultMaxEvents));

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
            _logger.LogError(ex, "Reading events for '{EventName}' failed.", parsed.Definition.Name);
            throw new McpProtocolException($"Event '{parsed.Definition.Name}' could not be read.", McpErrorCode.InternalError);
        }

        return ValueTask.FromResult<JsonNode?>(EventJson.PollResult(read, _options.PollInterval));
    }
}
