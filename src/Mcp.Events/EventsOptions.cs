using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace Mcp.Events;

public sealed partial class EventsOptions
{
    private readonly List<EventDefinition> _definitions = [];

    public int Capacity { get; set; } = 10_000;
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
    public int DefaultMaxEvents { get; set; } = 100;
    public int MaxEventsLimit { get; set; } = 1_000;
    /// <summary>Advertised as <c>nextPollMs</c>.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Push heartbeat interval (ruling R6).</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Epic 2; adds "webhook" to the advertised delivery modes (ruling R5).</summary>
    public bool WebhooksEnabled { get; set; }

    /// <summary>Webhook subscription and delivery settings (used when <see cref="WebhooksEnabled"/> is set).</summary>
    public WebhookOptions Webhooks { get; } = new();

    /// <summary>Resolves the authenticated principal of a request; <see langword="null"/> for anonymous requests.</summary>
    public Func<JsonRpcRequest, string?> PrincipalResolver { get; set; } = EventsPrincipal.FromRequest;

    public IReadOnlyList<EventDefinition> Definitions => _definitions;

    /// <summary>Registers an event type. Intended for startup configuration, before the log is shared across threads.</summary>
    /// <exception cref="ArgumentException">Invalid or duplicate name.</exception>
    public void Define(EventDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!NamePattern().IsMatch(definition.Name ?? ""))
            throw new ArgumentException($"Invalid event name '{definition.Name}'; expected dotted lowercase segments such as 'repo.push_received'.", nameof(definition));
        if (_definitions.Any(d => d.Name == definition.Name))
            throw new ArgumentException($"Event '{definition.Name}' is already defined.", nameof(definition));
        _definitions.Add(definition);
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+\z")]
    private static partial Regex NamePattern();
}
