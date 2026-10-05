using System.Net;
using System.Net.Sockets;

namespace Mcp.Events;

/// <summary>Addresses a webhook must never be delivered to (IANA special-purpose ranges that are not globally routable).</summary>
public static class WebhookAddressPolicy
{
    public static bool IsBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        Span<byte> b = stackalloc byte[16];
        if (!address.TryWriteBytes(b, out var length))
        {
            return true;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsBlockedV4(b[0], b[1], b[2], b[3]),
            AddressFamily.InterNetworkV6 => IsBlockedV6(b[..length]),
            _ => true,
        };
    }

    private static bool IsBlockedV4(byte a, byte b, byte c, byte d) =>
        a == 0                                        // 0.0.0.0/8
        || a == 10                                    // 10/8
        || (a == 100 && (b & 0xC0) == 64)             // 100.64/10
        || a == 127                                   // 127/8
        || (a == 169 && b == 254)                     // 169.254/16
        || (a == 172 && (b & 0xF0) == 16)             // 172.16/12
        || (a == 192 && b == 0 && c == 0)             // 192.0.0/24
        || (a == 192 && b == 168)                     // 192.168/16
        || (a == 198 && (b & 0xFE) == 18)             // 198.18/15
        || a >= 224;                                  // 224/4, 240/4, broadcast

    private static bool IsBlockedV6(ReadOnlySpan<byte> b)
    {
        if (b[..15].IndexOfAnyExcept((byte)0) < 0 && b[15] <= 1)
        {
            return true;                              // :: and ::1
        }

        return (b[0] & 0xFE) == 0xFC                  // fc00::/7
            || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) // fe80::/10
            || b[0] == 0xFF                           // ff00::/8
            || (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4..12].IndexOfAnyExcept((byte)0) < 0); // 64:ff9b::/96
    }
}
