# Codebase Guardian — Backlog / Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Codebase Guardian MCP server: a Git-repository watcher that exposes tools, resources, Skills (`io.modelcontextprotocol/skills`), draft MCP Events (poll, push, webhook), and Tasks, on the MCP C# SDK 2.2.0.

**Architecture:** Two generic extension libraries (`Mcp.Skills`, `Mcp.Events`) plug into the SDK through `IConfigureOptions<McpServerOptions>` custom request handlers; the `CodebaseGuardian` app wires git access, a repository watcher, checks, secret scanning, dependency audit and GitHub integration onto them. stdio is the default transport; stateless Streamable HTTP is the second.

**Tech Stack:** .NET 10 (`net10.0`), C# latest, MCP C# SDK 2.2.0 (`ModelContextProtocol`, `ModelContextProtocol.AspNetCore`, `ModelContextProtocol.Extensions.Tasks`), YamlDotNet 18.1.0, xunit.v3 4.0.1 on Microsoft.Testing.Platform, git CLI.

**Spec:** `docs/specs/2026-10-04-codebase-guardian-design.md` (binding). Reference material every task may need:
- `docs/reference/skills-events-wire-format.md` — exact JSON shapes and MUST rules for Skills and Events (section numbers like "A5" / "B6" below refer to it).
- `docs/reference/sdk-2.2-notes.md` — verified SDK 2.2.0 behaviour and recipes (section numbers like "SDK §3" refer to it).

**Task format note:** tasks give exact files, interfaces, behaviour, test cases and acceptance commands, plus code where precision matters. They do not contain complete implementations; implementers write the code test-first from these descriptions.

## Global Constraints

- Target framework `net10.0` everywhere (set once in `Directory.Build.props`; do not override per project). Nullable warnings are errors.
- Package versions live only in `Directory.Packages.props` (central package management). Add a `PackageVersion` there before referencing a new package; prefer packages already listed. MCP SDK version is exactly `2.2.0`; do not add other MCP libraries.
- Namespaces: `Mcp.Skills` (src/Mcp.Skills), `Mcp.Events` (src/Mcp.Events), `CodebaseGuardian.<Folder>` (src/CodebaseGuardian/<Folder>), `CodebaseGuardian.Tests.<Folder>` (tests).
- `Mcp.Skills` and `Mcp.Events` never reference `CodebaseGuardian`.
- Every custom JSON-RPC result includes `"resultType": "complete"`. Never return `null` from a custom handler (SDK §2).
- JSON property names on the wire are camelCase exactly as in the reference docs. Timestamps are ISO 8601 UTC with `Z` (format `yyyy-MM-ddTHH:mm:ss.fffZ`).
- Tool names (`repo_status`, `recent_commits`, `diff_summary`, `run_checks`, `scan_secrets`, `audit_dependencies`, `poll_events`, `create_issue`, `comment_on_pr`, `open_pull_request`, `full_scan`), resource URIs (`guardian://…`), event names and skill names are the contract in spec §5 — spell them exactly.
- No shell execution: every external process (`git`, `dotnet`, `npm`, `gh`) goes through `IProcessRunner` with an argument list. Never pass model-supplied strings as options (validate revisions; reject values starting with `-`).
- stdout is reserved for MCP on stdio; all logging goes to stderr.
- Secret values are never logged, returned, or stored unredacted.
- Tests: xunit.v3; `dotnet test` from the repo root must pass with no network access. Git tests use real temporary repositories via the `TempGitRepo` fixture. Use `TestContext.Current.CancellationToken` for test cancellation tokens.
- Use `TimeProvider` (injected, default `TimeProvider.System`) for anything time-dependent that tests must control.
- Each task ends with all tests green (`dotnet test` at repo root) and one or more commits on the current branch. Commit messages: Conventional Commits, ending with the trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push.
- Do not edit files owned by other tasks beyond what the task lists; if something outside scope is broken, report it.

## Review Focus

Inputs the spec implies but does not spell out, most likely to bite a real user first. Each has a pinning test in the owning task.

1. **Brand-new repository with no commits** (unborn `main`): `repo_status`, `recent_commits`, `diff_summary` and the watcher must work, not error; the first commit is announced. Tests: Task 3, Task 9.
2. **History rewritten or branch deleted while watching** (`git commit --amend`, rebase, `git branch -D`): the watcher keeps running, announces the rewritten commit once, and never crashes on a vanished ref. Test: Task 9.
3. **Paths with spaces and non-ASCII characters** (`docs/naïve file.md`): reported verbatim with `/` separators in status, diffs and file lists. Test: Task 2.
4. **The model passes `confirm: true` while the client can show prompts**: the user is still asked; the flag only counts when no prompt is possible. Test: Task 17.
5. **A task-mode `run_checks` is cancelled mid-run**: the process is killed, nothing is recorded, and the next run is not blocked by the one-at-a-time lock. Test: Task 22.

## Task order and dependencies

| Epic | Tasks |
|---|---|
| 1 — Core Guardian (local) | 1 Hosting + harness → 2 Process runner + git → 3 Repo tools/resources → 4 Skills catalog → 5 Skills protocol → 6 Events core → 7 Events list/poll → 8 Events stream → 9 Watcher + poll_events → 10 Checks → 11 Secret scanner → 12 Dependency audit → 13 Skill content + instructions → 14 Stateless HTTP + conformance → 15 Demo client + README + E2E |
| 2 — GitHub + webhooks | 16 GitHub client → 17 Action tools + confirmation → 18 GitHub poller → 19 HTTP auth + principal → 20 Webhook subscriptions → 21 Webhook dispatcher |
| 3 — Tasks | 22 Tasks extension → 23 full_scan + docs |

Tasks run strictly in this order.

---

## Epic 1 — Core Guardian (local)

### Task 1: Hosting, configuration, and in-process test harness

**Goal:** A runnable stdio server built through one composition root, configurable from CLI/env/appsettings, plus reusable test infrastructure (in-process MCP client/server pair, temporary git repositories) that every later task uses.

**Files:**
- Create: `src/CodebaseGuardian/Hosting/GuardianOptions.cs`
- Create: `src/CodebaseGuardian/Hosting/GuardianCommandLine.cs`
- Create: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs`
- Create: `src/CodebaseGuardian/Hosting/IInstructionsContributor.cs`
- Create: `src/CodebaseGuardian/Hosting/GuardianInstructions.cs`
- Modify: `src/CodebaseGuardian/Program.cs`
- Modify: `src/CodebaseGuardian/CodebaseGuardian.csproj` (NoWarn for MCP experimental IDs actually reported)
- Create: `tests/CodebaseGuardian.Tests/Infrastructure/InProcessMcpServer.cs`
- Create: `tests/CodebaseGuardian.Tests/Infrastructure/GuardianTestHost.cs`
- Create: `tests/CodebaseGuardian.Tests/Infrastructure/TempGitRepo.cs`
- Create: `tests/CodebaseGuardian.Tests/App/HostingTests.cs`
- Create: `tests/CodebaseGuardian.Tests/App/GuardianCommandLineTests.cs`
- Delete: `tests/CodebaseGuardian.Tests/ScaffoldTests.cs`

**Interfaces (Produces):**

```csharp
namespace CodebaseGuardian.Hosting;

public enum GuardianTransport { Stdio, Http }

public sealed class GuardianOptions
{
    public const string SectionName = "Guardian";
    public string RepositoryPath { get; set; } = Directory.GetCurrentDirectory(); // normalized to a full path after binding
    public GuardianTransport Transport { get; set; } = GuardianTransport.Stdio;
    public string HttpUrl { get; set; } = "http://127.0.0.1:5199";
    public bool AutoChecks { get; set; }
    public bool WatchEnabled { get; set; } = true;
    public int WatchIntervalMs { get; set; } = 2000;
    public int FileChangeDebounceMs { get; set; } = 2000;
    public string? SkillsDirectory { get; set; }   // null => Path.Combine(AppContext.BaseDirectory, "skills")
}

public static class GuardianCommandLine
{
    // Maps --repo, --transport, --urls, --auto-checks, --skills-dir, --no-watch to Guardian:* keys.
    // Bare flags (--auto-checks, --no-watch) become "true"/"false" values. Unknown args pass through unchanged.
    public static IReadOnlyDictionary<string, string> SwitchMappings { get; }
    public static string[] Normalize(string[] args);
}

public interface IInstructionsContributor
{
    int Order { get; }            // lower first
    string? GetSection();         // null/empty => skipped
}

public static class GuardianServiceCollectionExtensions
{
    // Binds GuardianOptions from configuration section "Guardian", validates on start, registers
    // core services and the MCP server (ServerInfo name "codebase-guardian", version = assembly informational version),
    // composes ServerInstructions from all IInstructionsContributor services. Does NOT choose a transport.
    public static IMcpServerBuilder AddCodebaseGuardian(this IServiceCollection services, IConfiguration configuration);
}
```

Test infrastructure (namespace `CodebaseGuardian.Tests.Infrastructure`):

```csharp
public sealed class InProcessMcpServer : IAsyncDisposable
{
    // Generic: builds a Host with a pipe-based stream transport, lets the caller register anything,
    // starts it (hosted services run), connects an McpClient. Used directly by library tests.
    public static Task<InProcessMcpServer> StartAsync(
        Action<IServiceCollection, IConfiguration, IMcpServerBuilder>? configure = null,  // builder already has stream transport
        IDictionary<string, string?>? configuration = null,
        McpClientOptions? clientOptions = null,
        CancellationToken cancellationToken = default);
    public McpClient Client { get; }
    public IServiceProvider Services { get; }
    // Sends a raw request; returns result JsonObject; protocol errors surface as McpProtocolException.
    public Task<JsonObject> RequestAsync(string method, JsonObject? parameters = null, CancellationToken cancellationToken = default);
}

public static class GuardianTestHost
{
    // Full app wiring (AddCodebaseGuardian) on top of InProcessMcpServer, pointed at repositoryPath.
    // Extra configuration overrides Guardian:* keys; configureServices runs after app registration (for fakes).
    public static Task<InProcessMcpServer> StartAsync(
        string repositoryPath,
        IDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? configureServices = null,
        McpClientOptions? clientOptions = null,
        CancellationToken cancellationToken = default);
}

public sealed class TempGitRepo : IDisposable
{
    public static TempGitRepo Create();                // temp dir, `git init -b main`, user.name/email set, commit.gpgsign=false
    public string Path { get; }
    public void WriteFile(string relativePath, string content);   // creates directories
    public void DeleteFile(string relativePath);
    public string Commit(string message);              // `git add -A` + commit; returns full SHA
    public string Git(params string[] args);           // runs git in Path; returns trimmed stdout; throws with stderr on non-zero exit
    public void Dispose();                             // deletes directory (clear read-only attributes first)
}
```

**Behaviour:**
- `Program.cs`: `GuardianCommandLine.Normalize(args)` → configuration (appsettings optional, env vars with `GUARDIAN__` prefix mapping to `Guardian:*`, command line with switch mappings). If `Transport == Stdio`: `Host.CreateApplicationBuilder`, console logging to stderr (`LogToStandardErrorThreshold = LogLevel.Trace`), `AddCodebaseGuardian(...).WithStdioServerTransport()`. If `Http`: write `HTTP transport is not available yet (Task 14).` to stderr and exit with code 2 (Task 14 replaces this branch).
- Option validation (fail fast at startup with a message naming the problem): `RepositoryPath` must exist as a directory; `WatchIntervalMs` ≥ 100; `FileChangeDebounceMs` ≥ 50.
- `GuardianInstructions` contributes a base section (Order 0): `Codebase Guardian is watching the Git repository at <full path>.` plus one sentence on what the server offers. Final instructions = sections in `Order`, joined with a blank line.
- `InProcessMcpServer` uses `Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true })`, in-memory configuration, logging to nothing (or xunit output if trivial), `services.AddMcpServer(...)`-based registration supplied by `configure`, `WithStreamServerTransport(c2s.Reader.AsStream(), s2c.Writer.AsStream())` (SDK §10), `await host.StartAsync()`, then `McpClient.CreateAsync(new StreamClientTransport(...), clientOptions)`. `DisposeAsync` disposes client, stops and disposes host. If `configure` is null it registers a bare `AddMcpServer()`.
  - Implementation hint: because `configure` receives an `IMcpServerBuilder`, `InProcessMcpServer` must create the builder itself (`services.AddMcpServer()`), apply the stream transport, then call `configure`. `GuardianTestHost` cannot use that builder (it needs `AddCodebaseGuardian`) — give `InProcessMcpServer` an internal/overload entry point that accepts a `Func<IServiceCollection, IConfiguration, IMcpServerBuilder>` builder factory; keep the public shape above for library tests.
- `GuardianTestHost` sets `Guardian:RepositoryPath` to the given path, `Guardian:WatchEnabled=false` unless the caller overrides it, and `Guardian:SkillsDirectory` to the built app's skills folder when it exists (Task 13 creates it).

**Tests (write first):**
- `GuardianCommandLineTests`: `--repo /x --transport http --urls http://127.0.0.1:6000` → `Guardian:RepositoryPath`, `Guardian:Transport`, `Guardian:HttpUrl`; `--auto-checks` → `Guardian:AutoChecks=true`; `--no-watch` → `Guardian:WatchEnabled=false`; unknown args preserved in order.
- `HostingTests`:
  - `Server_reports_name_and_version` — `client.ServerInfo.Name == "codebase-guardian"`, version non-empty.
  - `Instructions_mention_repository_path` — `client.ServerInstructions` contains the temp repo's full path.
  - `Instructions_compose_contributors_in_order` — register two fake `IInstructionsContributor`s (Order 20 "B", Order 10 "A") via `configureServices`; instructions contain "A" before "B".
  - `Startup_fails_for_missing_repository_path` — starting with a non-existent path throws (`OptionsValidationException` or the exception your validation uses) whose message names `RepositoryPath`.
  - `TempGitRepo_commits_files` — write + commit returns a 40-hex SHA; `Git("log","--oneline")` has one line.

**Acceptance:**
- [ ] `dotnet build` clean (no new warnings except documented NoWarn IDs), `dotnet test` green.
- [ ] `dotnet run --project src/CodebaseGuardian -- --repo .` starts and waits on stdin (stop with Ctrl+C); `--transport http` exits with code 2 and the message above.
- [ ] Commit: `feat(hosting): composition root, options, and in-process test harness`

### Task 2: Process runner and Git access layer

**Goal:** Safe external-process execution and a typed git CLI wrapper used by every repo feature.

**Files:**
- Create: `src/CodebaseGuardian/Processes/ProcessSpec.cs`, `ProcessResult.cs`, `IProcessRunner.cs`, `ProcessRunner.cs`, `ExecutableNotFoundException.cs`
- Create: `src/CodebaseGuardian/Git/IGitRepository.cs`, `GitRepository.cs`, `GitModels.cs`, `GitException.cs`, `GitRevision.cs`
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs` (register `IProcessRunner` → `ProcessRunner`, `IGitRepository` → `GitRepository`, both singletons)
- Create: `tests/CodebaseGuardian.Tests/App/ProcessRunnerTests.cs`, `tests/CodebaseGuardian.Tests/App/GitRepositoryTests.cs`

**Interfaces (Produces):**

```csharp
namespace CodebaseGuardian.Processes;

public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);
    public int MaxOutputBytes { get; init; } = 1_048_576;          // per stream; excess dropped, OutputTruncated=true
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError,
    bool TimedOut, bool OutputTruncated, TimeSpan Duration);

public interface IProcessRunner
{
    // Never throws for non-zero exit codes. Throws ExecutableNotFoundException when the executable cannot be started,
    // OperationCanceledException when ct is cancelled (process tree killed first).
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default);
}

public sealed class ExecutableNotFoundException(string fileName, Exception inner)
    : Exception($"Executable '{fileName}' could not be started; is it installed and on PATH?", inner)
{
    public string FileName { get; } = fileName;
}
```

```csharp
namespace CodebaseGuardian.Git;

public sealed record FileChange(string Path, string Kind);   // Kind: "added","modified","deleted","renamed","copied","type-changed","unmerged"
public sealed record RepoStatus(string? Branch, string? HeadSha, string? Upstream, int Ahead, int Behind,
    IReadOnlyList<FileChange> Staged, IReadOnlyList<FileChange> Unstaged, IReadOnlyList<string> Untracked, IReadOnlyList<string> Conflicted);
public sealed record CommitInfo(string Sha, string ShortSha, string AuthorName, string AuthorEmail,
    DateTimeOffset CommittedAt, string Subject, IReadOnlyList<string> ParentShas);
public sealed record FileDiffStat(string Path, int? Insertions, int? Deletions, bool Binary);   // null counts for binary
public sealed record DiffSummary(string From, string To, IReadOnlyList<FileDiffStat> Files,
    int Insertions, int Deletions, string Patch, bool PatchTruncated);

public sealed class GitException(string message, int exitCode, string standardError) : Exception(message)
{
    public int ExitCode { get; }
    public string StandardError { get; }
}

public static class GitRevision
{
    // Accepts branch/tag names, SHAs, HEAD~n, HEAD^, A..B is NOT accepted here (callers pass from/to separately).
    // Regex ^[A-Za-z0-9_./~^@{}-]+$, max 200 chars, must not start with '-', must not contain "..".
    public static bool IsValid(string revision);
    public static string Require(string revision, string parameterName); // throws ArgumentException naming the parameter
}

public interface IGitRepository
{
    string RootPath { get; }    // full path from GuardianOptions.RepositoryPath
    Task<RepoStatus> GetStatusAsync(CancellationToken ct = default);
    Task<string?> GetCurrentBranchAsync(CancellationToken ct = default);          // null when detached or unborn
    Task<string?> GetHeadShaAsync(CancellationToken ct = default);                // null when repository has no commits
    Task<IReadOnlyDictionary<string, string>> GetBranchHeadsAsync(CancellationToken ct = default);  // refs/heads/<name> -> sha
    Task<IReadOnlyList<CommitInfo>> GetRecentCommitsAsync(int limit, string? revision = null, CancellationToken ct = default); // newest first
    // Commits reachable from tip but from none of excludeTips; OLDEST first; at most limit (keeps the newest `limit` if more).
    Task<IReadOnlyList<CommitInfo>> GetNewCommitsAsync(string tip, IReadOnlyCollection<string> excludeTips, int limit, CancellationToken ct = default);
    // (null,null) = working tree incl. staged vs HEAD; (from,null) = working tree vs from; (from,to) = from vs to. Untracked files excluded.
    Task<DiffSummary> GetDiffSummaryAsync(string? from, string? to, int maxPatchBytes, CancellationToken ct = default);
    Task<DiffSummary> GetCommitDiffAsync(string sha, int maxPatchBytes, CancellationToken ct = default); // works for root commits
    Task<DiffSummary> GetStagedDiffAsync(int maxPatchBytes, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken ct = default);   // tracked + untracked-not-ignored, '/' separators
    Task<string?> GetRemoteUrlAsync(string remote = "origin", CancellationToken ct = default); // null when no such remote
}
```

**Behaviour:**
- `ProcessRunner`: `ProcessStartInfo` with `ArgumentList`, `UseShellExecute=false`, redirected stdout/stderr read concurrently (UTF-8), stdin closed. On timeout: kill entire tree (`Kill(entireProcessTree: true)`), `TimedOut=true`, `ExitCode=-1`. On cancellation: kill tree then throw `OperationCanceledException`. `Win32Exception` on start → `ExecutableNotFoundException`.
- `GitRepository` runs `git -C <RootPath> -c core.quotepath=off -c color.ui=never <args>`; non-zero exit → `GitException` with stderr (except where a method defines null results). Parsing:
  - status: `status --porcelain=v2 --branch -z` (handle `# branch.*` headers, ordinary `1`, renamed `2`, unmerged `u`, untracked `?`).
  - commits: `log --format=%H%x1f%h%x1f%an%x1f%ae%x1f%cI%x1f%s%x1f%P%x1e` (+ `-n <limit>`); new commits: `rev-list`/`log <tip> --not <exclude...> --reverse` with limit applied to the newest.
  - diffs: `diff --numstat -z` + `diff --patch` (patch cut at `maxPatchBytes` UTF-8 bytes, `PatchTruncated`); commit diffs via `show --format= --numstat` / `show --format= --patch` (root commits work with `show`); staged via `diff --cached`.
  - `ListFilesAsync`: `ls-files -co --exclude-standard -z`.
  - Validate every revision with `GitRevision.Require`.
