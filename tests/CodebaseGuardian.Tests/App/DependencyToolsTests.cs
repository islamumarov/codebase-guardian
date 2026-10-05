using CodebaseGuardian.Processes;
using CodebaseGuardian.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodebaseGuardian.Tests.App;

public class DependencyToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<InProcessMcpServer> StartAsync(TempGitRepo repo, FakeProcessRunner fake) =>
        GuardianTestHost.StartAsync(repo.Path, null, s => s.AddSingleton<IProcessRunner>(fake), cancellationToken: Ct);

    private static TempGitRepo NpmRepo()
    {
        var repo = TempGitRepo.Create();
        repo.WriteFile("package.json", "{}");
        repo.Commit("initial");
        return repo;
    }

    private static FakeProcessRunner NpmFake() => new FakeProcessRunner(new ProcessRunner())
        .On(s => s.FileName == "npm" && s.Arguments[0] == "audit", FakeProcessRunner.Result(1, NpmAuditParserTests.AuditJson))
        .On(s => s.FileName == "npm" && s.Arguments[0] == "outdated", FakeProcessRunner.Result(1, NpmAuditParserTests.OutdatedJson));

    [Fact]
    public async Task The_tool_is_read_only_and_open_world_with_an_optional_include_outdated_argument()
    {
        using var repo = NpmRepo();
        await using var server = await StartAsync(repo, NpmFake());

        var tool = Assert.Single(await server.Client.ListToolsAsync(cancellationToken: Ct), t => t.Name == "audit_dependencies");

        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tool.ProtocolTool.Annotations?.OpenWorldHint);
        Assert.True(tool.JsonSchema.GetProperty("properties").TryGetProperty("includeOutdated", out _));
        var output = tool.ProtocolTool.OutputSchema!.Value;
        Assert.Equal("object", output.GetProperty("type").GetString());
        Assert.True(output.GetProperty("properties").TryGetProperty("ecosystems", out _));
    }

    [Fact]
    public async Task The_tool_returns_the_report_as_structured_content_with_counts_and_a_summary()
    {
        using var repo = NpmRepo();
        await using var server = await StartAsync(repo, NpmFake());

        var result = await server.Client.CallToolAsync("audit_dependencies", cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        var json = result.StructuredContent!.Value;
        Assert.Equal(2, json.GetProperty("vulnerableCount").GetInt32());
        Assert.Equal(2, json.GetProperty("outdatedCount").GetInt32());
        var npm = json.GetProperty("ecosystems")[0];
        Assert.Equal("npm", npm.GetProperty("ecosystem").GetString());
        Assert.Equal("ok", npm.GetProperty("status").GetString());
        Assert.False(npm.TryGetProperty("reason", out _));
        var lodash = npm.GetProperty("vulnerable").EnumerateArray().Single(v => v.GetProperty("package").GetString() == "lodash");
        Assert.Equal("high", lodash.GetProperty("severity").GetString());
        Assert.False(lodash.TryGetProperty("project", out _));
        var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.Contains("2 vulnerable", text);
        Assert.Contains("2 outdated", text);
    }

    [Fact]
    public async Task Include_outdated_false_skips_npm_outdated()
    {
        using var repo = NpmRepo();
        var fake = NpmFake();
        await using var server = await StartAsync(repo, fake);

        var result = await server.Client.CallToolAsync("audit_dependencies",
            new Dictionary<string, object?> { ["includeOutdated"] = false }, cancellationToken: Ct);

        Assert.Equal(0, result.StructuredContent!.Value.GetProperty("outdatedCount").GetInt32());
        Assert.DoesNotContain(fake.Calls, c => c.Arguments[0] == "outdated");
    }

    [Fact]
    public async Task A_missing_toolchain_is_reported_as_skipped_not_as_a_tool_error()
    {
        using var repo = NpmRepo();
        await using var server = await StartAsync(repo, new FakeProcessRunner(new ProcessRunner())
            .OnThrow(s => s.FileName == "npm", new ExecutableNotFoundException("npm", new InvalidOperationException("missing"))));

        var result = await server.Client.CallToolAsync("audit_dependencies", cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        var npm = result.StructuredContent!.Value.GetProperty("ecosystems")[0];
        Assert.Equal("skipped", npm.GetProperty("status").GetString());
        Assert.Equal("npm not found on PATH", npm.GetProperty("reason").GetString());
    }
}
