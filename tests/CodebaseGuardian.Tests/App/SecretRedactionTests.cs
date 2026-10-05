using CodebaseGuardian.Security;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CodebaseGuardian.Tests.App;

public class SecretRedactionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> RedactAsync(string text)
    {
        using var repo = TempGitRepo.Create();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);
        return server.Services.GetRequiredService<ISecretScanner>().RedactSecrets(text);
    }

    [Fact]
    public async Task Secrets_are_replaced_by_their_redacted_form()
    {
        var aws = FakeSecrets.AwsAccessKeyId();
        var token = FakeSecrets.GitHubToken();

        var result = await RedactAsync($"deploy with {aws} and\n{token}, thanks");

        Assert.Equal($"deploy with {Redactor.Redact(aws)} and\n{Redactor.Redact(token)}, thanks", result);
        Assert.DoesNotContain(aws, result);
        Assert.DoesNotContain(token, result);
    }

    [Fact]
    public async Task Only_the_secret_group_is_replaced_for_assignments()
    {
        var result = await RedactAsync(FakeSecrets.GenericAssignment());

        Assert.StartsWith("api_key = \"", result);
        Assert.DoesNotContain("Zx9fQ2mK8vLp4TnR7sW1", result);
    }

    [Fact]
    public async Task Text_without_secrets_is_returned_unchanged()
    {
        const string text = "Crash on start.\r\nSteps: run `dotnet test`; password = \"changeme\"";

        Assert.Equal(text, await RedactAsync(text));
        Assert.Equal(string.Empty, await RedactAsync(string.Empty));
    }
}
