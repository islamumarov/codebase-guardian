using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text.Json.Nodes;
using CodebaseGuardian.Tests.Infrastructure;
using Mcp.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodebaseGuardian.Tests.Events;

public sealed class EventsStreamProtocolTests
{
    private const string SubscriptionIdKey = "io.modelcontextprotocol/subscriptionId";
    private static readonly string[] NotificationMethods =
    [
        "notifications/events/active", "notifications/events/event", "notifications/events/heartbeat",
    ];
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static Task<InProcessMcpServer> StartAsync(Action<EventsOptions>? tweak = null) =>
        InProcessMcpServer.StartAsync(
            (_, _, builder) => builder.WithEvents(o =>
            {
                o.HeartbeatInterval = TimeSpan.FromMilliseconds(200);
                o.Define(new EventDefinition
                {
                    Name = "test.alpha",
                    Description = "Alpha event",
                    InputSchema = Obj("""{"type":"object","properties":{"branch":{"type":"string"}}}"""),
                    PayloadSchema = Obj("""{"type":"object"}"""),
                    Matches = (args, data) => args is null || !args.ContainsKey("branch") || (string?)args["branch"] == (string?)data["branch"],
                });
                o.Define(new EventDefinition { Name = "test.beta", Description = "Beta event", PayloadSchema = Obj("""{"type":"object"}""") });
                tweak?.Invoke(o);
            }),
            cancellationToken: Ct);

    private static Task Publish(InProcessMcpServer server, string name, JsonObject data) =>
        server.Services.GetRequiredService<IEventPublisher>().PublishAsync(name, data, cancellationToken: Ct).AsTask();

    private static Task<JsonObject> PollAsync(InProcessMcpServer server, JsonObject p) => server.RequestAsync("events/poll", p, Ct);

    /// <summary>
    /// Event notifications ordered by their cursor. The SDK client runs notification handlers concurrently, so client-side
    /// arrival order is not wire order; the wire-order test below covers that against raw output.
    /// </summary>
    private static List<Note> Events(IEnumerable<Note> notes) =>
        notes.Where(n => n.Method == "notifications/events/event")
            .OrderBy(n => EventCursor.Decode((string)n.Params["cursor"]!).Sequence)
            .ToList();

    private static Note Active(IEnumerable<Note> notes) => Assert.Single(notes, n => n.Method == "notifications/events/active");

    private sealed record Note(string Method, JsonObject Params)
    {
        public string? SubscriptionId => Params["_meta"]?[SubscriptionIdKey]?.ToString();
    }

    /// <summary>Collects stream notifications in arrival order across all subscriptions.</summary>
    private sealed class Collector : IAsyncDisposable
    {
        private readonly ConcurrentQueue<Note> _notes = new();
        private readonly List<IAsyncDisposable> _registrations = [];

        public Collector(McpClient client)
        {
            foreach (var method in NotificationMethods)
            {
                _registrations.Add(client.RegisterNotificationHandler(method, (n, _) =>
                {
                    _notes.Enqueue(new Note(method, (JsonObject)n.Params!.DeepClone()));
                    return ValueTask.CompletedTask;
                }));
            }
        }

        public IReadOnlyList<Note> For(string id) => _notes.Where(n => n.SubscriptionId == id).ToList();

        public async Task<IReadOnlyList<Note>> WaitAsync(string id, Func<IReadOnlyList<Note>, bool> condition)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(Generous);
            while (true)
            {
                var snapshot = For(id);
                if (condition(snapshot)) return snapshot;
                await Task.Delay(10, timeout.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var r in _registrations) await r.DisposeAsync();
        }
    }

    private sealed class Stream(InProcessMcpServer server, string id, Task<JsonRpcResponse> response)
    {
        public Task<JsonRpcResponse> Response => response;

        public async Task CancelAsync() =>
            await server.Client.SendNotificationAsync(
                "notifications/cancelled",
                new CancelledNotificationParams { RequestId = new RequestId(id) },
                cancellationToken: Ct);
    }

