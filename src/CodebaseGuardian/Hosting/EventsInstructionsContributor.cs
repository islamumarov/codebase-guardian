using System.Text;
using Mcp.Events;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Hosting;

/// <summary>Lists the events the server publishes and the polling fallback (Order 20).</summary>
internal sealed class EventsInstructionsContributor(IOptions<EventsOptions> options) : IInstructionsContributor
{
    public int Order => 20;

    public string? GetSection()
    {
        var text = new StringBuilder("Events published by this server:");
        foreach (var definition in options.Value.Definitions)
        {
            text.Append("\n- ").Append(definition.Name);
        }

        text.Append("\nIf your client does not support `events/*`, call the `poll_events` tool.");
        return text.ToString();
    }
}
