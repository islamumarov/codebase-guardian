using CodebaseGuardian.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace CodebaseGuardian.Tests.App;

public class ProgressRelayTests
{
    private sealed class ThrowingProgress : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value) => throw new InvalidOperationException("transport gone");
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }

    [Fact]
    public void An_exception_from_progress_reporting_is_swallowed_and_logged_at_debug()
    {
        var logger = new RecordingLogger();
        var relay = new ProgressRelay(new ThrowingProgress(), logger);

        relay.Report("running");

        Assert.Equal([LogLevel.Debug], logger.Levels);
    }

    [Fact]
    public void Without_a_logger_a_failing_report_is_still_swallowed()
    {
        new ProgressRelay(new ThrowingProgress(), NullLogger.Instance).Report("running");
    }
}
