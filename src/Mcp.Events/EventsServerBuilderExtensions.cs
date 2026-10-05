using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Mcp.Events;

public static class EventsServerBuilderExtensions
{
    /// <summary>
    /// Serves the draft MCP Events extension: declares the <c>events</c> experimental capability and handles
    /// <c>events/list</c> and <c>events/poll</c>. Registers <see cref="EventsOptions"/>, <see cref="TimeProvider.System"/>
    /// (when none is registered) and one <see cref="InMemoryEventLog"/> exposed as <see cref="IEventLog"/> and
    /// <see cref="IEventPublisher"/>.
    /// </summary>
    public static IMcpServerBuilder WithEvents(this IMcpServerBuilder builder, Action<EventsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.Configure(configure);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton(sp => new InMemoryEventLog(
            sp.GetRequiredService<IOptions<EventsOptions>>().Value,
            sp.GetRequiredService<TimeProvider>()));
        builder.Services.TryAddSingleton<IEventLog>(sp => sp.GetRequiredService<InMemoryEventLog>());
        builder.Services.TryAddSingleton<IEventPublisher>(sp => sp.GetRequiredService<InMemoryEventLog>());
        builder.Services.AddSingleton<IConfigureOptions<McpServerOptions>, EventsConfigureOptions>();
        return builder;
    }
}
