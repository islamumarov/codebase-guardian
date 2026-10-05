using CodebaseGuardian.Dependencies;
using CodebaseGuardian.Git;
using CodebaseGuardian.Hosting;
using CodebaseGuardian.Processes;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Tests.App;

public class DependencyAuditorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DependencyAuditor Create(TempGitRepo repo, FakeProcessRunner fake)
    {
        var git = new GitRepository(new ProcessRunner(), Options.Create(new GuardianOptions { RepositoryPath = repo.Path }));
        return new DependencyAuditor(fake, git);
    }

    private static TempGitRepo Repo(params string[] files)
    {
        var repo = TempGitRepo.Create();
        foreach (var file in files)
        {
            repo.WriteFile(file, file.EndsWith(".json") ? "{}" : "");
        }

        repo.WriteFile("README.md", "x");
        repo.Commit("initial");
        return repo;
    }

    private static FakeProcessRunner NuGetFake() => new FakeProcessRunner()
        .On(s => s.FileName == "dotnet" && s.Arguments.Contains("--vulnerable"), FakeProcessRunner.Result(0, NuGetAuditParserTests.VulnerableJson))
        .On(s => s.FileName == "dotnet" && s.Arguments.Contains("--outdated"), FakeProcessRunner.Result(0, NuGetAuditParserTests.OutdatedJson));

    private static FakeProcessRunner NpmFake() => new FakeProcessRunner()
        .On(s => s.FileName == "npm" && s.Arguments[0] == "audit", FakeProcessRunner.Result(1, NpmAuditParserTests.AuditJson))
        .On(s => s.FileName == "npm" && s.Arguments[0] == "outdated", FakeProcessRunner.Result(1, NpmAuditParserTests.OutdatedJson));

    [Fact]
    public async Task NuGet_runs_the_documented_commands_against_the_solution_and_parses_both_results()
    {
        using var repo = Repo("App.slnx", "Directory.Packages.props");
        var fake = NuGetFake();

        var report = await Create(repo, fake).AuditAsync(true, null, Ct);

        var nuget = Assert.Single(report.Ecosystems);
        Assert.Equal("nuget", nuget.Ecosystem);
        Assert.Equal("ok", nuget.Status);
        Assert.Equal(2, nuget.Vulnerable.Count);
        Assert.Equal("Web", nuget.Vulnerable[0].Project);
        Assert.Equal("Serilog", Assert.Single(nuget.Outdated).Package);
        Assert.Equal(2, report.VulnerableCount);
        Assert.Equal(1, report.OutdatedCount);
        Assert.Equal(
            ["list", "App.slnx", "package", "--vulnerable", "--include-transitive", "--format", "json", "--output-version", "1"],
            fake.Calls[0].Arguments);
        Assert.Equal(
            ["list", "App.slnx", "package", "--outdated", "--format", "json", "--output-version", "1"],
            fake.Calls[1].Arguments);
        Assert.All(fake.Calls, c => Assert.Equal(TimeSpan.FromMinutes(5), c.Timeout));
        Assert.All(fake.Calls, c => Assert.Equal(repo.Path, c.WorkingDirectory));
    }

    [Fact]
    public async Task Without_a_solution_the_repository_root_is_the_target_and_outdated_can_be_skipped()
    {
        using var repo = Repo("src/App.csproj");
        var fake = NuGetFake();

        var report = await Create(repo, fake).AuditAsync(false, null, Ct);

        var call = Assert.Single(fake.Calls);
        Assert.Equal(repo.Path, call.Arguments[1]);
        Assert.Empty(report.Ecosystems[0].Outdated);
    }

    [Fact]
    public async Task Npm_audit_and_outdated_are_parsed_and_exit_code_1_is_success()
    {
        using var repo = Repo("package.json");
        var fake = NpmFake();

        var report = await Create(repo, fake).AuditAsync(true, null, Ct);

        var npm = Assert.Single(report.Ecosystems);
        Assert.Equal("npm", npm.Ecosystem);
        Assert.Equal("ok", npm.Status);
        Assert.Equal(2, npm.Vulnerable.Count);
        Assert.Equal(2, npm.Outdated.Count);
        Assert.Equal(["audit", "--json"], fake.Calls[0].Arguments);
        Assert.Equal(["outdated", "--json"], fake.Calls[1].Arguments);
    }

    [Fact]
    public async Task A_repository_with_only_package_json_runs_only_npm_commands()
    {
        using var repo = Repo("package.json");
        var fake = NpmFake();

        await Create(repo, fake).AuditAsync(true, null, Ct);

        Assert.NotEmpty(fake.Calls);
        Assert.All(fake.Calls, c => Assert.Equal("npm", c.FileName));
    }

    [Fact]
    public async Task A_missing_npm_is_skipped_while_nuget_is_still_audited()
    {
        using var repo = Repo("App.slnx", "Directory.Packages.props", "package.json");
        var runner = NuGetFake().OnThrow(s => s.FileName == "npm", new ExecutableNotFoundException("npm", new InvalidOperationException("missing")));

        var report = await Create(repo, runner).AuditAsync(true, null, Ct);

        var npm = Assert.Single(report.Ecosystems, e => e.Ecosystem == "npm");
        Assert.Equal("skipped", npm.Status);
        Assert.Equal("npm not found on PATH", npm.Reason);
        Assert.Empty(npm.Vulnerable);
        Assert.Equal("ok", Assert.Single(report.Ecosystems, e => e.Ecosystem == "nuget").Status);
    }

    [Fact]
    public async Task Output_that_is_not_json_fails_the_ecosystem_with_a_short_reason()
    {
        using var repo = Repo("package.json");
        var fake = new FakeProcessRunner().On(s => s.FileName == "npm", FakeProcessRunner.Result(0, new string('x', 2000)));

        var report = await Create(repo, fake).AuditAsync(true, null, Ct);

        var npm = Assert.Single(report.Ecosystems);
        Assert.Equal("failed", npm.Status);
        Assert.NotNull(npm.Reason);
        Assert.True(npm.Reason!.Length <= 600);
    }

    [Fact]
    public async Task An_unexpected_exit_code_fails_the_ecosystem()
    {
        using var repo = Repo("App.slnx", "Directory.Packages.props");
        var fake = new FakeProcessRunner().On(s => s.FileName == "dotnet", FakeProcessRunner.Result(1, "", "error NU1301: unable to load the service index"));

        var report = await Create(repo, fake).AuditAsync(false, null, Ct);

        Assert.Equal("failed", report.Ecosystems[0].Status);
        Assert.Contains("exit code 1", report.Ecosystems[0].Reason);
        Assert.Contains("NU1301", report.Ecosystems[0].Reason);
    }

    [Fact]
    public async Task A_timeout_fails_the_ecosystem()
    {
        using var repo = Repo("package.json");
        var fake = new FakeProcessRunner().On(s => s.FileName == "npm", FakeProcessRunner.Result(-1, timedOut: true));

        var report = await Create(repo, fake).AuditAsync(true, null, Ct);

        Assert.Equal("failed", report.Ecosystems[0].Status);
        Assert.Contains("timed out", report.Ecosystems[0].Reason);
    }

    [Fact]
    public async Task A_repository_without_manifests_has_an_empty_report()
    {
        using var repo = Repo();
        var fake = new FakeProcessRunner();

        var report = await Create(repo, fake).AuditAsync(true, null, Ct);

        Assert.Empty(report.Ecosystems);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_tracked_manifest_deleted_from_the_working_tree_does_not_count()
    {
        using var repo = Repo("package.json");
        repo.DeleteFile("package.json");
        var fake = new FakeProcessRunner();

        var report = await Create(repo, fake).AuditAsync(true, null, Ct);

        Assert.Empty(report.Ecosystems);
    }

    [Fact]
    public async Task Progress_names_each_ecosystem_being_audited()
    {
        using var repo = Repo("package.json");
        var messages = new List<string>();

        await Create(repo, NpmFake()).AuditAsync(true, new Sync(messages), Ct);

        Assert.Contains("auditing npm", messages);
    }

    private sealed class Sync(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }
}
