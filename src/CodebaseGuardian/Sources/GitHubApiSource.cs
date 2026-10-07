using System.Runtime.CompilerServices;
using CodebaseGuardian.Git;
using CodebaseGuardian.GitHub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Sources;

/// <summary>
/// The watched repository read through the GitHub REST API (remote mode). It matches the local semantics where the API
/// allows and fails with an argument error where it cannot (there is no working tree, and no revision syntax beyond names
/// and SHAs). Rate-limit and unavailability errors propagate to the caller.
/// </summary>
public sealed class GitHubApiSource : IRepositorySource
{
    internal const string NoWorkingTreeMessage = "Remote mode has no working tree: pass both 'from' and 'to'.";

    /// <summary>The marker local mode uses as the "from" side of a root commit's diff.</summary>
    internal const string EmptyTree = "(empty)";

    private const string Head = "HEAD";
    private const string GitHubApiHost = "api.github.com";
    private const int MaxCompares = 5;
    private const int CompareFileCap = 300;

    private readonly IGitHubRepositoryApi _api;
    private readonly RemoteOptions _remote;
    private readonly ILogger<GitHubApiSource> _logger;

    /// <param name="location">Must be <see cref="RepositoryLocation.GitHub"/>.</param>
    public GitHubApiSource(
        IGitHubRepositoryApi api, RepositoryLocation location, IOptions<RemoteOptions> remote, IOptions<GitHubOptions> github,
        ILogger<GitHubApiSource> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(logger);
        if (location is not RepositoryLocation.GitHub gitHub)
        {
            throw new ArgumentException("The GitHub API source needs a GitHub repository location.", nameof(location));
        }

        _api = api;
        _remote = remote.Value;
        _logger = logger;
        var host = Uri.TryCreate(github.Value.ApiBaseUrl, UriKind.Absolute, out var baseUrl) && baseUrl.Host != GitHubApiHost
            ? baseUrl.Host
            : "github.com";
        DisplayName = $"{host}/{gitHub.Repository}";
    }

    public RepositorySourceKind Kind => RepositorySourceKind.GitHub;

    public string DisplayName { get; }

    // ---- head, branch, status ---------------------------------------------------------------------------------------

