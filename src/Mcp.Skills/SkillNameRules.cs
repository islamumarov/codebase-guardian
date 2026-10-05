using System.Text.RegularExpressions;

namespace Mcp.Skills;

public static partial class SkillNameRules
{
    public const int MaxDescriptionLength = 1024;
    public const int MaxCompatibilityLength = 500;

    [GeneratedRegex("^(?!.*--)[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$")]
    private static partial Regex NameRegex();

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex FileSegmentRegex();

    public static bool IsValidName(string? name) => name is not null && NameRegex().IsMatch(name);

    public static bool IsValidFileSegment(string segment) => FileSegmentRegex().IsMatch(segment);
}
