using CodebaseGuardian.Hosting;
using Microsoft.Extensions.Configuration;

namespace CodebaseGuardian.Tests.App;

public class GuardianCommandLineTests
{
    // Mirrors how Program.cs feeds the command line into configuration.
    private static IConfigurationRoot Parse(params string[] args) =>
        new ConfigurationBuilder()
            .AddCommandLine(
                GuardianCommandLine.Normalize(args),
                new Dictionary<string, string>(GuardianCommandLine.SwitchMappings))
            .Build();

    [Fact]
    public void Maps_repo_transport_and_urls_to_guardian_keys()
    {
        var configuration = Parse("--repo", "/x", "--transport", "http", "--urls", "http://127.0.0.1:6000");

        Assert.Equal("/x", configuration["Guardian:RepositoryPath"]);
        Assert.Equal("http", configuration["Guardian:Transport"]);
        Assert.Equal("http://127.0.0.1:6000", configuration["Guardian:HttpUrl"]);
    }

    [Fact]
    public void Maps_skills_dir_to_the_skills_directory_key()
    {
        var configuration = Parse("--skills-dir", "/skills");

        Assert.Equal("/skills", configuration["Guardian:SkillsDirectory"]);
    }

    [Fact]
    public void Bare_auto_checks_flag_enables_auto_checks()
    {
        var configuration = Parse("--auto-checks");

        Assert.Equal("true", configuration["Guardian:AutoChecks"]);
    }

    [Fact]
    public void Bare_no_watch_flag_disables_watching()
    {
        var configuration = Parse("--no-watch");

        Assert.Equal("false", configuration["Guardian:WatchEnabled"]);
    }

    [Fact]
    public void Bare_flag_does_not_swallow_the_following_switch()
    {
        // Without normalization the command-line provider would take "--repo" as the value of "--auto-checks".
        var configuration = Parse("--auto-checks", "--no-watch", "--repo", "/x");

        Assert.Equal("true", configuration["Guardian:AutoChecks"]);
        Assert.Equal("false", configuration["Guardian:WatchEnabled"]);
        Assert.Equal("/x", configuration["Guardian:RepositoryPath"]);
    }

    [Theory]
    [InlineData("--auto-checks=true", "true")]
    [InlineData("--auto-checks=false", "false")]
    [InlineData("--AUTO-CHECKS", "true")]
    public void Auto_checks_accepts_explicit_and_differently_cased_forms(string argument, string expected)
    {
        var configuration = Parse(argument);

        Assert.Equal(expected, configuration["Guardian:AutoChecks"]);
    }

    [Theory]
    [InlineData(new[] { "--auto-checks", "false" }, "false")]
    [InlineData(new[] { "--auto-checks", "true" }, "true")]
    public void Auto_checks_accepts_a_separate_boolean_value(string[] args, string expected)
    {
        var configuration = Parse(args);

        Assert.Equal(expected, configuration["Guardian:AutoChecks"]);
    }

    [Theory]
    [InlineData(new[] { "--no-watch=true" }, "false")]
    [InlineData(new[] { "--no-watch=false" }, "true")]
    [InlineData(new[] { "--no-watch", "true" }, "false")]
    [InlineData(new[] { "--no-watch", "false" }, "true")]
    public void No_watch_with_an_explicit_value_inverts_it_for_watch_enabled(string[] args, string expected)
    {
        var configuration = Parse(args);

        Assert.Equal(expected, configuration["Guardian:WatchEnabled"]);
    }

    [Fact]
    public void A_bare_flag_is_not_confused_by_a_following_non_boolean_argument()
    {
        var normalized = GuardianCommandLine.Normalize(["--auto-checks", "positional"]);

        Assert.Equal(["--auto-checks=true", "positional"], normalized);
    }

    [Fact]
    public void Unknown_arguments_are_preserved_in_order()
    {
        string[] args = ["--foo", "bar", "positional", "--repo", "/x", "--baz=1", "-q"];

        Assert.Equal(args, GuardianCommandLine.Normalize(args));
    }

    [Fact]
    public void Unknown_arguments_keep_their_position_around_a_normalized_flag()
    {
        var normalized = GuardianCommandLine.Normalize(["--foo", "--no-watch", "bar", "--auto-checks"]);

        Assert.Equal(["--foo", "--no-watch=false", "bar", "--auto-checks=true"], normalized);
    }

    [Fact]
    public void Normalize_does_not_modify_its_input()
    {
        string[] args = ["--auto-checks", "--no-watch"];

        _ = GuardianCommandLine.Normalize(args);

        Assert.Equal(["--auto-checks", "--no-watch"], args);
    }
}
