# Codebase Guardian — Design Spec

- **Date:** 2026-10-04
- **Status:** Approved direction (user idea "Proactive Codebase Guardian"); this spec is the binding authority for the backlog in `docs/backlog/BACKLOG.md`.
- **Base protocol:** MCP 2026-07-28 (stateless core). Older revisions keep working through the SDK's negotiation.
- **SDK:** MCP C# SDK 2.2.0 (`ModelContextProtocol`, `ModelContextProtocol.AspNetCore`, `ModelContextProtocol.Extensions.Tasks`), .NET 10.

## 1. Purpose

One MCP server that turns an AI agent into a reactive and proactive software engineering assistant for a Git repository:

1. The agent (or user) points the Guardian at a repo.
2. The client subscribes to interesting events (poll, push stream, or webhook).
3. When something happens (new commit, failing checks, leaked secret, dependency change, GitHub issue, CI failure), the server emits an event whose payload names a **suggested skill**.
4. The agent loads that skill (`skills/get` + `resources/read`), reads its supporting files, and uses the Guardian's tools to act (triage, review, audit, comment, open issue/PR).

## 2. Feature map

| MCP feature | How Guardian uses it |
|---|---|
| Tools | Inspect repo, summarize diffs, run checks, scan secrets, audit dependencies, poll events (fallback), GitHub actions |
| Resources | `guardian://` live repo status, recent commits, check logs, scan reports; `skill://` skill files |
| Skills extension `io.modelcontextprotocol/skills` (SEP-2640, Final) | `skills/list`, `skills/get`, `resources/directory/read`; skills `guardian`, `security-audit`, `dependency-hygiene`, `bug-triage`, `pr-review` |
| Events (Triggers & Events WG **draft** design sketch) | `events/list`, `events/poll`, `events/stream`, `events/subscribe`, `events/unsubscribe` |
| Tasks extension `io.modelcontextprotocol/tasks` | Long-running `run_checks`, `audit_dependencies`, `full_scan` |
| Stateless 2026-07-28 core | stdio + stateless Streamable HTTP; no session affinity required for request/response methods |
| Elicitation (2026-07-28 multi round-trip) | Confirmation before outward-facing GitHub actions, with explicit-argument fallback |

## 3. Decomposition (epics)

| Epic | Delivers | Depends on |
|---|---|---|
| **1. Core Guardian (local)** | Hosting, git layer, repo tools/resources, Skills extension + skill content, Events poll/stream + repo watcher, checks, secret scan, dependency audit, stateless HTTP, demo client | — |
| **2. GitHub + webhooks** | GitHub client, outward actions with confirmation, GitHub event poller, HTTP auth, `events/subscribe` webhook delivery (Standard Webhooks signing, endpoint verification, SSRF guard) | Epic 1 |
| **3. Tasks** | Tasks extension, task-capable long-running tools, `full_scan` with report resource | Epic 1 |

Each epic leaves working, tested software.

## 4. Solution layout

```
CodebaseGuardian.slnx
global.json, Directory.Build.props, Directory.Packages.props
src/
  Mcp.Skills/          generic Skills extension server library (no Guardian knowledge)
  Mcp.Events/          generic Events extension server library (no Guardian knowledge)
  CodebaseGuardian/    the server app (exe, Microsoft.NET.Sdk.Web)
    Hosting/           options, composition root, transports, instructions
    Git/               process runner + git CLI wrapper
    Watching/          repository watcher -> events
    Checks/            build/test runner
    Security/          secret scanner
    Dependencies/      dependency auditor
    GitHub/            (Epic 2) REST client, poller, action tools
    Tools/             MCP tool classes (thin; delegate to services)
    Resources/         MCP resources (guardian://)
    skills/            skill content directories (copied to output)
samples/
  GuardianWatch/       demo console client (events stream -> suggested skill)
tests/
  CodebaseGuardian.Tests/   xunit.v3 (folders: Skills/, Events/, App/, Infrastructure/)
docs/specs, docs/backlog
```

Boundary rules:
- `Mcp.Skills` and `Mcp.Events` reference only the MCP SDK (+ YamlDotNet for Skills). They never reference `CodebaseGuardian`.
- The app depends on both libraries and wires them through builder extensions (`WithSkills`, `WithEvents`).
- Tool classes stay thin: argument validation + call into a service + shape the result.

