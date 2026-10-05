using System.Text.Json.Nodes;
using CodebaseGuardian.Checks;
using CodebaseGuardian.Dependencies;
using CodebaseGuardian.Processes;
using CodebaseGuardian.Scanning;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CodebaseGuardian.Tests.App;

public class FullScanServiceTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DependencyAuditReport OneVulnerable() => new([new EcosystemReport("npm", "ok", null,
        [new VulnerablePackage("npm", "lodash", "4.17.0", "high", null, null)], [])]);

    private static DependencyAuditReport Clean() => new([]);

    private static CheckRun Run(bool passed) => new("run_20261005120001_cd34", "dotnet test", passed ? 0 : 1, passed, false,
        DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), passed ? "ok" : "bad", passed ? [] : ["T.A"], "log", "scan", null);

    private static async Task<InProcessMcpServer> StartAsync(
        TempGitRepo repo, IDependencyAuditor auditor, ICheckRunner runner, CheckCommand? command)
    {
        return await GuardianTestHost.StartAsync(repo.Path, null, services =>
        {
            services.AddSingleton(auditor);
            services.AddSingleton(runner);
            services.AddSingleton<ICheckCommandResolver>(new FakeResolver(command));
        }, cancellationToken: Ct);
    }

    private static TempGitRepo RepoWith(string? secretFile)
    {
        var repo = TempGitRepo.Create();
        repo.WriteFile("README.md", "x");
        if (secretFile is not null)
        {
            repo.WriteFile(".env.prod", $"AWS_ACCESS_KEY_ID={secretFile}\n");
        }

        repo.Commit("initial");
        return repo;
    }

    private static readonly CheckCommand Command = new("dotnet", ["test"]);

    private static async Task<(ScanReport Report, JsonObject Data)> ScanAsync(InProcessMcpServer server, bool includeChecks = true)
    {
        var cursor = await EventsPolling.GetCursorAsync(server, Ct);
        var report = await server.Services.GetRequiredService<IFullScanService>().RunAsync(includeChecks, null, Ct);
        var evt = await EventsPolling.WaitForAsync(server, "scan.completed", cursor, null, Generous, Ct);
        return (report, evt["data"]!.AsObject());
    }

    [Fact]
    public async Task Secrets_and_a_vulnerable_package_complete_with_the_security_audit_skill()
    {
        using var repo = RepoWith(FakeSecrets.AwsAccessKeyId());
        await using var server = await StartAsync(repo, new FakeAuditor(OneVulnerable()), new FakeRunner(Run(true)), Command);

        var (report, data) = await ScanAsync(server);

        Assert.Equal(1, data["secretFindings"]!.GetValue<int>());
        Assert.Equal(1, data["vulnerablePackages"]!.GetValue<int>());
        Assert.Equal("skill://security-audit/SKILL.md", (string?)data["suggestedSkill"]);
        Assert.Equal(report.ScanId, (string?)data["scanId"]);
        Assert.Equal($"guardian://scans/{report.ScanId}/report", (string?)data["reportUri"]);
        Assert.True(data["checksPassed"]!.GetValue<bool>());
        Assert.Matches(@"^scan_\d{14}_[0-9a-f]{4}$", report.ScanId);
        Assert.Same(report, server.Services.GetRequiredService<ScanReportStore>().Get(report.ScanId));
        Assert.NotNull(report.HeadSha);
    }

    [Fact]
    public async Task The_stored_markdown_never_contains_the_raw_key()
    {
        using var repo = RepoWith(FakeSecrets.AwsAccessKeyId());
        await using var server = await StartAsync(repo, new FakeAuditor(Clean()), new FakeRunner(Run(true)), Command);

        var (report, _) = await ScanAsync(server);

        Assert.DoesNotContain(FakeSecrets.AwsAccessKeyId(), report.Markdown);
        Assert.Contains("aws-access-key-id", report.Markdown);
        Assert.StartsWith("# Guardian scan " + report.ScanId, report.Markdown);
    }

    [Fact]
    public async Task Free_text_from_the_check_run_is_redacted_before_it_is_stored()
    {
        using var repo = RepoWith(null);
        var token = FakeSecrets.GitHubToken();
        var run = Run(false) with { Summary = $"auth failed with {token}", FailedTests = [$"Tests.Leak {token}"] };
        await using var server = await StartAsync(repo, new FakeAuditor(Clean()), new FakeRunner(run), Command);

        var (report, _) = await ScanAsync(server);

        Assert.DoesNotContain(token, report.Markdown);
        Assert.DoesNotContain(token, report.Checks!.Summary);
        Assert.Contains("auth failed with", report.Markdown);
    }

    [Fact]
    public async Task Without_secrets_a_vulnerable_package_suggests_dependency_hygiene()
    {
        using var repo = RepoWith(null);
        await using var server = await StartAsync(repo, new FakeAuditor(OneVulnerable()), new FakeRunner(Run(true)), Command);

        var (_, data) = await ScanAsync(server);

        Assert.Equal("skill://dependency-hygiene/SKILL.md", (string?)data["suggestedSkill"]);
    }

    [Fact]
    public async Task A_clean_scan_has_no_suggested_skill_key()
    {
        using var repo = RepoWith(null);
        await using var server = await StartAsync(repo, new FakeAuditor(Clean()), new FakeRunner(Run(true)), Command);

        var (_, data) = await ScanAsync(server);

        Assert.False(data.ContainsKey("suggestedSkill"));
        Assert.Equal(0, data["secretFindings"]!.GetValue<int>());
        Assert.Equal(0, data["vulnerablePackages"]!.GetValue<int>());
    }

    [Fact]
    public async Task No_check_command_leaves_checks_passed_null_and_explains_why()
    {
        using var repo = RepoWith(null);
        var runner = new FakeRunner(Run(true));
        await using var server = await StartAsync(repo, new FakeAuditor(Clean()), runner, null);

        var (report, data) = await ScanAsync(server);

        Assert.Null(report.ChecksPassed);
        Assert.True(data.ContainsKey("checksPassed"));
        Assert.Null(data["checksPassed"]);
        Assert.Equal("No check command configured or detected.", report.ChecksSkippedReason);
        Assert.Contains("No check command configured or detected.", report.Markdown);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Not_including_checks_never_calls_the_runner()
    {
        using var repo = RepoWith(null);
        var runner = new FakeRunner(Run(true));
        await using var server = await StartAsync(repo, new FakeAuditor(Clean()), runner, Command);

        var (report, _) = await ScanAsync(server, includeChecks: false);

        Assert.Equal(0, runner.Calls);
        Assert.Equal("Checks not requested.", report.ChecksSkippedReason);
        Assert.Null(report.Checks);
    }

    [Fact]
    public async Task A_failing_step_is_recorded_and_the_scan_continues()
    {
        using var repo = RepoWith(null);
        var runner = new FakeRunner(Run(false));
        await using var server = await StartAsync(
            repo, new FakeAuditor(new InvalidOperationException("boom")), runner, Command);

        var (report, data) = await ScanAsync(server);

        Assert.Equal(["dependencies: boom"], report.StepErrors);
        Assert.Equal(1, runner.Calls);
        Assert.Contains("## Errors", report.Markdown);
        Assert.Null(report.Dependencies);
        Assert.False(data["checksPassed"]!.GetValue<bool>());
        Assert.Equal(0, data["vulnerablePackages"]!.GetValue<int>());
    }

    [Fact]
    public async Task Progress_names_the_three_steps_in_order()
    {
        using var repo = RepoWith(null);
        await using var server = await StartAsync(repo, new FakeAuditor(Clean()), new FakeRunner(Run(true)), Command);
        var steps = new List<string>();

        await server.Services.GetRequiredService<IFullScanService>().RunAsync(true, new Progress0(steps), Ct);

        Assert.Equal(["secrets", "dependencies", "checks"], steps);
    }

    [Fact]
    public async Task Cancellation_during_checks_propagates_and_stores_and_publishes_nothing()
    {
        using var repo = RepoWith(null);
        var runner = new FakeRunner(Run(true)) { Block = true };
        await using var server = await StartAsync(repo, new FakeAuditor(Clean()), runner, Command);
        var cursor = await EventsPolling.GetCursorAsync(server, Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var scan = server.Services.GetRequiredService<IFullScanService>().RunAsync(true, null, cts.Token);
        await runner.Started.Task.WaitAsync(Generous, Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Assert.Equal(0, server.Services.GetRequiredService<ScanReportStore>().Count);
        var (events, _) = await EventsPolling.PollAsync(server, "scan.completed", cursor, Ct);
        Assert.Empty(events);
    }

    [Fact]
    public void The_store_keeps_the_last_twenty_reports()
    {
        var store = new ScanReportStore();
        for (var i = 0; i < 25; i++)
        {
            store.Add(new ScanReport($"scan_{i}", DateTimeOffset.UtcNow, TimeSpan.Zero, null, [], null, null, null, [], "m"));
        }

        Assert.Equal(20, store.Count);
        Assert.Null(store.Get("scan_4"));
        Assert.NotNull(store.Get("scan_5"));
        Assert.NotNull(store.Get("scan_24"));
    }

    private sealed class Progress0(List<string> steps) : IProgress<string>
    {
        public void Report(string value) => steps.Add(value);
    }

    private sealed class FakeResolver(CheckCommand? command) : ICheckCommandResolver
    {
        public CheckCommand? Resolve() => command;
    }

    private sealed class FakeAuditor : IDependencyAuditor
    {
        private readonly DependencyAuditReport? _report;
        private readonly Exception? _exception;

        public FakeAuditor(DependencyAuditReport report) => _report = report;

        public FakeAuditor(Exception exception) => _exception = exception;

        public Task<DependencyAuditReport> AuditAsync(bool includeOutdated, IProgress<string>? progress, CancellationToken cancellationToken) =>
            _exception is not null ? Task.FromException<DependencyAuditReport>(_exception) : Task.FromResult(_report!);
    }

    private sealed class FakeRunner(CheckRun run) : ICheckRunner
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool Block { get; init; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CheckRun> RunAsync(string trigger, string? commitSha, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            if (Block)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return run;
        }
    }
}
