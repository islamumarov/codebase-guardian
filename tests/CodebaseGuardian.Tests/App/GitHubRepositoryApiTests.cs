using System.Net;
using System.Text;
using CodebaseGuardian.GitHub;
using CodebaseGuardian.Hosting;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CodebaseGuardian.Tests.App;

public class GitHubRepositoryApiTests
{
    private const string Raw = "application/vnd.github.raw+json";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IGitHubRepositoryApi Create(
        FakeGitHubApi api, bool anonymous = false, bool enabled = true, bool noRepository = false, ILoggerProvider? logs = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Guardian:GitHub:Enabled"] = enabled ? "true" : "false" })
            .Build();
        var services = new ServiceCollection();
        if (logs is not null)
        {
            services.AddLogging(builder => builder.AddProvider(logs));
        }

        services.AddGitHubIntegration(configuration);
        api.Install(services);
        if (anonymous)
        {
            services.RemoveAll<IGitHubTokenProvider>();
            services.AddSingleton<IGitHubTokenProvider>(new TokenStub(null));
        }

        if (noRepository)
        {
            services.RemoveAll<IGitHubRepositoryResolver>();
            services.AddSingleton<IGitHubRepositoryResolver>(new ResolverStub());
        }

        return services.BuildServiceProvider().GetRequiredService<IGitHubRepositoryApi>();
    }

