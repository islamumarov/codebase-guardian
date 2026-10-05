using CodebaseGuardian.Git;
using ModelContextProtocol;

namespace CodebaseGuardian.Tools;

/// <summary>
/// Turns expected failures (bad arguments, git failures) into MCP tool errors: the SDK converts a thrown
/// <see cref="McpException"/> into a result with <c>isError: true</c> and the exception message as its text,
/// so the model can read it and retry. Any other exception stays unexpected (a protocol error).
/// Every tool wraps its body in <see cref="RunAsync{T}"/>.
/// </summary>
public static class ToolErrors
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            return await action();
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (GitException ex)
        {
            var detail = ex.StandardError.Trim();
            throw new McpException(detail.Length == 0 ? ex.Message : $"{ex.Message} {detail}", ex);
        }
    }
}
