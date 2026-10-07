using CodebaseGuardian.GitHub;
using CodebaseGuardian.Hosting;
using CodebaseGuardian.Sources;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Tests.App;

public class RemoteOptionsTests
{
    private const string Remote = "github:acme/widgets";

    private static ValidateOptionsResult ValidateRemote(
        string repositoryPath,
        RemoteOptions? remote = null,
        GitHubOptions? github = null,
        GuardianOptions? guardian = null)
    {
        guardian ??= new GuardianOptions();
        guardian.RepositoryPath = repositoryPath;
        var validator = new RemoteOptionsValidator(
            RepositoryLocation.Parse(repositoryPath),
            Options.Create(guardian),
            Options.Create(github ?? new GitHubOptions()));
        return validator.Validate(null, remote ?? new RemoteOptions());
    }

    private static ValidateOptionsResult ValidateGuardian(GuardianOptions options) =>
        new GuardianOptionsValidator().Validate(null, options);

    [Fact]
    public void The_defaults_are_valid()
    {
        Assert.True(ValidateRemote(Remote).Succeeded);

        var defaults = new RemoteOptions();
        Assert.Equal(60, defaults.PollIntervalSeconds);
        Assert.Equal(104_857_600, defaults.MaxSnapshotBytes);
        Assert.Equal(20_000, defaults.MaxSnapshotFiles);
    }

    [Fact]
    public void Auto_checks_are_refused_in_remote_mode()
    {
        var result = ValidateGuardian(new GuardianOptions { RepositoryPath = Remote, AutoChecks = true });

        Assert.Contains("Guardian:AutoChecks needs a local checkout; it is not available in remote mode.", result.Failures!);
    }

    [Fact]
    public void A_remote_location_needs_no_directory()
    {
        var result = ValidateGuardian(new GuardianOptions { RepositoryPath = "github:acme/definitely-not-a-directory" });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void An_invalid_location_reports_its_reason()
    {
        var result = ValidateGuardian(new GuardianOptions { RepositoryPath = "github:acme" });

        var failure = Assert.Single(result.Failures!);
        Assert.Equal("Guardian:RepositoryPath 'github:acme' must be github:owner/name.", failure);
    }

    [Fact]
    public void Watch_intervals_are_still_checked_in_remote_mode()
    {
        var result = ValidateGuardian(new GuardianOptions { RepositoryPath = Remote, WatchIntervalMs = 99, FileChangeDebounceMs = 49 });

        Assert.Equal(2, result.Failures!.Count());
    }

    [Fact]
    public void A_missing_local_directory_is_still_reported()
    {
        var result = ValidateGuardian(new GuardianOptions { RepositoryPath = Path.Combine(Path.GetTempPath(), $"guardian-missing-{Guid.NewGuid():N}") });

        Assert.Contains("does not exist", Assert.Single(result.Failures!));
    }

    [Fact]
    public void Remote_mode_needs_the_GitHub_integration()
    {
        var result = ValidateRemote(Remote, github: new GitHubOptions { Enabled = false });

        Assert.Equal(
            "Remote mode reads the repository through the GitHub API; Guardian:GitHub:Enabled must be true.",
            Assert.Single(result.Failures!));
    }

    [Fact]
    public void Local_mode_passes_with_GitHub_disabled()
    {
        using var repo = TempGitRepo.Create();

        var result = ValidateRemote(repo.Path, github: new GitHubOptions { Enabled = false });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void A_conflicting_owner_and_repository_is_refused()
    {
        var result = ValidateRemote(Remote, github: new GitHubOptions { Owner = "other", Repository = "thing" });

        Assert.Equal(
            "Guardian:GitHub:Owner/Repository (other/thing) conflict with Guardian:RepositoryPath (acme/widgets).",
            Assert.Single(result.Failures!));
    }

    [Fact]
    public void A_matching_owner_and_repository_is_accepted_ignoring_case()
    {
        var result = ValidateRemote(Remote, github: new GitHubOptions { Owner = "ACME", Repository = "Widgets" });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("other", null)]
    [InlineData(null, "thing")]
    [InlineData("other", "")]
    public void The_conflict_check_needs_both_owner_and_repository(string? owner, string? repository)
    {
        var result = ValidateRemote(Remote, github: new GitHubOptions { Owner = owner, Repository = repository });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void The_conflict_is_not_checked_in_local_mode()
    {
        using var repo = TempGitRepo.Create();

        var result = ValidateRemote(repo.Path, github: new GitHubOptions { Owner = "other", Repository = "thing" });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void The_poll_interval_has_a_minimum_of_15_seconds()
    {
        var result = ValidateRemote(Remote, new RemoteOptions { PollIntervalSeconds = 5 });

        Assert.Equal("Guardian:Remote:PollIntervalSeconds must be at least 15 (was 5).", Assert.Single(result.Failures!));
    }

    [Fact]
    public void The_snapshot_limits_have_minimums()
    {
        var result = ValidateRemote(Remote, new RemoteOptions { MaxSnapshotBytes = 10, MaxSnapshotFiles = 0 });

        Assert.Equal(
            [
                "Guardian:Remote:MaxSnapshotBytes must be at least 1048576 (was 10).",
                "Guardian:Remote:MaxSnapshotFiles must be at least 1 (was 0).",
            ],
            result.Failures!);
    }

    [Fact]
    public void The_range_checks_apply_in_local_mode_too()
    {
        using var repo = TempGitRepo.Create();

        var result = ValidateRemote(repo.Path, new RemoteOptions { PollIntervalSeconds = 5 });

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Startup_fails_naming_every_invalid_remote_setting()
    {
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => GuardianTestHost.StartAsync(
            Remote,
            new Dictionary<string, string?>
            {
                ["Guardian:GitHub:Enabled"] = "false",
                ["Guardian:Remote:PollIntervalSeconds"] = "5",
                ["Guardian:Remote:MaxSnapshotBytes"] = "10",
            },
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Guardian:GitHub:Enabled must be true", exception.Message);
        Assert.Contains("Guardian:Remote:PollIntervalSeconds must be at least 15 (was 5)", exception.Message);
        Assert.Contains("Guardian:Remote:MaxSnapshotBytes must be at least 1048576 (was 10)", exception.Message);
        Assert.DoesNotContain("does not exist", exception.Message);
    }

    [Fact]
    public async Task Startup_fails_for_auto_checks_in_remote_mode()
    {
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => GuardianTestHost.StartAsync(
            Remote,
            new Dictionary<string, string?> { ["Guardian:AutoChecks"] = "true" },
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Guardian:AutoChecks needs a local checkout; it is not available in remote mode.", exception.Message);
        Assert.DoesNotContain("does not exist", exception.Message);
    }
}
