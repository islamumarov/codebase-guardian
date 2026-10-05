using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.Events;

/// <summary>
/// Serves <c>events/stream</c> (wire-format B6, B11): one long-lived request per subscription. After validation it sends
/// <c>active</c>, replays from the cursor, then delivers live events, sending a heartbeat whenever a heartbeat interval
/// passes without any notification.
/// <para>
/// Position handling: the loop only ever derives a sequence from cursors the log itself returned (<c>Read</c>), never from
/// the client's raw cursor, and always reads "everything after the last position read", so no event is lost or repeated
/// between the replay and live phases.
/// </para>
/// </summary>
internal sealed class EventStreamHandler(
    EventsOptions options,
    IEventLog log,
    TimeProvider time,
    IHostApplicationLifetime? lifetime,
    ILogger logger)
{
    /// <summary>Key in <see cref="JsonRpcMessageContext.Items"/> under which the request-bound server is stashed.</summary>
    internal const string ServerItemKey = "Mcp.Events.Server";

    public async ValueTask<JsonNode?> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        var parsed = EventsRequestParser.Parse(request.Params as JsonObject, options, log, EventDelivery.Push);
        if (request.Context?.Items is not { } items || !items.TryGetValue(ServerItemKey, out var stashed) || stashed is not McpServer server)
        {
            logger.LogError("events/stream could not find the request-bound server; was WithEvents() used to register the handlers?");
            throw new McpProtocolException("Event streams are not available.", McpErrorCode.InternalError);
        }

        var batchSize = EventsRequestParser.ClampMaxEvents(options, options.DefaultMaxEvents);
        var name = parsed.Definition.Name;
        EventQuery Query(string? cursor, TimeSpan? maxAge) => new([name], parsed.Arguments, cursor, maxAge, batchSize);

        // Validation and the first read happen before anything is sent, so failures are plain JSON-RPC errors.
        var first = ReadSafely(Query(parsed.Cursor, parsed.MaxAge), name, invalidCursorIsClientError: true);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime?.ApplicationStopping ?? default);
        var ct = stopping.Token;
        var subscriptionId = SubscriptionId(request.Id);
        var lastSent = time.GetTimestamp();

        async ValueTask SendAsync(string method, JsonObject payload)
        {
            ct.ThrowIfCancellationRequested(); // nothing goes out once the subscription is cancelled
            payload["_meta"] = new JsonObject { [EventsProtocol.SubscriptionIdMetaKey] = subscriptionId.DeepClone() };
            await server.SendMessageAsync(new JsonRpcNotification { Method = method, Params = payload }, ct).ConfigureAwait(false);
            lastSent = time.GetTimestamp();
        }

        async ValueTask DeliverAsync(EventReadResult read)
        {
            foreach (var envelope in read.Events)
            {
                var payload = EventJson.Envelope(envelope);
                payload["cursor"] = log.CursorAfter(envelope);
                await SendAsync(EventsProtocol.EventNotification, payload).ConfigureAwait(false);
            }
        }

        try
        {
            await SendAsync(EventsProtocol.ActiveNotification, new JsonObject
            {
                ["cursor"] = parsed.Cursor is not null && !first.Truncated ? parsed.Cursor : ResumeCursor(first),
                ["truncated"] = first.Truncated,
            }).ConfigureAwait(false);

            var read = first;
            var position = read.Cursor; // always a cursor returned by Read
            while (true)
            {
                await DeliverAsync(read).ConfigureAwait(false);
                position = read.Cursor;
                if (read.HasMore)
                {
                    read = ReadSafely(Query(position, null), name, invalidCursorIsClientError: false);
                    await AnnounceGapAsync(read).ConfigureAwait(false);
                    continue;
                }

                if (await WaitForEventsOrHeartbeatAsync(position).ConfigureAwait(false))
                {
                    read = ReadSafely(Query(position, null), name, invalidCursorIsClientError: false);
                    await AnnounceGapAsync(read).ConfigureAwait(false);
                }
                else
                {
                    read = new EventReadResult([], position, false, false);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Server shutdown: end the stream cleanly. (Client cancellation propagates: no result is sent.)
            return new JsonObject { ["resultType"] = EventJson.ResultTypeComplete, ["_meta"] = new JsonObject() };
        }

        ValueTask AnnounceGapAsync(EventReadResult r) => r.Truncated
            ? SendAsync(EventsProtocol.ActiveNotification, new JsonObject { ["cursor"] = ResumeCursor(r), ["truncated"] = true })
            : ValueTask.CompletedTask;

        // True when new events exist after position; false after sending a heartbeat for a quiet interval.
        async Task<bool> WaitForEventsOrHeartbeatAsync(string position)
        {
            var sequence = log.GetSequence(position);
            while (true)
            {
                var remaining = options.HeartbeatInterval - time.GetElapsedTime(lastSent);
                if (remaining <= TimeSpan.Zero)
                {
                    await SendAsync(EventsProtocol.HeartbeatNotification, new JsonObject { ["cursor"] = position }).ConfigureAwait(false);
                    return false;
                }

                // One waiter per iteration, cancelled and observed afterwards so none accumulate on the log.
                using var round = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var arrival = log.WaitForEventsAfterAsync(sequence, round.Token);
                var timer = Task.Delay(remaining, time, round.Token);
                try
                {
                    await Task.WhenAny(arrival, timer).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (arrival.IsCompletedSuccessfully) return true;
                }
                finally
                {
                    await round.CancelAsync().ConfigureAwait(false);
                    await Task.WhenAll(arrival, timer).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }
    }

    /// <summary>
    /// A servable position to announce when a read starts from the retained window instead of the requested cursor:
    /// just before the first delivered event, or the read's own cursor when it delivered none.
    /// </summary>
    private string ResumeCursor(EventReadResult read) =>
        read.Events.Count == 0
            ? read.Cursor
            : EventCursor.Encode(EventCursor.Decode(read.Cursor).Epoch, read.Events[0].Sequence - 1);

    private EventReadResult ReadSafely(EventQuery query, string eventName, bool invalidCursorIsClientError)
    {
        try
        {
            return log.Read(query);
        }
        catch (InvalidCursorException) when (invalidCursorIsClientError)
        {
            throw new McpProtocolException("params.cursor is not a valid cursor.", McpErrorCode.InvalidParams);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A Matches predicate is application code; its failure is a server fault whose detail must not reach the client.
            logger.LogError(ex, "Reading events for '{EventName}' failed.", eventName);
            throw new McpProtocolException($"Event '{eventName}' could not be read.", McpErrorCode.InternalError);
        }
    }

    /// <summary>The JSON-RPC request id exactly as the client sent it (string or integer).</summary>
    private static JsonNode SubscriptionId(RequestId id) => id.Id switch
    {
        string s => JsonValue.Create(s),
        long l => JsonValue.Create(l),
        _ => JsonValue.Create(id.ToString()),
    };
}
