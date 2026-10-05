using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CodebaseGuardian.Tests.Infrastructure;

/// <summary>A webhook endpoint on a loopback Kestrel port that records every POST and answers verification challenges.</summary>
public sealed class WebhookReceiver : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly object _gate = new();
    private readonly List<ReceivedWebhook> _received = [];

    private WebhookReceiver(WebApplication app, Uri url, string secret)
    {
        _app = app;
        Url = url;
        Secret = secret;
    }

    /// <summary><c>http://127.0.0.1:&lt;port&gt;/hooks</c>.</summary>
    public Uri Url { get; }

    /// <summary><c>whsec_</c> + base64 of 32 random bytes.</summary>
    public string Secret { get; }

    /// <summary>Replies <c>{"challenge": nonce}</c> to verification envelopes.</summary>
    public bool EchoChallenges { get; set; } = true;

    /// <summary>When set, replaces the challenge reply body; receives the nonce.</summary>
    public Func<string, string>? ChallengeReply { get; set; }

    /// <summary>Status code to answer with; default 200.</summary>
    public Func<ReceivedWebhook, int>? StatusFor { get; set; }

    /// <summary>When set, requests wait for it before responding.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public IReadOnlyList<ReceivedWebhook> Received
    {
        get { lock (_gate) return [.. _received]; }
    }

    public static async Task<WebhookReceiver> StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        WebhookReceiver? receiver = null;
        app.Run(context => receiver!.HandleAsync(context));
        await app.StartAsync(ct);
        var port = new Uri(app.Urls.First()).Port;
        receiver = new WebhookReceiver(
            app, new Uri($"http://127.0.0.1:{port}/hooks"), "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        return receiver;
    }

    public async Task<ReceivedWebhook> WaitForAsync(Func<ReceivedWebhook, bool> match, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            lock (_gate)
            {
                var found = _received.FirstOrDefault(match);
                if (found is not null) return found;
            }

            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("No matching webhook request arrived.");
            await Task.Delay(20, cancellationToken);
        }
    }

    /// <summary>Independent HMAC check (does not use the library's signer): true when any v1 entry matches <paramref name="key"/>.</summary>
    public static bool SignatureValid(ReceivedWebhook request, byte[] key)
    {
        if (!request.Headers.TryGetValue("webhook-id", out var id)
            || !request.Headers.TryGetValue("webhook-timestamp", out var timestamp)
            || !request.Headers.TryGetValue("webhook-signature", out var signatures))
        {
            return false;
        }

        var signed = Encoding.UTF8.GetBytes($"{id}.{timestamp}.").Concat(request.Body).ToArray();
        var expected = "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, signed));
        return signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(expected, StringComparer.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        var headers = context.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString(), StringComparer.Ordinal);
        var request = new ReceivedWebhook(headers, buffer.ToArray(), DateTimeOffset.UtcNow);
        lock (_gate) _received.Add(request);

        if (Gate is { } gate) await gate.Task.WaitAsync(context.RequestAborted);

        context.Response.StatusCode = StatusFor?.Invoke(request) ?? 200;
        if (EchoChallenges && TryChallenge(request, out var nonce))
        {
            context.Response.ContentType = "application/json";
            var reply = ChallengeReply?.Invoke(nonce!) ?? new JsonObject { ["challenge"] = nonce }.ToJsonString();
            await context.Response.WriteAsync(reply, context.RequestAborted);
        }
    }

    private static bool TryChallenge(ReceivedWebhook request, out string? nonce)
    {
        nonce = null;
        try
        {
            if (request.Json["type"]?.GetValue<string>() != "verification") return false;
            nonce = request.Json["challenge"]?.GetValue<string>();
            return nonce is not null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}

public sealed record ReceivedWebhook(IReadOnlyDictionary<string, string> Headers, byte[] Body, DateTimeOffset ReceivedAt)
{
    public JsonObject Json => (JsonObject)JsonNode.Parse(Body)!;
}
