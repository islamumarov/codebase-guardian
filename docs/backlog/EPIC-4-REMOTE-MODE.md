# Codebase Guardian — Epic 4: GitHub Remote Mode — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the Guardian watch one GitHub repository that is not checked out, reading it only through the GitHub REST API (`--repo github:owner/name`), with the same tools, resources, events and skills as local mode minus the execution-bound ones.

**Architecture:** A source-neutral `IRepositorySource` interface is extracted from `IGitRepository` (which keeps only working-tree members). A new `GitHubApiSource` implements it over a new read client `IGitHubRepositoryApi`, which shares an extracted HTTP core (`GitHubHttp`) with the existing `GitHubClient`. The composition root parses a `RepositoryLocation` once and registers either the local stack or the remote stack; tools, resources and events that need a checkout are not registered in remote mode.

**Tech Stack:** .NET 10 (`net10.0`), C# latest, MCP C# SDK 2.2.0, `System.Formats.Tar` + `System.IO.Compression` (BCL), xunit.v3 4.0.1 on Microsoft.Testing.Platform, `Microsoft.Extensions.TimeProvider.Testing` (already in `Directory.Packages.props`).

**Spec:** `docs/specs/2026-10-07-github-remote-mode-design.md` (binding; "spec §N" below). It extends `docs/specs/2026-10-04-codebase-guardian-design.md` ("v1 spec"), which still binds for everything it does not change.

**Task format note:** as in `docs/backlog/BACKLOG.md`, tasks give exact files, interfaces, behaviour, test cases and acceptance commands, plus code where precision matters. They do not contain complete implementations; implementers write the code test-first from these descriptions. Task numbers continue the v1 backlog (24–33).

**Execution:** this epic is run from an interactive session only. It is deliberately **not** in `BACKLOG.md`, so the scheduled cloud routine (`docs/backlog/CONTROLLER.md`) never picks it up. No `sdd-lock`, no STATUS.md entries, no push.

## Global Constraints

- Working directory `/Users/user/Documents/MCPs/CSMCP`, branch `feat/github-remote-mode`. Never push. Never switch branches.
- Target framework `net10.0` everywhere (set once in `Directory.Build.props`). Nullable warnings are errors.
- Package versions live only in `Directory.Packages.props`. No new packages are needed; `System.Formats.Tar` and `System.IO.Compression` are part of the BCL. MCP SDK stays exactly `2.2.0`.
- Namespaces: `CodebaseGuardian.<Folder>` (src/CodebaseGuardian/<Folder>), `CodebaseGuardian.Tests.<Folder>` (tests). New folder `src/CodebaseGuardian/Sources` → namespace `CodebaseGuardian.Sources`.
- `Mcp.Skills` and `Mcp.Events` are not modified by this epic.
- Tool names, resource URIs, event names and skill names are unchanged; remote mode only **omits** some of them (spec §3.1).
- JSON on the wire is camelCase; timestamps ISO 8601 UTC with `Z` (`yyyy-MM-ddTHH:mm:ss.fffZ`).
- No shell execution; every process goes through `IProcessRunner`. Model-supplied revisions are validated before use (`GitRevision` locally, `RemoteRevision` remotely) and URL-escaped per `/`-segment before reaching a GitHub URL.
- stdout is reserved for MCP on stdio; logging goes to stderr.
- Secret values are never logged, returned or stored unredacted. The GitHub token is never logged, never placed in a URL, and never sent to a host other than `Guardian:GitHub:ApiBaseUrl`'s host.
- Nothing from a remote repository is written to disk (spec D1).
- Tests: xunit.v3; `dotnet test` from the repo root, no network access. GitHub is faked with `FakeGitHubApi` (+ `FakeGitHubRepository`, Task 28). Git tests use `TempGitRepo`. Use `TestContext.Current.CancellationToken`. Time-dependent code takes an injected `TimeProvider`; tests use `FakeTimeProvider`.
- **Known baseline on macOS:** at `3d08f3d` the suite is 738 tests with exactly these 5 failures, caused by `/var` vs `/private/var` temp paths and one flaky watcher test, not by this epic: `CheckRunnerTests.The_check_runs_in_the_repository_root_with_the_resolved_argument_list`, `DependencyAuditorTests.NuGet_runs_the_documented_commands_against_the_solution_and_parses_both_results`, `DependencyAuditorTests.Without_a_solution_the_repository_root_is_the_target_and_outdated_can_be_skipped`, `GitRepositoryTests.Opening_through_a_subdirectory_keeps_paths_root_relative`, `RepositoryWatcherTests.A_new_branch_is_announced_once_and_merging_it_does_not_repeat_its_commits`. "Green" in this plan means: no failures other than these five, and every new test passes. Do not try to fix them.
- Each task ends green and with one or more commits. Conventional Commits; every message ends with the trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Local mode must behave exactly as before, apart from the two additive `scan_secrets` output fields (Task 26). Existing tests may only be edited where a task says so.
- Do not edit files owned by other tasks beyond what the task lists; if something outside scope is broken, report it.

## Review Focus

Inputs the spec implies, most likely to bite a real user first. Each has a pinning test in the owning task.

1. **Empty GitHub repository** (API answers `409` for commits and branches): `repo_status`, `recent_commits` and the watcher work, not error; the first pushed commit is announced. Tests: Task 30, Task 32.
2. **Force-push on a remote branch** (old tip answers 404, or compare says `diverged`): the watcher keeps running and announces the rewritten commits exactly once. Test: Task 32.
3. **Tarball redirect**: the `Authorization` header never reaches `codeload.github.com`; a redirect to any other host is refused. Test: Task 29.
4. **Rate limit hit**: the watcher pauses until the reset time and resumes; a tool call returns an MCP tool error naming the reset time instead of a protocol error. Tests: Task 32, Task 31.
5. **Hostile or huge snapshot** (`../` entries, symlinks, more files or bytes than the caps): `scan_secrets` returns `complete: false` with a warning, never throws, never yields a path outside the snapshot. Tests: Task 29, Task 31.
6. **Local mode untouched**: a local server's `tools/list`, `events/list` and resource templates are identical to before. Test: Task 31.

## Task order and dependencies

| Task | Delivers | Depends on |
|---|---|---|
| 24 | `RepositoryLocation`, `RemoteOptions`, validation | — |
| 25 | `IRepositorySource` split; local consumers migrated | 24 |
| 26 | Secret scanner on snapshots; `complete`/`warnings` | 25 |
| 27 | `GitHubHttp` core extracted from `GitHubClient`; anonymous + conditional GET | — |
| 28 | `IGitHubRepositoryApi` read client; `FakeGitHubRepository` | 27 |
| 29 | Tarball download + `SnapshotTarReader` | 25, 28 |
| 30 | `GitHubApiSource` | 25, 28, 29 |
| 31 | Remote composition root, tool behaviour, instructions | 24, 26, 30 |
| 32 | Watcher on `IRepositorySource` (remote polling, backoff) | 30, 31 |
| 33 | Skills, README, remote end-to-end | 31, 32 |

Tasks run strictly in this order.

---

### Task 24: Repository location, remote options and validation

**Goal:** Parse `Guardian:RepositoryPath` into a local path or a GitHub repository once, and fail startup on every invalid remote-mode combination. No remote behaviour yet; local mode unchanged.

**Files:**
- Create: `src/CodebaseGuardian/Sources/RepositoryLocation.cs`
- Create: `src/CodebaseGuardian/Sources/RemoteOptions.cs` (`RemoteOptions`, `RemoteOptionsValidator`)
- Modify: `src/CodebaseGuardian/Hosting/GuardianOptions.cs` (`GuardianOptionsValidator`: location-aware)
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs` (register `RepositoryLocation` singleton; `PostConfigure` normalizes local paths only; bind + validate `RemoteOptions` with `ValidateOnStart`)
- Create: `tests/CodebaseGuardian.Tests/App/RepositoryLocationTests.cs`, `tests/CodebaseGuardian.Tests/App/RemoteOptionsTests.cs`

**Interfaces:**
- Consumes: `GitHubRepositoryRef.TryParseRemoteUrl`, `GitHubRepositoryRef.IsValidSegment` (internal, same assembly), `GitHubOptions`, `GuardianOptions`.
- Produces:

```csharp
namespace CodebaseGuardian.Sources;

public abstract record RepositoryLocation
{
    /// <summary>FullPath is Path.GetFullPath(value); a blank value is kept as-is for validation to report.</summary>
    public sealed record Local(string FullPath) : RepositoryLocation;
    public sealed record GitHub(GitHubRepositoryRef Repository) : RepositoryLocation;
    /// <summary>Looked like a GitHub repository but is not a valid one. Reason is the validation message.</summary>
    public sealed record Invalid(string Value, string Reason) : RepositoryLocation;

