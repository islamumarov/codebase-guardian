using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;

namespace CodebaseGuardian.Hosting;

/// <summary>The HTTP transport was configured to listen where Guardian refuses to (see <see cref="GuardianOptions.HttpAllowRemote"/>).</summary>
public sealed class HttpBindingException(string message) : InvalidOperationException(message);

/// <summary>Serves the server over stateless Streamable HTTP.</summary>
public static class HttpHost
{
    private const string McpPath = "/mcp";

    /// <summary>
    /// Builds (does not run) the web app: Kestrel on <see cref="GuardianOptions.HttpUrl"/>, the Guardian MCP server on
    /// <c>/mcp</c> in stateless mode, and <c>GET /healthz</c>. Once started, it logs the MCP endpoint. The options always
    /// describe the running transport, so <see cref="GuardianOptions.Transport"/> is forced to <see cref="GuardianTransport.Http"/>.
    /// </summary>
    /// <param name="args">The raw command line; it is normalized here, exactly once.</param>
    /// <exception cref="HttpBindingException">The URL is empty, or may bind a non-loopback address without both
    /// <see cref="GuardianOptions.HttpAllowRemote"/> and at least one <c>Guardian:Http:ApiKeys</c> entry.</exception>
    public static WebApplication Build(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var arguments = GuardianCommandLine.Normalize(args);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = arguments,
            ContentRootPath = AppContext.BaseDirectory, // see StdioHost: a watched repository must not configure the tool
        });

        builder.Configuration.AddCommandLine(arguments, new Dictionary<string, string>(GuardianCommandLine.SwitchMappings));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{GuardianOptions.SectionName}:{nameof(GuardianOptions.Transport)}"] = nameof(GuardianTransport.Http),
        });

        var guardian = new GuardianOptions();
        builder.Configuration.GetSection(GuardianOptions.SectionName).Bind(guardian);
        var auth = new HttpAuthOptions();
        builder.Configuration.GetSection(HttpAuthOptions.SectionName).Bind(auth);
        var authenticated = auth.ApiKeys.Count > 0;
        EnsureLocalBinding(guardian, builder.Configuration, guardian.HttpAllowRemote && authenticated);

        builder.WebHost.UseUrls(guardian.HttpUrl);

        // Logs go to stderr for parity with stdio, where stdout belongs to the protocol.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services
            .AddCodebaseGuardian(builder.Configuration)
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless);
        if (authenticated)
        {
            builder.Services
                .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
            builder.Services.AddAuthorization();
        }

        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.Lifetime.ApplicationStarted.Register(() => LogEndpoints(app));
        app.Use((context, next) => RejectRebinding(context, next, guardian.HttpAllowRemote));

        if (authenticated)
        {
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapMcp(McpPath).RequireAuthorization();
        }
        else
        {
            app.MapMcp(McpPath);
        }

        app.MapGet("/healthz", () => Results.Text("ok"));
        return app;
    }

    /// <param name="args">The raw command line (not yet normalized).</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            await using var app = Build(args);
            await app.StartAsync();

            // Backstop: Kestrel can also be configured outside Guardian:HttpUrl (ASPNETCORE_HTTP_PORTS, ...). What counts is
            // where it actually listens.
            var allowRemote = app.Services.GetRequiredService<IOptions<GuardianOptions>>().Value.HttpAllowRemote
                && app.Services.GetRequiredService<IOptions<HttpAuthOptions>>().Value.ApiKeys.Count > 0;
            try
            {
                VerifyBoundAddresses(app.Urls, allowRemote);
            }
            catch (HttpBindingException)
            {
                await app.StopAsync();
                throw;
            }

            await app.WaitForShutdownAsync();
            return 0;
        }
        catch (Exception exception) when (exception is OptionsValidationException or HttpBindingException)
        {
            Console.Error.WriteLine($"codebase-guardian: invalid configuration.{Environment.NewLine}{exception.Message}");
            return 2;
        }
    }

    // Kestrel logs only the address it listens on, which is easy to mistake for the endpoint (the root answers 404).
    private static void LogEndpoints(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(HttpHost));
        foreach (var address in app.Urls)
        {
            logger.LogInformation("MCP endpoint: {Endpoint}", address.TrimEnd('/') + McpPath);
        }
    }

    internal static void VerifyBoundAddresses(IEnumerable<string> addresses, bool remoteAllowed)
    {
        if (remoteAllowed)
        {
            return;
        }

        foreach (var address in addresses)
        {
            if (!IsLoopback(address))
            {
                throw new HttpBindingException(RefusalMessage($"the server is listening on '{address}'"));
            }
        }
    }

    private static string RefusalMessage(string what) =>
        $"{what}, which is not a loopback address. Without authentication anyone who can reach that port could read " +
        "the repository and run its checks. Bind 127.0.0.1, ::1 or localhost, or set both Guardian:HttpAllowRemote=true " +
        "(GUARDIAN__HttpAllowRemote=true) and at least one API key in Guardian:Http:ApiKeys (Guardian:Http:ApiKeys:<principal>=<key>).";

    // DNS rebinding: a web page can make a victim's browser reach 127.0.0.1 under an attacker's hostname. Browsers send that
    // hostname in Host (and the page's origin in Origin), so a local-only server refuses anything that is not loopback.
    // With HttpAllowRemote only the Host restriction goes: a present non-loopback Origin is still refused (the MCP
    // Streamable HTTP spec requires Origin validation).
    private static Task RejectRebinding(HttpContext context, RequestDelegate next, bool allowRemote)
    {
        var origin = context.Request.Headers.Origin.ToString();
        var originAllowed = origin.Length == 0 || (Uri.TryCreate(origin, UriKind.Absolute, out var originUri) && IsLoopbackHost(originUri.Host));
        if (originAllowed && (allowRemote || IsLoopbackHost(context.Request.Host.Host)))
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private static void EnsureLocalBinding(GuardianOptions options, IConfiguration configuration, bool remoteAllowed)
    {
        if (string.IsNullOrWhiteSpace(options.HttpUrl))
        {
            throw new HttpBindingException("Guardian:HttpUrl must not be empty.");
        }

        // Remote binding needs HttpAllowRemote and an API key; every path that can bind non-loopback counts as remote.
        if (remoteAllowed)
        {
            return;
        }

        // Kestrel reads this section itself and it wins over the URL Guardian passes, so it would bypass the check below.
        if (configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
        {
            throw new HttpBindingException(RefusalMessage("Kestrel:Endpoints is configured and may bind anywhere"));
        }

        var urls = options.HttpUrl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (urls.Length == 0)
        {
            throw new HttpBindingException("Guardian:HttpUrl must not be empty.");
        }

        foreach (var url in urls)
        {
            if (!IsLoopback(url))
            {
                throw new HttpBindingException(RefusalMessage($"Guardian:HttpUrl '{url}'"));
            }
        }
    }

    private static bool IsLoopback(string url)
    {
        // "http://+:5000" and "http://*:5000" are not valid URIs, and bind every interface anyway.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return IsLoopbackHost(uri.Host);
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}
