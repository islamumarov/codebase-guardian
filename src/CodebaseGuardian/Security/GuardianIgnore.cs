using System.Text;
using System.Text.RegularExpressions;

namespace CodebaseGuardian.Security;

/// <summary>
/// The repository-root <c>.guardianignore</c>: one glob per line, <c>#</c> comments, <c>*</c> (not across <c>/</c>),
/// <c>**</c> (across <c>/</c>) and <c>?</c>, matched against '/'-separated paths relative to the root.
/// </summary>
public sealed class GuardianIgnore
{
    public const string FileName = ".guardianignore";

    public static GuardianIgnore None { get; } = new([]);

    private readonly Regex[] _patterns;

    private GuardianIgnore(Regex[] patterns) => _patterns = patterns;

    public static GuardianIgnore Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var patterns = content.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(ToRegex)
            .ToArray();
        return new GuardianIgnore(patterns);
    }

    public bool IsIgnored(string relativePath) => _patterns.Any(p => p.IsMatch(relativePath));

    private static Regex ToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                var followedBySlash = i + 2 < glob.Length && glob[i + 2] == '/';
                sb.Append(followedBySlash ? "(?:.*/)?" : ".*");
                i += followedBySlash ? 2 : 1;
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        return new Regex(sb.Append('$').ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
