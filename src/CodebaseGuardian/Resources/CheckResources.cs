using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodebaseGuardian.Checks;
using CodebaseGuardian.Json;
using CodebaseGuardian.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodebaseGuardian.Resources;

public sealed record LatestCheckRun(
    string RunId, string Command, bool Passed, int ExitCode, bool TimedOut,
    [property: JsonConverter(typeof(UtcTimestampJsonConverter))] DateTimeOffset StartedAt,
    long DurationMs, string Summary, IReadOnlyList<string> FailedTests, string LogUri, string Trigger, string? CommitSha);

[McpServerResourceType]
public sealed class CheckResources(CheckRunStore store)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerResource(UriTemplate = "guardian://checks/latest", Name = "latest-check-run", MimeType = "application/json")]
    [Description("The most recent check run (build and tests): outcome, summary, failed tests and a link to its log. Does not include the log itself.")]
    public string Latest()
    {
        var run = store.Latest ?? throw new McpProtocolException("No check runs yet.", McpErrorCode.InvalidParams);
        return JsonSerializer.Serialize(new LatestCheckRun(
            run.RunId, run.Command, run.Passed, run.ExitCode, run.TimedOut, run.StartedAt,
            (long)run.Duration.TotalMilliseconds, run.Summary, run.FailedTests, run.LogUri, run.Trigger, run.CommitSha), Json);
    }

    [McpServerResource(UriTemplate = "guardian://checks/{runId}/log", Name = "check-run-log", MimeType = "text/plain")]
    [Description("The full output (stdout, then stderr) of one check run; the run id comes from run_checks, checks events or guardian://checks/latest.")]
    public string Log(string runId) =>
        store.Get(runId)?.Log ?? throw new McpProtocolException($"Unknown check run '{runId}'.", McpErrorCode.InvalidParams);
}