- Paths in results use `/` separators relative to the repo root, verbatim (spaces and non-ASCII preserved; `-z` output is never unquoted or re-encoded).
- Unborn repository (no commits yet): `GetRecentCommitsAsync` and `GetBranchHeadsAsync` return empty (do not surface git's "does not have any commits yet" as an exception); `GetDiffSummaryAsync(null, null)` returns the staged changes (`diff --cached`, which compares against the empty tree) with `From = "(empty)"`; `GetDiffSummaryAsync` with an explicit `from` still fails with `GitException`.

**Tests (write first):**
- `ProcessRunnerTests`: runs `git --version` (exit 0, stdout starts with "git version"); non-zero exit returned not thrown (`git not-a-command`); missing executable → `ExecutableNotFoundException`; timeout kills a sleeping process (macOS/Linux `sleep 5`, Windows `ping -n 6 127.0.0.1`, chosen by `OperatingSystem.IsWindows()`) with `Timeout = 200ms` → `TimedOut=true` within 3 s; cancellation of the same command throws `OperationCanceledException`; `MaxOutputBytes = 10` with `git --version` → `OutputTruncated=true`.
- `GitRepositoryTests` (each with `TempGitRepo`):
  - empty repo: `GetHeadShaAsync` null, `GetCurrentBranchAsync` null or "main" (assert what unborn branch reports and document it); `GetRecentCommitsAsync(10)` and `GetBranchHeadsAsync` empty; a staged file shows up in `GetDiffSummaryAsync(null, null)` with `From == "(empty)"`.
  - path with a space and non-ASCII characters (`docs/naïve file.md`): appears verbatim as untracked in status, in `ListFilesAsync`, and (after commit) in `GetCommitDiffAsync` file stats.
  - status: staged/unstaged/untracked/renamed classification; branch name; HeadSha.
  - `GetRecentCommitsAsync(2)` newest first with parsed author/email/subject/parents; `CommittedAt` parsed with offset.
  - `GetNewCommitsAsync`: three commits on a feature branch excluded from main tip → oldest-first list; limit 2 keeps newest two.
  - `GetBranchHeadsAsync` lists `main` and `feature`.
  - `GetDiffSummaryAsync(null,null)` reports modified file insertions/deletions; binary file → `Binary=true`, null counts; patch truncation at small `maxPatchBytes`.
  - `GetCommitDiffAsync` on the root commit lists its files.
  - `GetStagedDiffAsync` sees only staged changes.
  - `ListFilesAsync` includes untracked, excludes `.gitignore`d files.
  - `GetRemoteUrlAsync` returns null without remote; returns URL after `git remote add origin https://github.com/acme/widgets.git`.
  - invalid revision `--upload-pack=x` → `ArgumentException`; non-repo directory → `GitException`.

**Acceptance:**
- [ ] `dotnet test` green.
- [ ] Commit: `feat(git): process runner and git CLI repository layer`

### Task 3: Repository tools and resources

**Goal:** First user-visible features: `repo_status`, `recent_commits`, `diff_summary` tools and `guardian://repo/status`, `guardian://repo/commits/recent` resources.

**Files:**
- Create: `src/CodebaseGuardian/Tools/RepositoryTools.cs`
- Create: `src/CodebaseGuardian/Resources/RepositoryResources.cs`
- Create: `src/CodebaseGuardian/Tools/ToolErrors.cs` (helper mapping `GitException`/`ArgumentException` to MCP tool errors)
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs` (`.WithTools<RepositoryTools>()`, `.WithResources<RepositoryResources>()`)
- Create: `tests/CodebaseGuardian.Tests/App/RepositoryToolsTests.cs`, `tests/CodebaseGuardian.Tests/App/RepositoryResourcesTests.cs`

**Interfaces:** Consumes `IGitRepository` (Task 2). Produces tool names `repo_status`, `recent_commits`, `diff_summary`; resource URIs `guardian://repo/status`, `guardian://repo/commits/recent`.

**Behaviour:**
- Tools use `[McpServerToolType]`/`[McpServerTool(Name = ..., ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]` with `[Description]`s that tell the model when to use each tool.
  - `repo_status()` → `RepoStatus` serialized camelCase.
  - `recent_commits(limit = 10, branch = null)`: limit clamped to 1..100; `branch` validated by `GitRevision`.
  - `diff_summary(from = null, to = null, maxPatchBytes = 20000)`: clamp maxPatchBytes to 0..200000; `to` without `from` → tool error "`to` requires `from`".
- Expected failures (invalid args, git errors) become tool errors (`isError: true`) with a readable message, not protocol errors. Use `McpException` (the SDK converts it into an error result) or return `CallToolResult { IsError = true }` — pick one and use it consistently via `ToolErrors`.
- Resources (`[McpServerResourceType]`, `MimeType = "application/json"`): status → same JSON as `repo_status`; commits/recent → last 20 commits.

**Tests (write first)** — use `GuardianTestHost` on a `TempGitRepo`:
- `tools/list` contains the three tools with read-only annotations.
- `repo_status` on a repo with one staged and one untracked file → structured content has `branch == "main"`, staged count 1, untracked contains the file.
- `recent_commits` with `limit: 1` returns the newest subject; `limit: 1000` returns at most 100; `branch: "--all"` → `isError: true`.
- `diff_summary` after modifying a tracked file reports insertions; `to` without `from` → error.
- brand-new repo (`TempGitRepo.Create()` with no commit): `repo_status` succeeds with `headSha == null`; `recent_commits` returns an empty list; `diff_summary` with a staged file lists it — none of them `isError`.
- Resources: `resources/list` includes both URIs with `application/json`; reading `guardian://repo/status` returns JSON parseable to an object with `headSha`.

**Acceptance:**
- [ ] `dotnet test` green.
- [ ] Commit: `feat(tools): repository status, commits and diff tools plus repo resources`

### Task 4: Mcp.Skills — skill catalog (loading, validation, integrity)

**Goal:** A pure, well-tested in-memory catalog of skills loaded from directories, with digests and frontmatter exactly as SEP-2640 requires. No MCP wiring yet.

**Files:**
- Create: `src/Mcp.Skills/SkillsOptions.cs`, `SkillCatalog.cs`, `SkillDefinition.cs`, `SkillFile.cs`, `SkillDirectoryEntry.cs`, `SkillValidationException.cs`, `FrontmatterParser.cs`, `SkillNameRules.cs`, `MimeTypes.cs`
- Create: `tests/CodebaseGuardian.Tests/Skills/SkillCatalogTests.cs`, `tests/CodebaseGuardian.Tests/Skills/FrontmatterParserTests.cs`, `tests/CodebaseGuardian.Tests/Skills/SkillFixture.cs` (helper writing skill trees into a temp directory)

**Interfaces (Produces):**

```csharp
namespace Mcp.Skills;

public sealed class SkillsOptions
{
    public IList<string> Directories { get; } = new List<string>();   // roots; each SKILL.md below a root defines a skill
    public string UriScheme { get; set; } = "skill";
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(5);  // ttlMs for skills/list, skills/get, resources/read
}

public sealed record SkillFile(string Uri, string RelativePath, byte[] Content, string Digest, long Size, string MimeType, bool IsUtf8Text);

public sealed record SkillDefinition(
    string Uri,            // skill://<skill-path>/SKILL.md
    string RootUri,        // skill://<skill-path>
    string SkillPath,      // e.g. "bug-triage" or "acme/billing/refunds"
    string Name,
    string Description,
    JsonObject Frontmatter,             // verbatim YAML -> JSON (deep clone before handing out)
    IReadOnlyList<SkillFile> Files);    // complete: SKILL.md + every supporting file + nested skills' files, sorted by Uri (ordinal)

public sealed record SkillDirectoryEntry(string Uri, string Name, bool IsDirectory, string MimeType, long? Size); // directories: MimeType "inode/directory", Size null

public sealed class SkillValidationException(IReadOnlyList<string> errors)
    : Exception("Invalid skills:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "- " + e)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed class SkillCatalog
{
    public static SkillCatalog Load(SkillsOptions options);           // throws SkillValidationException with ALL problems found
    public IReadOnlyList<SkillDefinition> Skills { get; }             // sorted by Uri (ordinal)
    public SkillDefinition? FindSkill(string skillMdUri);             // exact match on SkillDefinition.Uri
    public SkillFile? FindFile(string uri);                           // any file of any skill
    public bool TryListDirectory(string directoryUri, out IReadOnlyList<SkillDirectoryEntry> children); // direct children only, sorted by Uri
}

public static class FrontmatterParser
{
    // Extracts the leading '---' YAML block (tolerates a UTF-8 BOM and CRLF). Throws FormatException on missing/invalid frontmatter.
    public static JsonObject Parse(string skillMarkdown);
}
```

**Behaviour (normative refs: wire-format A2, A3, A4, A9, A10):**
- Discovery: every file named exactly `SKILL.md` below a root defines a skill; `SkillPath` = its directory relative to the root with `/` separators. Skill directories at the root itself (a `SKILL.md` directly in the root) are invalid (no name segment).
- Files: every regular file under the skill directory, recursively, including nested skills' files; skip hidden files and directories (name starts with `.`); reject symbolic links (validation error) — never follow them.
- Validation errors (collect all, then throw): missing/invalid frontmatter; `name` missing, not matching `^(?!.*--)[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$`, or not equal to the last `SkillPath` segment; every `SkillPath` segment must match the same regex; `description` missing, empty, or > 1024 chars; `compatibility` present but not 1..500 chars; file path segments must match `^[A-Za-z0-9._-]+$`; > 512 files or total size > 16 MiB per skill; YAML aliases/anchors present.
- Frontmatter → JSON with YAML 1.2 core-schema typing: plain scalars `null`/`~`/empty → null, `true`/`false` → bool, integers/floats (core-schema regexes) → number, everything else string; quoted scalars → string; sequences → arrays; mappings → objects with string keys. Key order preserved.
- Integrity: `Digest = "sha256:" + lowercase hex(SHA256(Content))`, `Size = Content.Length`; bytes are read once and kept; nothing normalizes line endings or BOMs.
- `IsUtf8Text`: strict UTF-8 decode (`new UTF8Encoding(false, throwOnInvalidBytes: true)`) succeeds and re-encoding yields identical bytes.
- `MimeTypes`: `.md` text/markdown, `.txt` text/plain, `.json` application/json, `.yaml`/`.yml` application/yaml, `.py` text/x-python, `.sh` text/x-shellscript, `.cs` text/x-csharp, `.js` text/javascript, `.ts` text/typescript, `.csv` text/csv, `.html` text/html, `.png` image/png, `.svg` image/svg+xml; otherwise application/octet-stream.
- Directory URIs: skill root `skill://<skill-path>` and each subdirectory `skill://<skill-path>/<dir>`, never a trailing slash. `TryListDirectory` returns false for unknown URIs and for file URIs.
- Multiple roots are merged; duplicate skill URIs across roots → validation error.

**Tests (write first)** — `SkillFixture` writes trees under a temp dir:
- single skill `git-workflow` with `SKILL.md` only → one skill, URI `skill://git-workflow/SKILL.md`, root `skill://git-workflow`, Files has exactly the self entry with digest equal to an independently computed SHA-256 of the bytes.
- Reproduce the spec example: SKILL.md content `"---\nname: pdf-processing\ndescription: Extract, fill, and assemble PDF documents\n---\n\n# PDF processing\n\nChoose the matching template from `templates/`.\n"` → size 151 and digest `sha256:99b737495721155ece826d57521e2d66141ebdc1344a400487481ea2642ab19e`.
- prefix path `acme/billing/refunds` → name `refunds`, URI `skill://acme/billing/refunds/SKILL.md`.
- nested skill `outer/inner` (both have SKILL.md) → two skills; outer.Files contains inner's files.
- frontmatter verbatim: extra fields (`license`, `metadata: {team: core}`, `version: 1.0` → number 1.0, `quoted: "1.0"` → string, `flag: true` → bool, `empty:` → null) all present with those JSON types.
- invalid names each produce an error: `PDF-Processing`, `-pdf`, `pdf--processing`, 65 chars, name ≠ directory.
- missing description, 1025-char description, missing frontmatter → errors; one `Load` reports multiple problems at once.
- CRLF SKILL.md: digest over raw CRLF bytes; `IsUtf8Text` true.
- non-UTF-8 file (bytes 0xFF 0xFE 0x00) → `IsUtf8Text` false, mime from extension.
- hidden `.DS_Store` ignored; symlink rejected (skip test on platforms where creating symlinks fails).
- directory listing: root lists `SKILL.md` (file) and `references` (inode/directory); `references` lists its files; listing `SKILL.md` URI → false; unknown → false.
- limits: 513 files → error (generate tiny files).

**Acceptance:**
- [ ] `dotnet test` green.
- [ ] Commit: `feat(skills): skill catalog with validation and sha256 manifests`

### Task 5: Mcp.Skills — protocol surface (`skills/list`, `skills/get`, `resources/directory/read`, `resources/read`)

**Goal:** Expose the catalog over MCP exactly per SEP-2640 via a `WithSkills` builder extension that composes with other resources.

**Files:**
- Create: `src/Mcp.Skills/SkillsProtocol.cs` (constants: extension id `io.modelcontextprotocol/skills`, method names, `inode/directory`)
- Create: `src/Mcp.Skills/SkillsServerBuilderExtensions.cs`
- Create: `src/Mcp.Skills/SkillsConfigureOptions.cs` (internal `IConfigureOptions<McpServerOptions>`)
- Create: `src/Mcp.Skills/SkillJson.cs` (entry/result JSON builders)
- Modify: `src/Mcp.Skills/Mcp.Skills.csproj` (NoWarn for reported MCP experimental IDs)
- Create: `tests/CodebaseGuardian.Tests/Skills/SkillsProtocolTests.cs`

**Interfaces:**
- Consumes: `SkillCatalog`, `SkillsOptions` (Task 4); `InProcessMcpServer` (Task 1).
- Produces:

```csharp
namespace Mcp.Skills;
public static class SkillsServerBuilderExtensions
{
    // Registers SkillsOptions (configure callback), SkillCatalog singleton (loaded eagerly when MCP options are built,
    // so invalid skills fail server startup), and the protocol handlers.
    public static IMcpServerBuilder WithSkills(this IMcpServerBuilder builder, Action<SkillsOptions> configure);
}
```

**Behaviour (wire-format A1, A4–A8, A11–A14; spec rulings R3, R7, R8):**
- Capability: `capabilities.resources` present (set `options.Capabilities.Resources ??= new()`), and `capabilities.extensions["io.modelcontextprotocol/skills"] = {"directoryRead": true}` (a `JsonObject`, inline, no envelope).
- Custom handlers registered through `McpServerOptions.RequestHandlers` from an `IConfigureOptions<McpServerOptions>` (SDK §2 pattern). No `RoutingNameParameter`.
  - `skills/list`: ignores `cursor`; result `{"resultType":"complete","skills":[entry...],"ttlMs":<CacheTtl ms>,"cacheScope":"public"}`; never `nextCursor`.
  - entry = `{"uri": <SKILL.md uri>, "frontmatter": <deep clone>, "resources": [{"uri","digest","size"}...]}` — no other fields.
  - `skills/get`: `params.uri` must be a string, else `-32602`; unknown/non-skill URI (root dir URI, supporting file, anything not a served `SKILL.md`) → `McpProtocolException("No skill is served at <uri>", McpErrorCode.InvalidParams)`; result `{"resultType":"complete","skill":entry,"ttlMs":…,"cacheScope":"public"}` with `skill.uri` equal to the requested URI; no `nextCursor`.
  - `resources/directory/read`: `params.uri` string required; known directory → `{"resultType":"complete","resources":[{"uri","name","mimeType","size"?}...]}` (`name` = last segment; directories `mimeType: "inode/directory"` and no size); otherwise `-32602` with message `"<uri> is not a directory resource"`.
- `resources/list` and `resources/read` via request filters (`options.Filters.Request.ListResourcesFilters` / `ReadResourceFilters`; see `McpRequestFilters.cs` in the SDK for the delegate shape) so they compose with app resources:
  - list filter: call `next`, then append one `Resource` per skill `SKILL.md` (`Uri`, `Name` = frontmatter name, `Description`, `MimeType = "text/markdown"`, `Size`). Supporting files are not listed.
  - read filter: if the URI uses the skills scheme → serve from catalog: exactly one content item with `Uri` = requested URI, `MimeType` from the file, `Text` when `IsUtf8Text` else `Blob` (base64 of raw bytes); set `TimeToLive = CacheTtl`, `CacheScope = Public`; unknown skill URI → `McpProtocolException("Resource not found: <uri>", McpErrorCode.InvalidParams)` with `Data["uri"] = uri`. Other schemes → `next`.
- Must work in a server that has no other resources (capability set ⇒ SDK installs default list/read handlers that the filters wrap — verify with a test) and in a server that also has attribute-based resources.

**Tests (write first)** — `InProcessMcpServer` with `.WithSkills(o => o.Directories.Add(fixtureRoot))`; client created with default options (no skills extension declared — R8):
- capability: `client.ServerCapabilities.Extensions["io.modelcontextprotocol/skills"]` is an object with `directoryRead == true`; `ServerCapabilities.Resources` not null.
- `skills/list` returns `resultType: "complete"`, `ttlMs: 300000`, `cacheScope: "public"`, no `nextCursor`; each entry has exactly the keys `uri`, `frontmatter`, `resources`; every `resources[].digest` matches `^sha256:[0-9a-f]{64}$`; self entry present; no duplicates.
- `skills/get` for a listed URI echoes the URI, same entry as the listing; for `skill://mcp-conformance-nonexistent-skill-9f3a2b/SKILL.md` → `McpProtocolException` with code `-32602`; for a skill root URI and for a supporting file URI → `-32602`; missing `uri` → `-32602`.
- `resources/read` of `SKILL.md` → one text content, `mimeType: text/markdown`, UTF-8 bytes of `text` hash to the manifest digest and length equals `size`; a binary fixture file comes back as blob whose decoded bytes match the digest; unknown `skill://…` → `-32602`.
- `resources/list` contains each `SKILL.md` with frontmatter name/description and no supporting files; with an extra attribute resource registered (define a tiny test resource type), both appear.
- `resources/directory/read`: root lists `SKILL.md` + `references` (inode/directory); subdirectory lists its files; `SKILL.md` URI → `-32602`.
- frontmatter equality: parse the served `SKILL.md` text with `FrontmatterParser` → deep-equals the entry's `frontmatter`.
- startup with an invalid skill directory → server start throws `SkillValidationException`.

**Acceptance:**
- [ ] `dotnet test` green.
- [ ] Commit: `feat(skills): skills/list, skills/get, directory read and skill resources`

### Task 6: Mcp.Events — event model, event log, cursors, publisher

**Goal:** The transport-independent core of the Events extension: definitions, a bounded in-memory event log with opaque cursors and replay semantics, argument validation, and the publisher API apps use.

**Files:**
- Create: `src/Mcp.Events/EventsProtocol.cs` (method names, notification names, error codes, capability key, meta keys)
- Create: `src/Mcp.Events/EventDefinition.cs`, `EventDelivery.cs`, `EventEnvelope.cs`, `EventsOptions.cs`, `EventQuery.cs`, `EventReadResult.cs`
- Create: `src/Mcp.Events/EventCursor.cs`, `IEventLog.cs`, `IEventPublisher.cs`, `InMemoryEventLog.cs`, `ArgumentValidator.cs`, `InvalidCursorException.cs`
- Create: `tests/CodebaseGuardian.Tests/Events/InMemoryEventLogTests.cs`, `EventCursorTests.cs`, `ArgumentValidatorTests.cs`

**Interfaces (Produces):**

```csharp
namespace Mcp.Events;

public static class EventsProtocol
{
    public const string ListMethod = "events/list", PollMethod = "events/poll", StreamMethod = "events/stream",
                        SubscribeMethod = "events/subscribe", UnsubscribeMethod = "events/unsubscribe";
    public const string ActiveNotification = "notifications/events/active", EventNotification = "notifications/events/event",
                        HeartbeatNotification = "notifications/events/heartbeat", ErrorNotification = "notifications/events/error",
                        TerminatedNotification = "notifications/events/terminated";
    public const string SubscriptionIdMetaKey = "io.modelcontextprotocol/subscriptionId";
    public const string CapabilityKey = "events";   // emitted under capabilities.experimental (spec ruling R1)
    public const int NotFound = -32011, Forbidden = -32012, ResourceExhausted = -32013, Unsupported = -32014, CallbackEndpointError = -32015; // ruling R2
}

public enum EventDelivery { Poll, Push, Webhook }   // wire strings "poll","push","webhook"

public sealed class EventDefinition
{
    public required string Name { get; init; }                 // ^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$
    public required string Description { get; init; }
    public JsonObject InputSchema { get; init; } = new() { ["type"] = "object" };
    public required JsonObject PayloadSchema { get; init; }
    public Func<JsonObject?, JsonObject, bool>? Matches { get; init; }   // (arguments, data) => deliver?; null = always
}

public sealed record EventEnvelope(string EventId, string Name, DateTimeOffset Timestamp, JsonObject Data, long Sequence);

public sealed class EventsOptions
{
    public int Capacity { get; set; } = 10_000;
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
    public int DefaultMaxEvents { get; set; } = 100;
    public int MaxEventsLimit { get; set; } = 1_000;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);        // nextPollMs
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);  // ruling R6
    public bool WebhooksEnabled { get; set; }                                      // Epic 2; adds "webhook" to delivery (R5)
    public IReadOnlyList<EventDefinition> Definitions { get; }
    public void Define(EventDefinition definition);   // throws on invalid/duplicate name
}

public sealed record EventQuery(IReadOnlyCollection<string>? Names, JsonObject? Arguments, string? Cursor, TimeSpan? MaxAge, int MaxEvents)
{
    public bool NewestFirst { get; init; }   // tail mode for the poll_events tool: when more than MaxEvents match, keep the newest MaxEvents
}

public sealed record EventReadResult(IReadOnlyList<EventEnvelope> Events, string Cursor, bool Truncated, bool HasMore);

public interface IEventPublisher
{
    // Validates the name is defined; eventId (optional, ^[A-Za-z0-9_-]{1,128}$, no '.') overrides the generated id;
    // a duplicate eventId still retained returns the existing envelope without appending (dedupe).
    ValueTask<EventEnvelope> PublishAsync(string name, JsonObject data, string? eventId = null,
        DateTimeOffset? timestamp = null, CancellationToken cancellationToken = default);
}

public interface IEventLog
{
    string HeadCursor { get; }         // position after the newest event
    string OldestCursor { get; }       // position before the oldest retained event
    EventReadResult Read(EventQuery query);      // throws InvalidCursorException for malformed cursors
    string CursorAfter(EventEnvelope envelope);
    // Completes when an event with Sequence > afterSequence exists (or immediately if one already does).
    Task WaitForEventsAfterAsync(long afterSequence, CancellationToken cancellationToken);
    long GetSequence(string cursor);   // decode helper for stream loops; throws InvalidCursorException
}

public sealed class InMemoryEventLog(EventsOptions options, TimeProvider timeProvider) : IEventLog, IEventPublisher;

public static class ArgumentValidator
{
    // Supports top-level "properties" with "type" (string|integer|number|boolean|array|object), "enum", and "required".
    // Undeclared properties are errors. Returns null when valid, else a human-readable message.
    public static string? Validate(JsonObject inputSchema, JsonObject? arguments);
}
```

**Behaviour (wire-format B4, B5, B11):**
- Sequence: global, starts at 1, monotonic. Epoch: random per process (`Guid.NewGuid()`), cursor = base64url(no padding) of `v1:<epoch N format>:<sequence>`, where sequence means "everything ≤ this has been seen". `HeadCursor` encodes the newest sequence (0 when empty).
- Event ids: `evt_<first 8 hex of epoch>_<sequence>` unless supplied.
- Retention: drop oldest when count > `Capacity` or older than `Retention` (checked on append and read).
- `Read` semantics:
  - `Cursor == null` → no events, `Cursor = HeadCursor`, `Truncated=false`, `HasMore=false` ("start from now").
  - cursor epoch ≠ current epoch, or cursor sequence < (oldest retained sequence − 1) → `Truncated=true`, read from oldest retained.
  - `MaxAge` set → skip events older than now − MaxAge; if any event after the cursor was skipped for age → `Truncated=true`.
  - filter by `Names` (null = all) and, when exactly one name is given and its definition has `Matches`, by `Matches(arguments, data)`.
  - take up to `MaxEvents` in sequence order; if more matches remain → `HasMore=true`, `Cursor` = sequence of the last returned event; else `Cursor = HeadCursor` (advances past non-matching events).
  - `NewestFirst` (tail mode): when more than `MaxEvents` match, return the newest `MaxEvents` (still ascending order), `HasMore=false`, `Cursor = HeadCursor`.
- `Data` is deep-cloned on publish and on read (callers can't mutate stored events).
- Thread-safe (lock or reader/writer lock); `WaitForEventsAfterAsync` uses a `TaskCompletionSource` swapped on each append (no polling).
- `InvalidCursorException` for undecodable cursors.

**Tests (write first)** (use `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` — add `PackageVersion` 10.x latest stable to `Directory.Packages.props` and reference it from the test project):
- null cursor → empty + head cursor; poll again with that cursor after 2 publishes → both events in order, `HasMore=false`.
- `MaxEvents` paging: 5 events, `MaxEvents=2` → 2, 2, 1 with `HasMore` true, true, false and no duplicates.
- name filter advances cursor past other events (subsequent read with returned cursor doesn't re-scan them — assert no events and same cursor).
- `Matches` filter: definition matching `arguments.branch == data.branch`.
- capacity eviction: capacity 3, publish 5, read from a cursor at sequence 1 → `Truncated=true`, events 3..5.
- cursor from another log instance (different epoch) → `Truncated=true`.
- maxAge: events at t0 and t0+10min, read with cursor before both and `MaxAge=5min` at t0+11min → only the second, `Truncated=true`.
- malformed cursor → `InvalidCursorException`.
- custom eventId used; duplicate eventId returns existing envelope, log count unchanged; eventId with `.` → `ArgumentException`; unknown event name → `ArgumentException`.
- `NewestFirst` returns the newest N.
- `WaitForEventsAfterAsync` completes after a publish and is cancellable.
- `ArgumentValidator`: type mismatch, enum violation, required missing, undeclared property, valid case, null arguments with no required → valid.

**Acceptance:**
- [ ] `dotnet test` green.
- [ ] Commit: `feat(events): event log with opaque cursors, replay and publisher`

### Task 7: Mcp.Events — `WithEvents`, `events/list`, `events/poll`

**Goal:** Expose the event log over MCP: capability, event-type listing and stateless polling.

**Files:**
- Create: `src/Mcp.Events/EventsServerBuilderExtensions.cs`, `EventsConfigureOptions.cs` (internal), `EventJson.cs` (descriptor/envelope JSON), `EventsRequestParser.cs` (shared param parsing/validation for poll/stream/subscribe)
- Modify: `src/Mcp.Events/Mcp.Events.csproj` (NoWarn for reported MCP experimental IDs)
- Create: `tests/CodebaseGuardian.Tests/Events/EventsPollProtocolTests.cs`

**Interfaces:**
- Consumes: Task 6 types.
- Produces:

```csharp
namespace Mcp.Events;
public static class EventsServerBuilderExtensions
{
    // Registers EventsOptions (configure callback), TimeProvider.System (TryAdd), InMemoryEventLog as singleton
    // exposed as IEventLog and IEventPublisher, and the protocol handlers.
    public static IMcpServerBuilder WithEvents(this IMcpServerBuilder builder, Action<EventsOptions> configure);
}

internal static class EventsRequestParser
{
    // Parses {name, arguments?, cursor?, maxAgeMs?, maxEvents?}; validates name exists (else McpProtocolException NotFound, data {kind:"event"}),
    // the requested delivery mode is supported (else Unsupported, data {feature:"deliveryMode", value:"<mode>"}),
    // arguments via ArgumentValidator (else InvalidParams), cursor decodability (else InvalidParams), numeric ranges.
}
```

**Behaviour (wire-format B1, B2, B4, B5, B12; rulings R1–R5):**
- Capability: `options.Capabilities.Experimental["events"] = {"listChanged": false}`.
- `delivery` for every definition: `["poll","push"]`, plus `"webhook"` when `EventsOptions.WebhooksEnabled`.
- `events/list` → `{"resultType":"complete","events":[{"name","description","delivery","inputSchema","payloadSchema"}...],"ttlMs":3600000,"cacheScope":"public"}`, ordered by name; ignores `cursor`.
- `events/poll` → parse; `maxEvents` default `DefaultMaxEvents`, clamp to 1..`MaxEventsLimit`; `maxAgeMs` ≥ 0 else `-32602`; result `{"resultType":"complete","events":[envelope...],"cursor","truncated","hasMore","nextPollMs"}`; envelope `{"eventId","name","timestamp","data"}` (no per-event cursor in poll). `nextPollMs` = `PollInterval` ms.
- Errors are `McpProtocolException` with the R2 codes and `Data` entries as specified.

**Tests (write first)** — `InProcessMcpServer` + `.WithEvents(...)` defining two test events (`test.alpha` with inputSchema `{branch: string}` and a `Matches` on branch; `test.beta`):
- `ServerCapabilities.Experimental["events"]` exists with `listChanged == false`.
- `events/list` shape: both events, `delivery == ["poll","push"]`, schemas present, `resultType`, `ttlMs`, `cacheScope`.
- poll with null cursor → `events: []`, non-null cursor, `truncated:false`, `hasMore:false`, `nextPollMs: 5000`.
- publish via `IEventPublisher` (resolve from `server.Services`) → poll with previous cursor returns the event with ISO `timestamp` ending in `Z`, and `data` equal to what was published.
- argument filter: `arguments: {branch: "main"}` only returns main events.
- unknown name → code `-32011`, `Data["kind"] == "event"`; bad arguments (`branch: 5`) → `-32602`; malformed cursor → `-32602`; `maxEvents: 0` clamps to 1.
- `WebhooksEnabled = true` → delivery includes `"webhook"`.

**Acceptance:**
- [ ] `dotnet test` green.
- [ ] Commit: `feat(events): events/list and events/poll with capability declaration`

### Task 8: Mcp.Events — `events/stream` push delivery

**Goal:** Long-lived push subscriptions with confirmation, replay, live delivery, heartbeats, gap signalling and cancellation, working over stdio and HTTP.

**Files:**
- Create: `src/Mcp.Events/EventStreamHandler.cs`
- Modify: `src/Mcp.Events/EventsConfigureOptions.cs` (register `events/stream`), `src/Mcp.Events/EventsServerBuilderExtensions.cs` (register the incoming message filter that stashes the request-bound `McpServer`, SDK §3)
- Create: `tests/CodebaseGuardian.Tests/Events/EventsStreamProtocolTests.cs`

**Interfaces:**
- Consumes: Task 6/7 types; `EventsRequestParser`.
- Produces: method `events/stream`; notifications `notifications/events/active|event|heartbeat` (and `terminated`/`error` constants for future use).

**Behaviour (wire-format B6, B11; SDK §3, §4; ruling R6):**
- Params as poll (except `maxEvents` ignored). Validation failures → immediate JSON-RPC error, no notifications sent.
- Subscription id = the JSON-RPC request id (`request.Id`), put in every notification's `params._meta["io.modelcontextprotocol/subscriptionId"]` (string or number exactly as the request id was).
- Sequence:
  1. Determine start position: `cursor == null` → head; else decode (replay from cursor), apply `maxAgeMs`.
  2. Send `notifications/events/active {cursor: <start cursor>, truncated: <bool>}`.
  3. Replay matching events after the start cursor in batches; each → `notifications/events/event` with `{eventId,name,timestamp,data,cursor:<cursor after this event>}`.
  4. Loop: wait for new events (`WaitForEventsAfterAsync`) or `HeartbeatInterval` elapsed (whichever first); deliver new matching events; on a quiet interval send `notifications/events/heartbeat {cursor: <current position>}`. If a read reports `Truncated` mid-stream (consumer fell behind retention) send `notifications/events/active {cursor, truncated: true}` and continue.
  5. Client cancellation (handler token) → stop and let `OperationCanceledException` propagate (no result). Server shutdown (`IHostApplicationLifetime.ApplicationStopping` when available) → return `{"resultType":"complete","_meta":{}}`.
- Notifications are sent with the request-bound server obtained from `request.Context.Items` (filter registered in `WithEvents`), `SendMessageAsync(new JsonRpcNotification { Method = ..., Params = ... }, ct)`.
- Per-subscription ordering preserved; one handler invocation per stream; no global concurrency cap.

**Tests (write first)** — `InProcessMcpServer` with `HeartbeatInterval = 200ms`; collect notifications via `client.RegisterNotificationHandler` filtered by subscription id; send the stream request with an explicit id (`new JsonRpcRequest { Id = new RequestId("s-1"), ... }`) and do not await it until cancellation; cancel by sending `notifications/cancelled` explicitly (SDK §4 quirk):
- first notification is `active` with `truncated:false` and the subscription id meta.
- publish 3 events → 3 `event` notifications in order, each with a distinct cursor; polling with the last cursor returns nothing new.
- quiet period → at least one `heartbeat` within 1 s, carrying a cursor.
- replay: publish 2 events, then open a stream with the cursor from before them → both replayed before live events.
- argument filter on stream.
- two concurrent streams (ids `s-1`, `s-2`) receive their own events with their own ids.
- cancellation: after `notifications/cancelled` for `s-1`, publishing another event produces no further `s-1` notifications within 500 ms; the client request task ends cancelled/faulted (assert it completes).
- unknown event name → error response, no `active` notification.
- capacity overflow during a slow consumer is hard to force; instead unit-test the "truncated mid-stream" branch by publishing more than `Capacity` events between two reads using a small capacity (e.g. 5) and a stream opened with replay from an old cursor → `active` with `truncated:true` appears.

**Acceptance:**
- [ ] `dotnet test` green (run the stream tests 3 times locally to check for flakiness: `dotnet test --filter-class *EventsStreamProtocolTests*` or the MTP equivalent).
- [ ] Commit: `feat(events): events/stream push delivery with heartbeats and replay`

### Task 9: Guardian event catalog, repository watcher, and `poll_events` fallback tool

**Goal:** Turn repository activity into events: define the Guardian event catalog, watch the repo for new commits, branch switches, file edits and dependency-manifest changes, and give clients without Events support a `poll_events` tool.

**Files:**
- Create: `src/CodebaseGuardian/Watching/GuardianEventNames.cs` (constants for every event name in spec §5.5, including Epic 2/3 names)
- Create: `src/CodebaseGuardian/Watching/GuardianEvents.cs` (`static void Register(EventsOptions options)` — defines the Epic 1 events: `repo.commit.created`, `repo.branch.changed`, `repo.files.changed`, `repo.dependencies.changed`, `checks.completed`, `checks.failed`, `security.secret_detected`, with description, inputSchema, payloadSchema, `Matches`)
- Create: `src/CodebaseGuardian/Watching/SuggestedSkills.cs` (constants: `skill://pr-review/SKILL.md`, `skill://dependency-hygiene/SKILL.md`, `skill://bug-triage/SKILL.md`, `skill://security-audit/SKILL.md`)
- Create: `src/CodebaseGuardian/Watching/IRepositoryChangeHandler.cs`
- Create: `src/CodebaseGuardian/Watching/RepositoryWatcher.cs` (`BackgroundService`)
- Create: `src/CodebaseGuardian/Watching/WorkingTreeWatcher.cs` (FileSystemWatcher + debounce; owned by `RepositoryWatcher`)
- Create: `src/CodebaseGuardian/Dependencies/DependencyManifests.cs`
- Create: `src/CodebaseGuardian/Tools/EventTools.cs`
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs` (`.WithEvents(GuardianEvents.Register)`, hosted watcher when `WatchEnabled`, `.WithTools<EventTools>()`)
- Create: `tests/CodebaseGuardian.Tests/App/RepositoryWatcherTests.cs`, `DependencyManifestsTests.cs`, `EventToolsTests.cs`, `GuardianEventsTests.cs`

**Interfaces:**
- Consumes: `IGitRepository` (Task 2), `IEventPublisher`/`IEventLog`/`EventsOptions` (Tasks 6–7).
- Produces:

```csharp
namespace CodebaseGuardian.Watching;
public interface IRepositoryChangeHandler
{
    // Called by RepositoryWatcher after it has published repo.commit.created for these commits (oldest first).
    // Exceptions are logged by the watcher and never stop it.
    Task OnNewCommitsAsync(IReadOnlyList<CommitInfo> commits, CancellationToken cancellationToken);
}

namespace CodebaseGuardian.Dependencies;
public static class DependencyManifests
{
    public static bool IsManifest(string relativePath);
    public static string? GetEcosystem(string relativePath);   // "nuget" | "npm" | null
    // nuget: *.csproj, *.fsproj, *.vbproj, Directory.Packages.props, Directory.Build.props, packages.lock.json, global.json
    // npm: package.json, package-lock.json, npm-shrinkwrap.json, yarn.lock, pnpm-lock.yaml
}
```

**Event payloads (exact field names; all JSON objects):**

| Event | inputSchema properties | data |
|---|---|---|
| `repo.commit.created` | `branch` (string, optional; `Matches` on equality) | `sha, shortSha, branch, author{name,email}, committedAt, subject, filesChanged, insertions, deletions, files[] (≤100 paths), suggestedSkill` |
| `repo.branch.changed` | — | `from` (string or null), `to` (string or null), `headSha` (string or null) |
| `repo.files.changed` | `pathPrefix` (string, optional; `Matches` when any path starts with it) | `paths[] (≤200), count` |
| `repo.dependencies.changed` | — | `manifests[], ecosystems[], commitSha` (string or null), `suggestedSkill` |
| `checks.completed` / `checks.failed` | — | `runId, command, exitCode, passed, timedOut, durationMs, summary, failedTests[], logUri, trigger, commitSha` (+ `suggestedSkill` on `checks.failed` only) |
| `security.secret_detected` | — | `source` ("commit" \| "scan"), `commitSha` (string or null), `findings[] {ruleId, path, line, redacted}`, `suggestedSkill` |

Payload schemas declare these properties with types (`"type": ["string","null"]` where nullable).

**Watcher behaviour:**
- Startup: snapshot branch heads (`GetBranchHeadsAsync`) and current branch; publish nothing for pre-existing history.
- Every `WatchIntervalMs`: re-read heads + current branch.
  - Branch switch → `repo.branch.changed`.
  - For each branch whose head changed or that is new: `GetNewCommitsAsync(newHead, previousHeadsSnapshot.Values, 50)`; publish one `repo.commit.created` per commit not seen before (keep a bounded set of the last 10 000 announced SHAs so a commit reachable from several branches is announced once; `branch` = the first branch that revealed it). Stats from `GetCommitDiffAsync(sha, 0)`.
  - If a commit touched manifests → also `repo.dependencies.changed` with `commitSha`.
  - Then call every `IRepositoryChangeHandler.OnNewCommitsAsync` with all newly announced commits (oldest first).
  - Rewritten history (`commit --amend`, rebase, force reset) is just a head change: the rewritten commits are new SHAs and are announced once each. Deleted branches simply leave the snapshot. If git rejects an exclusion tip (object pruned), retry excluding only the current heads of the other branches; the announced-SHA set prevents duplicates.
  - An unborn repository starts with an empty snapshot; its first commit is a new branch head and is announced.
- Working tree: `FileSystemWatcher` on the repo root (subdirectories, `Created|Changed|Deleted|Renamed`), ignore any path segment `.git`, `bin`, `obj`, `node_modules`; debounce `FileChangeDebounceMs` after the last change; publish `repo.files.changed` with distinct sorted relative `/` paths (≤200, `count` = total); manifests among them → `repo.dependencies.changed` with `commitSha: null`.
- All errors are logged (stderr) and the loop continues; the watcher stops with the host.

**`poll_events` tool:** `poll_events(cursor = null, names = null, maxEvents = 50)` — read-only, idempotent. Unknown names → tool error. Uses `IEventLog.Read` with `NewestFirst = (cursor == null)`; with no cursor it returns the newest `maxEvents` retained events (so an agent can see recent activity), otherwise everything after the cursor. Returns structured content `{events:[{eventId,name,timestamp,data}], cursor, truncated, hasMore}`. Description tells the model: "Use when your client cannot subscribe to MCP events; pass the returned cursor next time."

**Tests (write first)** — `GuardianTestHost` with `Guardian:WatchEnabled=true`, `Guardian:WatchIntervalMs=100`, `Guardian:FileChangeDebounceMs=100`; assert via `events/poll` (start with a null cursor to get the position, then poll until the event appears or 5 s pass — write a small `EventsPolling.WaitForAsync(host, name, cursor, predicate, timeout)` helper in `tests/.../Infrastructure`):
- commit on main → `repo.commit.created` with sha/subject/author/filesChanged and `suggestedSkill == "skill://pr-review/SKILL.md"`.
- commit touching `package.json` → also `repo.dependencies.changed` with `ecosystems == ["npm"]` and the commit sha.
- `git checkout -b feature` + commit → `repo.branch.changed` (`to == "feature"`) and the commit announced once with `branch == "feature"`; merging/fast-forwarding main to it does not re-announce.
- edit a file without committing → `repo.files.changed` containing the path; edit under `bin/` → not reported.
- a registered fake `IRepositoryChangeHandler` receives the new commits; one that throws does not stop later events.
- watcher started on a repo with no commits → first commit announced as `repo.commit.created` on `main`.
- `git commit --amend -m "reworded"` → exactly one new `repo.commit.created` with the amended sha and subject `reworded`.
- `git branch -D feature` (after its commits were announced), then a new commit on main → that commit is announced; no duplicate announcements of feature's commits; the watcher is still running (a later file edit still yields `repo.files.changed`).
- `branch` argument filter on `repo.commit.created` via `events/poll`.
- `poll_events` without cursor returns recent events newest-limited; with the returned cursor returns only later events; unknown name → `isError`.
- `GuardianEventsTests`: `events/list` contains exactly the seven Epic 1 names with `payloadSchema.properties` covering the fields above.
- `DependencyManifestsTests`: table of paths → ecosystem.

**Acceptance:**
- [ ] `dotnet test` green; watcher tests stable across 3 runs.
- [ ] Commit: `feat(watch): repository watcher, guardian event catalog and poll_events tool`

### Task 10: Check runner (`run_checks`, check resources, check events, auto-checks)

**Goal:** Run the project's build/test command safely, keep logs, expose results as tool output, resources and events.

**Files:**
- Create: `src/CodebaseGuardian/Checks/CheckOptions.cs` (section `Guardian:Checks`: `Command` string?, `Arguments` string[], `TimeoutMinutes` = 15, `MaxRuns` = 50)
- Create: `src/CodebaseGuardian/Checks/CheckCommandResolver.cs`, `CheckRun.cs`, `CheckRunStore.cs`, `CheckRunner.cs`, `TestOutputParser.cs`, `AutoChecksCommitHandler.cs`
- Create: `src/CodebaseGuardian/Tools/CheckTools.cs`, `src/CodebaseGuardian/Resources/CheckResources.cs`
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs`
- Create: `tests/CodebaseGuardian.Tests/App/CheckRunnerTests.cs`, `CheckCommandResolverTests.cs`, `TestOutputParserTests.cs`, `CheckToolsTests.cs`, `tests/CodebaseGuardian.Tests/Infrastructure/FakeProcessRunner.cs`

**Interfaces:**
- Consumes: `IProcessRunner`, `IGitRepository`, `IEventPublisher`, `IRepositoryChangeHandler`, `GuardianEventNames`, `SuggestedSkills`.
- Produces:

```csharp
namespace CodebaseGuardian.Checks;
public sealed record CheckCommand(string FileName, IReadOnlyList<string> Arguments)
{
    public string Display { get; }   // "dotnet test CodebaseGuardian.slnx --nologo"
}
public sealed record CheckRun(string RunId, string Command, int ExitCode, bool Passed, bool TimedOut,
    DateTimeOffset StartedAt, TimeSpan Duration, string Summary, IReadOnlyList<string> FailedTests,
    string Log, string Trigger, string? CommitSha)
{
    public string LogUri => $"guardian://checks/{RunId}/log";
}
public interface ICheckCommandResolver { CheckCommand? Resolve(); }
public interface ICheckRunner
{
    // One run at a time (later callers wait). Publishes checks.completed (always) and checks.failed (when !Passed).
    Task<CheckRun> RunAsync(string trigger, string? commitSha, IProgress<string>? progress, CancellationToken cancellationToken);
}
public sealed class CheckRunStore { void Add(CheckRun run); CheckRun? Get(string runId); CheckRun? Latest { get; } }

// tests/Infrastructure
public sealed class FakeProcessRunner : IProcessRunner
{
    // Register canned results by predicate; records every ProcessSpec; can block until released (for cancellation/task tests).
    public FakeProcessRunner On(Func<ProcessSpec, bool> match, ProcessResult result);
    public FakeProcessRunner OnBlocking(Func<ProcessSpec, bool> match, TaskCompletionSource<ProcessResult> completion);
    public IReadOnlyList<ProcessSpec> Calls { get; }
}
```

**Behaviour:**
- Resolver: configured `Command` (+ `Arguments`) wins. Else in repo root: first `*.slnx` then `*.sln` (ordinal order) → `dotnet test <file> --nologo`; else exactly one `*.csproj` → `dotnet test --nologo`; else `package.json` whose `scripts.test` exists → `npm test`; else null.
- Never accept a command from tool arguments (model input).
- `RunId` = `run_<yyyyMMddHHmmss>_<4 lowercase hex>` (UTC). Log = stdout + "\n" + stderr (≤1 MB each via runner cap). `Passed` = exit 0 and not timed out.
- `TestOutputParser` (best effort, pure): from dotnet/MTP/VSTest output extract failed test names (lines like `  Failed Namespace.Class.Test [12 ms]`, `failed Namespace.Class.Test (12ms)`), max 50; summary = the last line containing `Passed!`, `Failed!`, `Test run summary`, `total:`/`failed:`, or `npm ERR!`; fallback `"exit code <n>"`.
- `run_checks()` tool (no arguments; `ReadOnly=false, Destructive=false, OpenWorld=false`): no command → tool error "No check command configured or detected. Set Guardian:Checks:Command."; else run with trigger `"tool"` and return structured `{runId, command, passed, exitCode, timedOut, durationMs, summary, failedTests, logUri}`; reports progress messages ("running <command>") through `IProgress<ProgressNotificationValue>` when provided.
- Resources: `guardian://checks/latest` (application/json, the latest run without `log`; when none → `McpProtocolException(..., InvalidParams)` "No check runs yet") and template `guardian://checks/{runId}/log` (text/plain, the full log; unknown id → InvalidParams).
- `AutoChecksCommitHandler`: registered only when `GuardianOptions.AutoChecks`; runs checks once per batch for the newest commit (trigger `"commit"`, `commitSha` set).

**Tests (write first)** — use `FakeProcessRunner` (no real `dotnet test`):
- resolver: slnx preferred over sln; single csproj; package.json test script; nothing → null; configured command wins.
- parser: sample outputs (embed realistic snippets for MTP "failed X (12ms)", VSTest "Failed X [12 ms]", npm) → expected names/summary.
- runner: exit 1 → `checks.completed` + `checks.failed` published with `suggestedSkill == "skill://bug-triage/SKILL.md"` and `logUri`; exit 0 → only `checks.completed`; timeout → `Passed=false, TimedOut=true`; two concurrent runs execute sequentially (second starts after first completes — use `OnBlocking`).
- tool via `GuardianTestHost` with a fake runner replaced in `configureServices`: structured result fields; no command → `isError`.
- resources: latest JSON; log template returns the log text; unknown run id → `-32602`.
- auto-checks: with `AutoChecks=true`, a commit triggers a run with trigger `"commit"`.

**Acceptance:**
- [ ] `dotnet test` green.
- [ ] Commit: `feat(checks): check runner, run_checks tool, check resources and events`

### Task 11: Secret scanner (`scan_secrets`, commit scanning, `security.secret_detected`)

**Goal:** Detect committed or pending secrets with redacted reporting.

**Files:**
- Create: `src/CodebaseGuardian/Security/SecretRule.cs`, `SecretRules.cs`, `SecretFinding.cs`, `SecretScanner.cs`, `Redactor.cs`, `GuardianIgnore.cs`, `PatchParser.cs`, `SecretScanCommitHandler.cs`
- Create: `src/CodebaseGuardian/Tools/SecurityTools.cs`
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs`
- Create: `tests/CodebaseGuardian.Tests/App/SecretRulesTests.cs`, `SecretScannerTests.cs`, `PatchParserTests.cs`, `SecurityToolsTests.cs`, `tests/CodebaseGuardian.Tests/Infrastructure/FakeSecrets.cs`

**Interfaces (Produces):**

```csharp
namespace CodebaseGuardian.Security;
public sealed record SecretRule(string Id, string Description, Regex Pattern, int SecretGroup = 0, double? MinEntropy = null);
public sealed record SecretFinding(string RuleId, string Path, int Line, string Redacted);
public interface ISecretScanner
{
    Task<IReadOnlyList<SecretFinding>> ScanWorkingTreeAsync(CancellationToken ct);
    Task<IReadOnlyList<SecretFinding>> ScanStagedAsync(CancellationToken ct);
    Task<IReadOnlyList<SecretFinding>> ScanCommitAsync(string sha, CancellationToken ct);
    IReadOnlyList<SecretFinding> ScanText(string path, string content);           // line numbers 1-based
    IReadOnlyList<SecretFinding> ScanPatch(string unifiedDiff);                  // only added lines; new-file line numbers
}
public static class Redactor { public static string Redact(string secret); }  // len>=12: first4 + "****" + last4; else "****"
```

**Rules (ids are part of the contract; skills reference them):**

| Id | Pattern |
|---|---|
| `aws-access-key-id` | `\b(?:AKIA\|ASIA)[0-9A-Z]{16}\b` |
| `github-token` | `\bgh[pousr]_[A-Za-z0-9]{36,255}\b` |
| `github-fine-grained-pat` | `\bgithub_pat_[A-Za-z0-9_]{82}\b` |
| `slack-token` | `\bxox[baprs]-[A-Za-z0-9-]{10,}\b` |
| `stripe-live-key` | `\b(?:sk\|rk)_live_[A-Za-z0-9]{20,}\b` |
| `private-key` | `-----BEGIN (?:RSA \|EC \|OPENSSH \|DSA \|PGP )?PRIVATE KEY(?: BLOCK)?-----` |
| `jwt` | `\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b` |
| `generic-secret-assignment` | `(?i)\b(?:api[_-]?key\|secret\|token\|passw(?:or)?d)\b\s*[:=]\s*['"]?([A-Za-z0-9_\-+/=]{16,})` with Shannon entropy of group 1 ≥ 3.5 |

**Behaviour:**
- Working tree: `IGitRepository.ListFilesAsync`, skip files > 1 MB, binary files (NUL byte in first 8 KB), and paths matching `.guardianignore` globs (repo-root file; one glob per line; `#` comments; supports `*`, `**`, `?`; matched against `/` relative paths).
- Staged/commit: scan patches from `GetStagedDiffAsync` / `GetCommitDiffAsync` with `maxPatchBytes` 5 MB; `PatchParser` tracks `+++ b/<path>` and `@@ -a,b +c,d @@` hunks to compute new-side line numbers; ignores removed lines.
- One finding per (rule, path, line); the redacted value never includes the full secret; nothing logs secret values.
- `SecretScanCommitHandler` (`IRepositoryChangeHandler`, always registered): scans each new commit; findings → `security.secret_detected` `{source:"commit", commitSha, findings, suggestedSkill:"skill://security-audit/SKILL.md"}`.
- `scan_secrets(scope = "working_tree", commit = null)` tool: scope ∈ `working_tree|staged|commit` (`commit` requires `commit` arg validated by `GitRevision`); read-only; returns `{scope, findings, count}`; if findings > 0 also publishes `security.secret_detected` with `source:"scan"`, `commitSha` = commit arg or null.
  - Declare `scope` as a C# enum `SecretScanScope` with `[JsonConverter(typeof(JsonStringEnumConverter<SecretScanScope>))]` and `[JsonStringEnumMemberName("working_tree")]` etc., so `inputSchema.properties.scope` carries `"enum": ["working_tree","staged","commit"]` (Task 13's content lint relies on it).

**Tests (write first)** — **never put literal secret-shaped strings in source files** (push protection and scanners would flag the repo). `FakeSecrets` (tests/Infrastructure) builds them at runtime and is reused by Tasks 15, 17, 18, 23:

```csharp
public static class FakeSecrets
{
    public static string AwsAccessKeyId() => "AKIA" + "IOSFODNN7EXAMPLE";
    public static string GitHubToken() => "ghp_" + new string('a', 36);
    public static string SlackToken() => "xoxb-" + "1234567890-abcdefghij";
    public static string PrivateKeyHeader() => "-----BEGIN " + "RSA PRIVATE" + " KEY-----";
}
```

- `tools/list`: `scan_secrets` `inputSchema.properties.scope.enum` equals `["working_tree","staged","commit"]`.
- each rule: positive and negative example; generic assignment rejects low-entropy `password = "aaaaaaaaaaaaaaaaaaaa"`.
- redaction format; findings never contain the raw secret (assert `!finding.Redacted.Contains(secret)`).
- patch parser: added-line numbers across two hunks and two files; removed lines ignored.
- `.guardianignore` excludes `fixtures/**`.
- binary and >1 MB files skipped.
- commit handler via `GuardianTestHost` (watch on): committing a file containing a fake AWS key → `security.secret_detected` with `source:"commit"`, the commit sha, rule `aws-access-key-id`, correct line.
- tool: `scope:"staged"` sees a staged secret; `scope:"commit"` without `commit` → `isError`; invalid revision → `isError`.

**Acceptance:**
- [ ] `dotnet test` green.
- [ ] Commit: `feat(security): secret scanner, scan_secrets tool and commit scanning`

### Task 12: Dependency auditor (`audit_dependencies`)

**Goal:** Report vulnerable and outdated packages for NuGet and npm without failing when a toolchain is missing.

**Files:**
- Create: `src/CodebaseGuardian/Dependencies/DependencyAuditModels.cs`, `IDependencyAuditor.cs`, `DependencyAuditor.cs`, `NuGetAuditParser.cs`, `NpmAuditParser.cs`
- Create: `src/CodebaseGuardian/Tools/DependencyTools.cs`
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs`
- Create: `tests/CodebaseGuardian.Tests/App/NuGetAuditParserTests.cs`, `NpmAuditParserTests.cs`, `DependencyAuditorTests.cs`, `DependencyToolsTests.cs`

**Interfaces (Produces):**

```csharp
namespace CodebaseGuardian.Dependencies;
public sealed record VulnerablePackage(string Ecosystem, string Package, string Version, string Severity, string? AdvisoryUrl, string? Project);
public sealed record OutdatedPackage(string Ecosystem, string Package, string Current, string Latest, string? Project);
public sealed record EcosystemReport(string Ecosystem, string Status, string? Reason,   // Status: "ok" | "skipped" | "failed"
    IReadOnlyList<VulnerablePackage> Vulnerable, IReadOnlyList<OutdatedPackage> Outdated);
public sealed record DependencyAuditReport(IReadOnlyList<EcosystemReport> Ecosystems)
{
    public int VulnerableCount { get; }
    public int OutdatedCount { get; }
}
public interface IDependencyAuditor
{
    Task<DependencyAuditReport> AuditAsync(bool includeOutdated, IProgress<string>? progress, CancellationToken cancellationToken);
}
```

**Behaviour:**
- Ecosystems present = those of manifests in `ListFilesAsync` (`DependencyManifests.GetEcosystem`); none → empty report.
- NuGet target: root `*.slnx`/`*.sln` (first ordinal) else the repo root directory. Commands (timeout 5 min each): `dotnet list <target> package --vulnerable --include-transitive --format json --output-version 1` and, when `includeOutdated`, `dotnet list <target> package --outdated --format json --output-version 1`. Parse `projects[].frameworks[].topLevelPackages[]` (+ `transitivePackages[]` for vulnerable) — fields `id`, `resolvedVersion`, `latestVersion`, `vulnerabilities[] {severity, advisoryurl}`. Deduplicate by (package, version, project).
- npm: `npm audit --json` (`vulnerabilities` object: name → `{severity, range, via, fixAvailable}`; version from `range` when no better field) and `npm outdated --json` (exit code 1 is normal when packages are outdated; name → `{current, latest}`).
- `ExecutableNotFoundException` → ecosystem `Status:"skipped"`, `Reason:"<tool> not found on PATH"`. Non-JSON or unexpected exit → `Status:"failed"` with a short reason (no raw output dumps beyond 500 chars).
- `audit_dependencies(includeOutdated = true)` tool: read-only, `OpenWorld = true` (package feeds); returns the report as structured content plus a one-line summary text.

**Tests (write first)** — parsers fed with realistic JSON fixtures embedded as raw string literals (shape per the formats above); auditor with `FakeProcessRunner`:
- NuGet vulnerable + outdated parsed with project names and severities.
- npm audit + outdated parsed; `npm outdated` exit 1 handled as success.
- missing `npm` → npm skipped, nuget ok.
- repo with only `package.json` runs only npm commands (assert `Calls`).
- tool returns structured content with counts.

**Acceptance:**
- [ ] `dotnet test` green.
- [ ] Commit: `feat(deps): dependency auditor for NuGet and npm with audit_dependencies tool`

### Task 13: Skill content, server instructions, and content lint

**Goal:** Ship the five skills, point the agent at them from server instructions, and enforce that skills only reference things that exist.

**Files:**
- Create: `src/CodebaseGuardian/skills/guardian/SKILL.md`, `src/CodebaseGuardian/skills/guardian/references/events.md`
- Create: `src/CodebaseGuardian/skills/security-audit/SKILL.md`, `references/secret-types.md`, `references/remediation.md`
- Create: `src/CodebaseGuardian/skills/dependency-hygiene/SKILL.md`, `references/upgrade-policy.md`
- Create: `src/CodebaseGuardian/skills/bug-triage/SKILL.md`, `references/triage-checklist.md`, `assets/issue-template.md`
- Create: `src/CodebaseGuardian/skills/pr-review/SKILL.md`, `references/review-checklist.md`, `assets/review-comment-template.md`
- Modify: `src/CodebaseGuardian/CodebaseGuardian.csproj` (`<Content Include="skills/**" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />` and pack them with the tool)
- Create: `src/CodebaseGuardian/Hosting/SkillsInstructionsContributor.cs` (Order 10), `src/CodebaseGuardian/Hosting/EventsInstructionsContributor.cs` (Order 20)
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs` (`.WithSkills(o => o.Directories.Add(options.SkillsDirectory ?? Path.Combine(AppContext.BaseDirectory, "skills")))`, contributors)
- Modify: `tests/CodebaseGuardian.Tests/Infrastructure/GuardianTestHost.cs` only if Task 1's skills-directory default needs adjusting
- Create: `tests/CodebaseGuardian.Tests/App/SkillContentTests.cs`

**Skill content requirements:**
- Frontmatter: `name` (= directory), `description` (what + when, ≤ 300 chars, mentions the triggering events), `license: "MIT"`, `metadata: {author: "codebase-guardian", version: "1"}` — quote all string scalars (wire-format A9 guidance).
- `SKILL.md` ≤ 200 lines; instructions are numbered steps naming exact tools (`` `run_checks` ``), resources (`` `guardian://checks/{runId}/log` ``) and events (`` `checks.failed` ``) in backticks; relative links to the skill's own files (`references/triage-checklist.md`).
- `guardian` skill (load first): what the server watches; how to subscribe (`events/stream` preferred, `events/poll`, fallback tool `poll_events`); the event → skill table from spec §5.5 (Epic 1 rows); how to load a skill (`skills/get`, `resources/read`, digest verification is the host's job); safety rules (never paste secrets, never run destructive git commands on the user's behalf without consent).
- `security-audit`: triage `security.secret_detected` findings by `ruleId` (table in `references/secret-types.md` matching Task 11 rule ids), confirm with `scan_secrets`, remediation order (revoke/rotate first, then remove from history only with user consent — explain why rewriting history is disruptive) in `references/remediation.md`.
- `dependency-hygiene`: run `audit_dependencies`, prioritise vulnerable (critical/high first) over outdated, major vs minor upgrade policy in `references/upgrade-policy.md`, verify with `run_checks` after upgrades.
- `bug-triage`: from `checks.failed`: read `guardian://checks/{runId}/log`, reproduce with `run_checks`, isolate with `recent_commits` and `diff_summary`, classify (regression/flaky/environment), write the report from `assets/issue-template.md`.
- `pr-review`: from `repo.commit.created`: `diff_summary` with `from = <sha>^` and `to = <sha>`, checklist in `references/review-checklist.md` (correctness, tests, security incl. `scan_secrets` with `scope:"commit"`, naming, docs), output using `assets/review-comment-template.md`.

**Instructions contributors:**
- Skills (Order 10): "Skills available (load `skill://guardian/SKILL.md` first):" followed by one line per skill `- <uri> — <description>` from `SkillCatalog`.
- Events (Order 20): one line per event name from `EventsOptions.Definitions`, plus "If your client does not support `events/*`, call the `poll_events` tool."

**Content lint (`SkillContentTests`)** — loads the built skills directory through a `GuardianTestHost` and via `SkillCatalog.Load`:
- all five skills load (no validation errors) and appear in `skills/list`; `skills/list[0]` has a subdirectory (conformance directory check).
- every backticked token in every skill file matching `^[a-z]+(?:_[a-z]+)+$` is either a tool name from `tools/list`, or a property name or `enum` value declared in some tool's `inputSchema` (so argument values such as `working_tree` for `scan_secrets`' `scope` pass, while a misspelled tool name like `scan_secret` fails).
- every backticked `guardian://…` URI matches a resource from `resources/list` or a template from `resources/templates/list` (treat `{x}` as wildcard).
- every backticked token matching `^[a-z]+(?:\.[a-z_]+)+$` is an event name from `events/list`, except tokens ending in a file extension (`.md`, `.json`, `.yml`, `.yaml`, `.txt`, `.props`, `.lock`, `.cs`, `.js`, `.ts`, `.py`, `.sh`), which are file names.
- every `skill://…/SKILL.md` mentioned resolves with `skills/get`; every relative markdown link inside a skill resolves to a file of that skill.
- instructions contain each skill URI and the `poll_events` fallback sentence.
- Allowed exceptions list in the test for tokens that are not tools (e.g. `max_events` if used) — keep it empty unless needed and justified in a comment.

**Acceptance:**
- [ ] `dotnet test` green.
- [ ] `dotnet build` copies `skills/` next to the server binary.
- [ ] Commit: `feat(skills): bundled guardian skills, instructions pointer and content lint`

### Task 14: Stateless Streamable HTTP transport and conformance check

**Goal:** Serve the same server over stateless 2026-07-28 Streamable HTTP, verify Skills/Events over HTTP, and run the official conformance suite's Skills scenarios.

**Files:**
- Create: `src/CodebaseGuardian/Hosting/HttpHost.cs` (builds the `WebApplication`)
- Create: `src/CodebaseGuardian/Hosting/StdioHost.cs` (moves the stdio branch out of `Program.cs`)
- Modify: `src/CodebaseGuardian/Program.cs` (dispatch on `GuardianOptions.Transport`)
- Modify: `src/CodebaseGuardian/Hosting/GuardianOptions.cs` (add `public bool HttpAllowRemote { get; set; }`)
- Create: `tests/CodebaseGuardian.Tests/Infrastructure/GuardianHttpTestHost.cs`
- Create: `tests/CodebaseGuardian.Tests/App/HttpTransportTests.cs`
- Create: `docs/reference/conformance.md` (results of the conformance run)

**Interfaces (Produces):**

```csharp
namespace CodebaseGuardian.Hosting;
public static class HttpHost
{
    // Builds (does not run) the web app: Kestrel on GuardianOptions.HttpUrl, AddCodebaseGuardian(...).WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless),
    // app.MapMcp("/mcp"), GET /healthz -> 200 "ok". Logging to stderr.
    public static WebApplication Build(string[] args, Action<IServiceCollection>? configureServices = null);
}

// tests/Infrastructure
public sealed class GuardianHttpTestHost : IAsyncDisposable
{
    public static Task<GuardianHttpTestHost> StartAsync(string repositoryPath, IDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? configureServices = null, CancellationToken cancellationToken = default); // binds http://127.0.0.1:0
    public Uri Endpoint { get; }           // .../mcp
    public IServiceProvider Services { get; }
    public Task<McpClient> ConnectAsync(McpClientOptions? options = null, CancellationToken cancellationToken = default);
}
```

**Behaviour:**
- Binding: refuse to start when `HttpUrl` host is not loopback (`127.0.0.1`, `::1`, `localhost`) unless `HttpAllowRemote` is true; message explains the risk (no auth until Task 19).
- Stateless mode (`HttpServerSessionMode.Stateless`). Everything request/response must work; `events/stream` works as a long-lived POST with SSE (SDK §3, §4 — cancellation by closing the stream).
- Hosted services (watcher) run in HTTP mode too.

**Tests (write first)** — `GuardianHttpTestHost` on a `TempGitRepo`, SDK `HttpClientTransport`:
- `/healthz` → 200 "ok".
- client negotiates `2026-07-28`; `tools/list` contains Task 3/9/10/11/12 tools.
- `skills/list` and `skills/get` work; `resources/read` of `skill://guardian/SKILL.md` returns text whose SHA-256 matches the manifest.
- `events/poll` round trip; `events/stream`: `active` then an event published through `IEventPublisher` from `Services`; cancelling the client token closes the stream and later events are not delivered.
- non-loopback `HttpUrl` without `HttpAllowRemote` → `HttpHost.Build` (or start) throws with a message mentioning `HttpAllowRemote`.

**Conformance run (manual step, record results):**
1. `dotnet run --project src/CodebaseGuardian -- --transport http --repo . --urls http://127.0.0.1:5199` in the background.
2. `npx -y @modelcontextprotocol/conformance@latest --help` to discover the CLI, then run the server scenarios for Skills (`sep-2640-skills-enumeration`, `sep-2640-skills-manifest`, `sep-2640-skills-directory`) and any base 2026-07-28 server scenarios the CLI offers, against `http://127.0.0.1:5199/mcp`.
3. Write `docs/reference/conformance.md`: command lines, suite version, pass/fail/warning per check id, and for each failure either fix it in this task (if it is in Skills/HTTP code) or explain it. If the package or network is unavailable, record that and the exact error.

**Acceptance:**
- [ ] `dotnet test` green.
- [ ] `docs/reference/conformance.md` written; no FAILURE remains in the three Skills scenarios unless explained as a suite/spec issue.
- [ ] Commit: `feat(http): stateless streamable HTTP transport with conformance results`

### Task 15: Demo client (`samples/GuardianWatch`), README, and end-to-end scenario test

**Goal:** Show the full loop — event → suggested skill → verified skill content — in a runnable sample and an automated end-to-end test, and document the project.

**Files:**
- Create: `samples/GuardianWatch/GuardianWatch.csproj` (console; references `ModelContextProtocol` only — it's a client), `samples/GuardianWatch/Program.cs`, `samples/GuardianWatch/SkillVerifier.cs`
- Modify: `CodebaseGuardian.slnx` (add the sample under a `/samples/` folder)
- Create: `tests/CodebaseGuardian.Tests/App/EndToEndTests.cs`
- Modify: `README.md`

**Behaviour (sample):**
- `dotnet run --project samples/GuardianWatch -- --repo <path> [--server <path to CodebaseGuardian.dll or project dir>]`: starts the server over stdio (`StdioClientTransport` running `dotnet <dll>` or `dotnet run --project <dir> --`), prints server instructions, `skills/list` names, `events/list` names.
- Opens one `events/stream` per event `repo.commit.created`, `repo.dependencies.changed`, `checks.failed`, `security.secret_detected` (explicit request ids `watch-<name>`), prints each event compactly; when `data.suggestedSkill` is present: `skills/get` → `resources/read` of the `SKILL.md` → verify SHA-256 and size against the entry (`SkillVerifier`) → print "skill <name> verified (<size> bytes)" and the first 5 lines.
- Ctrl+C: send `notifications/cancelled` for each stream id (SDK §4), dispose client, exit 0.

**End-to-end test (`EndToEndTests`):** `GuardianTestHost` with watcher on (fast intervals) and the real bundled skills:
1. open `events/stream` for `security.secret_detected` (explicit id);
2. commit a file containing a fake AWS key (`FakeSecrets`);
3. receive the event; assert `suggestedSkill == "skill://security-audit/SKILL.md"`;
4. `skills/get` that URI; `resources/read` the `SKILL.md`; verify digest + size with the same logic as `SkillVerifier` (copy-free: the test references the sample project or re-implements the 5-line check — prefer referencing `SkillVerifier` via a project reference to the sample);
5. cancel the stream with `notifications/cancelled`.

**README sections:** what it is (one paragraph + the feature map from spec §2); quick start (build, `dotnet run --project src/CodebaseGuardian -- --repo .`); configuring clients — Claude Code (`claude mcp add codebase-guardian -- dotnet run --project <path>/src/CodebaseGuardian -- --repo <repo>`), VS Code `.vscode/mcp.json`, HTTP mode; tools/resources/events/skills tables (link spec); demo client usage; security notes (spec §7); limitations (Events is a draft — wire format may change; single-process state; NuGet/npm only); architecture mermaid diagram of components from spec §4; development (`dotnet test`, layout).

**Acceptance:**
- [ ] `dotnet test` green (E2E stable across 3 runs).
- [ ] Manual: run GuardianWatch against a temp repo, commit a file with a fake secret, see the verified skill printed (paste the console excerpt into the task report).
- [ ] Commit: `feat(sample): GuardianWatch demo client, README and end-to-end test`

---

## Epic 2 — GitHub + webhooks

### Task 16: GitHub REST client, token and repository resolution

**Goal:** A typed, testable GitHub client the action tools (Task 17) and the event poller (Task 18) share; no tools or events yet.

**Files:**
- Create: `src/CodebaseGuardian/GitHub/GitHubOptions.cs`, `GitHubRepositoryRef.cs`, `IGitHubTokenProvider.cs`, `GitHubTokenProvider.cs`, `IGitHubRepositoryResolver.cs`, `GitHubRepositoryResolver.cs`, `GitHubModels.cs`, `IGitHubClient.cs`, `GitHubClient.cs`, `GitHubExceptions.cs`
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs` (bind `GitHubOptions`, register provider/resolver singletons, `services.AddHttpClient<IGitHubClient, GitHubClient>(GitHubClient.HttpClientName)`)
- Modify: `tests/CodebaseGuardian.Tests/Infrastructure/GuardianTestHost.cs` (default `Guardian:GitHub:Enabled=false` unless the caller overrides it)
- Create: `tests/CodebaseGuardian.Tests/Infrastructure/FakeGitHubApi.cs`
- Create: `tests/CodebaseGuardian.Tests/App/GitHubRepositoryRefTests.cs`, `GitHubTokenProviderTests.cs`, `GitHubClientTests.cs`

**Interfaces:**
- Consumes: `IProcessRunner`, `ProcessSpec` (Task 2), `IGitRepository.GetRemoteUrlAsync` (Task 2), `FakeProcessRunner` (Task 10).
- Produces:

```csharp
namespace CodebaseGuardian.GitHub;

public sealed class GitHubOptions
{
    public const string SectionName = "Guardian:GitHub";
    public bool Enabled { get; set; } = true;               // false => every IGitHubClient call throws GitHubUnavailableException
    public string ApiBaseUrl { get; set; } = "https://api.github.com";
    public string? Owner { get; set; }                      // Owner+Repository both set => used; else derived from the origin remote
    public string? Repository { get; set; }
    public bool PollEnabled { get; set; } = true;           // Task 18
    public int PollIntervalSeconds { get; set; } = 60;      // Task 18; validated >= 15
}

public sealed record GitHubRepositoryRef(string Owner, string Name)
{
    // Accepts https://github.com/o/r, https://github.com/o/r.git, https://<user>@github.com/o/r.git, git@github.com:o/r(.git),
    // ssh://git@github.com/o/r(.git); trailing '/' tolerated; host github.com only (case-insensitive);
    // owner and name must match ^[A-Za-z0-9_.-]{1,100}$. Anything else => false.
    public static bool TryParseRemoteUrl(string url, [NotNullWhen(true)] out GitHubRepositoryRef? repository);
    public override string ToString() => $"{Owner}/{Name}";
}

public interface IGitHubTokenProvider
{
    // GITHUB_TOKEN env var (non-empty) wins; else `gh auth token` (10 s timeout) stdout trimmed; ExecutableNotFound/non-zero/empty => null.
    // A found token is cached for the process lifetime. The token is never logged.
    ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken);
}

public interface IGitHubRepositoryResolver
{
    ValueTask<GitHubRepositoryRef?> ResolveAsync(CancellationToken cancellationToken);  // options override, else parse origin remote; cached once resolved
}

public sealed record GitHubUser(string Login);
public sealed record GitHubIssue(long Id, int Number, string Title, string? Body, string HtmlUrl, GitHubUser User,
    IReadOnlyList<string> Labels, DateTimeOffset CreatedAt, bool IsPullRequest);
public sealed record GitHubComment(long Id, string Kind /* "issue" | "review" */, int IssueNumber, string? Body, string HtmlUrl,
    GitHubUser User, DateTimeOffset CreatedAt, string? Path, int? Line, bool OnPullRequest);
public sealed record GitHubPullRequest(long Id, int Number, string Title, string HtmlUrl, string Head, string Base, bool Draft);
public sealed record GitHubWorkflowRun(long Id, int RunAttempt, string Name, string HeadBranch, string HeadSha, string HtmlUrl,
    string? Conclusion, DateTimeOffset UpdatedAt);

public interface IGitHubClient
{
    Task<GitHubRepositoryRef> GetRepositoryAsync(CancellationToken ct);   // throws GitHubUnavailableException (disabled / no token / no github.com origin)
    Task<string> GetDefaultBranchAsync(CancellationToken ct);
    Task<bool> BranchExistsAsync(string branch, CancellationToken ct);
    Task<GitHubIssue> CreateIssueAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken ct);
    Task<GitHubComment> CreateIssueCommentAsync(int number, string body, CancellationToken ct);
    Task<GitHubPullRequest> CreatePullRequestAsync(string head, string @base, string title, string body, bool draft, CancellationToken ct);
    Task<IReadOnlyList<GitHubIssue>> ListIssuesSinceAsync(DateTimeOffset since, CancellationToken ct);          // includes PRs, flagged IsPullRequest
    Task<IReadOnlyList<GitHubComment>> ListIssueCommentsSinceAsync(DateTimeOffset since, CancellationToken ct); // Kind "issue"
    Task<IReadOnlyList<GitHubComment>> ListReviewCommentsSinceAsync(DateTimeOffset since, CancellationToken ct);// Kind "review", OnPullRequest true
    Task<IReadOnlyList<GitHubWorkflowRun>> ListFailedWorkflowRunsSinceAsync(DateTimeOffset since, CancellationToken ct);
}

public class GitHubApiException(int statusCode, string message) : Exception(message) { public int StatusCode { get; } = statusCode; }
public sealed class GitHubRateLimitException(DateTimeOffset resetAt)
    : GitHubApiException(429, $"GitHub rate limit reached; resets at {resetAt:O}.") { public DateTimeOffset ResetAt { get; } = resetAt; }
public sealed class GitHubUnavailableException(string reason) : Exception(reason);

public sealed class GitHubClient : IGitHubClient
{
    public const string HttpClientName = "github";
}
```

Test helper (namespace `CodebaseGuardian.Tests.Infrastructure`):

```csharp
public sealed class FakeGitHubApi : HttpMessageHandler
{
    public const string Token = "test-token-not-a-secret";
    // Route by method + path (query ignored for matching, available on the recorded request). Unmatched => 404 + recorded in Unmatched.
    public FakeGitHubApi Map(HttpMethod method, string path, Func<HttpRequestMessage, string, HttpResponseMessage> respond); // (request, requestBody) => response
    public FakeGitHubApi MapJson(HttpMethod method, string path, int status, string json, IDictionary<string, string>? headers = null);
    public IReadOnlyList<(HttpMethod Method, string PathAndQuery, string Body, IReadOnlyDictionary<string, string> Headers)> Requests { get; }
    public IReadOnlyList<string> Unmatched { get; }
    // Replaces IGitHubTokenProvider with one returning Token, pins Guardian:GitHub Owner/Repository to acme/widgets via
    // IGitHubRepositoryResolver, and sets this handler as the primary handler of the "github" named client.
    public void Install(IServiceCollection services);
}
```

**Behaviour:**
- Every request: `Accept: application/vnd.github+json`, `X-GitHub-Api-Version: 2022-11-28`, `User-Agent: codebase-guardian`, `Authorization: Bearer <token>`; base address `ApiBaseUrl`; JSON via internal DTOs with `JsonNamingPolicy.SnakeCaseLower`.
- Endpoints (`{o}/{r}` from the resolver):
  - `GET /repos/{o}/{r}` → `default_branch`.
  - `GET /repos/{o}/{r}/branches/{branch}` → 200 true, 404 false (branch name URL-escaped; validated with `GitRevision` first).
  - `POST /repos/{o}/{r}/issues` `{title, body, labels}`; `POST /repos/{o}/{r}/issues/{n}/comments` `{body}`; `POST /repos/{o}/{r}/pulls` `{title, head, base, body, draft}`.
  - `GET /repos/{o}/{r}/issues?state=all&sort=created&direction=desc&since=<iso>&per_page=100` (an item with a `pull_request` key → `IsPullRequest`).
  - `GET /repos/{o}/{r}/issues/comments?sort=created&direction=asc&since=<iso>&per_page=100` (`OnPullRequest` = `html_url` contains `/pull/`; `IssueNumber` parsed from `issue_url`'s last segment).
  - `GET /repos/{o}/{r}/pulls/comments?sort=created&direction=asc&since=<iso>&per_page=100` (`IssueNumber` from `pull_request_url`; `path`, `line`).
  - `GET /repos/{o}/{r}/actions/runs?status=failure&created=>=<yyyy-MM-ddTHH:mm:ssZ>&per_page=100` → `workflow_runs[]` (`id`, `run_attempt`, `name`, `head_branch`, `head_sha`, `html_url`, `conclusion`, `updated_at`).
- Pagination: follow `Link: <…>; rel="next"` for at most 5 pages.
- Errors: 401 → `GitHubApiException(401, "GitHub rejected the token (401).")`; 403/429 with `x-ratelimit-remaining: 0` → `GitHubRateLimitException(resetAt = x-ratelimit-reset epoch seconds)`, with `retry-after` → now + seconds; other non-2xx → `GitHubApiException(status, <GitHub "message"> + "; " + errors[].message)` cut to 500 chars. Messages never contain the token or request headers.
- `GetRepositoryAsync` (and every other method, which call it first): `Enabled == false` → `GitHubUnavailableException("GitHub integration is disabled (Guardian:GitHub:Enabled=false).")`; no token → `"No GitHub token: set GITHUB_TOKEN or run `gh auth login`."`; no GitHub repo → `"The origin remote is not a github.com repository; set Guardian:GitHub:Owner and Guardian:GitHub:Repository."`.
- `GitHubTokenProvider` takes an internal constructor seam `Func<string, string?> getEnvironmentVariable` (default `Environment.GetEnvironmentVariable`) so tests never touch the real environment.
- `GuardianTestHost` defaults `Guardian:GitHub:Enabled=false` so no test ever reaches api.github.com, even on a machine with `GITHUB_TOKEN` or `gh` logged in.

**Tests (write first):**
- `GitHubRepositoryRefTests`: the five accepted URL forms → `acme/widgets`; `https://gitlab.com/acme/widgets.git`, `https://github.com/acme`, `https://github.com/acme/widgets/extra`, `file:///tmp/x` → false.
- `GitHubTokenProviderTests` (`FakeProcessRunner`): env var set → returned, `gh` never called; env empty + `gh auth token` exit 0 `"gho_x\n"` → `"gho_x"`; `gh` missing (`ExecutableNotFoundException`) → null; exit 1 → null; second call uses the cache (one `gh` call).
- `GitHubClientTests` (client built over `FakeGitHubApi`): headers present on every request (`Authorization` equals `Bearer test-token-not-a-secret`); create issue posts the JSON body and parses `number`/`html_url`; issues list marks PRs; issue comments parse `IssueNumber` and `OnPullRequest`; review comments parse `path`/`line`; workflow runs parse attempt and conclusion; two-page `Link` pagination returns both pages; 404 on branch → false; 403 + `x-ratelimit-remaining: 0` + `x-ratelimit-reset` → `GitHubRateLimitException` with that reset time; 422 → message contains GitHub's message and never `test-token-not-a-secret`; disabled / no token / non-GitHub origin → `GitHubUnavailableException` with the reasons above.

**Acceptance:**
- [ ] `dotnet test` green with no network access.
- [ ] Commit: `feat(github): GitHub REST client with token and repository resolution`

### Task 17: GitHub action tools with confirmation (`create_issue`, `comment_on_pr`, `open_pull_request`)

**Goal:** Outward-facing GitHub actions that never run without the user's confirmation: MRTR elicitation when the client can show a prompt, an explicit `confirm: true` argument otherwise.

**Files:**
- Create: `src/CodebaseGuardian/GitHub/ActionConfirmation.cs` (`IActionConfirmation`, `ActionConfirmation`, `ConfirmationStatus`)
- Create: `src/CodebaseGuardian/GitHub/OutboundTextGuard.cs` (secret check on text sent to GitHub)
- Create: `src/CodebaseGuardian/Tools/GitHubTools.cs`
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs` (`.WithTools<GitHubTools>()`, confirmation singleton)
- Modify: `src/CodebaseGuardian/CodebaseGuardian.csproj` (NoWarn for any MRTR experimental ID the compiler reports)
- Create: `tests/CodebaseGuardian.Tests/App/ActionConfirmationTests.cs`, `GitHubToolsTests.cs`

**Interfaces:**
- Consumes: `IGitHubClient`, `GitHubUnavailableException`, `GitHubApiException` (Task 16); `ISecretScanner.ScanText` (Task 11); `GitRevision` (Task 2); `ToolErrors` (Task 3); `FakeGitHubApi`, `FakeSecrets`, `GuardianHttpTestHost` (Tasks 16, 11, 14).
- Produces:

```csharp
namespace CodebaseGuardian.GitHub;

public enum ConfirmationStatus { Confirmed, Declined, ConfirmArgumentRequired }

public interface IActionConfirmation
{
    // Prompt path when server.IsMrtrSupported AND the client declared elicitation
    // (context.JsonRpcRequest.Context?.ClientCapabilities?.Elicitation ?? server.ClientCapabilities?.Elicitation is not null):
    //   first round (no context.Params.RequestState): throws InputRequiredException with
    //     inputRequests {"confirm": InputRequest.ForElicitation(new ElicitRequestParams { Message = summary, RequestedSchema = new() })}
    //     and requestState = ConfirmationToken(toolName, arguments-without-"confirm");
    //   retry: RequestState must equal the recomputed token, else McpException("Confirmation does not match this request; call the tool again.");
    //     InputResponses["confirm"] deserialized with InputResponse.ElicitResultJsonTypeInfo; Action "accept" => Confirmed, anything else => Declined.
    //   The confirm argument is IGNORED on this path (a model-supplied flag must not skip a prompt the user could have seen).
    // Fallback path (no MRTR or no elicitation): confirmArgument ? Confirmed : ConfirmArgumentRequired.
    ConfirmationStatus Confirm(McpServer server, RequestContext<CallToolRequestParams> context, string summary, bool confirmArgument);
}

public static class OutboundTextGuard
{
    // Throws McpException("The <field> contains what looks like a secret (<ruleId> <redacted> on line <n>); remove it before sending.")
    // for the first finding; never includes the raw value.
    public static void EnsureNoSecrets(ISecretScanner scanner, string field, string text);
}
```

- `ConfirmationToken` (private helper inside `ActionConfirmation`): `base64url(HMAC-SHA256(key, UTF-8(toolName + "\n" + JSON)))`, where JSON is the arguments object with `confirm` removed and top-level keys sorted ordinally, values written with `JsonElement.GetRawText()`, no whitespace between members; `key` = 32 random bytes generated once per process. Forged or replayed state from another request fails the comparison (`CryptographicOperations.FixedTimeEquals`).

**Tools** (`[McpServerTool(ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]`; descriptions say "Writes to GitHub. The user is asked to confirm; if your client cannot show prompts, ask the user yourself and pass confirm: true."):

| Tool | Arguments | Validation (before any prompt) | GitHub call | Structured result |
|---|---|---|---|---|
| `create_issue` | `title`, `body`, `labels` (string[]?, default none), `confirm` (bool, default false) | title 1..256 chars; body ≤ 65 536; ≤ 10 labels, each 1..50 | `CreateIssueAsync` | `{status: "created", number, url}` |
| `comment_on_pr` | `number` (int), `body`, `confirm` | number ≥ 1; body 1..65 536 | `CreateIssueCommentAsync` | `{status: "created", commentId, url}` |
| `open_pull_request` | `head`, `title`, `body`, `base` (string?, default = repo default branch), `draft` (bool, default false), `confirm` | `GitRevision.Require` for head/base; head ≠ base; title 1..256; body ≤ 65 536; `BranchExistsAsync(head)` false → error "Branch '<head>' is not on GitHub; push it first." | `CreatePullRequestAsync` | `{status: "created", number, url}` |

- Order inside each tool: validate arguments → `OutboundTextGuard` on title and body → `IGitHubClient.GetRepositoryAsync` (availability) → PR-only branch checks → `Confirm(...)` with a one-line summary (`Create issue in acme/widgets: "<title>" [labels: a, b]`, `Comment on acme/widgets#12: "<first 120 chars of body>"`, `Open pull request in acme/widgets: <head> → <base> "<title>"`) → GitHub call.
- `Declined` → structured `{status: "declined"}`, `isError: false`, text "The user declined; nothing was sent to GitHub." `ConfirmArgumentRequired` → tool error "This action writes to GitHub. Your client cannot show a confirmation prompt, so ask the user to approve it, then call again with confirm: true."
- `GitHubUnavailableException` / `GitHubApiException` → tool errors carrying their message.
- These tools must stay synchronous: SDK 2.2.0 cannot combine MRTR with the Tasks extension (Task 22 pins them to `Synchronous`).

**Tests (write first)** — `GuardianTestHost` with `Guardian:GitHub:Enabled=true` and `FakeGitHubApi.Install` (map `GET /repos/acme/widgets` → `{"default_branch":"main"}`, the POST endpoints, and `GET /repos/acme/widgets/branches/feature` → 200):
- client with `Handlers.ElicitationHandler` returning `accept` → `create_issue` creates the issue: one `POST /repos/acme/widgets/issues` with the title/body/labels; result `status == "created"`, `number` from the fake; the handler saw the summary text.
- handler returning `decline` → `status == "declined"`, no POST recorded.
- handler accepting **and** the call passing `confirm: true` → the handler is still invoked exactly once (the flag does not bypass the prompt).
- client without an elicitation handler: no `confirm` → `isError` with the "confirm: true" guidance, no POST; with `confirm: true` → POST happens.
- body containing `FakeSecrets.GitHubToken()` → `isError`, the message contains the redacted form and not the raw token, no prompt shown, no POST.
- `open_pull_request` with `head: "missing"` (fake branch 404) → `isError` "push it first"; with `head: "feature"` and no `base` → POST body has `"base":"main"`.
- `comment_on_pr` with `number: 0` → `isError`; valid call posts to `/repos/acme/widgets/issues/12/comments`.
- GitHub disabled (default host) → `isError` with the disabled reason; all three tools still appear in `tools/list` with `openWorldHint: true`.
- legacy client (`McpClientOptions.ProtocolVersion = "2025-11-25"`) with an elicitation handler over the in-process stream transport → prompt shown and issue created.
- over `GuardianHttpTestHost` (stateless HTTP, default 2026-07-28 client) with an accepting handler → issue created.
- `ActionConfirmationTests`: token differs when any argument except `confirm` changes; identical for reordered keys; a retry carrying a tampered `RequestState` → error.

**Acceptance:**
- [ ] `dotnet test` green.
- [ ] Commit: `feat(github): create_issue, comment_on_pr and open_pull_request with user confirmation`

### Task 18: GitHub event poller and GitHub events; skills learn the GitHub flow

**Goal:** Emit `github.issue.opened`, `github.pr.comment.created` and `github.ci.failed` from polling the GitHub API, with secrets redacted from relayed text, and teach the bundled skills to react to them.

**Files:**
- Create: `src/CodebaseGuardian/GitHub/GitHubEventPoller.cs` (`BackgroundService`)
- Modify: `src/CodebaseGuardian/Watching/GuardianEvents.cs` (add `public static void RegisterGitHub(EventsOptions options)`)
- Modify: `src/CodebaseGuardian/Security/SecretScanner.cs` and `ISecretScanner` (add `string RedactSecrets(string text)`)
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs` (call `RegisterGitHub` when `GitHubOptions.Enabled`; add the poller hosted service when `Enabled && PollEnabled`)
- Modify: `src/CodebaseGuardian/skills/guardian/SKILL.md`, `skills/guardian/references/events.md`, `skills/bug-triage/SKILL.md`, `skills/pr-review/SKILL.md`
- Modify: `tests/CodebaseGuardian.Tests/App/SkillContentTests.cs` (start its host with `Guardian:GitHub:Enabled=true`, `Guardian:GitHub:PollEnabled=false` and `FakeGitHubApi.Install`, so GitHub events and tools exist for the lint)
- Modify: `tests/CodebaseGuardian.Tests/App/GuardianEventsTests.cs` (seven names with GitHub disabled; ten with it enabled)
- Create: `tests/CodebaseGuardian.Tests/App/GitHubEventPollerTests.cs`, `tests/CodebaseGuardian.Tests/App/SecretRedactionTests.cs`

**Interfaces:**
- Consumes: `IGitHubClient`, `GitHubOptions`, `GitHubRateLimitException` (Task 16); `IEventPublisher` (Task 6); `ISecretScanner`, `Redactor` (Task 11); `GuardianEventNames`, `SuggestedSkills` (Task 9); `EventsPolling.WaitForAsync` (Task 9).
- Produces:

```csharp
namespace CodebaseGuardian.GitHub;
public sealed class GitHubEventPoller(IGitHubClient client, IEventPublisher publisher, ISecretScanner scanner,
    IOptions<GitHubOptions> options, TimeProvider timeProvider, ILogger<GitHubEventPoller> logger) : BackgroundService
{
    internal Task PollOnceAsync(CancellationToken cancellationToken);   // one cycle; test seam (InternalsVisibleTo the test project)
}

namespace CodebaseGuardian.Security;
// New member on the existing ISecretScanner (Task 11):
string RedactSecrets(string text);   // every rule match replaced by Redactor.Redact(<secret>); text without matches returned unchanged
```

**Events (exact field names):**

| Event | inputSchema properties (`Matches`) | data | eventId |
|---|---|---|---|
| `github.issue.opened` | `label` (string; any label equals it) | `number, title, author, url, labels[], body` (≤ 2000 chars, redacted), `suggestedSkill: skill://bug-triage/SKILL.md` | `gh_issue_<id>` |
| `github.pr.comment.created` | `prNumber` (integer; equality) | `prNumber, commentId, author, body` (≤ 2000, redacted), `url, path` (string or null), `line` (integer or null), `suggestedSkill: skill://pr-review/SKILL.md` | `gh_comment_<id>` (issue comment) / `gh_reviewcomment_<id>` |
| `github.ci.failed` | `branch` (string; equality with `branch`) | `runId, workflowName, branch, headSha, url, conclusion, suggestedSkill: skill://bug-triage/SKILL.md` | `gh_run_<id>_<runAttempt>` |

Descriptions end with "(requires a GitHub token and a github.com origin)". Timestamps passed to `PublishAsync` = the GitHub `created_at` (runs: `updated_at`).

**Poller behaviour:**
- `startedAt` = time of the first cycle; nothing created before it is ever published (no backfill).
- Each cycle queries with `since = max(startedAt, lastCycleStart − 5 min)` (overlap tolerates clock skew), filters `CreatedAt >= startedAt` (runs: `UpdatedAt >= startedAt` and `Conclusion == "failure"`), skips issues with `IsPullRequest`, skips issue comments with `OnPullRequest == false`, and remembers published event ids in a bounded set (10 000) so overlaps never duplicate (the event log's eventId dedupe is the second line of defence).
- Bodies: `RedactSecrets` first, then cut to 2000 chars (`…` appended when cut).
- `GitHubUnavailableException` → log one warning (first occurrence only) and idle until the next interval. `GitHubRateLimitException` → no HTTP calls until `ResetAt`. Other exceptions → logged; the loop continues. Interval `PollIntervalSeconds` via `TimeProvider`.

**Skill content changes** (Task 13 rules apply; the lint must stay green):
- `guardian`: add the three GitHub rows to the event → skill table; one line: "Actions that write to GitHub (`create_issue`, `comment_on_pr`, `open_pull_request`) always ask the user to confirm."
- `bug-triage`: steps for `github.ci.failed` (open the run `url`, map `headSha` to local history with `recent_commits`/`diff_summary`) and `github.issue.opened` (reproduce with `run_checks`); filing the report with `create_issue` using `assets/issue-template.md`.
- `pr-review`: steps for `github.pr.comment.created` (read `path`/`line` context with `diff_summary`), reply with `comment_on_pr` using `assets/review-comment-template.md`.

**Tests (write first):**
- `SecretRedactionTests`: text with a fake AWS key and a fake GitHub token → both replaced by their redacted forms; text without secrets unchanged.
- `GitHubEventPollerTests` — host with `Guardian:GitHub:Enabled=true`, `PollEnabled=false` (drive cycles manually via `PollOnceAsync` resolved from DI), `FakeTimeProvider`, `FakeGitHubApi`:
  - cycle 1 at t0 with an issue created at t0−1h → nothing published.
  - issue created at t0+1 min → cycle 2 publishes one `github.issue.opened` (`eventId == "gh_issue_<id>"`, labels, author, suggestedSkill); cycle 3 with the same API data → no new event.
  - an issue-list item with `pull_request` → no issue event.
  - issue comment whose `html_url` contains `/pull/7` → `github.pr.comment.created` with `prNumber == 7`, `path == null`; comment on a plain issue → nothing; review comment → event with `path` and `line`.
  - failed run attempt 1 and later attempt 2 → two `github.ci.failed` events with ids `gh_run_<id>_1` / `gh_run_<id>_2`.
  - issue body containing `FakeSecrets.GitHubToken()` → event `data.body` contains the redacted form, not the raw token.
  - 403 with `x-ratelimit-remaining: 0`, reset t+10 min → the next cycle at t+1 min makes zero requests; a cycle after t+10 min requests again.
  - `events/poll` argument filters: `label: "bug"`, `prNumber: 7`, `branch: "main"`.
- `GuardianEventsTests` updated as listed; `SkillContentTests` green with the new skill text.

**Acceptance:**
- [ ] `dotnet test` green with no network access.
- [ ] Commit: `feat(github): GitHub event poller, github.* events and GitHub-aware skills`

### Task 19: HTTP bearer authentication and the events principal

**Goal:** Optional API-key bearer auth for the HTTP transport, an authenticated principal visible to custom handlers, and the switch that turns webhook delivery on (authenticated HTTP only, ruling R5).

**Files:**
- Create: `src/CodebaseGuardian/Hosting/HttpAuthOptions.cs`, `src/CodebaseGuardian/Hosting/ApiKeyAuthenticationHandler.cs`, `src/CodebaseGuardian/Hosting/WebhookHostingOptions.cs`
- Modify: `src/CodebaseGuardian/Hosting/HttpHost.cs` (authentication/authorization when keys exist; `MapMcp("/mcp").RequireAuthorization()`; `/healthz` stays anonymous; remote-binding rule)
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs` (set `EventsOptions.WebhooksEnabled`)
- Create: `src/Mcp.Events/EventsPrincipal.cs`
- Modify: `src/Mcp.Events/EventsOptions.cs` (add `PrincipalResolver`)
- Modify: `tests/CodebaseGuardian.Tests/Infrastructure/GuardianHttpTestHost.cs` (add `ConnectAsync(string bearerToken, ...)` overload and `CreateHttpClient()`)
- Create: `tests/CodebaseGuardian.Tests/App/HttpAuthTests.cs`, `tests/CodebaseGuardian.Tests/Events/EventsPrincipalTests.cs`

**Interfaces:**
- Consumes: `HttpHost`, `GuardianHttpTestHost` (Task 14); `EventsOptions` (Task 6).
- Produces:

```csharp
namespace CodebaseGuardian.Hosting;
public sealed class HttpAuthOptions
{
    public const string SectionName = "Guardian:Http";
    public Dictionary<string, string> ApiKeys { get; } = new(StringComparer.Ordinal);   // principal -> key
}
public sealed class WebhookHostingOptions
{
    public const string SectionName = "Guardian:Webhooks";
    public bool Enabled { get; set; } = true;
    public bool AllowInsecureLoopback { get; set; }      // development flag (spec §7); copied into EventsOptions.Webhooks in Task 20
}
internal sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "GuardianApiKey";
}

namespace Mcp.Events;
public static class EventsPrincipal
{
    // request.Context?.User with Identity.IsAuthenticated: claim "sub", then ClaimTypes.NameIdentifier, then Identity.Name; otherwise null.
    public static string? FromRequest(JsonRpcRequest request);
}
// EventsOptions: public Func<JsonRpcRequest, string?> PrincipalResolver { get; set; } = EventsPrincipal.FromRequest;

// tests/Infrastructure, GuardianHttpTestHost additions
public Task<McpClient> ConnectAsync(string bearerToken, McpClientOptions? options = null, CancellationToken cancellationToken = default); // AdditionalHeaders["Authorization"] = "Bearer <token>"
public HttpClient CreateHttpClient();   // BaseAddress = server root, no auth header
```

**Behaviour:**
- Validation at startup: every principal matches `^[A-Za-z0-9._@-]{1,64}$`; every key ≥ 32 chars; failure message names `Guardian:Http:ApiKeys`.
- Handler: reads `Authorization: Bearer <key>`; compares `SHA256(key)` against each configured key's hash with `CryptographicOperations.FixedTimeEquals` (no early exit on match); success → `ClaimsPrincipal` with `sub` and `ClaimTypes.NameIdentifier` = principal, authentication type `GuardianApiKey`; missing/invalid → challenge 401 with `WWW-Authenticate: Bearer`. Keys never logged.
- No keys configured → no authentication middleware (Task 14 behaviour unchanged).
- Remote binding (replaces the Task 14 rule): non-loopback `HttpUrl` requires `HttpAllowRemote == true` **and** at least one API key; otherwise startup fails with a message naming both settings.
- `EventsOptions.WebhooksEnabled = Transport == Http && ApiKeys.Count > 0 && WebhookHostingOptions.Enabled`. stdio never offers webhooks.
- The SDK copies `HttpContext.User` into `JsonRpcRequest.Context.User` for authenticated requests (verified in SDK source, `StreamableHttpHandler`), so `EventsPrincipal.FromRequest` works in stateless mode.

**Tests (write first):**
- `EventsPrincipalTests`: request with authenticated user carrying `sub=alice` → `"alice"`; only `NameIdentifier` → that value; unauthenticated identity → null; `Context == null` → null.
- `HttpAuthTests` — `GuardianHttpTestHost` with `Guardian:Http:ApiKeys:alice=<40-char test key>`:
  - raw POST to `/mcp` without `Authorization` → 401 with `WWW-Authenticate: Bearer`; wrong key → 401.
  - `ConnectAsync(key)` → `tools/list` succeeds; `GET /healthz` without auth → 200.
  - a test-only tool (registered through `configureServices`) returning `RequestContext<CallToolRequestParams>.User` `sub` → `"alice"`.
  - `events/list` → every event's `delivery` contains `"webhook"`; same server without keys → no `"webhook"`; `GuardianTestHost` (stdio-equivalent) → no `"webhook"`.
  - key of 10 chars → startup throws naming `Guardian:Http:ApiKeys`.
  - `HttpUrl=http://0.0.0.0:0` with `HttpAllowRemote=true` and no keys → `HttpHost.Build` throws naming `HttpAllowRemote` and `Guardian:Http:ApiKeys`; with a key → builds (do not start it).

**Acceptance:**
- [ ] `dotnet test` green.
- [ ] Commit: `feat(http): API key bearer auth, events principal and webhook enablement`

### Task 20: Webhook subscriptions (`events/subscribe`, `events/unsubscribe`) with endpoint verification

**Goal:** The subscription half of webhook delivery in `Mcp.Events`: parameter and URL validation, deterministic ids, idempotent upsert, TTL grants, Standard Webhooks signing, SSRF-guarded sending and the challenge handshake. Delivery of events is Task 21.

**Files:**
- Create in `src/Mcp.Events/Webhooks/` (namespace `Mcp.Events`): `WebhookOptions.cs`, `CanonicalJson.cs`, `SubscriptionKey.cs`, `WebhookSecret.cs`, `WebhookSigner.cs`, `WebhookUrlPolicy.cs`, `WebhookAddressPolicy.cs`, `IWebhookSender.cs`, `WebhookHttpSender.cs`, `WebhookSubscription.cs`, `WebhookSubscriptionStore.cs`, `WebhookSubscribeHandler.cs`
- Modify: `src/Mcp.Events/EventsOptions.cs` (add `Webhooks`), `src/Mcp.Events/EventsConfigureOptions.cs` (register the two methods), `src/Mcp.Events/EventsServerBuilderExtensions.cs` (register store, sender), `src/Mcp.Events/IEventLog.cs` + `InMemoryEventLog.cs` (add `CursorAt`, `Seek`; if Task 8's stream handler has equivalent private seek logic, move it here and call it from the stream handler)
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs` (copy `WebhookHostingOptions.AllowInsecureLoopback` into `EventsOptions.Webhooks`)
- Create: `tests/CodebaseGuardian.Tests/Infrastructure/WebhookReceiver.cs`
- Create: `tests/CodebaseGuardian.Tests/Events/WebhookPrimitivesTests.cs`, `WebhookSubscribeProtocolTests.cs`, `tests/CodebaseGuardian.Tests/App/WebhookEndToEndTests.cs`

**Interfaces:**
- Consumes: `EventsRequestParser`, `EventJson` (Task 7); `IEventLog` (Task 6); `EventsOptions.WebhooksEnabled`, `PrincipalResolver` (Tasks 6, 19); `GuardianHttpTestHost.ConnectAsync(bearerToken)` (Task 19).
- Produces:

```csharp
namespace Mcp.Events;

public sealed class WebhookOptions
{
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan MinTtl { get; set; } = TimeSpan.FromSeconds(60);       // R9 (tests may lower it)
    public TimeSpan MaxTtl { get; set; } = TimeSpan.FromHours(24);         // R9
    public int MaxSubscriptionsPerPrincipal { get; set; } = 100;
    public bool AllowInsecureLoopback { get; set; }                         // dev flag: http:// and loopback/private targets allowed
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan SecretRotationGrace { get; set; } = TimeSpan.FromMinutes(5);
    public IList<TimeSpan> RetryDelays { get; } = new List<TimeSpan> { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2) }; // Task 21
    public TimeSpan RetryWindow { get; set; } = TimeSpan.FromMinutes(15);  // Task 21
    public double SuspendFailureRate { get; set; } = 0.95;                 // Task 21
    public int SuspendMinAttempts { get; set; } = 100;                     // Task 21
    public TimeSpan SuspendWindow { get; set; } = TimeSpan.FromMinutes(60);// Task 21
    public Func<string, CancellationToken, ValueTask<IPAddress[]>>? ResolveHost { get; set; }  // test seam; null => Dns.GetHostAddressesAsync
}
// EventsOptions: public WebhookOptions Webhooks { get; } = new();

public static class CanonicalJson
{
    // Object keys sorted ordinally (string.CompareOrdinal), no whitespace, Utf8JsonWriter with JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    // numbers and strings as System.Text.Json writes them. null node => "null".
    public static string Serialize(JsonNode? node);
}

public static class SubscriptionKey
{
    // R11: "sub_" + first 16 lowercase hex of SHA-256(UTF-8(CanonicalJson.Serialize([principal, url, name, arguments ?? {}])))
    public static string ComputeId(string principal, string url, string name, JsonObject? arguments);
}

public static class WebhookSecret
{
    // "whsec_" + standard base64 (padded or unpadded) decoding to 24..64 bytes.
    public static bool TryParse(string? value, [NotNullWhen(true)] out byte[]? key);
}

public static class WebhookSigner
{
    // "v1,<base64 HMAC-SHA256(key, UTF-8(messageId + "." + timestamp + ".") + body)>" per key, space-separated, in key order.
    public static string Sign(string messageId, long timestampSeconds, ReadOnlySpan<byte> body, IReadOnlyList<byte[]> keys);
}

public static class WebhookAddressPolicy
{
    // Unwraps IPv4-mapped IPv6 first. Blocks: 0.0.0.0/8, 10/8, 100.64/10, 127/8, 169.254/16, 172.16/12, 192.0.0/24, 192.168/16,
    // 198.18/15, 224/4, 240/4, 255.255.255.255, ::, ::1, fc00::/7, fe80::/10, ff00::/8, 64:ff9b::/96.
    public static bool IsBlocked(IPAddress address);
}

public static class WebhookUrlPolicy
{
    // Absolute URI, ≤ 2048 chars, no userinfo, no fragment; scheme https, or http only when allowInsecureLoopback.
    public static bool TryValidate(string? url, bool allowInsecureLoopback, [NotNullWhen(true)] out Uri? uri, out string? problem);
}

public sealed record WebhookSendResult(bool Delivered, int? StatusCode, string? ErrorCategory, string? Body);
// Delivered = 2xx. ErrorCategory ∈ connection_refused | timeout | tls_error | http_4xx | http_5xx (null when Delivered).
// Body = first 4 KiB of the response, used only for the challenge comparison and never surfaced.

public interface IWebhookSender
{
    // Builds headers: Content-Type application/json, webhook-id, webhook-timestamp (unix seconds now), webhook-signature (all keys),
    // X-MCP-Subscription-Id. Never throws for endpoint failures (returns a category); throws OperationCanceledException on cancellation.
    Task<WebhookSendResult> SendAsync(Uri url, string messageId, ReadOnlyMemory<byte> body, string subscriptionId,
        IReadOnlyList<byte[]> keys, CancellationToken cancellationToken);
}

public sealed class WebhookSubscription
{
    public required string Id { get; init; }
    public required string Principal { get; init; }
    public required Uri Url { get; init; }
    public required string Name { get; init; }
    public required JsonObject Arguments { get; init; }
    public byte[] Secret { get; set; } = [];
    public byte[]? PreviousSecret { get; set; }
    public DateTimeOffset? PreviousSecretExpiresAt { get; set; }
    public DateTimeOffset RefreshBefore { get; set; }
    public long Position { get; set; }               // watermark: every event with Sequence <= Position is acked or abandoned
    public bool Active { get; set; } = true;
    public DateTimeOffset? LastDeliveryAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? FailedSince { get; set; }
    public IReadOnlyList<byte[]> SigningKeys(DateTimeOffset now);   // current, plus previous while its grace lasts
}

public sealed class WebhookSubscriptionStore
{
    public bool TryGet(string id, [NotNullWhen(true)] out WebhookSubscription? subscription);
    public void Add(WebhookSubscription subscription);
    public bool Remove(string id);
    public int CountFor(string principal);
    public IReadOnlyList<WebhookSubscription> Snapshot();
    public event Action<WebhookSubscription>? Changed;   // raised on add/refresh/remove (Task 21 listens)
    public object SyncRoot { get; }                       // lock for mutating a subscription
}

// IEventLog additions
string CursorAt(long sequence);
(long Sequence, bool Truncated) Seek(string? cursor, TimeSpan? maxAge);   // null cursor => (head, false); same truncation rules as Read
```

Test helper:

```csharp
public sealed class WebhookReceiver : IAsyncDisposable
{
    public static Task<WebhookReceiver> StartAsync(CancellationToken ct);   // Kestrel on http://127.0.0.1:0, POST on any path
    public Uri Url { get; }                                                  // http://127.0.0.1:<port>/hooks
    public string Secret { get; }                                            // "whsec_" + base64(32 random bytes)
    public bool EchoChallenges { get; set; } = true;                         // reply {"challenge": <nonce>} to verification envelopes
    public Func<ReceivedWebhook, int>? StatusFor { get; set; }               // default 200
    public TaskCompletionSource? Gate { get; set; }                          // when set, requests wait for it before responding
    public IReadOnlyList<ReceivedWebhook> Received { get; }
    public Task<ReceivedWebhook> WaitForAsync(Func<ReceivedWebhook, bool> match, TimeSpan timeout);
    // Independent HMAC check (does not call WebhookSigner): true when any v1 entry matches key.
    public static bool SignatureValid(ReceivedWebhook request, byte[] key);
}
public sealed record ReceivedWebhook(IReadOnlyDictionary<string, string> Headers, byte[] Body, DateTimeOffset ReceivedAt)
{
    public JsonObject Json { get; }
}
```

**Behaviour — `events/subscribe` (wire-format B7, B9, B10, B12; rulings R2, R5, R9–R12):**
1. `principal = options.PrincipalResolver(request)`; null → `-32012` "events/subscribe requires an authenticated principal".
2. Parse `name`, `arguments`, `cursor`, `maxAgeMs` with `EventsRequestParser` (NotFound / InvalidParams as Task 7). `WebhooksEnabled == false` → `-32014` with data `{feature: "deliveryMode", value: "webhook"}`.
3. `delivery` must be an object (else `-32602`). `delivery.mode`: absent or `"webhook"`; `"poll"`/`"push"` → `-32014` `{feature:"deliveryMode", value}`; anything else → `-32602`.
4. `delivery.url` via `WebhookUrlPolicy` → `-32602` naming `delivery.url`. `delivery.secret` via `WebhookSecret` → `-32602` "delivery.secret must be whsec_ followed by base64 of 24–64 bytes" (never echo the value).
5. `ttlMs`: absent → `DefaultTtl`; `null` or > `MaxTtl` → `MaxTtl`; < `MinTtl` → `MinTtl`; not a non-negative integer → `-32602`.
6. `id = SubscriptionKey.ComputeId(principal, delivery.url exactly as sent, name, arguments)`.
7. New key: `CountFor(principal) >= MaxSubscriptionsPerPrincipal` → `-32013` data `{limit: "subscriptions", max}` (checked before any network call).
8. Verification when `(principal, url)` is not in the verified cache (in-memory `HashSet`): body `{"type":"verification","challenge":"<base64url of 32 random bytes>"}`, message id `msg_verification_<16 random hex>`, signed with the new secret, sent with `X-MCP-Subscription-Id: <id>`. Verified only if `Delivered` and the response body parses as JSON whose `challenge` string equals the nonce (`FixedTimeEquals` over UTF-8). Reachable but wrong/no echo → `-32015` `{reason: "challenge_failed"}`; not reachable → `-32015` `{reason: <ErrorCategory>}`. Nothing from the response appears in the error.
9. Upsert. New: `Position` from `Seek(cursor, maxAge)`. Existing: secret changed → `PreviousSecret` = old, expires now + `SecretRotationGrace`; supplied cursor ahead of `Position` → move `Position` forward, otherwise keep; `Active = true`. Both: `RefreshBefore = now + grant`. Raise `Changed`.
10. Result: `{"resultType":"complete","id","refreshBefore":<ISO>,"cursor":CursorAt(Position),"truncated":<from Seek, false on a no-op refresh>}`; on refresh of an existing subscription also `"deliveryStatus":{"active","lastDeliveryAt"(ISO or null),"lastError"(category or null)}` plus `"failedSince"` when set.

**Behaviour — `events/unsubscribe`:** principal required (`-32012`); `name` string, `arguments` object (optional), `delivery.url` string required (`-32602`); id computed as above; removed → `{"resultType":"complete"}`; not found → `-32011` data `{kind: "subscription"}`.

**`WebhookHttpSender`:** one `SocketsHttpHandler` (`AllowAutoRedirect = false`, `UseCookies = false`, `ConnectTimeout = RequestTimeout`) whose `ConnectCallback` resolves the host (`ResolveHost` seam), drops blocked addresses unless `AllowInsecureLoopback`, connects to the first remaining address and fails with a "blocked" marker when none remain — the original host stays in `Host`/SNI because the URI is unchanged. Category mapping: blocked address, `SocketError.ConnectionRefused`, network/host unreachable, host not found → `connection_refused`; connect or response timeout → `timeout`; `AuthenticationException` → `tls_error`; any other non-2xx status below 500 (redirects included, since they are never followed) → `http_4xx`; ≥ 500 → `http_5xx`. Address checks happen on every connection (delivery time), not only at subscribe time.

**Tests (write first):**
- `WebhookPrimitivesTests`:
  - signer vector (wire-format B8): secret `whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw`, id `msg_p5jXN8AQM9LWM0D4loKWxJek`, timestamp `1614265330`, body `{"test": 2432232314}` → `v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=`; two keys → two space-separated entries.
  - `WebhookSecret`: that secret parses to 24 bytes; padded and unpadded 32-byte values parse; 23 and 65 bytes, missing prefix, `whsec_!!!` → false.
  - `CanonicalJson`: `["alice","https://h/x","n.e",{"b":1,"a":[true,null,"é"]}]` → `["alice","https://h/x","n.e",{"a":[true,null,"é"],"b":1}]`.
  - `SubscriptionKey`: equals `"sub_" + Convert.ToHexStringLower(SHA256.HashData(UTF8(<that canonical literal>)))[..16]` for the same inputs; argument key order does not change the id; null arguments == `{}`.
  - `WebhookAddressPolicy`: blocked `127.0.0.1`, `10.1.2.3`, `172.16.0.1`, `192.168.1.1`, `169.254.169.254`, `100.64.0.1`, `0.0.0.0`, `::1`, `fc00::1`, `fe80::1`, `::ffff:127.0.0.1`, `::ffff:10.0.0.1`; allowed `8.8.8.8`, `1.1.1.1`, `2606:4700:4700::1111`.
  - `WebhookUrlPolicy`: `https://hooks.example.com/x` ok; `http://…` rejected unless the flag; `https://u:p@h/` and `https://h/#f` and relative URLs rejected.
  - `WebhookHttpSender` with `ResolveHost` mapping `rebind.test` → `10.0.0.1` and the flag off: `SendAsync(https://rebind.test/hook)` → `connection_refused` without any network I/O.
- `WebhookSubscribeProtocolTests` — `InProcessMcpServer` + `.WithEvents(o => { define test.alpha (inputSchema {branch: string}); o.WebhooksEnabled = true; o.PrincipalResolver = _ => "alice"; o.Webhooks.AllowInsecureLoopback = true; })`, a `WebhookReceiver`:
  - happy path: the receiver gets one verification POST whose signature verifies with the receiver's secret, `webhook-id` starts with `msg_verification_`, `X-MCP-Subscription-Id` equals the returned id; result has `resultType`, `id` matching `^sub_[0-9a-f]{16}$` and equal to `SubscriptionKey.ComputeId("alice", url, "test.alpha", args)`, `refreshBefore` ≈ now + 1 h, `cursor`, `truncated: false`, no `deliveryStatus`.
  - subscribing again (same key) → same id, still one verification POST in total, `deliveryStatus` present.
  - `ttlMs: null` → ≈ 24 h; `ttlMs: 10000` → ≈ 60 s; `ttlMs: 172800000` → ≈ 24 h.
  - resolver returning null → `-32012`; `WebhooksEnabled = false` → `-32014` with `feature: "deliveryMode"`; `delivery.mode: "push"` → `-32014`; bad secret → `-32602` whose message does not contain the secret value; `http://` URL with the flag off → `-32602`.
  - receiver with `EchoChallenges = false` → `-32015` `reason: "challenge_failed"`; a closed port (bind a `TcpListener` to port 0, read its port, stop it) → `-32015` `reason: "connection_refused"`.
  - flag off + `https://localhost:<receiver port>/hooks` → `-32015` `reason: "connection_refused"` and the receiver saw nothing.
  - `MaxSubscriptionsPerPrincipal = 1`, second URL → `-32013` with `limit: "subscriptions"`, `max: 1`, and no verification POST to the second URL.
  - unsubscribe → `{resultType: "complete"}`; again → `-32011` `kind: "subscription"`; unsubscribe with resolver null → `-32012`.
- `WebhookEndToEndTests` (app): `GuardianHttpTestHost` with an API key and `Guardian:Webhooks:AllowInsecureLoopback=true`; `ConnectAsync(key)`; `events/subscribe` to `repo.commit.created` at a `WebhookReceiver` succeeds and the verification POST arrives.

**Acceptance:**
- [ ] `dotnet test` green with no network access (only loopback sockets).
- [ ] Commit: `feat(events): webhook subscriptions with verification, signing and SSRF guard`

### Task 21: Webhook dispatcher (delivery, retries, watermark, gaps, suspension)

**Goal:** Deliver events to verified webhook subscriptions per the Standard Webhooks profile with bounded retries and a safe watermark cursor.

**Files:**
- Create: `src/Mcp.Events/Webhooks/WebhookDispatcher.cs` (`BackgroundService`), `src/Mcp.Events/Webhooks/SubscriptionWorker.cs`, `src/Mcp.Events/Webhooks/DeliveryStats.cs`
- Modify: `src/Mcp.Events/EventsServerBuilderExtensions.cs` (`AddHostedService<WebhookDispatcher>()`)
- Create: `tests/CodebaseGuardian.Tests/Events/WebhookDeliveryTests.cs`

**Interfaces:**
- Consumes: Task 20 types (`WebhookSubscriptionStore`, `WebhookSubscription`, `IWebhookSender`, `WebhookOptions`, `IEventLog.CursorAt`), `EventJson` (Task 7), `IEventLog.Read`/`WaitForEventsAfterAsync` (Task 6), `WebhookReceiver` (Task 20).
- Produces: no new public API (internal types). Behaviour observable through the receiver and the `deliveryStatus` of a refresh.

**Behaviour (wire-format B8, B9, B11):**
- The dispatcher keeps one `SubscriptionWorker` per subscription in the store: starts on `Changed` (add), stops on remove or expiry, wakes on refresh. Workers stop with the host; no envelope is sent on shutdown (clients re-create lost subscriptions by refreshing, B7).
- Worker loop:
  1. If `now >= RefreshBefore` → remove the subscription from the store and stop (lapsed; no envelope).
  2. If `!Active` (suspended) → wait for a `Changed` signal (refresh) or expiry.
  3. `Read(new EventQuery([Name], Arguments, CursorAt(Position), MaxAge: null, MaxEvents: 100))`. If `Truncated` → send a gap control envelope `{"type":"gap","cursor":<cursor of the first servable position>}` (message id `msg_gap_<16 random hex>`, same retry rules, never blocks progress) and set `Position` to the servable start.
  4. For each event in order: body = `EventJson` envelope `{eventId,name,timestamp,data,cursor: CursorAt(event.Sequence)}` serialized once to UTF-8 bytes. Body > 256 KiB → log a warning, treat as abandoned. Otherwise attempt delivery: `IWebhookSender.SendAsync(url, eventId, body, id, SigningKeys(now))` — the sender regenerates timestamp and signature on every attempt.
     - 2xx → `LastDeliveryAt = now`, `LastError = null`, `FailedSince = null`.
     - 410 or 413 → no retry for this event (`LastError = "http_4xx"`), subscription unaffected.
     - other failure → `LastError = category`, `FailedSince ??= now`; wait `RetryDelays[i]` and retry; give up after `1 + RetryDelays.Count` attempts or when `RetryWindow` since the first attempt has elapsed.
     - after success or giving up: `Position = event.Sequence` (watermark advances only past acked or abandoned events).
  5. `HasMore` → loop immediately; else `WaitForEventsAfterAsync(Position)` raced against refresh/expiry signals.
- Suspension: `DeliveryStats` keeps attempt outcomes over `SuspendWindow`; failure rate ≥ `SuspendFailureRate` with ≥ `SuspendMinAttempts` attempts → `Active = false`. A refresh (`events/subscribe` on the same key) sets `Active = true`, clears stats, and the worker resumes from `Position` (a gap envelope follows if retention moved past it).
- All time via `TimeProvider`; mutations under `WebhookSubscriptionStore.SyncRoot`.
- `deliveryStatus` on refresh (Task 20) reflects these fields; `lastError` is only ever a category string.

**Tests (write first)** — same in-process setup as Task 20 with `RetryDelays = [50 ms, 100 ms]`, `RetryWindow = 5 s`; publish through `IEventPublisher`; use `WebhookReceiver.WaitForAsync` with ≤ 5 s timeouts:
- three events → three POSTs in publish order; each: signature valid for the receiver secret, `webhook-id == eventId`, `X-MCP-Subscription-Id == id`, `webhook-timestamp` within 10 s of now, body has `eventId`, `name`, `timestamp`, `data`, `cursor`.
- receiver answers 500, 500, then 200 → three attempts with the same `webhook-id` and differing `webhook-signature`; a refresh afterwards shows `lastError: null` and a `lastDeliveryAt`.
- 410 for event 1 → exactly one attempt for it; event 2 still delivered.
- always 503 → exactly three attempts, then event 2 is attempted after the receiver recovers; a refresh while failing shows `lastError: "http_5xx"` and `failedSince`.
- watermark: `Gate` holds the response to event 1; a refresh while it is held returns `cursor` equal to the cursor before event 1; after release and ack, a refresh returns the cursor after event 1.
- secret rotation: refresh with a new secret → the next delivery's `webhook-signature` has two `v1,` entries, one valid per secret; with `FakeTimeProvider` advanced past the grace → one entry.
- gap: `Capacity = 5`, `Gate` holds event 1, publish 10 more, release → the receiver gets a body with `"type":"gap"` and `webhook-id` starting `msg_gap_`, then later events.
- expiry: `MinTtl = 200 ms`, subscribe with `ttlMs: 200`; after 500 ms a published event is not delivered and a new subscribe creates a fresh subscription (verification is cached, so no new verification POST).
- unsubscribe stops delivery (event published after unsubscribe never arrives within 500 ms).
- delivery-time SSRF: subscribe to `http://127.0.0.1:<port>/hooks` with the flag on, deliver one event; set `options.Webhooks.AllowInsecureLoopback = false`; publish → no POST arrives and a refresh shows `lastError: "connection_refused"`.
- suspension: `SuspendMinAttempts = 4`, `SuspendFailureRate = 0.9`, receiver always 500 → `deliveryStatus.active == false`; receiver fixed + refresh → `active == true` and the pending event arrives.

**Acceptance:**
- [ ] `dotnet test` green; `WebhookDeliveryTests` stable across 3 runs.
- [ ] Commit: `feat(events): webhook dispatcher with retries, watermark cursor and suspension`

---

## Epic 3 — Tasks

### Task 22: Tasks extension for long-running tools

**Goal:** `run_checks` and `audit_dependencies` can run as MCP tasks (`io.modelcontextprotocol/tasks`), cancellation is clean, and outward-facing tools stay synchronous.

**Files:**
- Create: `src/CodebaseGuardian/Hosting/GuardianTaskModes.cs`
- Modify: `src/CodebaseGuardian/CodebaseGuardian.csproj` (`<PackageReference Include="ModelContextProtocol.Extensions.Tasks" />`; NoWarn IDs the compiler reports)
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs` (`.WithTasks(new InMemoryMcpTaskStore { DefaultPollIntervalMs = 1000, DefaultTimeToLive = TimeSpan.FromHours(1) }, o => o.ExecutionModeSelector = GuardianTaskModes.Select)`)
- Modify: `src/CodebaseGuardian/Checks/CheckRunner.cs` (cancellation semantics), `src/CodebaseGuardian/Dependencies/DependencyAuditor.cs` (cancellation propagates)
- Create: `tests/CodebaseGuardian.Tests/App/GuardianTaskModesTests.cs`, `tests/CodebaseGuardian.Tests/App/TasksTests.cs`

**Interfaces:**
- Consumes: SDK `ModelContextProtocol.Extensions.Tasks` (SDK §8: `WithTasks`, `McpTaskExecutionMode`, client `CallToolAsTaskAsync`, `GetTaskAsync`, `CancelTaskAsync`); `ICheckRunner`, `IDependencyAuditor`, `FakeProcessRunner.OnBlocking` (Tasks 10, 12); `GuardianHttpTestHost` (Task 14).
- Produces:

```csharp
namespace CodebaseGuardian.Hosting;
public static class GuardianTaskModes
{
    // run_checks, audit_dependencies => Optional; full_scan => Required (tool added in Task 23); everything else => Synchronous.
    // create_issue, comment_on_pr, open_pull_request MUST stay Synchronous: SDK 2.2.0 fails MRTR inside tasks
    // ("MRTR and tasks cannot be composed via [McpServerTool] yet").
    public static McpTaskExecutionMode Select(RequestContext<CallToolRequestParams> context);
}
```

**Behaviour:**
- Capability `capabilities.extensions["io.modelcontextprotocol/tasks"]` comes from the SDK. Tools run as tasks only for 2026-07-28 requests from clients declaring the tasks extension; others run synchronously (Optional).
- `CheckRunner`: when the token is cancelled the process tree is killed (runner behaviour from Task 2), no `CheckRun` is stored, no `checks.*` event is published, the one-at-a-time lock is released, and `OperationCanceledException` propagates (the SDK records the task as cancelled).
- `DependencyAuditor`: `OperationCanceledException` propagates instead of becoming an ecosystem `"failed"` status.
- Progress reported through `IProgress` must never fail a task-mode run (wrap reporting so exceptions are swallowed and logged at debug).

**Tests (write first)** — tasks-capable client: `McpClientOptions.Capabilities = new() { Extensions = new Dictionary<string, object> { ["io.modelcontextprotocol/tasks"] = new JsonObject() } }`; `FakeProcessRunner` swapped in via `configureServices`:
- `GuardianTaskModesTests`: table of tool names → modes, including the three GitHub tools → `Synchronous` and an unknown name → `Synchronous`.
- `ServerCapabilities.Extensions` contains `io.modelcontextprotocol/tasks`.
- `CallToolAsTaskAsync("run_checks")` → `IsTask`; polling `GetTaskAsync` reaches completed within 5 s; the wrapped `CallToolResult` has the same structured fields as the synchronous call (`runId`, `passed`, `logUri`, …); `checks.completed` is published.
- same call from a client without the tasks extension → plain synchronous result.
- `audit_dependencies` as a task completes with the report.
- cancellation: `OnBlocking` for the check command; start as task; `CancelTaskAsync` → `GetTaskAsync` reports cancelled; the blocked `ProcessSpec`'s token was cancelled; no `checks.completed` event within 500 ms; a following synchronous `run_checks` (fake now completes immediately) returns within 5 s (lock released).
- stateless HTTP (`GuardianHttpTestHost`, tasks-capable client): `run_checks` as task created by one request and completed via later `tasks/get` requests.

**Acceptance:**
- [ ] `dotnet test` green.
- [ ] Commit: `feat(tasks): run long-running tools as MCP tasks with clean cancellation`

### Task 23: `full_scan`, scan report resource, `scan.completed`, and final docs

**Goal:** One task-required tool that runs secrets + dependencies + checks, stores a Markdown report as a resource, emits `scan.completed` with the right suggested skill, and closes the documentation.

**Files:**
- Create: `src/CodebaseGuardian/Scanning/ScanReport.cs`, `ScanReportStore.cs`, `IFullScanService.cs`, `FullScanService.cs`, `ScanReportRenderer.cs`
- Create: `src/CodebaseGuardian/Tools/ScanTools.cs`, `src/CodebaseGuardian/Resources/ScanResources.cs`
- Modify: `src/CodebaseGuardian/Watching/GuardianEvents.cs` (define `scan.completed`, always registered)
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs`
- Modify: `src/CodebaseGuardian/skills/guardian/SKILL.md`, `skills/guardian/references/events.md`, `skills/security-audit/SKILL.md`, `skills/dependency-hygiene/SKILL.md`
- Modify: `tests/CodebaseGuardian.Tests/App/GuardianEventsTests.cs` (eight names with GitHub disabled, eleven enabled)
- Modify: `README.md`, `docs/backlog/README.md`
- Create: `tests/CodebaseGuardian.Tests/App/FullScanServiceTests.cs`, `ScanReportRendererTests.cs`, `ScanToolsTests.cs`

**Interfaces:**
- Consumes: `ISecretScanner.ScanWorkingTreeAsync`, `SecretFinding` (Task 11); `IDependencyAuditor`, `DependencyAuditReport` (Task 12); `ICheckRunner`, `ICheckCommandResolver`, `CheckRun` (Task 10); `IGitRepository.GetHeadShaAsync` (Task 2); `IEventPublisher` (Task 6); `GuardianTaskModes` (Task 22); `FakeSecrets`, `FakeProcessRunner` (Tasks 11, 10).
- Produces:

```csharp
namespace CodebaseGuardian.Scanning;
public sealed record ScanReport(string ScanId, DateTimeOffset StartedAt, TimeSpan Duration, string? HeadSha,
    IReadOnlyList<SecretFinding> SecretFindings, DependencyAuditReport? Dependencies, CheckRun? Checks,
    string? ChecksSkippedReason, IReadOnlyList<string> StepErrors, string Markdown)
{
    public string ReportUri => $"guardian://scans/{ScanId}/report";
    public bool? ChecksPassed => Checks?.Passed;
}
public sealed class ScanReportStore { public void Add(ScanReport report); public ScanReport? Get(string scanId); }   // keeps the last 20
public interface IFullScanService
{
    Task<ScanReport> RunAsync(bool includeChecks, IProgress<string>? progress, CancellationToken cancellationToken);
}
public static class ScanReportRenderer { public static string Render(ScanReport reportWithoutMarkdown, string repositoryPath); }
```

**Behaviour:**
- `ScanId` = `scan_<yyyyMMddHHmmss>_<4 lowercase hex>` (UTC).
- Steps in order, progress `"secrets"`, `"dependencies"`, `"checks"`: `ScanWorkingTreeAsync` → `AuditAsync(includeOutdated: true)` → when `includeChecks`: `Resolve()` null → `ChecksSkippedReason = "No check command configured or detected."`, else `ICheckRunner.RunAsync("scan", headSha, …)`; `includeChecks == false` → reason `"Checks not requested."`. A step throwing (not cancellation) adds `"<step>: <message>"` to `StepErrors` and the scan continues. Cancellation propagates: nothing stored, no event.
- Markdown (renderer is pure): `# Guardian scan <scanId>`; a metadata list (repository path, HEAD, started, duration); `## Secrets` table `Rule | Path | Line | Value` with `Redacted` values only, or "No secrets found."; `## Dependencies` one subsection per ecosystem with status/reason, a vulnerable table `Package | Version | Severity | Advisory | Project`, and an outdated count; `## Checks` command, result, summary, up to 20 failed tests, log link `guardian://checks/<runId>/log`, or the skipped reason; `## Errors` only when `StepErrors` is non-empty.
- `scan.completed` data: `scanId, reportUri, secretFindings` (count), `vulnerablePackages` (count), `checksPassed` (bool or null), `suggestedSkill` = `skill://security-audit/SKILL.md` when secrets > 0, else `skill://dependency-hygiene/SKILL.md` when vulnerable > 0, else the key is absent. Payload schema declares these (`checksPassed` `["boolean","null"]`).
- Tool `full_scan(includeChecks = true)`: `ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true`, task mode Required (Task 22 selector). Structured result `{scanId, reportUri, secretFindings, vulnerablePackages, outdatedPackages, checksPassed, durationMs}` plus a one-line text summary. Description: "Runs as an MCP task; clients without task support should call `scan_secrets`, `audit_dependencies` and `run_checks` instead."
- Resource template `guardian://scans/{scanId}/report` (`text/markdown`) → the stored Markdown; unknown id → `McpProtocolException(InvalidParams)`.
- Skills: `guardian` gains the `scan.completed` row; `security-audit` and `dependency-hygiene` describe reacting to `scan.completed` (read `reportUri`) and the per-tool fallback when tasks are unavailable.
- README: Tasks section (which tools run as tasks, how to enable the client capability), `full_scan`, the full event table (Epics 1–3), webhook setup (API key, `Guardian:Webhooks:*`, dev flag warning), GitHub setup (`GITHUB_TOKEN` / `gh auth login`, `Guardian:GitHub:*`), and the "Status" line changed to "Feature complete for v1 (see docs/backlog)". `docs/backlog/README.md`: mark all tasks done.

**Tests (write first):**
- `FullScanServiceTests` (fakes for scanner/auditor/runner/resolver or `FakeProcessRunner` + `TempGitRepo`):
  - a committed file with `FakeSecrets.AwsAccessKeyId()` plus one vulnerable package → `scan.completed` with `secretFindings: 1`, `vulnerablePackages: 1`, `suggestedSkill == "skill://security-audit/SKILL.md"`.
  - no secrets, one vulnerable → `skill://dependency-hygiene/SKILL.md`; nothing found → `data` has no `suggestedSkill` key.
  - no check command → `checksPassed == null` and the report contains the skipped reason; `includeChecks: false` → runner never called.
  - auditor throws `InvalidOperationException("boom")` → `StepErrors == ["dependencies: boom"]`, checks still run, report has `## Errors`.
  - cancellation during checks → `OperationCanceledException`, store empty, no `scan.completed`.
  - the stored Markdown never contains the raw fake key.
- `ScanReportRendererTests`: golden-string test for a small report (one finding, one vulnerable package, a failed check with two failed tests).
- `ScanToolsTests` (`GuardianTestHost`, tasks-capable client, `FakeProcessRunner`):
  - `CallToolAsTaskAsync("full_scan")` → completes; structured result fields present; `resources/read` of `reportUri` returns `text/markdown` starting with `# Guardian scan`.
  - a client without the tasks extension calling `full_scan` → protocol error `-32021` (MissingRequiredClientCapability).
  - `resources/templates/list` contains `guardian://scans/{scanId}/report`; unknown scan id → `-32602`.
  - stateless HTTP: `full_scan` as a task over `GuardianHttpTestHost` completes.
- `SkillContentTests` stays green with the updated skills; `GuardianEventsTests` updated as listed.

**Acceptance:**
- [ ] `dotnet test` green (whole suite 3 runs, no flaky failures).
- [ ] Manual: `dotnet run --project samples/GuardianWatch -- --repo <temp repo>` still works; README commands copy-paste cleanly.
- [ ] Commit: `feat(scan): full_scan task, scan report resource, scan.completed and v1 docs`