    /// <summary>Never throws.</summary>
    public static RepositoryLocation Parse(string value);

    /// <summary>Parses Guardian:RepositoryPath, defaulting to the current directory like GuardianOptions does.</summary>
    public static RepositoryLocation FromConfiguration(IConfiguration configuration);
}

public sealed class RemoteOptions
{
    public const string SectionName = "Guardian:Remote";
    public int PollIntervalSeconds { get; set; } = 60;            // >= 15
    public long MaxSnapshotBytes { get; set; } = 104_857_600;     // >= 1_048_576
    public int MaxSnapshotFiles { get; set; } = 20_000;           // >= 1
}

internal sealed class RemoteOptionsValidator(RepositoryLocation location, IOptions<GuardianOptions> guardian, IOptions<GitHubOptions> github)
    : IValidateOptions<RemoteOptions>;
```

**Behaviour:**
- `Parse` rules (spec §2.1):
  - `github:owner/name` → `GitHub` when both segments pass `IsValidSegment` and there are exactly two; otherwise `Invalid(value, "Guardian:RepositoryPath 'github:…' must be github:owner/name.")`.
  - A value starting with `https://github.com/`, `http://github.com/`, `git@github.com:` or `ssh://` and containing `github.com` → `TryParseRemoteUrl` (trailing `/` tolerated); success → `GitHub`; failure (e.g. `https://github.com/acme/widgets/tree/main`) → `Invalid(value, "Guardian:RepositoryPath '<value>' is not a GitHub repository URL (expected https://github.com/owner/name).")`.
  - Anything else → `Local(Path.GetFullPath(value))`, or `Local(value)` when blank. `acme/widgets` is a relative path.
- `GuardianOptionsValidator`: parses `options.RepositoryPath`. `Invalid` → its `Reason`. `Local` → today's checks (empty, `Directory.Exists`). `GitHub` → no directory check; `AutoChecks == true` → `"Guardian:AutoChecks needs a local checkout; it is not available in remote mode."`. Watch interval checks unchanged in both modes.
- `RemoteOptionsValidator`: range checks always (`"Guardian:Remote:PollIntervalSeconds must be at least 15 (was 5)."` style, as `GitHubOptionsValidator` words them). Only when `location is GitHub`: `GitHubOptions.Enabled == false` → `"Remote mode reads the repository through the GitHub API; Guardian:GitHub:Enabled must be true."`; `Owner` and `Repository` both set and not equal (case-insensitive) to the location's → `"Guardian:GitHub:Owner/Repository (x/y) conflict with Guardian:RepositoryPath (acme/widgets)."`.
- Composition root: `services.AddSingleton(RepositoryLocation.FromConfiguration(configuration))`; the `PostConfigure` becomes `options.RepositoryPath = RepositoryLocation.Parse(options.RepositoryPath) is RepositoryLocation.Local local ? local.FullPath : options.RepositoryPath`. The rest of registration is unchanged in this task (remote registration comes in Task 31).

**Tests (write first):**
- `RepositoryLocationTests` (theory data): `github:acme/widgets`, `https://github.com/acme/widgets`, `https://github.com/acme/widgets.git`, `https://github.com/acme/widgets/`, `git@github.com:acme/widgets.git`, `ssh://git@github.com/acme/widgets.git` → `GitHub(acme/widgets)`. `github:acme`, `github:acme/widgets/x`, `github:../widgets`, `https://github.com/acme/widgets/tree/main` → `Invalid` with the messages above. `acme/widgets`, `.`, `/tmp/repo`, `C:\\repo` (on any OS: just not GitHub) → `Local`. `""` → `Local("")`.
- `RemoteOptionsTests` (validators called directly, plus one host start via `GuardianTestHost` expecting `OptionsValidationException` whose message contains every failure): remote + `AutoChecks=true`; remote + `GitHub:Enabled=false`; remote + conflicting Owner/Repository; `PollIntervalSeconds=5`; `MaxSnapshotBytes=10`; local mode with `GitHub:Enabled=false` passes; a remote location with a non-existent directory name does **not** report "does not exist".

**Acceptance:**
- [ ] `dotnet test` green (baseline rule).
- [ ] Commit: `feat(sources): parse repository location and validate remote mode options`

---

### Task 25: `IRepositorySource` and the `IGitRepository` split

**Goal:** Behaviour-preserving refactor: a source-neutral interface that both a checkout and the GitHub API can implement; every consumer depends on the narrowest interface it needs.

**Files:**
- Create: `src/CodebaseGuardian/Sources/IRepositorySource.cs` (`RepositorySourceKind`, `IRepositorySource`, `SnapshotFile`, `SnapshotLimits`)
- Modify: `src/CodebaseGuardian/Git/IGitRepository.cs` (extends `IRepositorySource`; keeps only `RootPath`, `GetStagedDiffAsync`, `ListFilesAsync`, `GetRemoteUrlAsync`)
- Modify: `src/CodebaseGuardian/Git/GitRepository.cs` (`Kind`, `DisplayName`, `ReadFileAsync`, `ReadSnapshotAsync`)
- Modify: `src/CodebaseGuardian/Tools/RepositoryTools.cs`, `src/CodebaseGuardian/Resources/RepositoryResources.cs`, `src/CodebaseGuardian/Scanning/FullScanService.cs` (`IRepositorySource`; `ScanReportRenderer.Render(draft, source.DisplayName)`), `src/CodebaseGuardian/Watching/RepositoryWatcher.cs` (`IRepositorySource`; starts `WorkingTreeWatcher` only when `_source is IGitRepository local`, with `local.RootPath`)
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs` (one `GitRepository` instance behind both interfaces)
- Modify: tests that construct these consumers directly, only to pass the same `GitRepository` instance (no assertion changes)
- Create: `tests/CodebaseGuardian.Tests/App/GitRepositorySnapshotTests.cs`

**Interfaces:**
- Consumes: `RepositoryLocation` (Task 24) — not needed yet beyond compiling.
- Produces (exact; later tasks rely on these names):

```csharp
namespace CodebaseGuardian.Sources;

public enum RepositorySourceKind { Local, GitHub }

