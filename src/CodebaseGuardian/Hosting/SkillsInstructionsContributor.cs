using System.Text;
using Mcp.Skills;

namespace CodebaseGuardian.Hosting;

/// <summary>Points the agent at the bundled skills, starting with <c>guardian</c> (Order 10).</summary>
internal sealed class SkillsInstructionsContributor(SkillCatalog catalog) : IInstructionsContributor
{
    public int Order => 10;

    public string? GetSection()
    {
        if (catalog.Skills.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder("Skills available (load `skill://guardian/SKILL.md` first):");
        foreach (var skill in catalog.Skills)
        {
            text.Append("\n- ").Append(skill.Uri).Append(" — ").Append(skill.Description);
        }

        return text.ToString();
    }
}
