using System.Text.Json;
using CodebaseGuardian.GitHub;
using CodebaseGuardian.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodebaseGuardian.Tests.App;

public class ActionConfirmationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Token_changes_when_any_argument_but_confirm_changes()
    {
        var baseline = ActionConfirmation.ConfirmationToken("create_issue", Args("""{"title":"a","body":"b"}"""));

        Assert.NotEqual(baseline, ActionConfirmation.ConfirmationToken("create_issue", Args("""{"title":"a2","body":"b"}""")));
        Assert.NotEqual(baseline, ActionConfirmation.ConfirmationToken("create_issue", Args("""{"title":"a","body":"b","labels":["x"]}""")));
        Assert.NotEqual(baseline, ActionConfirmation.ConfirmationToken("comment_on_pr", Args("""{"title":"a","body":"b"}""")));
        Assert.Equal(baseline, ActionConfirmation.ConfirmationToken("create_issue", Args("""{"title":"a","body":"b","confirm":true}""")));
    }

    [Fact]
    public void Token_ignores_key_order_and_whitespace()
    {
        Assert.Equal(
            ActionConfirmation.ConfirmationToken("t", Args("""{"a":1,"b":[1,2]}""")),
            ActionConfirmation.ConfirmationToken("t", Args("""{ "b" : [1,2], "a" : 1 }""")));
    }

    [Fact]
    public async Task A_retry_with_a_tampered_request_state_is_refused()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("README.md", "x");
        repo.Commit("initial");
        var api = new FakeGitHubApi().MapJson(HttpMethod.Get, "/repos/acme/widgets", 200, """{"default_branch":"main"}""");
        var options = new McpClientOptions
        {
            Handlers = new McpClientHandlers { ElicitationHandler = (_, _) => ValueTask.FromResult(new ElicitResult { Action = "accept" }) },
        };
        await using var server = await GuardianTestHost.StartAsync(
            repo.Path, new Dictionary<string, string?> { ["Guardian:GitHub:Enabled"] = "true" }, api.Install, options, Ct);

        var result = await server.Client.CallToolAsync(
            new CallToolRequestParams
            {
                Name = "create_issue",
                Arguments = Args("""{"title":"t","body":"b"}"""),
                RequestState = "forged",
            },
            Ct);

        Assert.True(result.IsError);
        Assert.Contains("Confirmation does not match this request", string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text)));
        Assert.DoesNotContain(api.Requests, r => r.Method == HttpMethod.Post);
    }
}
