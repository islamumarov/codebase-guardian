using System.Text.Json;
using System.Text.Json.Nodes;
using CodebaseGuardian.Tests.Infrastructure;
using Mcp.Events;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;

namespace CodebaseGuardian.Tests.Events;

public sealed class EventsPollProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static Task<InProcessMcpServer> StartAsync(
        Action<EventsOptions>? tweak = null, bool faultyMatcher = false) =>
        InProcessMcpServer.StartAsync(
            (_, _, builder) => builder.WithEvents(o =>
            {
                o.Define(new EventDefinition
                {
                    Name = "test.alpha",
                    Description = "Alpha event",
                    InputSchema = Obj("""{"type":"object","properties":{"branch":{"type":"string"}}}"""),
                    PayloadSchema = Obj("""{"type":"object","properties":{"branch":{"type":"string"},"n":{"type":"integer"}}}"""),
                    Matches = (args, data) => args is null || !args.ContainsKey("branch") || (string?)args["branch"] == (string?)data["branch"],
                });
                o.Define(new EventDefinition
                {
                    Name = "test.beta",
                    Description = "Beta event",
                    PayloadSchema = Obj("""{"type":"object"}"""),
                });
                if (faultyMatcher)
                {
                    o.Define(new EventDefinition
                    {
                        Name = "test.faulty",
                        Description = "Throws in Matches",
                        PayloadSchema = Obj("""{"type":"object"}"""),
                        Matches = (_, _) => throw new InvalidOperationException("secret internal detail"),
                    });
                }

                tweak?.Invoke(o);
            }),
            cancellationToken: Ct);

    private static Task<JsonObject> PollAsync(InProcessMcpServer server, JsonObject p) =>
        server.RequestAsync("events/poll", p, Ct);

    private static async Task<McpProtocolException> PollErrorAsync(InProcessMcpServer server, JsonObject p) =>
        await Assert.ThrowsAsync<McpProtocolException>(() => PollAsync(server, p));

    private static Task Publish(InProcessMcpServer server, string name, JsonObject data) =>
        server.Services.GetRequiredService<IEventPublisher>().PublishAsync(name, data, cancellationToken: Ct).AsTask();

    [Fact]
    public async Task Capability_declares_events_with_listChanged_false()
    {
        await using var server = await StartAsync();
        var events = Assert.IsType<JsonElement>(server.Client.ServerCapabilities.Experimental!["events"]);
        Assert.Equal(JsonValueKind.False, events.GetProperty("listChanged").ValueKind);
    }

    [Fact]
    public async Task List_returns_every_definition_ordered_by_name_with_cache_hints()
    {
        await using var server = await StartAsync();
        var result = await server.RequestAsync("events/list", Obj("""{"cursor":"ignored"}"""), Ct);

        Assert.Equal("complete", (string?)result["resultType"]);
        Assert.Equal(3_600_000, (long?)result["ttlMs"]);
        Assert.Equal("public", (string?)result["cacheScope"]);
        Assert.False(result.ContainsKey("nextCursor"));

        var events = result["events"]!.AsArray();
        Assert.Equal(["test.alpha", "test.beta"], events.Select(e => (string)e!["name"]!).ToList());
        foreach (var e in events)
        {
            Assert.Equal(["poll", "push"], e!["delivery"]!.AsArray().Select(d => (string)d!).ToList());
            Assert.NotNull(e["description"]);
            Assert.Equal("object", (string?)e["inputSchema"]!["type"]);
            Assert.Equal("object", (string?)e["payloadSchema"]!["type"]);
        }

        Assert.Equal("string", (string?)events[0]!["inputSchema"]!["properties"]!["branch"]!["type"]);
    }

    [Fact]
    public async Task List_includes_webhook_delivery_when_enabled()
    {
        await using var server = await StartAsync(o => o.WebhooksEnabled = true);
        var result = await server.RequestAsync("events/list", cancellationToken: Ct);
        foreach (var e in result["events"]!.AsArray())
        {
            Assert.Equal(["poll", "push", "webhook"], e!["delivery"]!.AsArray().Select(d => (string)d!).ToList());
        }
    }

    [Fact]
    public async Task Poll_with_null_cursor_returns_no_events_and_a_fresh_cursor()
    {
        await using var server = await StartAsync();
        var result = await PollAsync(server, Obj("""{"name":"test.alpha","cursor":null}"""));

        Assert.Equal("complete", (string?)result["resultType"]);
        Assert.Empty(result["events"]!.AsArray());
        Assert.False(string.IsNullOrEmpty((string?)result["cursor"]));
        Assert.False((bool)result["truncated"]!);
        Assert.False((bool)result["hasMore"]!);
        Assert.Equal(5000, (long?)result["nextPollMs"]);
    }

    [Fact]
    public async Task Poll_returns_events_published_after_the_previous_cursor()
    {
        await using var server = await StartAsync();
        var first = await PollAsync(server, Obj("""{"name":"test.alpha"}"""));
        await Publish(server, "test.alpha", Obj("""{"branch":"main","n":1}"""));

        var p = Obj("""{"name":"test.alpha"}""");
        p["cursor"] = (string?)first["cursor"];
        var second = await PollAsync(server, p);

        var e = Assert.Single(second["events"]!.AsArray())!.AsObject();
        Assert.Equal(["eventId", "name", "timestamp", "data"], e.Select(kv => kv.Key).ToList());
        Assert.Equal("test.alpha", (string?)e["name"]);
        Assert.False(string.IsNullOrEmpty((string?)e["eventId"]));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", (string)e["timestamp"]!);
        Assert.True(JsonNode.DeepEquals(Obj("""{"branch":"main","n":1}"""), e["data"]));
        Assert.NotEqual((string?)first["cursor"], (string?)second["cursor"]);

        // Polling again from the new cursor yields nothing.
        p["cursor"] = (string?)second["cursor"];
        Assert.Empty((await PollAsync(server, p))["events"]!.AsArray());
    }

    [Fact]
    public async Task Poll_only_returns_events_of_the_requested_name()
    {
        await using var server = await StartAsync();
        var first = await PollAsync(server, Obj("""{"name":"test.alpha"}"""));
        await Publish(server, "test.beta", Obj("""{"x":1}"""));
        await Publish(server, "test.alpha", Obj("""{"branch":"main"}"""));

        var p = Obj("""{"name":"test.alpha"}""");
        p["cursor"] = (string?)first["cursor"];
        var result = await PollAsync(server, p);
        Assert.Equal("test.alpha", (string?)Assert.Single(result["events"]!.AsArray())!["name"]);
    }

    [Fact]
    public async Task Poll_applies_the_argument_filter()
    {
        await using var server = await StartAsync();
        var first = await PollAsync(server, Obj("""{"name":"test.alpha"}"""));
        await Publish(server, "test.alpha", Obj("""{"branch":"main","n":1}"""));
        await Publish(server, "test.alpha", Obj("""{"branch":"dev","n":2}"""));
        await Publish(server, "test.alpha", Obj("""{"branch":"main","n":3}"""));

        var p = Obj("""{"name":"test.alpha","arguments":{"branch":"main"}}""");
        p["cursor"] = (string?)first["cursor"];
        var result = await PollAsync(server, p);

        Assert.Equal([1, 3], result["events"]!.AsArray().Select(e => (int)e!["data"]!["n"]!).ToList());
    }

    [Fact]
    public async Task Poll_pages_with_maxEvents_and_clamps_zero_to_one()
    {
        await using var server = await StartAsync();
        var first = await PollAsync(server, Obj("""{"name":"test.beta"}"""));
        await Publish(server, "test.beta", Obj("""{"i":1}"""));
        await Publish(server, "test.beta", Obj("""{"i":2}"""));

        var p = Obj("""{"name":"test.beta","maxEvents":0}""");
        p["cursor"] = (string?)first["cursor"];
        var page1 = await PollAsync(server, p);
        Assert.Single(page1["events"]!.AsArray());
        Assert.True((bool)page1["hasMore"]!);

        p["cursor"] = (string?)page1["cursor"];
        var page2 = await PollAsync(server, p);
        Assert.Equal(2, (int)Assert.Single(page2["events"]!.AsArray())!["data"]!["i"]!);
        Assert.False((bool)page2["hasMore"]!);
    }

    [Fact]
    public async Task Poll_clamps_maxEvents_to_the_configured_limit()
    {
        await using var server = await StartAsync(o => o.MaxEventsLimit = 2);
        var first = await PollAsync(server, Obj("""{"name":"test.beta"}"""));
        for (var i = 0; i < 3; i++)
        {
            await Publish(server, "test.beta", Obj("{}"));
        }

        var p = Obj("""{"name":"test.beta","maxEvents":500}""");
        p["cursor"] = (string?)first["cursor"];
        var result = await PollAsync(server, p);
        Assert.Equal(2, result["events"]!.AsArray().Count);
        Assert.True((bool)result["hasMore"]!);
    }

    [Fact]
    public async Task Poll_with_maxAgeMs_zero_skips_events_and_reports_truncation()
    {
        await using var server = await StartAsync();
        var first = await PollAsync(server, Obj("""{"name":"test.beta"}"""));
        await Publish(server, "test.beta", Obj("{}"));
        await Task.Delay(20, Ct);

        var p = Obj("""{"name":"test.beta","maxAgeMs":0}""");
        p["cursor"] = (string?)first["cursor"];
        var result = await PollAsync(server, p);
        Assert.Empty(result["events"]!.AsArray());
        Assert.True((bool)result["truncated"]!);
    }

    [Fact]
    public async Task Poll_with_a_foreign_cursor_is_truncated_not_an_error()
    {
        await using var other = await StartAsync();
        var foreign = (string?)(await PollAsync(other, Obj("""{"name":"test.beta"}""")))["cursor"];

        await using var server = await StartAsync();
        await Publish(server, "test.beta", Obj("{}"));
        var p = Obj("""{"name":"test.beta"}""");
        p["cursor"] = foreign;
        var result = await PollAsync(server, p);
        Assert.True((bool)result["truncated"]!);
        Assert.Single(result["events"]!.AsArray());
    }

    [Fact]
    public async Task Poll_unknown_event_is_NotFound_with_kind()
    {
        await using var server = await StartAsync();
        var ex = await PollErrorAsync(server, Obj("""{"name":"test.nope"}"""));
        Assert.Equal(-32011, (int)ex.ErrorCode);
        Assert.Equal("event", Assert.IsType<string>(ex.Data["kind"]));
    }

    [Theory]
    [InlineData("""{"name":"test.alpha","arguments":{"branch":5}}""")]
    [InlineData("""{"name":"test.alpha","arguments":{"other":"x"}}""")]
    [InlineData("""{"name":"test.alpha","arguments":"main"}""")]
    [InlineData("""{"name":"test.alpha","cursor":"%%%not-a-cursor"}""")]
    [InlineData("""{"name":"test.alpha","cursor":5}""")]
    [InlineData("""{"name":"test.alpha","maxAgeMs":-1}""")]
    [InlineData("""{"name":"test.alpha","maxAgeMs":"soon"}""")]
    [InlineData("""{"name":"test.alpha","maxEvents":"many"}""")]
    [InlineData("""{"name":"test.alpha","maxEvents":1.5}""")]
    [InlineData("""{}""")]
    [InlineData("""{"name":7}""")]
    public async Task Poll_invalid_params_are_InvalidParams(string json)
    {
        await using var server = await StartAsync();
        var ex = await PollErrorAsync(server, Obj(json));
        Assert.Equal(-32602, (int)ex.ErrorCode);
    }

    [Fact]
    public async Task Poll_with_a_throwing_Matches_predicate_is_an_internal_error_without_leaking_detail()
    {
        await using var server = await StartAsync(faultyMatcher: true);
        var first = await PollAsync(server, Obj("""{"name":"test.faulty"}"""));
        await Publish(server, "test.faulty", Obj("{}"));

        var p = Obj("""{"name":"test.faulty"}""");
        p["cursor"] = (string?)first["cursor"];
        var ex = await PollErrorAsync(server, p);
        Assert.Equal(-32603, (int)ex.ErrorCode);
        Assert.DoesNotContain("secret internal detail", ex.Message);
    }

    [Fact]
    public void Parser_rejects_a_delivery_mode_the_server_does_not_offer_as_Unsupported()
    {
        var options = new EventsOptions();
        options.Define(new EventDefinition { Name = "test.beta", Description = "b", PayloadSchema = Obj("""{"type":"object"}""") });
        var log = new InMemoryEventLog(options);

        var ex = Assert.Throws<McpProtocolException>(() =>
            EventsRequestParser.Parse(Obj("""{"name":"test.beta"}"""), options, log, EventDelivery.Webhook));
        Assert.Equal(EventsProtocol.Unsupported, (int)ex.ErrorCode);
        Assert.Equal("deliveryMode", ex.Data["feature"]);
        Assert.Equal("webhook", ex.Data["value"]);

        options.WebhooksEnabled = true;
        Assert.Equal("test.beta", EventsRequestParser.Parse(Obj("""{"name":"test.beta"}"""), options, log, EventDelivery.Webhook).Definition.Name);
    }
}
