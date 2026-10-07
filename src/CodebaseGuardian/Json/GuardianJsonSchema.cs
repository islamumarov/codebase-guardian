using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace CodebaseGuardian.Json;

/// <summary>Schema generation for the Guardian tools.</summary>
public static class GuardianJsonSchema
{
    /// <summary>
    /// The options every Guardian tool schema is created with. The generator cannot see through a custom converter and
    /// describes its values as <c>true</c> (any value), so the values of the Guardian converters are described here.
    /// </summary>
    public static AIJsonSchemaCreateOptions CreateOptions { get; } = new()
    {
        TransformSchemaNode = (context, schema) =>
            context.PropertyInfo?.CustomConverter is UtcTimestampJsonConverter ? DateTimeString(schema) : schema,
    };

    private static JsonObject DateTimeString(JsonNode schema)
    {
        var described = schema as JsonObject ?? new JsonObject();
        described["type"] = "string";
        described["format"] = "date-time";
        return described;
    }
}