## 5. Protocol surface

### 5.1 Capabilities advertised

- `capabilities.tools`, `capabilities.resources` (with `subscribe`/`listChanged` as the SDK provides).
- `capabilities.extensions["io.modelcontextprotocol/skills"] = {}`.
- `capabilities.extensions["io.modelcontextprotocol/tasks"]` (set by the Tasks extension package, Epic 3).
- Events: `capabilities.experimental["events"] = { "listChanged": false }` (see ruling R1 in 5.7).

### 5.7 Protocol rulings (gaps and conflicts in the specs)

Exact wire shapes live in `docs/reference/skills-events-wire-format.md`; SDK behaviour in `docs/reference/sdk-2.2-notes.md`. Where those documents list an ambiguity, these rulings decide it.

| # | Ruling | Why | Cost if wrong |
|---|---|---|---|
| R1 | Events capability is declared as `capabilities.experimental["events"] = {"listChanged": false}`; one constant (`EventsProtocol.CapabilityKey`) controls it. | The sketch wants top-level `capabilities.events`, which SDK 2.2.0 cannot emit (no extension-data bag); the sketch defines no extension id, and claiming `io.modelcontextprotocol/events` would squat an official name. | One-line change when the WG settles the id. |
| R2 | Events error codes follow the sketch: `-32011 NotFound`, `-32012 Forbidden`, `-32013 ResourceExhausted`, `-32014 Unsupported`, `-32015 CallbackEndpointError`; `-32602` for invalid params. | Interop with sketch-based clients; the base 2026-07-28 range policy conflict is documented, not solved by us. | Renumber constants in one file. |
| R3 | Every custom result carries `"resultType": "complete"`. `skills/list`, `skills/get` and `events/list` carry `ttlMs` + `cacheScope` (`300000`/`"public"` for skills, `3600000`/`"public"` for events). | Base 2026-07-28 requires `resultType`; the SDK does not add it to custom handlers. | — |
| R4 | The event-type set is static for the process lifetime: `listChanged: false`, no `notifications/events/list_changed`. | No `subscriptions/listen` filter key exists for it. | Add when the WG defines a key. |
| R5 | Every event type supports `poll` and `push`; `webhook` is added to `delivery` only when webhook delivery is enabled (Epic 2, authenticated HTTP). Replay is supported for all types (ring-buffer cursor). | Uniform client experience; ring buffer gives cheap replay. | — |
| R6 | `events/stream` sends `notifications/events/heartbeat` every 15 s (configurable), never sends a final `StreamEventsResult` on cancellation, and sends `{"resultType":"complete","_meta":{}}` only when the server ends the stream itself (shutdown). | Sketch: heartbeat ≤ 30 s; servers SHOULD NOT answer cancelled requests. | — |
| R7 | Skills: `directoryRead: true`; no pagination for `skills/list` (catalog is small; `cursor` accepted and ignored, never a `nextCursor`); every `SKILL.md` is also listed in `resources/list` with `name`/`description`/`mimeType: text/markdown`; supporting files are readable but not listed; text vs blob by exact UTF-8 round-trip. | Conformance suite checks `SKILL.md` metadata via `resources/list`; directory read is cheap. | — |
| R8 | Skills are never gated on the client declaring the extension. | Spec defines no client declaration; conformance harness never declares it. | — |
| R9 | Webhook subscriptions are always finite: `ttlMs: null` or anything above 24 h is granted as 24 h; minimum grant 60 s. | No durable store ⇒ no-expiry grants are not allowed (sketch SDK guidance). | — |
| R10 | Webhook endpoint verification uses the challenge handshake (sketch option a), synchronously inside `events/subscribe`; receivers route the handshake by URL. | Only option that needs no out-of-band setup. | — |
| R11 | Subscription id = `sub_` + first 16 hex chars of SHA-256 over the UTF-8 of canonical JSON `[principal, url, name, arguments]` (object keys sorted ordinally, no insignificant whitespace). | Deterministic across restarts; sketch leaves the algorithm open. | — |
| R12 | Event ids never contain `.`: local events `evt_<epoch8>_<seq>`, GitHub events `gh_<kind>_<githubId>`. | Standard Webhooks forbids `.` in `webhook-id`. | — |