    private static Stream OpenStream(InProcessMcpServer server, string id, JsonObject p) =>
        new(server, id, server.Client.SendRequestAsync(
            new JsonRpcRequest { Id = new RequestId(id), Method = "events/stream", Params = p }, Ct));

    private static async Task AssertEndsAsync(Task task)
    {
        var done = await Task.WhenAny(task, Task.Delay(Generous, Ct));
        Assert.Same(task, done);
        await Assert.ThrowsAnyAsync<Exception>(() => task);
    }

    [Fact]
    public async Task First_notification_is_active_with_truncated_false_and_the_subscription_id()
    {
        await using var server = await StartAsync();
        await using var notes = new Collector(server.Client);
        var stream = OpenStream(server, "s-1", Obj("""{"name":"test.alpha"}"""));

        var seen = await notes.WaitAsync("s-1", n => n.Count >= 1);
        var active = Active(seen);
        Assert.False((bool)active.Params["truncated"]!);
        Assert.False(string.IsNullOrEmpty((string?)active.Params["cursor"]));
        Assert.Equal("String", active.Params["_meta"]![SubscriptionIdKey]!.GetValueKind().ToString());

        await stream.CancelAsync();
        await AssertEndsAsync(stream.Response);
    }

    [Fact]
    public async Task Numeric_request_ids_are_echoed_as_numbers()
    {
        await using var server = await StartAsync();
        await using var notes = new Collector(server.Client);
        var request = new JsonRpcRequest { Id = new RequestId(42), Method = "events/stream", Params = Obj("""{"name":"test.alpha"}""") };
        var response = server.Client.SendRequestAsync(request, Ct);

        var seen = await notes.WaitAsync("42", n => n.Count >= 1);
        Assert.Equal("Number", Active(seen).Params["_meta"]![SubscriptionIdKey]!.GetValueKind().ToString());

        await server.Client.SendNotificationAsync("notifications/cancelled", new CancelledNotificationParams { RequestId = new RequestId(42) }, cancellationToken: Ct);
        await AssertEndsAsync(response);
    }

    [Fact]
    public async Task Published_events_arrive_in_order_with_distinct_cursors_and_nothing_is_left_to_poll()
    {
        await using var server = await StartAsync();
        await using var notes = new Collector(server.Client);
        var stream = OpenStream(server, "s-1", Obj("""{"name":"test.alpha"}"""));
        await notes.WaitAsync("s-1", n => n.Count >= 1);

        for (var i = 1; i <= 3; i++) await Publish(server, "test.alpha", Obj($$"""{"n":{{i}}}"""));

        var seen = await notes.WaitAsync("s-1", n => n.Count(x => x.Method == "notifications/events/event") >= 3);
        var events = Events(seen);
        Assert.Equal([1, 2, 3], events.Select(e => (int)e.Params["data"]!["n"]!).ToList());
        Assert.Equal(3, events.Select(e => (string)e.Params["cursor"]!).Distinct().Count());
        Assert.All(events, e =>
        {
            Assert.Equal("test.alpha", (string?)e.Params["name"]);
            Assert.False(string.IsNullOrEmpty((string?)e.Params["eventId"]));
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", (string)e.Params["timestamp"]!);
        });

        var poll = await PollAsync(server, Obj($$"""{"name":"test.alpha","cursor":"{{(string)events[^1].Params["cursor"]!}}"}"""));
        Assert.Empty(poll["events"]!.AsArray());

        await stream.CancelAsync();
        await AssertEndsAsync(stream.Response);
    }

