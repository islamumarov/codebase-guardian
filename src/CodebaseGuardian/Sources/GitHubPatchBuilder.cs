using System.Text;
using CodebaseGuardian.Git;
using CodebaseGuardian.GitHub;

namespace CodebaseGuardian.Sources;

internal static class GitHubPatchBuilder
{
    private const string DevNull = "/dev/null";

    /// <summary>
    /// Stitches per-file API patches into one unified diff that <see cref="Security.PatchParser"/> reads; applies the binary/truncation rules:
    /// no patch and no counts is binary (except a pure rename or copy, which is 0/0); no patch with counts (GitHub's diff was too
    /// large) and a capped file list mark the patch truncated; a patch over <paramref name="maxPatchBytes"/> UTF-8 bytes is cut
    /// exactly as local mode cuts it (<see cref="PatchCap"/>).
    /// </summary>
    public static DiffSummary Build(string from, string to, IReadOnlyList<GitHubFileChange> files, int maxPatchBytes, bool fileListCapped)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPatchBytes);

        var stats = new List<FileDiffStat>(files.Count);
        var patch = new StringBuilder();
        var truncated = fileListCapped;
        foreach (var file in files)
        {
            // A rename or copy without content changes has no patch and no counts either, but it is not binary (local numstat says 0/0).
            var unchanged = file.Patch is null && file.Additions + file.Deletions == 0;
            var binary = unchanged && file.Status is not ("renamed" or "copied");
            stats.Add(binary
                ? new FileDiffStat(file.Path, null, null, true)
                : new FileDiffStat(file.Path, file.Additions, file.Deletions, false));
            truncated |= file.Patch is null && !unchanged;

            // Characters never outnumber UTF-8 bytes: past the limit in characters, the rest would be cut anyway.
            if (patch.Length <= maxPatchBytes)
            {
                AppendFile(patch, file);
            }
        }

        var (text, cut) = PatchCap.Apply(patch.ToString(), maxPatchBytes);
        return new DiffSummary(
            from,
            to,
            stats,
            stats.Sum(stat => stat.Insertions ?? 0),
            stats.Sum(stat => stat.Deletions ?? 0),
            text,
            truncated || cut);
    }

    private static void AppendFile(StringBuilder patch, GitHubFileChange file)
    {
        var oldPath = Quote("a/", file.PreviousPath ?? file.Path);
        var newPath = Quote("b/", file.Path);
        patch.Append("diff --git ").Append(oldPath).Append(' ').Append(newPath).Append('\n');
        patch.Append("--- ").Append(file.Status == "added" ? DevNull : oldPath).Append('\n');
        patch.Append("+++ ").Append(file.Status == "removed" ? DevNull : newPath).Append('\n');
        if (file.Patch is { Length: > 0 } text)
        {
            patch.Append(text);
            if (!text.EndsWith('\n'))
            {
                patch.Append('\n');
            }
        }
    }

    /// <summary>
    /// Like git, a path with a quote, a backslash or a control character is C-quoted, so a file name cannot break the header
    /// line and forge another file's header.
    /// </summary>
    private static string Quote(string prefix, string path)
    {
        if (!path.Any(c => c is '"' or '\\' || char.IsControl(c)))
        {
            return prefix + path;
        }

        var quoted = new StringBuilder("\"").Append(prefix);
        foreach (var c in path)
        {
            var escape = c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\a' => "\\a",
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => "\\n",
                '\v' => "\\v",
                '\f' => "\\f",
                '\r' => "\\r",
                _ when char.IsControl(c) => "\\" + Convert.ToString((int)c, 8).PadLeft(3, '0'),
                _ => null,
            };
            if (escape is null)
            {
                quoted.Append(c);
            }
            else
            {
                quoted.Append(escape);
            }
        }

        return quoted.Append('"').ToString();
    }
}
