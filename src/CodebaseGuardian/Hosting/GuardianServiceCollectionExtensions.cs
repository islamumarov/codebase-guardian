using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using CodebaseGuardian.Checks;
using CodebaseGuardian.Git;
using CodebaseGuardian.Processes;
using CodebaseGuardian.Resources;
using CodebaseGuardian.Tools;
using CodebaseGuardian.Watching;
using Mcp.Events;
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
        services.AddOptions<McpServerOptions>().Configure<IEnumerable<IInstructionsContributor>>(
            (options, contributors) => options.ServerInstructions = GuardianInstructions.Compose(contributors));

        services.AddSingleton<ICheckCommandResolver, CheckCommandResolver>();
        services.AddSingleton<CheckRunStore>();
        services.AddSingleton<ICheckRunner, CheckRunner>();
        if (configuration.GetValue<bool>($"{GuardianOptions.SectionName}:{nameof(GuardianOptions.AutoChecks)}"))
        {
            services.AddSingleton<IRepositoryChangeHandler, AutoChecksCommitHandler>();
        }

        // The watcher does nothing unless Guardian:WatchEnabled is set.
        services.AddHostedService<RepositoryWatcher>();

        return services
            .AddMcpServer(options => options.ServerInfo = new Implementation { Name = ServerName, Version = ServerVersion })
            .WithEvents(GuardianEvents.Register)
            .WithTools<RepositoryTools>()
            .WithTools<EventTools>()
            .WithTools<CheckTools>()
            .WithResources<RepositoryResources>()
            .WithResources<CheckResources>();
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