### 5.2 Server instructions

The server `instructions` string (built at startup) contains:
1. One paragraph: what the Guardian watches (repo path, branch).
2. The skills catalog pointer: each skill's `skill://…/SKILL.md` URI and one-line description (Skills spec "instructions pointer" convention), and the advice "load the `guardian` skill first".
3. Event names and the fallback: "If your client does not support `events/*`, call the `poll_events` tool."

### 5.3 Tools (snake_case names are part of the contract; skills reference them)

| Tool | Epic | Annotations | Purpose |
|---|---|---|---|
| `repo_status` | 1 | read-only | Branch, HEAD, upstream ahead/behind, staged/unstaged/untracked files |
| `recent_commits` | 1 | read-only | Last N commits (default 10, max 100), optional branch |
| `diff_summary` | 1 | read-only | Per-file stats for a range (`from`, `to`; default working tree vs HEAD) plus truncated patch |
| `run_checks` | 1 (task-capable in 3) | non-destructive | Run configured build/test command, store log, emit events |
| `scan_secrets` | 1 | read-only | Scan working tree, staged changes, or a commit range for secrets (redacted output) |
| `audit_dependencies` | 1 (task-capable in 3) | read-only | Outdated + vulnerable packages (NuGet via `dotnet list package`, npm via `npm audit`/`npm outdated`) |
| `poll_events` | 1 | read-only | Fallback for clients without Events: same semantics as `events/poll` across all or selected event names |
| `create_issue` | 2 | open-world, confirm | Create GitHub issue |
| `comment_on_pr` | 2 | open-world, confirm | Comment on PR/issue |
| `open_pull_request` | 2 | open-world, confirm | Open PR from a pushed branch |
| `full_scan` | 3 | task-required | Secrets + dependencies + checks -> report |

### 5.4 Resources

| URI | Type | Content |
|---|---|---|
| `guardian://repo/status` | `application/json` | Same model as `repo_status` |
| `guardian://repo/commits/recent` | `application/json` | Last 20 commits |
| `guardian://checks/latest` | `application/json` | Latest check run summary (404-style MCP error if none) |
| `guardian://checks/{runId}/log` | `text/plain` (template) | Full captured log of a run |
| `guardian://scans/{scanId}/report` | `text/markdown` (template, Epic 3) | `full_scan` report |
| `skill://<skill>/<file>` | per file | Served by `Mcp.Skills` |

### 5.5 Event catalog

All payloads are JSON objects. Every payload that has a natural follow-up carries `suggestedSkill` (a `skill://…/SKILL.md` URI) — the event→skill cross-link.

| Event name | Epic | Trigger | Arguments (inputSchema) | Payload highlights | suggestedSkill |
|---|---|---|---|---|---|
| `repo.commit.created` | 1 | New commit reachable from a local branch head that the watcher had not seen | `branch?` | `sha, shortSha, branch, author{name,email}, committedAt, subject, filesChanged, insertions, deletions, files[≤100]` | `skill://pr-review/SKILL.md` |
| `repo.branch.changed` | 1 | HEAD now points at a different branch | — | `from, to, headSha` | — |
| `repo.files.changed` | 1 | Working-tree edits (debounced 2s; ignores `.git`, `bin`, `obj`, `node_modules`) | `pathPrefix?` | `paths[≤200], count` | — |
| `repo.dependencies.changed` | 1 | A dependency manifest changed (working tree or commit) | — | `manifests[], ecosystems[], commitSha?` | `skill://dependency-hygiene/SKILL.md` |
| `checks.completed` | 1 | Any check run finished | — | `runId, command, exitCode, passed, durationMs, summary, failedTests[], logUri, trigger, commitSha?` | — |
| `checks.failed` | 1 | A check run failed (also emits `checks.completed`) | — | same as above | `skill://bug-triage/SKILL.md` |
| `security.secret_detected` | 1 | Secret scanner finding in a new commit or explicit scan | — | `source, commitSha?, findings[{ruleId, path, line, redacted}]` | `skill://security-audit/SKILL.md` |
| `github.issue.opened` | 2 | New issue on the origin repo | `label?` | `number, title, author, url, labels[], body(≤2000)` | `skill://bug-triage/SKILL.md` |
| `github.pr.comment.created` | 2 | New PR review/issue comment | `prNumber?` | `prNumber, commentId, author, body(≤2000), url, path?, line?` | `skill://pr-review/SKILL.md` |
| `github.ci.failed` | 2 | Workflow run concluded `failure` | `branch?` | `runId, workflowName, branch, headSha, url, conclusion` | `skill://bug-triage/SKILL.md` |
| `scan.completed` | 3 | `full_scan` finished | — | `scanId, reportUri, secretFindings, vulnerablePackages, checksPassed` | security-audit if secrets > 0, else dependency-hygiene if vulnerable > 0, else omitted |

