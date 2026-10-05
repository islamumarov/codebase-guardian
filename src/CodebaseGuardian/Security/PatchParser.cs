using System.Text.RegularExpressions;

namespace CodebaseGuardian.Security;

/// <summary>A line added by a patch, with its 1-based line number in the new version of the file.</summary>
public readonly record struct AddedLine(string Path, int Line, string Text);

public static partial class PatchParser
{
    [GeneratedRegex(@"^@@ -\d+(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeader();

    /// <summary>
    /// The added lines of a unified diff. Hunk line counts are followed, so an added line whose text itself looks like
    /// a file header (<c>+++ b/x</c>) is not mistaken for one. Removed lines and deleted files yield nothing.
    /// </summary>
    public static IEnumerable<AddedLine> AddedLines(string unifiedDiff)
    {
        ArgumentNullException.ThrowIfNull(unifiedDiff);
        string? path = null;
        int oldLeft = 0, newLeft = 0, newLine = 0;

        foreach (var raw in unifiedDiff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (oldLeft > 0 || newLeft > 0)
            {
                switch (line.Length == 0 ? ' ' : line[0])
                {
                    case '+':
                        newLeft--;
                        if (path is not null)
                        {
                            yield return new AddedLine(path, newLine, line[1..]);
                        }

                        newLine++;
                        continue;
                    case '-':
                        oldLeft--;
                        continue;
                    case '\\':
                        continue;
                    default:
                        oldLeft--;
                        newLeft--;
                        newLine++;
                        continue;
                }
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                path = null;
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                path = ParsePath(line[4..]);
            }
            else if (HunkHeader().Match(line) is { Success: true } hunk)
            {
                oldLeft = hunk.Groups[1].Success ? int.Parse(hunk.Groups[1].Value) : 1;
                newLine = int.Parse(hunk.Groups[2].Value);
                newLeft = hunk.Groups[3].Success ? int.Parse(hunk.Groups[3].Value) : 1;
            }
        }
    }

    private static string? ParsePath(string value)
    {
        var tab = value.IndexOf('\t');
        if (tab >= 0)
        {
            value = value[..tab];
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        if (value == "/dev/null")
        {
            return null;
        }

        return value.StartsWith("b/", StringComparison.Ordinal) ? value[2..] : value;
    }
}
