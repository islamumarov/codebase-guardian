using System.IO.Pipelines;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodebaseGuardian.Tests.Infrastructure;

/// <summary>
/// A real MCP server and client connected through in-memory pipes (the stdio transport without a process).
/// The server runs inside a generic host, so hosted services run exactly as they do in the application.
/// </summary>
/// <remarks>Nothing is written to stdout: the host has no logging providers.</remarks>
public sealed class InProcessMcpServer : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly IHost _host;
    private int _disposed;

    private InProcessMcpServer(IHost host, McpClient client)
    {
        _host = host;
        Client = client;
    }

    /// <summary>The client connected to the server, created with the options passed to <c>StartAsync</c>.</summary>
    public McpClient Client { get; }

    /// <summary>The server host's services, for resolving fakes and for publishing from tests.</summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>
    /// Starts a bare MCP server connected to a client. <paramref name="configure"/> registers anything else: it receives
    /// the host's services, its configuration (including <paramref name="configuration"/>) and the MCP server builder,
    /// to which the harness connects the in-memory stream transport, so it must not choose a transport itself.
    /// </summary>
    public static Task<InProcessMcpServer> StartAsync(
        Action<IServiceCollection, IConfiguration, IMcpServerBuilder>? configure = null,
        IDictionary<string, string?>? configuration = null,
        McpClientOptions? clientOptions = null,
        CancellationToken cancellationToken = default) =>
        StartWithBuilderAsync(
            (services, hostConfiguration) =>
            {
                var builder = services.AddMcpServer();
                configure?.Invoke(services, hostConfiguration, builder);
                return builder;
            },
            configuration,
            clientOptions,
            cancellationToken);

    /// <summary>
    /// Entry point for hosts that create the MCP server builder themselves (for example through
    /// <c>AddCodebaseGuardian</c>); the stream transport is connected to the builder that <paramref name="createServerBuilder"/> returns.
    /// </summary>
    internal static async Task<InProcessMcpServer> StartWithBuilderAsync(
        Func<IServiceCollection, IConfiguration, IMcpServerBuilder> createServerBuilder,
        IDictionary<string, string?>? configuration,
        McpClientOptions? clientOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(createServerBuilder);

        Pipe clientToServer = new();
        Pipe serverToClient = new();

        var hostBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        if (configuration is not null)
        {
            hostBuilder.Configuration.AddInMemoryCollection(configuration);
        }

        createServerBuilder(hostBuilder.Services, hostBuilder.Configuration)
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());

        var host = hostBuilder.Build();
        McpClient? client = null;
        try
        {
            // A server that fails to come up would otherwise leave the client waiting for its handshake forever.
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(StartupTimeout);

            await host.StartAsync(startup.Token);
            client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
                clientOptions,
                cancellationToken: startup.Token);

            return new InProcessMcpServer(host, client);
        }
        catch
        {
            // Best effort only: the start-up failure is what the caller has to see, not a secondary clean-up failure
            // (stopping a host whose start failed early can itself throw).
            try
            {
                if (client is not null)
                {
                    await client.DisposeAsync();
                }

                await StopAndDisposeAsync(host);
            }
            catch (Exception)
            {
                // Deliberately ignored, see above.
            }

            throw;
        }
    }

    /// <summary>
    /// Sends a raw JSON-RPC request and returns the result object. Protocol errors surface as
    /// <see cref="ModelContextProtocol.McpProtocolException"/>.
    /// </summary>
    public async Task<JsonObject> RequestAsync(
        string method,
        JsonObject? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var response = await Client.SendRequestAsync(
            new JsonRpcRequest { Method = method, Params = parameters ?? new JsonObject() },
            cancellationToken);

        return response.Result as JsonObject
            ?? throw new InvalidOperationException($"'{method}' did not return a JSON object result.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await Client.DisposeAsync(); // closes the pipes, which ends the server's read loop
        await StopAndDisposeAsync(_host);
    }

    private static async Task StopAndDisposeAsync(IHost host)
    {
        try
        {
            using var shutdown = new CancellationTokenSource(ShutdownTimeout);
            await host.StopAsync(shutdown.Token);
        }
        finally
        {
            if (host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                host.Dispose();
            }
        }
    }
}
