using System.Text;
using System.Text.Json.Nodes;
using CodebaseGuardian.Tests.Infrastructure;
using GuardianWatch;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodebaseGuardian.Tests.App;

/// <summary>The whole loop of the demo client: commit a secret, get the event, load the suggested skill, verify it.</summary>
public class EndToEndTests
{
    private const string StreamId = "watch-security.secret_detected";
    private const string SubscriptionIdKey = "io.modelcontextprotocol/subscriptionId";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_committed_secret_leads_through_the_event_to_a_verified_security_audit_skill()
    {
        using var repo = TempGitRepo.Create();
        repo.WriteFile("README.md", "hello");
        repo.Commit("initial");
        await using var host = await GuardianTestHost.StartAsync(
            repo.Path,
            new Dictionary<string, string?>
            {
                ["Guardian:WatchEnabled"] = "true",
                ["Guardian:WatchIntervalMs"] = "100",
                ["Guardian:FileChangeDebounceMs"] = "100",
            },
            cancellationToken: Ct);

        // 1. one events/stream with an explicit id
        var delivered = 0;
        var received = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var handler = host.Client.RegisterNotificationHandler("notifications/events/event", (n, _) =>
        {
            var p = (JsonObject)n.Params!;
            if (p["_meta"]?[SubscriptionIdKey]?.ToString() == StreamId)
            {
                Interlocked.Increment(ref delivered);
                received.TrySetResult((JsonObject)p.DeepClone());
            }

            return ValueTask.CompletedTask;
        });
        var stream = host.Client.SendRequestAsync(
            new JsonRpcRequest { Id = new RequestId(StreamId), Method = "events/stream", Params = new JsonObject { ["name"] = "security.secret_detected" } },
            Ct);

        // 2. commit a fake AWS key (assembled at runtime)
        repo.WriteFile("config/settings.env", $"AWS_ACCESS_KEY_ID={FakeSecrets.AwsAccessKeyId()}\n");
        var sha = repo.Commit("add settings");

        // 3. the event, with its suggested skill
        var notification = await received.Task.WaitAsync(Timeout, Ct);
        var data = (JsonObject)notification["data"]!;
        Assert.Equal("security.secret_detected", (string?)notification["name"]);
        Assert.Equal(sha, (string?)data["commitSha"]);
        Assert.Equal("skill://security-audit/SKILL.md", (string?)data["suggestedSkill"]);
        Assert.DoesNotContain(FakeSecrets.AwsAccessKeyId(), notification.ToJsonString());

        // 4. skills/get, resources/read, then the same verification the sample performs
        var uri = (string)data["suggestedSkill"]!;
        var skill = (JsonObject)(await host.RequestAsync("skills/get", new JsonObject { ["uri"] = uri }, Ct))["skill"]!;
        Assert.Equal("security-audit", (string?)skill["frontmatter"]!["name"]);
        var entry = SkillVerifier.FindEntry(skill, uri);
        Assert.NotNull(entry);
        var text = Assert.IsType<TextResourceContents>(Assert.Single((await host.Client.ReadResourceAsync(uri, cancellationToken: Ct)).Contents));
        var content = Encoding.UTF8.GetBytes(text.Text);
        Assert.Null(SkillVerifier.Verify(entry, content));
        Assert.StartsWith("---", text.Text);

        // The check is real: a single changed byte is refused.
        Assert.NotNull(SkillVerifier.Verify(entry, [.. content, (byte)'x']));
        var flipped = (byte[])content.Clone();
        flipped[^1] ^= 1;
        Assert.Contains("digest mismatch", SkillVerifier.Verify(entry, flipped));

        // 5. cancel the stream the way the SDK needs it over a stream transport
        await host.Client.SendNotificationAsync(
            "notifications/cancelled", new CancelledNotificationParams { RequestId = new RequestId(StreamId) }, cancellationToken: Ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.WaitAsync(Timeout, Ct)); // the guard fails the test with a TimeoutException

        // The server really stopped: a further secret commit is detected but nothing reaches the cancelled stream.
        var before = Volatile.Read(ref delivered);
        repo.WriteFile("config/other.env", $"AWS_ACCESS_KEY_ID={FakeSecrets.AwsAccessKeyId()}\n");
        repo.Commit("add other settings");
        await Task.Delay(1000, Ct);
        Assert.Equal(before, Volatile.Read(ref delivered));
    }
}