public interface IRepositorySource
{
    RepositorySourceKind Kind { get; }
    /// <summary>Local: RootPath. GitHub: "github.com/owner/name" (the API host instead of github.com for GHES).</summary>
    string DisplayName { get; }
    Task<RepoStatus> GetStatusAsync(CancellationToken ct = default);
    /// <summary>Local: checked-out branch (null when detached/unborn). GitHub: the default branch.</summary>
    Task<string?> GetCurrentBranchAsync(CancellationToken ct = default);
    Task<string?> GetHeadShaAsync(CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> GetBranchHeadsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CommitInfo>> GetRecentCommitsAsync(int limit, string? revision = null, CancellationToken ct = default);
    Task<IReadOnlyList<CommitInfo>> GetNewCommitsAsync(string tip, IReadOnlyCollection<string> excludeTips, int limit, CancellationToken ct = default);
    Task<DiffSummary> GetDiffSummaryAsync(string? from, string? to, int maxPatchBytes, CancellationToken ct = default);
    Task<DiffSummary> GetCommitDiffAsync(string sha, int maxPatchBytes, CancellationToken ct = default);
    /// <summary>One snapshot file (local: working tree; GitHub: default-branch head); null when absent. Path is '/'-separated and relative; "..", absolute paths and empty paths throw ArgumentException.</summary>
    Task<byte[]?> ReadFileAsync(string path, CancellationToken ct = default);
    /// <summary>Every regular, non-symlink file of the snapshot. Ends with a sentinel (Path == "") when the snapshot stopped early.</summary>
    IAsyncEnumerable<SnapshotFile> ReadSnapshotAsync(CancellationToken ct = default);
}

/// <summary>Content null => skipped, Skipped says why. Path "" => sentinel: the snapshot is incomplete, Skipped says why.</summary>
public sealed record SnapshotFile(string Path, ReadOnlyMemory<byte>? Content, string? Skipped)
{
    public bool IsIncompleteMarker => Path.Length == 0;
}

public static class SnapshotLimits
{
    public const long MaxFileBytes = 1024 * 1024;   // moved from SecretScanner.MaxFileBytes (keep that constant as an alias)
}
```

```csharp
namespace CodebaseGuardian.Git;

public interface IGitRepository : IRepositorySource
{
    string RootPath { get; }
    Task<DiffSummary> GetStagedDiffAsync(int maxPatchBytes, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken ct = default);
    Task<string?> GetRemoteUrlAsync(string remote = "origin", CancellationToken ct = default);
}
```

**Behaviour:**
- `GitRepository.Kind = Local`, `DisplayName = RootPath`.
- `GitRepository.ReadSnapshotAsync`: the file loop `SecretScanner.ScanWorkingTreeAsync` has today — `ListFilesAsync`, skip symlinks and unreadable/vanished files exactly as `TryReadAsync` does now, files over `SnapshotLimits.MaxFileBytes` yield `SnapshotFile(path, null, "larger than 1 MB")`. Never yields the sentinel (a local snapshot is never cut short).
- `GitRepository.ReadFileAsync`: validates the path, reads `Path.Combine(RootPath, path)`; missing → null; refuses symlinks (returns null).
- DI: `services.AddSingleton<GitRepository>()`, `AddSingleton<IGitRepository>(sp => sp.GetRequiredService<GitRepository>())`, `AddSingleton<IRepositorySource>(sp => sp.GetRequiredService<GitRepository>())`.
- `SecretScanner` keeps using `IGitRepository` in this task (Task 26 moves it).
- Everything observable over MCP is unchanged.

**Tests (write first):**
- `GitRepositorySnapshotTests` (`TempGitRepo`): snapshot lists tracked + untracked-not-ignored files with content; a `.gitignore`d file is absent; a symlink is absent; a 1 MB + 1 byte file yields `Skipped` and null content; `docs/naïve file.md` appears verbatim; no sentinel. `ReadFileAsync("README.md")` returns bytes; `"missing.txt"` → null; `"../x"`, `"/etc/passwd"`, `""` → `ArgumentException`. `Kind`/`DisplayName` values.
- The whole existing suite is the regression test for the migration.

**Acceptance:**
- [ ] `dotnet build` with no new warnings; `dotnet test` green (baseline rule).
- [ ] `grep -rn "IGitRepository" src/CodebaseGuardian --include='*.cs'` lists only `Git/`, `Checks/CheckRunner.cs`, `Checks/CheckCommandResolver.cs`, `Dependencies/DependencyAuditor.cs`, `Security/SecretScanner.cs`, `GitHub/GitHubRepositoryResolver.cs`, `Watching/RepositoryWatcher.cs` (the `is IGitRepository` check) and the composition root.
- [ ] Commit: `refactor(sources): extract IRepositorySource from IGitRepository`

---

### Task 26: Secret scanning over snapshots, with completeness reporting

**Goal:** `scan_secrets` scans whatever snapshot the source provides and tells the model when the scan did not see everything.

**Files:**
- Modify: `src/CodebaseGuardian/Security/SecretScanner.cs`
- Create: `src/CodebaseGuardian/Security/SecretScanOutcome.cs`
- Modify: `src/CodebaseGuardian/Tools/SecurityTools.cs` (`SecretScanResult` gains `Complete`, `Warnings`)
- Modify: `src/CodebaseGuardian/Scanning/FullScanService.cs`, `src/CodebaseGuardian/Security/SecretScanCommitHandler.cs` (adapt to `SecretScanOutcome`)
- Modify: `tests/CodebaseGuardian.Tests/App/SecretScannerTests.cs`, `SecurityToolsTests.cs`, `ToolSchemaTests.cs` and any fake `ISecretScanner` (adapt to the new return type; add the tests below)

**Interfaces:**
- Consumes: `IRepositorySource`, `SnapshotFile`, `IGitRepository` (Task 25).
- Produces:

```csharp
namespace CodebaseGuardian.Security;

public sealed record SecretScanOutcome(IReadOnlyList<SecretFinding> Findings, bool Complete, IReadOnlyList<string> Warnings);

public interface ISecretScanner
{
    Task<SecretScanOutcome> ScanWorkingTreeAsync(CancellationToken ct);
    /// <summary>Throws ArgumentException("Remote mode has nothing staged.") when there is no local checkout.</summary>
    Task<SecretScanOutcome> ScanStagedAsync(CancellationToken ct);
    Task<SecretScanOutcome> ScanCommitAsync(string sha, CancellationToken ct);
    IReadOnlyList<SecretFinding> ScanText(string path, string content);   // unchanged
    IReadOnlyList<SecretFinding> ScanPatch(string unifiedDiff);          // unchanged
    string RedactSecrets(string text);                                    // unchanged
}

public sealed class SecretScanner(IRepositorySource source, ILogger<SecretScanner> logger, IGitRepository? git = null) : ISecretScanner;
```

```csharp
namespace CodebaseGuardian.Tools;
public sealed record SecretScanResult(string Scope, IReadOnlyList<SecretFinding> Findings, int Count, bool Complete, IReadOnlyList<string> Warnings);
```

**Behaviour:**
- `ScanWorkingTreeAsync`: `.guardianignore` via `source.ReadFileAsync(".guardianignore")` (null → empty ignore set; parse the bytes the way `LoadIgnoreAsync` parses the file today); then `source.ReadSnapshotAsync`: ignored paths skipped; `Content` null → skipped silently (per-file cap is documented behaviour, not a warning); NUL in the first 8 KB → binary, skipped; sentinel → `Complete = false`, `Warnings += "Snapshot incomplete: <Skipped>"`.
- `ScanStagedAsync` / `ScanCommitAsync`: as today, but a truncated patch now also yields `Complete = false` and `Warnings += "Only the first 5242880 bytes of the <scope> patch were scanned."` (the existing log warning stays).
- `ScanStagedAsync` with `git == null` → `ArgumentException("Remote mode has nothing staged.")` (becomes a tool error through `ToolErrors`).
- `SecurityTools` maps the outcome into `SecretScanResult`; the event payload is unchanged.
- `FullScanService`: uses `outcome.Findings`; each warning is appended to the report's step errors as `"secrets: <warning>"`.
- `SecretScanCommitHandler`: uses `outcome.Findings`; logs warnings at warning level.
- DI: the container resolves `IGitRepository? git = null` to the registered instance in local mode (Microsoft DI honours default parameter values when a service is absent; Task 31 relies on that for remote mode).

**Tests (write first):**
- `SecretScannerTests`: a fake `IRepositorySource` whose snapshot yields one file with an AWS key and then the sentinel `("", null, "stopped after 1 files")` → one finding, `Complete == false`, warning text contains `stopped after 1 files`; `.guardianignore` served by `ReadFileAsync` is honoured; a file with `Content == null` produces no warning; `ScanStagedAsync` with no `IGitRepository` → `ArgumentException` with the exact message; a truncated commit patch (fake `GetCommitDiffAsync` with `PatchTruncated = true`) → `Complete == false`.
- `SecurityToolsTests`: local working-tree scan of a clean `TempGitRepo` returns `complete: true` and `warnings: []` in structured content.
- `ToolSchemaTests`: the advertised `scan_secrets` output schema requires `complete` (boolean) and `warnings` (array of string).

**Acceptance:**
- [ ] `dotnet test` green (baseline rule).
- [ ] Commit: `feat(security): scan repository snapshots and report incomplete scans`

---

### Task 27: Shared GitHub HTTP core (`GitHubHttp`)

**Goal:** Move `GitHubClient`'s HTTP plumbing into a reusable internal class, adding anonymous requests, extra accepted status codes, a custom `Accept`, and conditional GETs. `GitHubClient` behaviour is unchanged.

**Files:**
- Create: `src/CodebaseGuardian/GitHub/GitHubHttp.cs`
- Create: `src/CodebaseGuardian/GitHub/GitHubConditionalCache.cs`
- Modify: `src/CodebaseGuardian/GitHub/GitHubClient.cs` (delegates to `GitHubHttp`; public surface unchanged)
- Create: `tests/CodebaseGuardian.Tests/App/GitHubHttpTests.cs`
- Do **not** modify `tests/CodebaseGuardian.Tests/App/GitHubClientTests.cs`; it must pass unchanged.

**Interfaces:**
- Consumes: `GitHubOptions`, `GitHubApiException`, `GitHubRateLimitException`.
- Produces:

```csharp
namespace CodebaseGuardian.GitHub;

/// <summary>One logical GitHub REST conversation over a given HttpClient. A null token sends no Authorization header.</summary>
internal sealed partial class GitHubHttp(HttpClient http, GitHubOptions options, TimeProvider time)
{
    public const int MaxPages = 5;
    public static JsonSerializerOptions Json { get; }     // SnakeCaseLower, Web defaults (moved from GitHubClient)
    public Uri BaseAddress { get; }                      // ApiBaseUrl with trailing '/', set on the HttpClient if unset

    /// <summary>Non-success statuses not in <paramref name="allow"/> throw (mapping below). The caller disposes the response.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string pathOrUrl, object? body, string? token,
        CancellationToken ct, string? accept = null, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        params HttpStatusCode[] allow);

    public Task<T> SendJsonAsync<T>(HttpMethod method, string path, object? body, string? token, CancellationToken ct);

    /// <summary>Follows Link rel="next" for at most <paramref name="maxPages"/> pages, never off the API host. Statuses in allow end the listing with what was read so far.</summary>
    public Task<List<T>> ListAsync<T>(string firstPath, string? token, CancellationToken ct, int maxPages = MaxPages, params HttpStatusCode[] allow);