    [Fact]
    public async Task A_quiet_stream_sends_heartbeats_carrying_the_current_position()
    {
        await using var server = await StartAsync();
        await using var notes = new Collector(server.Client);
        var stream = OpenStream(server, "s-1", Obj("""{"name":"test.alpha"}"""));
        await notes.WaitAsync("s-1", n => n.Count >= 1);
        await Publish(server, "test.alpha", Obj("""{"n":1}"""));
        var quietSince = System.Diagnostics.Stopwatch.StartNew();

        // A heartbeat for the pre-event position may legitimately arrive first; the one that matters carries the event's cursor.
        var seen = await notes.WaitAsync("s-1", n =>
        {
            var events = Events(n);
            return events.Count == 1 && n.Any(x => x.Method == "notifications/events/heartbeat"
                && EventCursor.Decode((string)x.Params["cursor"]!).Sequence >= EventCursor.Decode((string)events[0].Params["cursor"]!).Sequence);
        });
        quietSince.Stop();
        Assert.True(quietSince.Elapsed < TimeSpan.FromSeconds(1), $"heartbeat took {quietSince.Elapsed}");
        var eventCursor = (string)Events(seen)[0].Params["cursor"]!;
        Assert.Contains(seen, x => x.Method == "notifications/events/heartbeat" && (string?)x.Params["cursor"] == eventCursor);

        await stream.CancelAsync();
        await AssertEndsAsync(stream.Response);
    }

    [Fact]
    public async Task A_cursor_from_before_the_events_replays_them_before_live_events()
    {
        await using var server = await StartAsync();
        await using var notes = new Collector(server.Client);
        var start = (string)(await PollAsync(server, Obj("""{"name":"test.alpha"}"""))) ["cursor"]!;
        await Publish(server, "test.alpha", Obj("""{"n":1}"""));
        await Publish(server, "test.alpha", Obj("""{"n":2}"""));

        var p = Obj("""{"name":"test.alpha"}""");
        p["cursor"] = start;
        var stream = OpenStream(server, "s-1", p);
        await notes.WaitAsync("s-1", n => n.Count(x => x.Method == "notifications/events/event") >= 2);
        await Publish(server, "test.alpha", Obj("""{"n":3}"""));

        var seen = await notes.WaitAsync("s-1", n => n.Count(x => x.Method == "notifications/events/event") >= 3);
        var active = Active(seen);
        Assert.Equal(start, (string?)active.Params["cursor"]);
        Assert.False((bool)active.Params["truncated"]!);
        var ns = Events(seen).Select(e => (int)e.Params["data"]!["n"]!).ToList();
        Assert.Equal([1, 2, 3], ns);

        await stream.CancelAsync();
        await AssertEndsAsync(stream.Response);
    }

    [Fact]
    public async Task Replay_pages_through_more_events_than_one_batch_without_gaps_or_duplicates()
    {
        await using var server = await StartAsync(o => o.DefaultMaxEvents = 2);
        await using var notes = new Collector(server.Client);
        var start = (string)(await PollAsync(server, Obj("""{"name":"test.alpha"}"""))) ["cursor"]!;
        for (var i = 1; i <= 5; i++) await Publish(server, "test.alpha", Obj($$"""{"n":{{i}}}"""));

        var p = Obj("""{"name":"test.alpha"}""");
        p["cursor"] = start;
        var stream = OpenStream(server, "s-1", p);
        await notes.WaitAsync("s-1", n => n.Count(x => x.Method == "notifications/events/event") >= 5);
        await Publish(server, "test.alpha", Obj("""{"n":6}"""));
        var seen = await notes.WaitAsync("s-1", n => n.Count(x => x.Method == "notifications/events/event") >= 6);

        Assert.Equal([1, 2, 3, 4, 5, 6], Events(seen).Select(e => (int)e.Params["data"]!["n"]!).ToList());

        await stream.CancelAsync();
        await AssertEndsAsync(stream.Response);
    }

