using System.Net;
using System.Text.Json.Nodes;
using CodebaseGuardian.GitHub;
using CodebaseGuardian.Tests.Infrastructure;
using CodebaseGuardian.Watching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CodebaseGuardian.Tests.App;

public sealed class GitHubEventPollerTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(T0);
    private readonly FakeGitHubApi _api = new();
    private TempGitRepo _repo = null!;
    private InProcessMcpServer _server = null!;

    private string _issues = "[]";
    private string _issueComments = "[]";
    private string _reviewComments = "[]";
    private string _runs = """{"workflow_runs":[]}""";
    private Func<HttpResponseMessage>? _issuesOverride;

    public async ValueTask InitializeAsync()
    {
        _api.Map(HttpMethod.Get, "/repos/acme/widgets/issues", (_, _) => _issuesOverride?.Invoke() ?? Json(_issues))
            .Map(HttpMethod.Get, "/repos/acme/widgets/issues/comments", (_, _) => Json(_issueComments))
            .Map(HttpMethod.Get, "/repos/acme/widgets/pulls/comments", (_, _) => Json(_reviewComments))
            .Map(HttpMethod.Get, "/repos/acme/widgets/actions/runs", (request, _) => Json(RunsCreatedSince(request)));
        _repo = TempGitRepo.Create();
        _server = await GuardianTestHost.StartAsync(
            _repo.Path,
            new Dictionary<string, string?> { ["Guardian:GitHub:Enabled"] = "true", ["Guardian:GitHub:PollEnabled"] = "false" },
            services =>
            {
                _api.Install(services);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_time);
            },
            cancellationToken: Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _repo.Dispose();
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private static string IssueJson(long id, int number, DateTimeOffset createdAt, string body = "It crashes", bool pull = false) =>
        $$"""
        {"id":{{id}},"number":{{number}},"title":"Crash","body":"{{body}}","html_url":"https://github.com/acme/widgets/{{(pull ? "pull" : "issues")}}/{{number}}",
         "user":{"login":"octo"},"labels":[{"name":"bug"},{"name":"p1"}],"created_at":"{{Stamp(createdAt)}}"{{(pull ? ",\"pull_request\":{\"url\":\"x\"}" : "")}}}
        """;

    private static string IssueCommentJson(long id, string kind, int number, DateTimeOffset createdAt, string body = "Please rename") =>
        $$"""
        {"id":{{id}},"body":"{{body}}","html_url":"https://github.com/acme/widgets/{{kind}}/{{number}}#issuecomment-{{id}}",
         "issue_url":"https://api.github.com/repos/acme/widgets/issues/{{number}}","user":{"login":"rev"},"created_at":"{{Stamp(createdAt)}}"}
        """;

    private static string ReviewCommentJson(long id, int number, DateTimeOffset createdAt) =>
        $$"""
        {"id":{{id}},"body":"nit","html_url":"https://github.com/acme/widgets/pull/{{number}}#discussion_r{{id}}",
         "pull_request_url":"https://api.github.com/repos/acme/widgets/pulls/{{number}}","user":{"login":"rev"},
         "created_at":"{{Stamp(createdAt)}}","path":"src/A.cs","line":42}
        """;

    // Like GitHub, honours the "created=>=<timestamp>" query by each run's created_at.
    private string RunsCreatedSince(HttpRequestMessage request)
    {
        var query = Uri.UnescapeDataString(request.RequestUri!.Query);
        var match = System.Text.RegularExpressions.Regex.Match(query, @"created=>=(?<ts>[^&]+)");
        var from = match.Success ? DateTimeOffset.Parse(match.Groups["ts"].Value, System.Globalization.CultureInfo.InvariantCulture) : DateTimeOffset.MinValue;
        var runs = JsonNode.Parse(_runs)!["workflow_runs"]!.AsArray()
            .Where(run => DateTimeOffset.Parse((string)run!["created_at"]!, System.Globalization.CultureInfo.InvariantCulture) >= from)
            .Select(run => run!.DeepClone());
        return new JsonObject { ["workflow_runs"] = new JsonArray([.. runs]) }.ToJsonString();
    }

    private static string RunJson(
        long id, int attempt, DateTimeOffset updatedAt, string branch = "main", string conclusion = "failure", DateTimeOffset? createdAt = null) =>
        $$"""
        {"id":{{id}},"run_attempt":{{attempt}},"name":"CI","head_branch":"{{branch}}","head_sha":"abc123",
         "html_url":"https://github.com/acme/widgets/actions/runs/{{id}}","conclusion":"{{conclusion}}","created_at":"{{Stamp(createdAt ?? updatedAt)}}","updated_at":"{{Stamp(updatedAt)}}"}
        """;

    private GitHubEventPoller Poller => _server.Services.GetRequiredService<GitHubEventPoller>();

    private Task CycleAsync() => Poller.PollOnceAsync(Ct);

    private async Task<(string Cursor, Func<string, JsonObject?, Task<IReadOnlyList<JsonObject>>> Poll)> WatchAsync()
    {
        var cursor = await EventsPolling.GetCursorAsync(_server, Ct);
        return (cursor, async (name, arguments) => (await EventsPolling.PollAsync(_server, name, cursor, Ct, arguments)).Events);
    }

    [Fact]
    public async Task Nothing_created_before_the_first_cycle_is_published()
    {
        var (_, poll) = await WatchAsync();
        _issues = "[" + IssueJson(1, 5, T0.AddHours(-1)) + "]";
        _issueComments = "[" + IssueCommentJson(2, "pull", 7, T0.AddHours(-1)) + "]";
        _reviewComments = "[" + ReviewCommentJson(3, 7, T0.AddHours(-1)) + "]";
        _runs = $$"""{"workflow_runs":[{{RunJson(4, 1, T0.AddHours(-1))}}]}""";

        await CycleAsync();

        Assert.Empty(await poll(GuardianEventNames.GithubIssueOpened, null));
        Assert.Empty(await poll(GuardianEventNames.GithubPrCommentCreated, null));
        Assert.Empty(await poll(GuardianEventNames.GithubCiFailed, null));
    }

    [Fact]
    public async Task A_new_issue_is_published_once_even_when_later_cycles_see_it_again()
    {
        var (_, poll) = await WatchAsync();
        await CycleAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        _issues = "[" + IssueJson(41, 5, T0.AddMinutes(1)) + "]";

        await CycleAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        await CycleAsync();

        var published = Assert.Single(await poll(GuardianEventNames.GithubIssueOpened, null));
        Assert.Equal("gh_issue_41", (string)published["eventId"]!);
        var data = published["data"]!.AsObject();
        Assert.Equal(5, (int)data["number"]!);
        Assert.Equal("octo", (string)data["author"]!);
        Assert.Equal("https://github.com/acme/widgets/issues/5", (string)data["url"]!);
        Assert.Equal(["bug", "p1"], data["labels"]!.AsArray().Select(l => (string)l!).ToArray());
        Assert.Equal("It crashes", (string)data["body"]!);
        Assert.Equal("skill://bug-triage/SKILL.md", (string)data["suggestedSkill"]!);
        Assert.Equal("2026-10-05T12:01:00.000Z", (string)published["timestamp"]!);
    }

    [Fact]
    public async Task Pull_requests_in_the_issue_list_are_not_issue_events()
    {
        var (_, poll) = await WatchAsync();
        await CycleAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        _issues = "[" + IssueJson(42, 8, T0.AddMinutes(1), pull: true) + "]";

        await CycleAsync();

        Assert.Empty(await poll(GuardianEventNames.GithubIssueOpened, null));
    }

    [Fact]
    public async Task Pull_request_comments_are_published_and_plain_issue_comments_are_not()
    {
        var (_, poll) = await WatchAsync();
        await CycleAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        var created = T0.AddMinutes(1);
        _issueComments = "[" + IssueCommentJson(10, "pull", 7, created) + "," + IssueCommentJson(11, "issues", 3, created) + "]";
        _reviewComments = "[" + ReviewCommentJson(20, 7, created) + "]";

        await CycleAsync();

        var events = (await poll(GuardianEventNames.GithubPrCommentCreated, null)).OrderBy(e => (string)e["eventId"]!).ToList();
        Assert.Equal(["gh_comment_10", "gh_reviewcomment_20"], events.Select(e => (string)e["eventId"]!).ToArray());
        var issueComment = events[0]["data"]!.AsObject();
        Assert.Equal(7, (int)issueComment["prNumber"]!);
        Assert.Equal(10, (long)issueComment["commentId"]!);
        Assert.Equal("rev", (string)issueComment["author"]!);
        Assert.Null(issueComment["path"]);
        Assert.True(issueComment.ContainsKey("path") && issueComment.ContainsKey("line"));
        Assert.Null(issueComment["line"]);
        Assert.Equal("skill://pr-review/SKILL.md", (string)issueComment["suggestedSkill"]!);
        var review = events[1]["data"]!.AsObject();
        Assert.Equal(7, (int)review["prNumber"]!);
        Assert.Equal("src/A.cs", (string)review["path"]!);
        Assert.Equal(42, (int)review["line"]!);
    }

    [Fact]
    public async Task Each_failed_run_attempt_is_its_own_event_and_other_conclusions_are_ignored()
    {
        var (_, poll) = await WatchAsync();
        await CycleAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        _runs = $$"""{"workflow_runs":[{{RunJson(77, 1, T0.AddMinutes(1))}},{{RunJson(78, 1, T0.AddMinutes(1), conclusion: "cancelled")}}]}""";
        await CycleAsync();
        _time.Advance(TimeSpan.FromMinutes(2));
        _runs = $$"""{"workflow_runs":[{{RunJson(77, 2, T0.AddMinutes(3))}}]}""";
        await CycleAsync();

        var events = await poll(GuardianEventNames.GithubCiFailed, null);

        Assert.Equal(["gh_run_77_1", "gh_run_77_2"], events.Select(e => (string)e["eventId"]!).ToArray());
        var data = events[0]["data"]!.AsObject();
        Assert.Equal(77, (long)data["runId"]!);
        Assert.Equal("CI", (string)data["workflowName"]!);
        Assert.Equal("main", (string)data["branch"]!);
        Assert.Equal("abc123", (string)data["headSha"]!);
        Assert.Equal("https://github.com/acme/widgets/actions/runs/77", (string)data["url"]!);
        Assert.Equal("failure", (string)data["conclusion"]!);
        Assert.Equal("skill://bug-triage/SKILL.md", (string)data["suggestedSkill"]!);
    }

    [Fact]
    public async Task A_rerun_of_a_run_created_before_the_query_window_is_still_published()
    {
        var (_, poll) = await WatchAsync();
        await CycleAsync();
        _time.Advance(TimeSpan.FromHours(1));
        await CycleAsync();
        _time.Advance(TimeSpan.FromHours(1));
        // Created before the poller started, re-run (attempt 2) after: its created_at is older than the cycle's overlap window.
        _runs = $$"""{"workflow_runs":[{{RunJson(77, 2, T0.AddHours(2), createdAt: T0.AddHours(-2))}}]}""";

        await CycleAsync();

        var published = Assert.Single(await poll(GuardianEventNames.GithubCiFailed, null));
        Assert.Equal("gh_run_77_2", (string)published["eventId"]!);
    }

    [Fact]
    public async Task Secrets_in_relayed_text_are_redacted_and_long_bodies_are_cut()
    {
        var (_, poll) = await WatchAsync();
        await CycleAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        var token = FakeSecrets.GitHubToken();
        _issues = "[" + IssueJson(50, 9, T0.AddMinutes(1), body: $"token {token} " + new string('x', 3000)) + "]";

        await CycleAsync();

        var body = (string)Assert.Single(await poll(GuardianEventNames.GithubIssueOpened, null))["data"]!["body"]!;
        Assert.StartsWith("token " + CodebaseGuardian.Security.Redactor.Redact(token) + " ", body);
        Assert.DoesNotContain(token, body);
        Assert.Equal(2000, body.Length);
        Assert.EndsWith("…", body);
    }

    [Fact]
    public async Task A_rate_limit_pauses_requests_until_the_reset_time()
    {
        await CycleAsync();
        var reset = T0.AddMinutes(10);
        _issuesOverride = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"rate limited"}""") };
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
            response.Headers.TryAddWithoutValidation("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString());
            return response;
        };
        _time.Advance(TimeSpan.FromMinutes(1));
        await CycleAsync();
        var before = _api.Requests.Count;

        _time.Advance(TimeSpan.FromMinutes(1));
        await CycleAsync();
        Assert.Equal(before, _api.Requests.Count);

        _issuesOverride = null;
        _time.Advance(TimeSpan.FromMinutes(9));
        await CycleAsync();
        Assert.True(_api.Requests.Count > before);
    }

    [Fact]
    public async Task An_unavailable_github_is_idle_and_does_not_throw()
    {
        using var repo = TempGitRepo.Create();
        await using var server = await GuardianTestHost.StartAsync(
            repo.Path,
            new Dictionary<string, string?> { ["Guardian:GitHub:Enabled"] = "true", ["Guardian:GitHub:PollEnabled"] = "false" },
            services => services.RemoveAll<IGitHubTokenProvider>().AddSingleton<IGitHubTokenProvider, NoToken>(),
            cancellationToken: Ct);
        var poller = server.Services.GetRequiredService<GitHubEventPoller>();

        var cursor = await EventsPolling.GetCursorAsync(server, Ct);

        await poller.PollOnceAsync(Ct);
        await poller.PollOnceAsync(Ct);

        foreach (var name in new[] { GuardianEventNames.GithubIssueOpened, GuardianEventNames.GithubPrCommentCreated, GuardianEventNames.GithubCiFailed })
        {
            Assert.Empty((await EventsPolling.PollAsync(server, name, cursor, Ct)).Events);
        }
    }

    [Fact]
    public async Task Another_failure_is_swallowed_and_the_next_cycle_still_works()
    {
        var (_, poll) = await WatchAsync();
        await CycleAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        _issuesOverride = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await CycleAsync();

        _issuesOverride = null;
        _issues = "[" + IssueJson(60, 11, T0.AddMinutes(1)) + "]";
        _time.Advance(TimeSpan.FromMinutes(1));
        await CycleAsync();

        Assert.Single(await poll(GuardianEventNames.GithubIssueOpened, null));
    }

    [Fact]
    public async Task Polling_arguments_filter_by_label_pull_request_number_and_branch()
    {
        var (_, poll) = await WatchAsync();
        await CycleAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        var created = T0.AddMinutes(1);
        _issues = "[" + IssueJson(70, 12, created) + "]";
        _issueComments = "[" + IssueCommentJson(71, "pull", 7, created) + "]";
        _runs = $$"""{"workflow_runs":[{{RunJson(72, 1, created)}}]}""";
        await CycleAsync();

        Assert.Single(await poll(GuardianEventNames.GithubIssueOpened, new JsonObject { ["label"] = "bug" }));
        Assert.Empty(await poll(GuardianEventNames.GithubIssueOpened, new JsonObject { ["label"] = "question" }));
        Assert.Single(await poll(GuardianEventNames.GithubPrCommentCreated, new JsonObject { ["prNumber"] = 7 }));
        Assert.Empty(await poll(GuardianEventNames.GithubPrCommentCreated, new JsonObject { ["prNumber"] = 8 }));
        Assert.Single(await poll(GuardianEventNames.GithubCiFailed, new JsonObject { ["branch"] = "main" }));
        Assert.Empty(await poll(GuardianEventNames.GithubCiFailed, new JsonObject { ["branch"] = "dev" }));
    }

    private sealed class NoToken : IGitHubTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
    }
}