    /// <summary>GET with If-None-Match from <paramref name="cache"/>; 304 returns the cached entry with NotModified = true; 200 refreshes the cache.</summary>
    public Task<ConditionalResponse> GetConditionalAsync(string path, string? token, GitHubConditionalCache cache, CancellationToken ct, params HttpStatusCode[] allow);

    /// <summary>The validated next-page URL of a response, or null (moved from GitHubClient.NextLink).</summary>
    public string? NextLink(HttpResponseMessage response);
}

internal sealed record ConditionalResponse(HttpStatusCode Status, string Body, string? NextLink, bool NotModified);

/// <summary>Thread-safe path -> (ETag, body, next link) store; one instance per long-lived client.</summary>
internal sealed class GitHubConditionalCache
{
    public bool TryGet(string path, out (string ETag, string Body, string? NextLink) entry);
    public void Set(string path, string etag, string body, string? nextLink);
}
```

**Behaviour:**
- Headers on every request exactly as today (`Accept: application/vnd.github+json` unless `accept` is given, `X-GitHub-Api-Version: 2022-11-28`, `User-Agent: codebase-guardian`), plus `Authorization: Bearer <token>` only when `token` is not null.
- Error mapping moved verbatim from `GitHubClient.ToExceptionAsync` (401, 403/429 rate limit via `retry-after` or `x-ratelimit-*`, other statuses with GitHub's message cut to 500 characters). Token scrubbing happens only when a token was sent.
- `GetConditionalAsync`: a 200 without an `ETag` header is returned but not cached. A 304 with no cache entry → `GitHubApiException(304, "GitHub returned 304 without a cached response.")`.
- `GitHubClient` keeps `PrepareAsync` (enabled / token required / resolver) and its DTOs; it creates one `GitHubHttp` per call over its typed `HttpClient`.

**Tests (write first):**
- `GitHubHttpTests` (`GitHubHttp` over `new HttpClient(fakeApi)` with base `https://api.github.com/`): no token → no `Authorization` header recorded; token → `Bearer`; `accept: "application/vnd.github.raw+json"` replaces the default `Accept`; a 409 in `allow` is returned, not thrown; a 409 not in `allow` throws `GitHubApiException(409, …)`; `ListAsync` follows two pages and refuses a `next` link to `https://evil.example/`; `GetConditionalAsync`: first call 200 with `ETag: "v1"` → `NotModified == false`; second call sends `If-None-Match: "v1"`, fake returns 304 → same body, `NotModified == true`; rate-limit 403 with `x-ratelimit-remaining: 0` → `GitHubRateLimitException` with the reset time; an error message never contains the token.
- `GitHubClientTests` unchanged and green.

**Acceptance:**
- [ ] `dotnet test` green (baseline rule); `git diff --stat HEAD~1 -- tests/CodebaseGuardian.Tests/App/GitHubClientTests.cs` shows no change.
- [ ] Commit: `refactor(github): extract shared HTTP core with anonymous and conditional requests`

---

### Task 28: GitHub repository read API and `FakeGitHubRepository`

**Goal:** A typed, token-optional client for the repository reads remote mode needs, and a stateful fake GitHub repository that later tasks seed with a commit graph.

**Files:**
- Create: `src/CodebaseGuardian/GitHub/IGitHubRepositoryApi.cs`, `GitHubRepositoryApi.cs`, `GitHubRepositoryModels.cs`
- Modify: `src/CodebaseGuardian/GitHub/GitHubExceptions.cs` (`GitHubNotFoundException`, `GitHubRevisionNotFoundException`)
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs` (`AddGitHubIntegration` registers `IGitHubRepositoryApi` as a singleton)
- Modify: `tests/CodebaseGuardian.Tests/Infrastructure/FakeGitHubApi.cs` (add `MapPrefix`, `RequestUris`)
- Create: `tests/CodebaseGuardian.Tests/Infrastructure/FakeGitHubRepository.cs`
- Create: `tests/CodebaseGuardian.Tests/App/GitHubRepositoryApiTests.cs`, `tests/CodebaseGuardian.Tests/Infrastructure/FakeGitHubRepositoryTests.cs`

**Interfaces:**
- Consumes: `GitHubHttp`, `GitHubConditionalCache`, `ConditionalResponse` (Task 27); `IGitHubTokenProvider`, `IGitHubRepositoryResolver`, `GitHubOptions`.
- Produces:

```csharp
namespace CodebaseGuardian.GitHub;

public sealed record GitHubRepositoryInfo(string DefaultBranch, bool Private);
public sealed record GitHubCommit(string Sha, string AuthorName, string AuthorEmail, DateTimeOffset CommittedAt, string Message, IReadOnlyList<string> ParentShas);
/// <summary>Status: "added" | "removed" | "modified" | "renamed" | "copied" | "changed" | "unchanged". Patch null when GitHub omits it.</summary>
public sealed record GitHubFileChange(string Path, string? PreviousPath, string Status, int Additions, int Deletions, string? Patch);
public sealed record GitHubCommitDetail(GitHubCommit Commit, IReadOnlyList<GitHubFileChange> Files);
/// <summary>Status: "ahead" | "behind" | "identical" | "diverged". Commits oldest first (GitHub returns at most 250).</summary>
public sealed record GitHubComparison(string Status, int AheadBy, int BehindBy, string BaseSha, IReadOnlyList<GitHubCommit> Commits, IReadOnlyList<GitHubFileChange> Files);

public interface IGitHubRepositoryApi
{
    Task<GitHubRepositoryInfo> GetRepositoryAsync(CancellationToken ct);                    // conditional GET
    Task<IReadOnlyDictionary<string, string>> GetBranchHeadsAsync(CancellationToken ct);     // conditional first page; empty repo => {}
    Task<IReadOnlyList<GitHubCommit>> ListCommitsAsync(string? sha, int limit, CancellationToken ct); // newest first; empty repo => []
    Task<GitHubCommitDetail> GetCommitAsync(string sha, CancellationToken ct);
    Task<GitHubComparison> CompareAsync(string @base, string head, CancellationToken ct);
    Task<byte[]?> GetFileAsync(string path, string @ref, CancellationToken ct);             // 404 => null
    Task<Stream> OpenTarballAsync(string @ref, CancellationToken ct);                        // Task 29; throws NotImplementedException until then
}

public sealed partial class GitHubRepositoryApi(
    IHttpClientFactory httpClients, IOptions<GitHubOptions> options, IGitHubTokenProvider tokens,
    IGitHubRepositoryResolver resolver, ILogger<GitHubRepositoryApi> logger, TimeProvider? timeProvider = null) : IGitHubRepositoryApi;

public sealed class GitHubNotFoundException(GitHubRepositoryRef repository)
    : GitHubApiException(404, $"Repository {repository} was not found, or the token cannot read it (private repositories need contents:read).");

public sealed class GitHubRevisionNotFoundException(string revision, GitHubRepositoryRef repository)
    : GitHubApiException(404, $"Revision '{revision}' was not found in {repository}.")
{
    public string Revision { get; } = revision;
}
```

Test helpers (namespace `CodebaseGuardian.Tests.Infrastructure`):

```csharp
public sealed class FakeGitHubApi : HttpMessageHandler
{
    // New: route every path starting with prefix (after exact routes failed to match).
    public FakeGitHubApi MapPrefix(HttpMethod method, string prefix, Func<HttpRequestMessage, string, HttpResponseMessage> respond);
    // New: full URI of every request, in order (Requests stays as it is).
    public IReadOnlyList<Uri> RequestUris { get; }
}

