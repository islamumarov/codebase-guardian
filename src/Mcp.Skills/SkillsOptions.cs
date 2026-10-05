namespace Mcp.Skills;

public sealed class SkillsOptions
{
    /// <summary>Skill roots; each SKILL.md below a root defines a skill.</summary>
    public IList<string> Directories { get; } = new List<string>();

    public string UriScheme { get; set; } = "skill";

    /// <summary>ttlMs for skills/list, skills/get and resources/read.</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(5);
}
