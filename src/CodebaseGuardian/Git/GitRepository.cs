using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CodebaseGuardian.Hosting;
using CodebaseGuardian.Processes;
using CodebaseGuardian.Sources;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Git;

/// <summary>
/// Typed wrapper over the <c>git</c> CLI for the configured repository. It runs against the user's real repository
/// and therefore leaves the user's git configuration alone. Every path it returns is verbatim (<c>-z</c> output is
/// never unquoted), uses '/' separators and is relative to <see cref="RootPath"/>.
/// </summary>
public sealed partial class GitRepository : IGitRepository
{
    private const string EmptyTree = "(empty)";
    private const string WorkingTree = "working tree";
    private const int NumstatLimitBytes = 16 * 1024 * 1024;
    private const char FieldSeparator = '\u001f';
    private const char RecordSeparator = '\u001e';

    private const string LogFormat = "%H%x1f%h%x1f%an%x1f%ae%x1f%cI%x1f%s%x1f%P%x1e";

    private static readonly IReadOnlyDictionary<string, string> GitEnvironment =
        new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]*$")]
    private static partial Regex RemoteNamePattern();

    private readonly IProcessRunner _runner;
    private readonly string _repositoryPath;
    private string? _rootPath;

    public GitRepository(IProcessRunner runner, IOptions<GuardianOptions> options)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        _runner = runner;
        _repositoryPath = options.Value.RepositoryPath;
    }

    /// <summary>
    /// Resolved on first use and then cached; a failed resolution (not a repository) is not cached.
    /// Synchronous because it is a property; the work is a single quick <c>rev-parse</c>.
    /// </summary>
    public string RootPath => _rootPath ?? ResolveRootAsync(CancellationToken.None).GetAwaiter().GetResult();

    public RepositorySourceKind Kind => RepositorySourceKind.Local;

    public string DisplayName => RootPath;

    // ---- head, branch, status

    public async Task<string?> GetHeadShaAsync(CancellationToken ct = default)
    {
        var result = await RunGitAsync(["rev-parse", "--verify", "--quiet", "HEAD"], ct: ct);
        if (result.ExitCode == 1)
        {
            return null; // --quiet --verify: exit 1 means HEAD does not resolve (no commits yet)
        }

        ThrowIfFailed(result, "rev-parse HEAD");
        return result.StandardOutput.Trim();
    }

    public async Task<string?> GetCurrentBranchAsync(CancellationToken ct = default)
    {
        // An unborn branch has a symbolic HEAD but no commit; by contract that reports null, like a detached HEAD.
        if (await GetHeadShaAsync(ct) is null)
        {
            return null;
        }

        var result = await RunGitAsync(["symbolic-ref", "--short", "--quiet", "HEAD"], ct: ct);
        if (result.ExitCode == 1)
        {
            return null; // detached
        }

        ThrowIfFailed(result, "symbolic-ref HEAD");
        return result.StandardOutput.Trim();
    }

    public async Task<RepoStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var result = await RunGitAsync(["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"], ct: ct);
        ThrowIfFailed(result, "status");
        return ParseStatus(result.StandardOutput);
    }

    private static RepoStatus ParseStatus(string output)
    {
        string? branch = null, headSha = null, upstream = null;
        int ahead = 0, behind = 0;
        List<FileChange> staged = [], unstaged = [];
        List<string> untracked = [], conflicted = [];

        var tokens = output.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length == 0)
            {
                continue;
            }

            if (token.StartsWith("# ", StringComparison.Ordinal))
            {
                var header = token[2..];
                if (header.StartsWith("branch.oid ", StringComparison.Ordinal))
                {
                    var oid = header["branch.oid ".Length..];
                    headSha = oid == "(initial)" ? null : oid;
                }
                else if (header.StartsWith("branch.head ", StringComparison.Ordinal))
                {
                    branch = header["branch.head ".Length..];
                }
                else if (header.StartsWith("branch.upstream ", StringComparison.Ordinal))
                {
                    upstream = header["branch.upstream ".Length..];
                }
                else if (header.StartsWith("branch.ab ", StringComparison.Ordinal))
                {
                    var parts = header["branch.ab ".Length..].Split(' ');
                    ahead = int.Parse(parts[0].TrimStart('+'), CultureInfo.InvariantCulture);
                    behind = int.Parse(parts[1].TrimStart('-'), CultureInfo.InvariantCulture);
                }

                continue;
            }

            switch (token[0])
            {
                case '1': // 1 XY sub mH mI mW hH hI path
                {
                    var fields = token.Split(' ', 9);
                    AddChanges(fields[1], fields[8]);
                    break;
                }

                case '2': // 2 XY sub mH mI mW hH hI Xscore path NUL origPath
                {
                    var fields = token.Split(' ', 10);
                    AddChanges(fields[1], fields[9]);
                    i++; // the original path follows as its own token
                    break;
                }

                case 'u': // u XY sub m1 m2 m3 mW h1 h2 h3 path
                    conflicted.Add(token.Split(' ', 11)[10]);
                    break;

                case '?':
                    untracked.Add(token[2..]);
                    break;
            }
        }

        // Detached HEAD and unborn branches both report no branch (see GetCurrentBranchAsync).
        if (branch == "(detached)" || headSha is null)
        {
            branch = null;
        }

        return new RepoStatus(branch, headSha, upstream, ahead, behind, staged, unstaged, untracked, conflicted);

        void AddChanges(string xy, string path)
        {
            if (xy[0] != '.')
            {
                staged.Add(new FileChange(path, KindOf(xy[0])));
            }

            if (xy[1] != '.')
            {
                unstaged.Add(new FileChange(path, KindOf(xy[1])));
            }
        }
    }

    private static string KindOf(char code) => code switch
    {
        'A' => "added",
        'M' => "modified",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        'T' => "type-changed",
        'U' => "unmerged",
        _ => "modified",
    };

    // ---- branches and commits

    public async Task<IReadOnlyDictionary<string, string>> GetBranchHeadsAsync(CancellationToken ct = default)
    {
        var result = await RunGitAsync(
            ["for-each-ref", "--format=%(refname)%1f%(objectname)", "refs/heads"], ct: ct);
        ThrowIfFailed(result, "for-each-ref");

        const string prefix = "refs/heads/";
        var heads = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split(FieldSeparator);
            heads[parts[0][prefix.Length..]] = parts[1];
        }

        return heads;
    }

    public async Task<IReadOnlyList<CommitInfo>> GetRecentCommitsAsync(
        int limit, string? revision = null, CancellationToken ct = default)
    {
        RequirePositive(limit, nameof(limit));
        if (revision is not null)
        {
            GitRevision.Require(revision, nameof(revision));
        }
        else if (await GetHeadShaAsync(ct) is null)
        {
            return []; // unborn: git log would fail with "does not have any commits yet"
        }

        List<string> args = ["log", $"--format={LogFormat}", "-n", limit.ToString(CultureInfo.InvariantCulture)];
        if (revision is not null)
        {
            args.Add(revision);
        }

        args.Add("--");
        var result = await RunGitAsync(args, ct: ct);
        ThrowIfFailed(result, "log");
        return ParseCommits(result.StandardOutput);
    }

    public async Task<IReadOnlyList<CommitInfo>> GetNewCommitsAsync(
        string tip, IReadOnlyCollection<string> excludeTips, int limit, CancellationToken ct = default)
    {
        GitRevision.Require(tip, nameof(tip));
        ArgumentNullException.ThrowIfNull(excludeTips);
        foreach (var exclude in excludeTips)
        {
            GitRevision.Require(exclude, nameof(excludeTips));
        }

        RequirePositive(limit, nameof(limit));

        // Newest-first with -n keeps the newest `limit`; the caller wants them oldest first.
        List<string> args = ["log", $"--format={LogFormat}", "-n", limit.ToString(CultureInfo.InvariantCulture), tip];
        if (excludeTips.Count > 0)
        {
            args.Add("--not");
            args.AddRange(excludeTips);
        }

        args.Add("--");
        var result = await RunGitAsync(args, ct: ct);
        ThrowIfFailed(result, "log");

        var commits = ParseCommits(result.StandardOutput).ToList();
        commits.Reverse();
        return commits;
    }

    private static List<CommitInfo> ParseCommits(string output)
    {
        var commits = new List<CommitInfo>();
        foreach (var record in output.Split(RecordSeparator))
        {
            var text = record.TrimStart('\n', '\r');
            if (text.Length == 0)
            {
                continue;
            }

            var f = text.Split(FieldSeparator);
            commits.Add(new CommitInfo(
                f[0],
                f[1],
                f[2],
                f[3],
                DateTimeOffset.Parse(f[4], CultureInfo.InvariantCulture),
                f[5],
                f[6].Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        }

        return commits;
    }

    // ---- diffs

    public async Task<DiffSummary> GetDiffSummaryAsync(string? from, string? to, int maxPatchBytes, CancellationToken ct = default)
    {
        if (from is not null)
        {
            GitRevision.Require(from, nameof(from));
        }

        if (to is not null)
        {
            GitRevision.Require(to, nameof(to));
            if (from is null)
            {
                throw new ArgumentException("A 'to' revision requires a 'from' revision.", nameof(from));
            }
        }

        RequireNonNegative(maxPatchBytes, nameof(maxPatchBytes));

        if (from is null)
        {
            // Working tree (staged and unstaged) against HEAD; an unborn repository has only the index to show,
            // which `diff --cached` compares against the empty tree.
            return await GetHeadShaAsync(ct) is null
                ? await DiffAsync(EmptyTree, WorkingTree, ["diff", "--cached"], maxPatchBytes, ct)
                : await DiffAsync("HEAD", WorkingTree, ["diff", "HEAD"], maxPatchBytes, ct);
        }

        return to is null
            ? await DiffAsync(from, WorkingTree, ["diff", from], maxPatchBytes, ct)
            : await DiffAsync(from, to, ["diff", from, to], maxPatchBytes, ct);
    }

    public async Task<DiffSummary> GetCommitDiffAsync(string sha, int maxPatchBytes, CancellationToken ct = default)
    {
        GitRevision.Require(sha, nameof(sha));
        RequireNonNegative(maxPatchBytes, nameof(maxPatchBytes));

        var parents = await RunGitAsync(["log", "-n", "1", "--format=%P", sha, "--"], ct: ct);
        ThrowIfFailed(parents, "log");
        var parent = parents.StandardOutput.Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return await DiffAsync(parent ?? EmptyTree, sha, ["show", "--format=", sha], maxPatchBytes, ct);
    }

    public async Task<DiffSummary> GetStagedDiffAsync(int maxPatchBytes, CancellationToken ct = default)
    {
        RequireNonNegative(maxPatchBytes, nameof(maxPatchBytes));
        var from = await GetHeadShaAsync(ct) is null ? EmptyTree : "HEAD";
        return await DiffAsync(from, "index", ["diff", "--cached"], maxPatchBytes, ct);
    }

    /// <param name="command">The git command and its revision operands, e.g. <c>["diff", "a", "b"]</c>; the output options are inserted after the first element.</param>
    private async Task<DiffSummary> DiffAsync(
        string from, string to, string[] command, int maxPatchBytes, CancellationToken ct)
    {
        string[] Build(params string[] options) =>
            [command[0], .. options, "--no-renames", "--no-ext-diff", "--no-textconv", .. command[1..], "--"];

        var numstat = await RunGitAsync(Build("--numstat", "-z"), NumstatLimitBytes, ct);
        ThrowIfFailed(numstat, "numstat");

        // Read a few bytes more than the limit so truncation is detectable; the cut below lands on a character boundary.
        var patchLimit = (int)Math.Min(int.MaxValue, (long)maxPatchBytes + 4);
        var patch = await RunGitAsync(Build("--patch"), patchLimit, ct);
        ThrowIfFailed(patch, "diff");

        var files = ParseNumstat(numstat);
        var (patchText, truncated) = CapPatch(patch, maxPatchBytes);
        return new DiffSummary(
            from,
            to,
            files,
            files.Sum(f => f.Insertions ?? 0),
            files.Sum(f => f.Deletions ?? 0),
            patchText,
            truncated);
    }

    private static List<FileDiffStat> ParseNumstat(ProcessResult numstat)
    {
        var tokens = numstat.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (numstat.OutputTruncated && tokens.Count > 0)
        {
            tokens.RemoveAt(tokens.Count - 1); // the last entry may be cut mid-path
        }

        var files = new List<FileDiffStat>();
        foreach (var token in tokens)
        {
            var parts = token.TrimStart('\n').Split('\t', 3);
            if (parts.Length != 3)
            {
                continue;
            }

            var binary = parts[0] == "-" && parts[1] == "-";
            files.Add(binary
                ? new FileDiffStat(parts[2], null, null, true)
                : new FileDiffStat(
                    parts[2],
                    int.Parse(parts[0], CultureInfo.InvariantCulture),
                    int.Parse(parts[1], CultureInfo.InvariantCulture),
                    false));
        }

        return files;
    }

    private static (string Patch, bool Truncated) CapPatch(ProcessResult patch, int maxBytes)
    {
        var (text, cut) = PatchCap.Apply(patch.StandardOutput, maxBytes);
        return (text, cut || patch.OutputTruncated);
    }

    // ---- files and remotes

    public async Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken ct = default)
    {
        var result = await RunGitAsync(["ls-files", "-co", "--exclude-standard", "-z"], NumstatLimitBytes, ct);
        ThrowIfFailed(result, "ls-files");
        return result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
    }

    public async Task<string?> GetRemoteUrlAsync(string remote = "origin", CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(remote) || !RemoteNamePattern().IsMatch(remote))
        {
            throw new ArgumentException($"'{remote}' is not a valid remote name.", nameof(remote));
        }

        // `config --get` returns the URL as configured; `remote get-url` would apply url.*.insteadOf rewrites.
        var result = await RunGitAsync(["config", "--get", $"remote.{remote}.url"], ct: ct);
        if (result.ExitCode == 1)
        {
            return null;
        }

        ThrowIfFailed(result, "config");
        var url = result.StandardOutput.Trim();
        return url.Length == 0 ? null : url;
    }

    // ---- snapshot

    public async Task<byte[]?> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var fullPath = ResolveInsideRoot(path);
        try
        {
            var info = new FileInfo(fullPath);
            // A symlink's content is the target's, which may lie outside the repository: never follow it.
            if (!info.Exists || info.LinkTarget is not null || HasSymlinkedParent(info))
            {
                return null;
            }

            return await File.ReadAllBytesAsync(fullPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // vanished or unreadable between the check and the read
        }
    }

    public async IAsyncEnumerable<SnapshotFile> ReadSnapshotAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var root = RootPath;
        foreach (var path in await ListFilesAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            // The file list can name tracked files that were deleted from the working tree, or directories (submodules).
            var fullPath = Path.Combine(root, path);
            long length;
            try
            {
                var info = new FileInfo(fullPath);
                // A symlink's content is the target's, which may lie outside the repository (~/.aws/credentials): never follow it.
                if (info.LinkTarget is not null || !info.Exists)
                {
                    continue;
                }

                length = info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (length > SnapshotLimits.MaxFileBytes)
            {
                yield return new SnapshotFile(path, null, SnapshotLimits.TooLargeReason);
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(fullPath, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            yield return new SnapshotFile(path, bytes, null);
        }
    }

    private string ResolveInsideRoot(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || path.Contains('\0') || Path.IsPathRooted(path) || path[0] is '/' or '\\')
        {
            throw new ArgumentException($"'{path}' is not a relative snapshot path.", nameof(path));
        }

        if (path.Split('/', '\\').Any(segment => segment == ".."))
        {
            throw new ArgumentException($"'{path}' leaves the repository.", nameof(path));
        }

        return Path.Combine(RootPath, path);
    }

    // A symlinked directory on the way to the file would also lead outside the repository.
    private bool HasSymlinkedParent(FileInfo file)
    {
        var root = Path.TrimEndingDirectorySeparator(RootPath);
        for (var directory = file.Directory; directory is not null; directory = directory.Parent)
        {
            if (string.Equals(Path.TrimEndingDirectorySeparator(directory.FullName), root, StringComparison.Ordinal))
            {
                return false;
            }

            if (directory.LinkTarget is not null)
            {
                return true;
            }
        }

        return false;
    }

    // ---- plumbing

    private async Task<string> ResolveRootAsync(CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            Spec(["rev-parse", "--show-toplevel"], _repositoryPath, _repositoryPath, maxOutputBytes: null), ct);
        ThrowIfFailed(result, "rev-parse --show-toplevel");

        var root = Path.GetFullPath(result.StandardOutput.TrimEnd('\n', '\r'));
        return _rootPath = root;
    }

    private async Task<ProcessResult> RunGitAsync(IReadOnlyList<string> args, int? maxOutputBytes = null, CancellationToken ct = default)
    {
        var root = _rootPath ?? await ResolveRootAsync(ct);
        return await _runner.RunAsync(Spec(args, root, root, maxOutputBytes), ct);
    }

    private static ProcessSpec Spec(IReadOnlyList<string> args, string gitDirectory, string workingDirectory, int? maxOutputBytes)
    {
        string[] prefix =
        [
            "--no-optional-locks", "-C", gitDirectory, "-c", "core.quotepath=off", "-c", "color.ui=never",
        ];
        var spec = new ProcessSpec("git", [.. prefix, .. args], workingDirectory) { Environment = GitEnvironment };
        return maxOutputBytes is { } max ? spec with { MaxOutputBytes = max } : spec;
    }

    private static void ThrowIfFailed(ProcessResult result, string operation)
    {
        if (result.TimedOut)
        {
            throw new GitException($"git {operation} timed out.", result.ExitCode, result.StandardError);
        }

        if (result.ExitCode != 0)
        {
            var detail = result.StandardError.Trim();
            throw new GitException(
                $"git {operation} failed with exit code {result.ExitCode}: {detail}", result.ExitCode, result.StandardError);
        }
    }

    private static void RequirePositive(int value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be greater than zero.");
        }
    }

    private static void RequireNonNegative(int value, string name)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must not be negative.");
        }
    }
}
