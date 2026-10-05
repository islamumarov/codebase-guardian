using CodebaseGuardian.GitHub;
using CodebaseGuardian.Hosting;
using CodebaseGuardian.Processes;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Tests.App;

public class GitHubTokenProviderTests
{
    private static bool IsGhAuthToken(ProcessSpec spec) =>
        spec.FileName == "gh" && spec.Arguments.SequenceEqual(["auth", "token"]);

    private static GitHubTokenProvider Create(FakeProcessRunner runner, string? environmentToken = null) =>
        new(runner, Options.Create(new GuardianOptions { RepositoryPath = Directory.GetCurrentDirectory() }),
            name => name == "GITHUB_TOKEN" ? environmentToken : null);

    [Fact]
    public async Task Environment_variable_wins_and_gh_is_never_called()
    {
        var runner = new FakeProcessRunner();

        var token = await Create(runner, "env-token").GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("env-token", token);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Falls_back_to_gh_auth_token_and_trims_it()
    {
        var runner = new FakeProcessRunner().On(IsGhAuthToken, FakeProcessRunner.Result(0, "gho_x\n"));

        var token = await Create(runner, environmentToken: "").GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("gho_x", token);
        Assert.Equal(TimeSpan.FromSeconds(10), Assert.Single(runner.Calls).Timeout);
    }

    [Fact]
    public async Task Missing_gh_gives_null()
    {
        var runner = new FakeProcessRunner().OnThrow(IsGhAuthToken,
            new ExecutableNotFoundException("gh", new InvalidOperationException("missing")));

        Assert.Null(await Create(runner).GetTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failing_or_empty_gh_gives_null()
    {
        var failing = new FakeProcessRunner().On(IsGhAuthToken, FakeProcessRunner.Result(1, "", "not logged in"));
        var empty = new FakeProcessRunner().On(IsGhAuthToken, FakeProcessRunner.Result(0, "  \n"));

        Assert.Null(await Create(failing).GetTokenAsync(TestContext.Current.CancellationToken));
        Assert.Null(await Create(empty).GetTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_found_token_is_cached_but_a_miss_is_retried()
    {
        var runner = new FakeProcessRunner().On(IsGhAuthToken, FakeProcessRunner.Result(0, "gho_x\n"));
        var provider = Create(runner);

        await provider.GetTokenAsync(TestContext.Current.CancellationToken);
        await provider.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Single(runner.Calls);

        var missing = new FakeProcessRunner().On(IsGhAuthToken, FakeProcessRunner.Result(1));
        var retrying = Create(missing);
        await retrying.GetTokenAsync(TestContext.Current.CancellationToken);
        await retrying.GetTokenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, missing.Calls.Count);
    }
}