    [Fact]
    public async Task Arguments_filter_the_stream()
    {
        await using var server = await StartAsync();
        await using var notes = new Collector(server.Client);
        var stream = OpenStream(server, "s-1", Obj("""{"name":"test.alpha","arguments":{"branch":"main"}}"""));
        await notes.WaitAsync("s-1", n => n.Count >= 1);

        await Publish(server, "test.alpha", Obj("""{"branch":"dev","n":1}"""));
        await Publish(server, "test.alpha", Obj("""{"branch":"main","n":2}"""));
        await Publish(server, "test.beta", Obj("""{"n":3}"""));
        await Publish(server, "test.alpha", Obj("""{"branch":"main","n":4}"""));

        var seen = await notes.WaitAsync("s-1", n => n.Count(x => x.Method == "notifications/events/event") >= 2);
        await Task.Delay(100, Ct);
        seen = notes.For("s-1");
        Assert.Equal([2, 4], Events(seen).Select(e => (int)e.Params["data"]!["n"]!).ToList());

        await stream.CancelAsync();
        await AssertEndsAsync(stream.Response);
    }

    [Fact]
    public async Task Concurrent_streams_receive_their_own_events_under_their_own_ids()
    {
        await using var server = await StartAsync();
        await using var notes = new Collector(server.Client);
        var one = OpenStream(server, "s-1", Obj("""{"name":"test.alpha"}"""));
        var two = OpenStream(server, "s-2", Obj("""{"name":"test.beta"}"""));
        await notes.WaitAsync("s-1", n => n.Count >= 1);
        await notes.WaitAsync("s-2", n => n.Count >= 1);

        await Publish(server, "test.alpha", Obj("""{"n":1}"""));
        await Publish(server, "test.beta", Obj("""{"n":2}"""));

        var a = await notes.WaitAsync("s-1", n => n.Any(x => x.Method == "notifications/events/event"));
        var b = await notes.WaitAsync("s-2", n => n.Any(x => x.Method == "notifications/events/event"));
        Assert.All(Events(a), e => Assert.Equal("test.alpha", (string?)e.Params["name"]));
        Assert.All(Events(b), e => Assert.Equal("test.beta", (string?)e.Params["name"]));

        await one.CancelAsync();
        await two.CancelAsync();
        await AssertEndsAsync(one.Response);
        await AssertEndsAsync(two.Response);
    }

    [Fact]
    public async Task Cancelling_one_stream_stops_its_notifications_and_leaves_the_other_running()
    {
        await using var server = await StartAsync();
        await using var notes = new Collector(server.Client);
        var one = OpenStream(server, "s-1", Obj("""{"name":"test.alpha"}"""));
        var two = OpenStream(server, "s-2", Obj("""{"name":"test.alpha"}"""));
        await notes.WaitAsync("s-1", n => n.Count >= 1);
        await notes.WaitAsync("s-2", n => n.Count >= 1);

        await one.CancelAsync();
        await AssertEndsAsync(one.Response);
        await Task.Delay(100, Ct); // lets a notification already in flight when the cancel arrived reach the client's handlers
        var countAtCancel = notes.For("s-1").Count;
        await Publish(server, "test.alpha", Obj("""{"n":1}"""));

        await notes.WaitAsync("s-2", n => n.Any(x => x.Method == "notifications/events/event"));
        await Task.Delay(500, Ct);
        Assert.Equal(countAtCancel, notes.For("s-1").Count);
        Assert.False(two.Response.IsCompleted);

        await two.CancelAsync();
        await AssertEndsAsync(two.Response);
    }

