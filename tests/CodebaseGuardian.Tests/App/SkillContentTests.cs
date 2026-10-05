using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodebaseGuardian.Hosting;
using CodebaseGuardian.Tests.Infrastructure;
using Mcp.Skills;

namespace CodebaseGuardian.Tests.App;

/// <summary>
/// Content lint for the bundled skills: they may only mention tools, tool arguments, resources, events, skills and
/// files that exist, so a rename in the server fails the build instead of silently misleading the agent.
/// </summary>
public sealed partial class SkillContentTests
{
    private static readonly string[] SkillNames = ["bug-triage", "dependency-hygiene", "guardian", "pr-review", "security-audit"];

    private static readonly string[] FileExtensions = [".md", ".json", ".yml", ".yaml", ".txt", ".props", ".lock", ".cs", ".js", ".ts", ".py", ".sh"];

    // Snake_case tokens that are neither tool names nor tool argument names/values. Keep empty unless justified here.
    private static readonly HashSet<string> AllowedSnakeCaseTokens = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SkillsDirectory => Path.Combine(AppContext.BaseDirectory, "skills");

    [GeneratedRegex(@"^[a-z]+(?:_[a-z]+)+$")]
    private static partial Regex SnakeCase();

    [GeneratedRegex(@"^[a-z]+(?:\.[a-z_]+)+$")]
    private static partial Regex DottedName();

    [GeneratedRegex(@"`([^`\r\n]+)`")]
    private static partial Regex Backticked();

    [GeneratedRegex(@"(?<!!)\[[^\]\r\n]*\]\(([^)\s]+)\)")]
    private static partial Regex MarkdownLink();

    private static SkillCatalog LoadCatalog()
    {
        var options = new SkillsOptions();
        options.Directories.Add(SkillsDirectory);
        return SkillCatalog.Load(options);
    }

    private static IEnumerable<(SkillDefinition Skill, SkillFile File, string Text)> TextFiles(SkillCatalog catalog) =>
        catalog.Skills.SelectMany(skill => skill.Files
            .Where(file => file.IsUtf8Text)
            .Select(file => (skill, file, Encoding.UTF8.GetString(file.Content))));

    private static IEnumerable<string> Tokens(string text) =>
        Backticked().Matches(text).Select(match => match.Groups[1].Value.Trim());

