using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CodebaseGuardian.Git;
using CodebaseGuardian.Processes;
using CodebaseGuardian.Watching;
using Mcp.Events;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Checks;

public sealed class NoCheckCommandException()
    : InvalidOperationException("No check command configured or detected. Set Guardian:Checks:Command.");

public interface ICheckRunner
{
    /// <summary>
    /// Runs the check command; one run at a time (later callers wait). Publishes <c>checks.completed</c> always and
    /// <c>checks.failed</c> when the run did not pass.
    /// </summary>
    /// <exception cref="NoCheckCommandException">No command is configured or detected.</exception>
    Task<CheckRun> RunAsync(string trigger, string? commitSha, IProgress<string>? progress, CancellationToken cancellationToken);
}

public sealed class CheckRunner(
    IProcessRunner processes,
    IGitRepository git,
    ICheckCommandResolver resolver,
    IEventPublisher publisher,
    CheckRunStore store,
    IOptions<CheckOptions> options,
    TimeProvider time) : ICheckRunner
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public async Task<CheckRun> RunAsync(string trigger, string? commitSha, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            var command = resolver.Resolve() ?? throw new NoCheckCommandException();
            progress?.Report($"running {command.Display}");

            var timeout = TimeSpan.FromMinutes(Math.Max(1, options.Value.TimeoutMinutes));
            var startedAt = time.GetUtcNow();
            var spec = new ProcessSpec(command.FileName, command.Arguments, git.RootPath) { Timeout = timeout };

            ProcessResult result;
            ExecutableNotFoundException? notFound = null;
            try
            {
                result = await processes.RunAsync(spec, cancellationToken);
            }
            catch (ExecutableNotFoundException exception)
            {
                notFound = exception;
                result = new ProcessResult(-1, "", exception.Message, false, false, TimeSpan.Zero);
            }

            var log = result.StandardOutput + "\n" + result.StandardError;
            var parsed = TestOutputParser.Parse(log, result.ExitCode);
            var run = new CheckRun(
                NewRunId(startedAt), command.Display, result.ExitCode,
                Passed: result.ExitCode == 0 && !result.TimedOut, result.TimedOut,
                startedAt, result.Duration,
                notFound is not null ? notFound.Message
                : result.TimedOut ? $"timed out after {timeout.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes" : parsed.Summary,
                parsed.FailedTests, log, trigger, commitSha);

            store.Add(run);
            await PublishAsync(run, cancellationToken);
            return run;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task PublishAsync(CheckRun run, CancellationToken cancellationToken)
    {
        await publisher.PublishAsync(GuardianEventNames.ChecksCompleted, Payload(run, false), cancellationToken: cancellationToken);
        if (!run.Passed)
        {
            await publisher.PublishAsync(GuardianEventNames.ChecksFailed, Payload(run, true), cancellationToken: cancellationToken);
        }
    }

    private static JsonObject Payload(CheckRun run, bool withSuggestedSkill)
    {
        var data = new JsonObject
        {
            ["runId"] = run.RunId,
            ["command"] = run.Command,
            ["exitCode"] = run.ExitCode,
            ["passed"] = run.Passed,
            ["timedOut"] = run.TimedOut,
            ["durationMs"] = (long)run.Duration.TotalMilliseconds,
            ["summary"] = run.Summary,
            ["failedTests"] = new JsonArray([.. run.FailedTests.Select(t => (JsonNode?)t)]),
            ["logUri"] = run.LogUri,
            ["trigger"] = run.Trigger,
            ["commitSha"] = run.CommitSha,
        };
        if (withSuggestedSkill)
        {
            data["suggestedSkill"] = SuggestedSkills.BugTriage;
        }

        return data;
    }

    private static string NewRunId(DateTimeOffset startedAt) =>
        $"run_{startedAt.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}";
}
