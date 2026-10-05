using System.Text;

namespace CodebaseGuardian.Tests.Skills;

/// <summary>Writes skill trees into a unique temp directory that is removed on dispose.</summary>
public sealed class SkillFixture : IDisposable
{
    public SkillFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "skills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public static string SkillMd(string name, string description = "Does a thing", string extra = "") =>
        $"---\nname: {name}\ndescription: {description}\n{extra}---\n\n# {name}\n";

    public string WriteFile(string relativePath, string content) => WriteBytes(relativePath, new UTF8Encoding(false).GetBytes(content));

    public string WriteBytes(string relativePath, byte[] content)
    {
        var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public void WriteSkill(string skillPath, string? skillMd = null, string? name = null) =>
        WriteFile(skillPath + "/SKILL.md", skillMd ?? SkillMd(name ?? skillPath.Split('/')[^1]));

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
