using ModelContextProtocol;

namespace CodebaseGuardian.Tools;

/// <summary>Progress&lt;T&gt; posts to the thread pool; the relay reports in order, on the caller's thread.</summary>
internal sealed class ProgressRelay(IProgress<ProgressNotificationValue> inner) : IProgress<string>
{
    public void Report(string message) => inner.Report(new ProgressNotificationValue { Progress = 0, Message = message });
}
