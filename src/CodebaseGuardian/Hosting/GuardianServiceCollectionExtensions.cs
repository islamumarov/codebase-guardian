using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using CodebaseGuardian.Checks;
using CodebaseGuardian.Dependencies;
using CodebaseGuardian.Git;
using CodebaseGuardian.GitHub;
using CodebaseGuardian.Processes;
using CodebaseGuardian.Resources;
using CodebaseGuardian.Security;
using CodebaseGuardian.Tools;
using CodebaseGuardian.Watching;
using Mcp.Events;
using Mcp.Skills;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodebaseGuardian.Hosting;

public static class GuardianServiceCollectionExtensions
{
    internal const string ServerName = "codebase-guardian";

    private static readonly string ServerVersion = ResolveServerVersion();

    /// <summary>
    /// Composition root: binds and validates <see cref="GuardianOptions"/> from the <c>Guardian</c> section,
    /// registers the core services and the MCP server, and composes the server instructions from every
    /// <see cref="IInstructionsContributor"/>. It does not choose a transport; the caller adds one to the returned builder.
    /// </summary>
    public static IMcpServerBuilder AddCodebaseGuardian(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // ValidateOnStart makes an invalid configuration fail the host start itself, also for transports that only
        // build the MCP server later (per request over HTTP), instead of failing on the first client request.
        services.AddSingleton<IValidateOptions<GuardianOptions>, GuardianOptionsValidator>();
        services.AddOptions<GuardianOptions>()
            .Bind(configuration.GetSection(GuardianOptions.SectionName))
            .PostConfigure(options => options.RepositoryPath = ToFullPath(options.RepositoryPath))
            .ValidateOnStart();

        services.Configure<CheckOptions>(configuration.GetSection(CheckOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IGitRepository, GitRepository>();

        // Instructions are composed when McpServerOptions are first built, so contributors that are
        // registered after this call (by tests, or by features added later) are included.
        services.AddSingleton<IInstructionsContributor, GuardianInstructions>();
        services.AddSingleton<IInstructionsContributor, SkillsInstructionsContributor>();
        services.AddSingleton<IInstructionsContributor, EventsInstructionsContributor>();
        services.AddOptions<McpServerOptions>().Configure<IEnumerable<IInstructionsContributor>>(
            (options, contributors) => options.ServerInstructions = GuardianInstructions.Compose(contributors));

        services.AddSingleton<ICheckCommandResolver, CheckCommandResolver>();
        services.AddSingleton<CheckRunStore>();
        services.AddSingleton<ICheckRunner, CheckRunner>();
        if (configuration.GetValue<bool>($"{GuardianOptions.SectionName}:{nameof(GuardianOptions.AutoChecks)}"))
        {
            services.AddSingleton<AutoChecksCommitHandler>();
            services.AddSingleton<IRepositoryChangeHandler>(sp => sp.GetRequiredService<AutoChecksCommitHandler>());
            services.AddHostedService(sp => sp.GetRequiredService<AutoChecksCommitHandler>());
        }

        services.AddSingleton<ISecretScanner, SecretScanner>();
        services.AddSingleton<IRepositoryChangeHandler, SecretScanCommitHandler>();

        services.AddSingleton<IDependencyAuditor, DependencyAuditor>();

        services.AddGitHubIntegration(configuration);

        // The watcher does nothing unless Guardian:WatchEnabled is set.
        services.AddHostedService<RepositoryWatcher>();

        return services
            .AddMcpServer(options => options.ServerInfo = new Implementation { Name = ServerName, Version = ServerVersion })
            .WithSkills(skills => skills.Directories.Add(
                SkillsDirectoryFor(configuration) ?? Path.Combine(AppContext.BaseDirectory, "skills")))
            .WithEvents(GuardianEvents.Register)
            .WithTools<RepositoryTools>()
            .WithTools<EventTools>()
            .WithTools<CheckTools>()
            .WithTools<SecurityTools>()
            .WithTools<DependencyTools>()
            .WithResources<RepositoryResources>()
            .WithResources<CheckResources>();
    }

    /// <summary>Binds <see cref="GitHubOptions"/> and registers the GitHub token provider, repository resolver and REST client.</summary>
    internal static IServiceCollection AddGitHubIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<GitHubOptions>, GitHubOptionsValidator>();
        services.AddOptions<GitHubOptions>()
            .Bind(configuration.GetSection(GitHubOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IGitHubTokenProvider, GitHubTokenProvider>();
        services.AddSingleton<IGitHubRepositoryResolver, GitHubRepositoryResolver>();
        services.AddHttpClient<IGitHubClient, GitHubClient>(GitHubClient.HttpClientName);
        return services;
    }

    private static string? SkillsDirectoryFor(IConfiguration configuration)
    {
        var configured = configuration[$"{GuardianOptions.SectionName}:{nameof(GuardianOptions.SkillsDirectory)}"];
        return string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured);
    }

    // Empty values are left for validation to report; GetFullPath would throw for them.
    private static string ToFullPath(string path) => string.IsNullOrWhiteSpace(path) ? path : Path.GetFullPath(path);

    private static string ResolveServerVersion()
    {
        var assembly = typeof(GuardianServiceCollectionExtensions).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }
}
