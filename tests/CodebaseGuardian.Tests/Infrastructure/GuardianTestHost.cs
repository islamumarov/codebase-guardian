using CodebaseGuardian.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace CodebaseGuardian.Tests.Infrastructure;

/// <summary>
/// The full Guardian application (<c>AddCodebaseGuardian</c>) running in-process against a repository.
/// </summary>
public static class GuardianTestHost
{
    /// <summary>
    /// Starts the application on top of <see cref="InProcessMcpServer"/>. <paramref name="configuration"/> overrides the
    /// <c>Guardian:*</c> defaults below; <paramref name="configureServices"/> runs after the application's own
    /// registrations, so it can replace services with fakes.
    /// </summary>
    public static Task<InProcessMcpServer> StartAsync(
        string repositoryPath,
        IDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? configureServices = null,
        McpClientOptions? clientOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryPath);

        // Configuration keys are case-insensitive, so the comparer lets a caller's key replace the default's.
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [Key(nameof(GuardianOptions.RepositoryPath))] = repositoryPath,
            [Key(nameof(GuardianOptions.WatchEnabled))] = "false",
            // No test may reach api.github.com, even on a machine with GITHUB_TOKEN set or `gh` logged in.
            [Key("GitHub:Enabled")] = "false",
            [Key("GitHub:PollEnabled")] = "false",
        };

        // The skills that ship with the application, once they exist: the same folder the application itself
        // defaults to (the build copies it to the output directory).
        var skillsDirectory = Path.Combine(AppContext.BaseDirectory, "skills");
        if (Directory.Exists(skillsDirectory))
        {
            settings[Key(nameof(GuardianOptions.SkillsDirectory))] = skillsDirectory;
        }

        if (configuration is not null)
        {
            foreach (var (key, value) in configuration)
            {
                settings[key] = value;
            }
        }

        return InProcessMcpServer.StartWithBuilderAsync(
            (services, hostConfiguration) =>
            {
                var builder = services.AddCodebaseGuardian(hostConfiguration);
                configureServices?.Invoke(services);
                return builder;
            },
            settings,
            clientOptions,
            cancellationToken);
    }

    private static string Key(string property) => $"{GuardianOptions.SectionName}:{property}";
}
