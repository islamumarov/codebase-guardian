using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.Events;

public static class EventsServerBuilderExtensions
{
    /// <summary>
    /// Serves the draft MCP Events extension: declares the <c>events</c> experimental capability and handles
    /// <c>events/list</c>, <c>events/poll</c>, <c>events/stream</c>, <c>events/subscribe</c> and <c>events/unsubscribe</c>. Registers <see cref="EventsOptions"/>, <see cref="TimeProvider.System"/>
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
        builder.Services.TryAddSingleton<WebhookSubscriptionStore>();
        builder.Services.TryAddSingleton<IWebhookSender>(sp => new WebhookHttpSender(
            sp.GetRequiredService<IOptions<EventsOptions>>().Value.Webhooks, sp.GetRequiredService<TimeProvider>()));
        builder.Services.TryAddSingleton(sp => new WebhookSubscribeHandler(
            sp.GetRequiredService<IOptions<EventsOptions>>().Value,
            sp.GetRequiredService<IEventLog>(),
            sp.GetRequiredService<WebhookSubscriptionStore>(),
            sp.GetRequiredService<IWebhookSender>(),
            sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddHostedService<WebhookDispatcher>();
        builder.Services.AddSingleton<IConfigureOptions<McpServerOptions>, EventsConfigureOptions>();
        // Stream handlers have no server property on the request; stash the request-bound server for them (SDK notes section 3).
        builder.WithMessageFilters(filters => filters.AddIncomingFilter(next => async (context, cancellationToken) =>
        {
            if (context.JsonRpcMessage is JsonRpcRequest)
            {
                context.Items[EventStreamHandler.ServerItemKey] = context.Server;
            }

            await next(context, cancellationToken).ConfigureAwait(false);
        }));
        return builder;
    }
}
