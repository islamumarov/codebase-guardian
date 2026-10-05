using System.Text.Json.Nodes;

namespace Mcp.Skills;

public sealed record SkillDefinition(
    string Uri,
    string RootUri,
    string SkillPath,
    string Name,
    string Description,
    JsonObject Frontmatter,
    IReadOnlyList<SkillFile> Files);
