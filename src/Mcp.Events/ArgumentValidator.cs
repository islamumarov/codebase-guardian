using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mcp.Events;

/// <summary>
/// Minimal JSON-Schema subset for event subscription arguments: top-level "properties" with "type"
/// (string|integer|number|boolean|array|object) and "enum", plus "required". Undeclared properties are errors.
/// </summary>
public static class ArgumentValidator
{
    /// <returns>null when valid, otherwise a human-readable message.</returns>
    public static string? Validate(JsonObject inputSchema, JsonObject? arguments)
    {
        ArgumentNullException.ThrowIfNull(inputSchema);
        var properties = inputSchema["properties"] as JsonObject;

        if (inputSchema["required"] is JsonArray required)
        {
            foreach (var r in required)
            {
                var name = r?.GetValue<string>();
                if (name is not null && (arguments is null || !arguments.ContainsKey(name)))
                    return $"Missing required argument '{name}'.";
            }
        }

        if (arguments is null) return null;

        foreach (var (key, value) in arguments)
        {
            if (properties is null || !properties.TryGetPropertyValue(key, out var schemaNode))
                return $"Unknown argument '{key}'.";
            if (schemaNode is not JsonObject schema) continue;

            if (schema["type"] is JsonValue t && t.TryGetValue<string>(out var type) && !TypeMatches(type, value))
                return $"Argument '{key}' must be of type {type}.";

            if (schema["enum"] is JsonArray allowed && !allowed.Any(a => JsonNode.DeepEquals(a, value)))
                return $"Argument '{key}' must be one of: {string.Join(", ", allowed.Select(a => a?.ToJsonString() ?? "null"))}.";
        }
        return null;
    }

    private static bool TypeMatches(string type, JsonNode? value) => type switch
    {
        "string" => value is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "boolean" => value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number,
        "integer" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && IsWhole(v),
        "array" => value is JsonArray,
        "object" => value is JsonObject,
        _ => true, // unsupported type keyword: do not reject
    };

    private static bool IsWhole(JsonValue v)
    {
        if (v.TryGetValue<long>(out _)) return true;
        if (v.TryGetValue<decimal>(out var d)) return d == decimal.Truncate(d);
        return v.TryGetValue<double>(out var f) && double.IsFinite(f) && f == Math.Floor(f);
    }
}
