using System.Security.Cryptography;
using System.Text;

namespace Mcp.Events;

/// <summary>Standard Webhooks signatures: HMAC-SHA256 over <c>id.timestamp.body</c>.</summary>
public static class WebhookSigner
{
    public static string Sign(string messageId, long timestampSeconds, ReadOnlySpan<byte> body, IReadOnlyList<byte[]> keys)
    {
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(keys);
        var prefix = Encoding.UTF8.GetBytes($"{messageId}.{timestampSeconds}.");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));
        return string.Join(' ', keys.Select(key => "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, signed))));
    }
}
