using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodebaseGuardian.Git;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.GitHub;

public sealed partial class GitHubClient : IGitHubClient
{
    public const string HttpClientName = "github";

    private const int MaxPages = 5;
    private const int MaxErrorMessageLength = 500;
    private const string Disabled = "GitHub integration is disabled (Guardian:GitHub:Enabled=false).";
    private const string NoToken = "No GitHub token: set GITHUB_TOKEN or run `gh auth login`.";
    private const string NoRepository =
        "The origin remote is not a github.com repository; set Guardian:GitHub:Owner and Guardian:GitHub:Repository.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

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
        using var response = await SendAsync(context, HttpMethod.Get, context.Path("branches", escaped), null, ct, allowNotFound: true);
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

    private async Task<T> SendJsonAsync<T>(Context context, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendAsync(context, method, path, body, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
            ?? throw new GitHubApiException((int)response.StatusCode, "GitHub returned an empty response.");
    }

    /// <summary>Follows <c>Link: rel="next"</c> for at most <see cref="MaxPages"/> pages, and never off the API host.</summary>
    private async Task<List<T>> ListAsync<T>(Context context, string firstPath, CancellationToken ct)
    {
        var results = new List<T>();
        string? next = firstPath;
        for (var page = 0; page < MaxPages && next is not null; page++)
        {
            using var response = await SendAsync(context, HttpMethod.Get, next, null, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            if (content.TrimStart().StartsWith('['))
            {
                results.AddRange(JsonSerializer.Deserialize<List<T>>(content, Json) ?? []);
            }
            else if (JsonSerializer.Deserialize<T>(content, Json) is { } single)
            {
                results.Add(single);
            }

            next = NextLink(response);
        }

        return results;
    }

    private string? NextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        foreach (var match in LinkPattern().Matches(string.Join(',', values)).Cast<Match>())
        {
            if (match.Groups["rel"].Value == "next"
                && Uri.TryCreate(_http.BaseAddress, match.Groups["url"].Value, out var uri)
                && uri.Scheme == _http.BaseAddress!.Scheme
                && uri.Host == _http.BaseAddress.Host
                && uri.Port == _http.BaseAddress.Port)
            {
                return uri.AbsoluteUri;
            }
        }

        return null;
    }

    [GeneratedRegex("<(?<url>[^>]+)>\\s*;\\s*rel=\"(?<rel>[^\"]+)\"")]
    private static partial Regex LinkPattern();

    private async Task<HttpResponseMessage> SendAsync(
        Context context, HttpMethod method, string pathOrUrl, object? body, CancellationToken ct, bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, pathOrUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("codebase-guardian");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.Token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound))
        {
            return response;
        }

        using (response)
        {
            throw await ToExceptionAsync(response, context.Token, ct);
        }
    }

    private async Task<Exception> ToExceptionAsync(HttpResponseMessage response, string token, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (status == 401)
        {
            return new GitHubApiException(401, "GitHub rejected the token (401).");
        }

        if (status is 403 or 429)
        {
            if (TryHeader(response, "retry-after", out var retryAfter)
                && double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return new GitHubRateLimitException(_time.GetUtcNow().AddSeconds(seconds));
            }

            if (TryHeader(response, "x-ratelimit-remaining", out var remaining) && remaining == "0"
                && TryHeader(response, "x-ratelimit-reset", out var reset)
                && long.TryParse(reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
            {
                return new GitHubRateLimitException(DateTimeOffset.FromUnixTimeSeconds(epoch));
            }
        }

        var message = Describe(await response.Content.ReadAsStringAsync(ct), response.ReasonPhrase);
        message = message.Replace(token, "***", StringComparison.Ordinal);
        return new GitHubApiException(status, message.Length > MaxErrorMessageLength ? message[..MaxErrorMessageLength] : message);
    }

    private static bool TryHeader(HttpResponseMessage response, string name, out string value)
    {
        value = response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() ?? string.Empty : string.Empty;
        return value.Length > 0;
    }

    private static string Describe(string body, string? fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var parts = new List<string>();
            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                parts.Add(message.GetString()!);
            }

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var error in errors.EnumerateArray())
                {
                    var text = error.ValueKind switch
                    {
                        JsonValueKind.String => error.GetString(),
                        JsonValueKind.Object when error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String => m.GetString(),
                        _ => null,
                    };
                    if (!string.IsNullOrEmpty(text))
                    {
                        parts.Add(text);
                    }
                }
            }

            if (parts.Count > 0)
            {
                return string.Join("; ", parts);
            }
        }
        catch (JsonException)
        {
            // Not a JSON error body; fall through to the status text.
        }

        return string.IsNullOrWhiteSpace(fallback) ? "GitHub request failed." : fallback;
    }

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
