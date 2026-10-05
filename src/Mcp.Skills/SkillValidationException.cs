namespace Mcp.Skills;

public sealed class SkillValidationException(IReadOnlyList<string> errors)
    : Exception("Invalid skills:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "- " + e)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
