using System.Text.Json.Nodes;
using Mcp.Events;

namespace CodebaseGuardian.Watching;

/// <summary>The Epic 1 part of the event catalog (spec section 5.5).</summary>
public static class GuardianEvents
{
    public static void Register(EventsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Define(new EventDefinition
        {
            Name = GuardianEventNames.RepoCommitCreated,
            Description = "A new commit became reachable from a local branch head that the watcher had not seen.",
            InputSchema = Obj("""{"type":"object","properties":{"branch":{"type":"string","description":"Only commits first seen on this branch."}}}"""),
            PayloadSchema = Obj("""
                {"type":"object","properties":{
                  "sha":{"type":"string"},"shortSha":{"type":"string"},"branch":{"type":"string"},
                  "author":{"type":"object","properties":{"name":{"type":"string"},"email":{"type":"string"}}},
                  "committedAt":{"type":"string"},"subject":{"type":"string"},
                  "filesChanged":{"type":"integer"},"insertions":{"type":"integer"},"deletions":{"type":"integer"},
                  "files":{"type":"array","items":{"type":"string"}},"suggestedSkill":{"type":"string"}}}
                """),
            Matches = (arguments, data) => !HasArgument(arguments, "branch")
                || Argument(arguments, "branch") is { } branch && AsString(data["branch"]) == branch,
        });

        options.Define(new EventDefinition
        {
            Name = GuardianEventNames.RepoBranchChanged,
            Description = "HEAD now points at a different branch.",
            PayloadSchema = Obj("""
                {"type":"object","properties":{
                  "from":{"type":@NS@},"to":{"type":@NS@},"headSha":{"type":@NS@}}}
                """),
        });

        options.Define(new EventDefinition
        {
            Name = GuardianEventNames.RepoFilesChanged,
            Description = "Working-tree files were edited (debounced; .git, bin, obj and node_modules are ignored).",
            InputSchema = Obj("""{"type":"object","properties":{"pathPrefix":{"type":"string","description":"Only changes where a path starts with this prefix."}}}"""),
            PayloadSchema = Obj("""
                {"type":"object","properties":{"paths":{"type":"array","items":{"type":"string"}},"count":{"type":"integer"}}}
                """),
            Matches = (arguments, data) => !HasArgument(arguments, "pathPrefix")
                || Argument(arguments, "pathPrefix") is { } prefix
                && data["paths"] is JsonArray paths && paths.Any(p => AsString(p)?.StartsWith(prefix, StringComparison.Ordinal) == true),
        });

        options.Define(new EventDefinition
        {
            Name = GuardianEventNames.RepoDependenciesChanged,
            Description = "A dependency manifest changed, in the working tree or in a new commit.",
            PayloadSchema = Obj("""
                {"type":"object","properties":{
                  "manifests":{"type":"array","items":{"type":"string"}},"ecosystems":{"type":"array","items":{"type":"string"}},
                  "commitSha":{"type":@NS@},"suggestedSkill":{"type":"string"}}}
                """),
        });

        options.Define(CheckEvent(GuardianEventNames.ChecksCompleted, "A check run finished.", withSuggestedSkill: false));
        options.Define(CheckEvent(GuardianEventNames.ChecksFailed, "A check run failed (checks.completed is published as well).", withSuggestedSkill: true));

        options.Define(new EventDefinition
        {
            Name = GuardianEventNames.SecuritySecretDetected,
            Description = "The secret scanner found secrets in a new commit or an explicit scan. Findings are redacted.",
            PayloadSchema = Obj("""
                {"type":"object","properties":{
                  "source":{"type":"string","enum":["commit","scan"]},"commitSha":{"type":@NS@},
                  "findings":{"type":"array","items":{"type":"object","properties":{
                    "ruleId":{"type":"string"},"path":{"type":"string"},"line":{"type":"integer"},"redacted":{"type":"string"}}}},
                  "suggestedSkill":{"type":"string"}}}
                """),
        });
    }

