using System.Text;
using System.Text.RegularExpressions;
using CodebaseGuardian.Git;
using CodebaseGuardian.Sources;
using Microsoft.Extensions.Logging;

namespace CodebaseGuardian.Security;

public interface ISecretScanner
{
    Task<SecretScanOutcome> ScanWorkingTreeAsync(CancellationToken ct);

    /// <summary>Throws <see cref="ArgumentException"/> ("Remote mode has nothing staged.") when there is no local checkout.</summary>
    Task<SecretScanOutcome> ScanStagedAsync(CancellationToken ct);

    Task<SecretScanOutcome> ScanCommitAsync(string sha, CancellationToken ct);

    /// <summary>Line numbers are 1-based.</summary>
    IReadOnlyList<SecretFinding> ScanText(string path, string content);

    /// <summary>Only added lines are scanned; line numbers are those of the new file.</summary>
    IReadOnlyList<SecretFinding> ScanPatch(string unifiedDiff);

    /// <summary>Replaces every rule match's secret with <see cref="Redactor.Redact"/>; text without matches is returned unchanged.</summary>
    string RedactSecrets(string text);
}

public sealed class SecretScanner(IRepositorySource source, ILogger<SecretScanner> logger, IGitRepository? git = null) : ISecretScanner
{
    internal const int MaxPatchBytes = 5 * 1024 * 1024;
    private const int BinarySniffBytes = 8192;

    public async Task<SecretScanOutcome> ScanWorkingTreeAsync(CancellationToken ct)
    {
        var ignore = await LoadIgnoreAsync(ct);
        var findings = new List<SecretFinding>();
        var warnings = new List<string>();

        await foreach (var file in source.ReadSnapshotAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (file.IsIncompleteMarker)
            {
                warnings.Add($"Snapshot incomplete: {file.Skipped}");
                continue;
            }

            // A file over the per-file cap is documented behaviour (Content is null), not a gap worth a warning.
            if (ignore.IsIgnored(file.Path) || file.Content is not { } content)
            {
                continue;
            }

            var span = content.Span;
            if (span[..Math.Min(span.Length, BinarySniffBytes)].Contains((byte)0))
            {
                continue;
            }

            findings.AddRange(ScanText(file.Path, Decode(span)));
        }

        return new SecretScanOutcome(findings, warnings.Count == 0, warnings);
    }

    public async Task<SecretScanOutcome> ScanStagedAsync(CancellationToken ct)
    {
        if (git is null)
        {
            throw new ArgumentException("Remote mode has nothing staged.");
        }

        return ScanDiff(await git.GetStagedDiffAsync(MaxPatchBytes, ct), "staged changes");
    }

    public async Task<SecretScanOutcome> ScanCommitAsync(string sha, CancellationToken ct) =>
        ScanDiff(await source.GetCommitDiffAsync(GitRevision.Require(sha, nameof(sha)), MaxPatchBytes, ct), $"commit {sha}");

    // A capped patch means the scan saw only its beginning: say so rather than report a silent all-clear.
    private SecretScanOutcome ScanDiff(DiffSummary diff, string scope)
    {
        var findings = ScanPatch(diff.Patch);
        if (!diff.PatchTruncated)
        {
            return new SecretScanOutcome(findings, true, []);
        }

        logger.LogWarning(
            "Secret scan of {Scope} covered only the first {Bytes} bytes of the patch; later changes were not scanned.",
            scope, MaxPatchBytes);
        return new SecretScanOutcome(findings, false, [$"Only the first {MaxPatchBytes} bytes of the {scope} patch were scanned."]);
    }

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

    private async Task<GuardianIgnore> LoadIgnoreAsync(CancellationToken ct)
    {
        var bytes = await source.ReadFileAsync(GuardianIgnore.FileName, ct);
        return bytes is null ? GuardianIgnore.None : GuardianIgnore.Parse(Decode(bytes));
    }

    // Like File.ReadAllText: UTF-8 by default, a byte order mark is honoured and not part of the text.
    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes.ToArray()), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
