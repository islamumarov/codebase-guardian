using System.Text;
using System.Text.RegularExpressions;
using CodebaseGuardian.Git;
using Microsoft.Extensions.Logging;

namespace CodebaseGuardian.Security;

public interface ISecretScanner
{
    Task<IReadOnlyList<SecretFinding>> ScanWorkingTreeAsync(CancellationToken ct);
    Task<IReadOnlyList<SecretFinding>> ScanStagedAsync(CancellationToken ct);
    Task<IReadOnlyList<SecretFinding>> ScanCommitAsync(string sha, CancellationToken ct);

    /// <summary>Line numbers are 1-based.</summary>
    IReadOnlyList<SecretFinding> ScanText(string path, string content);

    /// <summary>Only added lines are scanned; line numbers are those of the new file.</summary>
    IReadOnlyList<SecretFinding> ScanPatch(string unifiedDiff);

    /// <summary>Replaces every rule match's secret with <see cref="Redactor.Redact"/>; text without matches is returned unchanged.</summary>
    string RedactSecrets(string text);
}

public sealed class SecretScanner(IGitRepository git, ILogger<SecretScanner> logger) : ISecretScanner
{
    internal const long MaxFileBytes = 1024 * 1024;
    internal const int MaxPatchBytes = 5 * 1024 * 1024;
    private const int BinarySniffBytes = 8192;

    public async Task<IReadOnlyList<SecretFinding>> ScanWorkingTreeAsync(CancellationToken ct)
    {
        var root = git.RootPath;
        var ignore = await LoadIgnoreAsync(root, ct);
        var findings = new List<SecretFinding>();

        foreach (var path in await git.ListFilesAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (ignore.IsIgnored(path))
            {
                continue;
            }

            var bytes = await TryReadAsync(Path.Combine(root, path), ct);
            if (bytes is null || Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, BinarySniffBytes)) >= 0)
            {
                continue;
            }

            findings.AddRange(ScanText(path, Encoding.UTF8.GetString(bytes)));
        }

        return findings;
    }

    public async Task<IReadOnlyList<SecretFinding>> ScanStagedAsync(CancellationToken ct) =>
        ScanPatch((await git.GetStagedDiffAsync(MaxPatchBytes, ct)).Patch);

    public async Task<IReadOnlyList<SecretFinding>> ScanCommitAsync(string sha, CancellationToken ct) =>
        ScanPatch((await git.GetCommitDiffAsync(GitRevision.Require(sha, nameof(sha)), MaxPatchBytes, ct)).Patch);

    public IReadOnlyList<SecretFinding> ScanText(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(content);
        var findings = new List<SecretFinding>();
        var number = 0;
        foreach (var line in content.Split('\n'))
        {
            number++;
            ScanLine(path, number, line.TrimEnd('\r'), findings);
        }

        return findings;
    }

    public IReadOnlyList<SecretFinding> ScanPatch(string unifiedDiff)
    {
        var findings = new List<SecretFinding>();
        foreach (var added in PatchParser.AddedLines(unifiedDiff))
        {
            ScanLine(added.Path, added.Line, added.Text, findings);
        }

        return findings;
    }

    public string RedactSecrets(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var spans = new List<(int Start, int End)>();
        foreach (var rule in SecretRules.All)
        {
            try
            {
                foreach (Match match in rule.Pattern.Matches(text))
                {
                    var group = match.Groups[rule.SecretGroup];
                    if (group.Success && group.Length > 0 && (rule.MinEntropy is not { } min || SecretRules.Entropy(group.Value) >= min))
                    {
                        spans.Add((group.Index, group.Index + group.Length));
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                logger.LogWarning("Rule {RuleId} timed out while redacting text; the text is withheld.", rule.Id);
                return "[text withheld: secret scan timed out]";
            }
        }

        if (spans.Count == 0)
        {
            return text;
        }

        // Rules can overlap (a token inside an assignment): redact the union of the spans once.
        spans.Sort();
        var builder = new StringBuilder(text.Length);
        var position = 0;
        var (start, end) = spans[0];
        void Flush()
        {
            builder.Append(text, position, start - position).Append(Redactor.Redact(text[start..end]));
            position = end;
        }

        foreach (var span in spans.Skip(1))
        {
            if (span.Start < end)
            {
                end = Math.Max(end, span.End);
                continue;
            }

            Flush();
            (start, end) = span;
        }

        Flush();
        return builder.Append(text, position, text.Length - position).ToString();
    }

    // One finding per (rule, path, line): the first match of each rule on the line.
    private void ScanLine(string path, int line, string text, List<SecretFinding> findings)
    {
        foreach (var rule in SecretRules.All)
        {
            try
            {
                foreach (Match match in rule.Pattern.Matches(text))
                {
                    var secret = match.Groups[rule.SecretGroup].Value;
                    if (rule.MinEntropy is { } min && SecretRules.Entropy(secret) < min)
                    {
                        continue;
                    }

                    findings.Add(new SecretFinding(rule.Id, path, line, Redactor.Redact(secret)));
                    break;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                logger.LogWarning("Rule {RuleId} timed out on {Path}:{Line}; line skipped for that rule.", rule.Id, path, line);
            }
        }
    }

    private static async Task<GuardianIgnore> LoadIgnoreAsync(string root, CancellationToken ct)
    {
        try
        {
            return GuardianIgnore.Parse(await File.ReadAllTextAsync(Path.Combine(root, GuardianIgnore.FileName), ct));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GuardianIgnore.None;
        }
    }

    // The file list can name tracked files that were deleted from the working tree, or directories (submodules).
    private async Task<byte[]?> TryReadAsync(string fullPath, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length > MaxFileBytes)
            {
                return null;
            }

            return await File.ReadAllBytesAsync(fullPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Skipping unreadable file during secret scan.");
            return null;
        }
    }
}
