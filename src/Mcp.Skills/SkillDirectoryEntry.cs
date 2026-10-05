namespace Mcp.Skills;

public sealed record SkillDirectoryEntry(string Uri, string Name, bool IsDirectory, string MimeType, long? Size);
