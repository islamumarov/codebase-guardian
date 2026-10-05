using System.ComponentModel;
using CodebaseGuardian.Checks;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CodebaseGuardian.Tools;

/// <summary>Result of <c>run_checks</c>; the full log is behind <paramref name="LogUri"/>.</summary>
public sealed record CheckRunResult(
    string RunId, string Command, bool Passed, int ExitCode, bool TimedOut, long DurationMs,
    string Summary, IReadOnlyList<string> FailedTests, string LogUri)
{
    public static CheckRunResult From(CheckRun run) => new(
        run.RunId, run.Command, run.Passed, run.ExitCode, run.TimedOut, (long)run.Duration.TotalMilliseconds,
        run.Summary, run.FailedTests, run.LogUri);
}

[McpServerToolType]
public sealed class CheckTools(ICheckRunner runner)
{
    [McpServerTool(Name = "run_checks", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Runs the project's build and test command (configured by the server operator, or detected from the repository) and returns the result: pass or fail, a one-line summary, the names of failed tests and a guardian:// link to the full log. Use it after changing code, or to find out whether the project currently builds and its tests pass. Runs one at a time and can take minutes.")]
    public Task<CheckRunResult> RunChecks(IProgress<ProgressNotificationValue> progress, CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync(async () =>
        {
            try
            {
                var run = await runner.RunAsync(
                    "tool", null,
                    new ProgressRelay(progress),
                    cancellationToken);
                return CheckRunResult.From(run);
            }
            catch (NoCheckCommandException exception)
            {
                throw new McpException(exception.Message, exception);
            }
        });
}

// Progress<T> posts to the thread pool; the relay reports in order, on the caller's thread.
file sealed class ProgressRelay(IProgress<ProgressNotificationValue> inner) : IProgress<string>
{
    public void Report(string message) => inner.Report(new ProgressNotificationValue { Progress = 0, Message = message });
}
