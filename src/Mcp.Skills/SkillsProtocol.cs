namespace Mcp.Skills;

/// <summary>Wire constants of the MCP Skills extension (SEP-2640).</summary>
internal static class SkillsProtocol
{
    public const string ExtensionId = "io.modelcontextprotocol/skills";
    public const string ListMethod = "skills/list";
    public const string GetMethod = "skills/get";
    public const string DirectoryReadMethod = "resources/directory/read";
    public const string DirectoryMimeType = "inode/directory";
    public const string MarkdownMimeType = "text/markdown";
    public const string ResultTypeComplete = "complete";
}
