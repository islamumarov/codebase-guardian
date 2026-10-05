using System.Text.Json.Nodes;
using CodebaseGuardian.Tests.Infrastructure;
using Mcp.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CodebaseGuardian.Tests.Events;

public sealed class WebhookDeliveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static Task<InProcessMcpServer> StartAsync(Action<EventsOptions>? tweak = null, FakeTimeProvider? time = null) =>
        InProcessMcpServer.StartAsync(
            (services, _, builder) =>
            {
                if (time is not null) services.AddSingleton<TimeProvider>(time);
                builder.WithEvents(o =>
                {
                    o.Define(new EventDefinition
                    {
                        Name = "test.alpha",
                        Description = "Alpha event",
                        InputSchema = Obj("""{"type":"object","properties":{"branch":{"type":"string"}}}"""),
                        PayloadSchema = Obj("""{"type":"object"}"""),
                    });
                    o.WebhooksEnabled = true;
                    o.PrincipalResolver = _ => "alice";
                    o.Webhooks.AllowInsecureLoopback = true;
                    o.Webhooks.RetryDelays.Clear();
                    o.Webhooks.RetryDelays.Add(TimeSpan.FromMilliseconds(50));
                    o.Webhooks.RetryDelays.Add(TimeSpan.FromMilliseconds(100));
                    o.Webhooks.RetryWindow = TimeSpan.FromSeconds(5);
                    tweak?.Invoke(o);
                });
            },
            cancellationToken: Ct);

    private static JsonObject SubscribeParams(WebhookReceiver receiver, Action<JsonObject>? tweak = null)
    {
        var p = Obj($$$"""
            {"name":"test.alpha","arguments":{"branch":"main"},
             "delivery":{"mode":"webhook","url":"{{{receiver.Url}}}","secret":"{{{receiver.Secret}}}"}}
            """);
        tweak?.Invoke(p);
        return p;
    }

    private static Task<JsonObject> SubscribeAsync(InProcessMcpServer server, WebhookReceiver receiver, Action<JsonObject>? tweak = null) =>
        server.RequestAsync("events/subscribe", SubscribeParams(receiver, tweak), Ct);

    private static ValueTask<EventEnvelope> PublishAsync(InProcessMcpServer server, string eventId, int n = 0) =>
        server.Services.GetRequiredService<IEventPublisher>()
            .PublishAsync("test.alpha", Obj($$"""{"branch":"main","n":{{n}}}"""), eventId, cancellationToken: Ct);

    private static byte[] KeyOf(string secret)
    {
        Assert.True(WebhookSecret.TryParse(secret, out var key));
        return key;
    }

    private static Func<ReceivedWebhook, bool> ForId(string id) => r => r.Headers["webhook-id"] == id;

    private static List<ReceivedWebhook> Attempts(WebhookReceiver receiver, string id) =>
        [.. receiver.Received.Where(ForId(id))];

    private static async Task<ReceivedWebhook> ArrivesAsync(WebhookReceiver receiver, string id) =>
        await receiver.WaitForAsync(ForId(id), Wait, Ct);

    [Fact]
    public async Task Events_are_delivered_in_publish_order_with_signed_standard_webhook_requests()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync();
        var sub = await SubscribeAsync(server, receiver);

        await PublishAsync(server, "e1");
        await PublishAsync(server, "e2");
        await PublishAsync(server, "e3");
        foreach (var id in new[] { "e1", "e2", "e3" }) await ArrivesAsync(receiver, id);

        var deliveries = receiver.Received.Where(r => r.Json["type"]?.GetValue<string>() != "verification").ToList();
        Assert.Equal(["e1", "e2", "e3"], deliveries.Select(r => r.Headers["webhook-id"]));
        foreach (var request in deliveries)
        {
            Assert.True(WebhookReceiver.SignatureValid(request, KeyOf(receiver.Secret)));
            Assert.Equal(sub["id"]!.GetValue<string>(), request.Headers["x-mcp-subscription-id"]);
            var stamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(request.Headers["webhook-timestamp"]));
            Assert.InRange((stamp - DateTimeOffset.UtcNow).TotalSeconds, -10, 10);
            var body = request.Json;
            Assert.Equal(request.Headers["webhook-id"], body["eventId"]!.GetValue<string>());
            Assert.Equal("test.alpha", body["name"]!.GetValue<string>());
            Assert.NotNull(body["timestamp"]);
            Assert.Equal("main", body["data"]!["branch"]!.GetValue<string>());
            Assert.False(string.IsNullOrEmpty(body["cursor"]!.GetValue<string>()));
        }
    }

    [Fact]
    public async Task A_failing_event_is_retried_with_the_same_id_and_a_fresh_signature()
    {
        // Signatures cover a timestamp with one-second resolution, so the clock is faked and moved on between attempts.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        var seen = 0;
        receiver.StatusFor = r => r.Headers["webhook-id"] == "e1" && Interlocked.Increment(ref seen) <= 2 ? 500 : 200;
        await using var server = await StartAsync(o => o.Webhooks.RetryWindow = TimeSpan.FromHours(1), time);
        await SubscribeAsync(server, receiver);

        await PublishAsync(server, "e1");
        // Drive the clock by progress: wait for attempt n, then advance past the next retry delay.
        await WaitUntilAsync(() => Task.FromResult(Attempts(receiver, "e1").Count == 1));
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => Task.FromResult(Attempts(receiver, "e1").Count == 2));
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => Task.FromResult(DeliveredStatus(server) is not null));

        var attempts = Attempts(receiver, "e1");
        Assert.Equal(3, attempts.Count);
        Assert.Equal(3, attempts.Select(a => a.Headers["webhook-signature"]).Distinct().Count());
        Assert.All(attempts, a => Assert.True(WebhookReceiver.SignatureValid(a, KeyOf(receiver.Secret))));
        var refresh = await SubscribeAsync(server, receiver);
        var status = refresh["deliveryStatus"]!.AsObject();
        Assert.Null(status["lastError"]);
        Assert.NotNull(status["lastDeliveryAt"]);
        Assert.False(refresh.ContainsKey("failedSince"));
    }

    private static string? DeliveredStatus(InProcessMcpServer server) =>
        server.Services.GetRequiredService<WebhookSubscriptionStore>().Snapshot()
            .FirstOrDefault(s => s.LastDeliveryAt is not null)?.Id;

    [Fact]
    public async Task A_410_is_not_retried_and_the_next_event_is_still_delivered()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        receiver.StatusFor = r => r.Headers["webhook-id"] == "e1" ? 410 : 200;
        await using var server = await StartAsync();
        await SubscribeAsync(server, receiver);

        await PublishAsync(server, "e1");
        await PublishAsync(server, "e2");
        await ArrivesAsync(receiver, "e2");

        Assert.Single(Attempts(receiver, "e1"));
    }

    [Fact]
    public async Task A_persistently_failing_event_is_abandoned_after_three_attempts()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        var recovered = 0;
        receiver.StatusFor = r => Volatile.Read(ref recovered) == 0 && r.Headers["webhook-id"] == "e1" ? 503 : 200;
        await using var server = await StartAsync();
        await SubscribeAsync(server, receiver);

        await PublishAsync(server, "e1");
        await WaitUntilAsync(() => Task.FromResult(Attempts(receiver, "e1").Count == 3));
        var refresh = await SubscribeAsync(server, receiver);
        Assert.Equal("http_5xx", refresh["deliveryStatus"]!["lastError"]!.GetValue<string>());
        Assert.NotNull(refresh["failedSince"]);

        Volatile.Write(ref recovered, 1);
        await PublishAsync(server, "e2");
        await ArrivesAsync(receiver, "e2");
        await Task.Delay(300, Ct);
        Assert.Equal(3, Attempts(receiver, "e1").Count);
    }

    [Fact]
    public async Task The_cursor_stays_before_an_event_until_it_is_acknowledged()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync();
        var created = await SubscribeAsync(server, receiver);
        var before = created["cursor"]!.GetValue<string>();
        receiver.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var e1 = await PublishAsync(server, "e1");
        await ArrivesAsync(receiver, "e1");
        var held = await SubscribeAsync(server, receiver);
        Assert.Equal(before, held["cursor"]!.GetValue<string>());

        receiver.Gate.SetResult();
        var log = server.Services.GetRequiredService<IEventLog>();
        var after = log.CursorAt(e1.Sequence);
        await WaitUntilAsync(() => Task.FromResult(DeliveredStatus(server) is not null));
        await WaitUntilAsync(async () => (await SubscribeAsync(server, receiver))["cursor"]!.GetValue<string>() == after);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition was not met in time.");
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task Rotated_secrets_both_sign_during_the_grace_and_only_the_new_one_after()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync(time: time);
        await SubscribeAsync(server, receiver);
        var newSecret = "whsec_" + Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await server.RequestAsync("events/subscribe", SubscribeParams(receiver, p => p["delivery"]!["secret"] = newSecret), Ct);

        await PublishAsync(server, "e1");
        var during = await ArrivesAsync(receiver, "e1");
        Assert.Equal(2, during.Headers["webhook-signature"].Split(' ').Count(x => x.StartsWith("v1,")));
        Assert.True(WebhookReceiver.SignatureValid(during, KeyOf(receiver.Secret)));
        Assert.True(WebhookReceiver.SignatureValid(during, KeyOf(newSecret)));

        time.Advance(TimeSpan.FromMinutes(6));
        await PublishAsync(server, "e2");
        var after = await ArrivesAsync(receiver, "e2");
        Assert.Single(after.Headers["webhook-signature"].Split(' '));
        Assert.True(WebhookReceiver.SignatureValid(after, KeyOf(newSecret)));
    }

    [Fact]
    public async Task Lost_events_are_reported_with_a_gap_envelope_and_later_events_follow()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync(o => o.Capacity = 5);
        await SubscribeAsync(server, receiver);
        receiver.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await PublishAsync(server, "e1");
        await ArrivesAsync(receiver, "e1");
        for (var i = 2; i <= 11; i++) await PublishAsync(server, "e" + i, i);
        receiver.Gate.SetResult();

        var gap = await receiver.WaitForAsync(r => r.Headers["webhook-id"].StartsWith("msg_gap_"), Wait, Ct);
        Assert.Equal("gap", gap.Json["type"]!.GetValue<string>());
        var log = server.Services.GetRequiredService<IEventLog>();
        // Capacity 5 keeps e7..e11, so servable events start after sequence 6.
        Assert.Equal(log.CursorAt(6), gap.Json["cursor"]!.GetValue<string>());
        Assert.True(WebhookReceiver.SignatureValid(gap, KeyOf(receiver.Secret)));
        await ArrivesAsync(receiver, "e11");
        var order = receiver.Received.Select(r => r.Headers["webhook-id"]).ToList();
        Assert.True(order.IndexOf(order.First(i => i.StartsWith("msg_gap_"))) < order.IndexOf("e11"));
    }

    [Fact]
    public async Task An_expired_subscription_stops_delivering_and_can_be_created_again_without_reverification()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync(o => o.Webhooks.MinTtl = TimeSpan.FromMilliseconds(200));
        await SubscribeAsync(server, receiver, p => p["ttlMs"] = 200);
        var store = server.Services.GetRequiredService<WebhookSubscriptionStore>();
        await WaitUntilAsync(() => Task.FromResult(store.Snapshot().Count == 0));

        await PublishAsync(server, "e1");
        await Task.Delay(500, Ct);
        Assert.Empty(Attempts(receiver, "e1"));

        var again = await SubscribeAsync(server, receiver);
        Assert.False(again.ContainsKey("deliveryStatus"));
        Assert.Single(receiver.Received); // only the original verification
        await PublishAsync(server, "e2");
        await ArrivesAsync(receiver, "e2");
    }

    [Fact]
    public async Task Unsubscribing_stops_delivery()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync();
        await SubscribeAsync(server, receiver);
        await server.RequestAsync("events/unsubscribe", Obj($$$"""
            {"name":"test.alpha","arguments":{"branch":"main"},"delivery":{"url":"{{{receiver.Url}}}"}}
            """), Ct);

        await PublishAsync(server, "e1");
        await Task.Delay(500, Ct);

        Assert.Empty(Attempts(receiver, "e1"));
    }

    [Fact]
    public async Task Delivery_rechecks_the_target_address_and_records_connection_refused()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        EventsOptions? options = null;
        await using var server = await StartAsync(o => options = o);
        await SubscribeAsync(server, receiver);
        await PublishAsync(server, "e1");
        await ArrivesAsync(receiver, "e1");
        await WaitUntilAsync(() => Task.FromResult(DeliveredStatus(server) is not null));

        options!.Webhooks.AllowInsecureLoopback = false;
        await PublishAsync(server, "e2");
        var store = server.Services.GetRequiredService<WebhookSubscriptionStore>();
        await WaitUntilAsync(() => Task.FromResult(store.Snapshot().Single().LastError == "connection_refused"));

        Assert.Empty(Attempts(receiver, "e2"));
    }

    [Fact]
    public async Task Repeated_failures_suspend_the_subscription_until_it_is_refreshed()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        var failing = 1;
        receiver.StatusFor = r => Volatile.Read(ref failing) == 1 && r.Json["type"]?.GetValue<string>() != "verification" ? 500 : 200;
        await using var server = await StartAsync(o =>
        {
            o.Webhooks.SuspendMinAttempts = 4;
            o.Webhooks.SuspendFailureRate = 0.9;
        });
        await SubscribeAsync(server, receiver);
        var store = server.Services.GetRequiredService<WebhookSubscriptionStore>();

        await PublishAsync(server, "e1");
        await PublishAsync(server, "e2");
        await WaitUntilAsync(() => Task.FromResult(!store.Snapshot().Single().Active));
        Assert.Equal(4, receiver.Received.Count(r => r.Headers["webhook-id"] is "e1" or "e2"));

        Volatile.Write(ref failing, 0);
        var refresh = await SubscribeAsync(server, receiver);
        Assert.False(refresh["deliveryStatus"]!["active"]!.GetValue<bool>()); // status before reactivation
        await WaitUntilAsync(() => Task.FromResult(DeliveredStatus(server) is not null));
        Assert.Equal(2, Attempts(receiver, "e2").Count); // the pending event, retried once after the refresh
        Assert.True(store.Snapshot().Single().Active);
    }

    [Fact]
    public async Task A_refresh_resets_the_failure_statistics_so_one_more_failure_does_not_suspend_again()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        receiver.StatusFor = r => r.Json["type"]?.GetValue<string>() == "verification" ? 200 : 500;
        await using var server = await StartAsync(o =>
        {
            o.Webhooks.SuspendMinAttempts = 4;
            o.Webhooks.SuspendFailureRate = 0.9;
            o.Webhooks.RetryDelays.Clear();
        });
        await SubscribeAsync(server, receiver);
        var store = server.Services.GetRequiredService<WebhookSubscriptionStore>();
        for (var i = 1; i <= 4; i++) await PublishAsync(server, "e" + i);
        await WaitUntilAsync(() => Task.FromResult(!store.Snapshot().Single().Active));

        var refresh = await SubscribeAsync(server, receiver); // still failing
        Assert.False(refresh["deliveryStatus"]!["active"]!.GetValue<bool>());
        var attemptsAtRefresh = receiver.Received.Count;
        await WaitUntilAsync(() => Task.FromResult(receiver.Received.Count > attemptsAtRefresh));
        await Task.Delay(300, Ct);

        Assert.True(store.Snapshot().Single().Active); // one failure after the refresh is not 4 attempts
    }
}
