# Codebase Guardian — GitHub Remote Mode Design Spec

- **Date:** 2026-10-07
- **Status:** Draft for review. Once approved, binding authority for Epic 4 in `docs/backlog/BACKLOG.md`.
- **Extends:** `docs/specs/2026-10-04-codebase-guardian-design.md` (the "v1 spec"). Everything not changed here stays as the v1 spec says.

## 1. Purpose

Today the Guardian watches one **local** Git working tree. Remote mode lets it watch one **GitHub repository that is not checked out** on the machine, read entirely through the GitHub REST API:

```
codebase-guardian --repo github:octo-org/payments
codebase-guardian --repo https://github.com/octo-org/payments
```

The agent gets the same tools, resources, events and skills it already knows, minus the ones that need a checkout to execute code. No clone is made; nothing from the repository is written to disk.

### Decisions taken during brainstorming

| # | Decision |
|---|---|
| D1 | **Pure API, never clone.** All repository data comes from the GitHub REST API. File contents are streamed in memory; nothing is written to disk. |
| D2 | **One repository per server, chosen at startup** through `--repo` / `Guardian:RepositoryPath`. Tool names and arguments stay the same as in local mode. No per-call `repository` argument. |
| D3 | **Execution-bound tools are hidden.** `run_checks` and `audit_dependencies` are not registered in remote mode; `full_scan` runs only its secret-scan part. |
| D4 | **Approach A — split abstraction.** A new source-neutral `IRepositorySource` interface; `IGitRepository` (local) extends it with working-tree members; a new `GitHubApiSource` implements it over REST. Consumers depend on the narrowest interface they need. |

## 2. Configuration and mode selection

### 2.1 Repository location

`Guardian:RepositoryPath` (CLI `--repo`) keeps its name and accepts either form. A new value object parses it once:

```csharp
namespace CodebaseGuardian.Sources;

public abstract record RepositoryLocation
{
    public sealed record Local(string FullPath) : RepositoryLocation;
    public sealed record GitHub(GitHubRepositoryRef Repository) : RepositoryLocation;

    /// <summary>Never throws; an unparseable remote form is reported by validation.</summary>
    public static RepositoryLocation Parse(string value);
}
```

| Input | Result |
|---|---|
| `github:owner/name` | GitHub |
| `https://github.com/owner/name`, with optional `.git` and optional trailing `/` | GitHub |
| `git@github.com:owner/name.git`, `ssh://git@github.com/owner/name.git` | GitHub (reuse `GitHubRepositoryRef.TryParseRemoteUrl`) |
| anything else (including bare `owner/name`) | Local — it is a path. `owner/name` stays a relative path because it is ambiguous. |

`github:` with invalid segments (fails `GitHubRepositoryRef.IsValidSegment`) or a `https://github.com/…` URL with extra path segments (`/tree/main`, `/pull/1`) is a validation failure, not a local path. GitHub Enterprise Server is reached with `github:owner/name` plus `Guardian:GitHub:ApiBaseUrl`; URL forms are github.com only.

`RepositoryLocation` is registered as a singleton. The `PostConfigure` that calls `Path.GetFullPath` applies only to local values.

### 2.2 Validation (startup fails, naming every problem)

In remote mode, `GuardianOptionsValidator` and `GitHubOptionsValidator` report:
- `Guardian:RepositoryPath` remote form is invalid (see 2.1).
- `Guardian:AutoChecks=true` → "AutoChecks needs a local checkout; it is not available in remote mode."
- `Guardian:GitHub:Enabled=false` → "Remote mode reads the repository through the GitHub API; Guardian:GitHub:Enabled must be true."
- `Guardian:GitHub:Owner`/`Repository` set and different from the `--repo` repository → name both values.

The `Directory.Exists` check runs only in local mode.

### 2.3 Remote options

New section `Guardian:Remote` (`RemoteOptions`):

| Key | Default | Rule |
|---|---|---|
| `PollIntervalSeconds` | 60 | minimum 15. Without a token the effective interval is `max(configured, 300)` and a startup warning says so (unauthenticated limit is 60 requests/hour). |
| `MaxSnapshotBytes` | 104 857 600 (100 MB) | uncompressed bytes read from the tarball before the snapshot stops |
| `MaxSnapshotFiles` | 20 000 | regular files read before the snapshot stops |

`Guardian:WatchIntervalMs` and `Guardian:FileChangeDebounceMs` are ignored in remote mode. `Guardian:WatchEnabled` still turns the watcher on or off.

### 2.4 Token

