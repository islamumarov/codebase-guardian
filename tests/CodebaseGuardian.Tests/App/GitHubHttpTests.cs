using System.Net;
using CodebaseGuardian.GitHub;
using CodebaseGuardian.Tests.Infrastructure;

namespace CodebaseGuardian.Tests.App;

public class GitHubHttpTests
{
    private const string Secret = "ghp_secret-token-value";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GitHubHttp Create(FakeGitHubApi api, TimeProvider? time = null) =>
        new(new HttpClient(api) { BaseAddress = new Uri("https://api.github.com/") },
            new GitHubOptions(),
            time ?? TimeProvider.System);

    [Fact]
    public async Task Without_a_token_no_Authorization_header_is_sent()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/a/b", 200, "{}");

        using var response = await Create(api).SendAsync(HttpMethod.Get, "repos/a/b", null, null, Ct);

        var headers = Assert.Single(api.Requests).Headers;
        Assert.False(headers.ContainsKey("Authorization"));
        Assert.Equal("application/vnd.github+json", headers["Accept"]);
        Assert.Equal("2022-11-28", headers["X-GitHub-Api-Version"]);
        Assert.Equal("codebase-guardian", headers["User-Agent"]);
    }

    [Fact]
    public async Task With_a_token_the_request_carries_a_Bearer_header()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/a/b", 200, "{}");

        using var response = await Create(api).SendAsync(HttpMethod.Get, "repos/a/b", null, Secret, Ct);

        Assert.Equal($"Bearer {Secret}", Assert.Single(api.Requests).Headers["Authorization"]);
    }

    [Fact]
    public async Task A_custom_accept_replaces_the_default()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/a/b/contents/x", 200, "raw");

        using var response = await Create(api).SendAsync(
            HttpMethod.Get, "repos/a/b/contents/x", null, null, Ct, accept: "application/vnd.github.raw+json");

        Assert.Equal("application/vnd.github.raw+json", Assert.Single(api.Requests).Headers["Accept"]);
    }

    [Fact]
    public async Task An_allowed_status_is_returned_not_thrown()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/a/b/commits", 409, "{\"message\":\"Git Repository is empty.\"}");

        using var response = await Create(api).SendAsync(
            HttpMethod.Get, "repos/a/b/commits", null, null, Ct, allow: HttpStatusCode.Conflict);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_status_that_is_not_allowed_throws_with_the_GitHub_message()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/a/b/commits", 409, "{\"message\":\"Git Repository is empty.\"}");

        var error = await Assert.ThrowsAsync<GitHubApiException>(
            () => Create(api).SendAsync(HttpMethod.Get, "repos/a/b/commits", null, null, Ct));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("Git Repository is empty.", error.Message);
    }

    [Fact]
    public async Task Listing_follows_the_next_link_across_two_pages()
    {
        var api = new FakeGitHubApi()
            .MapJson(HttpMethod.Get, "/repos/a/b/items", 200, "[1,2]",
                new Dictionary<string, string> { ["Link"] = "<https://api.github.com/repos/a/b/items2>; rel=\"next\"" })
            .MapJson(HttpMethod.Get, "/repos/a/b/items2", 200, "[3]");

        var items = await Create(api).ListAsync<int>("repos/a/b/items", Secret, Ct);

        Assert.Equal([1, 2, 3], items);
    }

    [Fact]
    public async Task Listing_refuses_a_next_link_to_another_host()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/a/b/items", 200, "[1]",
            new Dictionary<string, string> { ["Link"] = "<https://evil.example/steal>; rel=\"next\"" });

        var items = await Create(api).ListAsync<int>("repos/a/b/items", Secret, Ct);

        Assert.Equal([1], items);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task Listing_stops_at_an_allowed_status_with_what_was_read()
    {
        var api = new FakeGitHubApi()
            .MapJson(HttpMethod.Get, "/repos/a/b/items", 200, "[1]",
                new Dictionary<string, string> { ["Link"] = "<https://api.github.com/repos/a/b/items2>; rel=\"next\"" })
            .MapJson(HttpMethod.Get, "/repos/a/b/items2", 409, "{\"message\":\"gone\"}");

        var items = await Create(api).ListAsync<int>("repos/a/b/items", null, Ct, allow: HttpStatusCode.Conflict);

        Assert.Equal([1], items);
    }

    [Fact]
    public async Task A_conditional_request_sends_the_cached_ETag_and_a_304_returns_the_cached_body()
    {
        var calls = 0;
        var api = new FakeGitHubApi().Map(HttpMethod.Get, "/repos/a/b/branches", (request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                var first = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"name\":\"main\"}]") };
                first.Headers.TryAddWithoutValidation("ETag", "\"v1\"");
                first.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/repos/a/b/branches?page=2>; rel=\"next\"");
                return first;
            }

            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        var http = Create(api);
        var cache = new GitHubConditionalCache();

        var first = await http.GetConditionalAsync("repos/a/b/branches", null, cache, Ct);
        var second = await http.GetConditionalAsync("repos/a/b/branches", null, cache, Ct);

        Assert.False(first.NotModified);
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal("[{\"name\":\"main\"}]", first.Body);
        Assert.True(second.NotModified);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal(first.Body, second.Body);
        Assert.Equal(first.NextLink, second.NextLink);
        Assert.NotNull(second.NextLink);
        Assert.False(api.Requests[0].Headers.ContainsKey("If-None-Match"));
        Assert.Equal("\"v1\"", api.Requests[1].Headers["If-None-Match"]);
    }

    [Fact]
    public async Task A_200_without_an_ETag_is_returned_but_not_cached()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/a/b/branches", 200, "[]");
        var cache = new GitHubConditionalCache();

        var response = await Create(api).GetConditionalAsync("repos/a/b/branches", null, cache, Ct);

        Assert.False(response.NotModified);
        Assert.False(cache.TryGet("repos/a/b/branches", out _));
    }

    [Fact]
    public async Task A_304_without_a_cache_entry_is_an_error()
    {
        var api = new FakeGitHubApi().Map(HttpMethod.Get, "/repos/a/b/branches", (_, _) => new HttpResponseMessage(HttpStatusCode.NotModified));

        var error = await Assert.ThrowsAsync<GitHubApiException>(
            () => Create(api).GetConditionalAsync("repos/a/b/branches", null, new GitHubConditionalCache(), Ct));

        Assert.Equal(304, error.StatusCode);
        Assert.Equal("GitHub returned 304 without a cached response.", error.Message);
    }

    [Fact]
    public async Task A_rate_limited_403_throws_with_the_reset_time()
    {
        var reset = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/a/b", 403, "{\"message\":\"API rate limit exceeded\"}",
            new Dictionary<string, string>
            {
                ["x-ratelimit-remaining"] = "0",
                ["x-ratelimit-reset"] = reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

        var error = await Assert.ThrowsAsync<GitHubRateLimitException>(
            () => Create(api).SendAsync(HttpMethod.Get, "repos/a/b", null, null, Ct));

        Assert.Equal(reset, error.ResetAt);
    }

    [Fact]
    public async Task An_error_message_never_contains_the_token()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/a/b", 422, $"{{\"message\":\"bad credentials {Secret} given\"}}");

        var error = await Assert.ThrowsAsync<GitHubApiException>(
            () => Create(api).SendAsync(HttpMethod.Get, "repos/a/b", null, Secret, Ct));

        Assert.DoesNotContain(Secret, error.Message);
        Assert.Contains("***", error.Message);
    }

    [Fact]
    public async Task A_401_names_the_rejected_token_without_echoing_it()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/a/b", 401, "{\"message\":\"Bad credentials\"}");

        var error = await Assert.ThrowsAsync<GitHubApiException>(
            () => Create(api).SendAsync(HttpMethod.Get, "repos/a/b", null, Secret, Ct));

        Assert.Equal(401, error.StatusCode);
        Assert.DoesNotContain(Secret, error.Message);
    }
}
