using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodebaseGuardian.Tests.Infrastructure;
using Mcp.Skills;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CodebaseGuardian.Tests.Skills;

public sealed class SkillsProtocolTests : IDisposable
{
    private const string Alpha = "skill://alpha";
    private const string AlphaMd = Alpha + "/SKILL.md";
    private static readonly byte[] BinaryBytes = [0x00, 0xFF, 0xFE, 0x10, 0x80, 0x00];

    private readonly SkillFixture _fixture = new();

    public SkillsProtocolTests()
    {
        _fixture.WriteSkill("alpha", SkillFixture.SkillMd("alpha", "Alpha skill", "license: MIT\nmetadata:\n  owner: team\n"));
        _fixture.WriteFile("alpha/references/guide.md", "# Guide\r\nunix é text\r\n");
        _fixture.WriteBytes("alpha/references/data.bin", BinaryBytes);
        _fixture.WriteFile("alpha/notes.txt", "notes");
        _fixture.WriteSkill("beta");
    }

    public void Dispose() => _fixture.Dispose();

    private Task<InProcessMcpServer> StartAsync(bool withAttributeResource = false) =>
        InProcessMcpServer.StartAsync(
            (_, _, builder) =>
            {
                builder.WithSkills(o => o.Directories.Add(_fixture.Root));
                if (withAttributeResource)
                {
                    builder.WithResources<AppResources>();
                }
            },
            cancellationToken: TestContext.Current.CancellationToken);

    private static JsonObject Params(string uri) => new() { ["uri"] = uri };

