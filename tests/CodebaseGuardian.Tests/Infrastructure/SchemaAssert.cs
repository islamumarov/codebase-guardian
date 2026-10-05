using System.Text.Json;

namespace CodebaseGuardian.Tests.Infrastructure;

/// <summary>Minimal output-schema conformance check: every <c>required</c> key is present, recursively (with <c>$ref</c>).</summary>
public static class SchemaAssert
{
    public static void Conforms(JsonElement schema, JsonElement value) => AssertConforms(schema, value, "$", schema);

    private static JsonElement Resolve(JsonElement schema, JsonElement root)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var node = root;
            foreach (var part in reference.GetString()!.TrimStart('#', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                node = node.GetProperty(part);
            }

            return node;
        }

        return schema;
    }

    public static void AssertConforms(JsonElement schema, JsonElement value, string path, JsonElement root)
    {
        schema = Resolve(schema, root);
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
                {
                    Assert.True(value.TryGetProperty(name, out _), $"{path}.{name} is required by the output schema but missing from the content");
                }
            }

            if (schema.TryGetProperty("properties", out var properties))
            {
                foreach (var property in properties.EnumerateObject().Where(p => value.TryGetProperty(p.Name, out _)))
                {
                    AssertConforms(property.Value, value.GetProperty(property.Name), $"{path}.{property.Name}", root);
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var element in value.EnumerateArray())
            {
                AssertConforms(items, element, $"{path}[{index++}]", root);
            }
        }
    }
}
