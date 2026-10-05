using System.ComponentModel;
using CodebaseGuardian.Git;
using CodebaseGuardian.GitHub;
using CodebaseGuardian.Security;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodebaseGuardian.Tools;

public sealed record GitHubActionResult(string Status, int? Number, long? CommentId, string? Url, string? Message = null);

/// <summary>
/// Outward-facing GitHub actions. They never run without the user's confirmation, and they must stay synchronous:
/// SDK 2.2.0 cannot combine MRTR with the Tasks extension.
/// </summary>
[McpServerToolType]
public sealed class GitHubTools(IGitHubClient github, ISecretScanner scanner, IActionConfirmation confirmation)
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
    public Task<GitHubActionResult> CreateIssue(
        McpServer server,
        RequestContext<CallToolRequestParams> context,
        [Description("Issue title, 1 to 256 characters.")] string title,
        [Description("Issue body (Markdown), up to 65536 characters.")] string body,
        [Description("Labels to apply, at most 10.")] string[]? labels = null,
        [Description("Pass true only after the user approved this action, when your client cannot show prompts.")] bool confirm = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
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
            var summary = $"Create issue in {repo.Owner}/{repo.Name}: \"{title}\"" + (labelList.Length > 0 ? $" [labels: {string.Join(", ", labelList)}]" : "");
            if (Check(server, context, summary, confirm) is { } stop)
            {
                return stop;
            }

            var issue = await github.CreateIssueAsync(title, body, labelList, cancellationToken);
            return new GitHubActionResult("created", issue.Number, null, issue.HtmlUrl);
        });

    [McpServerTool(Name = "comment_on_pr", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Adds a comment to a pull request (or issue) in the GitHub repository. Writes to GitHub. The user is asked to confirm; if your client cannot show prompts, ask the user yourself and pass confirm: true.")]
    public Task<GitHubActionResult> CommentOnPr(
        McpServer server,
        RequestContext<CallToolRequestParams> context,
        [Description("Pull request number, 1 or greater.")] int number,
        [Description("Comment body (Markdown), 1 to 65536 characters.")] string body,
        [Description("Pass true only after the user approved this action, when your client cannot show prompts.")] bool confirm = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            if (number < 1)
            {
                throw new ArgumentException("The number must be 1 or greater.", nameof(number));
            }

            RequireLength(body, 1, MaxBody, nameof(body));
            OutboundTextGuard.EnsureNoSecrets(scanner, "body", body);
            var repo = await github.GetRepositoryAsync(cancellationToken);
            var excerpt = body.Length > 120 ? body[..120] : body;
            var summary = $"Comment on {repo.Owner}/{repo.Name}#{number}: \"{excerpt}\"";
            if (Check(server, context, summary, confirm) is { } stop)
            {
                return stop;
            }

            var comment = await github.CreateIssueCommentAsync(number, body, cancellationToken);
            return new GitHubActionResult("created", null, comment.Id, comment.HtmlUrl);
        });

    [McpServerTool(Name = "open_pull_request", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Opens a pull request from a branch that is already pushed to GitHub. Writes to GitHub. The user is asked to confirm; if your client cannot show prompts, ask the user yourself and pass confirm: true.")]
    public Task<GitHubActionResult> OpenPullRequest(
        McpServer server,
        RequestContext<CallToolRequestParams> context,
        [Description("The branch to merge (must already be pushed).")] string head,
        [Description("Pull request title, 1 to 256 characters.")] string title,
        [Description("Pull request body (Markdown), up to 65536 characters.")] string body,
        [Description("The branch to merge into; defaults to the repository's default branch.")] string? @base = null,
        [Description("Open as a draft pull request.")] bool draft = false,
        [Description("Pass true only after the user approved this action, when your client cannot show prompts.")] bool confirm = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
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

            var summary = $"Open pull request in {repo.Owner}/{repo.Name}: {head} → {target} \"{title}\"";
            if (Check(server, context, summary, confirm) is { } stop)
            {
                return stop;
            }

            var pr = await github.CreatePullRequestAsync(source, target, title, body, draft, cancellationToken);
            return new GitHubActionResult("created", pr.Number, null, pr.HtmlUrl);
        });

    private GitHubActionResult? Check(McpServer server, RequestContext<CallToolRequestParams> context, string summary, bool confirm) =>
        confirmation.Confirm(server, context, summary, confirm) switch
        {
            ConfirmationStatus.Confirmed => null,
            ConfirmationStatus.Declined => throw new DeclinedException(),
            _ => throw new McpException(ConfirmRequired),
        };

    private static async Task<GitHubActionResult> RunAsync(Func<Task<GitHubActionResult>> action)
    {
        try
        {
            return await ToolErrors.RunAsync(action);
        }
        catch (DeclinedException)
        {
            return new GitHubActionResult("declined", null, null, null, Declined);
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

    private sealed class DeclinedException : Exception;
}
