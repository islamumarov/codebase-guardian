using System.Text.Json.Nodes;

namespace Mcp.Skills;

/// <summary>Builders for the JSON shapes of skills/list, skills/get and resources/directory/read results.</summary>
internal static class SkillJson
{
    public static JsonObject Entry(SkillDefinition skill)
    {
        var resources = new JsonArray();
        foreach (var file in skill.Files)
        {
            resources.Add(new JsonObject
            {
                ["uri"] = file.Uri,
                ["digest"] = file.Digest,
                ["size"] = file.Size,
            });
        }

        return new JsonObject
        {
            ["uri"] = skill.Uri,
            ["frontmatter"] = skill.Frontmatter.DeepClone(),
            ["resources"] = resources,
        };
    }

    public static JsonObject ListResult(SkillCatalog catalog, TimeSpan ttl)
    {
        var skills = new JsonArray();
        foreach (var skill in catalog.Skills)
        {
            skills.Add(Entry(skill));
        }

        return Cacheable(new JsonObject { ["skills"] = skills }, ttl);
    }

    public static JsonObject GetResult(SkillDefinition skill, TimeSpan ttl) =>
        Cacheable(new JsonObject { ["skill"] = Entry(skill) }, ttl);

    public static JsonObject DirectoryResult(IEnumerable<SkillDirectoryEntry> children)
    {
        var resources = new JsonArray();
        foreach (var child in children)
        {
            var node = new JsonObject
            {
                ["uri"] = child.Uri,
                ["name"] = child.Name,
                ["mimeType"] = child.IsDirectory ? SkillsProtocol.DirectoryMimeType : child.MimeType,
            };
            if (!child.IsDirectory && child.Size is { } size)
            {
                node["size"] = size;
            }

            resources.Add(node);
        }

        return new JsonObject
        {
            ["resultType"] = SkillsProtocol.ResultTypeComplete,
            ["resources"] = resources,
        };
    }

    private static JsonObject Cacheable(JsonObject body, TimeSpan ttl)
    {
        var result = new JsonObject { ["resultType"] = SkillsProtocol.ResultTypeComplete };
        foreach (var (key, value) in body.ToArray())
        {
            body.Remove(key);
            result[key] = value;
        }

        result["ttlMs"] = (long)ttl.TotalMilliseconds;
        result["cacheScope"] = "public";
        return result;
    }
}
