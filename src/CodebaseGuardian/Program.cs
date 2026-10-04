using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries MCP protocol messages; all logs go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "codebase-guardian", Version = "0.1.0" })
    .WithStdioServerTransport();

await builder.Build().RunAsync();