### 5.6 Skills catalog

| Skill | Use when | Key files |
|---|---|---|
| `guardian` | First load; how to watch the repo, event→skill map, fallbacks | `SKILL.md`, `references/events.md` |
| `security-audit` | `security.secret_detected`, `scan.completed` with secrets | `SKILL.md`, `references/secret-types.md`, `references/remediation.md` |
| `dependency-hygiene` | `repo.dependencies.changed`, vulnerable/outdated packages | `SKILL.md`, `references/upgrade-policy.md` |
| `bug-triage` | `checks.failed`, `github.ci.failed`, `github.issue.opened` | `SKILL.md`, `references/triage-checklist.md`, `assets/issue-template.md` |
| `pr-review` | `repo.commit.created`, `github.pr.comment.created` | `SKILL.md`, `references/review-checklist.md`, `assets/review-comment-template.md` |

Skills only reference tools, resources, and events that exist (enforced by a test).

## 6. Component design

### 6.1 Mcp.Skills (generic)

- **Loading:** `DirectorySkillSource(rootDirectory)` discovers every `SKILL.md` below the root; the directory path relative to root is the `<skill-path>`. Every regular file in that directory (recursively, excluding nested skill directories) is a skill file.
- **Validation (load-time, fail fast with a clear message):** YAML frontmatter present; `name` matches Agent Skills rules and equals the last path segment; `description` present and within limits; per-file and per-skill size limits from the spec.
- **Integrity:** for each file, `size` = raw byte length, `digest` = `sha256:` + 64 lowercase hex over the raw bytes. Content is loaded once into memory; reads serve exactly the hashed bytes.
- **Catalog:** immutable snapshot; `Reload()` builds a new snapshot (used by tests; hot reload is out of scope).
- **Protocol:** `skills/list`, `skills/get`, `resources/directory/read`, and `resources/read` for skill URIs; capability entry; list-caching attributes where the base spec supports them.
- **Builder API:** `IMcpServerBuilder WithSkills(this IMcpServerBuilder builder, Action<SkillsOptions> configure)`; `SkillsOptions.Directories` (one or more roots), `SkillsOptions.UriScheme` (default `skill`).

### 6.2 Mcp.Events (generic)

- **Model:** `EventDefinition` (name, description, delivery modes, input schema, payload schema, argument matcher), `EventEnvelope` (eventId, name, timestamp, data, cursor).
- **Event log:** in-memory, append-only, bounded (default 10 000 events, 24 h). Global monotonic sequence. Cursor is opaque base64url of `v1:<instanceEpoch>:<sequence>`; a cursor from another epoch or older than retention yields `truncated: true` and replay from the oldest retained event within `maxAgeMs`. Event ids `evt_<epoch8>_<sequence>` (stable for dedupe).
- **Publisher:** `IEventPublisher.PublishAsync(string name, JsonObject data, CancellationToken ct)` validates the name is registered; app services depend only on this interface.
- **Protocol:** `events/list`, `events/poll`, `events/stream` (Epic 1); `events/subscribe`, `events/unsubscribe`, webhook dispatcher (Epic 2).
- **Builder API:** `IMcpServerBuilder WithEvents(this IMcpServerBuilder builder, Action<EventsOptions> configure)`; definitions registered via `EventsOptions.Define(...)`.
- **State note:** the protocol is stateless; this event log is per-process. Multi-instance deployments need a shared store behind `IEventLog` — interface exists, only the in-memory implementation ships.