/// <summary>A mutable commit graph served through FakeGitHubApi as acme/widgets (or the given names).</summary>
public sealed class FakeGitHubRepository
{
    public FakeGitHubRepository(FakeGitHubApi api, string owner = "acme", string name = "widgets", string defaultBranch = "main");
    public string DefaultBranch { get; set; }
    /// <summary>Adds a commit; parents must exist. Files: (path, status, additions, deletions, patch-or-null). Returns this.</summary>
    public FakeGitHubRepository Commit(string sha, string message, IReadOnlyList<string> parents, params GitHubFileChange[] files);
    public FakeGitHubRepository SetBranch(string branch, string sha);    // changes the branches ETag
    public FakeGitHubRepository DeleteBranch(string branch);
    public FakeGitHubRepository Forget(string sha);                     // commit now answers 404 (garbage-collected after a force-push)
    public FakeGitHubRepository File(string path, string content);      // served by contents (and by the tarball in Task 29)
    public int BranchListRequests { get; }                              // how many GET .../branches reached the fake (304s included)
}
```

**Behaviour:**
- Client creation: `httpClients.CreateClient(GitHubClient.HttpClientName)` per call, wrapped in `GitHubHttp`. One `GitHubConditionalCache` per `GitHubRepositoryApi` instance (singleton).
- Every method: `Enabled == false` → `GitHubUnavailableException` (same message as `GitHubClient`); resolver null → same `NoRepository` message; token from `IGitHubTokenProvider`, **null allowed** (anonymous).
- Endpoints (`{o}/{r}` escaped; refs escaped per `/`-segment; `{base}...{head}` with each side escaped):
  - `GET repos/{o}/{r}` (conditional) → `default_branch`, `private`. 404 → `GitHubNotFoundException`.
  - `GET repos/{o}/{r}/branches?per_page=100` → `[{name, commit{sha}}]`. Page 1 conditional; when 304 and a previous complete result exists, return it without fetching further pages; otherwise follow pages up to 5. More than 5 pages → warning log "Only the first 500 branches are watched." 409 → `{}`. 404 → `GitHubNotFoundException`.
  - `GET repos/{o}/{r}/commits?per_page={clamp(limit,1,100)}[&sha={sha}]` → newest first. 409 → `[]`. 404/422 with `sha` → `GitHubRevisionNotFoundException(sha)`.
  - `GET repos/{o}/{r}/commits/{sha}` → commit + `files[]` (follow file pages up to 5). 404/422 → `GitHubRevisionNotFoundException`.
  - `GET repos/{o}/{r}/compare/{base}...{head}` → `status`, `ahead_by`, `behind_by`, `base_commit.sha`, `commits[]`, `files[]`. 404 → `GitHubRevisionNotFoundException($"{base}...{head}")`.
  - `GET repos/{o}/{r}/contents/{path}?ref={ref}` with `Accept: application/vnd.github.raw+json` → raw bytes. 404 → null. Path segments escaped individually.
- Mapping: `CommittedAt` = `commit.committer.date`; author from `commit.author.{name,email}`; `ParentShas` from `parents[].sha`; file `filename` → `Path`, `previous_filename` → `PreviousPath`.
- `FakeGitHubRepository` derives everything from the graph: `commits?sha=X` lists ancestors of X newest first (ties by insertion order); compare computes ahead/behind sets from ancestry, `status`, `base_commit` = the merge base, `commits` = ahead set oldest first, `files` = union of those commits' files (last wins per path); unknown/forgotten SHAs → 404; no commits at all → 409 for commits, compare and branches. Branches are served with an `ETag` that changes on every `SetBranch`/`DeleteBranch`, answering 304 to a matching `If-None-Match`.

**Tests (write first):**
- `GitHubRepositoryApiTests` (API built over a `ServiceCollection` with `AddHttpClient(GitHubClient.HttpClientName)` + `FakeGitHubApi.Install`, or `FakeGitHubRepository` where convenient):
  - anonymous: with a token provider returning null, requests carry no `Authorization` and still succeed;
  - repository info parses `default_branch`; 404 → `GitHubNotFoundException` whose message names `acme/widgets`;
  - branch heads: two branches parsed; second call sends `If-None-Match` and returns the same map on 304; empty repository (409) → empty map;
  - list commits: mapping of every `GitHubCommit` field; `sha` query present; `per_page` clamped to 100; 409 → empty;
  - commit detail with two file pages → all files; 422 → `GitHubRevisionNotFoundException` with `Revision`;
  - compare: path is `/repos/acme/widgets/compare/main...feature%2Fx` for head `feature/x`; fields parsed;
  - contents: `Accept: application/vnd.github.raw+json`; bytes returned; 404 → null; path `docs/naïve file.md` escaped per segment;
  - `Enabled = false` → `GitHubUnavailableException`.
- `FakeGitHubRepositoryTests`: linear history A←B←C on main: `commits?sha=main` = C,B,A; compare A...C = ahead 2 [B,C]; feature branch from B with D: compare main...feature = diverged, ahead 1 [D], behind 1; `Forget(C)` → compare C...main = 404; empty fake → 409s; `SetBranch` changes the ETag.

**Acceptance:**
- [ ] `dotnet test` green (baseline rule).
- [ ] Commit: `feat(github): token-optional repository read API`

---

### Task 29: Tarball download and `SnapshotTarReader`

**Goal:** Stream the default-branch head as a tarball, in memory, through a host-allowlisted redirect that never forwards the token, and read it into `SnapshotFile`s with hard caps.

**Files:**
- Modify: `src/CodebaseGuardian/GitHub/GitHubRepositoryApi.cs` (`OpenTarballAsync`; `ArchiveHttpClientName`)
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs` (`AddGitHubIntegration`: `services.AddHttpClient(GitHubRepositoryApi.ArchiveHttpClientName).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })`)
- Create: `src/CodebaseGuardian/Sources/SnapshotTarReader.cs`
- Modify: `tests/CodebaseGuardian.Tests/Infrastructure/FakeGitHubApi.cs` (`Install` also sets the primary handler of the archive client)
- Modify: `tests/CodebaseGuardian.Tests/Infrastructure/FakeGitHubRepository.cs` (tarball + codeload routes built from `File(...)` entries; `TarballBuilder` helper for hand-made archives)
- Create: `tests/CodebaseGuardian.Tests/App/GitHubTarballTests.cs`, `tests/CodebaseGuardian.Tests/App/SnapshotTarReaderTests.cs`

**Interfaces:**
- Consumes: `IGitHubRepositoryApi`, `GitHubHttp` (Tasks 27–28); `SnapshotFile`, `SnapshotLimits` (Task 25).
- Produces:

```csharp
namespace CodebaseGuardian.GitHub;
public sealed partial class GitHubRepositoryApi
{
    public const string ArchiveHttpClientName = "github-archive";
    // OpenTarballAsync(string @ref, CancellationToken ct): see Behaviour. The returned stream owns and disposes the HTTP response.
}
```

```csharp
namespace CodebaseGuardian.Sources;

public sealed record SnapshotCaps(long MaxFileBytes, long MaxTotalBytes, int MaxFiles);

internal static class SnapshotTarReader
{
    /// <summary>Reads a gzip-compressed tar; never throws for archive content (only for cancellation).</summary>
    public static IAsyncEnumerable<SnapshotFile> ReadAsync(Stream gzipTar, SnapshotCaps caps, ILogger logger, CancellationToken ct);
}
```

Test helper:

```csharp
namespace CodebaseGuardian.Tests.Infrastructure;
public static class TarballBuilder
{
    /// <summary>gzip-compressed tar; entries are added under "acme-widgets-<sha7>/" unless rawNames is true.</summary>
    public static byte[] Build(IEnumerable<(string Name, TarEntryType Type, byte[] Content, string? LinkName)> entries, bool rawNames = false);
}
```

**Behaviour:**
- `OpenTarballAsync(ref)`:
  1. Archive client (`ArchiveHttpClientName`), absolute URL `{ApiBaseUrl}/repos/{o}/{r}/tarball/{ref}` (ref escaped per segment), `Authorization` only when a token exists, `HttpCompletionOption.ResponseHeadersRead`.
  2. `302`/`301`/`307` with `Location`: must be absolute `https`; allowed host is `codeload.github.com` when the API host is `api.github.com`, else the API host itself (GHES). Anything else → `GitHubApiException(502, "Refusing tarball redirect to an unexpected host.")` (the message must not include the URL: it carries a token).
  3. Follow with a **new** request with no `Authorization` header, `ResponseHeadersRead`. At most one redirect.
  4. `200` directly from the API host is also accepted. `404` → `GitHubRevisionNotFoundException(ref)`. Other errors → normal mapping.
  5. Return a stream wrapper that disposes the response when disposed. Nothing touches disk.
- `SnapshotTarReader.ReadAsync`: `GZipStream` → `TarReader` (`GetNextEntryAsync(copyData: false)`):
  - Skip `GlobalExtendedAttributes`/`ExtendedAttributes` entries; strip the first path segment of every remaining entry; an entry that becomes empty (the top directory) is skipped.
  - Yield only `RegularFile`/`V7RegularFile` entries; skip directories, symlinks, hard links, devices, FIFOs.
  - Entry name absolute, containing `\`, or with any `..` segment → skipped, warning logged with the sanitized name; never yielded.
  - `Length > caps.MaxFileBytes` → `SnapshotFile(path, null, "larger than 1 MB")` without reading its data.
  - Before yielding a file, if yielding it would exceed `MaxFiles` or the running total of content bytes would exceed `MaxTotalBytes` → yield sentinel `("", null, $"stopped after {n} files and {bytes} bytes (Guardian:Remote:MaxSnapshotFiles / MaxSnapshotBytes)")` and stop.
  - `InvalidDataException`/`EndOfStreamException`/`FormatException` from gzip or tar → sentinel `("", null, "the archive could not be read: <exception message>")` and stop.
- `FakeGitHubRepository`: `GET /repos/acme/widgets/tarball/{ref}` → 302 to `https://codeload.github.com/acme/widgets/legacy.tar.gz/{sha}?token=fake`; the codeload route serves `TarballBuilder.Build` of the current `File(...)` entries. Because `FakeGitHubApi` routes by path only, tests assert hosts through `RequestUris`.

