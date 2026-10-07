using System.Text;
using CodebaseGuardian.Git;
using CodebaseGuardian.GitHub;

namespace CodebaseGuardian.Sources;

internal static class GitHubPatchBuilder
{
    private const string DevNull = "/dev/null";

    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>
    /// Stitches per-file API patches into one unified diff that <see cref="Security.PatchParser"/> reads; applies the binary/truncation rules:
    /// no patch and no counts is binary; no patch with counts (GitHub's diff was too large) and a capped file list mark the patch
    /// truncated; a patch over <paramref name="maxPatchBytes"/> UTF-8 bytes is cut after its last complete line within the limit.
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
            var binary = file.Patch is null && file.Additions + file.Deletions == 0;
            stats.Add(binary
                ? new FileDiffStat(file.Path, null, null, true)
                : new FileDiffStat(file.Path, file.Additions, file.Deletions, false));
            truncated |= file.Patch is null && !binary;

            // Characters never outnumber UTF-8 bytes: past the limit in characters, the rest would be cut anyway.
            if (maxPatchBytes > 0 && patch.Length <= maxPatchBytes)
            {
                AppendFile(patch, file);
            }
        }

        var (text, cut) = Cap(patch.ToString(), maxPatchBytes);
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

    /// <summary>Zero bytes asks for no patch at all, which is not a truncation; otherwise the cut ends after a complete line.</summary>
    private static (string Patch, bool Truncated) Cap(string patch, int maxBytes)
    {
        if (maxBytes == 0)
        {
            return (string.Empty, false);
        }

        var bytes = Utf8.GetBytes(patch);
        if (bytes.Length <= maxBytes)
        {
            return (patch, false);
        }

        // '\n' is never part of a multi-byte character, so cutting after one keeps the text valid UTF-8.
        var cut = Array.LastIndexOf(bytes, (byte)'\n', maxBytes - 1) + 1;
        return (Utf8.GetString(bytes, 0, cut), true);
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
