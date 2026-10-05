using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CodebaseGuardian.Checks;
using CodebaseGuardian.Git;
using CodebaseGuardian.Hosting;
using CodebaseGuardian.Processes;
using CodebaseGuardian.Tests.Infrastructure;
using CodebaseGuardian.Watching;
using Mcp.Events;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Tests.App;

public class CheckRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Func<ProcessSpec, bool> IsCheck = s => s.FileName == "dotnet";

    private sealed class Harness : IDisposable
    {
        public TempGitRepo Repo { get; } = TempGitRepo.Create();
        public FakeProcessRunner Process { get; } = new();
        public RecordingPublisher Events { get; } = new();
        public CheckRunStore Store { get; } = new(Options.Create(new CheckOptions()));
        public CheckRunner Runner { get; }

        public Harness(CheckOptions? options = null)
        {
            Repo.WriteFile("App.slnx", "");
            options ??= new CheckOptions();
            var git = new GitRepository(new ProcessRunner(), Options.Create(new GuardianOptions { RepositoryPath = Repo.Path }));
            var resolver = new CheckCommandResolver(git, Options.Create(options));
            Runner = new CheckRunner(Process, git, resolver, Events, Store, Options.Create(options), TimeProvider.System);
        }

        public void Dispose() => Repo.Dispose();
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        private readonly ConcurrentQueue<(string Name, JsonObject Data)> _events = new();
        public IReadOnlyList<(string Name, JsonObject Data)> Events => [.. _events];

        public ValueTask<EventEnvelope> PublishAsync(string name, JsonObject data, string? eventId = null,
            DateTimeOffset? timestamp = null, CancellationToken cancellationToken = default)
        {
            _events.Enqueue((name, data));
            return ValueTask.FromResult<EventEnvelope>(null!);
        }
    }

    [Fact]
    public async Task A_failing_run_publishes_completed_and_failed_with_suggested_skill_and_log_uri()
    {
        using var h = new Harness();
        h.Process.On(IsCheck, FakeProcessRunner.Result(1, "  Failed Demo.T.One [2 ms]\nFailed!  - Failed: 1, Passed: 0, Total: 1\n", "boom"));

        var run = await h.Runner.RunAsync("tool", null, null, Ct);

        Assert.False(run.Passed);
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["Demo.T.One"], run.FailedTests);
        Assert.Contains("boom", run.Log);
        Assert.Matches(@"^run_\d{14}_[0-9a-f]{4}$", run.RunId);
        Assert.Equal($"guardian://checks/{run.RunId}/log", run.LogUri);
        Assert.Equal("dotnet test App.slnx --nologo", run.Command);
        Assert.Equal(["checks.completed", "checks.failed"], h.Events.Events.Select(e => e.Name));

        var failed = h.Events.Events[1].Data;
        Assert.Equal("skill://bug-triage/SKILL.md", (string?)failed["suggestedSkill"]);
        Assert.Equal(run.LogUri, (string?)failed["logUri"]);
        Assert.Equal(run.RunId, (string?)failed["runId"]);
        Assert.Equal("tool", (string?)failed["trigger"]);
        Assert.False((bool)failed["passed"]!);
        Assert.Equal(1, (int)failed["exitCode"]!);
        Assert.Equal(["Demo.T.One"], failed["failedTests"]!.AsArray().Select(n => (string)n!));
        Assert.Same(run, h.Store.Latest);
        Assert.Same(run, h.Store.Get(run.RunId));
    }

    [Fact]
    public async Task A_passing_run_publishes_only_completed()
    {
        using var h = new Harness();
        h.Process.On(IsCheck, FakeProcessRunner.Result(0, "Passed!  - Failed: 0, Passed: 3, Total: 3\n"));

        var run = await h.Runner.RunAsync("commit", "abc123", null, Ct);

        Assert.True(run.Passed);
        var published = Assert.Single(h.Events.Events);
        Assert.Equal("checks.completed", published.Name);
        Assert.Null(published.Data["suggestedSkill"]);
        Assert.Equal("abc123", (string?)published.Data["commitSha"]);
        Assert.Equal("commit", run.Trigger);
    }

    [Fact]
    public async Task A_timed_out_run_is_not_passed_even_with_exit_code_zero()
    {
        using var h = new Harness(new CheckOptions { TimeoutMinutes = 3 });
        h.Process.On(IsCheck, FakeProcessRunner.Result(0, timedOut: true));

        var run = await h.Runner.RunAsync("tool", null, null, Ct);

        Assert.False(run.Passed);
        Assert.True(run.TimedOut);
        Assert.Equal("timed out after 3 minutes", run.Summary);
        Assert.Equal(TimeSpan.FromMinutes(3), Assert.Single(h.Process.Calls).Timeout);
        Assert.Contains(h.Events.Events, e => e.Name == "checks.failed");
    }

    [Fact]
    public async Task A_missing_executable_is_a_failed_run_whose_summary_is_the_error()
    {
        using var h = new Harness(new CheckOptions { Command = "no-such-tool" });
        var missing = new ExecutableNotFoundException("no-such-tool", new InvalidOperationException());
        var runner = new CheckRunner(new ThrowingRunner(missing), new GitRepository(new ProcessRunner(),
            Options.Create(new GuardianOptions { RepositoryPath = h.Repo.Path })), new FixedResolver(), h.Events, h.Store,
            Options.Create(new CheckOptions()), TimeProvider.System);

        var run = await runner.RunAsync("tool", null, null, Ct);

        Assert.False(run.Passed);
        Assert.Equal(-1, run.ExitCode);
        Assert.Equal(missing.Message, run.Summary);
        Assert.Contains("no-such-tool", run.Log);
        Assert.Contains(h.Events.Events, e => e.Name == "checks.failed");
    }

    private sealed class ThrowingRunner(Exception exception) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) => throw exception;
    }

    private sealed class FixedResolver : ICheckCommandResolver
    {
        public CheckCommand? Resolve() => new("no-such-tool", []);
    }

    [Fact]
    public async Task The_check_runs_in_the_repository_root_with_the_resolved_argument_list()
    {
        using var h = new Harness();
        h.Process.On(IsCheck, FakeProcessRunner.Result(0));

        await h.Runner.RunAsync("tool", null, null, Ct);

        var spec = Assert.Single(h.Process.Calls);
        Assert.Equal(Path.GetFullPath(h.Repo.Path), Path.GetFullPath(spec.WorkingDirectory));
        Assert.Equal(["test", "App.slnx", "--nologo"], spec.Arguments);
    }

    [Fact]
    public async Task Progress_reports_the_command_being_run()
    {
        using var h = new Harness();
        h.Process.On(IsCheck, FakeProcessRunner.Result(0));
        var messages = new List<string>();

        await h.Runner.RunAsync("tool", null, new SyncProgress(messages), Ct);

        Assert.Equal(["running dotnet test App.slnx --nologo"], messages);
    }

    private sealed class SyncProgress(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }

    [Fact]
    public async Task Two_concurrent_runs_execute_one_after_the_other()
    {
        using var h = new Harness();
        var first = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Process.OnBlocking(IsCheck, first);

        var one = h.Runner.RunAsync("tool", null, null, Ct);
        await WaitUntilAsync(() => h.Process.Calls.Count == 1);
        var two = h.Runner.RunAsync("tool", null, null, Ct);

        await Task.Delay(300, Ct);
        Assert.Single(h.Process.Calls);
        Assert.False(two.IsCompleted);

        first.SetResult(FakeProcessRunner.Result(0));
        await one;
        await WaitUntilAsync(() => h.Process.Calls.Count == 2);
        // The second call also matches the (already completed) blocking registration, so it finishes at once.
        Assert.True((await two).Passed);
        Assert.Equal(2, h.Events.Events.Count(e => e.Name == "checks.completed"));
    }

    [Fact]
    public async Task Without_a_command_the_run_is_refused()
    {
        using var h = new Harness();
        File.Delete(Path.Combine(h.Repo.Path, "App.slnx"));

        await Assert.ThrowsAsync<NoCheckCommandException>(() => h.Runner.RunAsync("tool", null, null, Ct));
    }

    [Fact]
    public void The_store_keeps_only_the_newest_max_runs()
    {
        var store = new CheckRunStore(Options.Create(new CheckOptions { MaxRuns = 2 }));
        foreach (var id in new[] { "r1", "r2", "r3" })
        {
            store.Add(new CheckRun(id, "c", 0, true, false, DateTimeOffset.UnixEpoch, TimeSpan.Zero, "s", [], "", "tool", null));
        }

        Assert.Null(store.Get("r1"));
        Assert.NotNull(store.Get("r2"));
        Assert.Equal("r3", store.Latest!.RunId);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(20, Ct);
        }
    }
}
