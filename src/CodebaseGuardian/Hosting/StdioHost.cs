using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Hosting;

/// <summary>Runs the server on stdin/stdout.</summary>
public static class StdioHost
{
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] normalizedArgs)
    {
        ArgumentNullException.ThrowIfNull(normalizedArgs);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = normalizedArgs,

            // appsettings.json is read from the tool's own directory, not the working directory: the working directory is usually
            // the repository being watched, and a repository must not be able to configure the tool that watches it.
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Configuration sources, lowest to highest priority: appsettings.json (optional), environment variables
        // (GUARDIAN__RepositoryPath maps to Guardian:RepositoryPath), then the command line through its switch mappings.
        builder.Configuration.AddCommandLine(normalizedArgs, new Dictionary<string, string>(GuardianCommandLine.SwitchMappings));

        // stdout carries MCP protocol messages; all logs go to stderr.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services
            .AddCodebaseGuardian(builder.Configuration)
            .WithStdioServerTransport();

        try
        {
            await builder.Build().RunAsync();
            return 0;
        }
        catch (OptionsValidationException exception)
        {
            Console.Error.WriteLine($"codebase-guardian: invalid configuration.{Environment.NewLine}{exception.Message}");
            return 2;
        }
    }
}
