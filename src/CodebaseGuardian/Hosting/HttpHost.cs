using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;

namespace CodebaseGuardian.Hosting;

/// <summary>Serves the server over stateless Streamable HTTP.</summary>
public static class HttpHost
{
    /// <summary>
    /// Builds (does not run) the web app: Kestrel on <see cref="GuardianOptions.HttpUrl"/>, the Guardian MCP server on
    /// <c>/mcp</c> in stateless mode, and <c>GET /healthz</c>. The options always describe the running transport, so
    /// <see cref="GuardianOptions.Transport"/> is forced to <see cref="GuardianTransport.Http"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The URL is not loopback and <see cref="GuardianOptions.HttpAllowRemote"/> is not set.</exception>
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
        EnsureLocalBinding(guardian);

        builder.WebHost.UseUrls(guardian.HttpUrl);

        // Logs go to stderr for parity with stdio, where stdout belongs to the protocol.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services
            .AddCodebaseGuardian(builder.Configuration)
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        if (!guardian.HttpAllowRemote)
        {
            app.Use(RejectNonLoopbackHostAndOrigin);
        }

        app.MapMcp("/mcp");
        app.MapGet("/healthz", () => Results.Text("ok"));
        return app;
    }

    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            await using var app = Build(args);
            await app.RunAsync();
            return 0;
        }
        catch (Exception exception) when (exception is OptionsValidationException or InvalidOperationException)
        {
            Console.Error.WriteLine($"codebase-guardian: {exception.Message}");
            return 2;
        }
    }

    // DNS rebinding: a web page can make a victim's browser reach 127.0.0.1 under an attacker's hostname. Browsers send that
    // hostname in Host (and the page's origin in Origin), so a local-only server refuses anything that is not loopback.
    private static Task RejectNonLoopbackHostAndOrigin(HttpContext context, RequestDelegate next)
    {
        var origin = context.Request.Headers.Origin.ToString();
        var originAllowed = origin.Length == 0 || (Uri.TryCreate(origin, UriKind.Absolute, out var originUri) && IsLoopbackHost(originUri.Host));
        if (originAllowed && IsLoopbackHost(context.Request.Host.Host))
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private static void EnsureLocalBinding(GuardianOptions options)
    {
        if (options.HttpAllowRemote)
        {
            return;
        }

        foreach (var url in options.HttpUrl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsLoopback(url))
            {
                throw new InvalidOperationException(
                    $"Guardian:HttpUrl '{url}' is not a loopback address. The HTTP transport has no authentication yet, so " +
                    "anyone who can reach that port could read the repository and run its checks. Bind 127.0.0.1, ::1 or localhost, " +
                    "or set Guardian:HttpAllowRemote=true (GUARDIAN__HttpAllowRemote=true) to accept the risk.");
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
