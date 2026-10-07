using System.Globalization;
using System.Net;
using System.Text.Json;
using CodebaseGuardian.Git;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.GitHub;

public sealed class GitHubClient : IGitHubClient
{
    public const string HttpClientName = "github";

    private const string Disabled = "GitHub integration is disabled (Guardian:GitHub:Enabled=false).";
    private const string NoToken = "No GitHub token: set GITHUB_TOKEN or run `gh auth login`.";
    private const string NoRepository =
        "The origin remote is not a github.com repository; set Guardian:GitHub:Owner and Guardian:GitHub:Repository.";

    private readonly HttpClient _http;
    private readonly GitHubOptions _options;
    private readonly IGitHubTokenProvider _tokens;
    private readonly IGitHubRepositoryResolver _resolver;
    private readonly TimeProvider _time;

    public GitHubClient(
        HttpClient http,
        IOptions<GitHubOptions> options,
        IGitHubTokenProvider tokens,
        IGitHubRepositoryResolver resolver,
        TimeProvider? timeProvider = null)
    {
        _http = http;
        _options = options.Value;
        _tokens = tokens;
        _resolver = resolver;
        _time = timeProvider ?? TimeProvider.System;
        _http.BaseAddress ??= new Uri(_options.ApiBaseUrl.TrimEnd('/') + "/");
    }

    public async Task<GitHubRepositoryRef> GetRepositoryAsync(CancellationToken ct) => (await PrepareAsync(ct)).Repository;

    public async Task<string> GetDefaultBranchAsync(CancellationToken ct)
    {
        var context = await PrepareAsync(ct);
        var repository = await SendJsonAsync<RepositoryDto>(context, HttpMethod.Get, context.Path(), null, ct);
        return repository.DefaultBranch;
    }

    public async Task<bool> BranchExistsAsync(string branch, CancellationToken ct)
    {
        GitRevision.Require(branch, nameof(branch));
        var context = await PrepareAsync(ct);
        var escaped = string.Join('/', branch.Split('/').Select(Uri.EscapeDataString));
        using var response = await Http().SendAsync(
            HttpMethod.Get, context.Path("branches", escaped), null, context.Token, ct, allow: HttpStatusCode.NotFound);
        return response.StatusCode != HttpStatusCode.NotFound;
    }

