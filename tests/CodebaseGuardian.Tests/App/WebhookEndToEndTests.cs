using System.Text.Json.Nodes;
using CodebaseGuardian.Tests.Infrastructure;
using Mcp.Events;
using ModelContextProtocol.Protocol;

namespace CodebaseGuardian.Tests.App;

public sealed class WebhookEndToEndTests : IDisposable
{
    private const string Key = "test-key-0123456789-abcdefghijklmnopqrstuvwxyz";
    private readonly TempGitRepo _repo = TempGitRepo.Create();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task An_authenticated_client_subscribes_and_the_receiver_gets_a_signed_verification()
    {
        await using var receiver = await WebhookReceiver.StartAsync(Ct);
        await using var host = await GuardianHttpTestHost.StartAsync(
            _repo.Path,
            new Dictionary<string, string?>
            {
                ["Guardian:Http:ApiKeys:alice"] = Key,
                ["Guardian:Webhooks:AllowInsecureLoopback"] = "true",
            },
            cancellationToken: Ct);
        await using var client = await host.ConnectAsync(Key, cancellationToken: Ct);

        var response = await client.SendRequestAsync(new JsonRpcRequest
        {
            Method = "events/subscribe",
            Params = new JsonObject
            {
                ["name"] = "repo.commit.created",
                ["delivery"] = new JsonObject { ["mode"] = "webhook", ["url"] = receiver.Url.ToString(), ["secret"] = receiver.Secret },
            },
        }, Ct);

        var result = (JsonObject)response.Result!;
        Assert.Matches("^sub_[0-9a-f]{16}$", result["id"]!.GetValue<string>());
        var verification = await receiver.WaitForAsync(r => r.Headers.ContainsKey("webhook-signature"), TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("verification", verification.Json["type"]!.GetValue<string>());
        Assert.True(WebhookSecret.TryParse(receiver.Secret, out var secret));
        Assert.True(WebhookReceiver.SignatureValid(verification, secret));
    }
}
