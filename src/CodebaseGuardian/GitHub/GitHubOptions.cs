using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.GitHub;

/// <summary>GitHub integration settings, bound from <c>Guardian:GitHub</c>.</summary>
public sealed class GitHubOptions
{
    public const string SectionName = "Guardian:GitHub";

    /// <summary>When false, every <see cref="IGitHubClient"/> call throws <see cref="GitHubUnavailableException"/>.</summary>
    public bool Enabled { get; set; } = true;

    public string ApiBaseUrl { get; set; } = "https://api.github.com";

    /// <summary>With <see cref="Repository"/>: the repository to use. When either is unset it is derived from the origin remote.</summary>
    public string? Owner { get; set; }

    public string? Repository { get; set; }

    public bool PollEnabled { get; set; } = true;

    /// <summary>Validated to be at least 15.</summary>
    public int PollIntervalSeconds { get; set; } = 60;
}

internal sealed class GitHubOptionsValidator : IValidateOptions<GitHubOptions>
{
    internal const int MinimumPollIntervalSeconds = 15;

    public ValidateOptionsResult Validate(string? name, GitHubOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.PollIntervalSeconds < MinimumPollIntervalSeconds)
        {
            failures.Add($"{Key(nameof(GitHubOptions.PollIntervalSeconds))} must be at least {MinimumPollIntervalSeconds} (was {options.PollIntervalSeconds}).");
        }

        if (!Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out var baseUrl)
            || (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp))
        {
            failures.Add($"{Key(nameof(GitHubOptions.ApiBaseUrl))} must be an absolute http(s) URL.");
        }

        CheckSegment(failures, nameof(GitHubOptions.Owner), options.Owner);
        CheckSegment(failures, nameof(GitHubOptions.Repository), options.Repository);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void CheckSegment(List<string> failures, string property, string? value)
    {
        if (!string.IsNullOrEmpty(value) && !GitHubRepositoryRef.IsValidSegment(value))
        {
            failures.Add($"{Key(property)} must match {GitHubRepositoryRef.SegmentPattern}.");
        }
    }

    private static string Key(string property) => $"{GitHubOptions.SectionName}:{property}";
}
