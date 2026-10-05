using System.Net;
using System.Text.Json;
using CodebaseGuardian.GitHub;
using CodebaseGuardian.Hosting;
using CodebaseGuardian.Processes;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Tests.App;

public class GitHubClientTests
{
    private static readonly DateTimeOffset Since = new(2026, 10, 4, 12, 30, 15, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IGitHubClient CreateClient(FakeGitHubApi api, Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        var services = new ServiceCollection();
        services.AddGitHubIntegration(configuration);
        api.Install(services);
        return services.BuildServiceProvider().GetRequiredService<IGitHubClient>();
    }

    private static GitHubClient CreateDirect(
        bool enabled, string? token, GitHubRepositoryRef? repository, FakeGitHubApi? api = null) =>
        new(new HttpClient(api ?? new FakeGitHubApi()) { BaseAddress = new Uri("https://api.github.com") },
            Options.Create(new GitHubOptions { Enabled = enabled }),
            new TokenStub(token),
            new ResolverStub(repository));

    private sealed class TokenStub(string? token) : IGitHubTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult(token);
    }

    private sealed class ResolverStub(GitHubRepositoryRef? repository) : IGitHubRepositoryResolver
    {
        public ValueTask<GitHubRepositoryRef?> ResolveAsync(CancellationToken cancellationToken) => ValueTask.FromResult(repository);
    }

    private const string IssueJson = """
        {"id":11,"number":7,"title":"Bug","body":"b","html_url":"https://github.com/acme/widgets/issues/7",
         "user":{"login":"octo"},"labels":[{"name":"bug"},{"name":"p1"}],"created_at":"2026-10-04T13:00:00Z"}
        """;

    // ---- requests -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_request_carries_the_github_headers()
    {
        var api = new FakeGitHubApi()
            .MapJson(HttpMethod.Get, "/repos/acme/widgets", 200, """{"default_branch":"main"}""");

        var branch = await CreateClient(api).GetDefaultBranchAsync(Ct);

        Assert.Equal("main", branch);
        var request = Assert.Single(api.Requests);
        Assert.Equal($"Bearer {FakeGitHubApi.Token}", request.Headers["Authorization"]);
        Assert.Equal("application/vnd.github+json", request.Headers["Accept"]);
        Assert.Equal("2022-11-28", request.Headers["X-GitHub-Api-Version"]);
        Assert.Equal("codebase-guardian", request.Headers["User-Agent"]);
    }

