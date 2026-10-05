using System.Text.Json;
using CodebaseGuardian.Checks;
using CodebaseGuardian.Processes;
using CodebaseGuardian.Tests.Infrastructure;
using CodebaseGuardian.Watching;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodebaseGuardian.Tests.App;

public class CheckToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<InProcessMcpServer> StartAsync(
        TempGitRepo repo, FakeProcessRunner fake, Dictionary<string, string?>? configuration = null) =>
        GuardianTestHost.StartAsync(repo.Path, configuration, s => s.AddSingleton<IProcessRunner>(fake), cancellationToken: Ct);

    private static TempGitRepo SolutionRepo()
    {
        var repo = TempGitRepo.Create();
        repo.WriteFile("App.slnx", "");
        repo.WriteFile("README.md", "x");
        repo.Commit("initial");
        return repo;
    }

    private static string TextOf(CallToolResult result) =>
        string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public async Task Run_checks_is_listed_as_a_non_read_only_closed_world_tool_without_arguments()
    {
        using var repo = SolutionRepo();
        await using var server = await StartAsync(repo, new FakeProcessRunner(new ProcessRunner()));

        var tool = Assert.Single(await server.Client.ListToolsAsync(cancellationToken: Ct), t => t.Name == "run_checks");

        Assert.False(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(tool.ProtocolTool.Annotations?.DestructiveHint);
        Assert.False(tool.ProtocolTool.Annotations?.OpenWorldHint);
        Assert.Empty(tool.JsonSchema.GetProperty("properties").EnumerateObject());
    }

    [Fact]
    public async Task Run_checks_returns_the_structured_result()
    {
        using var repo = SolutionRepo();
        var fake = new FakeProcessRunner(new ProcessRunner())
            .On(s => s.FileName == "dotnet", FakeProcessRunner.Result(1, "  Failed Demo.T.One [2 ms]\nFailed!  - Failed: 1, Passed: 0, Total: 1\n"));
        await using var server = await StartAsync(repo, fake);

        var result = await server.Client.CallToolAsync("run_checks", cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        var json = result.StructuredContent!.Value;
        Assert.Matches(@"^run_\d{14}_[0-9a-f]{4}$", json.GetProperty("runId").GetString());
        Assert.Equal("dotnet test App.slnx --nologo", json.GetProperty("command").GetString());
        Assert.False(json.GetProperty("passed").GetBoolean());
        Assert.Equal(1, json.GetProperty("exitCode").GetInt32());
        Assert.False(json.GetProperty("timedOut").GetBoolean());
        Assert.True(json.GetProperty("durationMs").GetInt64() >= 0);
        Assert.StartsWith("Failed!", json.GetProperty("summary").GetString());
        Assert.Equal("Demo.T.One", json.GetProperty("failedTests")[0].GetString());
        Assert.Equal($"guardian://checks/{json.GetProperty("runId").GetString()}/log", json.GetProperty("logUri").GetString());
        Assert.False(json.TryGetProperty("log", out _));
    }

    [Fact]
    public async Task Run_checks_reports_progress_while_the_check_is_running()
    {
        using var repo = SolutionRepo();
        var release = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeProcessRunner(new ProcessRunner()).OnBlocking(s => s.FileName == "dotnet", release);
        await using var server = await StartAsync(repo, fake);
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string?>();

        var call = server.Client.CallToolAsync("run_checks", progress: new SyncProgress(messages), cancellationToken: Ct).AsTask();

        // The client only listens to progress while the call is outstanding, so the check is held until the message arrives.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!messages.Contains("running dotnet test App.slnx --nologo") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        release.SetResult(FakeProcessRunner.Result(0));
        await call;
        Assert.Contains("running dotnet test App.slnx --nologo", messages);
    }

    private sealed class SyncProgress(System.Collections.Concurrent.ConcurrentQueue<string?> messages) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value) => messages.Enqueue(value.Message);
    }

    [Fact]
    public async Task Run_checks_without_a_command_is_a_tool_error()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("README.md", "x");
        repo.Commit("initial");
        await using var server = await StartAsync(repo, new FakeProcessRunner(new ProcessRunner()));

        var result = await server.Client.CallToolAsync("run_checks", cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("No check command configured or detected. Set Guardian:Checks:Command.", TextOf(result));
    }

    [Fact]
    public async Task A_command_from_configuration_is_used()
    {
        using var repo = SolutionRepo();
        var fake = new FakeProcessRunner(new ProcessRunner()).On(s => s.FileName == "make", FakeProcessRunner.Result(0));
        await using var server = await StartAsync(repo, fake, new()
        {
            ["Guardian:Checks:Command"] = "make",
            ["Guardian:Checks:Arguments:0"] = "check",
        });

        var result = await server.Client.CallToolAsync("run_checks", cancellationToken: Ct);

        Assert.Equal("make check", result.StructuredContent!.Value.GetProperty("command").GetString());
    }

    [Fact]
    public async Task Resources_expose_the_latest_run_and_its_log()
    {
        using var repo = SolutionRepo();
        var fake = new FakeProcessRunner(new ProcessRunner())
            .On(s => s.FileName == "dotnet", FakeProcessRunner.Result(1, "out line\nFailed!  - Failed: 1\n", "err line"));
        await using var server = await StartAsync(repo, fake);

        var tool = await server.Client.CallToolAsync("run_checks", cancellationToken: Ct);
        var runId = tool.StructuredContent!.Value.GetProperty("runId").GetString();

        var latest = Assert.IsType<TextResourceContents>(Assert.Single(
            (await server.Client.ReadResourceAsync("guardian://checks/latest", cancellationToken: Ct)).Contents));
        Assert.Equal("application/json", latest.MimeType);
        using var json = JsonDocument.Parse(latest.Text);
        Assert.Equal(runId, json.RootElement.GetProperty("runId").GetString());
        Assert.False(json.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal("tool", json.RootElement.GetProperty("trigger").GetString());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", json.RootElement.GetProperty("startedAt").GetString());
        Assert.False(json.RootElement.TryGetProperty("log", out _));
        Assert.False(json.RootElement.TryGetProperty("commitSha", out _));

        var log = Assert.IsType<TextResourceContents>(Assert.Single(
            (await server.Client.ReadResourceAsync($"guardian://checks/{runId}/log", cancellationToken: Ct)).Contents));
        Assert.Equal("text/plain", log.MimeType);
        Assert.Equal("out line\nFailed!  - Failed: 1\n\nerr line", log.Text);
    }

    [Fact]
    public async Task Resources_are_listed_with_their_mime_types()
    {
        using var repo = SolutionRepo();
        await using var server = await StartAsync(repo, new FakeProcessRunner(new ProcessRunner()));

        var resource = Assert.Single(await server.Client.ListResourcesAsync(cancellationToken: Ct), r => r.Uri == "guardian://checks/latest");
        var template = Assert.Single(await server.Client.ListResourceTemplatesAsync(cancellationToken: Ct), t => t.UriTemplate == "guardian://checks/{runId}/log");

        Assert.Equal("application/json", resource.MimeType);
        Assert.Equal("text/plain", template.MimeType);
    }

    [Fact]
    public async Task Reading_resources_without_a_matching_run_is_invalid_params()
    {
        using var repo = SolutionRepo();
        await using var server = await StartAsync(repo, new FakeProcessRunner(new ProcessRunner()));

        var none = await Assert.ThrowsAsync<McpProtocolException>(() =>
            server.Client.ReadResourceAsync("guardian://checks/latest", cancellationToken: Ct).AsTask());
        var unknown = await Assert.ThrowsAsync<McpProtocolException>(() =>
            server.Client.ReadResourceAsync("guardian://checks/run_nope/log", cancellationToken: Ct).AsTask());

        Assert.Equal(McpErrorCode.InvalidParams, none.ErrorCode);
        Assert.Contains("No check runs yet", none.Message);
        Assert.Equal(McpErrorCode.InvalidParams, unknown.ErrorCode);
    }

    [Fact]
    public async Task Auto_checks_run_once_for_a_new_commit_with_the_commit_trigger()
    {
        using var repo = SolutionRepo();
        var fake = new FakeProcessRunner(new ProcessRunner()).On(s => s.FileName == "dotnet", FakeProcessRunner.Result(0, "Passed!\n"));
        await using var server = await StartAsync(repo, fake, new()
        {
            ["Guardian:AutoChecks"] = "true",
            ["Guardian:WatchEnabled"] = "true",
            ["Guardian:WatchIntervalMs"] = "100",
        });
        var store = server.Services.GetRequiredService<CheckRunStore>();

        repo.WriteFile("a.txt", "a");
        repo.Commit("one");
        repo.WriteFile("b.txt", "b");
        var newest = repo.Commit("two");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (store.Latest?.CommitSha != newest && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        Assert.Equal(newest, store.Latest?.CommitSha);
        Assert.Equal("commit", store.Latest!.Trigger);
    }

    [Fact]
    public async Task Auto_checks_do_not_stall_the_watcher_while_a_check_is_running()
    {
        using var repo = SolutionRepo();
        var release = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeProcessRunner(new ProcessRunner()).OnBlocking(s => s.FileName == "dotnet", release);
        await using var server = await StartAsync(repo, fake, new()
        {
            ["Guardian:AutoChecks"] = "true",
            ["Guardian:WatchEnabled"] = "true",
            ["Guardian:WatchIntervalMs"] = "100",
        });
        var cursor = await EventsPolling.GetCursorAsync(server, Ct);

        repo.WriteFile("a.txt", "a");
        repo.Commit("one");
        await WaitUntilAsync(() => fake.Calls.Count(c => c.FileName == "dotnet") == 1);

        // The first check is still blocked; the next commit must be announced regardless.
        repo.WriteFile("b.txt", "b");
        var second = repo.Commit("two");
        await EventsPolling.WaitForAsync(server, "repo.commit.created", cursor, d => (string?)d["sha"] == second, EventsPolling.DefaultTimeout, Ct);
        Assert.Equal(1, fake.Calls.Count(c => c.FileName == "dotnet"));

        release.SetResult(FakeProcessRunner.Result(0));
        var store = server.Services.GetRequiredService<CheckRunStore>();
        await WaitUntilAsync(() => store.Latest?.CommitSha == second);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task Auto_checks_are_off_by_default()
    {
        using var repo = SolutionRepo();
        await using var server = await StartAsync(repo, new FakeProcessRunner(new ProcessRunner()), new()
        {
            ["Guardian:WatchEnabled"] = "true",
            ["Guardian:WatchIntervalMs"] = "100",
        });

        Assert.DoesNotContain(server.Services.GetServices<IRepositoryChangeHandler>(), h => h is AutoChecksCommitHandler);
    }
}