Reads (`IGitHubRepositoryApi`, §4.2) work without a token so public repositories can be watched anonymously; private repositories need a token with `contents:read` (plus `metadata:read`). The action tools (`create_issue`, `comment_on_pr`, `open_pull_request`) and the GitHub event poller keep requiring a token exactly as today. The token is never logged, never put in a URL, never sent to any host other than `Guardian:GitHub:ApiBaseUrl`'s host.

## 3. Behaviour in remote mode

### 3.1 Surface

| Item | Remote mode |
|---|---|
| `repo_status` | Registered. `Branch` = default branch, `HeadSha` = its head; `Upstream` null; `Ahead`/`Behind` 0; `Staged`, `Unstaged`, `Untracked`, `Conflicted` empty. Same output schema. |
| `recent_commits` | Registered. `GET /repos/{o}/{r}/commits?sha={rev}&per_page={limit}`. `branch` omitted or `HEAD` → default branch. |
| `diff_summary` | Registered. **Both `from` and `to` are required**; missing either → tool error "Remote mode has no working tree: pass both 'from' and 'to'." Uses `GET /repos/{o}/{r}/compare/{from}...{to}` — **three-dot (merge-base) semantics**, unlike local `git diff from to`; the tool description says so in remote mode. |
| `scan_secrets` | Registered. `commit` scope → commit API patch. `working_tree` scope → default-branch head snapshot (§4.4). `staged` → tool error "Remote mode has nothing staged." |
| `full_scan` | Registered. Secret scan of the snapshot plus repository summary; dependency and checks sections render as "Skipped: not available in remote mode". `includeChecks` is accepted and ignored. `scan.completed` payload reports `vulnerablePackages: 0`, `checksPassed: null`. |
| `poll_events` | Registered, unchanged. |
| `create_issue`, `comment_on_pr`, `open_pull_request` | Registered, unchanged. |
| `run_checks`, `audit_dependencies` | **Not registered.** |
| `guardian://repo/status`, `guardian://repo/commits/recent` | Registered, served through `IRepositorySource`. |
| `guardian://checks/latest`, `guardian://checks/{runId}/log` | **Not registered.** |
| `guardian://scans/{scanId}/report` | Registered. The report header names `github.com/owner/name` (or the GHES host) and the scanned SHA instead of a disk path. |
| Events `repo.commit.created`, `repo.branch.changed`, `repo.dependencies.changed`, `security.secret_detected`, `scan.completed`, `github.*` | Registered. |
| Events `repo.files.changed`, `checks.completed`, `checks.failed` | **Not registered** (`events/list` does not show them). |

### 3.2 Revisions

In remote mode a revision argument must pass `GitRevision.IsValid` **and** contain none of `~`, `^`, `@{`, `:`. `HEAD` alone maps to the default branch. Anything else → tool error "Remote mode accepts branch names, tag names and commit SHAs only." Refs are URL-escaped per `/`-segment as `GitHubClient.BranchExistsAsync` does today.

### 3.3 Tool descriptions and instructions

- `WithGuardianTools` replaces the description of `repo_status`, `recent_commits`, `diff_summary`, `scan_secrets` and `full_scan` with a remote-mode variant (kept next to the local text in the tool class as a `const string`) when the location is GitHub.
- `GuardianInstructions` first paragraph in remote mode: "Watching the GitHub repository owner/name through the GitHub API (remote mode). There is no working tree: run_checks and audit_dependencies are not available, diff_summary needs both ends, and scan_secrets working_tree scans the default branch head."
- Skill `guardian` gains a "Remote mode" paragraph: which events and tools are absent, and that the server instructions say which mode is active.
- Skills `bug-triage` and `dependency-hygiene` gain one short paragraph each: what to do when `run_checks` / `audit_dependencies` are absent (rely on `github.ci.failed` details and the CI run URL; say that the dependency audit needs a local checkout). `SkillContentTests` keep passing (skills may mention tools that exist in local mode).

## 4. Component design

### 4.1 `IRepositorySource` and the split of `IGitRepository`

