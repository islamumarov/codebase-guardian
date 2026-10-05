using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace CodebaseGuardian.Tools;

/// <summary>
/// Progress&lt;T&gt; posts to the thread pool; the relay reports in order, on the caller's thread. Progress is best effort:
/// a failure to report (for example a closed transport, or a task-mode run whose request has ended) never fails the run.
/// </summary>
internal sealed class ProgressRelay(IProgress<ProgressNotificationValue> inner, ILogger logger) : IProgress<string>
{
    public void Report(string message)
    {
        try
        {
            inner.Report(new ProgressNotificationValue { Progress = 0, Message = message });
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Reporting progress failed; the run continues");
        }
    }
}
