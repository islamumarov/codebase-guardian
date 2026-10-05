using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CodebaseGuardian.Hosting;
using CodebaseGuardian.Tests.Infrastructure;
using Mcp.Events;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodebaseGuardian.Tests.App;

public sealed class HttpTransportTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private readonly TempGitRepo _repo = TempGitRepo.Create();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _repo.Dispose();

    private Task<GuardianHttpTestHost> StartAsync() => GuardianHttpTestHost.StartAsync(_repo.Path, cancellationToken: Ct);

    private static async Task<JsonObject> RequestAsync(McpClient client, string method, JsonObject? parameters = null) =>
        (JsonObject)(await client.SendRequestAsync(new JsonRpcRequest { Method = method, Params = parameters ?? new JsonObject() }, Ct)).Result!;

    [Fact]
    public async Task Healthz_answers_200_ok()
    {
        await using var host = await StartAsync();
        using var http = new HttpClient();

        using var response = await http.GetAsync(new Uri(host.BaseAddress, "/healthz"), Ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("evil.example.com", null)]
    [InlineData(null, "http://evil.example.com")]
    public async Task Requests_with_a_non_loopback_Host_or_Origin_are_rejected_to_stop_DNS_rebinding(string? host, string? origin)
    {
        await using var server = await StartAsync();
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.BaseAddress, "/healthz"));
        if (host is not null) request.Headers.Host = host;
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);

        using var response = await http.SendAsync(request, Ct);

        Assert.True((int)response.StatusCode is >= 400 and < 500, $"was {(int)response.StatusCode}");
    }

    [Fact]
    public async Task A_loopback_Origin_is_accepted()
    {
        await using var server = await StartAsync();
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.BaseAddress, "/healthz"));
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:3000");

        using var response = await http.SendAsync(request, Ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HttpAllowRemote_turns_the_Host_and_Origin_check_off()
    {
        await using var server = await GuardianHttpTestHost.StartAsync(
            _repo.Path, new Dictionary<string, string?> { ["Guardian:HttpAllowRemote"] = "true" }, cancellationToken: Ct);
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.BaseAddress, "/healthz"));
        request.Headers.Host = "guardian.internal";

        using var response = await http.SendAsync(request, Ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Client_negotiates_2026_07_28_and_lists_the_tools()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        Assert.Equal("2026-07-28", client.NegotiatedProtocolVersion);
        var tools = (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToHashSet();
        foreach (var expected in new[]
        {
            "repo_status", "recent_commits", "diff_summary", "run_checks", "scan_secrets", "audit_dependencies", "poll_events",
        })
        {
            Assert.Contains(expected, tools);
        }
    }

    [Fact]
    public async Task Tool_calls_work_over_http()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var result = await client.CallToolAsync("repo_status", cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
    }

    [Fact]
    public async Task Skills_list_get_and_the_skill_md_resource_agree_on_the_digest()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        const string uri = "skill://guardian/SKILL.md";

        var list = await RequestAsync(client, "skills/list");
        Assert.Contains(list["skills"]!.AsArray(), s => (string?)s!["uri"] == uri);

        var get = await RequestAsync(client, "skills/get", new JsonObject { ["uri"] = uri });
        Assert.Equal(uri, (string?)get["skill"]!["uri"]);
        var manifest = get["skill"]!["resources"]!.AsArray().Single(r => (string?)r!["uri"] == uri)!;

        var read = await client.ReadResourceAsync(new Uri(uri), cancellationToken: Ct);
        var text = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text;
        var bytes = Encoding.UTF8.GetBytes(text);
        Assert.Equal((string?)manifest["digest"], "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.Equal((long?)manifest["size"], bytes.Length);
    }

    [Fact]
    public async Task Events_poll_round_trips_a_published_event()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        var cursor = (string)(await RequestAsync(client, "events/poll", new JsonObject { ["name"] = "repo.commit.created" }))["cursor"]!;

        await Publish(host, new JsonObject { ["n"] = 1 });
        var poll = await RequestAsync(client, "events/poll", new JsonObject { ["name"] = "repo.commit.created", ["cursor"] = cursor });

        var received = Assert.Single(poll["events"]!.AsArray())!;
        Assert.Equal(1, (int)received["data"]!["n"]!);
    }

    [Fact]
    public async Task Events_stream_delivers_active_then_events_and_stops_after_the_client_cancels()
    {
        await using var host = await StartAsync();
        await using var client = await host.ConnectAsync(cancellationToken: Ct);
        var notes = new Notes(client);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var stream = client.SendRequestAsync(
            new JsonRpcRequest { Id = new RequestId("s-1"), Method = "events/stream", Params = new JsonObject { ["name"] = "repo.commit.created" } },
            cancel.Token);

        await notes.WaitAsync(n => n.Any(x => x.Method == "notifications/events/active"));
        await Publish(host, new JsonObject { ["n"] = 1 });
        await notes.WaitAsync(n => n.Count(x => x.Method == "notifications/events/event") == 1);
        Assert.Equal("notifications/events/active", notes.Snapshot().First().Method);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);

        // Give the server time to observe the closed stream, then publish: nothing may be delivered any more.
        await Task.Delay(500, Ct);
        await Publish(host, new JsonObject { ["n"] = 2 });
        await Task.Delay(500, Ct);
        Assert.Single(notes.Snapshot(), n => n.Method == "notifications/events/event");
    }

    [Fact]
    public async Task Events_stream_receives_the_final_complete_frame_when_the_server_closes_it()
    {
        var host = await StartAsync();
        try
        {
            await using var client = await host.ConnectAsync(cancellationToken: Ct);
            var notes = new Notes(client);
            var stream = client.SendRequestAsync(
                new JsonRpcRequest { Id = new RequestId("s-1"), Method = "events/stream", Params = new JsonObject { ["name"] = "repo.commit.created" } },
                Ct);
            await notes.WaitAsync(n => n.Any(x => x.Method == "notifications/events/active"));

            await host.StopServerAsync();

            var done = await Task.WhenAny(stream, Task.Delay(Generous, Ct));
            Assert.Same(stream, done);
            var result = (await stream).Result!.AsObject();
            Assert.Equal("complete", (string?)result["resultType"]);
            Assert.NotNull(result["_meta"]);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("http://0.0.0.0:5199")]
    [InlineData("http://192.0.2.1:5199")]
    [InlineData("http://example.com:5199")]
    public void Non_loopback_url_is_refused_without_HttpAllowRemote(string url)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => HttpHost.Build(["--urls", url, "--repo", _repo.Path]));

        Assert.Contains("HttpAllowRemote", exception.Message);
    }

    [Theory]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://localhost:0")]
    [InlineData("http://[::1]:0")]
    public async Task Loopback_urls_are_accepted(string url)
    {
        await using var app = HttpHost.Build(["--urls", url, "--repo", _repo.Path, "--no-watch", "--Logging:LogLevel:Default=None"]);

        Assert.NotNull(app);
    }

    [Fact]
    public async Task Non_loopback_url_is_accepted_with_HttpAllowRemote()
    {
        await using var app = HttpHost.Build(
            ["--urls", "http://0.0.0.0:0", "--Guardian:HttpAllowRemote=true", "--repo", _repo.Path, "--no-watch", "--Logging:LogLevel:Default=None"]);

        Assert.NotNull(app);
    }

    [Fact]
    public async Task Build_forces_the_Http_transport_whatever_the_configuration_says()
    {
        await using var app = HttpHost.Build(
            ["--transport", "Stdio", "--urls", "http://127.0.0.1:0", "--repo", _repo.Path, "--no-watch", "--Logging:LogLevel:Default=None"]);

        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<GuardianOptions>>().Value;
        Assert.Equal(GuardianTransport.Http, options.Transport);
    }

    private static ValueTask<EventEnvelope> Publish(GuardianHttpTestHost host, JsonObject data) =>
        host.Services.GetRequiredService<IEventPublisher>().PublishAsync("repo.commit.created", data, cancellationToken: Ct);

    private sealed record Note(string Method, JsonObject Params);

    private sealed class Notes
    {
        private readonly ConcurrentQueue<Note> _notes = new();

        public Notes(McpClient client)
        {
            foreach (var method in new[] { "notifications/events/active", "notifications/events/event", "notifications/events/heartbeat" })
            {
                client.RegisterNotificationHandler(method, (n, _) =>
                {
                    _notes.Enqueue(new Note(method, (JsonObject)n.Params!.DeepClone()));
                    return ValueTask.CompletedTask;
                });
            }
        }

        public IReadOnlyList<Note> Snapshot() => _notes.ToList();

        public async Task WaitAsync(Func<IReadOnlyList<Note>, bool> condition)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(Generous);
            while (!condition(Snapshot()))
            {
                await Task.Delay(10, timeout.Token);
            }
        }
    }
}
