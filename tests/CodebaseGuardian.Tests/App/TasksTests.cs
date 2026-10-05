using System.Text.Json;
using System.Text.Json.Nodes;
using CodebaseGuardian.Processes;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace CodebaseGuardian.Tests.App;

public class TasksTests
{
    private const string TasksExtension = "io.modelcontextprotocol/tasks";
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static McpClientOptions TasksClient() => new()
    {
        Capabilities = new ClientCapabilities { Extensions = new Dictionary<string, object> { [TasksExtension] = new JsonObject() } },
    };

    private static TempGitRepo SolutionRepo()
    {
        var repo = TempGitRepo.Create();
        repo.WriteFile("App.slnx", "");
        repo.WriteFile("README.md", "x");
        repo.Commit("initial");
        return repo;
    }

    private static bool IsCheck(ProcessSpec spec) => spec.FileName == "dotnet" && spec.Arguments.Contains("test");

    private static Task<InProcessMcpServer> StartAsync(TempGitRepo repo, IProcessRunner fake, McpClientOptions? client) =>
        GuardianTestHost.StartAsync(repo.Path, null, s => s.AddSingleton<IProcessRunner>(fake), client, Ct);

    private static async Task<GetTaskResult> WaitForTerminalAsync(McpClient client, string taskId)
    {
        var deadline = DateTime.UtcNow + Generous;
        while (true)
        {
            var task = await client.GetTaskAsync(taskId, Ct);
            if (task is not WorkingTaskResult)
            {
                return task;
            }

            Assert.True(DateTime.UtcNow < deadline, "the task did not leave the working state in time");
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task The_server_advertises_the_tasks_extension()
    {
        using var repo = SolutionRepo();
        await using var server = await StartAsync(repo, new FakeProcessRunner(new ProcessRunner()), TasksClient());

        Assert.True(server.Client.ServerCapabilities.Extensions?.ContainsKey(TasksExtension));
    }

    [Fact]
    public async Task Run_checks_as_a_task_completes_with_the_same_structured_result_as_a_synchronous_call()
    {
        using var repo = SolutionRepo();
        var fake = new FakeProcessRunner(new ProcessRunner()).On(IsCheck, FakeProcessRunner.Result(0, "Passed!  - Failed: 0, Passed: 3, Total: 3\n"));
        await using var server = await StartAsync(repo, fake, TasksClient());
        var cursor = await EventsPolling.GetCursorAsync(server, Ct);

        var created = await server.Client.CallToolAsTaskAsync(new CallToolRequestParams { Name = "run_checks" }, Ct);

        Assert.True(created.IsTask);
        var done = Assert.IsType<CompletedTaskResult>(await WaitForTerminalAsync(server.Client, created.TaskCreated!.TaskId));
        var structured = done.Result.GetProperty("structuredContent");
        Assert.True(structured.GetProperty("passed").GetBoolean());
        Assert.Equal(0, structured.GetProperty("exitCode").GetInt32());
        Assert.Matches(@"^run_\d{14}_[0-9a-f]{4}$", structured.GetProperty("runId").GetString());
        Assert.StartsWith("guardian://checks/", structured.GetProperty("logUri").GetString());
        await EventsPolling.WaitForAsync(server, "checks.completed", cursor, null, Generous, Ct);

        // The same fields as the synchronous result (see CheckToolsTests), and no log body.
        Assert.Equal(
            ["command", "durationMs", "exitCode", "failedTests", "logUri", "passed", "runId", "summary", "timedOut"],
            structured.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task A_client_without_the_tasks_extension_gets_a_plain_synchronous_result()
    {
        using var repo = SolutionRepo();
        var fake = new FakeProcessRunner(new ProcessRunner()).On(IsCheck, FakeProcessRunner.Result(0));
        await using var server = await StartAsync(repo, fake, null);

        var result = await server.Client.CallToolAsync("run_checks", cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        Assert.True(result.StructuredContent!.Value.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task Audit_dependencies_as_a_task_completes_with_the_report_including_nulls()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("package.json", "{}");
        repo.Commit("initial");
        var fake = new FakeProcessRunner(new ProcessRunner())
            .On(s => s.FileName == "npm" && s.Arguments.Contains("audit"), FakeProcessRunner.Result(0, "{\"vulnerabilities\":{}}"))
            .On(s => s.FileName == "npm" && s.Arguments.Contains("outdated"), FakeProcessRunner.Result(0, "{}"));
        await using var server = await StartAsync(repo, fake, TasksClient());

        var created = await server.Client.CallToolAsTaskAsync(new CallToolRequestParams { Name = "audit_dependencies" }, Ct);

        Assert.True(created.IsTask);
        var done = Assert.IsType<CompletedTaskResult>(await WaitForTerminalAsync(server.Client, created.TaskCreated!.TaskId));
        var ecosystem = done.Result.GetProperty("structuredContent").GetProperty("ecosystems")[0];
        Assert.Equal("npm", ecosystem.GetProperty("ecosystem").GetString());
        Assert.Equal("ok", ecosystem.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, ecosystem.GetProperty("reason").ValueKind);
    }

    [Fact]
    public async Task Cancelling_a_check_task_kills_the_run_stores_nothing_and_releases_the_lock()
    {
        using var repo = SolutionRepo();
        var blocked = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeProcessRunner(new ProcessRunner()).OnBlocking(IsCheck, blocked);
        var recording = new TokenRecordingRunner(fake);
        await using var server = await StartAsync(repo, recording, TasksClient());
        var cursor = await EventsPolling.GetCursorAsync(server, Ct);

        var created = await server.Client.CallToolAsTaskAsync(new CallToolRequestParams { Name = "run_checks" }, Ct);
        var taskId = created.TaskCreated!.TaskId;
        await WaitUntilAsync(() => fake.Calls.Any(IsCheck));
        Assert.True(recording.CheckToken.CanBeCanceled);

        await server.Client.CancelTaskAsync(taskId, Ct);

        Assert.IsType<CancelledTaskResult>(await WaitForTerminalAsync(server.Client, taskId));
        await WaitUntilAsync(() => recording.CheckToken.IsCancellationRequested);
        await Task.Delay(500, Ct);
        var (events, _) = await EventsPolling.PollAsync(server, "checks.completed", cursor, Ct);
        Assert.Empty(events);
        Assert.Null(server.Services.GetRequiredService<CodebaseGuardian.Checks.CheckRunStore>().Latest);

        // The lock is free: a following synchronous run (the fake now completes at once) finishes.
        blocked.SetResult(FakeProcessRunner.Result(0));
        var next = await server.Client.CallToolAsync("run_checks", cancellationToken: Ct).AsTask().WaitAsync(Generous, Ct);
        Assert.NotEqual(true, next.IsError);
    }

    [Fact]
    public async Task Over_stateless_http_a_task_created_by_one_request_completes_through_later_requests()
    {
        using var repo = SolutionRepo();
        var fake = new FakeProcessRunner(new ProcessRunner()).On(IsCheck, FakeProcessRunner.Result(0));
        await using var host = await GuardianHttpTestHost.StartAsync(repo.Path, null, s => s.AddSingleton<IProcessRunner>(fake), Ct);
        var client = await host.ConnectAsync(TasksClient(), Ct);

        var created = await client.CallToolAsTaskAsync(new CallToolRequestParams { Name = "run_checks" }, Ct);

        Assert.True(created.IsTask);
        var done = Assert.IsType<CompletedTaskResult>(await WaitForTerminalAsync(client, created.TaskCreated!.TaskId));
        Assert.True(done.Result.GetProperty("structuredContent").GetProperty("passed").GetBoolean());
    }

    private sealed class TokenRecordingRunner(FakeProcessRunner inner) : IProcessRunner
    {
        private readonly TaskCompletionSource<CancellationToken> _checkToken = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken CheckToken => _checkToken.Task.IsCompletedSuccessfully ? _checkToken.Task.Result : default;

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
        {
            if (IsCheck(spec))
            {
                _checkToken.TrySetResult(cancellationToken);
            }

            return inner.RunAsync(spec, cancellationToken);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Generous;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(25, Ct);
        }
    }
}
