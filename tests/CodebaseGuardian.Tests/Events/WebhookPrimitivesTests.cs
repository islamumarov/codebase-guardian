using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Mcp.Events;

namespace CodebaseGuardian.Tests.Events;

public sealed class WebhookPrimitivesTests
{
    private const string VectorSecret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Signer_matches_the_Standard_Webhooks_vector()
    {
        Assert.True(WebhookSecret.TryParse(VectorSecret, out var key));
        var body = Encoding.UTF8.GetBytes("""{"test": 2432232314}""");

        var signature = WebhookSigner.Sign("msg_p5jXN8AQM9LWM0D4loKWxJek", 1614265330, body, [key]);

        Assert.Equal("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", signature);
    }

    [Fact]
    public void Signer_with_two_keys_emits_two_space_separated_entries_in_key_order()
    {
        Assert.True(WebhookSecret.TryParse(VectorSecret, out var key));
        var other = new byte[32];
        var body = "{}"u8.ToArray();

        var signature = WebhookSigner.Sign("msg_1", 1, body, [key, other]);

        var parts = signature.Split(' ');
        Assert.Equal(2, parts.Length);
        Assert.Equal(WebhookSigner.Sign("msg_1", 1, body, [key]), parts[0]);
        Assert.Equal(WebhookSigner.Sign("msg_1", 1, body, [other]), parts[1]);
        Assert.All(parts, p => Assert.StartsWith("v1,", p));
    }

    [Fact]
    public void Secret_parses_the_vector_to_24_bytes()
    {
        Assert.True(WebhookSecret.TryParse(VectorSecret, out var key));
        Assert.Equal(24, key.Length);
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(32, false)]
    [InlineData(24, true)]
    [InlineData(64, false)]
    public void Secret_accepts_padded_and_unpadded_base64(int bytes, bool padded)
    {
        var b64 = Convert.ToBase64String(new byte[bytes]);
        if (!padded) b64 = b64.TrimEnd('=');

        Assert.True(WebhookSecret.TryParse("whsec_" + b64, out var key));
        Assert.Equal(bytes, key.Length);
    }

    [Theory]
    [InlineData("whsec_")]
    [InlineData("whsec_!!!")]
    [InlineData(null)]
    public void Secret_rejects_malformed_values(string? value) => Assert.False(WebhookSecret.TryParse(value, out _));

    [Theory]
    [InlineData(23)]
    [InlineData(65)]
    public void Secret_rejects_out_of_range_lengths(int bytes) =>
        Assert.False(WebhookSecret.TryParse("whsec_" + Convert.ToBase64String(new byte[bytes]), out _));

    [Fact]
    public void Secret_rejects_a_missing_prefix() =>
        Assert.False(WebhookSecret.TryParse(Convert.ToBase64String(new byte[32]), out _));

    private const string CanonicalLiteral = """["alice","https://h/x","n.e",{"a":[true,null,"é"],"b":1}]""";

    [Fact]
    public void CanonicalJson_sorts_keys_and_drops_whitespace()
    {
        var node = JsonNode.Parse("""["alice", "https://h/x", "n.e", {"b":1, "a":[true, null, "é"]}]""");

        Assert.Equal(CanonicalLiteral, CanonicalJson.Serialize(node));
    }

    [Fact]
    public void CanonicalJson_serializes_a_null_node_as_null() => Assert.Equal("null", CanonicalJson.Serialize(null));

    [Fact]
    public void SubscriptionKey_hashes_the_canonical_key()
    {
        var args = (JsonObject)JsonNode.Parse("""{"b":1,"a":[true,null,"é"]}""")!;

        var id = SubscriptionKey.ComputeId("alice", "https://h/x", "n.e", args);

        Assert.Equal("sub_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalLiteral)))[..16], id);
    }

    [Fact]
    public void SubscriptionKey_ignores_argument_key_order_and_treats_null_as_empty()
    {
        var a = (JsonObject)JsonNode.Parse("""{"x":1,"y":2}""")!;
        var b = (JsonObject)JsonNode.Parse("""{"y":2,"x":1}""")!;

        Assert.Equal(SubscriptionKey.ComputeId("p", "https://h", "n.e", a), SubscriptionKey.ComputeId("p", "https://h", "n.e", b));
        Assert.Equal(SubscriptionKey.ComputeId("p", "https://h", "n.e", null), SubscriptionKey.ComputeId("p", "https://h", "n.e", new JsonObject()));
        Assert.NotEqual(SubscriptionKey.ComputeId("p", "https://h", "n.e", a), SubscriptionKey.ComputeId("q", "https://h", "n.e", a));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    public void AddressPolicy_blocks_non_global_addresses(string address) =>
        Assert.True(WebhookAddressPolicy.IsBlocked(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void AddressPolicy_allows_global_addresses(string address) =>
        Assert.False(WebhookAddressPolicy.IsBlocked(IPAddress.Parse(address)));

    [Fact]
    public void UrlPolicy_accepts_https()
    {
        Assert.True(WebhookUrlPolicy.TryValidate("https://hooks.example.com/x", false, out var uri, out var problem));
        Assert.Equal("hooks.example.com", uri.Host);
        Assert.Null(problem);
    }

    [Fact]
    public void UrlPolicy_accepts_http_only_with_the_loopback_flag()
    {
        Assert.False(WebhookUrlPolicy.TryValidate("http://hooks.example.com/x", false, out _, out var problem));
        Assert.NotNull(problem);
        Assert.True(WebhookUrlPolicy.TryValidate("http://hooks.example.com/x", true, out _, out _));
    }

    [Theory]
    [InlineData("https://u:p@h/")]
    [InlineData("https://h/#f")]
    [InlineData("/relative/path")]
    [InlineData("ftp://h/x")]
    [InlineData("")]
    [InlineData(null)]
    public void UrlPolicy_rejects_unsafe_or_malformed_urls(string? url) =>
        Assert.False(WebhookUrlPolicy.TryValidate(url, true, out _, out var problem) || problem is null);

    [Fact]
    public void UrlPolicy_rejects_overlong_urls() =>
        Assert.False(WebhookUrlPolicy.TryValidate("https://h/" + new string('a', 2048), false, out _, out _));

    [Fact]
    public async Task Sender_refuses_a_host_that_resolves_to_a_private_address_without_any_connection()
    {
        var resolved = new List<string>();
        var options = new WebhookOptions
        {
            ResolveHost = (host, _) =>
            {
                resolved.Add(host);
                return ValueTask.FromResult(new[] { IPAddress.Parse("10.0.0.1") });
            },
        };
        using var sender = new WebhookHttpSender(options);

        var result = await sender.SendAsync(new Uri("https://rebind.test/hook"), "msg_1", "{}"u8.ToArray(), "sub_x", [new byte[32]], Ct);

        Assert.False(result.Delivered);
        Assert.Equal("connection_refused", result.ErrorCategory);
        Assert.Equal(["rebind.test"], resolved);
    }
}
