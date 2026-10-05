using System.Text.Json.Nodes;

namespace Mcp.Events;

/// <summary>
/// One webhook subscription. Identity fields are immutable; the rest is mutable state that must be changed while holding
/// <see cref="WebhookSubscriptionStore.SyncRoot"/>.
/// </summary>
public sealed class WebhookSubscription
{
    public required string Id { get; init; }
    public required string Principal { get; init; }
    public required Uri Url { get; init; }
    public required string Name { get; init; }
    public required JsonObject Arguments { get; init; }
    public byte[] Secret { get; set; } = [];
    public byte[]? PreviousSecret { get; set; }
    public DateTimeOffset? PreviousSecretExpiresAt { get; set; }
    public DateTimeOffset RefreshBefore { get; set; }
    /// <summary>Watermark: every event with Sequence &lt;= Position is acked or abandoned.</summary>
    public long Position { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset? LastDeliveryAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? FailedSince { get; set; }

    /// <summary>The current secret, plus the previous one while its rotation grace lasts.</summary>
    public IReadOnlyList<byte[]> SigningKeys(DateTimeOffset now) =>
        PreviousSecret is { } previous && PreviousSecretExpiresAt is { } expires && now < expires
            ? [Secret, previous]
            : [Secret];
}
