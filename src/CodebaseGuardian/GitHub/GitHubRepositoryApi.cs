using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.GitHub;

/// <summary>
/// Token-optional repository reads over the GitHub REST API. Registered as a singleton: it owns the conditional-request
/// cache and the last complete branch listing. Without a token, requests are anonymous (public repositories only).
/// </summary>
public sealed partial class GitHubRepositoryApi(
    IHttpClientFactory httpClients,
    IOptions<GitHubOptions> options,
    IGitHubTokenProvider tokens,
    IGitHubRepositoryResolver resolver,
    ILogger<GitHubRepositoryApi> logger,
    TimeProvider? timeProvider = null) : IGitHubRepositoryApi
{
    private const string Disabled = "GitHub integration is disabled (Guardian:GitHub:Enabled=false).";
    private const string NoRepository =
        "The origin remote is not a github.com repository; set Guardian:GitHub:Owner and Guardian:GitHub:Repository.";
    private const string RawMediaType = "application/vnd.github.raw+json";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly GitHubConditionalCache _cache = new();
    private readonly object _gate = new();
    private IReadOnlyDictionary<string, string>? _lastBranches;

    public async Task<GitHubRepositoryInfo> GetRepositoryAsync(CancellationToken ct)
    {
        var session = await PrepareAsync(ct);
        var response = await session.Http.GetConditionalAsync(session.Path(), session.Token, _cache, ct, HttpStatusCode.NotFound);
        if (response.Status == HttpStatusCode.NotFound)
        {
            throw new GitHubNotFoundException(session.Repository);
        }

        var dto = Parse<RepositoryDto>(response.Body);
        return new GitHubRepositoryInfo(dto.DefaultBranch, dto.Private);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetBranchHeadsAsync(CancellationToken ct)
    {
        var session = await PrepareAsync(ct);
        var path = session.Path("branches") + "?per_page=100";
        var first = await session.Http.GetConditionalAsync(path, session.Token, _cache, ct, HttpStatusCode.Conflict, HttpStatusCode.NotFound);
        if (first.Status == HttpStatusCode.NotFound)
        {
            throw new GitHubNotFoundException(session.Repository);
        }

        if (first.Status == HttpStatusCode.Conflict)
        {
            return Remember(new Dictionary<string, string>());
        }

        if (first.NotModified)
        {
            lock (_gate)
            {
                if (_lastBranches is not null)
                {
                    return _lastBranches;
                }
            }

            // The cache holds the page but no complete listing was ever parsed (an earlier call failed part-way): fetch again, unconditionally.
            first = await session.Http.GetConditionalAsync(path, session.Token, new GitHubConditionalCache(), ct);
        }

        var heads = new Dictionary<string, string>(StringComparer.Ordinal);
        AddBranches(heads, first.Body);
        var next = first.NextLink;
        for (var page = 2; next is not null && page <= GitHubHttp.MaxPages; page++)
        {
            using var response = await session.Http.SendAsync(HttpMethod.Get, next, null, session.Token, ct);
            AddBranches(heads, await response.Content.ReadAsStringAsync(ct));
            next = session.Http.NextLink(response);
        }

        if (next is not null)
        {
            logger.LogWarning("Only the first 500 branches are watched.");
        }

        return Remember(heads);
    }

    public async Task<IReadOnlyList<GitHubCommit>> ListCommitsAsync(string? sha, int limit, CancellationToken ct)
    {
        var session = await PrepareAsync(ct);
        var path = session.Path("commits") + $"?per_page={Math.Clamp(limit, 1, 100)}" + (sha is null ? string.Empty : "&sha=" + EscapeRef(sha));
        using var response = await session.Http.SendAsync(
            HttpMethod.Get, path, null, session.Token, ct,
            allow: [HttpStatusCode.Conflict, HttpStatusCode.NotFound, HttpStatusCode.UnprocessableEntity]);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            throw sha is null
                ? new GitHubNotFoundException(session.Repository)
                : new GitHubRevisionNotFoundException(sha, session.Repository);
        }

        return [.. Parse<List<CommitDto>>(await response.Content.ReadAsStringAsync(ct)).Select(ToCommit)];
    }

    public async Task<GitHubCommitDetail> GetCommitAsync(string sha, CancellationToken ct)
    {
        var session = await PrepareAsync(ct);
        // Each page is a full commit object whose "files" holds that page's files; the commit itself comes from page 1.
        var pages = await session.Http.ListAsync<CommitDto>(
            session.Path("commits", EscapeRef(sha)), session.Token, ct, GitHubHttp.MaxPages,
            HttpStatusCode.NotFound, HttpStatusCode.UnprocessableEntity);
        if (pages.Count == 0)
        {
            throw new GitHubRevisionNotFoundException(sha, session.Repository);
        }

        return new GitHubCommitDetail(ToCommit(pages[0]), [.. pages.SelectMany(page => page.Files ?? []).Select(ToFileChange)]);
    }

    public async Task<GitHubComparison> CompareAsync(string @base, string head, CancellationToken ct)
    {
        var session = await PrepareAsync(ct);
        var path = session.Path("compare", $"{Uri.EscapeDataString(@base)}...{Uri.EscapeDataString(head)}");
        using var response = await session.Http.SendAsync(HttpMethod.Get, path, null, session.Token, ct, allow: HttpStatusCode.NotFound);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new GitHubRevisionNotFoundException($"{@base}...{head}", session.Repository);
        }

        var dto = Parse<CompareDto>(await response.Content.ReadAsStringAsync(ct));
        return new GitHubComparison(
            dto.Status, dto.AheadBy, dto.BehindBy, dto.BaseCommit?.Sha ?? string.Empty,
            [.. (dto.Commits ?? []).Select(ToCommit)], [.. (dto.Files ?? []).Select(ToFileChange)]);
    }

    public async Task<byte[]?> GetFileAsync(string path, string @ref, CancellationToken ct)
    {
        var session = await PrepareAsync(ct);
        var escapedPath = EscapeRef(path);
        using var response = await session.Http.SendAsync(
            HttpMethod.Get, session.Path("contents", escapedPath) + "?ref=" + EscapeRef(@ref), null, session.Token, ct,
            accept: RawMediaType, allow: HttpStatusCode.NotFound);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await response.Content.ReadAsByteArrayAsync(ct);
    }

    public Task<Stream> OpenTarballAsync(string @ref, CancellationToken ct) => throw new NotImplementedException("Task 29");

    // ---- plumbing -----------------------------------------------------------------------------------------------

    private sealed record Session(GitHubHttp Http, GitHubRepositoryRef Repository, string? Token)
    {
        public string Path(params string[] segments) =>
            $"repos/{Uri.EscapeDataString(Repository.Owner)}/{Uri.EscapeDataString(Repository.Name)}"
            + string.Concat(segments.Select(segment => "/" + segment));

        // The compiler-generated ToString would print the token.
        public override string ToString() => Repository.ToString();
    }

    private async Task<Session> PrepareAsync(CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            throw new GitHubUnavailableException(Disabled);
        }

        var token = await tokens.GetTokenAsync(ct); // optional: null means anonymous
        var repository = await resolver.ResolveAsync(ct) ?? throw new GitHubUnavailableException(NoRepository);
        return new Session(new GitHubHttp(httpClients.CreateClient(GitHubClient.HttpClientName), settings, _time), repository, token);
    }

    private IReadOnlyDictionary<string, string> Remember(IReadOnlyDictionary<string, string> heads)
    {
        lock (_gate)
        {
            _lastBranches = heads;
        }

        return heads;
    }

    private static string EscapeRef(string value) => string.Join('/', value.Split('/').Select(Uri.EscapeDataString));

    private static T Parse<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, GitHubHttp.Json) ?? throw new GitHubApiException(200, "GitHub returned an empty response.");

    private static void AddBranches(Dictionary<string, string> heads, string json)
    {
        foreach (var branch in Parse<List<BranchDto>>(json))
        {
            heads[branch.Name] = branch.Commit.Sha;
        }
    }

    private static GitHubCommit ToCommit(CommitDto dto) => new(
        dto.Sha,
        dto.Commit.Author?.Name ?? string.Empty,
        dto.Commit.Author?.Email ?? string.Empty,
        dto.Commit.Committer?.Date ?? dto.Commit.Author?.Date ?? DateTimeOffset.UnixEpoch,
        dto.Commit.Message ?? string.Empty,
        [.. (dto.Parents ?? []).Select(parent => parent.Sha)]);

    private static GitHubFileChange ToFileChange(FileDto dto) =>
        new(dto.Filename, dto.PreviousFilename, dto.Status ?? "changed", dto.Additions, dto.Deletions, dto.Patch);

    // ---- wire DTOs (snake_case) ---------------------------------------------------------------------------------

    private sealed record RepositoryDto(string DefaultBranch, bool Private);

    private sealed record ShaDto(string Sha);

    private sealed record BranchDto(string Name, ShaDto Commit);

    private sealed record PersonDto(string? Name, string? Email, DateTimeOffset? Date);

    private sealed record CommitCoreDto(PersonDto? Author, PersonDto? Committer, string? Message);

    private sealed record FileDto(string Filename, string? PreviousFilename, string? Status, int Additions, int Deletions, string? Patch);

    private sealed record CommitDto(string Sha, CommitCoreDto Commit, List<ShaDto>? Parents, List<FileDto>? Files);

    private sealed record CompareDto(
        string Status, int AheadBy, int BehindBy, ShaDto? BaseCommit, List<CommitDto>? Commits, List<FileDto>? Files);
}