    public async Task<GitHubIssue> CreateIssueAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken ct)
    {
        var context = await PrepareAsync(ct);
        var dto = await SendJsonAsync<IssueDto>(context, HttpMethod.Post, context.Path("issues"), new { title, body, labels }, ct);
        return ToIssue(dto);
    }

    public async Task<GitHubComment> CreateIssueCommentAsync(int number, string body, CancellationToken ct)
    {
        var context = await PrepareAsync(ct);
        var dto = await SendJsonAsync<CommentDto>(
            context, HttpMethod.Post, context.Path("issues", number.ToString(CultureInfo.InvariantCulture), "comments"), new { body }, ct);
        return ToComment(dto, "issue", dto.IssueUrl);
    }

    public async Task<GitHubPullRequest> CreatePullRequestAsync(
        string head, string @base, string title, string body, bool draft, CancellationToken ct)
    {
        var context = await PrepareAsync(ct);
        var dto = await SendJsonAsync<PullRequestDto>(
            context, HttpMethod.Post, context.Path("pulls"), new { title, head, @base, body, draft }, ct);
        return new GitHubPullRequest(dto.Id, dto.Number, dto.Title, dto.HtmlUrl, dto.Head.Ref, dto.Base.Ref, dto.Draft);
    }

    public async Task<IReadOnlyList<GitHubIssue>> ListIssuesSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        var context = await PrepareAsync(ct);
        var query = $"state=all&sort=created&direction=desc&since={Timestamp(since)}&per_page=100";
        var items = await ListAsync<IssueDto>(context, context.Path("issues") + "?" + query, ct);
        return [.. items.Select(ToIssue)];
    }

    public async Task<IReadOnlyList<GitHubComment>> ListIssueCommentsSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        var context = await PrepareAsync(ct);
        var query = $"sort=created&direction=asc&since={Timestamp(since)}&per_page=100";
        var items = await ListAsync<CommentDto>(context, context.Path("issues", "comments") + "?" + query, ct);
        return [.. items.Select(item => ToComment(item, "issue", item.IssueUrl))];
    }

    public async Task<IReadOnlyList<GitHubComment>> ListReviewCommentsSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        var context = await PrepareAsync(ct);
        var query = $"sort=created&direction=asc&since={Timestamp(since)}&per_page=100";
        var items = await ListAsync<CommentDto>(context, context.Path("pulls", "comments") + "?" + query, ct);
        return [.. items.Select(item => ToComment(item, "review", item.PullRequestUrl) with { OnPullRequest = true })];
    }

    public async Task<IReadOnlyList<GitHubWorkflowRun>> ListFailedWorkflowRunsSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        var context = await PrepareAsync(ct);
        var created = Uri.EscapeDataString(">=" + Timestamp(since, escape: false));
        var path = context.Path("actions", "runs") + $"?status=failure&created={created}&per_page=100";
        var pages = await ListAsync<WorkflowRunsDto>(context, path, ct);
        return [.. pages.SelectMany(page => page.WorkflowRuns ?? [])
            .Select(run => new GitHubWorkflowRun(
                run.Id, run.RunAttempt, run.Name ?? string.Empty, run.HeadBranch ?? string.Empty, run.HeadSha ?? string.Empty,
                run.HtmlUrl ?? string.Empty, run.Conclusion, run.UpdatedAt))];
    }

    // ---- plumbing -----------------------------------------------------------------------------------------------

    internal sealed record Context(GitHubRepositoryRef Repository, string Token)
    {
        public string Path(params string[] segments) =>
            $"repos/{Uri.EscapeDataString(Repository.Owner)}/{Uri.EscapeDataString(Repository.Name)}"
            + string.Concat(segments.Select(segment => "/" + segment));

        // The compiler-generated ToString would print the token.
        public override string ToString() => Repository.ToString();
    }

    private async Task<Context> PrepareAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            throw new GitHubUnavailableException(Disabled);
        }

        var token = await _tokens.GetTokenAsync(ct) ?? throw new GitHubUnavailableException(NoToken);
        var repository = await _resolver.ResolveAsync(ct) ?? throw new GitHubUnavailableException(NoRepository);
        return new Context(repository, token);
    }

    private static string Timestamp(DateTimeOffset value, bool escape = true)
    {
        var text = value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return escape ? Uri.EscapeDataString(text) : text;
    }

    private GitHubHttp Http() => new(_http, _options, _time);

    private Task<T> SendJsonAsync<T>(Context context, HttpMethod method, string path, object? body, CancellationToken ct) =>
        Http().SendJsonAsync<T>(method, path, body, context.Token, ct);

    private Task<List<T>> ListAsync<T>(Context context, string firstPath, CancellationToken ct) =>
        Http().ListAsync<T>(firstPath, context.Token, ct);

    private static int LastSegmentNumber(string? url) =>
        url is not null && int.TryParse(url.TrimEnd('/').Split('/')[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;

    private static GitHubIssue ToIssue(IssueDto dto) => new(
        dto.Id, dto.Number, dto.Title ?? string.Empty, dto.Body, dto.HtmlUrl ?? string.Empty,
        new GitHubUser(dto.User?.Login ?? string.Empty),
        [.. (dto.Labels ?? []).Select(label => label.Name ?? string.Empty)],
        dto.CreatedAt, dto.PullRequest is not null);

    private static GitHubComment ToComment(CommentDto dto, string kind, string? numberUrl) => new(
        dto.Id, kind, LastSegmentNumber(numberUrl), dto.Body, dto.HtmlUrl ?? string.Empty,
        new GitHubUser(dto.User?.Login ?? string.Empty), dto.CreatedAt, dto.Path, dto.Line,
        dto.HtmlUrl?.Contains("/pull/", StringComparison.Ordinal) ?? false);

    // ---- wire DTOs (snake_case) ---------------------------------------------------------------------------------

    private sealed record UserDto(string? Login);

    private sealed record LabelDto(string? Name);

    private sealed record RepositoryDto(string DefaultBranch);

    private sealed record IssueDto(
        long Id, int Number, string? Title, string? Body, string? HtmlUrl, UserDto? User, List<LabelDto>? Labels,
        DateTimeOffset CreatedAt, JsonElement? PullRequest);

    private sealed record CommentDto(
        long Id, string? Body, string? HtmlUrl, string? IssueUrl, string? PullRequestUrl, UserDto? User,
        DateTimeOffset CreatedAt, string? Path, int? Line);

    private sealed record RefDto(string Ref);

    private sealed record PullRequestDto(long Id, int Number, string Title, string HtmlUrl, RefDto Head, RefDto Base, bool Draft);

    private sealed record WorkflowRunDto(
        long Id, int RunAttempt, string? Name, string? HeadBranch, string? HeadSha, string? HtmlUrl, string? Conclusion,
        DateTimeOffset UpdatedAt);

    private sealed record WorkflowRunsDto(List<WorkflowRunDto>? WorkflowRuns);
}
