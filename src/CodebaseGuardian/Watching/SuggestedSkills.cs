namespace CodebaseGuardian.Watching;

/// <summary>Skill entry points that event payloads point the agent to (<c>suggestedSkill</c>).</summary>
public static class SuggestedSkills
{
    public const string PrReview = "skill://pr-review/SKILL.md";
    public const string DependencyHygiene = "skill://dependency-hygiene/SKILL.md";
    public const string BugTriage = "skill://bug-triage/SKILL.md";
    public const string SecurityAudit = "skill://security-audit/SKILL.md";
}
