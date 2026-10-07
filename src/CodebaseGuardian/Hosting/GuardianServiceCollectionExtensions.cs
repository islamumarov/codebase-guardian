using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using CodebaseGuardian.Checks;
using CodebaseGuardian.Dependencies;
using CodebaseGuardian.Git;
using CodebaseGuardian.GitHub;
using CodebaseGuardian.Json;
using CodebaseGuardian.Processes;
using CodebaseGuardian.Resources;
using CodebaseGuardian.Scanning;
using CodebaseGuardian.Security;
using CodebaseGuardian.Sources;
using CodebaseGuardian.Tools;
using CodebaseGuardian.Watching;
using Mcp.Events;
using Mcp.Skills;
using ModelContextProtocol.Extensions.Tasks;
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
        services.AddSingleton(RepositoryLocation.FromConfiguration(configuration));
        services.AddSingleton<IValidateOptions<GuardianOptions>, GuardianOptionsValidator>();
        services.AddOptions<GuardianOptions>()
            .Bind(configuration.GetSection(GuardianOptions.SectionName))
            .PostConfigure(options => options.RepositoryPath =
                RepositoryLocation.Parse(options.RepositoryPath) is RepositoryLocation.Local local ? local.FullPath : options.RepositoryPath)
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<RemoteOptions>, RemoteOptionsValidator>();
        services.AddOptions<RemoteOptions>()
            .Bind(configuration.GetSection(RemoteOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<HttpAuthOptions>, HttpAuthOptionsValidator>();
        services.AddOptions<HttpAuthOptions>()
            .Bind(configuration.GetSection(HttpAuthOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<WebhookHostingOptions>().Bind(configuration.GetSection(WebhookHostingOptions.SectionName));

        services.Configure<CheckOptions>(configuration.GetSection(CheckOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<GitRepository>();
        services.AddSingleton<IGitRepository>(sp => sp.GetRequiredService<GitRepository>());
        services.AddSingleton<IRepositorySource>(sp => sp.GetRequiredService<GitRepository>());

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

        services.AddSingleton<ScanReportStore>();
        services.AddSingleton<IFullScanService, FullScanService>();

        services.AddGitHubIntegration(configuration);
        services.AddSingleton<IActionConfirmation, ActionConfirmation>();

        // The watcher does nothing unless Guardian:WatchEnabled is set.
        services.AddHostedService<RepositoryWatcher>();

        return services
            .AddMcpServer(options => options.ServerInfo = new Implementation { Name = ServerName, Version = ServerVersion })
            .WithSkills(skills => skills.Directories.Add(
                SkillsDirectoryFor(configuration) ?? Path.Combine(AppContext.BaseDirectory, "skills")))
            .WithEvents(options =>
            {
                options.WebhooksEnabled = WebhooksEnabled(configuration);
                options.Webhooks.AllowInsecureLoopback =
                    (configuration.GetSection(WebhookHostingOptions.SectionName).Get<WebhookHostingOptions>() ?? new WebhookHostingOptions()).AllowInsecureLoopback;
                GuardianEvents.Register(options);
                if (GitHubEnabled(configuration))
                {
                    GuardianEvents.RegisterGitHub(options);
                }
            })
            .WithTasks(
                new InMemoryMcpTaskStore { DefaultPollIntervalMs = 1000, DefaultTimeToLive = TimeSpan.FromHours(1) },
                options => options.ExecutionModeSelector = GuardianTaskModes.Select)
            .WithGuardianTools<RepositoryTools>()
            .WithGuardianTools<EventTools>()
            .WithGuardianTools<CheckTools>()
            .WithGuardianTools<SecurityTools>()
            .WithGuardianTools<DependencyTools>()
            .WithGuardianTools<GitHubTools>()
            .WithGuardianTools<ScanTools>()
            .WithResources<RepositoryResources>()
            .WithResources<CheckResources>()
            .WithResources<ScanResources>();
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

        // The poller is a singleton whenever GitHub is enabled (tests drive it directly); it runs as a hosted
        // service only when polling is enabled too.
        var github = configuration.GetSection(GitHubOptions.SectionName).Get<GitHubOptions>() ?? new GitHubOptions();
        if (github.Enabled)
        {
            services.AddSingleton<GitHubEventPoller>();
            if (github.PollEnabled)
            {
                services.AddHostedService(sp => sp.GetRequiredService<GitHubEventPoller>());
            }
        }

        return services;
    }

    /// <summary>
    /// The SDK's <c>WithTools&lt;T&gt;</c> (2.2.0) plus <see cref="GuardianJsonSchema.CreateOptions"/>, which that overload
    /// cannot pass: without them a value written by a custom converter (a timestamp) is advertised as any value.
    /// </summary>
    private static IMcpServerBuilder WithGuardianTools<TToolType>(this IMcpServerBuilder builder)
    {
        foreach (var method in typeof(TToolType).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is null)
            {
                continue;
            }

            builder.Services.AddSingleton(services => method.IsStatic
                ? McpServerTool.Create(method, options: ToolOptions(services))
                : McpServerTool.Create(method, static request => CreateTarget(request.Services, typeof(TToolType)), ToolOptions(services)));
        }

        return builder;
    }

    private static McpServerToolCreateOptions ToolOptions(IServiceProvider services) =>
        new() { Services = services, SchemaCreateOptions = GuardianJsonSchema.CreateOptions };

    // As in the SDK: a new instance per invocation, with its constructor dependencies from the request's services.
    private static object CreateTarget(IServiceProvider? services, Type type) =>
        services is not null ? ActivatorUtilities.CreateInstance(services, type) : Activator.CreateInstance(type)!;

    /// <summary>Webhooks are offered on authenticated HTTP only (ruling R5); stdio never offers them.</summary>
    private static bool WebhooksEnabled(IConfiguration configuration)
    {
        var transport = configuration.GetValue<GuardianTransport>($"{GuardianOptions.SectionName}:{nameof(GuardianOptions.Transport)}");
        var keys = configuration.GetSection(HttpAuthOptions.SectionName).Get<HttpAuthOptions>()?.ApiKeys.Count ?? 0;
        var hosting = configuration.GetSection(WebhookHostingOptions.SectionName).Get<WebhookHostingOptions>() ?? new WebhookHostingOptions();
        return transport == GuardianTransport.Http && keys > 0 && hosting.Enabled;
    }

    private static bool GitHubEnabled(IConfiguration configuration) =>
        (configuration.GetSection(GitHubOptions.SectionName).Get<GitHubOptions>() ?? new GitHubOptions()).Enabled;

    private static string? SkillsDirectoryFor(IConfiguration configuration)
    {
        var configured = configuration[$"{GuardianOptions.SectionName}:{nameof(GuardianOptions.SkillsDirectory)}"];
        return string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured);
    }

    private static string ResolveServerVersion()
    {
        var assembly = typeof(GuardianServiceCollectionExtensions).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }
}
