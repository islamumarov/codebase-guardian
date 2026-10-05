using System.Diagnostics.CodeAnalysis;

namespace Mcp.Events;

/// <summary>Standard Webhooks symmetric secrets: <c>whsec_</c> followed by base64 of 24 to 64 bytes.</summary>
public static class WebhookSecret
{
    private const string Prefix = "whsec_";
    private const int MinBytes = 24, MaxBytes = 64;

    public static bool TryParse(string? value, [NotNullWhen(true)] out byte[]? key)
    {
        key = null;
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var encoded = value.AsSpan(Prefix.Length).TrimEnd('=');
        if (encoded.Length == 0 || encoded.Length % 4 == 1 || value.Length - Prefix.Length - encoded.Length > 2)
        {
            return false;
        }

        foreach (var c in encoded)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/'))
            {
                return false;
            }
        }

        var padded = string.Concat(encoded, new string('=', (4 - encoded.Length % 4) % 4));
        try
        {
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length is < MinBytes or > MaxBytes)
            {
                return false;
            }

            key = bytes;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
