using System.Text.Json;
using System.Text.Json.Nodes;
using CodebaseGuardian.Processes;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace CodebaseGuardian.Tests.App;

public class ScanToolsTests
{
    private const string TasksExtension = "io.modelcontextprotocol/tasks";
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

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

    private static FakeProcessRunner PassingChecks() =>
        new FakeProcessRunner(new ProcessRunner()).On(IsCheck, FakeProcessRunner.Result(0, "Passed!  - Failed: 0, Passed: 3, Total: 3\n"));

    private static async Task<CompletedTaskResult> WaitForCompletedAsync(McpClient client, string taskId)
    {
        var deadline = DateTime.UtcNow + Generous;
        while (true)
        {
            var task = await client.GetTaskAsync(taskId, Ct);
            if (task is not WorkingTaskResult)
            {
                return Assert.IsType<CompletedTaskResult>(task);
            }

            Assert.True(DateTime.UtcNow < deadline, "the task did not finish in time");
            await Task.Delay(50, Ct);
        }
    }

    private static void AssertStructured(JsonElement structured)
    {
        Assert.Equal(
            ["checksPassed", "durationMs", "outdatedPackages", "reportUri", "scanId", "secretFindings", "vulnerablePackages"],
            structured.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Matches(@"^scan_\d{14}_[0-9a-f]{4}$", structured.GetProperty("scanId").GetString());
        Assert.Equal($"guardian://scans/{structured.GetProperty("scanId").GetString()}/report", structured.GetProperty("reportUri").GetString());
        Assert.Equal(0, structured.GetProperty("secretFindings").GetInt32());
        Assert.Equal(0, structured.GetProperty("vulnerablePackages").GetInt32());
        Assert.Equal(0, structured.GetProperty("outdatedPackages").GetInt32());
        Assert.True(structured.GetProperty("checksPassed").GetBoolean());
        Assert.True(structured.GetProperty("durationMs").GetInt64() >= 0);
    }

    [Fact]
    public async Task Full_scan_as_a_task_completes_and_its_report_resource_is_markdown()
    {
        using var repo = SolutionRepo();
        await using var server = await GuardianTestHost.StartAsync(
            repo.Path, null, s => s.AddSingleton<IProcessRunner>(PassingChecks()), TasksClient(), Ct);

        var created = await server.Client.CallToolAsTaskAsync(new CallToolRequestParams { Name = "full_scan" }, Ct);

        Assert.True(created.IsTask);
        var done = await WaitForCompletedAsync(server.Client, created.TaskCreated!.TaskId);
        var structured = done.Result.GetProperty("structuredContent");
        AssertStructured(structured);
        Assert.Contains("secret", done.Result.GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.OrdinalIgnoreCase);

        var read = await server.Client.ReadResourceAsync(structured.GetProperty("reportUri").GetString()!, cancellationToken: Ct);
        var text = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));
        Assert.Equal("text/markdown", text.MimeType);
        Assert.StartsWith("# Guardian scan scan_", text.Text);
    }

    [Fact]
    public async Task Without_checks_the_structured_result_carries_an_explicit_null_and_the_output_schema_allows_it()
    {
        using var repo = SolutionRepo();
        await using var server = await GuardianTestHost.StartAsync(
            repo.Path, null, s => s.AddSingleton<IProcessRunner>(PassingChecks()), TasksClient(), Ct);

        var created = await server.Client.CallToolAsTaskAsync(
            new CallToolRequestParams { Name = "full_scan", Arguments = new Dictionary<string, JsonElement> { ["includeChecks"] = JsonSerializer.SerializeToElement(false) } }, Ct);
        var done = await WaitForCompletedAsync(server.Client, created.TaskCreated!.TaskId);
        var structured = done.Result.GetProperty("structuredContent");

        Assert.Equal(JsonValueKind.Null, structured.GetProperty("checksPassed").ValueKind);

        var tool = Assert.Single(await server.Client.ListToolsAsync(cancellationToken: Ct), t => t.Name == "full_scan");
        var schema = tool.ProtocolTool.OutputSchema!.Value;
        var checksType = schema.GetProperty("properties").GetProperty("checksPassed").GetProperty("type");
        Assert.Contains("null", checksType.EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(
            structured.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal),
            schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_client_without_the_tasks_extension_gets_missing_required_client_capability()
    {
        using var repo = SolutionRepo();
        await using var server = await GuardianTestHost.StartAsync(
            repo.Path, null, s => s.AddSingleton<IProcessRunner>(PassingChecks()), null, Ct);

        var error = await Assert.ThrowsAnyAsync<McpProtocolException>(() => server.Client.CallToolAsync("full_scan", cancellationToken: Ct).AsTask());

        Assert.Equal(-32021, (int)error.ErrorCode);
    }

    [Fact]
    public async Task The_tool_is_annotated_and_the_template_is_listed_and_unknown_ids_are_invalid_params()
    {
        using var repo = SolutionRepo();
        await using var server = await GuardianTestHost.StartAsync(
            repo.Path, null, s => s.AddSingleton<IProcessRunner>(PassingChecks()), TasksClient(), Ct);

        var tool = Assert.Single(await server.Client.ListToolsAsync(cancellationToken: Ct), t => t.Name == "full_scan");
        var annotations = tool.ProtocolTool.Annotations!;
        Assert.False(annotations.ReadOnlyHint);
        Assert.False(annotations.DestructiveHint);
        Assert.False(annotations.IdempotentHint);
        Assert.True(annotations.OpenWorldHint);
        Assert.Contains("clients without task support should call `scan_secrets`, `audit_dependencies` and `run_checks` instead", tool.Description);
        Assert.True(tool.JsonSchema.GetProperty("properties").TryGetProperty("includeChecks", out _));

        var templates = await server.Client.ListResourceTemplatesAsync(cancellationToken: Ct);
        var template = Assert.Single(templates, t => t.UriTemplate == "guardian://scans/{scanId}/report");
        Assert.Equal("text/markdown", template.MimeType);

        var error = await Assert.ThrowsAsync<McpProtocolException>(
            () => server.Client.ReadResourceAsync("guardian://scans/scan_nope/report", cancellationToken: Ct).AsTask());
        Assert.Equal(-32602, (int)error.ErrorCode);
    }

    [Fact]
    public async Task Over_stateless_http_full_scan_as_a_task_completes()
    {
        using var repo = SolutionRepo();
        await using var host = await GuardianHttpTestHost.StartAsync(repo.Path, null, s => s.AddSingleton<IProcessRunner>(PassingChecks()), Ct);
        var client = await host.ConnectAsync(TasksClient(), Ct);

        var created = await client.CallToolAsTaskAsync(new CallToolRequestParams { Name = "full_scan" }, Ct);

        Assert.True(created.IsTask);
        var done = await WaitForCompletedAsync(client, created.TaskCreated!.TaskId);
        var structured = done.Result.GetProperty("structuredContent");
        AssertStructured(structured);
        var read = await client.ReadResourceAsync(structured.GetProperty("reportUri").GetString()!, cancellationToken: Ct);
        Assert.StartsWith("# Guardian scan", Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text);
    }
}