**Tests (write first):**
- `GitHubTarballTests` (**Review Focus 3**): with a token, the API request has `Authorization` and the codeload request has none (`Requests[i].Headers` + `RequestUris[i].Host`); redirect to `https://evil.example/x.tgz` → `GitHubApiException` 502 whose message does not contain `evil.example` or `token=`; redirect to `http://codeload.github.com/...` (not https) → refused; GHES base `https://ghe.acme.test/api/v3` accepts a redirect to `ghe.acme.test` and refuses `codeload.github.com`; 404 → `GitHubRevisionNotFoundException`; disposing the stream disposes the response (assert via a fake content stream that records disposal).
- `SnapshotTarReaderTests` (`TarballBuilder`; **Review Focus 5**): regular files yielded with stripped prefix and exact bytes; `docs/naïve file.md` verbatim; a symlink entry and a hard link are not yielded; `acme-widgets-abc1234/../../etc/passwd` and `/abs` are not yielded and nothing outside appears; an over-cap file yields `Skipped` with null content; caps `MaxFiles = 2` with three files → two files then the sentinel; `MaxTotalBytes = 10` with two 8-byte files → one file then the sentinel; random bytes (not gzip) → only the sentinel with "could not be read"; cancellation token cancelled → `OperationCanceledException`.

**Acceptance:**
- [ ] `dotnet test` green (baseline rule).
- [ ] Commit: `feat(github): stream repository tarballs with an allowlisted, token-free redirect`

---

### Task 30: `GitHubApiSource`

**Goal:** The remote implementation of `IRepositorySource`, matching local semantics wherever the API allows and failing with clear argument errors where it cannot.

**Files:**
- Create: `src/CodebaseGuardian/Sources/GitHubApiSource.cs`
- Create: `src/CodebaseGuardian/Sources/RemoteRevision.cs`
- Create: `src/CodebaseGuardian/Sources/GitHubPatchBuilder.cs`
- Create: `tests/CodebaseGuardian.Tests/App/GitHubApiSourceTests.cs`, `tests/CodebaseGuardian.Tests/App/GitHubPatchBuilderTests.cs`, `tests/CodebaseGuardian.Tests/App/RemoteRevisionTests.cs`

**Interfaces:**
- Consumes: `IRepositorySource`, `SnapshotFile`, `SnapshotLimits` (Task 25); `IGitHubRepositoryApi` + models + exceptions (Tasks 28–29); `SnapshotTarReader`, `SnapshotCaps` (Task 29); `RemoteOptions`, `RepositoryLocation` (Task 24); `PatchParser` (existing, `CodebaseGuardian.Security`); `GitRevision`, `RepoStatus`, `CommitInfo`, `DiffSummary`, `FileDiffStat` (existing).
- Produces:

```csharp
namespace CodebaseGuardian.Sources;

public sealed class GitHubApiSource(
    IGitHubRepositoryApi api, RepositoryLocation location, IOptions<RemoteOptions> remote, IOptions<GitHubOptions> github,
    ILogger<GitHubApiSource> logger) : IRepositorySource;   // location must be RepositoryLocation.GitHub, else ArgumentException

public static class RemoteRevision
{
    /// <summary>GitRevision.IsValid and none of "~", "^", "@{", ":". Else ArgumentException("Remote mode accepts branch names, tag names and commit SHAs only.", parameterName).</summary>
    public static string Require(string revision, string parameterName);
}

internal static class GitHubPatchBuilder
{
    /// <summary>Stitches per-file API patches into one unified diff that PatchParser reads; applies the binary/truncation rules.</summary>
    public static DiffSummary Build(string from, string to, IReadOnlyList<GitHubFileChange> files, int maxPatchBytes, bool fileListCapped);
}
```

**Behaviour (spec §3.1, §3.2, §4.3):**
- `Kind = GitHub`; `DisplayName` = `github.com/acme/widgets`, or `{ApiBaseUrl host}/acme/widgets` when the API host is not `api.github.com`.
- `"HEAD"` (exactly) as a revision means the default branch; every other revision goes through `RemoteRevision.Require`.
- `GetCurrentBranchAsync` → `GetRepositoryAsync().DefaultBranch`. `GetBranchHeadsAsync` → API. `GetHeadShaAsync` → heads[default] or null.
- `GetStatusAsync` → `RepoStatus(default, headSha, null, 0, 0, [], [], [], [])`.
- `GetRecentCommitsAsync(limit, revision)` → `ListCommitsAsync`, mapped: `ShortSha` = first 7 characters, `Subject` = first line of `Message` (no trailing `\r`).
- `GetNewCommitsAsync(tip, excludeTips, limit)`: no excludes → `ListCommitsAsync(tip, limit)` reversed. Otherwise for each of the first 5 distinct excludes (in the given order, skipping `exclude == tip`, which contributes an empty set): `CompareAsync(exclude, tip)`; `GitHubRevisionNotFoundException` → that exclude is dropped (warning log). Result = intersection of the `Commits` lists by SHA, keeping the order of the first comparison (oldest first), last `limit` kept. All excludes dropped → treat as no excludes. More than 5 excludes → debug log "approximate".
- `GetDiffSummaryAsync(from, to, max)`: either null → `ArgumentException("Remote mode has no working tree: pass both 'from' and 'to'.")`; else `CompareAsync` → `GitHubPatchBuilder.Build(base_commit.sha, lastCommitSha ?? to, files, max, files.Count >= 300)`.
- `GetCommitDiffAsync(sha, max)` → `GetCommitAsync` → `Build(parents[0] ?? "(empty)", sha, files, max, false)` (`"(empty)"` is the marker local mode uses for root commits).
- `GitHubPatchBuilder.Build` per file: header lines `diff --git a/{old} b/{new}`, `--- a/{old}` or `--- /dev/null` (added), `+++ b/{new}` or `+++ /dev/null` (removed), then `Patch` + `\n` if missing. `old = PreviousPath ?? Path`. `Patch == null && Additions + Deletions == 0` → `FileDiffStat(Path, null, null, Binary: true)`, no hunk text. `Patch == null` with non-zero counts → counts kept, `PatchTruncated = true`. `fileListCapped` → `PatchTruncated = true`. Patch longer than `maxPatchBytes` UTF-8 bytes → cut at the last complete line within the limit and `PatchTruncated = true` (match how `GitRepository.CapPatch` cuts; read it first). `maxPatchBytes == 0` → empty patch, `PatchTruncated` false unless one of the other rules applies. `Insertions`/`Deletions` totals = sums over non-binary files.
- `ReadFileAsync(path)`: validate like `GitRepository.ReadFileAsync`; resolve the head SHA at call time; null head (empty repo) → null; else `GetFileAsync(path, headSha)`.
- `ReadSnapshotAsync`: resolve the head SHA once; null → yield nothing; else `OpenTarballAsync(headSha)` → `SnapshotTarReader.ReadAsync(stream, new SnapshotCaps(SnapshotLimits.MaxFileBytes, remote.MaxSnapshotBytes, remote.MaxSnapshotFiles), logger, ct)`.
- No method catches `GitHubRateLimitException` or `GitHubUnavailableException`; they propagate to callers (tools map them in Task 31, the watcher in Task 32).

**Tests (write first)** (`GitHubApiSource` over the real `GitHubRepositoryApi` + `FakeGitHubRepository`):
- **Review Focus 1:** empty repository → `GetStatusAsync` has `Branch == "main"`, `HeadSha == null`; `GetRecentCommitsAsync` empty; `GetBranchHeadsAsync` empty; `ReadSnapshotAsync` yields nothing.
- status/head/branches on a seeded graph; `GetRecentCommitsAsync(2, "HEAD")` = newest two of main with all `CommitInfo` fields; `GetRecentCommitsAsync(10, "main~1")` → `ArgumentException` with the remote message.
- `GetNewCommitsAsync`: linear A←B←C, exclude [A] → [B, C]; feature branch D from B, tip D, excludes [main=C] → [D]; excludes [Forgotten X, C] → [D] (X dropped); excludes [] → full history oldest first, limited.
- `GetDiffSummaryAsync(null, "main")` → `ArgumentException` with the exact message; `("main", "feature")` → stats and a patch that `PatchParser` parses to the same added lines the fake files contain; `From` = merge-base SHA, `To` = D.
- `GetCommitDiffAsync` on a root commit → `From == "(empty)"`.
- `ReadFileAsync(".guardianignore")` served from contents at the head SHA (assert the `ref` query).
- `ReadSnapshotAsync` yields the fake's files.
- `GitHubPatchBuilderTests`: added / removed / renamed / binary / missing-patch-with-counts / 300-file cap / byte cap / zero cap — each with its expected `DiffSummary`; every built patch round-trips through `PatchParser` (added-line paths and line numbers match).
- `RemoteRevisionTests`: `main`, `feature/x`, `v1.2.3`, a 40-hex SHA accepted; `HEAD~1`, `main^`, `main@{1}`, `a:b`, `-x`, `a..b` rejected.

