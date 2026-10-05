using System.Text.Json.Serialization;
using CodebaseGuardian.Json;

namespace CodebaseGuardian.Checks;

public sealed record CheckRun(
    string RunId,
    string Command,
    int ExitCode,
    bool Passed,
    bool TimedOut,
    [property: JsonConverter(typeof(UtcTimestampJsonConverter))] DateTimeOffset StartedAt,
    TimeSpan Duration,
    string Summary,
    IReadOnlyList<string> FailedTests,
    string Log,
    string Trigger,
    string? CommitSha)
{
    public string LogUri => $"guardian://checks/{RunId}/log";
}