    /// <summary>The Epic 2 part of the catalog; registered only when the GitHub integration is enabled.</summary>
    public static void RegisterGitHub(EventsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        const string Requires = " (requires a GitHub token and a github.com origin)";

        options.Define(new EventDefinition
        {
            Name = GuardianEventNames.GithubIssueOpened,
            Description = "A new issue was opened on the GitHub repository." + Requires,
            InputSchema = Obj("""{"type":"object","properties":{"label":{"type":"string","description":"Only issues that carry this label."}}}"""),
            PayloadSchema = Obj("""
                {"type":"object","properties":{
                  "number":{"type":"integer"},"title":{"type":"string"},"author":{"type":"string"},"url":{"type":"string"},
                  "labels":{"type":"array","items":{"type":"string"}},"body":{"type":"string"},"suggestedSkill":{"type":"string"}}}
                """),
            Matches = (arguments, data) => !HasArgument(arguments, "label")
                || Argument(arguments, "label") is { } label
                && data["labels"] is JsonArray labels && labels.Any(l => AsString(l) == label),
        });

        options.Define(new EventDefinition
        {
            Name = GuardianEventNames.GithubPrCommentCreated,
            Description = "A comment was added to a pull request, as a conversation comment or a review comment." + Requires,
            InputSchema = Obj("""{"type":"object","properties":{"prNumber":{"type":"integer","description":"Only comments on this pull request."}}}"""),
            PayloadSchema = Obj("""
                {"type":"object","properties":{
                  "prNumber":{"type":"integer"},"commentId":{"type":"integer"},"author":{"type":"string"},"body":{"type":"string"},
                  "url":{"type":"string"},"path":{"type":@NS@},"line":{"type":["integer","null"]},"suggestedSkill":{"type":"string"}}}
                """),
            Matches = (arguments, data) => !HasArgument(arguments, "prNumber")
                || AsInteger(arguments!["prNumber"]) is { } number && AsInteger(data["prNumber"]) == number,
        });

        options.Define(new EventDefinition
        {
            Name = GuardianEventNames.GithubCiFailed,
            Description = "A GitHub Actions workflow run failed." + Requires,
            InputSchema = Obj("""{"type":"object","properties":{"branch":{"type":"string","description":"Only runs on this branch."}}}"""),
            PayloadSchema = Obj("""
                {"type":"object","properties":{
                  "runId":{"type":"integer"},"workflowName":{"type":"string"},"branch":{"type":"string"},"headSha":{"type":"string"},
                  "url":{"type":"string"},"conclusion":{"type":"string"},"suggestedSkill":{"type":"string"}}}
                """),
            Matches = (arguments, data) => !HasArgument(arguments, "branch")
                || Argument(arguments, "branch") is { } branch && AsString(data["branch"]) == branch,
        });
    }

    private static EventDefinition CheckEvent(string name, string description, bool withSuggestedSkill)
    {
        var schema = Obj("""
            {"type":"object","properties":{
              "runId":{"type":"string"},"command":{"type":"string"},"exitCode":{"type":["integer","null"]},
              "passed":{"type":"boolean"},"timedOut":{"type":"boolean"},"durationMs":{"type":"integer"},
              "summary":{"type":"string"},"failedTests":{"type":"array","items":{"type":"string"}},
              "logUri":{"type":"string"},"trigger":{"type":"string"},"commitSha":{"type":@NS@}}}
            """);
        if (withSuggestedSkill)
        {
            schema["properties"]!["suggestedSkill"] = new JsonObject { ["type"] = "string" };
        }

        return new EventDefinition { Name = name, Description = description, PayloadSchema = schema };
    }

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json.Replace("@NS@", """["string","null"]"""))!;

    private static bool HasArgument(JsonObject? arguments, string name) =>
        arguments is not null && arguments.TryGetPropertyValue(name, out var value) && value is not null;

    /// <summary>The argument as a string; null when it is missing or not a string (a non-string never matches).</summary>
    private static string? Argument(JsonObject? arguments, string name) =>
        arguments is not null && arguments.TryGetPropertyValue(name, out var value) ? AsString(value) : null;

    /// <summary>The node as an integer; null when it is not a JSON number with an integral value.</summary>
    private static long? AsInteger(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
        && long.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
