namespace Mcp.Events;

/// <summary>
/// Outcome of one webhook POST. <see cref="ErrorCategory"/> is one of connection_refused, timeout, tls_error, http_4xx, http_5xx
/// (null when delivered). <see cref="Body"/> is at most the first 4 KiB of the response, used only for the challenge
/// comparison and never surfaced.
/// </summary>
public sealed record WebhookSendResult(bool Delivered, int? StatusCode, string? ErrorCategory, string? Body);

public interface IWebhookSender
{
    /// <summary>
    /// POSTs <paramref name="body"/> with Standard Webhooks headers (<c>webhook-id</c>, <c>webhook-timestamp</c>,
    /// <c>webhook-signature</c> over all <paramref name="keys"/>) and <c>X-MCP-Subscription-Id</c>. Endpoint failures are
    /// returned as a category, never thrown.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    Task<WebhookSendResult> SendAsync(Uri url, string messageId, ReadOnlyMemory<byte> body, string subscriptionId,
        IReadOnlyList<byte[]> keys, CancellationToken cancellationToken);
}
