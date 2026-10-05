using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodebaseGuardian.Git;
using CodebaseGuardian.GitHub;
using CodebaseGuardian.Security;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodebaseGuardian.Tools;

/// <summary>
/// Outward-facing GitHub actions. They never run without the user's confirmation, and they must stay synchronous:
/// SDK 2.2.0 cannot combine MRTR with the Tasks extension.
/// </summary>
[McpServerToolType]
public sealed partial class GitHubTools(IGitHubClient github, ISecretScanner scanner, IActionConfirmation confirmation)
{
    private const int MaxTitle = 256;
    private const int MaxBody = 65_536;
    private const int MaxLabels = 10;
    private const int MaxLabel = 50;
    private const string Declined = "The user declined; nothing was sent to GitHub.";
    private const string ConfirmRequired =
        "This action writes to GitHub. Your client cannot show a confirmation prompt, so ask the user to approve it, then call again with confirm: true.";

    [McpServerTool(Name = "create_issue", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Creates an issue in the GitHub repository. Writes to GitHub. The user is asked to confirm; if your client cannot show prompts, ask the user yourself and pass confirm: true.")]
    public Task<CallToolResult> CreateIssue(
        McpServer server,
        RequestContext<CallToolRequestParams> context,
        [Description("Issue title, 1 to 256 characters.")] string title,
        [Description("Issue body (Markdown), up to 65536 characters.")] string body,
        [Description("Labels to apply, at most 10.")] string[]? labels = null,
        [Description("Pass true only after the user approved this action, when your client cannot show prompts.")] bool confirm = false,
        CancellationToken cancellationToken = default) =>
        RunGitHubAsync(async () =>
        {
            RequireLength(title, 1, MaxTitle, nameof(title));
            RequireLength(body, 0, MaxBody, nameof(body));
            var labelList = labels ?? [];
            if (labelList.Length > MaxLabels)
            {
                throw new ArgumentException($"At most {MaxLabels} labels are allowed.", nameof(labels));
            }

            foreach (var label in labelList)
            {
                RequireLength(label, 1, MaxLabel, nameof(labels));
            }

            OutboundTextGuard.EnsureNoSecrets(scanner, "title", title);
            OutboundTextGuard.EnsureNoSecrets(scanner, "body", body);
            var repo = await github.GetRepositoryAsync(cancellationToken);
            var summary = $"Create issue in {repo.Owner}/{repo.Name}: \"{OneLine(title)}\"" + (labelList.Length > 0 ? $" [labels: {string.Join(", ", labelList.Select(OneLine))}]" : "");
            if (Decision(server, context, summary, confirm) is { } stop)
            {
                return stop;
            }

            var issue = await github.CreateIssueAsync(title, body, labelList, cancellationToken);
            return Created(("number", issue.Number), ("url", issue.HtmlUrl));
        });

    [McpServerTool(Name = "comment_on_pr", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Adds a comment to a pull request (or issue) in the GitHub repository. Writes to GitHub. The user is asked to confirm; if your client cannot show prompts, ask the user yourself and pass confirm: true.")]
    public Task<CallToolResult> CommentOnPr(
        McpServer server,
        RequestContext<CallToolRequestParams> context,
        [Description("Pull request number, 1 or greater.")] int number,
        [Description("Comment body (Markdown), 1 to 65536 characters.")] string body,
        [Description("Pass true only after the user approved this action, when your client cannot show prompts.")] bool confirm = false,
        CancellationToken cancellationToken = default) =>
        RunGitHubAsync(async () =>
        {
            if (number < 1)
            {
                throw new ArgumentException("The number must be 1 or greater.", nameof(number));
            }

            RequireLength(body, 1, MaxBody, nameof(body));
            OutboundTextGuard.EnsureNoSecrets(scanner, "body", body);
            var repo = await github.GetRepositoryAsync(cancellationToken);
            var excerpt = Excerpt(OneLine(body), 120);
            var summary = $"Comment on {repo.Owner}/{repo.Name}#{number}: \"{excerpt}\"";
            if (Decision(server, context, summary, confirm) is { } stop)
            {
                return stop;
            }

            var comment = await github.CreateIssueCommentAsync(number, body, cancellationToken);
            return Created(("commentId", comment.Id), ("url", comment.HtmlUrl));
        });

    [McpServerTool(Name = "open_pull_request", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Opens a pull request from a branch that is already pushed to GitHub. Writes to GitHub. The user is asked to confirm; if your client cannot show prompts, ask the user yourself and pass confirm: true.")]
    public Task<CallToolResult> OpenPullRequest(
        McpServer server,
        RequestContext<CallToolRequestParams> context,
        [Description("The branch to merge (must already be pushed).")] string head,
        [Description("Pull request title, 1 to 256 characters.")] string title,
        [Description("Pull request body (Markdown), up to 65536 characters.")] string body,
        [Description("The branch to merge into; defaults to the repository's default branch.")] string? @base = null,
        [Description("Open as a draft pull request.")] bool draft = false,
        [Description("Pass true only after the user approved this action, when your client cannot show prompts.")] bool confirm = false,
        CancellationToken cancellationToken = default) =>
        RunGitHubAsync(async () =>
        {
            var source = GitRevision.Require(head ?? string.Empty, nameof(head));
            if (@base is not null)
            {
                GitRevision.Require(@base, nameof(@base));
            }

            if (source == @base)
            {
                throw new ArgumentException("The head and base branches must differ.", nameof(head));
            }

            RequireLength(title, 1, MaxTitle, nameof(title));
            RequireLength(body, 0, MaxBody, nameof(body));
            OutboundTextGuard.EnsureNoSecrets(scanner, "title", title);
            OutboundTextGuard.EnsureNoSecrets(scanner, "body", body);
            var repo = await github.GetRepositoryAsync(cancellationToken);
            var target = @base ?? await github.GetDefaultBranchAsync(cancellationToken);
            if (source == target)
            {
                throw new ArgumentException("The head and base branches must differ.", nameof(head));
            }

            if (!await github.BranchExistsAsync(source, cancellationToken))
            {
                throw new McpException($"Branch '{source}' is not on GitHub; push it first.");
            }

            var summary = $"Open pull request in {repo.Owner}/{repo.Name}: {OneLine(source)} → {OneLine(target)} \"{OneLine(title)}\"";
            if (Decision(server, context, summary, confirm) is { } stop)
            {
                return stop;
            }

            var pr = await github.CreatePullRequestAsync(source, target, title, body, draft, cancellationToken);
            return Created(("number", pr.Number), ("url", pr.HtmlUrl));
        });

    /// <summary>Null when confirmed; otherwise the result to return (declined) or a tool error.</summary>
    private CallToolResult? Decision(McpServer server, RequestContext<CallToolRequestParams> context, string summary, bool confirm) =>
        confirmation.Confirm(server, context, summary, confirm) switch
        {
            ConfirmationStatus.Confirmed => null,
            ConfirmationStatus.Declined => new CallToolResult
            {
                Content = [new TextContentBlock { Text = Declined }],
                StructuredContent = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["status"] = "declined" }),
            },
            _ => throw new McpException(ConfirmRequired),
        };

    private static CallToolResult Created(params (string Name, object Value)[] fields)
    {
        var content = new Dictionary<string, object> { ["status"] = "created" };
        foreach (var (name, value) in fields)
        {
            content[name] = value;
        }

        var json = JsonSerializer.SerializeToElement(content);
        return new CallToolResult { Content = [new TextContentBlock { Text = json.GetRawText() }], StructuredContent = json };
    }

    private static string OneLine(string text) => Whitespace().Replace(text, " ").Trim();

    private static string Excerpt(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        return char.IsHighSurrogate(text[max - 1]) ? text[..(max - 1)] : text[..max];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static async Task<CallToolResult> RunGitHubAsync(Func<Task<CallToolResult>> action)
    {
        try
        {
            return await ToolErrors.RunAsync(action);
        }
        catch (Exception ex) when (ex is GitHubUnavailableException or GitHubApiException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    private static void RequireLength(string? value, int min, int max, string parameter)
    {
        if (value is null || value.Length < min || value.Length > max)
        {
            throw new ArgumentException($"The {parameter} must be {min} to {max} characters.", parameter);
        }
    }

}
