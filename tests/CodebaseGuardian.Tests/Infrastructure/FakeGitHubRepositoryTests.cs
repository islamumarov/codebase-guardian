using System.Net;
using System.Text.Json;
using CodebaseGuardian.GitHub;

namespace CodebaseGuardian.Tests.Infrastructure;

public class FakeGitHubRepositoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GitHubFileChange Change(string path, string status = "modified") => new(path, null, status, 1, 0, "@@ -0,0 +1 @@\n+x");

    private static (FakeGitHubApi Api, FakeGitHubRepository Repository, HttpClient Http) Create()
    {
        var api = new FakeGitHubApi();
        return (api, new FakeGitHubRepository(api), new HttpClient(api) { BaseAddress = new Uri("https://api.github.com/") });
    }

    private static FakeGitHubRepository Linear(FakeGitHubRepository repository) => repository
        .Commit("A", "first", [], Change("a.txt", "added"))
        .Commit("B", "second", ["A"], Change("b.txt", "added"))
        .Commit("C", "third", ["B"], Change("a.txt"))
        .SetBranch("main", "C");

    private static async Task<(HttpStatusCode Status, JsonElement Json, HttpResponseMessage Response)> GetAsync(HttpClient http, string url)
    {
        var response = await http.GetAsync(url, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var json = string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (response.StatusCode, json, response);
    }

    [Fact]
    public async Task Commits_of_a_branch_are_listed_newest_first()
    {
        var (_, repository, http) = Create();
        Linear(repository);

        var (status, json, _) = await GetAsync(http, "repos/acme/widgets/commits?sha=main&per_page=100");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["C", "B", "A"], json.EnumerateArray().Select(c => c.GetProperty("sha").GetString()));
        var first = json[0];
        Assert.Equal("third", first.GetProperty("commit").GetProperty("message").GetString());
        Assert.Equal("B", first.GetProperty("parents")[0].GetProperty("sha").GetString());
    }

    [Fact]
    public async Task Commits_default_to_the_default_branch_and_honour_per_page()
    {
        var (_, repository, http) = Create();
        Linear(repository);

        var (_, json, _) = await GetAsync(http, "repos/acme/widgets/commits?per_page=2");

        Assert.Equal(["C", "B"], json.EnumerateArray().Select(c => c.GetProperty("sha").GetString()));
    }

    [Fact]
    public async Task Compare_of_a_linear_range_is_ahead_with_the_commits_oldest_first()
    {
        var (_, repository, http) = Create();
        Linear(repository);

        var (status, json, _) = await GetAsync(http, "repos/acme/widgets/compare/A...C");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ahead", json.GetProperty("status").GetString());
        Assert.Equal(2, json.GetProperty("ahead_by").GetInt32());
        Assert.Equal(0, json.GetProperty("behind_by").GetInt32());
        Assert.Equal("A", json.GetProperty("base_commit").GetProperty("sha").GetString());
        Assert.Equal(["B", "C"], json.GetProperty("commits").EnumerateArray().Select(c => c.GetProperty("sha").GetString()));
        Assert.Equal(["a.txt", "b.txt"], json.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("filename").GetString()).Order());
    }

    [Fact]
    public async Task Compare_of_identical_and_behind_ranges()
    {
        var (_, repository, http) = Create();
        Linear(repository);

        var (_, identical, _) = await GetAsync(http, "repos/acme/widgets/compare/main...C");
        var (_, behind, _) = await GetAsync(http, "repos/acme/widgets/compare/C...A");

        Assert.Equal("identical", identical.GetProperty("status").GetString());
        Assert.Equal("behind", behind.GetProperty("status").GetString());
        Assert.Equal(2, behind.GetProperty("behind_by").GetInt32());
        Assert.Equal(0, behind.GetProperty("commits").GetArrayLength());
    }

    [Fact]
    public async Task A_feature_branch_from_the_middle_diverges_from_main()
    {
        var (_, repository, http) = Create();
        Linear(repository)
            .Commit("D", "feature work", ["B"], Change("d.txt", "added"))
            .SetBranch("feature/x", "D");

        var (status, json, _) = await GetAsync(http, "repos/acme/widgets/compare/main...feature%2Fx");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("diverged", json.GetProperty("status").GetString());
        Assert.Equal(1, json.GetProperty("ahead_by").GetInt32());
        Assert.Equal(1, json.GetProperty("behind_by").GetInt32());
        Assert.Equal("B", json.GetProperty("base_commit").GetProperty("sha").GetString());
        Assert.Equal(["D"], json.GetProperty("commits").EnumerateArray().Select(c => c.GetProperty("sha").GetString()));
        Assert.Equal(["d.txt"], json.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("filename").GetString()));
    }

    [Fact]
    public async Task A_forgotten_commit_answers_404_everywhere()
    {
        var (_, repository, http) = Create();
        Linear(repository)
            .Commit("D", "rewritten", ["B"], Change("d.txt", "added"))
            .SetBranch("main", "D")
            .Forget("C");

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(http, "repos/acme/widgets/compare/C...main")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(http, "repos/acme/widgets/commits/C")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(http, "repos/acme/widgets/commits?sha=C")).Status);
    }

    [Fact]
    public async Task Commit_detail_carries_the_files()
    {
        var (_, repository, http) = Create();
        Linear(repository);

        var (status, json, _) = await GetAsync(http, "repos/acme/widgets/commits/B");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("B", json.GetProperty("sha").GetString());
        var file = Assert.Single(json.GetProperty("files").EnumerateArray());
        Assert.Equal("b.txt", file.GetProperty("filename").GetString());
        Assert.Equal("added", file.GetProperty("status").GetString());
        Assert.Equal(1, file.GetProperty("additions").GetInt32());
    }

    [Fact]
    public async Task An_empty_repository_answers_409_for_commits_compare_and_branches()
    {
        var (_, _, http) = Create();

        Assert.Equal(HttpStatusCode.Conflict, (await GetAsync(http, "repos/acme/widgets/commits")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await GetAsync(http, "repos/acme/widgets/compare/main...main")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await GetAsync(http, "repos/acme/widgets/branches?per_page=100")).Status);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(http, "repos/acme/widgets")).Status);
    }

    [Fact]
    public async Task Branches_are_listed_and_the_etag_changes_on_every_mutation()
    {
        var (_, repository, http) = Create();
        Linear(repository);

        var (_, json, first) = await GetAsync(http, "repos/acme/widgets/branches?per_page=100");
        repository.SetBranch("topic", "B");
        var (_, _, second) = await GetAsync(http, "repos/acme/widgets/branches?per_page=100");
        repository.DeleteBranch("topic");
        var (_, after, third) = await GetAsync(http, "repos/acme/widgets/branches?per_page=100");

        Assert.Equal("main", Assert.Single(json.EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal("C", json[0].GetProperty("commit").GetProperty("sha").GetString());
        Assert.NotEqual(first.Headers.ETag, second.Headers.ETag);
        Assert.NotEqual(second.Headers.ETag, third.Headers.ETag);
        Assert.NotEqual(first.Headers.ETag, third.Headers.ETag);
        Assert.Equal(1, after.GetArrayLength());
        Assert.Equal(3, repository.BranchListRequests);
    }

    [Fact]
    public async Task A_matching_if_none_match_gets_304_and_is_still_counted()
    {
        var (_, repository, http) = Create();
        Linear(repository);
        var (_, _, first) = await GetAsync(http, "repos/acme/widgets/branches?per_page=100");

        using var request = new HttpRequestMessage(HttpMethod.Get, "repos/acme/widgets/branches?per_page=100");
        request.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag!.ToString());
        using var response = await http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal(2, repository.BranchListRequests);
    }

    [Fact]
    public async Task Repository_info_reports_the_default_branch_and_its_etag_follows_it()
    {
        var (_, repository, http) = Create();

        var (_, json, first) = await GetAsync(http, "repos/acme/widgets");
        repository.DefaultBranch = "trunk";
        var (_, changed, second) = await GetAsync(http, "repos/acme/widgets");

        Assert.Equal("main", json.GetProperty("default_branch").GetString());
        Assert.False(json.GetProperty("private").GetBoolean());
        Assert.Equal("trunk", changed.GetProperty("default_branch").GetString());
        Assert.NotEqual(first.Headers.ETag, second.Headers.ETag);
    }

    [Fact]
    public async Task Files_are_served_by_contents_and_missing_ones_answer_404()
    {
        var (_, repository, http) = Create();
        repository.File("docs/readme.md", "hello");

        var found = await http.GetAsync("repos/acme/widgets/contents/docs/readme.md?ref=main", Ct);
        var missing = await http.GetAsync("repos/acme/widgets/contents/docs/other.md?ref=main", Ct);

        Assert.Equal("hello", await found.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public void Adding_a_commit_with_an_unknown_parent_is_rejected()
    {
        var (_, repository, _) = Create();

        Assert.Throws<ArgumentException>(() => repository.Commit("B", "orphan", ["missing"]));
    }

    [Fact]
    public async Task The_fake_can_use_other_names()
    {
        var api = new FakeGitHubApi();
        _ = new FakeGitHubRepository(api, "octo", "gadgets", "trunk");
        using var http = new HttpClient(api) { BaseAddress = new Uri("https://api.github.com/") };

        var (_, json, _) = await GetAsync(http, "repos/octo/gadgets");

        Assert.Equal("trunk", json.GetProperty("default_branch").GetString());
    }
}