    private static async Task<int> ErrorCodeAsync(Task task)
    {
        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => task);
        return (int)ex.ErrorCode;
    }

    [Fact]
    public async Task Capabilities_declare_resources_and_the_skills_extension_without_client_opt_in()
    {
        await using var server = await StartAsync();
        var caps = server.Client.ServerCapabilities;

        Assert.NotNull(caps.Resources);
        var ext = Assert.IsType<JsonElement>(caps.Extensions!["io.modelcontextprotocol/skills"]);
        Assert.Equal(JsonValueKind.Object, ext.ValueKind);
        Assert.True(ext.GetProperty("directoryRead").GetBoolean());
    }

    [Fact]
    public async Task Skills_list_returns_complete_manifest_entries()
    {
        await using var server = await StartAsync();
        var result = await server.RequestAsync("skills/list", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("complete", (string?)result["resultType"]);
        Assert.Equal(300000, (long?)result["ttlMs"]);
        Assert.Equal("public", (string?)result["cacheScope"]);
        Assert.False(result.ContainsKey("nextCursor"));

        var skills = result["skills"]!.AsArray();
        var uris = skills.Select(s => (string)s!["uri"]!).ToList();
        Assert.Equal(["skill://alpha/SKILL.md", "skill://beta/SKILL.md"], uris);
        Assert.Equal(uris.Count, uris.Distinct().Count());

        foreach (var entry in skills)
        {
            Assert.Equal(["frontmatter", "resources", "uri"], entry!.AsObject().Select(p => p.Key).Order().ToArray());
            var resources = entry["resources"]!.AsArray();
            Assert.Contains(resources, r => (string?)r!["uri"] == (string?)entry["uri"]);
            foreach (var r in resources)
            {
                Assert.Equal(["digest", "size", "uri"], r!.AsObject().Select(p => p.Key).Order().ToArray());
                Assert.Matches("^sha256:[0-9a-f]{64}$", (string)r["digest"]!);
            }
        }

        var alphaResources = skills[0]!["resources"]!.AsArray().Select(r => (string)r!["uri"]!).ToList();
        Assert.Equal(
            [AlphaMd, Alpha + "/notes.txt", Alpha + "/references/data.bin", Alpha + "/references/guide.md"],
            alphaResources.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("MIT", (string?)skills[0]!["frontmatter"]!["license"]);
    }

    [Fact]
    public async Task Skills_list_ignores_cursor()
    {
        await using var server = await StartAsync();
        var result = await server.RequestAsync("skills/list", new JsonObject { ["cursor"] = "bogus" }, TestContext.Current.CancellationToken);

        Assert.Equal(2, result["skills"]!.AsArray().Count);
        Assert.False(result.ContainsKey("nextCursor"));
    }

    [Fact]
    public async Task Skills_get_returns_the_listed_entry()
    {
        await using var server = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var list = await server.RequestAsync("skills/list", cancellationToken: ct);
        var get = await server.RequestAsync("skills/get", Params(AlphaMd), ct);

        Assert.Equal("complete", (string?)get["resultType"]);
        Assert.Equal(300000, (long?)get["ttlMs"]);
        Assert.Equal("public", (string?)get["cacheScope"]);
        Assert.False(get.ContainsKey("nextCursor"));
        Assert.Equal(AlphaMd, (string?)get["skill"]!["uri"]);
        Assert.True(JsonNode.DeepEquals(list["skills"]![0], get["skill"]));
    }

    [Theory]
    [InlineData("skill://mcp-conformance-nonexistent-skill-9f3a2b/SKILL.md")]
    [InlineData(Alpha)]
    [InlineData(Alpha + "/references/guide.md")]
    public async Task Skills_get_rejects_anything_but_a_served_skill_md(string uri)
    {
        await using var server = await StartAsync();
        var code = await ErrorCodeAsync(server.RequestAsync("skills/get", Params(uri), TestContext.Current.CancellationToken));
        Assert.Equal(-32602, code);
    }

    [Fact]
    public async Task Skills_get_requires_a_string_uri()
    {
        await using var server = await StartAsync();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(-32602, await ErrorCodeAsync(server.RequestAsync("skills/get", cancellationToken: ct)));
        Assert.Equal(-32602, await ErrorCodeAsync(server.RequestAsync("skills/get", new JsonObject { ["uri"] = 5 }, ct)));
    }

    [Fact]
    public async Task Resources_read_serves_skill_md_as_text_matching_the_manifest()
    {
        await using var server = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var entry = (await server.RequestAsync("skills/get", Params(AlphaMd), ct))["skill"]!;
        var manifest = entry["resources"]!.AsArray().Single(r => (string?)r!["uri"] == AlphaMd)!;

        var read = await server.RequestAsync("resources/read", Params(AlphaMd), ct);

        Assert.Equal(300000, (long?)read["ttlMs"]);
        Assert.Equal("public", (string?)read["cacheScope"]);
        var content = Assert.Single(read["contents"]!.AsArray())!;
        Assert.Equal(AlphaMd, (string?)content["uri"]);
        Assert.Equal("text/markdown", (string?)content["mimeType"]);
        var bytes = Encoding.UTF8.GetBytes((string)content["text"]!);
        Assert.Equal((string?)manifest["digest"], "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.Equal((long?)manifest["size"], bytes.Length);
    }

    [Fact]
    public async Task Resources_read_preserves_crlf_text_bytes()
    {
        await using var server = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var read = await server.RequestAsync("resources/read", Params(Alpha + "/references/guide.md"), ct);

        var content = Assert.Single(read["contents"]!.AsArray())!;
        Assert.Equal("# Guide\r\nunix é text\r\n", (string?)content["text"]);
    }

    [Fact]
    public async Task Resources_read_serves_binary_files_as_blobs_matching_the_manifest()
    {
        await using var server = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var uri = Alpha + "/references/data.bin";
        var entry = (await server.RequestAsync("skills/get", Params(AlphaMd), ct))["skill"]!;
        var manifest = entry["resources"]!.AsArray().Single(r => (string?)r!["uri"] == uri)!;

        var read = await server.RequestAsync("resources/read", Params(uri), ct);

        var content = Assert.Single(read["contents"]!.AsArray())!;
        Assert.Equal(uri, (string?)content["uri"]);
        Assert.False(content.AsObject().ContainsKey("text"));
        var bytes = Convert.FromBase64String((string)content["blob"]!);
        Assert.Equal(BinaryBytes, bytes);
        Assert.Equal((string?)manifest["digest"], "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public async Task Resources_read_of_an_unknown_skill_uri_is_invalid_params_with_the_uri()
    {
        await using var server = await StartAsync();
        var uri = "skill://nope/SKILL.md";
        var ex = await Assert.ThrowsAsync<McpProtocolException>(
            () => server.RequestAsync("resources/read", Params(uri), TestContext.Current.CancellationToken));

        Assert.Equal(-32602, (int)ex.ErrorCode);
        Assert.Contains("Resource not found", ex.Message);
    }

    [Fact]
    public async Task Resources_list_contains_skill_md_files_only()
    {
        await using var server = await StartAsync();
        var result = await server.RequestAsync("resources/list", cancellationToken: TestContext.Current.CancellationToken);

        var resources = result["resources"]!.AsArray();
        Assert.Equal([AlphaMd, "skill://beta/SKILL.md"], resources.Select(r => (string)r!["uri"]!).ToArray());
        var alpha = resources[0]!;
        Assert.Equal("alpha", (string?)alpha["name"]);
        Assert.Equal("Alpha skill", (string?)alpha["description"]);
        Assert.Equal("text/markdown", (string?)alpha["mimeType"]);
        Assert.True((long?)alpha["size"] > 0);
    }

    [Fact]
    public async Task Skills_compose_with_attribute_based_resources()
    {
        await using var server = await StartAsync(withAttributeResource: true);
        var ct = TestContext.Current.CancellationToken;

        var list = await server.RequestAsync("resources/list", cancellationToken: ct);
        var uris = list["resources"]!.AsArray().Select(r => (string)r!["uri"]!).ToList();
        Assert.Contains("test://status", uris);
        Assert.Contains(AlphaMd, uris);

        var app = await server.RequestAsync("resources/read", Params("test://status"), ct);
        Assert.Equal("ok", (string?)app["contents"]![0]!["text"]);
        var skill = await server.RequestAsync("resources/read", Params(AlphaMd), ct);
        Assert.Equal("text/markdown", (string?)skill["contents"]![0]!["mimeType"]);
    }

    [Fact]
    public async Task Directory_read_lists_the_root_children()
    {
        await using var server = await StartAsync();
        var result = await server.RequestAsync("resources/directory/read", Params(Alpha), TestContext.Current.CancellationToken);

        Assert.Equal("complete", (string?)result["resultType"]);
        var children = result["resources"]!.AsArray();
        Assert.Equal(["SKILL.md", "notes.txt", "references"], children.Select(c => (string)c!["name"]!).Order(StringComparer.Ordinal).ToArray());

        var references = children.Single(c => (string?)c!["name"] == "references")!;
        Assert.Equal("skill://alpha/references", (string?)references["uri"]);
        Assert.Equal("inode/directory", (string?)references["mimeType"]);
        Assert.False(references.AsObject().ContainsKey("size"));

        var skillMd = children.Single(c => (string?)c!["name"] == "SKILL.md")!;
        Assert.Equal("text/markdown", (string?)skillMd["mimeType"]);
        Assert.True((long?)skillMd["size"] > 0);
    }

    [Fact]
    public async Task Directory_read_lists_a_subdirectory()
    {
        await using var server = await StartAsync();
        var result = await server.RequestAsync("resources/directory/read", Params(Alpha + "/references"), TestContext.Current.CancellationToken);

        Assert.Equal(
            [Alpha + "/references/data.bin", Alpha + "/references/guide.md"],
            result["resources"]!.AsArray().Select(c => (string)c!["uri"]!).Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData(AlphaMd)]
    [InlineData("skill://alpha/missing")]
    public async Task Directory_read_of_a_non_directory_is_invalid_params(string uri)
    {
        await using var server = await StartAsync();
        var ex = await Assert.ThrowsAsync<McpProtocolException>(
            () => server.RequestAsync("resources/directory/read", Params(uri), TestContext.Current.CancellationToken));

        Assert.Equal(-32602, (int)ex.ErrorCode);
        Assert.EndsWith($"{uri} is not a directory resource", ex.Message);
    }

    [Fact]
    public async Task Directory_read_requires_a_string_uri()
    {
        await using var server = await StartAsync();
        Assert.Equal(-32602, await ErrorCodeAsync(server.RequestAsync("resources/directory/read", cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Listed_frontmatter_equals_the_frontmatter_parsed_from_the_served_skill_md()
    {
        await using var server = await StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var entry = (await server.RequestAsync("skills/get", Params(AlphaMd), ct))["skill"]!;
        var read = await server.RequestAsync("resources/read", Params(AlphaMd), ct);

        var parsed = FrontmatterParser.Parse((string)read["contents"]![0]!["text"]!);

        Assert.True(JsonNode.DeepEquals(parsed, entry["frontmatter"]));
    }

    [Fact]
    public async Task Startup_fails_when_a_skill_is_invalid()
    {
        using var bad = new SkillFixture();
        bad.WriteSkill("gamma", SkillFixture.SkillMd("not-gamma"));

        await Assert.ThrowsAsync<SkillValidationException>(() =>
            InProcessMcpServer.StartAsync(
                (_, _, builder) => builder.WithSkills(o => o.Directories.Add(bad.Root)),
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [McpServerResourceType]
    private sealed class AppResources
    {
        [McpServerResource(UriTemplate = "test://status", Name = "status", MimeType = "text/plain")]
        [Description("Test resource")]
        public static string Status() => "ok";
    }
}