**Acceptance:**
- [ ] `dotnet test` green (baseline rule).
- [ ] Commit: `feat(sources): GitHub API repository source`

---

### Task 31: Remote-mode composition, tool behaviour and instructions

**Goal:** `--repo github:owner/name` starts a working server: only mode-valid tools, resources and events are offered, descriptions and instructions say what remote mode means, and GitHub failures become readable tool errors.

**Files:**
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs` (mode branch per spec §4.7)
- Create: `src/CodebaseGuardian/GitHub/FixedGitHubRepositoryResolver.cs`
- Create: `src/CodebaseGuardian/Tools/RemoteDescriptionAttribute.cs`
- Modify: `src/CodebaseGuardian/Tools/RepositoryTools.cs`, `SecurityTools.cs`, `ScanTools.cs` (add `[RemoteDescription(...)]` to `repo_status`, `recent_commits`, `diff_summary`, `scan_secrets`, `full_scan`)
- Modify: `src/CodebaseGuardian/Tools/ToolErrors.cs` (map GitHub exceptions)
- Modify: `src/CodebaseGuardian/Watching/GuardianEvents.cs` (`Register(EventsOptions options, RepositorySourceKind kind)`)
- Modify: `src/CodebaseGuardian/Hosting/GuardianInstructions.cs` (remote paragraph)
- Modify: `src/CodebaseGuardian/Scanning/FullScanService.cs`, `ScanReport.cs`, `ScanReportRenderer.cs` (optional dependency/check services; `DependenciesSkippedReason`)
- Create: `tests/CodebaseGuardian.Tests/Infrastructure/RemoteGuardianTestHost.cs`
- Create: `tests/CodebaseGuardian.Tests/App/RemoteModeTests.cs`, `tests/CodebaseGuardian.Tests/App/LocalModeSurfaceTests.cs`
- Modify: `tests/CodebaseGuardian.Tests/App/ScanReportRendererTests.cs`, `FullScanServiceTests.cs` (new record field; new remote cases)

**Interfaces:**
- Consumes: everything from Tasks 24–30.
- Produces:

```csharp
namespace CodebaseGuardian.GitHub;
public sealed class FixedGitHubRepositoryResolver(RepositoryLocation location) : IGitHubRepositoryResolver; // returns the GitHub location's ref

namespace CodebaseGuardian.Tools;
[AttributeUsage(AttributeTargets.Method)]
public sealed class RemoteDescriptionAttribute(string description) : Attribute { public string Description { get; } = description; }

namespace CodebaseGuardian.Scanning;
public sealed record ScanReport(/* existing fields */ ..., string? ChecksSkippedReason, string? DependenciesSkippedReason, IReadOnlyList<string> StepErrors, string Markdown);
public sealed class FullScanService(
    ISecretScanner secrets, IRepositorySource source, IEventPublisher publisher, ScanReportStore store, TimeProvider time,
    IDependencyAuditor? dependencies = null, ICheckRunner? checks = null, ICheckCommandResolver? resolver = null) : IFullScanService;

namespace CodebaseGuardian.Tests.Infrastructure;
public static class RemoteGuardianTestHost
{
    /// <summary>GuardianTestHost.StartAsync("github:acme/widgets") with Guardian:GitHub:Enabled=true, PollEnabled=false,
    /// WatchEnabled=false (override via configuration), FakeGitHubApi installed; returns the server and the fake repository.</summary>
    public static Task<(InProcessMcpServer Server, FakeGitHubRepository Repository, FakeGitHubApi Api)> StartAsync(
        Action<FakeGitHubRepository>? seed = null, IDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? configureServices = null, CancellationToken cancellationToken = default);
}
```

**Behaviour:**
- Composition root reads the `RepositoryLocation` singleton it registered (Task 24) and branches:
  - Local: unchanged registrations (Task 25 DI), `GitHubRepositoryResolver`, all tools/resources/events.
  - GitHub: `IRepositorySource` → `GitHubApiSource`; **no** `IGitRepository`, `GitRepository`, check services, `IDependencyAuditor`, `AutoChecksCommitHandler`; `IGitHubRepositoryResolver` → `FixedGitHubRepositoryResolver`; tools without `CheckTools`, `DependencyTools`; resources without `CheckResources`; `GuardianEvents.Register(options, RepositorySourceKind.GitHub)` omits `repo.files.changed`, `checks.completed`, `checks.failed`; GitHub events registered (Enabled is validated true).
  - `RepositoryLocation.Invalid` registers the local stack (validation fails start-up anyway).
- `WithGuardianTools` gets the location; in GitHub mode a method with `[RemoteDescription]` is created with `McpServerToolCreateOptions.Description = attribute.Description`.
- Remote descriptions (write them so the model can act on them):
  - `repo_status`: default branch and its head commit of the GitHub repository; no working tree, so the file lists are always empty.
  - `recent_commits`: newest first from the GitHub API; `branch` is a branch, tag or SHA (`HEAD` = default branch; `~`/`^` not supported).
  - `diff_summary`: both `from` and `to` required; changes from their merge base to `to` (GitHub compare, three-dot); at most 300 files.
  - `scan_secrets`: `working_tree` scans the default branch head (downloaded in memory), `commit` scans one commit, `staged` is not available; check `complete` and `warnings`.
  - `full_scan`: secret scan of the default branch head only; dependency audit and checks are not available in remote mode.
- `ToolErrors.RunAsync` also maps `GitHubApiException` (rate limit included — its message names the reset time) and `GitHubUnavailableException` to `McpException(ex.Message)`.
- `GuardianInstructions` in GitHub mode: "Codebase Guardian is watching the GitHub repository {owner}/{name} through the GitHub API (remote mode). There is no working tree: run_checks and audit_dependencies are not available, diff_summary needs both ends, and scan_secrets working_tree scans the default branch head. It offers tools to inspect the repository and audit it for secrets, resources and events that report what changes, and skills that explain how to react."
- `FullScanService`: `dependencies == null` → no audit, `DependenciesSkippedReason = "Not available in remote mode."`; `checks == null || resolver == null` → no checks, `ChecksSkippedReason = "Not available in remote mode."` (whatever `includeChecks` says). `ScanReportRenderer` prints "Skipped: <reason>" for each skipped section and uses `source.DisplayName` + head SHA in the header. Local output unchanged except the new record field being null.

**Tests (write first):**
- `RemoteModeTests` (`RemoteGuardianTestHost`, seeded main A←B with a file containing `FakeSecrets.AwsAccessKeyId()` in B's patch and in the tarball files):
  - `tools/list` = exactly `repo_status, recent_commits, diff_summary, scan_secrets, poll_events, create_issue, comment_on_pr, open_pull_request, full_scan`; the five tools carry their remote descriptions;
  - `events/list` lacks `repo.files.changed`, `checks.completed`, `checks.failed` and has `github.ci.failed`;
  - resource templates/resources lack `guardian://checks/*`;
  - server instructions start with "Codebase Guardian is watching the GitHub repository acme/widgets";
  - `repo_status` structured content: `branch: "main"`, `headSha: B`, empty lists, `upstream: null`;
  - `recent_commits` returns B, A; `recent_commits branch="main~1"` → `isError: true` with the remote revision message;
  - `diff_summary` without `to` → `isError: true` "Remote mode has no working tree…"; with `from=A, to=main` → B's file;
  - `scan_secrets commit=B` → one finding, `complete: true`; `scan_secrets` (working tree) → one finding from the tarball; `scan_secrets scope=staged` → `isError: true` "Remote mode has nothing staged.";
  - **Review Focus 5:** the tarball made with `MaxSnapshotFiles=1` (configuration) and two files → `complete: false`, one warning;
  - **Review Focus 4:** fake answers 403 + `x-ratelimit-remaining: 0` + reset → `recent_commits` returns `isError: true` whose text contains the reset time in `O` format (not a JSON-RPC error);
  - start-up with `Guardian:AutoChecks=true` fails with the Task 24 message;
  - no request in `Api.RequestUris` has a host other than `api.github.com` / `codeload.github.com`.
