using System.Text.Json;
using CodebaseGuardian.Security;
using CodebaseGuardian.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodebaseGuardian.Tests.App;

public class GitHubToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> Enabled = new() { ["Guardian:GitHub:Enabled"] = "true" };

    private static FakeGitHubApi Api(bool branchExists = true)
    {
        var api = new FakeGitHubApi()
            .MapJson(HttpMethod.Get, "/repos/acme/widgets", 200, """{"default_branch":"main"}""")
            .MapJson(HttpMethod.Post, "/repos/acme/widgets/issues", 201,
                """{"id":1,"number":7,"title":"t","body":"b","html_url":"https://github.com/acme/widgets/issues/7","user":{"login":"bot"},"labels":[],"created_at":"2026-10-04T10:00:00Z"}""")
            .MapJson(HttpMethod.Post, "/repos/acme/widgets/issues/12/comments", 201,
                """{"id":55,"html_url":"https://github.com/acme/widgets/issues/12#issuecomment-55","body":"b","user":{"login":"bot"},"created_at":"2026-10-04T10:00:00Z","issue_url":"https://api.github.com/repos/acme/widgets/issues/12"}""")
            .MapJson(HttpMethod.Post, "/repos/acme/widgets/pulls", 201,
                """{"id":3,"number":9,"title":"t","html_url":"https://github.com/acme/widgets/pull/9","head":{"ref":"feature"},"base":{"ref":"main"},"draft":false}""");
        return branchExists
            ? api.MapJson(HttpMethod.Get, "/repos/acme/widgets/branches/feature", 200, """{"name":"feature"}""")
            : api;
    }

    private static McpClientOptions Elicit(string action, List<string> seen, string? version = null)
    {
        var options = new McpClientOptions
        {
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = (request, _) =>
                {
                    seen.Add(request!.Message);
                    return ValueTask.FromResult(new ElicitResult { Action = action });
                },
            },
        };
        if (version is not null) { options.ProtocolVersion = version; }
        return options;
    }

    private static Task<InProcessMcpServer> StartAsync(TempGitRepo repo, FakeGitHubApi api, McpClientOptions? client = null) =>
        GuardianTestHost.StartAsync(repo.Path, Enabled, api.Install, client, Ct);

    private static TempGitRepo Repo()
    {
        var repo = TempGitRepo.Create();
        repo.WriteFile("README.md", "x");
        repo.Commit("initial");
        return repo;
    }

    private static string TextOf(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static Dictionary<string, object?> Issue(bool? confirm = null, string body = "Details")
    {
        var args = new Dictionary<string, object?> { ["title"] = "Crash on start", ["body"] = body, ["labels"] = new[] { "bug", "p1" } };
        if (confirm is not null) { args["confirm"] = confirm; }
        return args;
    }

    private static int Posts(FakeGitHubApi api) => api.Requests.Count(r => r.Method == HttpMethod.Post);

    [Fact]
    public async Task Accepted_prompt_creates_the_issue()
    {
        using var repo = Repo();
        var api = Api();
        var seen = new List<string>();
        await using var server = await StartAsync(repo, api, Elicit("accept", seen));

        var result = await server.Client.CallToolAsync("create_issue", Issue(), cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        var json = result.StructuredContent!.Value;
        Assert.Equal("created", json.GetProperty("status").GetString());
        Assert.Equal(7, json.GetProperty("number").GetInt32());
        Assert.Equal("Create issue in acme/widgets: \"Crash on start\" [labels: bug, p1]", Assert.Single(seen));
        var post = Assert.Single(api.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("/repos/acme/widgets/issues", post.PathAndQuery);
        using var body = JsonDocument.Parse(post.Body);
        Assert.Equal("Crash on start", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("Details", body.RootElement.GetProperty("body").GetString());
        Assert.Equal(["bug", "p1"], body.RootElement.GetProperty("labels").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Declined_prompt_sends_nothing()
    {
        using var repo = Repo();
        var api = Api();
        await using var server = await StartAsync(repo, api, Elicit("decline", []));

        var result = await server.Client.CallToolAsync("create_issue", Issue(), cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("declined", result.StructuredContent!.Value.GetProperty("status").GetString());
        Assert.Equal("The user declined; nothing was sent to GitHub.", TextOf(result));
        Assert.Equal("""{"status":"declined"}""", result.StructuredContent!.Value.GetRawText());
        Assert.Equal(0, Posts(api));
    }

    [Fact]
    public async Task Confirm_argument_does_not_bypass_the_prompt()
    {
        using var repo = Repo();
        var api = Api();
        var seen = new List<string>();
        await using var server = await StartAsync(repo, api, Elicit("decline", seen));

        var result = await server.Client.CallToolAsync("create_issue", Issue(confirm: true), cancellationToken: Ct);

        Assert.Single(seen);
        Assert.Equal("declined", result.StructuredContent!.Value.GetProperty("status").GetString());
        Assert.Equal(0, Posts(api));
    }

    [Fact]
    public async Task Confirm_argument_with_an_accepting_prompt_prompts_once_and_posts_once()
    {
        using var repo = Repo();
        var api = Api();
        var seen = new List<string>();
        await using var server = await StartAsync(repo, api, Elicit("accept", seen));

        var result = await server.Client.CallToolAsync("create_issue", Issue(confirm: true), cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        Assert.Single(seen);
        Assert.Equal(1, Posts(api));
    }

    [Fact]
    public async Task Summaries_are_a_single_line()
    {
        using var repo = Repo();
        var seen = new List<string>();
        await using var server = await StartAsync(repo, Api(), Elicit("accept", seen));

        await server.Client.CallToolAsync("comment_on_pr",
            new Dictionary<string, object?> { ["number"] = 12, ["body"] = "line one\r\n\r\nline  two\n" + new string('x', 200) }, cancellationToken: Ct);

        var summary = Assert.Single(seen);
        Assert.DoesNotContain('\n', summary);
        Assert.DoesNotContain('\r', summary);
        Assert.StartsWith("Comment on acme/widgets#12: \"line one line two xxx", summary);
    }

    [Fact]
    public async Task Without_elicitation_the_confirm_argument_is_required()
    {
        using var repo = Repo();
        var api = Api();
        await using var server = await StartAsync(repo, api);

        var refused = await server.Client.CallToolAsync("create_issue", Issue(), cancellationToken: Ct);
        Assert.True(refused.IsError);
        Assert.Contains("confirm: true", TextOf(refused));
        Assert.Equal(0, Posts(api));

        var created = await server.Client.CallToolAsync("create_issue", Issue(confirm: true), cancellationToken: Ct);
        Assert.NotEqual(true, created.IsError);
        Assert.Equal(1, Posts(api));
    }

    [Fact]
    public async Task A_secret_in_the_body_is_refused_before_any_prompt()
    {
        using var repo = Repo();
        var api = Api();
        var seen = new List<string>();
        await using var server = await StartAsync(repo, api, Elicit("accept", seen));

        var result = await server.Client.CallToolAsync("create_issue", Issue(body: $"token {FakeSecrets.GitHubToken()}"), cancellationToken: Ct);

        Assert.True(result.IsError);
        var text = TextOf(result);
        Assert.Contains($"body contains what looks like a secret (github-token {Redactor.Redact(FakeSecrets.GitHubToken())} on line 1)", text);
        Assert.DoesNotContain(FakeSecrets.GitHubToken(), text);
        Assert.Empty(seen);
        Assert.Equal(0, Posts(api));
    }

    [Fact]
    public async Task Invalid_arguments_are_tool_errors()
    {
        using var repo = Repo();
        var api = Api();
        var seen = new List<string>();
        await using var server = await StartAsync(repo, api, Elicit("accept", seen));

        var empty = await server.Client.CallToolAsync("create_issue",
            new Dictionary<string, object?> { ["title"] = "", ["body"] = "b" }, cancellationToken: Ct);
        var labels = await server.Client.CallToolAsync("create_issue",
            new Dictionary<string, object?> { ["title"] = "t", ["body"] = "b", ["labels"] = Enumerable.Range(0, 11).Select(i => $"l{i}").ToArray() }, cancellationToken: Ct);

        Assert.True(empty.IsError);
        Assert.Contains("The title must be 1 to 256 characters.", TextOf(empty));
        Assert.True(labels.IsError);
        Assert.Contains("At most 10 labels are allowed.", TextOf(labels));
        Assert.Empty(seen);
        Assert.Equal(0, Posts(api));
    }

    [Fact]
    public async Task Open_pull_request_requires_a_pushed_branch()
    {
        using var repo = Repo();
        var api = Api();
        await using var server = await StartAsync(repo, api, Elicit("accept", []));

        var result = await server.Client.CallToolAsync("open_pull_request",
            new Dictionary<string, object?> { ["head"] = "missing", ["title"] = "t", ["body"] = "b" }, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("Branch 'missing' is not on GitHub; push it first.", TextOf(result));
        Assert.Equal(0, Posts(api));
    }

    [Fact]
    public async Task Open_pull_request_defaults_the_base_to_the_default_branch()
    {
        using var repo = Repo();
        var api = Api();
        var seen = new List<string>();
        await using var server = await StartAsync(repo, api, Elicit("accept", seen));

        var result = await server.Client.CallToolAsync("open_pull_request",
            new Dictionary<string, object?> { ["head"] = "feature", ["title"] = "Add it", ["body"] = "b" }, cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(9, result.StructuredContent!.Value.GetProperty("number").GetInt32());
        var post = Assert.Single(api.Requests, r => r.Method == HttpMethod.Post);
        using var body = JsonDocument.Parse(post.Body);
        Assert.Equal("main", body.RootElement.GetProperty("base").GetString());
        Assert.Equal("feature", body.RootElement.GetProperty("head").GetString());
        Assert.Contains("feature → main", Assert.Single(seen));
    }

    [Fact]
    public async Task Open_pull_request_rejects_same_head_and_base_and_option_like_names()
    {
        using var repo = Repo();
        await using var server = await StartAsync(repo, Api(), Elicit("accept", []));

        var same = await server.Client.CallToolAsync("open_pull_request",
            new Dictionary<string, object?> { ["head"] = "feature", ["base"] = "feature", ["title"] = "t", ["body"] = "b" }, cancellationToken: Ct);
        var option = await server.Client.CallToolAsync("open_pull_request",
            new Dictionary<string, object?> { ["head"] = "--evil", ["title"] = "t", ["body"] = "b" }, cancellationToken: Ct);

        Assert.True(same.IsError);
        Assert.True(option.IsError);
    }

    [Fact]
    public async Task Comment_on_pr_validates_the_number_and_posts_the_comment()
    {
        using var repo = Repo();
        var api = Api();
        await using var server = await StartAsync(repo, api, Elicit("accept", []));

        var bad = await server.Client.CallToolAsync("comment_on_pr",
            new Dictionary<string, object?> { ["number"] = 0, ["body"] = "hi" }, cancellationToken: Ct);
        Assert.True(bad.IsError);
        Assert.Equal(0, Posts(api));

        var ok = await server.Client.CallToolAsync("comment_on_pr",
            new Dictionary<string, object?> { ["number"] = 12, ["body"] = "Looks good" }, cancellationToken: Ct);

        Assert.NotEqual(true, ok.IsError);
        Assert.Equal(55, ok.StructuredContent!.Value.GetProperty("commentId").GetInt64());
        Assert.Equal("/repos/acme/widgets/issues/12/comments", Assert.Single(api.Requests, r => r.Method == HttpMethod.Post).PathAndQuery);
    }

    [Fact]
    public async Task Disabled_github_is_a_tool_error_and_the_tools_are_annotated_open_world()
    {
        using var repo = Repo();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var tools = await server.Client.ListToolsAsync(cancellationToken: Ct);
        foreach (var name in new[] { "create_issue", "comment_on_pr", "open_pull_request" })
        {
            var tool = Assert.Single(tools, t => t.Name == name);
            Assert.True(tool.ProtocolTool.Annotations?.OpenWorldHint);
            Assert.NotEqual(true, tool.ProtocolTool.Annotations?.ReadOnlyHint);
        }

        var result = await server.Client.CallToolAsync("create_issue", Issue(confirm: true), cancellationToken: Ct);
        Assert.True(result.IsError);
        Assert.Contains("disabled", TextOf(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Legacy_protocol_client_is_prompted_and_creates_the_issue()
    {
        using var repo = Repo();
        var api = Api();
        var seen = new List<string>();
        await using var server = await StartAsync(repo, api, Elicit("accept", seen, "2025-11-25"));

        var result = await server.Client.CallToolAsync("create_issue", Issue(), cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        Assert.Single(seen);
        Assert.Equal(1, Posts(api));
    }

    [Fact]
    public async Task Stateless_http_prompts_through_mrtr_and_creates_the_issue()
    {
        using var repo = Repo();
        var api = Api();
        var seen = new List<string>();
        await using var host = await GuardianHttpTestHost.StartAsync(repo.Path, Enabled, api.Install, Ct);
        var client = await host.ConnectAsync(Elicit("accept", seen), Ct);

        var result = await client.CallToolAsync("create_issue", Issue(), cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(7, result.StructuredContent!.Value.GetProperty("number").GetInt32());
        Assert.Single(seen);
        Assert.Equal(1, Posts(api));
    }
}