    private static void CollectSchemaNames(JsonElement schema, HashSet<string> names)
    {
        switch (schema.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in schema.EnumerateObject())
                {
                    if (property.Name == "properties" && property.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var declared in property.Value.EnumerateObject())
                        {
                            names.Add(declared.Name);
                        }
                    }
                    else if (property.Name == "enum" && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var value in property.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String))
                        {
                            names.Add(value.GetString()!);
                        }
                    }

                    CollectSchemaNames(property.Value, names);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in schema.EnumerateArray())
                {
                    CollectSchemaNames(item, names);
                }

                break;
        }
    }

    private static string ToPattern(string uriOrTemplate) =>
        "^" + Regex.Replace(Regex.Escape(uriOrTemplate).Replace(@"\{", "{"), @"\{[^}]+\}", "[^/]+") + "$";

    [Fact]
    public async Task All_five_skills_load_and_are_listed_with_a_subdirectory_in_the_first()
    {
        var catalog = LoadCatalog();
        Assert.Equal(SkillNames, catalog.Skills.Select(s => s.Name).Order(StringComparer.Ordinal).ToArray());

        using var repo = TempGitRepo.Create();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var list = await server.RequestAsync("skills/list", cancellationToken: Ct);
        var uris = list["skills"]!.AsArray().Select(s => (string)s!["uri"]!).ToList();
        Assert.Equal(SkillNames.Select(n => $"skill://{n}/SKILL.md").ToArray(), uris.Order(StringComparer.Ordinal).ToArray());

        var first = uris[0];
        var root = first[..^"/SKILL.md".Length];
        var directory = await server.RequestAsync("resources/directory/read", new JsonObject { ["uri"] = root }, Ct);
        Assert.Contains(directory["resources"]!.AsArray(), child => (string?)child!["mimeType"] == "inode/directory");
    }

    [Fact]
    public void Frontmatter_follows_the_authoring_rules_and_skill_files_stay_short()
    {
        var catalog = LoadCatalog();
        foreach (var skill in catalog.Skills)
        {
            Assert.InRange(skill.Description.Length, 1, 300);
            Assert.Equal("MIT", (string?)skill.Frontmatter["license"]);
            Assert.Equal("codebase-guardian", (string?)skill.Frontmatter["metadata"]?["author"]);
            Assert.Equal("1", (string?)skill.Frontmatter["metadata"]?["version"]);

            var skillMd = skill.Files.Single(f => f.Uri == skill.Uri);
            var text = Encoding.UTF8.GetString(skillMd.Content);
            Assert.True(text.Split('\n').Length <= 200, $"{skill.Uri} is longer than 200 lines");
            Assert.All(text.Split('\n').TakeWhile((line, index) => index == 0 || line.TrimEnd() != "---"),
                line => Assert.DoesNotMatch(@"^(name|description|license):\s*[^""\s]", line));
        }
    }

    [Fact]
    public async Task Every_skill_description_names_a_triggering_event_that_exists()
    {
        var catalog = LoadCatalog();
        using var repo = TempGitRepo.Create();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);
        var events = (await server.RequestAsync("events/list", cancellationToken: Ct))["events"]!.AsArray()
            .Select(e => (string)e!["name"]!).ToHashSet();

        foreach (var skill in catalog.Skills.Where(s => s.Name != "guardian"))
        {
            Assert.Contains(events, name => skill.Description.Contains(name, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Backticked_references_exist_in_the_running_server()
    {
        var catalog = LoadCatalog();
        using var repo = TempGitRepo.Create();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var tools = await server.Client.ListToolsAsync(cancellationToken: Ct);
        var toolNames = tools.Select(t => t.Name).ToHashSet();
        var schemaNames = new HashSet<string>();
        foreach (var tool in tools)
        {
            CollectSchemaNames(tool.JsonSchema, schemaNames);
        }

        var resources = (await server.Client.ListResourcesAsync(cancellationToken: Ct)).Select(r => r.Uri).ToList();
        var templates = (await server.Client.ListResourceTemplatesAsync(cancellationToken: Ct)).Select(t => t.UriTemplate).ToList();
        var resourcePatterns = resources.Concat(templates).Select(ToPattern).ToList();
        var events = (await server.RequestAsync("events/list", cancellationToken: Ct))["events"]!.AsArray()
            .Select(e => (string)e!["name"]!).ToHashSet();

        var problems = new List<string>();
        var checkedTokens = 0;
        foreach (var (skill, file, text) in TextFiles(catalog))
        {
            if (text.Contains("```"))
            {
                problems.Add($"{file.Uri}: fenced code blocks are not linted; use inline code or plain text");
            }

            foreach (var token in Tokens(text))
            {
                checkedTokens++;
                if (token.StartsWith("guardian://", StringComparison.Ordinal))
                {
                    if (!resourcePatterns.Any(pattern => Regex.IsMatch(token, pattern)))
                    {
                        problems.Add($"{file.Uri}: unknown resource `{token}`");
                    }
                }
                else if (token.StartsWith("skill://", StringComparison.Ordinal))
                {
                    if (token.EndsWith("/SKILL.md", StringComparison.Ordinal))
                    {
                        try
                        {
                            await server.RequestAsync("skills/get", new JsonObject { ["uri"] = token }, Ct);
                        }
                        catch (Exception ex) when (ex is ModelContextProtocol.McpException)
                        {
                            problems.Add($"{file.Uri}: skills/get fails for `{token}`");
                        }
                    }
                    else if (catalog.FindFile(token) is null)
                    {
                        problems.Add($"{file.Uri}: unknown skill file `{token}`");
                    }
                }
                else if (SnakeCase().IsMatch(token))
                {
                    if (!toolNames.Contains(token) && !schemaNames.Contains(token) && !AllowedSnakeCaseTokens.Contains(token))
                    {
                        problems.Add($"{file.Uri}: `{token}` is neither a tool nor a tool argument or enum value");
                    }
                }
                else if (DottedName().IsMatch(token))
                {
                    if (!FileExtensions.Any(extension => token.EndsWith(extension, StringComparison.Ordinal)) && !events.Contains(token))
                    {
                        problems.Add($"{file.Uri}: `{token}` is not an event name");
                    }
                }
            }

            foreach (Match link in MarkdownLink().Matches(text))
            {
                var target = link.Groups[1].Value;
                if (target.Contains("://", StringComparison.Ordinal) || target.StartsWith('#'))
                {
                    continue;
                }

                var path = target.Split('#')[0];
                if (!skill.Files.Any(f => f.RelativePath == path))
                {
                    problems.Add($"{file.Uri}: link '{target}' does not resolve to a file of {skill.Name}");
                }
            }
        }

        Assert.True(checkedTokens > 50, "The lint found suspiciously few backticked tokens.");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_lint_rejects_a_misspelled_tool_name()
    {
        // Guards the guard: a typo must not be satisfied by property or enum names.
        Assert.Matches(SnakeCase(), "scan_secret");
        var names = new HashSet<string>();
        CollectSchemaNames(JsonDocument.Parse("""{"properties":{"scope":{"enum":["working_tree"]}}}""").RootElement, names);
        Assert.Contains("working_tree", names);
        Assert.DoesNotContain("scan_secret", names);
    }

    [Fact]
    public void Skills_guide_the_core_workflows_with_the_documented_tools()
    {
        var catalog = LoadCatalog();
        string Text(string skill) => string.Concat(catalog.Skills.Single(s => s.Name == skill).Files
            .Where(f => f.IsUtf8Text).Select(f => Encoding.UTF8.GetString(f.Content)));

        Assert.Contains("`guardian://checks/{runId}/log`", Text("bug-triage"));
        Assert.Contains("`run_checks`", Text("bug-triage"));
        Assert.Contains("`audit_dependencies`", Text("dependency-hygiene"));
        Assert.Contains("`scan_secrets`", Text("security-audit"));
        Assert.Contains("`scope:\"commit\"`", Text("pr-review"));
        foreach (var rule in new[] { "aws-access-key-id", "github-token", "github-fine-grained-pat", "slack-token", "stripe-live-key", "private-key", "jwt", "generic-secret-assignment" })
        {
            Assert.Contains($"`{rule}`", Text("security-audit"));
        }
    }

    [Fact]
    public async Task Server_instructions_point_to_every_skill_and_the_polling_fallback()
    {
        using var repo = TempGitRepo.Create();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);
        var instructions = server.Client.ServerInstructions;

        Assert.NotNull(instructions);
        Assert.Contains("Skills available (load `skill://guardian/SKILL.md` first):", instructions);
        foreach (var name in SkillNames)
        {
            Assert.Contains($"- skill://{name}/SKILL.md — ", instructions);
        }

        var events = (await server.RequestAsync("events/list", cancellationToken: Ct))["events"]!.AsArray();
        foreach (var name in events.Select(e => (string)e!["name"]!))
        {
            Assert.Contains($"- {name}", instructions);
        }

        Assert.Contains("If your client does not support `events/*`, call the `poll_events` tool.", instructions);
        Assert.True(instructions.IndexOf("Skills available", StringComparison.Ordinal) < instructions.IndexOf("poll_events", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_configured_skills_directory_replaces_the_bundled_default()
    {
        var custom = Path.Combine(Path.GetTempPath(), "guardian-skills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(custom, "only-one"));
        await File.WriteAllTextAsync(Path.Combine(custom, "only-one", "SKILL.md"), "---\nname: \"only-one\"\ndescription: \"Custom\"\n---\n# Only one\n", Ct);
        try
        {
            using var repo = TempGitRepo.Create();
            await using var server = await GuardianTestHost.StartAsync(
                repo.Path,
                new Dictionary<string, string?> { [$"{GuardianOptions.SectionName}:{nameof(GuardianOptions.SkillsDirectory)}"] = custom },
                cancellationToken: Ct);

            var list = await server.RequestAsync("skills/list", cancellationToken: Ct);
            Assert.Equal(["skill://only-one/SKILL.md"], list["skills"]!.AsArray().Select(s => (string)s!["uri"]!).ToArray());
            Assert.Contains("- skill://only-one/SKILL.md — Custom", server.Client.ServerInstructions);
        }
        finally
        {
            Directory.Delete(custom, recursive: true);
        }
    }
}