### 6.3 CodebaseGuardian app

- **Configuration:** `GuardianOptions` bound from section `Guardian` (appsettings, env `GUARDIAN__*`, CLI). CLI switches: `--repo`, `--transport stdio|http`, `--urls`, `--auto-checks`. Defaults: repo = current directory, transport = stdio, HTTP url `http://127.0.0.1:5199`, auto-checks off.
- **Process execution:** `IProcessRunner` runs executables with `ArgumentList` (never a shell), working directory, timeout (default 120 s, checks 15 min), output capture with size cap (1 MB per stream, truncation marked). All git/dotnet/npm calls go through it, which makes services testable with a fake runner.
- **Git:** `IGitRepository` over the git CLI (`status --porcelain=v2 --branch`, `log` with `%x1f`/`%x1e` separators, `diff --numstat`, `rev-parse`, `for-each-ref`, `remote get-url`). Fails with a clear `GitException` when the path is not a repository.
- **Watcher:** `RepositoryWatcher` (hosted service) polls refs every `WatchIntervalMs` (default 2000) to detect new commits/branch switch; `FileSystemWatcher` for working-tree edits (debounced). Publishes repo.* events; runs secret scan on each new commit; triggers checks when `AutoChecks` is on.
- **Checks:** `CheckRunner` resolves the command (configured, else auto-detect: `*.slnx`/`*.sln`/`*.csproj` → `dotnet test`; `package.json` with `test` script → `npm test`), runs it, stores `CheckRun` records in memory (last 50), emits events, exposes logs as resources. One run at a time (second request waits).
- **Secret scanner:** regex rule set (AWS access key, GitHub tokens, Slack tokens, private key blocks, generic `api_key=`-style assignments with high-entropy values, JWTs); allowlist file `.guardianignore`-style patterns; output redacted (`ghp_****abcd`); never logs or returns full secret values.
- **Dependency auditor:** NuGet: `dotnet list <target> package --vulnerable --include-transitive --format json` and `--outdated --format json`; npm: `npm audit --json`, `npm outdated --json`. Missing toolchain → reported as "skipped" with reason, never an exception.
- **Transports:** stdio (default). HTTP: ASP.NET Core, `MapMcp("/mcp")`, stateless 2026-07-28 mode, bound to loopback by default.

## 7. Security

- No shell execution; arguments always passed as lists; repo-relative paths validated (no `..` escape) before use.
- Resource reads resolve only catalog entries or known run/scan ids — no arbitrary file reads.
- Secret values are redacted everywhere (tool results, events, logs, reports).
- HTTP transport binds `127.0.0.1` by default. Epic 2 adds optional bearer-token auth; webhook subscriptions over HTTP require it.
- Webhooks (Epic 2): `https` only (loopback `http` allowed only with an explicit development flag), no redirects, private/link-local/loopback IP blocking at delivery time (except with the development flag), callback endpoint verification before activation, Standard Webhooks HMAC-SHA256 signatures, timestamp + id for replay protection.
- GitHub token read from `GITHUB_TOKEN` env (fallback `gh auth token`); never echoed. Outward actions require confirmation (elicitation; fallback `confirm: true` argument).

## 8. Testing strategy

- xunit.v3 on Microsoft.Testing.Platform; `dotnet test` at repo root runs everything; no network access (GitHub and webhook receivers are faked with in-process handlers).
- **Protocol tests** run a real in-process client/server pair and send real JSON-RPC (`skills/list`, `events/poll`, `tools/call`, …).
- **Git tests** create throwaway repos in temp directories with the `git` CLI (`TempGitRepo` fixture: init, config user, commit files).
- **HTTP tests** use `WebApplicationFactory`/TestServer against the stateless endpoint.
- **Content tests**: every bundled skill loads and validates; every tool/resource/event a skill mentions exists.

## 9. Non-goals (v1)

- Multi-instance shared state (event log, task store, webhook subscriptions are in-memory, single process).
- Languages beyond NuGet and npm for dependency audit.
- Auto-fixing code or pushing commits (the agent does that with its own tools; Guardian only opens PRs from branches that already exist on the remote).
- Hot reload of skills.
