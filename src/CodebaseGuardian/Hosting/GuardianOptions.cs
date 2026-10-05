using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Hosting;

public enum GuardianTransport
{
    Stdio,
    Http,
}

/// <summary>
/// Guardian settings, bound from the <c>Guardian</c> configuration section
/// (appsettings, <c>GUARDIAN__*</c> environment variables, command line).
/// </summary>
public sealed class GuardianOptions
{
    public const string SectionName = "Guardian";

    /// <summary>The Git repository to watch. Normalized to a full path after binding.</summary>
    public string RepositoryPath { get; set; } = Directory.GetCurrentDirectory();

    public GuardianTransport Transport { get; set; } = GuardianTransport.Stdio;

    public string HttpUrl { get; set; } = "http://127.0.0.1:5199";

    /// <summary>
    /// Allows <see cref="HttpUrl"/> to bind a non-loopback address. The HTTP transport has no authentication yet, so
    /// anyone who can reach the port can read the repository and run its checks.
    /// </summary>
    public bool HttpAllowRemote { get; set; }

    public bool AutoChecks { get; set; }

    public bool WatchEnabled { get; set; } = true;

    /// <summary>How often the watcher polls Git refs, in milliseconds (minimum 100).</summary>
    public int WatchIntervalMs { get; set; } = 2000;

    /// <summary>Quiet period before working-tree edits are reported, in milliseconds (minimum 50).</summary>
    public int FileChangeDebounceMs { get; set; } = 2000;

    /// <summary>Root of the skill directories; <c>null</c> means a <c>skills</c> folder next to the server binary.</summary>
    public string? SkillsDirectory { get; set; }
}

/// <summary>Fails host start-up, naming every invalid setting, so a misconfigured server never starts listening.</summary>
internal sealed class GuardianOptionsValidator : IValidateOptions<GuardianOptions>
{
    internal const int MinimumWatchIntervalMs = 100;
    internal const int MinimumFileChangeDebounceMs = 50;

    public ValidateOptionsResult Validate(string? name, GuardianOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.RepositoryPath))
        {
            failures.Add($"{Key(nameof(GuardianOptions.RepositoryPath))} must not be empty.");
        }
        else if (!Directory.Exists(options.RepositoryPath))
        {
            failures.Add($"{Key(nameof(GuardianOptions.RepositoryPath))} '{options.RepositoryPath}' does not exist or is not a directory.");
        }

        if (options.WatchIntervalMs < MinimumWatchIntervalMs)
        {
            failures.Add($"{Key(nameof(GuardianOptions.WatchIntervalMs))} must be at least {MinimumWatchIntervalMs} (was {options.WatchIntervalMs}).");
        }

        if (options.FileChangeDebounceMs < MinimumFileChangeDebounceMs)
        {
            failures.Add($"{Key(nameof(GuardianOptions.FileChangeDebounceMs))} must be at least {MinimumFileChangeDebounceMs} (was {options.FileChangeDebounceMs}).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static string Key(string property) => $"{GuardianOptions.SectionName}:{property}";
}
