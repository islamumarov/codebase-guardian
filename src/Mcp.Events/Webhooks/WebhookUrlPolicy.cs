using System.Diagnostics.CodeAnalysis;

namespace Mcp.Events;

/// <summary>Syntactic checks for webhook callback URLs; address checks happen at connect time.</summary>
public static class WebhookUrlPolicy
{
    public const int MaxLength = 2048;

    public static bool TryValidate(string? url, bool allowInsecureLoopback, [NotNullWhen(true)] out Uri? uri, out string? problem)
    {
        uri = null;
        if (string.IsNullOrEmpty(url) || url.Length > MaxLength)
        {
            problem = $"must be an absolute URL of at most {MaxLength} characters";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host))
        {
            problem = "must be an absolute URL";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && !(allowInsecureLoopback && parsed.Scheme == Uri.UriSchemeHttp))
        {
            problem = "must use https";
            return false;
        }

        if (parsed.UserInfo.Length > 0)
        {
            problem = "must not contain credentials";
            return false;
        }

        if (parsed.Fragment.Length > 0 || url.Contains('#', StringComparison.Ordinal))
        {
            problem = "must not contain a fragment";
            return false;
        }

        uri = parsed;
        problem = null;
        return true;
    }
}
