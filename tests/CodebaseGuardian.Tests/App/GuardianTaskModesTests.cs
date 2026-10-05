using CodebaseGuardian.Hosting;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodebaseGuardian.Tests.App;

public class GuardianTaskModesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("run_checks", McpTaskExecutionMode.Optional)]
    [InlineData("audit_dependencies", McpTaskExecutionMode.Optional)]
    [InlineData("full_scan", McpTaskExecutionMode.Required)]
    [InlineData("repo_status", McpTaskExecutionMode.Synchronous)]
    [InlineData("recent_commits", McpTaskExecutionMode.Synchronous)]
    [InlineData("diff_summary", McpTaskExecutionMode.Synchronous)]
    [InlineData("scan_secrets", McpTaskExecutionMode.Synchronous)]
    [InlineData("poll_events", McpTaskExecutionMode.Synchronous)]
    // MRTR elicitation inside a task fails in SDK 2.2.0, so the GitHub action tools stay synchronous.
    [InlineData("create_issue", McpTaskExecutionMode.Synchronous)]
    [InlineData("comment_on_pr", McpTaskExecutionMode.Synchronous)]
    [InlineData("open_pull_request", McpTaskExecutionMode.Synchronous)]
    [InlineData("no_such_tool", McpTaskExecutionMode.Synchronous)]
    public async Task Each_tool_gets_its_execution_mode(string toolName, McpTaskExecutionMode expected)
    {
        await using var host = await InProcessMcpServer.StartAsync(cancellationToken: Ct);
        var server = host.Services.GetRequiredService<McpServer>();
        var context = new RequestContext<CallToolRequestParams>(
            server, new JsonRpcRequest { Method = "tools/call" }, new CallToolRequestParams { Name = toolName });

        Assert.Equal(expected, GuardianTaskModes.Select(context));
    }
}
