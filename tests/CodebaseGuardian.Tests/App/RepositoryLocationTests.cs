using CodebaseGuardian.GitHub;
using CodebaseGuardian.Sources;
using Microsoft.Extensions.Configuration;

namespace CodebaseGuardian.Tests.App;

public class RepositoryLocationTests
{
    [Theory]
    [InlineData("github:acme/widgets")]
    [InlineData("https://github.com/acme/widgets")]
    [InlineData("https://github.com/acme/widgets.git")]
    [InlineData("https://github.com/acme/widgets/")]
    [InlineData("git@github.com:acme/widgets.git")]
    [InlineData("ssh://git@github.com/acme/widgets.git")]
    public void A_GitHub_repository_is_recognised_in_every_supported_form(string value)
    {
        var location = RepositoryLocation.Parse(value);

        var github = Assert.IsType<RepositoryLocation.GitHub>(location);
        Assert.Equal(new GitHubRepositoryRef("acme", "widgets"), github.Repository);
    }

    [Theory]
    [InlineData("github:acme")]
    [InlineData("github:acme/widgets/x")]
    [InlineData("github:../widgets")]
    public void A_malformed_github_shorthand_is_invalid(string value)
    {
        var location = RepositoryLocation.Parse(value);

        var invalid = Assert.IsType<RepositoryLocation.Invalid>(location);
        Assert.Equal(value, invalid.Value);
        Assert.Equal($"Guardian:RepositoryPath '{value}' must be github:owner/name.", invalid.Reason);
    }

    [Fact]
    public void A_GitHub_URL_that_is_not_a_repository_is_invalid()
    {
        const string value = "https://github.com/acme/widgets/tree/main";

        var invalid = Assert.IsType<RepositoryLocation.Invalid>(RepositoryLocation.Parse(value));

        Assert.Equal(
            $"Guardian:RepositoryPath '{value}' is not a GitHub repository URL (expected https://github.com/owner/name).",
            invalid.Reason);
    }

    [Theory]
    [InlineData("acme/widgets")]
    [InlineData(".")]
    [InlineData("/tmp/repo")]
    [InlineData("C:\\repo")]
    [InlineData("https://gitlab.com/acme/widgets")]
    public void Everything_else_is_a_local_path(string value)
    {
        var location = RepositoryLocation.Parse(value);

        var local = Assert.IsType<RepositoryLocation.Local>(location);
        Assert.Equal(Path.GetFullPath(value), local.FullPath);
    }

    [Fact]
    public void A_relative_path_is_resolved_against_the_current_directory()
    {
        var local = Assert.IsType<RepositoryLocation.Local>(RepositoryLocation.Parse("acme/widgets"));

        Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), "acme", "widgets"), local.FullPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_value_is_kept_for_validation_to_report(string value)
    {
        var local = Assert.IsType<RepositoryLocation.Local>(RepositoryLocation.Parse(value));

        Assert.Equal(value, local.FullPath);
    }

    [Fact]
    public void Configuration_without_a_repository_path_means_the_current_directory()
    {
        var configuration = new ConfigurationBuilder().Build();

        var local = Assert.IsType<RepositoryLocation.Local>(RepositoryLocation.FromConfiguration(configuration));

        Assert.Equal(Directory.GetCurrentDirectory(), local.FullPath);
    }

    [Fact]
    public void Configuration_is_read_from_Guardian_RepositoryPath()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Guardian:RepositoryPath"] = "github:acme/widgets" })
            .Build();

        var github = Assert.IsType<RepositoryLocation.GitHub>(RepositoryLocation.FromConfiguration(configuration));

        Assert.Equal("acme/widgets", github.Repository.ToString());
    }
}