    [Fact]
    public async Task An_unknown_event_name_is_an_error_without_any_notification()
    {
        await using var server = await StartAsync();
        await using var notes = new Collector(server.Client);
        var stream = OpenStream(server, "s-1", Obj("""{"name":"nope.missing"}"""));

        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => stream.Response);
        Assert.Equal(EventsProtocol.NotFound, (int)ex.ErrorCode);
        await Task.Delay(100, Ct);
        Assert.Empty(notes.For("s-1"));
    }

    [Fact]
    public async Task An_invalid_cursor_is_invalid_params_without_any_notification()
    {
        await using var server = await StartAsync();
        await using var notes = new Collector(server.Client);
        var stream = OpenStream(server, "s-1", Obj("""{"name":"test.alpha","cursor":"%%%"}"""));

        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => stream.Response);
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Empty(notes.For("s-1"));
    }

    [Fact]
    public async Task A_cursor_older_than_retention_yields_active_truncated_then_the_retained_events()
    {
        await using var server = await StartAsync(o => o.Capacity = 5);
        await using var notes = new Collector(server.Client);
        var start = (string)(await PollAsync(server, Obj("""{"name":"test.alpha"}"""))) ["cursor"]!;
        for (var i = 1; i <= 8; i++) await Publish(server, "test.alpha", Obj($$"""{"n":{{i}}}"""));

        var p = Obj("""{"name":"test.alpha"}""");
        p["cursor"] = start;
        var stream = OpenStream(server, "s-1", p);
        var seen = await notes.WaitAsync("s-1", n => n.Count(x => x.Method == "notifications/events/event") >= 5);

        Assert.True((bool)Active(seen).Params["truncated"]!);
        Assert.Equal([4, 5, 6, 7, 8], Events(seen).Select(e => (int)e.Params["data"]!["n"]!).ToList());

        await stream.CancelAsync();
        await AssertEndsAsync(stream.Response);
    }

    /// <summary>
    /// The SDK client dispatches notification handlers concurrently, so arrival order at a client handler says nothing
    /// about the server. This reads the server's raw newline-delimited JSON output to verify wire order.
    /// </summary>
    [Fact]
    public async Task Notifications_are_written_to_the_wire_in_order_with_the_subscription_id()
    {
        Pipe clientToServer = new(), serverToClient = new();
        var hostBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        hostBuilder.Services.AddMcpServer()
            .WithEvents(o =>
            {
                o.HeartbeatInterval = TimeSpan.FromSeconds(30);
                o.Define(new EventDefinition { Name = "test.beta", Description = "Beta event", PayloadSchema = Obj("""{"type":"object"}""") });
            })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        using var host = hostBuilder.Build();
        await host.StartAsync(Ct);
        try
        {
            using var reader = new StreamReader(serverToClient.Reader.AsStream());
            var writer = clientToServer.Writer.AsStream();
            async Task SendLine(string json) { await writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json + "\n"), Ct); await writer.FlushAsync(Ct); }
            async Task<JsonObject> ReadMessage() => (JsonObject)JsonNode.Parse(await reader.ReadLineAsync(Ct).AsTask().WaitAsync(Generous, Ct) ?? throw new EndOfStreamException())!;

            await SendLine("""{"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"raw","version":"1"}}}""");
            Assert.Equal("init", (string?)(await ReadMessage())["id"]);
            await SendLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            var publisher = host.Services.GetRequiredService<IEventPublisher>();
            await SendLine("""{"jsonrpc":"2.0","id":"s-1","method":"events/stream","params":{"name":"test.beta"}}""");
            Assert.Equal("notifications/events/active", (string?)(await ReadMessage())["method"]);

            for (var i = 1; i <= 20; i++) await publisher.PublishAsync("test.beta", Obj($$"""{"n":{{i}}}"""), cancellationToken: Ct);

            var cursors = new List<string>();
            for (var i = 1; i <= 20; i++)
            {
                var message = await ReadMessage();
                Assert.Equal("notifications/events/event", (string?)message["method"]);
                Assert.Equal("s-1", (string?)message["params"]!["_meta"]![SubscriptionIdKey]);
                Assert.Equal(i, (int)message["params"]!["data"]!["n"]!);
                cursors.Add((string)message["params"]!["cursor"]!);
            }

            Assert.Equal(cursors.Select(c => EventCursor.Decode(c).Sequence).OrderBy(x => x), cursors.Select(c => EventCursor.Decode(c).Sequence));

            // ApplicationStopping while the stream is open ends it with the final frame. (Only the signal is raised: a full
            // host stop races the transport teardown against the response write.)
            host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            var final = await ReadMessage();
            Assert.True(JsonNode.DeepEquals(
                Obj("""{"jsonrpc":"2.0","id":"s-1","result":{"resultType":"complete","_meta":{}}}"""), final), final.ToJsonString());
        }
        finally
        {
            await host.StopAsync(Ct);
        }
    }
}