    public async Task<RepoStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var (branch, headSha) = await GetDefaultHeadAsync(ct);
        return new RepoStatus(branch, headSha, null, 0, 0, [], [], [], []);
    }

    public async Task<string?> GetCurrentBranchAsync(CancellationToken ct = default) =>
        (await _api.GetRepositoryAsync(ct)).DefaultBranch;

    public async Task<string?> GetHeadShaAsync(CancellationToken ct = default) => (await GetDefaultHeadAsync(ct)).HeadSha;

    public Task<IReadOnlyDictionary<string, string>> GetBranchHeadsAsync(CancellationToken ct = default) => _api.GetBranchHeadsAsync(ct);

    /// <summary>
    /// The head comes from the branch listing, which answers an empty map for an empty repository: nothing that GitHub answers
    /// with 409 for an empty repository (commits, compare, tarball) is asked.
    /// </summary>
    private async Task<(string Branch, string? HeadSha)> GetDefaultHeadAsync(CancellationToken ct)
    {
        var branch = (await _api.GetRepositoryAsync(ct)).DefaultBranch;
        var heads = await _api.GetBranchHeadsAsync(ct);
        return (branch, heads.GetValueOrDefault(branch));
    }

    // ---- commits ----------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<CommitInfo>> GetRecentCommitsAsync(int limit, string? revision = null, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var sha = revision is null or Head ? null : RemoteRevision.Require(revision, nameof(revision));

        return [.. (await _api.ListCommitsAsync(sha, limit, ct)).Select(ToCommitInfo)];
    }

    public async Task<IReadOnlyList<CommitInfo>> GetNewCommitsAsync(
        string tip, IReadOnlyCollection<string> excludeTips, int limit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tip);
        ArgumentNullException.ThrowIfNull(excludeTips);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (tip != Head)
        {
            RemoteRevision.Require(tip, nameof(tip));
        }

        var excludes = excludeTips.Distinct(StringComparer.Ordinal).ToList();
        foreach (var exclude in excludes)
        {
            if (exclude != Head)
            {
                RemoteRevision.Require(exclude, nameof(excludeTips));
            }
        }

        if (excludes.Count > MaxCompares)
        {
            _logger.LogDebug(
                "New commits of {Tip} are approximate: only the first {Max} of {Count} exclude tips are compared.", tip, MaxCompares, excludes.Count);
        }

        List<GitHubCommit>? result = null;
        string? resolvedTip = null;
        foreach (var exclude in excludes.Take(MaxCompares))
        {
            if (exclude == tip)
            {
                return []; // nothing is reachable from the tip but not from itself
            }

            GitHubComparison comparison;
            try
            {
                resolvedTip ??= await ResolveAsync(tip, ct);
                comparison = await _api.CompareAsync(await ResolveAsync(exclude, ct), resolvedTip, ct);
            }
            catch (GitHubRevisionNotFoundException)
            {
                _logger.LogWarning("Exclude tip {Exclude} is no longer in the repository (force-push?); it is ignored.", exclude);
                continue;
            }

            if (result is null)
            {
                result = [.. comparison.Commits];
            }
            else
            {
                var shas = comparison.Commits.Select(commit => commit.Sha).ToHashSet(StringComparer.Ordinal);
                result.RemoveAll(commit => !shas.Contains(commit.Sha));
            }
        }

        if (result is null)
        {
            // No excludes, or every one of them is gone: the tip's own history, oldest first.
            var history = (await _api.ListCommitsAsync(tip == Head ? null : tip, limit, ct)).Select(ToCommitInfo).ToList();
            history.Reverse();
            return history;
        }

        return [.. result.Skip(Math.Max(0, result.Count - limit)).Select(ToCommitInfo)];
    }

    private static CommitInfo ToCommitInfo(GitHubCommit commit)
    {
        var newline = commit.Message.IndexOf('\n');
        var subject = (newline < 0 ? commit.Message : commit.Message[..newline]).TrimEnd('\r');
        return new CommitInfo(
            commit.Sha,
            commit.Sha.Length > 7 ? commit.Sha[..7] : commit.Sha,
            commit.AuthorName,
            commit.AuthorEmail,
            commit.CommittedAt,
            subject,
            commit.ParentShas);
    }

    // ---- diffs ------------------------------------------------------------------------------------------------------

    public async Task<DiffSummary> GetDiffSummaryAsync(string? from, string? to, int maxPatchBytes, CancellationToken ct = default)
    {
        if (from is null || to is null)
        {
            throw new ArgumentException(NoWorkingTreeMessage, from is null ? nameof(from) : nameof(to));
        }

        RequireRevision(from, nameof(from));
        RequireRevision(to, nameof(to));
        ArgumentOutOfRangeException.ThrowIfNegative(maxPatchBytes);

        var comparison = await _api.CompareAsync(await ResolveAsync(from, ct), await ResolveAsync(to, ct), ct);
        return GitHubPatchBuilder.Build(
            comparison.MergeBaseSha,
            comparison.Commits.Count > 0 ? comparison.Commits[^1].Sha : to,
            comparison.Files,
            maxPatchBytes,
            fileListCapped: comparison.Files.Count >= CompareFileCap);
    }

    public async Task<DiffSummary> GetCommitDiffAsync(string sha, int maxPatchBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sha);
        RequireRevision(sha, nameof(sha));
        ArgumentOutOfRangeException.ThrowIfNegative(maxPatchBytes);

        var detail = await _api.GetCommitAsync(await ResolveAsync(sha, ct), ct);
        return GitHubPatchBuilder.Build(
            detail.Commit.ParentShas.Count > 0 ? detail.Commit.ParentShas[0] : EmptyTree,
            sha,
            detail.Files,
            maxPatchBytes,
            fileListCapped: detail.FilesTruncated);
    }

    // ---- snapshot ---------------------------------------------------------------------------------------------------

    public async Task<byte[]?> ReadFileAsync(string path, CancellationToken ct = default)
    {
        RequireSnapshotPath(path);
        var headSha = await GetHeadShaAsync(ct);
        return headSha is null ? null : await _api.GetFileAsync(path, headSha, ct);
    }

    public async IAsyncEnumerable<SnapshotFile> ReadSnapshotAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        // One SHA for the whole snapshot, even when the branch moves while it is read.
        var headSha = await GetHeadShaAsync(ct);
        if (headSha is null)
        {
            yield break;
        }

        await using var tarball = await _api.OpenTarballAsync(headSha, ct);
        var caps = new SnapshotCaps(SnapshotLimits.MaxFileBytes, _remote.MaxSnapshotBytes, _remote.MaxSnapshotFiles);
        await foreach (var file in SnapshotTarReader.ReadAsync(tarball, caps, _logger, ct))
        {
            yield return file;
        }
    }

    // ---- validation -------------------------------------------------------------------------------------------------

    /// <summary>"HEAD" (exactly) is the default branch; everything else must be a name or a SHA.</summary>
    private async Task<string> ResolveAsync(string revision, CancellationToken ct) =>
        revision == Head ? (await _api.GetRepositoryAsync(ct)).DefaultBranch : revision;

    private static void RequireRevision(string revision, string parameterName)
    {
        if (revision != Head)
        {
            RemoteRevision.Require(revision, parameterName);
        }
    }

    /// <summary>The same rules as the local working-tree read.</summary>
    private static void RequireSnapshotPath(string path)
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
    }
}
