using System.Text.Json.Nodes;

namespace CodebaseGuardian.Tests.Infrastructure;

/// <summary>Helpers for asserting on events through the raw <c>events/poll</c> protocol.</summary>
public static class EventsPolling
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollPause = TimeSpan.FromMilliseconds(50);

    /// <summary>The log position now; events published afterwards are returned by polls that pass it.</summary>
    public static async Task<string> GetCursorAsync(InProcessMcpServer host, CancellationToken cancellationToken)
    {
        var result = await host.RequestAsync("events/poll", new JsonObject { ["name"] = "repo.commit.created" }, cancellationToken);
        return (string)result["cursor"]!;
    }

    /// <summary>One <c>events/poll</c> of <paramref name="name"/> after <paramref name="cursor"/>.</summary>
    public static async Task<(IReadOnlyList<JsonObject> Events, string Cursor)> PollAsync(
        InProcessMcpServer host, string name, string cursor, CancellationToken cancellationToken, JsonObject? arguments = null)
    {
        var parameters = new JsonObject { ["name"] = name, ["cursor"] = cursor, ["maxEvents"] = 1000 };
        if (arguments is not null)
        {
            parameters["arguments"] = arguments;
        }

        var result = await host.RequestAsync("events/poll", parameters, cancellationToken);
        var events = result["events"]!.AsArray().Select(e => e!.AsObject()).ToList();
        return (events, (string)result["cursor"]!);
    }

    /// <summary>Polls until an event of <paramref name="name"/> after <paramref name="cursor"/> satisfies <paramref name="predicate"/>.</summary>
    public static async Task<JsonObject> WaitForAsync(
        InProcessMcpServer host, string name, string cursor, Func<JsonObject, bool>? predicate,
        TimeSpan timeout, CancellationToken cancellationToken, JsonObject? arguments = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var (events, next) = await PollAsync(host, name, cursor, cancellationToken, arguments);
            foreach (var e in events)
            {
                if (predicate is null || predicate(e["data"]!.AsObject()))
                {
                    return e;
                }
            }

            cursor = next;
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"No matching '{name}' event within {timeout.TotalSeconds:0.#} s.");
            }

            await Task.Delay(PollPause, cancellationToken);
        }
    }

    /// <summary>Collects every event of <paramref name="name"/> published after <paramref name="cursor"/> during <paramref name="duration"/>.</summary>
    public static async Task<IReadOnlyList<JsonObject>> CollectAsync(
        InProcessMcpServer host, string name, string cursor, TimeSpan duration, CancellationToken cancellationToken)
    {
        await Task.Delay(duration, cancellationToken);
        return (await PollAsync(host, name, cursor, cancellationToken)).Events;
    }
}
