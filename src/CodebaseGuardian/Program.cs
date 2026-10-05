using CodebaseGuardian.Hosting;
using Microsoft.Extensions.Configuration;

var arguments = GuardianCommandLine.Normalize(args);

// Only the transport is needed to dispatch; each host builds its own full configuration. Same sources as the hosts:
// appsettings.json next to the binary, environment variables, then the command line.
var transport = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(arguments, new Dictionary<string, string>(GuardianCommandLine.SwitchMappings))
    .Build()
    .GetValue<GuardianTransport>($"{GuardianOptions.SectionName}:{nameof(GuardianOptions.Transport)}");

return transport == GuardianTransport.Http
    ? await HttpHost.RunAsync(arguments)
    : await StdioHost.RunAsync(arguments);
