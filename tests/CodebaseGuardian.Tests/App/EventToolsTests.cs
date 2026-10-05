using System.Text.Json;
using CodebaseGuardian.Tests.Infrastructure;
using Mcp.Events;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json.Nodes;

namespace CodebaseGuardian.Tests.App;

public class EventToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task Publish(InProcessMcpServer server, string name, JsonObject data) =>
        server.Services.GetRequiredService<IEventPublisher>().PublishAsync(name, data, cancellationToken: Ct).AsTask();

    private static Task<CallToolResult> PollAsync(InProcessMcpServer server, Dictionary<string, object?>? arguments = null) =>
        server.Client.CallToolAsync("poll_events", arguments, cancellationToken: Ct).AsTask();

    private static JsonElement Structured(CallToolResult result) => Assert.IsType<JsonElement>(result.StructuredContent);

    [Fact]
    public async Task Tool_is_listed_read_only_and_tells_the_model_how_to_use_the_cursor()
    {
        using var repo = TempGitRepo.Create();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var tool = Assert.Single(await server.Client.ListToolsAsync(cancellationToken: Ct), t => t.Name == "poll_events");
        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tool.ProtocolTool.Annotations?.IdempotentHint);
        Assert.Contains("cannot subscribe to MCP events", tool.Description);
        Assert.Contains("pass the returned cursor next time", tool.Description);
    }

    [Fact]
    public async Task Without_a_cursor_it_returns_the_newest_events_and_with_the_cursor_only_later_ones()
    {
        using var repo = TempGitRepo.Create();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);
        for (var i = 1; i <= 3; i++)
        {
            await Publish(server, "repo.files.changed", new JsonObject { ["paths"] = new JsonArray($"f{i}.txt"), ["count"] = 1 });
        }

        var first = Structured(await PollAsync(server, new() { ["maxEvents"] = 2 }));
        var firstEvents = first.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(["f2.txt", "f3.txt"], firstEvents.Select(e => e.GetProperty("data").GetProperty("paths")[0].GetString()).ToList());
        Assert.Equal("repo.files.changed", firstEvents[0].GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(firstEvents[0].GetProperty("eventId").GetString()));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", firstEvents[0].GetProperty("timestamp").GetString()!);
        var cursor = first.GetProperty("cursor").GetString();

        await Publish(server, "repo.files.changed", new JsonObject { ["paths"] = new JsonArray("f4.txt"), ["count"] = 1 });
        var second = Structured(await PollAsync(server, new() { ["cursor"] = cursor }));
        var path = Assert.Single(second.GetProperty("events").EnumerateArray()).GetProperty("data").GetProperty("paths")[0];
        Assert.Equal("f4.txt", path.GetString());
        Assert.False(second.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Names_filter_the_result()
    {
        using var repo = TempGitRepo.Create();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);
        await Publish(server, "repo.files.changed", new JsonObject { ["paths"] = new JsonArray("a"), ["count"] = 1 });
        await Publish(server, "repo.branch.changed", new JsonObject { ["from"] = "a", ["to"] = "b", ["headSha"] = null });

        var result = Structured(await PollAsync(server, new() { ["names"] = new[] { "repo.branch.changed" } }));
        Assert.Equal("repo.branch.changed", Assert.Single(result.GetProperty("events").EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Unknown_event_name_is_a_tool_error()
    {
        using var repo = TempGitRepo.Create();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var result = await PollAsync(server, new() { ["names"] = new[] { "repo.nope" } });
        Assert.True(result.IsError);
        Assert.Contains("repo.nope", string.Join(' ', result.Content.OfType<TextContentBlock>().Select(b => b.Text)));
    }

    [Fact]
    public async Task Invalid_cursor_is_a_tool_error()
    {
        using var repo = TempGitRepo.Create();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var result = await PollAsync(server, new() { ["cursor"] = "%%%not-a-cursor" });
        Assert.True(result.IsError);
    }
}
