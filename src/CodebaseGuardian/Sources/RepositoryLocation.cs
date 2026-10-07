using CodebaseGuardian.GitHub;
using CodebaseGuardian.Hosting;
using Microsoft.Extensions.Configuration;

namespace CodebaseGuardian.Sources;

/// <summary>Where the watched repository lives: a local working tree or a GitHub repository read through the API.</summary>
public abstract record RepositoryLocation
{
    private const string GitHubShorthand = "github:";
    private static readonly string[] GitHubUrlPrefixes = ["https://github.com/", "http://github.com/", "git@github.com:", "ssh://"];

    /// <summary>FullPath is Path.GetFullPath(value); a blank value is kept as-is for validation to report.</summary>
    public sealed record Local(string FullPath) : RepositoryLocation;

    public sealed record GitHub(GitHubRepositoryRef Repository) : RepositoryLocation;

    /// <summary>Looked like a GitHub repository but is not a valid one. Reason is the validation message.</summary>
    public sealed record Invalid(string Value, string Reason) : RepositoryLocation;

    /// <summary>Never throws.</summary>
    public static RepositoryLocation Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Local(value ?? string.Empty);
        }

        if (value.StartsWith(GitHubShorthand, StringComparison.OrdinalIgnoreCase))
        {
            return ParseShorthand(value);
        }

        if (LooksLikeGitHubUrl(value))
        {
            return GitHubRepositoryRef.TryParseRemoteUrl(value, out var repository)
                ? new GitHub(repository)
                : new Invalid(value, $"{Key} '{value}' is not a GitHub repository URL (expected https://github.com/owner/name).");
        }

        try
        {
            return new Local(Path.GetFullPath(value));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new Invalid(value, $"{Key} '{value}' is not a valid path.");
        }
    }

    /// <summary>Parses Guardian:RepositoryPath, defaulting to the current directory like GuardianOptions does.</summary>
    public static RepositoryLocation FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return Parse(configuration[Key] ?? Directory.GetCurrentDirectory());
    }

    private static string Key => $"{GuardianOptions.SectionName}:{nameof(GuardianOptions.RepositoryPath)}";

    private static RepositoryLocation ParseShorthand(string value)
    {
        var parts = value[GitHubShorthand.Length..].Split('/');
        return parts.Length == 2 && GitHubRepositoryRef.IsValidSegment(parts[0]) && GitHubRepositoryRef.IsValidSegment(parts[1])
            ? new GitHub(new GitHubRepositoryRef(parts[0], parts[1]))
            : new Invalid(value, $"{Key} '{value}' must be github:owner/name.");
    }

    private static bool LooksLikeGitHubUrl(string value) =>
        GitHubUrlPrefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        && value.Contains("github.com", StringComparison.OrdinalIgnoreCase);
}
