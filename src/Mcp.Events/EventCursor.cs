using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Mcp.Events;

/// <summary>
/// Opaque cursor codec: base64url (no padding) of <c>v1:&lt;epoch, "N" format&gt;:&lt;sequence&gt;</c>.
/// The sequence means "everything at or below this has been seen".
/// Decoding is strict: anything that does not re-encode to the identical string is rejected.
/// </summary>
public static class EventCursor
{
    private const string Version = "v1";

    public static string Encode(Guid epoch, long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        var raw = string.Create(CultureInfo.InvariantCulture, $"{Version}:{epoch:N}:{sequence}");
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
    }

    public static (Guid Epoch, long Sequence) Decode(string cursor)
    {
        if (string.IsNullOrEmpty(cursor)) throw new InvalidCursorException("Cursor is empty.");
        string raw;
        try
        {
            raw = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Base64Url.DecodeFromChars(cursor));
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            throw new InvalidCursorException("Cursor is not valid base64url.", e);
        }

        var parts = raw.Split(':');
        if (parts.Length != 3) throw new InvalidCursorException("Cursor has the wrong number of fields.");
        if (parts[0] != Version) throw new InvalidCursorException("Cursor has an unsupported version.");
        if (!Guid.TryParseExact(parts[1], "N", out var epoch)) throw new InvalidCursorException("Cursor epoch is malformed.");
        if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
            throw new InvalidCursorException("Cursor sequence is malformed.");

        // Reject non-canonical encodings (trailing '=', alternate leading zeros, uppercase hex, ...).
        if (Encode(epoch, sequence) != cursor) throw new InvalidCursorException("Cursor is not in canonical form.");
        return (epoch, sequence);
    }
}
