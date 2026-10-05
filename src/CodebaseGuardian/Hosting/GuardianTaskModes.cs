using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodebaseGuardian.Hosting;

public static class GuardianTaskModes
{
    /// <summary>
    /// <c>run_checks</c> and <c>audit_dependencies</c> may run as tasks, <c>full_scan</c> must; everything else is synchronous.
    /// <c>create_issue</c>, <c>comment_on_pr</c> and <c>open_pull_request</c> MUST stay synchronous: SDK 2.2.0 fails MRTR inside
    /// tasks ("MRTR and tasks cannot be composed via [McpServerTool] yet").
    /// </summary>
    public static McpTaskExecutionMode Select(RequestContext<CallToolRequestParams> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Params?.Name switch
        {
            "run_checks" or "audit_dependencies" => McpTaskExecutionMode.Optional,
            "full_scan" => McpTaskExecutionMode.Required,
            _ => McpTaskExecutionMode.Synchronous,
        };
    }
}
