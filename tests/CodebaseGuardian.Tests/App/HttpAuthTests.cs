using System.ComponentModel;
using System.Net;
using System.Text.Json.Nodes;
using CodebaseGuardian.Hosting;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodebaseGuardian.Tests.App;

public sealed class HttpAuthTests : IDisposable
{
    private const string Key = "test-key-0123456789-abcdefghijklmnopqrstuvwxyz";
    private readonly TempGitRepo _repo = TempGitRepo.Create();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _repo.Dispose();

    private static Dictionary<string, string?> WithKey() => new() { ["Guardian:Http:ApiKeys:alice"] = Key };

    private Task<GuardianHttpTestHost> StartAsync(Dictionary<string, string?>? configuration = null, Action<IServiceCollection>? services = null) =>
        GuardianHttpTestHost.StartAsync(_repo.Path, configuration, services, Ct);

    private static HttpRequestMessage Initialize(string? authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    [Fact]
    public async Task A_request_without_Authorization_gets_401_with_a_Bearer_challenge()
    {
        await using var host = await StartAsync(WithKey());
        using var http = host.CreateHttpClient();

        using var response = await http.SendAsync(Initialize(null), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    [Fact]
    public async Task A_wrong_key_gets_401()
    {
        await using var host = await StartAsync(WithKey());
        using var http = host.CreateHttpClient();

        using var response = await http.SendAsync(Initialize("Bearer " + new string('x', 40)), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    [Fact]
    public async Task A_valid_key_lists_tools_and_healthz_stays_anonymous()
    {
        await using var host = await StartAsync(WithKey());
        await using var client = await host.ConnectAsync(Key, cancellationToken: Ct);
        using var http = host.CreateHttpClient();

        Assert.NotEmpty(await client.ListToolsAsync(cancellationToken: Ct));
        using var health = await http.GetAsync("/healthz", Ct);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Without_keys_the_endpoint_is_open()
    {
        await using var host = await StartAsync();
        using var http = host.CreateHttpClient();

        using var response = await http.SendAsync(Initialize(null), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_tool_sees_the_authenticated_principal()
    {
        await using var host = await StartAsync(WithKey(), services => services.AddMcpServer().WithTools<WhoAmITool>());
        await using var client = await host.ConnectAsync(Key, cancellationToken: Ct);

        var result = await client.CallToolAsync("who_am_i", cancellationToken: Ct);

        Assert.Equal("alice", ((TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public async Task Every_event_offers_webhook_delivery_when_authenticated()
    {
        await using var host = await StartAsync(WithKey());
        await using var client = await host.ConnectAsync(Key, cancellationToken: Ct);

        var delivery = await DeliveryModesAsync(client);

        Assert.All(delivery, modes => Assert.Contains("webhook", modes));
    }

    [Fact]
    public async Task Webhook_delivery_is_not_offered_without_keys()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var delivery = await DeliveryModesAsync(client);

        Assert.NotEmpty(delivery);
        Assert.All(delivery, modes => Assert.DoesNotContain("webhook", modes));
    }

    [Fact]
    public async Task Webhook_delivery_is_not_offered_when_disabled()
    {
        var configuration = WithKey();
        configuration["Guardian:Webhooks:Enabled"] = "false";
        await using var host = await StartAsync(configuration);
        await using var client = await host.ConnectAsync(Key, cancellationToken: Ct);

        Assert.All(await DeliveryModesAsync(client), modes => Assert.DoesNotContain("webhook", modes));
    }

    [Fact]
    public async Task Webhook_delivery_is_not_offered_on_stdio()
    {
        await using var host = await GuardianTestHost.StartAsync(_repo.Path, cancellationToken: Ct);

        var delivery = await DeliveryModesAsync(host.Client);

        Assert.NotEmpty(delivery);
        Assert.All(delivery, modes => Assert.DoesNotContain("webhook", modes));
    }

    [Fact]
    public async Task A_short_key_fails_startup_naming_the_setting()
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            StartAsync(new Dictionary<string, string?> { ["Guardian:Http:ApiKeys:alice"] = "short-key-" }));

        Assert.Contains("Guardian:Http:ApiKeys", exception.Message);
    }

    [Fact]
    public async Task An_invalid_principal_name_fails_startup_naming_the_setting()
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            StartAsync(new Dictionary<string, string?> { ["Guardian:Http:ApiKeys:bad name!"] = Key }));

        Assert.Contains("Guardian:Http:ApiKeys", exception.Message);
    }

    [Fact]
    public async Task Two_principals_sharing_one_key_fail_startup_naming_the_setting_and_principals_but_not_the_key()
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(new Dictionary<string, string?>
        {
            ["Guardian:Http:ApiKeys:alice"] = Key,
            ["Guardian:Http:ApiKeys:bob"] = Key,
        }));

        Assert.Contains("Guardian:Http:ApiKeys", exception.Message);
        Assert.Contains("alice", exception.Message);
        Assert.Contains("bob", exception.Message);
        Assert.DoesNotContain(Key, exception.Message);
    }

    [Theory]
    [InlineData("--urls=http://0.0.0.0:0")]
    [InlineData("--urls=http://127.0.0.1:0;http://0.0.0.0:0")]
    public async Task Remote_binding_without_a_key_names_both_settings(string urls)
    {
        var exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            HttpHost.Build([urls, "--Guardian:HttpAllowRemote=true", "--repo", _repo.Path, "--no-watch"]));

        Assert.Contains("HttpAllowRemote", exception.Message);
        Assert.Contains("Guardian:Http:ApiKeys", exception.Message);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Remote_binding_with_a_key_but_without_HttpAllowRemote_is_refused()
    {
        var exception = Assert.ThrowsAny<InvalidOperationException>(() => HttpHost.Build(
            ["--urls=http://0.0.0.0:0", $"--Guardian:Http:ApiKeys:alice={Key}", "--repo", _repo.Path, "--no-watch"]));

        Assert.Contains("HttpAllowRemote", exception.Message);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Kestrel_endpoints_without_a_key_are_refused_even_with_HttpAllowRemote()
    {
        var exception = Assert.ThrowsAny<InvalidOperationException>(() => HttpHost.Build(
            ["--urls=http://127.0.0.1:0", "--Kestrel:Endpoints:e:Url=http://0.0.0.0:5299", "--Guardian:HttpAllowRemote=true", "--repo", _repo.Path]));

        Assert.Contains("Guardian:Http:ApiKeys", exception.Message);
        await Task.CompletedTask;
    }

    [Fact]
    public void The_bound_address_backstop_needs_both_the_flag_and_a_key()
    {
        // RunAsync passes HttpAllowRemote && keys; without a key a wildcard address is refused after start.
        var exception = Assert.ThrowsAny<InvalidOperationException>(() => HttpHost.VerifyBoundAddresses(["http://[::]:5000"], remoteAllowed: false));
        Assert.Contains("Guardian:Http:ApiKeys", exception.Message);
        HttpHost.VerifyBoundAddresses(["http://[::]:5000"], remoteAllowed: true);
    }

    [Fact]
    public async Task Remote_binding_with_a_key_and_HttpAllowRemote_builds()
    {
        await using var app = HttpHost.Build(
            ["--urls=http://0.0.0.0:0", "--Guardian:HttpAllowRemote=true", $"--Guardian:Http:ApiKeys:alice={Key}", "--repo", _repo.Path, "--no-watch", "--Logging:LogLevel:Default=None"]);

        Assert.NotNull(app);
    }

    private static async Task<List<string[]>> DeliveryModesAsync(McpClient client)
    {
        var result = (JsonObject)(await client.SendRequestAsync(
            new JsonRpcRequest { Method = "events/list", Params = new JsonObject() }, Ct)).Result!;
        return [.. result["events"]!.AsArray().Select(e => e!["delivery"]!.AsArray().Select(m => m!.GetValue<string>()).ToArray())];
    }

    [McpServerToolType]
    private sealed class WhoAmITool
    {
        [McpServerTool(Name = "who_am_i"), Description("Test-only: the authenticated principal.")]
        public static string WhoAmI(RequestContext<CallToolRequestParams> context) =>
            context.User?.FindFirst("sub")?.Value ?? "anonymous";
    }
}
