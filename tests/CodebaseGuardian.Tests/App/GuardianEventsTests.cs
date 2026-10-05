using System.Text.Json.Nodes;
using CodebaseGuardian.Tests.Infrastructure;
using CodebaseGuardian.Watching;

namespace CodebaseGuardian.Tests.App;

public class GuardianEventsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] CheckFields =
        ["runId", "command", "exitCode", "passed", "timedOut", "durationMs", "summary", "failedTests", "logUri", "trigger", "commitSha"];

    [Fact]
    public async Task Events_list_contains_exactly_the_eight_non_github_events()
    {
        using var repo = TempGitRepo.Create();
        await using var server = await GuardianTestHost.StartAsync(repo.Path, cancellationToken: Ct);

        var result = await server.RequestAsync("events/list", cancellationToken: Ct);
        var events = result["events"]!.AsArray().ToDictionary(e => (string)e!["name"]!, e => e!.AsObject());

        Assert.Equal(
            ["checks.completed", "checks.failed", "repo.branch.changed", "repo.commit.created", "repo.dependencies.changed", "repo.files.changed", "scan.completed", "security.secret_detected"],
            events.Keys.Order().ToList());

        AssertProperties(events["repo.commit.created"],
            "sha", "shortSha", "branch", "author", "committedAt", "subject", "filesChanged", "insertions", "deletions", "files", "suggestedSkill");
        AssertProperties(events["repo.branch.changed"], "from", "to", "headSha");
        AssertProperties(events["repo.files.changed"], "paths", "count");
        AssertProperties(events["repo.dependencies.changed"], "manifests", "ecosystems", "commitSha", "suggestedSkill");
        AssertProperties(events["checks.completed"], CheckFields);
        AssertProperties(events["checks.failed"], [.. CheckFields, "suggestedSkill"]);
        AssertProperties(events["security.secret_detected"], "source", "commitSha", "findings", "suggestedSkill");
        AssertProperties(events["scan.completed"], "scanId", "reportUri", "secretFindings", "vulnerablePackages", "checksPassed", "suggestedSkill");
        Assert.Equal(["boolean", "null"], events["scan.completed"]["payloadSchema"]!["properties"]!["checksPassed"]!["type"]!.AsArray().Select(t => (string)t!).ToList());

        var author = events["repo.commit.created"]["payloadSchema"]!["properties"]!["author"]!["properties"]!.AsObject();
        Assert.Equal(["name", "email"], author.Select(p => p.Key).ToList());
        Assert.Equal(["string", "null"], events["repo.branch.changed"]["payloadSchema"]!["properties"]!["to"]!["type"]!.AsArray().Select(t => (string)t!).ToList());

        Assert.Equal("string", (string?)events["repo.commit.created"]["inputSchema"]!["properties"]!["branch"]!["type"]);
        Assert.Equal("string", (string?)events["repo.files.changed"]["inputSchema"]!["properties"]!["pathPrefix"]!["type"]);
        Assert.All(events.Values, e => Assert.False(string.IsNullOrWhiteSpace((string?)e["description"])));
    }

    [Fact]
    public async Task Github_events_are_listed_only_when_github_is_enabled()
    {
        using var repo = TempGitRepo.Create();
        await using var server = await GuardianTestHost.StartAsync(
            repo.Path, new Dictionary<string, string?> { ["Guardian:GitHub:Enabled"] = "true" }, cancellationToken: Ct);

        var result = await server.RequestAsync("events/list", cancellationToken: Ct);
        var events = result["events"]!.AsArray().ToDictionary(e => (string)e!["name"]!, e => e!.AsObject());

        Assert.Equal(11, events.Count);
        Assert.Equal(
            ["github.ci.failed", "github.issue.opened", "github.pr.comment.created"],
            events.Keys.Where(k => k.StartsWith("github.", StringComparison.Ordinal)).Order().ToList());
        AssertProperties(events["github.issue.opened"], "number", "title", "author", "url", "labels", "body", "suggestedSkill");
        AssertProperties(events["github.pr.comment.created"], "prNumber", "commentId", "author", "body", "url", "path", "line", "suggestedSkill");
        AssertProperties(events["github.ci.failed"], "runId", "workflowName", "branch", "headSha", "url", "conclusion", "suggestedSkill");
        Assert.Equal("string", (string?)events["github.issue.opened"]["inputSchema"]!["properties"]!["label"]!["type"]);
        Assert.Equal("integer", (string?)events["github.pr.comment.created"]["inputSchema"]!["properties"]!["prNumber"]!["type"]);
        Assert.Equal("string", (string?)events["github.ci.failed"]["inputSchema"]!["properties"]!["branch"]!["type"]);
        var props = events["github.pr.comment.created"]["payloadSchema"]!["properties"]!;
        Assert.Equal(["string", "null"], props["path"]!["type"]!.AsArray().Select(t => (string)t!).ToList());
        Assert.Equal(["integer", "null"], props["line"]!["type"]!.AsArray().Select(t => (string)t!).ToList());
        Assert.All(
            events.Where(e => e.Key.StartsWith("github.", StringComparison.Ordinal)).Select(e => e.Value),
            e => Assert.EndsWith("(requires a GitHub token and a github.com origin)", (string)e["description"]!));
    }

    [Fact]
    public void Github_matches_compare_label_pull_request_number_and_branch()
    {
        var options = new Mcp.Events.EventsOptions();
        GuardianEvents.RegisterGitHub(options);
        var issue = options.Definitions.Single(d => d.Name == "github.issue.opened").Matches!;
        var comment = options.Definitions.Single(d => d.Name == "github.pr.comment.created").Matches!;
        var run = options.Definitions.Single(d => d.Name == "github.ci.failed").Matches!;

        var issueData = new JsonObject { ["labels"] = new JsonArray("bug", "p1") };
        Assert.True(issue(null, issueData));
        Assert.True(issue(new JsonObject { ["label"] = "p1" }, issueData));
        Assert.False(issue(new JsonObject { ["label"] = "docs" }, issueData));
        Assert.False(issue(new JsonObject { ["label"] = 1 }, issueData));

        var commentData = new JsonObject { ["prNumber"] = 7 };
        Assert.True(comment(null, commentData));
        Assert.True(comment(new JsonObject { ["prNumber"] = 7 }, commentData));
        Assert.False(comment(new JsonObject { ["prNumber"] = 8 }, commentData));
        Assert.False(comment(new JsonObject { ["prNumber"] = "7" }, commentData));

        var runData = new JsonObject { ["branch"] = "main" };
        Assert.True(run(null, runData));
        Assert.True(run(new JsonObject { ["branch"] = "main" }, runData));
        Assert.False(run(new JsonObject { ["branch"] = "dev" }, runData));
    }

    [Fact]
    public void Event_names_and_suggested_skills_match_the_spec_catalog()
    {
        Assert.Equal("repo.commit.created", GuardianEventNames.RepoCommitCreated);
        Assert.Equal("repo.branch.changed", GuardianEventNames.RepoBranchChanged);
        Assert.Equal("repo.files.changed", GuardianEventNames.RepoFilesChanged);
        Assert.Equal("repo.dependencies.changed", GuardianEventNames.RepoDependenciesChanged);
        Assert.Equal("checks.completed", GuardianEventNames.ChecksCompleted);
        Assert.Equal("checks.failed", GuardianEventNames.ChecksFailed);
        Assert.Equal("security.secret_detected", GuardianEventNames.SecuritySecretDetected);
        Assert.Equal("github.issue.opened", GuardianEventNames.GithubIssueOpened);
        Assert.Equal("github.pr.comment.created", GuardianEventNames.GithubPrCommentCreated);
        Assert.Equal("github.ci.failed", GuardianEventNames.GithubCiFailed);
        Assert.Equal("scan.completed", GuardianEventNames.ScanCompleted);
        Assert.Equal("skill://pr-review/SKILL.md", SuggestedSkills.PrReview);
        Assert.Equal("skill://dependency-hygiene/SKILL.md", SuggestedSkills.DependencyHygiene);
        Assert.Equal("skill://bug-triage/SKILL.md", SuggestedSkills.BugTriage);
        Assert.Equal("skill://security-audit/SKILL.md", SuggestedSkills.SecurityAudit);
    }

    [Fact]
    public void Matches_filters_commits_by_branch_and_file_changes_by_prefix()
    {
        var options = new Mcp.Events.EventsOptions();
        GuardianEvents.Register(options);
        var commit = options.Definitions.Single(d => d.Name == "repo.commit.created").Matches!;
        var files = options.Definitions.Single(d => d.Name == "repo.files.changed").Matches!;

        var data = new JsonObject { ["branch"] = "main" };
        Assert.True(commit(null, data));
        Assert.True(commit(new JsonObject(), data));
        Assert.True(commit(new JsonObject { ["branch"] = "main" }, data));
        Assert.False(commit(new JsonObject { ["branch"] = "dev" }, data));

        var changed = new JsonObject { ["paths"] = new JsonArray("src/a.cs", "docs/b.md"), ["count"] = 2 };
        Assert.True(files(null, changed));
        Assert.True(files(new JsonObject { ["pathPrefix"] = "docs/" }, changed));
        Assert.False(files(new JsonObject { ["pathPrefix"] = "tests/" }, changed));
    }

    [Fact]
    public void Matches_treats_non_string_arguments_as_no_match()
    {
        var options = new Mcp.Events.EventsOptions();
        GuardianEvents.Register(options);
        var commit = options.Definitions.Single(d => d.Name == "repo.commit.created").Matches!;
        var files = options.Definitions.Single(d => d.Name == "repo.files.changed").Matches!;

        Assert.False(commit(new JsonObject { ["branch"] = 5 }, new JsonObject { ["branch"] = "main" }));
        Assert.False(files(new JsonObject { ["pathPrefix"] = 5 }, new JsonObject { ["paths"] = new JsonArray("a") }));
    }

    private static void AssertProperties(JsonObject definition, params string[] expected)
    {
        var properties = definition["payloadSchema"]!["properties"]!.AsObject().Select(p => p.Key).ToHashSet();
        foreach (var name in expected)
        {
            Assert.Contains(name, properties);
        }
    }
}
