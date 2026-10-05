using System.Diagnostics;
using CodebaseGuardian.Processes;

namespace CodebaseGuardian.Tests.App;

public sealed class ProcessRunnerTests
{
    private readonly ProcessRunner _runner = new();

    private static ProcessSpec Spec(string fileName, params string[] args) =>
        new(fileName, args, Directory.GetCurrentDirectory());

    private static ProcessSpec SleepSpec() =>
        OperatingSystem.IsWindows() ? Spec("ping", "-n", "6", "127.0.0.1") : Spec("sleep", "5");

    [Fact]
    public async Task Runs_a_process_and_captures_output()
    {
        var result = await _runner.RunAsync(Spec("git", "--version"), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("git version", result.StandardOutput);
        Assert.False(result.TimedOut);
        Assert.False(result.OutputTruncated);
    }

    [Fact]
    public async Task Returns_a_non_zero_exit_code_instead_of_throwing()
    {
        var result = await _runner.RunAsync(Spec("git", "not-a-command"), TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not-a-command", result.StandardError);
    }

    [Fact]
    public async Task Throws_ExecutableNotFoundException_for_a_missing_executable()
    {
        var exception = await Assert.ThrowsAsync<ExecutableNotFoundException>(
            () => _runner.RunAsync(Spec("guardian-no-such-executable"), TestContext.Current.CancellationToken));

        Assert.Equal("guardian-no-such-executable", exception.FileName);
    }

    [Fact]
    public async Task Kills_a_process_that_exceeds_the_timeout()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await _runner.RunAsync(
            SleepSpec() with { Timeout = TimeSpan.FromMilliseconds(200) }, TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Cancellation_kills_the_process_and_throws()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _runner.RunAsync(SleepSpec(), cts.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Truncates_output_beyond_MaxOutputBytes()
    {
        var result = await _runner.RunAsync(
            Spec("git", "--version") with { MaxOutputBytes = 10 }, TestContext.Current.CancellationToken);

        Assert.True(result.OutputTruncated);
        Assert.Equal("git versio", result.StandardOutput);
    }

    [Fact]
    public async Task Environment_entries_reach_the_process()
    {
        var spec = Spec("git", "config", "--get", "guardian.probe") with
        {
            Environment = new Dictionary<string, string>
            {
                ["GIT_CONFIG_COUNT"] = "1",
                ["GIT_CONFIG_KEY_0"] = "guardian.probe",
                ["GIT_CONFIG_VALUE_0"] = "hello",
            },
        };

        var result = await _runner.RunAsync(spec, TestContext.Current.CancellationToken);

        Assert.Equal("hello", result.StandardOutput.Trim());
    }
}