- `LocalModeSurfaceTests` (**Review Focus 6**, `GuardianTestHost` over a `TempGitRepo`): `tools/list` names = the eleven v1 tools; `events/list` names = the v1 catalog for the configuration used (GitHub disabled); resource templates include `guardian://checks/{runId}/log`; instructions start with "Codebase Guardian is watching the Git repository at".
- `FullScanServiceTests`: remote construction (no dependency/check services) → report with both skipped reasons, `checksPassed: null` in `scan.completed`; `ScanReportRendererTests`: "Skipped: Not available in remote mode." for both sections.

**Acceptance:**
- [ ] `dotnet test` green (baseline rule).
- [ ] Manual smoke (no network needed to start): `dotnet run --project src/CodebaseGuardian -- --repo github:acme/widgets --no-watch --auto-checks` exits non-zero naming the AutoChecks rule.
- [ ] Commit: `feat(remote): remote-mode composition, tool surface and instructions`

---

### Task 32: Watching a GitHub repository

**Goal:** The existing watcher runs unchanged in spirit over `IRepositorySource`, polling the API at a rate-limit-friendly interval, surviving force-pushes, empty repositories and rate limits.

**Files:**
- Modify: `src/CodebaseGuardian/Watching/RepositoryWatcher.cs`
- Modify: `src/CodebaseGuardian/Hosting/GuardianServiceCollectionExtensions.cs` (the watcher gets `TimeProvider`, `IOptions<RemoteOptions>` and an optional `IGitHubTokenProvider`)
- Create: `tests/CodebaseGuardian.Tests/App/RemoteRepositoryWatcherTests.cs`

**Interfaces:**
- Consumes: `IRepositorySource`, `RemoteOptions`, `GitHubApiSource` (via `IRepositorySource`), `GitHubRateLimitException`, `IGitHubTokenProvider`, `FakeGitHubRepository`, `RemoteGuardianTestHost`.
- Produces:

```csharp
namespace CodebaseGuardian.Watching;
public sealed class RepositoryWatcher(
    IRepositorySource source, IEventPublisher publisher, IEnumerable<IRepositoryChangeHandler> handlers,
    IOptions<GuardianOptions> options, IOptions<RemoteOptions> remote, ILogger<RepositoryWatcher> logger,
    TimeProvider? timeProvider = null, IGitHubTokenProvider? tokens = null) : BackgroundService
{
    /// <summary>The poll interval in effect after StartAsync (remote: max(PollIntervalSeconds, 300 s) without a token).</summary>
    internal TimeSpan Interval { get; }
}
```

**Behaviour (spec §4.6):**
- Interval: local → `WatchIntervalMs`; remote → `PollIntervalSeconds`, or `max(PollIntervalSeconds, 300)` seconds when `tokens` is null or returns null at start; in that case log once at warning: "No GitHub token: polling acme/widgets every 300 s (unauthenticated limit 60 requests/hour)."
- The `PeriodicTimer` is created with the injected `TimeProvider` (`new PeriodicTimer(interval, time)`), so tests advance a `FakeTimeProvider`. Local behaviour unchanged with `TimeProvider.System`.
- `WorkingTreeWatcher` only when the source is an `IGitRepository` (done in Task 25; keep).
- Exclude tips passed to `GetNewCommitsAsync` are ordered: previous tip of the same branch (if any), then the current head of the current/default branch, then the remaining previous tips in ordinal branch order; distinct; the tip itself excluded.
- The existing `GitException` retry branch stays for local; it is not reached remotely.
- `GitHubRateLimitException` anywhere in a poll (baseline included) → warning log once with `ResetAt`, no polls until `time.GetUtcNow() >= ResetAt`, then resume; `_heads` untouched so nothing is lost or repeated.
- Other exceptions from a remote poll (`GitHubApiException`, `HttpRequestException`, `GitHubUnavailableException`) → error log, retry at the next interval (today's behaviour).
- Baseline failure at start (API down) → the first successful poll takes the baseline instead of announcing history (verify today's code does this; if not, add it).

**Tests (write first)** (`RemoteRepositoryWatcherTests`; watcher constructed directly over `GitHubApiSource` + `FakeGitHubRepository`, `FakeTimeProvider`, in-memory `IEventPublisher` capture or `RemoteGuardianTestHost` with `events/poll` where simpler):
- baseline on A; push B to main; advance one interval → one `repo.commit.created` for B with `branch: "main"`, files from B, `suggestedSkill` pr-review; a second advance with no change sends `If-None-Match` and announces nothing (`BranchListRequests` grew, no new events);
- **Review Focus 1:** empty repository at start; push A; advance → A announced once;
- new branch `feature` at D (parent B) → D announced only;
- **Review Focus 2:** force-push main from C to C' (parent B), `Forget(C)` → C' announced once, watcher still running, a later push C'' announced;
- branch deleted → no event, no error log beyond debug, next push elsewhere announced;
- default branch switched from `main` to `trunk` → `repo.branch.changed` `{from: "main", to: "trunk", headSha}`;
- a commit touching `package.json` → `repo.dependencies.changed` with `commitSha`;
- **Review Focus 4:** fake answers 403 rate limit with reset = now + 10 min; advance one interval → no further branch requests until the fake clock passes the reset; then a pending push is announced exactly once;
- no token → `Interval == 300 s` and the warning logged; token + `PollIntervalSeconds = 30` → 30 s;
- secret in a pushed commit → `security.secret_detected` (through the existing `SecretScanCommitHandler`).
- The existing `RepositoryWatcherTests` stay green unchanged (baseline rule).

**Acceptance:**
- [ ] `dotnet test` green (baseline rule).
- [ ] Commit: `feat(watching): poll GitHub repositories with conditional requests and rate-limit backoff`

---

### Task 33: Skills, documentation and remote end-to-end

**Goal:** Users and agents learn remote mode from the README, the server instructions and the skills; one end-to-end test exercises the whole remote flow.

**Files:**
- Modify: `README.md` ("Remote mode (GitHub API)" section: forms of `--repo`, token rules and scopes, what is unavailable, `Guardian:Remote:*` settings, rate-limit behaviour, three-dot `diff_summary` note, nothing written to disk)
- Modify: `src/CodebaseGuardian/skills/guardian/SKILL.md` (Remote mode paragraph: absent events and tools, how to tell the mode from the instructions)
- Modify: `src/CodebaseGuardian/skills/guardian/references/events.md` (mark `repo.files.changed`, `checks.*` as local-only)
- Modify: `src/CodebaseGuardian/skills/bug-triage/SKILL.md` (without `run_checks`: use the `github.ci.failed` payload and run URL, `recent_commits`, `diff_summary from=<last green sha> to=<failing sha>`; never claim to have reproduced locally)
- Modify: `src/CodebaseGuardian/skills/dependency-hygiene/SKILL.md` (without `audit_dependencies`: report the changed manifests from `repo.dependencies.changed`, recommend a local run; never invent audit results)
- Modify: `docs/specs/2026-10-04-codebase-guardian-design.md` (one line under §1 linking the remote-mode spec; nothing else)
- Create: `tests/CodebaseGuardian.Tests/App/RemoteEndToEndTests.cs`
- Modify: `tests/CodebaseGuardian.Tests/App/SkillContentTests.cs` only if the new text needs an allowance (it should not)

**Interfaces:**
- Consumes: everything above.
- Produces: documentation only, plus the E2E test.

**Behaviour:**
- Skill text stays within the existing size and frontmatter rules (`SkillContentTests`); every tool/event named exists in local mode.
- README examples use `github:octo-org/payments`; no real tokens or real private repositories.

**Tests (write first):**
- `RemoteEndToEndTests` (`RemoteGuardianTestHost` with `WatchEnabled=true`, `PollIntervalSeconds=15` and a `FakeTimeProvider` registered via `configureServices`, `McpClient`):
  1. load `skill://guardian/SKILL.md` via `resources/read` and assert it contains "Remote mode";
  2. `events/poll` from now; push a commit with an AWS key in its patch and in a new tarball file; advance the clock;
  3. `events/poll` returns `repo.commit.created` then `security.secret_detected` with redacted values only (no raw key anywhere in the JSON);
  4. `full_scan` as a task (as `ScanToolsTests` does) completes; `guardian://scans/{id}/report` contains `github.com/acme/widgets`, the head SHA, "Skipped: Not available in remote mode." twice, and the redacted finding;
  5. `scan.completed` payload: `secretFindings: 1`, `vulnerablePackages: 0`, `checksPassed: null`, `suggestedSkill` security-audit.
- `SkillContentTests` green.

**Acceptance:**
- [ ] `dotnet test` green (baseline rule).
- [ ] README renders (headings, code fences closed); `grep -n "Remote mode" README.md src/CodebaseGuardian/skills/*/SKILL.md` finds the new sections.
- [ ] Commit: `docs(remote): document remote mode in README and skills; remote end-to-end test`
