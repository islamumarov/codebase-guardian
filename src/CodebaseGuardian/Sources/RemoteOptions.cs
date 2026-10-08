using CodebaseGuardian.GitHub;
using CodebaseGuardian.Hosting;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Sources;

/// <summary>Remote-mode settings, bound from <c>Guardian:Remote</c>. They only take effect when the repository is a GitHub one.</summary>
public sealed class RemoteOptions
{
    public const string SectionName = "Guardian:Remote";

    /// <summary>How often the watcher polls GitHub, in seconds (minimum 15).</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Largest snapshot (tarball contents) a scan may unpack, in bytes (minimum 1 MiB).</summary>
    public long MaxSnapshotBytes { get; set; } = 104_857_600;

    /// <summary>Most files a scan may unpack from a snapshot (minimum 1).</summary>
    public int MaxSnapshotFiles { get; set; } = 20_000;
}

/// <summary>Fails host start-up on an invalid remote-mode combination, naming every problem.</summary>
// guardian is part of the specified constructor but nothing reads it yet: AutoChecks is checked by GuardianOptionsValidator.
#pragma warning disable CS9113
internal sealed class RemoteOptionsValidator(
    RepositoryLocation location,
    IOptions<GuardianOptions> guardian,
    IOptions<GitHubOptions> github) : IValidateOptions<RemoteOptions>
#pragma warning restore CS9113
{
    internal const int MinimumPollIntervalSeconds = 15;
    internal const long MinimumSnapshotBytes = 1_048_576;
    internal const int MinimumSnapshotFiles = 1;

    public ValidateOptionsResult Validate(string? name, RemoteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.PollIntervalSeconds < MinimumPollIntervalSeconds)
        {
            failures.Add($"{Key(nameof(RemoteOptions.PollIntervalSeconds))} must be at least {MinimumPollIntervalSeconds} (was {options.PollIntervalSeconds}).");
        }

        if (options.MaxSnapshotBytes < MinimumSnapshotBytes)
        {
            failures.Add($"{Key(nameof(RemoteOptions.MaxSnapshotBytes))} must be at least {MinimumSnapshotBytes} (was {options.MaxSnapshotBytes}).");
        }

        if (options.MaxSnapshotFiles < MinimumSnapshotFiles)
        {
            failures.Add($"{Key(nameof(RemoteOptions.MaxSnapshotFiles))} must be at least {MinimumSnapshotFiles} (was {options.MaxSnapshotFiles}).");
        }

        if (location is RepositoryLocation.GitHub remote)
        {
            var gitHub = github.Value;
            if (!gitHub.Enabled)
            {
                failures.Add($"Remote mode reads the repository through the GitHub API; {GitHubOptions.SectionName}:{nameof(GitHubOptions.Enabled)} must be true.");
            }

            if (!string.IsNullOrWhiteSpace(gitHub.Owner)
                && !string.IsNullOrWhiteSpace(gitHub.Repository)
                && (!gitHub.Owner.Equals(remote.Repository.Owner, StringComparison.OrdinalIgnoreCase)
                    || !gitHub.Repository.Equals(remote.Repository.Name, StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add(
                    $"{GitHubOptions.SectionName}:{nameof(GitHubOptions.Owner)}/{nameof(GitHubOptions.Repository)} ({gitHub.Owner}/{gitHub.Repository}) "
                    + $"conflict with {GuardianOptions.SectionName}:{nameof(GuardianOptions.RepositoryPath)} ({remote.Repository}).");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static string Key(string property) => $"{RemoteOptions.SectionName}:{property}";
}