    private sealed class TokenStub(string? token) : IGitHubTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult(token);
    }

    private sealed class ResolverStub : IGitHubRepositoryResolver
    {
        public ValueTask<GitHubRepositoryRef?> ResolveAsync(CancellationToken cancellationToken) => ValueTask.FromResult<GitHubRepositoryRef?>(null);
    }

    private static string CommitJson(string sha, string parent = "p0", string message = "msg") => $$"""
        {"sha":"{{sha}}","commit":{"author":{"name":"Ada","email":"ada@example.com","date":"2026-10-04T12:00:00Z"},
         "committer":{"name":"Bot","email":"bot@example.com","date":"2026-10-04T13:00:00Z"},"message":"{{message}}"},
         "parents":[{"sha":"{{parent}}"}]}
        """;

    private static HttpResponseMessage Json(HttpStatusCode status, string json, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    private static string Next(string url) => $"<{url}>; rel=\"next\"";

    // ---- token ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_token_requests_are_anonymous_and_still_succeed()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets", 200, """{"default_branch":"main","private":false}""");

        var info = await Create(api, anonymous: true).GetRepositoryAsync(Ct);

        Assert.Equal("main", info.DefaultBranch);
        var request = Assert.Single(api.Requests);
        Assert.False(request.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task With_a_token_requests_carry_it_as_a_bearer()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets", 200, """{"default_branch":"main"}""");

        await Create(api).GetRepositoryAsync(Ct);

        Assert.Equal($"Bearer {FakeGitHubApi.Token}", Assert.Single(api.Requests).Headers["Authorization"]);
    }

    // ---- repository -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Repository_info_parses_the_default_branch_and_visibility()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets", 200, """{"default_branch":"trunk","private":true}""");

        var info = await Create(api).GetRepositoryAsync(Ct);

        Assert.Equal(new GitHubRepositoryInfo("trunk", true), info);
    }

    [Fact]
    public async Task A_missing_repository_names_it_in_the_exception()
    {
        var exception = await Assert.ThrowsAsync<GitHubNotFoundException>(() => Create(new FakeGitHubApi()).GetRepositoryAsync(Ct));

        Assert.Equal(404, exception.StatusCode);
        Assert.Contains("acme/widgets", exception.Message, StringComparison.Ordinal);
        Assert.Contains("contents:read", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repository_info_is_fetched_conditionally_the_second_time()
    {
        var api = new FakeGitHubApi();
        var repository = new FakeGitHubRepository(api, defaultBranch: "trunk");
        var client = Create(api);

        var first = await client.GetRepositoryAsync(Ct);
        var second = await client.GetRepositoryAsync(Ct);
        repository.DefaultBranch = "main";
        var third = await client.GetRepositoryAsync(Ct);

        Assert.Equal("trunk", first.DefaultBranch);
        Assert.Equal(first, second);
        Assert.Equal("main", third.DefaultBranch);
        Assert.False(api.Requests[0].Headers.ContainsKey("If-None-Match"));
        Assert.True(api.Requests[1].Headers.ContainsKey("If-None-Match"));
    }

    // ---- branches -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Branch_heads_are_parsed_and_the_second_call_is_conditional()
    {
        var api = new FakeGitHubApi();
        var repository = new FakeGitHubRepository(api)
            .Commit("A", "first", [])
            .Commit("B", "second", ["A"])
            .SetBranch("main", "B")
            .SetBranch("feature/x", "A");
        var client = Create(api);

        var first = await client.GetBranchHeadsAsync(Ct);
        var second = await client.GetBranchHeadsAsync(Ct);

        Assert.Equal(new Dictionary<string, string> { ["main"] = "B", ["feature/x"] = "A" }, first);
        Assert.Equal(first, second);
        Assert.Equal(2, repository.BranchListRequests);
        Assert.False(api.Requests[0].Headers.ContainsKey("If-None-Match"));
        Assert.True(api.Requests[1].Headers.ContainsKey("If-None-Match"));
        Assert.EndsWith("/branches?per_page=100", api.Requests[0].PathAndQuery, StringComparison.Ordinal);

        repository.SetBranch("main", "A");
        Assert.Equal("A", (await client.GetBranchHeadsAsync(Ct))["main"]);
    }

    [Fact]
    public async Task An_empty_repository_has_no_branch_heads_and_no_commits()
    {
        var api = new FakeGitHubApi();
        _ = new FakeGitHubRepository(api);
        var client = Create(api);

        Assert.Empty(await client.GetBranchHeadsAsync(Ct));
        Assert.Empty(await client.ListCommitsAsync(null, 30, Ct));
    }

    [Fact]
    public async Task Branch_pages_are_followed_and_merged()
    {
        var api = PagedBranches(pages: 2);

        var heads = await Create(api).GetBranchHeadsAsync(Ct);

        Assert.Equal(new Dictionary<string, string> { ["b1"] = "sha1", ["b2"] = "sha2" }, heads);
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public async Task More_than_five_branch_pages_stop_at_five_with_a_warning()
    {
        var logs = new CapturingLoggerProvider();
        var api = PagedBranches(pages: 7);

        var heads = await Create(api, logs: logs).GetBranchHeadsAsync(Ct);

        Assert.Equal(5, heads.Count);
        Assert.Equal(5, api.Requests.Count);
        Assert.Contains(logs.Messages, message => message.Contains("Only the first 500 branches are watched.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_304_without_a_complete_previous_result_refetches_unconditionally()
    {
        var failSecondPageOnce = true;
        var api = new FakeGitHubApi().Map(HttpMethod.Get, "/repos/acme/widgets/branches", (request, _) =>
        {
            var page = request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal) ? 2 : 1;
            if (page == 2)
            {
                if (failSecondPageOnce)
                {
                    failSecondPageOnce = false;
                    return Json(HttpStatusCode.InternalServerError, """{"message":"boom"}""");
                }

                return Json(HttpStatusCode.OK, """[{"name":"b2","commit":{"sha":"sha2"}}]""");
            }

            if (request.Headers.IfNoneMatch.Count > 0)
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            return Json(HttpStatusCode.OK, """[{"name":"b1","commit":{"sha":"sha1"}}]""",
                ("ETag", "\"e1\""), ("Link", Next("https://api.github.com/repos/acme/widgets/branches?per_page=100&page=2")));
        });
        var client = Create(api);

        await Assert.ThrowsAsync<GitHubApiException>(() => client.GetBranchHeadsAsync(Ct));
        var heads = await client.GetBranchHeadsAsync(Ct);

        Assert.Equal(new Dictionary<string, string> { ["b1"] = "sha1", ["b2"] = "sha2" }, heads);
        var third = await client.GetBranchHeadsAsync(Ct);
        Assert.Equal(heads, third);
        Assert.Equal(6, api.Requests.Count); // failed run: 2; retry: 304, unconditional page 1, page 2; third call: 304 only
    }

    [Fact]
    public async Task Branches_of_a_missing_repository_throw_not_found()
    {
        await Assert.ThrowsAsync<GitHubNotFoundException>(() => Create(new FakeGitHubApi()).GetBranchHeadsAsync(Ct));
    }

    private static FakeGitHubApi PagedBranches(int pages) =>
        new FakeGitHubApi().Map(HttpMethod.Get, "/repos/acme/widgets/branches", (request, _) =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var page = int.TryParse(query["page"], out var parsed) ? parsed : 1;
            var json = $$$"""[{"name":"b{{{page}}}","commit":{"sha":"sha{{{page}}}"}}]""";
            return page < pages
                ? Json(HttpStatusCode.OK, json, ("ETag", $"\"e{page}\""),
                    ("Link", Next($"https://api.github.com/repos/acme/widgets/branches?per_page=100&page={page + 1}")))
                : Json(HttpStatusCode.OK, json);
        });

    // ---- commits --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Listed_commits_map_every_field()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets/commits", 200,
            "[" + CommitJson("c2", "c1", "second") + "," + CommitJson("c1", "c0", "first") + "]");

        var commits = await Create(api).ListCommitsAsync("feature/x", 20, Ct);

        Assert.Equal(2, commits.Count);
        var commit = commits[0];
        Assert.Equal("c2", commit.Sha);
        Assert.Equal("Ada", commit.AuthorName);
        Assert.Equal("ada@example.com", commit.AuthorEmail);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 13, 0, 0, TimeSpan.Zero), commit.CommittedAt);
        Assert.Equal("second", commit.Message);
        Assert.Equal(["c1"], commit.ParentShas);
        Assert.Equal("c1", commits[1].Sha);
        var uri = Assert.Single(api.RequestUris);
        Assert.Equal("per_page=20&sha=feature/x", Uri.UnescapeDataString(uri.Query.TrimStart('?')));
    }

    [Theory]
    [InlineData(500, 100)]
    [InlineData(100, 100)]
    [InlineData(7, 7)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public async Task The_page_size_is_clamped_to_one_through_one_hundred(int limit, int perPage)
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets/commits", 200, "[]");

        await Create(api).ListCommitsAsync(null, limit, Ct);

        var uri = Assert.Single(api.RequestUris);
        Assert.Equal($"?per_page={perPage}", uri.Query);
    }

    [Fact]
    public async Task Listing_the_commits_of_an_empty_repository_yields_nothing()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets/commits", 409, """{"message":"Git Repository is empty."}""");

        Assert.Empty(await Create(api).ListCommitsAsync(null, 30, Ct));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(422)]
    public async Task An_unknown_sha_when_listing_commits_is_a_revision_error(int status)
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets/commits", status, """{"message":"No commit found for SHA: abc"}""");

        var exception = await Assert.ThrowsAsync<GitHubRevisionNotFoundException>(() => Create(api).ListCommitsAsync("abc", 30, Ct));

        Assert.Equal("abc", exception.Revision);
        Assert.Contains("acme/widgets", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_commits_of_a_missing_repository_without_a_sha_is_a_repository_error()
    {
        await Assert.ThrowsAsync<GitHubNotFoundException>(() => Create(new FakeGitHubApi()).ListCommitsAsync(null, 30, Ct));
    }

    [Fact]
    public async Task Commit_detail_concatenates_the_file_pages_and_takes_the_commit_from_the_first()
    {
        var api = new FakeGitHubApi().Map(HttpMethod.Get, "/repos/acme/widgets/commits/abc123", (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, CommitJson("abc123", message: "page two").TrimEnd('}')
                    + ",\"files\":[{\"filename\":\"b.cs\",\"status\":\"removed\",\"additions\":0,\"deletions\":9}]}");
            }

            return Json(
                HttpStatusCode.OK,
                CommitJson("abc123", "p1", "page one").TrimEnd('}')
                + ",\"files\":[{\"filename\":\"new/a.cs\",\"previous_filename\":\"old/a.cs\",\"status\":\"renamed\",\"additions\":2,\"deletions\":1,\"patch\":\"@@ -1 +1 @@\"}]}",
                ("Link", Next("https://api.github.com/repos/acme/widgets/commits/abc123?page=2")));
        });

        var detail = await Create(api).GetCommitAsync("abc123", Ct);

        Assert.Equal("page one", detail.Commit.Message);
        Assert.Equal(["p1"], detail.Commit.ParentShas);
        Assert.Equal(
            [
                new GitHubFileChange("new/a.cs", "old/a.cs", "renamed", 2, 1, "@@ -1 +1 @@"),
                new GitHubFileChange("b.cs", null, "removed", 0, 9, null),
            ],
            detail.Files);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(422)]
    public async Task An_unknown_commit_is_a_revision_error(int status)
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets/commits/deadbeef", status, """{"message":"No commit found"}""");

        var exception = await Assert.ThrowsAsync<GitHubRevisionNotFoundException>(() => Create(api).GetCommitAsync("deadbeef", Ct));

        Assert.Equal("deadbeef", exception.Revision);
    }

    // ---- compare --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Compare_escapes_each_side_and_parses_the_result()
    {
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets/compare/main...feature/x", 200, $$$"""
            {"status":"diverged","ahead_by":2,"behind_by":1,"base_commit":{"sha":"mb"},
             "commits":[{{{CommitJson("c1")}}},{{{CommitJson("c2", "c1")}}}],
             "files":[{"filename":"a.cs","status":"added","additions":3,"deletions":0,"patch":"+x"}]}
            """);

        var comparison = await Create(api).CompareAsync("main", "feature/x", Ct);

        Assert.Equal("/repos/acme/widgets/compare/main...feature%2Fx", Assert.Single(api.Requests).PathAndQuery);
        Assert.Equal("diverged", comparison.Status);
        Assert.Equal(2, comparison.AheadBy);
        Assert.Equal(1, comparison.BehindBy);
        Assert.Equal("mb", comparison.BaseSha);
        Assert.Equal(["c1", "c2"], comparison.Commits.Select(c => c.Sha));
        Assert.Equal([new GitHubFileChange("a.cs", null, "added", 3, 0, "+x")], comparison.Files);
    }

    [Fact]
    public async Task Comparing_an_unknown_revision_names_both_sides()
    {
        var exception = await Assert.ThrowsAsync<GitHubRevisionNotFoundException>(
            () => Create(new FakeGitHubApi()).CompareAsync("gone", "main", Ct));

        Assert.Equal("gone...main", exception.Revision);
    }

    [Fact]
    public async Task The_fake_repository_drives_commit_detail_and_compare_end_to_end()
    {
        var api = new FakeGitHubApi();
        _ = new FakeGitHubRepository(api)
            .Commit("A", "first", [], new GitHubFileChange("a.txt", null, "added", 1, 0, "+a"))
            .Commit("B", "second", ["A"], new GitHubFileChange("b.txt", null, "added", 1, 0, null))
            .Commit("C", "third", ["A"])
            .SetBranch("main", "B")
            .SetBranch("topic", "C");
        var client = Create(api);

        var detail = await client.GetCommitAsync("B", Ct);
        var comparison = await client.CompareAsync("main", "topic", Ct);
        var listed = await client.ListCommitsAsync("main", 10, Ct);

        Assert.Equal(["A"], detail.Commit.ParentShas);
        Assert.Equal("b.txt", Assert.Single(detail.Files).Path);
        Assert.Equal("diverged", comparison.Status);
        Assert.Equal("A", comparison.BaseSha);
        Assert.Equal(["B", "A"], listed.Select(c => c.Sha));
    }

    // ---- contents -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task File_contents_are_requested_raw_and_returned_as_bytes()
    {
        byte[] bytes = [0xFF, 0x00, 0x41, 0xC3];
        var api = new FakeGitHubApi().Map(HttpMethod.Get, "/repos/acme/widgets/contents/src/app.cs", (_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

        var content = await Create(api).GetFileAsync("src/app.cs", "main", Ct);

        Assert.Equal(bytes, content);
        var request = Assert.Single(api.Requests);
        Assert.Equal(Raw, request.Headers["Accept"]);
        Assert.Equal("/repos/acme/widgets/contents/src/app.cs?ref=main", request.PathAndQuery);
    }

    [Fact]
    public async Task A_missing_file_is_null()
    {
        Assert.Null(await Create(new FakeGitHubApi()).GetFileAsync("nope.txt", "main", Ct));
    }

    [Fact]
    public async Task File_paths_and_refs_are_escaped_per_segment()
    {
        var api = new FakeGitHubApi().Map(HttpMethod.Get, "/repos/acme/widgets/contents/docs/naïve file.md", (_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("ok"u8.ToArray()) });

        var content = await Create(api).GetFileAsync("docs/naïve file.md", "feature/x", Ct);

        Assert.Equal("ok"u8.ToArray(), content);
        Assert.Equal("/repos/acme/widgets/contents/docs/na%C3%AFve%20file.md?ref=feature/x", Assert.Single(api.Requests).PathAndQuery);
    }

    [Fact]
    public async Task The_fake_repository_serves_files_through_the_api()
    {
        var api = new FakeGitHubApi();
        _ = new FakeGitHubRepository(api).File("README.md", "# hi");

        Assert.Equal("# hi"u8.ToArray(), await Create(api).GetFileAsync("README.md", "main", Ct));
    }

    // ---- availability ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_disabled_integration_throws_unavailable_from_every_read()
    {
        var api = new FakeGitHubApi();
        var client = Create(api, enabled: false);
        Func<Task>[] calls =
        [
            () => client.GetRepositoryAsync(Ct),
            () => client.GetBranchHeadsAsync(Ct),
            () => client.ListCommitsAsync(null, 10, Ct),
            () => client.GetCommitAsync("abc", Ct),
            () => client.CompareAsync("a", "b", Ct),
            () => client.GetFileAsync("a.txt", "main", Ct),
        ];

        foreach (var call in calls)
        {
            var exception = await Assert.ThrowsAsync<GitHubUnavailableException>(call);
            Assert.Equal("GitHub integration is disabled (Guardian:GitHub:Enabled=false).", exception.Message);
        }

        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Without_a_github_repository_the_read_is_unavailable()
    {
        var exception = await Assert.ThrowsAsync<GitHubUnavailableException>(
            () => Create(new FakeGitHubApi(), noRepository: true).GetRepositoryAsync(Ct));

        Assert.Equal(
            "The origin remote is not a github.com repository; set Guardian:GitHub:Owner and Guardian:GitHub:Repository.",
            exception.Message);
    }
}