    [Fact]
    public async Task Create_issue_posts_the_body_and_parses_the_result()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Post, "/repos/acme/widgets/issues", 201, IssueJson);

        var issue = await CreateClient(api).CreateIssueAsync("Bug", "details", ["bug"], Ct);

        Assert.Equal(7, issue.Number);
        Assert.Equal(11, issue.Id);
        Assert.Equal("https://github.com/acme/widgets/issues/7", issue.HtmlUrl);
        Assert.Equal("octo", issue.User.Login);
        Assert.Equal(["bug", "p1"], issue.Labels);
        Assert.False(issue.IsPullRequest);
        var sent = JsonDocument.Parse(Assert.Single(api.Requests).Body).RootElement;
        Assert.Equal("Bug", sent.GetProperty("title").GetString());
        Assert.Equal("details", sent.GetProperty("body").GetString());
        Assert.Equal("bug", sent.GetProperty("labels")[0].GetString());
    }

    [Fact]
    public async Task Create_comment_and_pull_request_post_to_their_endpoints()
    {
        var api = new FakeGitHubApi()
            .MapJson(HttpMethod.Post, "/repos/acme/widgets/issues/7/comments", 201,
                """{"id":5,"body":"hi","html_url":"https://github.com/acme/widgets/pull/7#c","issue_url":"https://api.github.com/repos/acme/widgets/issues/7","user":{"login":"octo"},"created_at":"2026-10-04T13:00:00Z"}""")
            .MapJson(HttpMethod.Post, "/repos/acme/widgets/pulls", 201,
                """{"id":9,"number":8,"title":"T","html_url":"https://github.com/acme/widgets/pull/8","head":{"ref":"feature/x"},"base":{"ref":"main"},"draft":true}""");
        var client = CreateClient(api);

        var comment = await client.CreateIssueCommentAsync(7, "hi", Ct);
        var pull = await client.CreatePullRequestAsync("feature/x", "main", "T", "body", true, Ct);

        Assert.Equal("issue", comment.Kind);
        Assert.Equal(7, comment.IssueNumber);
        Assert.True(comment.OnPullRequest);
        Assert.Equal(new GitHubPullRequest(9, 8, "T", "https://github.com/acme/widgets/pull/8", "feature/x", "main", true), pull);
        var sent = JsonDocument.Parse(api.Requests[1].Body).RootElement;
        Assert.Equal("feature/x", sent.GetProperty("head").GetString());
        Assert.Equal("main", sent.GetProperty("base").GetString());
        Assert.True(sent.GetProperty("draft").GetBoolean());
        Assert.Equal("hi", JsonDocument.Parse(api.Requests[0].Body).RootElement.GetProperty("body").GetString());
    }

    // ---- lists ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Issue_list_flags_pull_requests_and_sends_the_query()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets/issues", 200,
            "[" + IssueJson + ",{\"id\":12,\"number\":8,\"title\":\"PR\",\"body\":null,\"html_url\":\"https://github.com/acme/widgets/pull/8\"," +
            "\"user\":{\"login\":\"octo\"},\"labels\":[],\"created_at\":\"2026-10-04T14:00:00Z\",\"pull_request\":{\"url\":\"x\"}}]");

        var issues = await CreateClient(api).ListIssuesSinceAsync(Since, Ct);

        Assert.Equal([false, true], issues.Select(i => i.IsPullRequest));
        Assert.Null(issues[1].Body);
        var query = Uri.UnescapeDataString(Assert.Single(api.Requests).PathAndQuery);
        Assert.Contains("state=all", query);
        Assert.Contains("sort=created&direction=desc", query);
        Assert.Contains("since=2026-10-04T12:30:15Z", query);
        Assert.Contains("per_page=100", query);
    }

    [Fact]
    public async Task Issue_comments_parse_issue_number_and_pull_request_flag()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets/issues/comments", 200, """
            [{"id":1,"body":"a","html_url":"https://github.com/acme/widgets/issues/3#issuecomment-1","issue_url":"https://api.github.com/repos/acme/widgets/issues/3","user":{"login":"u"},"created_at":"2026-10-04T13:00:00Z"},
             {"id":2,"body":"b","html_url":"https://github.com/acme/widgets/pull/4#issuecomment-2","issue_url":"https://api.github.com/repos/acme/widgets/issues/4","user":{"login":"u"},"created_at":"2026-10-04T13:05:00Z"}]
            """);

        var comments = await CreateClient(api).ListIssueCommentsSinceAsync(Since, Ct);

        Assert.All(comments, c => Assert.Equal("issue", c.Kind));
        Assert.Equal([3, 4], comments.Select(c => c.IssueNumber));
        Assert.Equal([false, true], comments.Select(c => c.OnPullRequest));
        Assert.Contains("direction=asc", Assert.Single(api.Requests).PathAndQuery);
    }

    [Fact]
    public async Task Review_comments_parse_path_line_and_pull_request_number()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets/pulls/comments", 200, """
            [{"id":9,"body":"nit","html_url":"https://github.com/acme/widgets/pull/4#discussion_r9","pull_request_url":"https://api.github.com/repos/acme/widgets/pulls/4","user":{"login":"rev"},"created_at":"2026-10-04T13:00:00Z","path":"src/A.cs","line":42},
             {"id":10,"body":"gone","html_url":"https://github.com/acme/widgets/pull/4#discussion_r10","pull_request_url":"https://api.github.com/repos/acme/widgets/pulls/4","user":{"login":"rev"},"created_at":"2026-10-04T13:01:00Z","path":"src/B.cs","line":null}]
            """);

        var comments = await CreateClient(api).ListReviewCommentsSinceAsync(Since, Ct);

        Assert.All(comments, c =>
        {
            Assert.Equal("review", c.Kind);
            Assert.True(c.OnPullRequest);
            Assert.Equal(4, c.IssueNumber);
        });
        Assert.Equal(("src/A.cs", 42), (comments[0].Path, comments[0].Line));
        Assert.Null(comments[1].Line);
    }

    [Fact]
    public async Task Failed_workflow_runs_parse_attempt_and_conclusion()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets/actions/runs", 200, """
            {"total_count":1,"workflow_runs":[{"id":77,"run_attempt":2,"name":"CI","head_branch":"main","head_sha":"abc123","html_url":"https://github.com/acme/widgets/actions/runs/77","conclusion":"failure","updated_at":"2026-10-04T13:00:00Z"}]}
            """);

        var runs = await CreateClient(api).ListFailedWorkflowRunsSinceAsync(Since, Ct);

        var run = Assert.Single(runs);
        Assert.Equal((77L, 2, "CI", "main", "abc123", "failure"), (run.Id, run.RunAttempt, run.Name, run.HeadBranch, run.HeadSha, run.Conclusion));
        var query = Uri.UnescapeDataString(Assert.Single(api.Requests).PathAndQuery);
        Assert.Contains("status=failure", query);
        Assert.Contains("created=>=2026-10-04T12:30:15Z", query);
    }

    [Fact]
    public async Task Link_header_pagination_returns_both_pages()
    {
        var api = new FakeGitHubApi().Map(HttpMethod.Get, "/repos/acme/widgets/issues", (request, _) =>
        {
            var second = request.RequestUri!.Query.Contains("page=2");
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[" + (second ? IssueJson.Replace("\"number\":7", "\"number\":8") : IssueJson) + "]"),
            };
            if (!second)
            {
                response.Headers.TryAddWithoutValidation("Link",
                    "<https://api.github.com/repos/acme/widgets/issues?page=2>; rel=\"next\", <https://api.github.com/repos/acme/widgets/issues?page=2>; rel=\"last\"");
            }

            return response;
        });

        var issues = await CreateClient(api).ListIssuesSinceAsync(Since, Ct);

        Assert.Equal([7, 8], issues.Select(i => i.Number));
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public async Task Pagination_stops_after_five_pages_and_never_leaves_the_api_host()
    {
        var api = new FakeGitHubApi().Map(HttpMethod.Get, "/repos/acme/widgets/issues", (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[" + IssueJson + "]") };
            response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/repos/acme/widgets/issues?page=n>; rel=\"next\"");
            return response;
        }).Map(HttpMethod.Get, "/evil", (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });

        var issues = await CreateClient(api).ListIssuesSinceAsync(Since, Ct);
        Assert.Equal(5, issues.Count);
        Assert.Equal(5, api.Requests.Count);

        var foreign = new FakeGitHubApi().Map(HttpMethod.Get, "/repos/acme/widgets/issues", (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[" + IssueJson + "]") };
            response.Headers.TryAddWithoutValidation("Link", "<https://evil.example/evil>; rel=\"next\"");
            return response;
        });
        Assert.Single(await CreateClient(foreign).ListIssuesSinceAsync(Since, Ct));
        Assert.Single(foreign.Requests);
    }

    // ---- branches ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Branch_exists_maps_200_and_404_and_rejects_invalid_names()
    {
        var api = new FakeGitHubApi();
        var client = CreateClient(api);

        Assert.False(await client.BranchExistsAsync("nope", Ct));
        // 'x y' is not a valid revision; the validator rejects it before any request is made.
        await Assert.ThrowsAsync<ArgumentException>(() => client.BranchExistsAsync("feature/x y", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.BranchExistsAsync("--upload-pack=x", Ct));
        Assert.Equal("/repos/acme/widgets/branches/nope", Assert.Single(api.Requests).PathAndQuery);

        var found = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets/branches/feature/x", 200, "{}");
        Assert.True(await CreateClient(found).BranchExistsAsync("feature/x", Ct));
    }

    // ---- errors --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rate_limit_403_reports_the_reset_time()
    {
        var reset = new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets", 403, """{"message":"API rate limit exceeded"}""",
            new Dictionary<string, string>
            {
                ["x-ratelimit-remaining"] = "0",
                ["x-ratelimit-reset"] = reset.ToUnixTimeSeconds().ToString(),
            });

        var exception = await Assert.ThrowsAsync<GitHubRateLimitException>(() => CreateClient(api).GetDefaultBranchAsync(Ct));

        Assert.Equal(reset, exception.ResetAt);
        Assert.Equal(429, exception.StatusCode);
    }

    [Fact]
    public async Task Retry_after_gives_a_reset_relative_to_now()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets", 429, "{}",
            new Dictionary<string, string> { ["retry-after"] = "60" });

        var before = DateTimeOffset.UtcNow;
        var exception = await Assert.ThrowsAsync<GitHubRateLimitException>(() => CreateClient(api).GetDefaultBranchAsync(Ct));

        Assert.InRange(exception.ResetAt, before.AddSeconds(59), DateTimeOffset.UtcNow.AddSeconds(61));
    }

    [Fact]
    public async Task Unauthorized_says_the_token_was_rejected_without_echoing_it()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets", 401, """{"message":"Bad credentials"}""");

        var exception = await Assert.ThrowsAsync<GitHubApiException>(() => CreateClient(api).GetDefaultBranchAsync(Ct));

        Assert.Equal(401, exception.StatusCode);
        Assert.Equal("GitHub rejected the token (401).", exception.Message);
    }

    [Fact]
    public async Task Validation_error_carries_githubs_message_and_never_the_token()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Post, "/repos/acme/widgets/issues", 422,
            """{"message":"Validation Failed","errors":[{"resource":"Issue","code":"missing_field","message":"title is required"},"plain error"]}""");

        var exception = await Assert.ThrowsAsync<GitHubApiException>(() => CreateClient(api).CreateIssueAsync("", "b", [], Ct));

        Assert.Equal(422, exception.StatusCode);
        Assert.Contains("Validation Failed", exception.Message);
        Assert.Contains("title is required", exception.Message);
        Assert.DoesNotContain(FakeGitHubApi.Token, exception.Message);
        Assert.DoesNotContain(FakeGitHubApi.Token, exception.ToString());
    }

    [Fact]
    public async Task Long_error_messages_are_cut_to_500_characters()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets", 500,
            JsonSerializer.Serialize(new { message = new string('x', 2000) }));

        var exception = await Assert.ThrowsAsync<GitHubApiException>(() => CreateClient(api).GetDefaultBranchAsync(Ct));

        Assert.Equal(500, exception.Message.Length);
    }

    // ---- availability --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Disabled_integration_throws_unavailable_without_a_request()
    {
        var api = new FakeGitHubApi();
        var client = CreateClient(api, new() { ["Guardian:GitHub:Enabled"] = "false" });

        var exception = await Assert.ThrowsAsync<GitHubUnavailableException>(() => client.GetRepositoryAsync(Ct));

        Assert.Equal("GitHub integration is disabled (Guardian:GitHub:Enabled=false).", exception.Message);
        await Assert.ThrowsAsync<GitHubUnavailableException>(() => client.ListIssuesSinceAsync(Since, Ct));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Missing_token_throws_unavailable()
    {
        var client = CreateDirect(true, null, new GitHubRepositoryRef("acme", "widgets"));

        var exception = await Assert.ThrowsAsync<GitHubUnavailableException>(() => client.GetDefaultBranchAsync(Ct));

        Assert.Equal("No GitHub token: set GITHUB_TOKEN or run `gh auth login`.", exception.Message);
    }

    [Fact]
    public async Task Non_github_origin_throws_unavailable()
    {
        var client = CreateDirect(true, FakeGitHubApi.Token, null);

        var exception = await Assert.ThrowsAsync<GitHubUnavailableException>(() => client.GetRepositoryAsync(Ct));

        Assert.Equal(
            "The origin remote is not a github.com repository; set Guardian:GitHub:Owner and Guardian:GitHub:Repository.",
            exception.Message);
    }

    [Fact]
    public async Task Get_repository_returns_the_resolved_repository()
    {
        var repository = await CreateClient(new FakeGitHubApi()).GetRepositoryAsync(Ct);

        Assert.Equal(new GitHubRepositoryRef("acme", "widgets"), repository);
    }

    // ---- resolver and options -----------------------------------------------------------------------------------

    [Fact]
    public async Task Resolver_prefers_configured_owner_and_repository_and_otherwise_parses_origin()
    {
        using var repo = TempGitRepo.Create();
        repo.Git("remote", "add", "origin", "git@github.com:from-origin/repo.git");
        var git = new CodebaseGuardian.Git.GitRepository(
            new ProcessRunner(), Options.Create(new GuardianOptions { RepositoryPath = repo.Path }));

        var derived = new GitHubRepositoryResolver(git, Options.Create(new GitHubOptions()));
        var pinned = new GitHubRepositoryResolver(git, Options.Create(new GitHubOptions { Owner = "acme", Repository = "widgets" }));

        Assert.Equal(new GitHubRepositoryRef("from-origin", "repo"), await derived.ResolveAsync(Ct));
        Assert.Equal(new GitHubRepositoryRef("acme", "widgets"), await pinned.ResolveAsync(Ct));
    }

    [Fact]
    public async Task Resolver_gives_null_without_a_github_origin()
    {
        using var repo = TempGitRepo.Create();
        var git = new CodebaseGuardian.Git.GitRepository(
            new ProcessRunner(), Options.Create(new GuardianOptions { RepositoryPath = repo.Path }));

        Assert.Null(await new GitHubRepositoryResolver(git, Options.Create(new GitHubOptions())).ResolveAsync(Ct));

        repo.Git("remote", "add", "origin", "https://gitlab.com/a/b.git");
        Assert.Null(await new GitHubRepositoryResolver(git, Options.Create(new GitHubOptions())).ResolveAsync(Ct));
    }

    [Theory]
    [InlineData(14, true)]
    [InlineData(15, false)]
    public void Poll_interval_must_be_at_least_15_seconds(int seconds, bool fails)
    {
        var result = new GitHubOptionsValidator().Validate(null, new GitHubOptions { PollIntervalSeconds = seconds });

        Assert.Equal(fails, result.Failed);
        if (fails)
        {
            Assert.Contains("Guardian:GitHub:PollIntervalSeconds", result.FailureMessage);
        }
    }

    [Fact]
    public void Owner_and_repository_must_be_safe_path_segments()
    {
        var validator = new GitHubOptionsValidator();

        Assert.True(validator.Validate(null, new GitHubOptions { Owner = "a/b", Repository = "r" }).Failed);
        Assert.True(validator.Validate(null, new GitHubOptions { Owner = "a", Repository = "../r" }).Failed);
        Assert.True(validator.Validate(null, new GitHubOptions { Owner = "acme", Repository = "widgets" }).Succeeded);
    }

    [Theory]
    [InlineData(".", "r")]
    [InlineData("..", "r")]
    [InlineData("o", "..")]
    public void Dot_segments_are_rejected_in_options(string owner, string repository) =>
        Assert.True(new GitHubOptionsValidator().Validate(null, new GitHubOptions { Owner = owner, Repository = repository }).Failed);

    [Fact]
    public void The_request_context_never_prints_the_token()
    {
        var context = new GitHubClient.Context(new GitHubRepositoryRef("acme", "widgets"), "ghp_secret-token-value");

        Assert.DoesNotContain("ghp_secret-token-value", context.ToString());
        Assert.Equal("acme/widgets", context.ToString());
    }

    // ---- test hosts ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_hosts_disable_github_by_default_and_a_caller_can_override_it()
    {
        using var repo = TempGitRepo.Create();

        await using (var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct))
        {
            Assert.False(server.Services.GetRequiredService<IOptions<GitHubOptions>>().Value.Enabled);
            Assert.False(server.Services.GetRequiredService<IOptions<GitHubOptions>>().Value.PollEnabled);
        }

        await using (var server = await GuardianTestHost.StartAsync(
            repo.Path, new Dictionary<string, string?> { ["Guardian:GitHub:Enabled"] = "true" }, cancellationToken: Ct))
        {
            Assert.True(server.Services.GetRequiredService<IOptions<GitHubOptions>>().Value.Enabled);
        }

        await using var http = await GuardianHttpTestHost.StartAsync(
            repo.Path, new Dictionary<string, string?> { ["Guardian:GitHub:PollEnabled"] = "true" }, cancellationToken: Ct);
        var options = http.Services.GetRequiredService<IOptions<GitHubOptions>>().Value;
        Assert.False(options.Enabled);
        Assert.True(options.PollEnabled);
    }
}
