namespace Mcp.Skills;

public sealed record SkillFile(string Uri, string RelativePath, byte[] Content, string Digest, long Size, string MimeType, bool IsUtf8Text);