```csharp
namespace CodebaseGuardian.Sources;

public enum RepositorySourceKind { Local, GitHub }

/// <summary>Repository data that both a local checkout and the GitHub API can provide.</summary>
public interface IRepositorySource
{
    RepositorySourceKind Kind { get; }

    /// <summary>Local: the root path. GitHub: "github.com/owner/name" (API host for GHES).</summary>
    string DisplayName { get; }

    Task<RepoStatus> GetStatusAsync(CancellationToken ct = default);
    Task<string?> GetCurrentBranchAsync(CancellationToken ct = default);   // GitHub: default branch
    Task<string?> GetHeadShaAsync(CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> GetBranchHeadsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CommitInfo>> GetRecentCommitsAsync(int limit, string? revision = null, CancellationToken ct = default);
    Task<IReadOnlyList<CommitInfo>> GetNewCommitsAsync(string tip, IReadOnlyCollection<string> excludeTips, int limit, CancellationToken ct = default);
    Task<DiffSummary> GetDiffSummaryAsync(string? from, string? to, int maxPatchBytes, CancellationToken ct = default);
    Task<DiffSummary> GetCommitDiffAsync(string sha, int maxPatchBytes, CancellationToken ct = default);

    /// <summary>Contents of one file in the snapshot (local: working tree; GitHub: default-branch head); null when absent.</summary>
    Task<byte[]?> ReadFileAsync(string path, CancellationToken ct = default);

    /// <summary>Every regular, non-symlink file of the snapshot, '/' separators, relative paths.</summary>
    IAsyncEnumerable<SnapshotFile> ReadSnapshotAsync(CancellationToken ct = default);
}

/// <summary>Content is null when the file was skipped (over the per-file cap); Skipped says why.</summary>
public sealed record SnapshotFile(string Path, ReadOnlyMemory<byte>? Content, string? Skipped);
```

