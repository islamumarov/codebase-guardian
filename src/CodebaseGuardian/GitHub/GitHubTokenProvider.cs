using CodebaseGuardian.Hosting;
using CodebaseGuardian.Processes;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.GitHub;

public sealed class GitHubTokenProvider : IGitHubTokenProvider
{
    private static readonly TimeSpan GhTimeout = TimeSpan.FromSeconds(10);

    private readonly IProcessRunner _runner;
    private readonly IOptions<GuardianOptions> _options;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private volatile string? _token;

    public GitHubTokenProvider(IProcessRunner runner, IOptions<GuardianOptions> options)
        : this(runner, options, Environment.GetEnvironmentVariable)
    {
    }

    // The environment seam lets tests run without touching the real environment.
    internal GitHubTokenProvider(IProcessRunner runner, IOptions<GuardianOptions> options, Func<string, string?> getEnvironmentVariable)
    {
        _runner = runner;
        _options = options;
        _getEnvironmentVariable = getEnvironmentVariable;
    }

    public async ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is { } cached)
        {
            return cached;
        }

        var fromEnvironment = _getEnvironmentVariable("GITHUB_TOKEN")?.Trim();
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return _token = fromEnvironment;
        }

        try
        {
            var result = await _runner.RunAsync(
                new ProcessSpec("gh", ["auth", "token"], _options.Value.RepositoryPath) { Timeout = GhTimeout },
                cancellationToken);
            var token = result is { ExitCode: 0, TimedOut: false } ? result.StandardOutput.Trim() : string.Empty;
            return token.Length == 0 ? null : _token = token;
        }
        catch (ExecutableNotFoundException)
        {
            return null;
        }
    }
}
