using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace GuardianWatch;

/// <summary>
/// The host-side integrity check of the Skills extension: a file read through <c>resources/read</c> must match the
/// <c>digest</c> (<c>sha256:{hex}</c> over the raw bytes) and <c>size</c> that <c>skills/get</c> published for it.
/// </summary>
public static class SkillVerifier
{
    /// <summary>The manifest entry (<c>uri</c>, <c>digest</c>, <c>size</c>) for <paramref name="uri"/> in a <c>skills/get</c> result's <c>skill</c>.</summary>
    public static JsonObject? FindEntry(JsonObject skill, string uri) =>
        skill["resources"] is JsonArray entries
            ? entries.OfType<JsonObject>().FirstOrDefault(e => (string?)e["uri"] == uri)
            : null;

    /// <summary>Returns <c>null</c> when <paramref name="content"/> matches the entry, otherwise the reason it does not.</summary>
    public static string? Verify(JsonObject entry, byte[] content)
    {
        var size = (long?)entry["size"];
        if (size != content.Length)
        {
            return $"size mismatch: entry says {size?.ToString() ?? "nothing"}, content is {content.Length} bytes";
        }

        var actual = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));
        var expected = (string?)entry["digest"];
        return string.Equals(actual, expected, StringComparison.Ordinal)
            ? null
            : $"digest mismatch: entry says {expected ?? "nothing"}, content is {actual}";
    }
}
