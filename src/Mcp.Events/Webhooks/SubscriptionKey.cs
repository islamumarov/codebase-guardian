using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Mcp.Events;

/// <summary>Deterministic subscription ids (ruling R11): stable across refreshes and restarts, no random salt.</summary>
public static class SubscriptionKey
{
    public static string ComputeId(string principal, string url, string name, JsonObject? arguments)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(name);
        var key = new JsonArray(principal, url, name, (JsonNode?)arguments?.DeepClone() ?? new JsonObject());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(key)));
        return "sub_" + Convert.ToHexStringLower(hash)[..16];
    }
}
