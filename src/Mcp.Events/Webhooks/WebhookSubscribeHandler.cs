using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Mcp.Events;

/// <summary>
/// Serves <c>events/subscribe</c> and <c>events/unsubscribe</c> (wire-format B7, B9, B10, B12; rulings R2, R5, R9 to R12):
/// validation, endpoint verification by challenge handshake, idempotent upsert and TTL grants. Delivering events is not
/// done here.
/// </summary>
internal sealed class WebhookSubscribeHandler(
    EventsOptions options,
    IEventLog log,
    WebhookSubscriptionStore store,
    IWebhookSender sender,
    TimeProvider time)
{
    private readonly WebhookOptions _webhooks = options.Webhooks;
    private readonly HashSet<(string Principal, string Url)> _verified = [];
    private readonly object _verifiedGate = new();

    public async ValueTask<JsonNode?> SubscribeAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        var principal = options.PrincipalResolver(request)
            ?? throw Error("events/subscribe requires an authenticated principal", EventsProtocol.Forbidden);
        var parameters = request.Params as JsonObject;
        var parsed = EventsRequestParser.Parse(parameters, options, log, EventDelivery.Webhook);

        var delivery = parameters!["delivery"] as JsonObject ?? throw InvalidParams("params.delivery must be an object.");
        CheckMode(delivery);
        var urlText = delivery["url"] is JsonValue u && u.TryGetValue<string>(out var s) ? s : null;
        if (!WebhookUrlPolicy.TryValidate(urlText, _webhooks.AllowInsecureLoopback, out var url, out var problem))
        {
            throw InvalidParams($"params.delivery.url {problem ?? "is invalid"}.");
        }

        var secretText = delivery["secret"] is JsonValue sv && sv.TryGetValue<string>(out var secretValue) ? secretValue : null;
        if (!WebhookSecret.TryParse(secretText, out var secret))
        {
            throw InvalidParams("params.delivery.secret must be whsec_ followed by base64 of 24–64 bytes.");
        }

        var grant = ReadGrant(parameters);
        var id = SubscriptionKey.ComputeId(principal, urlText!, parsed.Definition.Name, parsed.Arguments);
        var exists = store.TryGet(id, out _);
        if (!exists)
        {
            ThrowIfAtLimit(principal);
        }

        if (!IsVerified(principal, urlText!))
        {
            await VerifyAsync(url, id, secret, cancellationToken).ConfigureAwait(false);
            lock (_verifiedGate) _verified.Add((principal, urlText!));
        }

        var now = time.GetUtcNow();
        while (true)
        {
            // Mutation happens under the store lock; Changed is raised after it is released.
            if (store.TryGet(id, out var existing))
            {
                bool truncated = false;
                JsonObject status;
                long position;
                DateTimeOffset refreshBefore;
                DateTimeOffset? failedSince;
                lock (store.SyncRoot)
                {
                    if (!CryptographicOperations.FixedTimeEquals(existing.Secret, secret))
                    {
                        existing.PreviousSecret = existing.Secret;
                        existing.PreviousSecretExpiresAt = now + _webhooks.SecretRotationGrace;
                        existing.Secret = secret;
                    }

                    if (parsed.Cursor is not null)
                    {
                        var (sequence, gap) = log.Seek(parsed.Cursor, parsed.MaxAge);
                        if (sequence > existing.Position)
                        {
                            existing.Position = sequence;
                            truncated = gap;
                        }
                    }

                    existing.Active = true;
                    existing.RefreshBefore = now + grant;
                    status = DeliveryStatus(existing);
                    position = existing.Position;
                    refreshBefore = existing.RefreshBefore;
                    failedSince = existing.FailedSince;
                }

                store.NotifyChanged(existing);
                var refreshed = Result(id, refreshBefore, position, truncated);
                refreshed["deliveryStatus"] = status;
                if (failedSince is { } since)
                {
                    refreshed["failedSince"] = EventJson.FormatTimestamp(since);
                }

                return refreshed;
            }

            var (start, startGap) = log.Seek(parsed.Cursor, parsed.MaxAge);
            var created = new WebhookSubscription
            {
                Id = id,
                Principal = principal,
                Url = url,
                Name = parsed.Definition.Name,
                Arguments = (JsonObject?)parsed.Arguments?.DeepClone() ?? new JsonObject(),
                Secret = secret,
                Position = start,
                RefreshBefore = now + grant,
            };
            switch (store.TryAdd(created, _webhooks.MaxSubscriptionsPerPrincipal))
            {
                case WebhookAddResult.Added:
                    return Result(id, created.RefreshBefore, start, startGap);
                case WebhookAddResult.LimitReached:
                    throw LimitError();
                // WebhookAddResult.Exists: a concurrent subscribe created it first; refresh that one.
            }
        }
    }

    private JsonObject Result(string id, DateTimeOffset refreshBefore, long position, bool truncated) => new()
    {
        ["resultType"] = EventJson.ResultTypeComplete,
        ["id"] = id,
        ["refreshBefore"] = EventJson.FormatTimestamp(refreshBefore),
        ["cursor"] = log.CursorAt(position),
        ["truncated"] = truncated,
    };

    private McpProtocolException LimitError() =>
        new("Subscription limit reached.", (McpErrorCode)EventsProtocol.ResourceExhausted)
        {
            Data = { ["limit"] = "subscriptions", ["max"] = _webhooks.MaxSubscriptionsPerPrincipal },
        };

    public ValueTask<JsonNode?> UnsubscribeAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        var principal = options.PrincipalResolver(request)
            ?? throw Error("events/unsubscribe requires an authenticated principal", EventsProtocol.Forbidden);
        var parameters = request.Params as JsonObject;
        var name = parameters?["name"] is JsonValue n && n.TryGetValue<string>(out var nameValue) ? nameValue : null;
        if (name is null)
        {
            throw InvalidParams("params.name must be a string.");
        }

        JsonObject? arguments = null;
        if (parameters!.TryGetPropertyValue("arguments", out var argumentsNode) && argumentsNode is not null)
        {
            arguments = argumentsNode as JsonObject ?? throw InvalidParams("params.arguments must be an object.");
        }

        var url = (parameters["delivery"] as JsonObject)?["url"] is JsonValue uv && uv.TryGetValue<string>(out var urlValue) ? urlValue : null;
        if (url is null)
        {
            throw InvalidParams("params.delivery.url must be a string.");
        }

        if (store.Remove(SubscriptionKey.ComputeId(principal, url, name, arguments)))
        {
            // The pair is no longer in use: forget its verification so a later subscribe proves the endpoint again.
            if (!store.Snapshot().Any(x => x.Principal == principal && x.Url.OriginalString == url))
            {
                lock (_verifiedGate) _verified.Remove((principal, url));
            }
        }
        else
        {
            throw new McpProtocolException("No such subscription.", (McpErrorCode)EventsProtocol.NotFound)
            {
                Data = { ["kind"] = "subscription" },
            };
        }

        return ValueTask.FromResult<JsonNode?>(new JsonObject { ["resultType"] = EventJson.ResultTypeComplete });
    }

    private void CheckMode(JsonObject delivery)
    {
        if (!delivery.TryGetPropertyValue("mode", out var modeNode) || modeNode is null)
        {
            return;
        }

        var mode = modeNode is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
        switch (mode)
        {
            case "webhook":
                return;
            case "poll" or "push":
                throw new McpProtocolException($"Delivery mode '{mode}' is not supported by events/subscribe.", (McpErrorCode)EventsProtocol.Unsupported)
                {
                    Data = { ["feature"] = "deliveryMode", ["value"] = mode },
                };
            default:
                throw InvalidParams("params.delivery.mode must be \"webhook\".");
        }
    }

    /// <summary>TTL negotiation (ruling R9): absent is the default, null or too long is the maximum, too short the minimum.</summary>
    private TimeSpan ReadGrant(JsonObject parameters)
    {
        if (!parameters.TryGetPropertyValue("ttlMs", out var node))
        {
            return Clamp(_webhooks.DefaultTtl);
        }

        if (node is null)
        {
            return _webhooks.MaxTtl;
        }

        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var ms)
            && double.IsFinite(ms) && ms >= 0 && ms == Math.Floor(ms))
        {
            return ms >= _webhooks.MaxTtl.TotalMilliseconds ? _webhooks.MaxTtl : Clamp(TimeSpan.FromMilliseconds(ms));
        }

        throw InvalidParams("params.ttlMs must be a non-negative integer or null.");
    }

    private TimeSpan Clamp(TimeSpan ttl) => ttl < _webhooks.MinTtl ? _webhooks.MinTtl : ttl > _webhooks.MaxTtl ? _webhooks.MaxTtl : ttl;

    private void ThrowIfAtLimit(string principal)
    {
        if (store.CountFor(principal) >= _webhooks.MaxSubscriptionsPerPrincipal)
        {
            throw LimitError();
        }
    }

    private bool IsVerified(string principal, string url)
    {
        lock (_verifiedGate) return _verified.Contains((principal, url));
    }

    /// <summary>Wire-format B9 handshake: the endpoint must echo the nonce in a 2xx body. Nothing from the response is surfaced.</summary>
    private async Task VerifyAsync(Uri url, string subscriptionId, byte[] secret, CancellationToken cancellationToken)
    {
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["type"] = "verification", ["challenge"] = nonce }.ToJsonString());
        var messageId = "msg_verification_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

        var result = await sender.SendAsync(url, messageId, body, subscriptionId, [secret], cancellationToken).ConfigureAwait(false);
        if (!result.Delivered && result.ErrorCategory is { } category)
        {
            throw CallbackError(category);
        }

        if (!Echoes(result.Body, nonce))
        {
            throw CallbackError("challenge_failed");
        }
    }

    private static bool Echoes(string? responseBody, string nonce)
    {
        if (string.IsNullOrEmpty(responseBody))
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(responseBody) is JsonObject obj
                && obj["challenge"] is JsonValue v && v.GetValueKind() == JsonValueKind.String
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(v.GetValue<string>()), Encoding.UTF8.GetBytes(nonce));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return false; // malformed, or duplicate keys
        }
    }

    private static JsonObject DeliveryStatus(WebhookSubscription s) => new()
    {
        ["active"] = s.Active,
        ["lastDeliveryAt"] = s.LastDeliveryAt is { } at ? EventJson.FormatTimestamp(at) : null,
        ["lastError"] = s.LastError,
    };

    private static McpProtocolException CallbackError(string reason) =>
        new("The callback endpoint could not be verified.", (McpErrorCode)EventsProtocol.CallbackEndpointError)
        {
            Data = { ["reason"] = reason },
        };

    private static McpProtocolException Error(string message, int code) => new(message, (McpErrorCode)code);

    private static McpProtocolException InvalidParams(string message) => new(message, McpErrorCode.InvalidParams);
}
