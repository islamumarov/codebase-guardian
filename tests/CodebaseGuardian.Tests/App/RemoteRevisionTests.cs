using CodebaseGuardian.Sources;

namespace CodebaseGuardian.Tests.App;

public class RemoteRevisionTests
{
    private const string Message = "Remote mode accepts branch names, tag names and commit SHAs only.";

    [Theory]
    [InlineData("main")]
    [InlineData("feature/x")]
    [InlineData("v1.2.3")]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    public void Branch_tag_and_sha_names_are_accepted(string revision)
    {
        Assert.Equal(revision, RemoteRevision.Require(revision, "branch"));
    }

    [Theory]
    [InlineData("HEAD~1")]
    [InlineData("main^")]
    [InlineData("main@{1}")]
    [InlineData("a:b")]
    [InlineData("-x")]
    [InlineData("a..b")]
    [InlineData("")]
    public void Ancestry_reflog_path_option_and_range_syntax_is_rejected(string revision)
    {
        var exception = Assert.Throws<ArgumentException>(() => RemoteRevision.Require(revision, "branch"));

        Assert.StartsWith(Message, exception.Message, StringComparison.Ordinal);
        Assert.Equal("branch", exception.ParamName);
    }
}