`IGitRepository` becomes `IGitRepository : IRepositorySource` and keeps only the local-only members: `RootPath`, `GetStagedDiffAsync`, `ListFilesAsync`, `GetRemoteUrlAsync`. `GitRepository` implements `Kind = Local`, `DisplayName = RootPath`, `ReadFileAsync`, and `ReadSnapshotAsync` (moving today's `SecretScanner.ScanWorkingTreeAsync` file loop: `ListFilesAsync`, skip symlinks, 1 MB per-file cap).

A snapshot can end early. `ReadSnapshotAsync` signals that by yielding a final `SnapshotFile(Path: "", Content: null, Skipped: "<reason>")` sentinel; consumers treat an empty `Path` as "snapshot incomplete: reason".

Consumers after the split:

| Consumer | Depends on |
|---|---|
| `RepositoryTools`, `RepositoryResources`, `FullScanService`, `RepositoryWatcher`, `SecretScanCommitHandler` | `IRepositorySource` |
| `SecretScanner` | `IRepositorySource`, plus `IGitRepository?` (optional; only `ScanStagedAsync` uses it and throws "Remote mode has nothing staged." without it) |
| `CheckRunner`, `CheckCommandResolver`, `DependencyAuditor`, `WorkingTreeWatcher` | `IGitRepository` (local mode only) |
| `GitHubRepositoryResolver` | local: `IGitRepository` (origin remote); remote: a new `FixedGitHubRepositoryResolver` returns the `--repo` repository |

In remote mode `IGitRepository` is **not registered**, so any leftover dependency fails loudly at resolution instead of silently reading the wrong place.

### 4.2 Shared GitHub HTTP core and the read API

Today `GitHubClient` owns the HTTP plumbing (token, error mapping, pagination, rate-limit exceptions). Extract it into an internal `GitHubHttp` used by both clients:

- `SendAsync(method, path, body, ct, requireToken, allowNotFound)`; anonymous when `requireToken` is false and no token exists.
- Error mapping unchanged (`GitHubRateLimitException` with reset time, `GitHubApiException`, `GitHubUnavailableException`), plus `GitHubNotFoundException` for 404 on repository reads: "Repository owner/name was not found, or the token cannot read it (private repositories need contents:read)."
- `ListAsync<T>` pagination (`Link: rel="next"`, at most `MaxPages`, never off the API host) — unchanged behaviour.
- **Conditional GET:** `GetConditionalAsync(path)` keeps the last `ETag` and body per path in memory and sends `If-None-Match`; `304` returns the cached body.

`GitHubClient` keeps its public surface and behaviour; its existing tests stay green unmodified.

New read client:

```csharp
namespace CodebaseGuardian.GitHub;

public interface IGitHubRepositoryApi
{
    Task<GitHubRepositoryInfo> GetRepositoryAsync(CancellationToken ct);                 // default branch, private flag
    Task<IReadOnlyDictionary<string, string>> GetBranchHeadsAsync(CancellationToken ct);  // conditional GET, paginated
    Task<IReadOnlyList<GitHubCommit>> ListCommitsAsync(string? sha, int limit, CancellationToken ct);
    Task<GitHubCommitDetail> GetCommitAsync(string sha, CancellationToken ct);           // files + patches, paginated
    Task<GitHubComparison> CompareAsync(string @base, string head, CancellationToken ct); // status, aheadBy, behindBy, commits, files
    Task<byte[]?> GetFileAsync(string path, string @ref, CancellationToken ct);         // contents API, raw media type
    Task<Stream> OpenTarballAsync(string @ref, CancellationToken ct);                     // see 4.4
}
```

Empty repository: GitHub answers `409` for commits/compare. `ListCommitsAsync` returns an empty list; `GetBranchHeadsAsync` returns an empty map; `GetRepositoryAsync` still works.

### 4.3 `GitHubApiSource : IRepositorySource`

- `GetStatusAsync`: §3.1. Empty repository → `Branch` = default branch, `HeadSha` null.
- `GetRecentCommitsAsync`: maps API commits to `CommitInfo` — `CommittedAt` = `commit.committer.date`, `Subject` = first line of `commit.message`, `ShortSha` = first 7 hex digits, `ParentShas` from `parents[].sha`.
- `GetNewCommitsAsync(tip, excludeTips, limit)`: for each exclude tip (at most 5, in the order given), `CompareAsync(exclude, tip)`; the result is the **intersection** of the `commits` lists (by SHA), oldest first, newest `limit` kept. An exclude tip that answers 404 (garbage-collected after force-push) is dropped. With more than 5 exclude tips the result may be a superset; the watcher's announced-set dedupe absorbs that. No exclude tips → `ListCommitsAsync(tip, limit)` reversed.
- `GetDiffSummaryAsync(from, to)`: requires both (§3.1); `CompareAsync(from, to)`.
- `GetCommitDiffAsync(sha)`: `GetCommitAsync(sha)`.
- **Patch stitching** (shared by both diff methods): for each file, emit `diff --git a/{old} b/{new}`, `--- a/{old}` (or `/dev/null` when added), `+++ b/{new}` (or `/dev/null` when removed), then the API `patch` text, ending in `\n`. The result must parse with the existing `PatchParser`. Rules:
  - `patch` missing and `additions + deletions == 0` → `FileDiffStat(Binary: true, Insertions: null, Deletions: null)`.
  - `patch` missing with non-zero counts (diff too large for the API) → counts kept, `PatchTruncated = true`.
  - Compare returns at most 300 files: exactly 300 → `PatchTruncated = true`.
  - Patch longer than `maxPatchBytes` is cut as local mode does.
  - `From`/`To` in the returned `DiffSummary`: commit diff → parent SHA (or the empty-tree marker local mode uses for root commits) and the commit SHA; compare → `base_commit.sha` and the last compare commit's SHA, or the `to` argument when the comparison has no commits (`identical`/`behind`).
- `ReadFileAsync` / `ReadSnapshotAsync`: §4.4, always at the default-branch head SHA resolved at call start (one consistent SHA per snapshot).

### 4.4 Snapshot through the tarball

`OpenTarballAsync(ref)`:
1. `GET /repos/{o}/{r}/tarball/{ref}` with the token, **automatic redirects off**.
2. Expect `302`. The `Location` host must be `codeload.github.com` (github.com) or the API host (GHES); anything else → `GitHubApiException("Refusing tarball redirect to an unexpected host.")`.
3. Follow it with a request that carries **no `Authorization` header** (the redirect URL carries its own short-lived token).
4. Return the response stream; never buffer to disk.

`GitHubApiSource.ReadSnapshotAsync` reads it with `GZipStream` + `System.Formats.Tar.TarReader`:
- Strips the single top-level `owner-name-sha/` directory.
- Yields only regular files; skips symlinks, hard links, directories, device entries.
- Rejects entry names that are absolute or contain a `..` segment (skipped, logged as warning).
- Per-file cap 1 MB (`SecretScanner.MaxFileBytes`): larger files yield `Skipped`.
- Stops after `MaxSnapshotBytes` uncompressed bytes or `MaxSnapshotFiles` files, yielding the "incomplete" sentinel (§4.1). This also bounds decompression bombs.

### 4.5 Secret scanning

- `ScanWorkingTreeAsync` reads `.guardianignore` through `ReadFileAsync(".guardianignore")` and files through `ReadSnapshotAsync`; binary sniffing (NUL in the first 8 KB) unchanged.
- `SecretScanResult` gains two fields (additive schema change, both modes): `complete` (bool) and `warnings` (string[]). `complete` is false when the patch was truncated or the snapshot ended early; `warnings` says what was not scanned. Today's log warning for truncated patches stays.

### 4.6 Watcher

`RepositoryWatcher` depends on `IRepositorySource` and keeps all of today's logic (baseline, changed-branch detection, announced-set dedupe, keep-old-head-on-failure, handlers). Changes:
- Starts `WorkingTreeWatcher` only when `Kind == Local`.
- Poll interval: `WatchIntervalMs` (local) or the effective `Remote:PollIntervalSeconds` (remote).
- For each changed branch the exclude tips are ordered: that branch's previous tip first, then the default branch's current head, then the rest — so the GitHub source's 5-compare cap uses the most useful bases.
- Remote `repo.branch.changed` fires when the default branch changes (renamed or switched in settings); `from`/`to`/`headSha` as today.
- `GitHubRateLimitException` from a poll → log once at warning with the reset time, skip polls until the reset (via `TimeProvider`), then resume. Other transient errors (`HttpRequestException`, 5xx) → log and retry at the next interval. The watcher never stops on API errors.
- `repo.dependencies.changed` from commits works unchanged (it uses the commit diff's paths).

### 4.7 Composition root

`AddCodebaseGuardian` parses `RepositoryLocation` from configuration at registration time (as it already does for `AutoChecks`) and branches:

| Registration | Local | Remote |
|---|---|---|
| `IGitRepository`, `IRepositorySource` | `GitRepository` (one instance, both interfaces) | `IRepositorySource` → `GitHubApiSource`; no `IGitRepository` |
| `IGitHubRepositoryApi` | registered (unused unless remote) | registered |
| `IGitHubRepositoryResolver` | `GitHubRepositoryResolver` | `FixedGitHubRepositoryResolver` |
| Checks services, `IDependencyAuditor`, `CheckTools`, `DependencyTools`, `CheckResources` | yes | no |
| Events `checks.*`, `repo.files.changed` | yes | no |
| `RemoteOptions` bound + validated | yes (unused) | yes |

## 5. Security

- Owner and repository segments validated (`GitHubRepositoryRef.IsValidSegment`); refs validated (§3.2) and escaped per segment; no model-supplied string is concatenated into a URL unescaped.
- The token goes only to the configured API host; tarball redirects are host-allowlisted and followed without `Authorization`.
- Pagination never leaves the API host (unchanged).
- Snapshot: no disk writes, path traversal entries rejected, symlinks skipped, byte and file caps.
- Secret values stay redacted everywhere, including findings from remote snapshots and patches.
- Error messages quoting API responses keep the existing 500-character cap and token scrubbing.

## 6. Testing

- No network: `FakeGitHubApi` gains handlers for `repos/{o}/{r}`, `branches` (with `ETag`/`304`), `commits`, `commits/{sha}`, `compare/{a}...{b}`, `contents/{path}`, `tarball/{ref}` (302) and a fake codeload host. Tests build tarballs in memory with `TarWriter` + `GZipStream`.
- Unit tests per component (location parsing, validation, `GitHubHttp` conditional GET, read API mapping, patch stitching through `PatchParser`, tarball reader caps and traversal, `GetNewCommitsAsync` intersection).
- Remote-mode protocol tests through the in-process MCP server: `tools/list` excludes `run_checks`/`audit_dependencies`; `events/list` excludes `checks.*`/`repo.files.changed`; each remaining tool returns the documented remote shape; watcher announces a commit after the fake API moves a branch.
- Local mode: every existing test stays green; the refactor (§4.1) is behaviour-preserving.

### Review focus (each gets a pinning test)

1. **Empty GitHub repository** (`409`): `repo_status`, `recent_commits`, watcher work; the first pushed commit is announced.
2. **Force-push on a remote branch** (old tip 404 or `diverged`): watcher keeps running, announces the rewritten commits once.
3. **Tarball redirect**: no `Authorization` header reaches codeload; a redirect to any other host is refused.
4. **Rate limit hit mid-poll**: watcher backs off until reset and resumes; a tool call returns an error naming the reset time.
5. **Snapshot caps / traversal**: an over-size or `../` tarball yields `complete: false` with a warning, never an exception, never a file outside the snapshot.
6. **Local mode untouched**: a local server's `tools/list`, `events/list` and tool outputs (apart from the two additive `scan_secrets` fields) are unchanged.

## 7. Non-goals

- Cloning, shallow fetch, or any disk cache of repository contents.
- More than one repository per server; switching repositories at runtime.
- Running checks or dependency audits remotely; substituting GitHub Actions or Dependabot data for them.
- Non-GitHub hosts (GitLab, Bitbucket).
- GitHub webhooks as an event source (the watcher polls).
- Working-tree concepts in remote mode (`staged`, untracked files, `repo.files.changed`).
