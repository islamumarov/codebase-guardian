using CodebaseGuardian.GitHub;

namespace CodebaseGuardian.Tests.App;

public class GitHubRepositoryRefTests
{
    [Theory]
    [InlineData("https://github.com/acme/widgets")]
    [InlineData("https://github.com/acme/widgets.git")]
    [InlineData("https://someone@github.com/acme/widgets.git")]
    [InlineData("git@github.com:acme/widgets")]
    [InlineData("git@github.com:acme/widgets.git")]
    [InlineData("ssh://git@github.com/acme/widgets")]
    [InlineData("ssh://git@github.com/acme/widgets.git")]
    [InlineData("https://github.com/acme/widgets/")]
    [InlineData("https://GitHub.com/acme/widgets")]
    public void Accepts_github_remote_forms(string url)
    {
        Assert.True(GitHubRepositoryRef.TryParseRemoteUrl(url, out var repository));
        Assert.Equal(new GitHubRepositoryRef("acme", "widgets"), repository);
        Assert.Equal("acme/widgets", repository.ToString());
    }

    [Theory]
    [InlineData("https://gitlab.com/acme/widgets.git")]
    [InlineData("https://github.com/acme")]
    [InlineData("https://github.com/acme/widgets/extra")]
    [InlineData("file:///tmp/x")]
    [InlineData("")]
    [InlineData("https://github.com.evil.example/acme/widgets")]
    [InlineData("https://github.com/ac me/widgets")]
    [InlineData("git@github.com:acme/wid$gets.git")]
    public void Rejects_everything_else(string url)
    {
        Assert.False(GitHubRepositoryRef.TryParseRemoteUrl(url, out var repository));
        Assert.Null(repository);
    }
}
