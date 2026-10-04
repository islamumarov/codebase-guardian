using CodebaseGuardian.Hosting;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace CodebaseGuardian.Tests.App;

public class HostingTests
{
    // ---- server identity and instructions ---------------------------------------------------------------------

    [Fact]
    public async Task Server_reports_name_and_version()
    {
        using var repo = TempGitRepo.Create();

        await using var server = await GuardianTestHost.StartAsync(
            repo.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("codebase-guardian", server.Client.ServerInfo.Name);
        // The csproj <Version>, followed by the "+<commit>" suffix the SDK appends to the informational version.
        Assert.StartsWith("0.1.0-alpha", server.Client.ServerInfo.Version, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Instructions_mention_repository_path()
    {
        using var repo = TempGitRepo.Create();

        await using var server = await GuardianTestHost.StartAsync(
            repo.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains($"Codebase Guardian is watching the Git repository at {repo.Path}.", Instructions(server));
    }

    [Fact]
    public async Task Instructions_compose_contributors_in_order()
    {
        using var repo = TempGitRepo.Create();

        await using var server = await GuardianTestHost.StartAsync(
            repo.Path,
            configureServices: services =>
            {
                services.AddSingleton<IInstructionsContributor>(new FixedContributor(20, "B"));
                services.AddSingleton<IInstructionsContributor>(new FixedContributor(10, "A"));
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var sections = Instructions(server).Split("\n\n");
        var a = Array.IndexOf(sections, "A");
        var b = Array.IndexOf(sections, "B");

        Assert.StartsWith("Codebase Guardian is watching the Git repository at ", sections[0]);
        Assert.True(a > 0, "section A is missing");
        Assert.True(b > a, "section B must come after section A");
    }

    [Fact]
    public async Task Instructions_skip_empty_sections_and_separate_the_rest_with_one_blank_line()
    {
        using var repo = TempGitRepo.Create();

        await using var server = await GuardianTestHost.StartAsync(
            repo.Path,
            configureServices: services =>
            {
                services.AddSingleton<IInstructionsContributor>(new FixedContributor(1, null));
                services.AddSingleton<IInstructionsContributor>(new FixedContributor(2, ""));
                services.AddSingleton<IInstructionsContributor>(new FixedContributor(3, "   \n"));
                services.AddSingleton<IInstructionsContributor>(new FixedContributor(4, "\n  A  \n"));
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var instructions = Instructions(server);
        var sections = instructions.Split("\n\n");

        Assert.Contains("A", sections); // present and trimmed
        Assert.All(sections, section => Assert.False(string.IsNullOrWhiteSpace(section))); // nothing empty between separators
        Assert.DoesNotContain("\n\n\n", instructions); // exactly one blank line between sections
    }

    [Fact]
    public async Task Repository_path_is_normalized_to_a_full_path()
    {
        using var repo = TempGitRepo.Create();
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), repo.Path);

        await using var server = await GuardianTestHost.StartAsync(
            relative, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(repo.Path, Options(server).RepositoryPath);
        Assert.Contains($"repository at {repo.Path}.", Instructions(server));
    }

    // ---- option validation ------------------------------------------------------------------------------------

    [Fact]
    public async Task Startup_fails_for_missing_repository_path()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"guardian-missing-{Guid.NewGuid():N}");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => GuardianTestHost.StartAsync(missing, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("RepositoryPath", exception.Message);
    }

    [Fact]
    public async Task Startup_fails_when_repository_path_is_a_file()
    {
        var file = Path.Combine(Path.GetTempPath(), $"guardian-file-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(file, "not a directory", TestContext.Current.CancellationToken);
        try
        {
            var exception = await Assert.ThrowsAsync<OptionsValidationException>(
                () => GuardianTestHost.StartAsync(file, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("RepositoryPath", exception.Message);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("Guardian:WatchIntervalMs", "99", "WatchIntervalMs")]
    [InlineData("Guardian:FileChangeDebounceMs", "49", "FileChangeDebounceMs")]
    public async Task Startup_fails_for_intervals_below_their_minimum(string key, string value, string expectedName)
    {
        using var repo = TempGitRepo.Create();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => GuardianTestHost.StartAsync(
                repo.Path,
                new Dictionary<string, string?> { [key] = value },
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(expectedName, exception.Message);
    }

    [Fact]
    public async Task Startup_accepts_the_minimum_intervals()
    {
        using var repo = TempGitRepo.Create();

        await using var server = await GuardianTestHost.StartAsync(
            repo.Path,
            new Dictionary<string, string?>
            {
                ["Guardian:WatchIntervalMs"] = "100",
                ["Guardian:FileChangeDebounceMs"] = "50",
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(100, Options(server).WatchIntervalMs);
        Assert.Equal(50, Options(server).FileChangeDebounceMs);
    }

    [Fact]
    public async Task Startup_reports_every_invalid_option_at_once()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"guardian-missing-{Guid.NewGuid():N}");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => GuardianTestHost.StartAsync(
                missing,
                new Dictionary<string, string?>
                {
                    ["Guardian:WatchIntervalMs"] = "1",
                    ["Guardian:FileChangeDebounceMs"] = "1",
                },
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("RepositoryPath", exception.Message);
        Assert.Contains("WatchIntervalMs", exception.Message);
        Assert.Contains("FileChangeDebounceMs", exception.Message);
    }

    // ---- test host defaults -----------------------------------------------------------------------------------

    [Fact]
    public async Task Test_host_disables_the_watcher_unless_the_caller_overrides_it()
    {
        using var repo = TempGitRepo.Create();

        await using var byDefault = await GuardianTestHost.StartAsync(
            repo.Path, cancellationToken: TestContext.Current.CancellationToken);
        await using var overridden = await GuardianTestHost.StartAsync(
            repo.Path,
            new Dictionary<string, string?> { ["guardian:watchenabled"] = "true" }, // keys are case-insensitive
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(Options(byDefault).WatchEnabled);
        Assert.True(Options(overridden).WatchEnabled);
    }

    // ---- in-process harness -----------------------------------------------------------------------------------

    [Fact]
    public async Task InProcessMcpServer_runs_hosted_services_with_the_supplied_configuration()
    {
        var probe = new StartupProbe();

        await using var server = await InProcessMcpServer.StartAsync(
            (services, _, _) =>
            {
                services.AddSingleton(probe);
                services.AddHostedService<ProbeService>();
            },
            new Dictionary<string, string?> { ["probe:value"] = "42" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("42", probe.Value);
        Assert.Same(probe, server.Services.GetRequiredService<StartupProbe>());
    }

    [Fact]
    public async Task InProcessMcpServer_returns_the_result_object_of_raw_requests()
    {
        await using var server = await InProcessMcpServer.StartAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await server.RequestAsync("server/discover", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("complete", result["resultType"]?.GetValue<string>());
    }

    [Fact]
    public async Task InProcessMcpServer_surfaces_protocol_errors_from_raw_requests()
    {
        await using var server = await InProcessMcpServer.StartAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => server.RequestAsync("does/not/exist", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(McpErrorCode.MethodNotFound, exception.ErrorCode);
    }

    // ---- TempGitRepo ------------------------------------------------------------------------------------------

    [Fact]
    public void TempGitRepo_commits_files()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("docs/readme.md", "hello");

        var sha = repo.Commit("first commit");

        Assert.Matches("^[0-9a-f]{40}$", sha);
        Assert.Equal(sha, repo.Git("rev-parse", "HEAD"));
        var log = repo.Git("log", "--oneline");
        Assert.Single(log.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("first commit", log);
        Assert.Equal("main", repo.Git("branch", "--show-current"));
    }

    [Fact]
    public void TempGitRepo_deletes_files_and_commits_the_removal()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("a.txt", "a");
        repo.WriteFile("nested/b.txt", "b");
        repo.Commit("add files");

        repo.DeleteFile("a.txt");
        repo.Commit("remove a");

        Assert.Equal("nested/b.txt", repo.Git("ls-files"));
        Assert.Equal(2, repo.Git("log", "--oneline").Split('\n').Length);
    }

    [Fact]
    public void TempGitRepo_ignores_global_and_system_git_configuration()
    {
        // A developer's global excludes, autocrlf, hooks or signing settings must not change what the tests commit.
        using var repo = TempGitRepo.Create();

        var entries = repo.Git("config", "--list", "--show-scope").Split('\n');

        Assert.All(entries, entry => Assert.StartsWith("local\t", entry));
        Assert.Contains("local\tcommit.gpgsign=false", entries);
        Assert.Contains(entries, entry => entry.StartsWith("local\tuser.name=", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.StartsWith("local\tuser.email=", StringComparison.Ordinal));
    }

    [Fact]
    public void TempGitRepo_git_failures_include_the_command_and_stderr()
    {
        using var repo = TempGitRepo.Create(); // no commits yet, so HEAD does not resolve

        var exception = Assert.ThrowsAny<Exception>(() => repo.Git("rev-parse", "--verify", "HEAD"));

        Assert.Contains("rev-parse --verify HEAD", exception.Message);
        Assert.Contains("fatal:", exception.Message);
    }

    [Fact]
    public void TempGitRepo_rejects_paths_outside_the_repository()
    {
        using var repo = TempGitRepo.Create();

        Assert.Throws<ArgumentException>(() => repo.WriteFile("../escape.txt", "x"));
        Assert.Throws<ArgumentException>(() => repo.DeleteFile(Path.Combine(Path.GetTempPath(), "absolute.txt")));
    }

    [Fact]
    public void TempGitRepo_dispose_removes_the_directory_including_read_only_files()
    {
        var repo = TempGitRepo.Create();
        repo.WriteFile("locked.txt", "x");
        repo.Commit("add locked");
        File.SetAttributes(Path.Combine(repo.Path, "locked.txt"), FileAttributes.ReadOnly);

        repo.Dispose();

        Assert.False(Directory.Exists(repo.Path));
        repo.Dispose(); // idempotent
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static string Instructions(InProcessMcpServer server)
    {
        var instructions = server.Client.ServerInstructions;
        Assert.False(string.IsNullOrWhiteSpace(instructions));
        return instructions;
    }

    private static GuardianOptions Options(InProcessMcpServer server) =>
        server.Services.GetRequiredService<IOptions<GuardianOptions>>().Value;

    private sealed class FixedContributor(int order, string? section) : IInstructionsContributor
    {
        public int Order => order;

        public string? GetSection() => section;
    }

    private sealed class StartupProbe
    {
        public string? Value { get; set; }
    }

    private sealed class ProbeService(IConfiguration configuration, StartupProbe probe) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            probe.Value = configuration["probe:value"];
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
