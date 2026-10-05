using CodebaseGuardian.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace CodebaseGuardian.Tests.Infrastructure;

/// <summary>
/// The full Guardian application on its stateless Streamable HTTP transport, served by a real Kestrel on a free
/// loopback port (long-lived SSE requests and their cancellation are verified on Kestrel, not on TestServer).
/// </summary>
public sealed class GuardianHttpTestHost : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly WebApplication _app;
    private readonly List<McpClient> _clients = [];
    private int _disposed;

    private GuardianHttpTestHost(WebApplication app, Uri endpoint)
    {
        _app = app;
        Endpoint = endpoint;
    }

    /// <summary>The MCP endpoint, <c>http://127.0.0.1:{port}/mcp</c>.</summary>
    public Uri Endpoint { get; }

    /// <summary>The application's root URL; <c>/healthz</c> hangs off it.</summary>
    public Uri BaseAddress => new(Endpoint, "/");

    public IServiceProvider Services => _app.Services;

    /// <summary>
    /// Starts the application. <paramref name="configuration"/> overrides the <c>Guardian:*</c> defaults below (the same
    /// defaults as <see cref="GuardianTestHost"/>); <paramref name="configureServices"/> runs after the application's own
    /// registrations, so it can replace services with fakes.
    /// </summary>
    public static async Task<GuardianHttpTestHost> StartAsync(
        string repositoryPath,
        IDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? configureServices = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryPath);

        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [Key(nameof(GuardianOptions.RepositoryPath))] = repositoryPath,
            [Key(nameof(GuardianOptions.WatchEnabled))] = "false",
            [Key(nameof(GuardianOptions.HttpUrl))] = "http://127.0.0.1:0",
            [Key("GitHub:Enabled")] = "false",
            [Key("GitHub:PollEnabled")] = "false",
            ["Logging:LogLevel:Default"] = "None", // keeps the test output clean
        };

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

        var args = settings.Select(pair => $"--{pair.Key}={pair.Value}").ToArray();
        var app = HttpHost.Build(args, configureServices);
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(StartupTimeout);
            await app.StartAsync(startup.Token);

            var address = app.Urls.First();
            return new GuardianHttpTestHost(app, new Uri(new Uri(address), "/mcp"));
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public async Task<McpClient> ConnectAsync(McpClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = Endpoint, TransportMode = HttpTransportMode.StreamableHttp }),
            options,
            cancellationToken: cancellationToken);
        _clients.Add(client);
        return client;
    }

    /// <summary>Connects with <c>Authorization: Bearer &lt;token&gt;</c>.</summary>
    public async Task<McpClient> ConnectAsync(string bearerToken, McpClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bearerToken);
        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = Endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearerToken}" },
            }),
            options,
            cancellationToken: cancellationToken);
        _clients.Add(client);
        return client;
    }

    /// <summary>A plain client rooted at <see cref="BaseAddress"/>, with no authorization header. The caller disposes it.</summary>
    public HttpClient CreateHttpClient() => new() { BaseAddress = BaseAddress };

    /// <summary>Stops the server while clients stay connected, as an operator shutting the process down would.</summary>
    public async Task StopServerAsync()
    {
        using var shutdown = new CancellationTokenSource(ShutdownTimeout);
        await _app.StopAsync(shutdown.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }

        try
        {
            await StopServerAsync();
        }
        finally
        {
            await _app.DisposeAsync();
        }
    }

    private static string Key(string property) => $"{GuardianOptions.SectionName}:{property}";
}
