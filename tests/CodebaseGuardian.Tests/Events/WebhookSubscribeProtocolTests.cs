using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodebaseGuardian.Tests.Infrastructure;
using Mcp.Events;
using ModelContextProtocol;

namespace CodebaseGuardian.Tests.Events;

public sealed class WebhookSubscribeProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static Task<InProcessMcpServer> StartAsync(Action<EventsOptions>? tweak = null) =>
        InProcessMcpServer.StartAsync(
            (_, _, builder) => builder.WithEvents(o =>
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
                tweak?.Invoke(o);
            }),
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

    private static Task<JsonObject> SubscribeAsync(InProcessMcpServer server, JsonObject p) =>
        server.RequestAsync("events/subscribe", p, Ct);

    private static async Task<McpProtocolException> SubscribeErrorAsync(InProcessMcpServer server, JsonObject p) =>
        await Assert.ThrowsAsync<McpProtocolException>(() => SubscribeAsync(server, p));

    private static byte[] KeyOf(WebhookReceiver receiver)
    {
        Assert.True(WebhookSecret.TryParse(receiver.Secret, out var key));
        return key;
    }

    private static int Code(McpProtocolException ex) => (int)ex.ErrorCode;

    private static void AssertNear(TimeSpan expected, JsonObject result)
    {
        var refreshBefore = DateTimeOffset.Parse(result["refreshBefore"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AssumeUniversal);
        Assert.InRange((refreshBefore - DateTimeOffset.UtcNow - expected).TotalSeconds, -30, 30);
    }

    [Fact]
    public async Task Subscribe_verifies_the_endpoint_and_returns_a_deterministic_id()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync();

        var result = await SubscribeAsync(server, SubscribeParams(receiver));

        var verification = Assert.Single(receiver.Received);
        Assert.True(WebhookReceiver.SignatureValid(verification, KeyOf(receiver)));
        Assert.StartsWith("msg_verification_", verification.Headers["webhook-id"]);
        Assert.Equal("verification", verification.Json["type"]!.GetValue<string>());
        var id = result["id"]!.GetValue<string>();
        Assert.Equal(id, verification.Headers["x-mcp-subscription-id"]);
        Assert.Matches(new Regex("^sub_[0-9a-f]{16}$"), id);
        Assert.Equal(SubscriptionKey.ComputeId("alice", receiver.Url.ToString(), "test.alpha", Obj("""{"branch":"main"}""")), id);
        Assert.Equal("complete", result["resultType"]!.GetValue<string>());
        AssertNear(TimeSpan.FromHours(1), result);
        Assert.False(string.IsNullOrEmpty(result["cursor"]!.GetValue<string>()));
        Assert.False(result["truncated"]!.GetValue<bool>());
        Assert.False(result.ContainsKey("deliveryStatus"));
    }

    [Fact]
    public async Task Subscribing_again_refreshes_without_a_second_verification()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync();

        var first = await SubscribeAsync(server, SubscribeParams(receiver));
        var second = await SubscribeAsync(server, SubscribeParams(receiver));

        Assert.Equal(first["id"]!.GetValue<string>(), second["id"]!.GetValue<string>());
        Assert.Single(receiver.Received);
        var status = second["deliveryStatus"]!.AsObject();
        Assert.True(status["active"]!.GetValue<bool>());
        Assert.Null(status["lastDeliveryAt"]);
        Assert.Null(status["lastError"]);
        Assert.False(second["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_second_subscription_to_a_verified_url_is_not_verified_again()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync();

        await SubscribeAsync(server, SubscribeParams(receiver));
        var other = await SubscribeAsync(server, SubscribeParams(receiver, p => p["arguments"] = Obj("""{"branch":"dev"}""")));

        Assert.Single(receiver.Received);
        Assert.False(other.ContainsKey("deliveryStatus"));
    }

    [Theory]
    [InlineData("null", 24 * 3600)]
    [InlineData("10000", 60)]
    [InlineData("172800000", 24 * 3600)]
    [InlineData("120000", 120)]
    public async Task Ttl_is_granted_within_the_configured_bounds(string ttlJson, int expectedSeconds)
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync();

        var result = await SubscribeAsync(server, SubscribeParams(receiver, p => p["ttlMs"] = JsonNode.Parse(ttlJson)));

        AssertNear(TimeSpan.FromSeconds(expectedSeconds), result);
    }

    [Theory]
    [InlineData("\"soon\"")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public async Task A_malformed_ttl_is_invalid_params(string ttlJson)
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync();

        var ex = await SubscribeErrorAsync(server, SubscribeParams(receiver, p => p["ttlMs"] = JsonNode.Parse(ttlJson)));

        Assert.Equal(-32602, Code(ex));
        Assert.Empty(receiver.Received);
    }

    [Fact]
    public async Task An_anonymous_caller_is_forbidden()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync(o => o.PrincipalResolver = _ => null);

        var ex = await SubscribeErrorAsync(server, SubscribeParams(receiver));

        Assert.Equal(-32012, Code(ex));
        Assert.Empty(receiver.Received);
    }

    [Fact]
    public async Task Webhooks_disabled_is_unsupported()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync(o => o.WebhooksEnabled = false);

        var ex = await SubscribeErrorAsync(server, SubscribeParams(receiver));

        Assert.Equal(-32014, Code(ex));
        Assert.Equal("deliveryMode", ex.Data["feature"]);
        Assert.Equal("webhook", ex.Data["value"]);
    }

    [Theory]
    [InlineData("push")]
    [InlineData("poll")]
    public async Task Other_known_delivery_modes_are_unsupported(string mode)
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync();

        var ex = await SubscribeErrorAsync(server, SubscribeParams(receiver, p => p["delivery"]!["mode"] = mode));

        Assert.Equal(-32014, Code(ex));
        Assert.Equal("deliveryMode", ex.Data["feature"]);
        Assert.Equal(mode, ex.Data["value"]);
    }

    [Fact]
    public async Task An_unknown_delivery_mode_or_a_missing_delivery_object_is_invalid_params()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync();

        Assert.Equal(-32602, Code(await SubscribeErrorAsync(server, SubscribeParams(receiver, p => p["delivery"]!["mode"] = "carrier-pigeon"))));
        Assert.Equal(-32602, Code(await SubscribeErrorAsync(server, SubscribeParams(receiver, p => p.Remove("delivery")))));
    }

    [Fact]
    public async Task A_bad_secret_is_rejected_without_echoing_it()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync();
        const string bad = "whsec_not-base64-at-all!!";

        var ex = await SubscribeErrorAsync(server, SubscribeParams(receiver, p => p["delivery"]!["secret"] = bad));

        Assert.Equal(-32602, Code(ex));
        Assert.Contains("delivery.secret", ex.Message);
        Assert.DoesNotContain(bad, ex.Message);
        Assert.DoesNotContain("not-base64", ex.Message);
        Assert.Empty(receiver.Received);
    }

    [Fact]
    public async Task An_http_url_is_rejected_unless_the_insecure_flag_is_set()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync(o => o.Webhooks.AllowInsecureLoopback = false);

        var ex = await SubscribeErrorAsync(server, SubscribeParams(receiver));

        Assert.Equal(-32602, Code(ex));
        Assert.Contains("delivery.url", ex.Message);
        Assert.Empty(receiver.Received);
    }

    [Fact]
    public async Task An_endpoint_that_does_not_echo_the_challenge_fails_verification()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        receiver.EchoChallenges = false;
        await using var server = await StartAsync();

        var ex = await SubscribeErrorAsync(server, SubscribeParams(receiver));

        Assert.Equal(-32015, Code(ex));
        Assert.Equal("challenge_failed", ex.Data["reason"]);
        Assert.Single(receiver.Received);
    }

    [Fact]
    public async Task A_failed_verification_is_not_cached()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        receiver.EchoChallenges = false;
        await using var server = await StartAsync();
        await SubscribeErrorAsync(server, SubscribeParams(receiver));

        receiver.EchoChallenges = true;
        await SubscribeAsync(server, SubscribeParams(receiver));

        Assert.Equal(2, receiver.Received.Count);
    }

    [Fact]
    public async Task A_closed_port_reports_connection_refused()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync();

        var ex = await SubscribeErrorAsync(server, SubscribeParams(receiver, p => p["delivery"]!["url"] = $"http://127.0.0.1:{port}/hooks"));

        Assert.Equal(-32015, Code(ex));
        Assert.Equal("connection_refused", ex.Data["reason"]);
    }

    [Fact]
    public async Task Loopback_targets_are_refused_without_the_insecure_flag()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync(o => o.Webhooks.AllowInsecureLoopback = false);

        var ex = await SubscribeErrorAsync(server, SubscribeParams(receiver, p => p["delivery"]!["url"] = $"https://localhost:{receiver.Url.Port}/hooks"));

        Assert.Equal(-32015, Code(ex));
        Assert.Equal("connection_refused", ex.Data["reason"]);
        Assert.Empty(receiver.Received);
    }

    [Fact]
    public async Task The_subscription_limit_is_checked_before_any_network_call()
    {
        await using var first = await WebhookReceiver.StartAsync(Ct);
        await using var second = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync(o => o.Webhooks.MaxSubscriptionsPerPrincipal = 1);
        await SubscribeAsync(server, SubscribeParams(first));

        var ex = await SubscribeErrorAsync(server, SubscribeParams(second));

        Assert.Equal(-32013, Code(ex));
        Assert.Equal("subscriptions", ex.Data["limit"]);
        Assert.Equal(1L, Convert.ToInt64(ex.Data["max"]));
        Assert.Empty(second.Received);
        // Refreshing the existing subscription stays allowed at the limit.
        await SubscribeAsync(server, SubscribeParams(first));
    }

    [Fact]
    public async Task Rotating_the_secret_signs_with_both_keys_during_the_grace_period()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync();
        var first = await SubscribeAsync(server, SubscribeParams(receiver));
        var newSecret = "whsec_" + Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray());

        await SubscribeAsync(server, SubscribeParams(receiver, p => p["delivery"]!["secret"] = newSecret));

        var subscription = server.Services.GetService(typeof(WebhookSubscriptionStore)) as WebhookSubscriptionStore;
        Assert.NotNull(subscription);
        Assert.True(subscription.TryGet(first["id"]!.GetValue<string>(), out var stored));
        Assert.Equal(2, stored.SigningKeys(DateTimeOffset.UtcNow).Count);
        Assert.Single(stored.SigningKeys(DateTimeOffset.UtcNow.AddHours(1)));
    }

    [Fact]
    public async Task Unsubscribe_removes_the_subscription_once()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var server = await StartAsync();
        await SubscribeAsync(server, SubscribeParams(receiver));
        var unsubscribe = Obj($$$"""{"name":"test.alpha","arguments":{"branch":"main"},"delivery":{"url":"{{{receiver.Url}}}"}}""");

        var result = await server.RequestAsync("events/unsubscribe", unsubscribe, Ct);
        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => server.RequestAsync("events/unsubscribe", unsubscribe, Ct));

        Assert.Equal("complete", result["resultType"]!.GetValue<string>());
        Assert.Equal(-32011, Code(ex));
        Assert.Equal("subscription", ex.Data["kind"]);
    }

    [Fact]
    public async Task Unsubscribe_requires_a_principal_and_a_url()
    {
        await using var server = await StartAsync(o => o.PrincipalResolver = _ => null);
        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => server.RequestAsync(
            "events/unsubscribe", Obj("""{"name":"test.alpha","delivery":{"url":"https://h/x"}}"""), Ct));
        Assert.Equal(-32012, Code(ex));

        await using var authed = await StartAsync();
        var bad = await Assert.ThrowsAsync<McpProtocolException>(() => authed.RequestAsync(
            "events/unsubscribe", Obj("""{"name":"test.alpha","delivery":{}}"""), Ct));
        Assert.Equal(-32602, Code(bad));
    }
}
