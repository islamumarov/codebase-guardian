# Codebase Guardian

An MCP server that turns an AI agent into a reactive and proactive software engineering assistant for a Git repository. It
watches a repository, publishes events (new commit, changed dependencies, failing checks, detected secret), and every event
that has a natural follow-up names a **suggested skill**: a packaged workflow the agent loads (`skills/get` +
`resources/read`, verified against a SHA-256 digest) and carries out with the Guardian's tools.

Built on the MCP C# SDK 2.2.0 and .NET 10.

| MCP feature | How the Guardian uses it |
|---|---|
| Tools | Inspect the repository, summarize diffs, run checks, scan for secrets, audit dependencies, `poll_events` fallback |
| Resources | `guardian://` live repo status, recent commits, check runs and logs; `skill://` skill files |
| Skills (`io.modelcontextprotocol/skills`) | `skills/list`, `skills/get`, `resources/directory/read`; five bundled skills |
| Events (Triggers & Events, **draft**) | `events/list`, `events/poll`, `events/stream` |
| Stateless 2026-07-28 core | stdio and stateless Streamable HTTP |

Status: Epic 1 (local Guardian) is complete. The design is in
[docs/specs/2026-10-04-codebase-guardian-design.md](docs/specs/2026-10-04-codebase-guardian-design.md); the GitHub
integration, webhooks and the Tasks extension (Epics 2 and 3) are planned and **not implemented**.

## Quick start

Requires the .NET SDK 10.0 and `git` on the PATH.

```bash
dotnet build
dotnet run --project src/CodebaseGuardian -- --repo .
```

The server speaks MCP on stdio (stdout is reserved for the protocol; logs go to stderr). Options:

| Option | Meaning |
|---|---|
| `--repo <path>` | Repository to watch (default: current directory) |
| `--transport stdio\|http` | Transport (default `stdio`) |
| `--urls <url>` | HTTP listen address (default `http://127.0.0.1:5199`) |
| `--auto-checks` | Run the configured checks after each new commit |
| `--no-watch` | Disable the repository watcher (no pushed events) |
| `--skills-dir <path>` | Skill directories (default: `skills` next to the binary) |

Every option is also a `Guardian:*` setting (appsettings, `GUARDIAN__*` environment variables).

## Configuring clients

**Claude Code**

```bash
claude mcp add codebase-guardian -- dotnet run --project <path>/src/CodebaseGuardian -- --repo <repo>
```

**VS Code** (`.vscode/mcp.json`)

```json
{
  "servers": {
    "codebase-guardian": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "<path>/src/CodebaseGuardian", "--", "--repo", "${workspaceFolder}"]
    }
  }
}
```

**HTTP mode** (stateless Streamable HTTP, endpoint `/mcp`)

```bash
dotnet run --project src/CodebaseGuardian -- --transport http --repo . --urls http://127.0.0.1:5199
```

```json
{ "servers": { "codebase-guardian": { "type": "http", "url": "http://127.0.0.1:5199/mcp" } } }
```

## What it offers

Full contract: spec [section 5](docs/specs/2026-10-04-codebase-guardian-design.md).

**Tools**

| Tool | Purpose |
|---|---|
| `repo_status` | Branch, HEAD, ahead/behind, staged/unstaged/untracked files |
| `recent_commits` | Last N commits (default 10, max 100), optional branch |
| `diff_summary` | Per-file stats for a range plus a truncated patch |
| `run_checks` | Run the build/test command, store the log, emit events |
| `scan_secrets` | Scan working tree, staged changes or a commit range; output is redacted |
| `audit_dependencies` | Outdated and vulnerable packages (NuGet and npm) |
| `poll_events` | Fallback for clients without `events/*` |

**Resources**: `guardian://repo/status`, `guardian://repo/commits/recent`, `guardian://checks/latest`,
`guardian://checks/{runId}/log`, and the skill files under `skill://<skill>/<file>`.

**Events**

| Event | Trigger | Suggested skill |
|---|---|---|
| `repo.commit.created` | New commit on a local branch | `pr-review` |
| `repo.branch.changed` | HEAD moved to another branch | none |
| `repo.files.changed` | Working-tree edits (debounced) | none |
| `repo.dependencies.changed` | A dependency manifest changed | `dependency-hygiene` |
| `checks.completed` | A check run finished | none |
| `checks.failed` | A check run failed | `bug-triage` |
| `security.secret_detected` | Secret found in a new commit or scan | `security-audit` |

**Skills**: `guardian` (load first; event-to-skill map), `security-audit`, `dependency-hygiene`, `bug-triage`, `pr-review`.

## Demo client

`samples/GuardianWatch` is a console MCP client that runs the whole loop: it starts the server over stdio, prints the
server instructions, the skill names and the event names, opens one `events/stream` per event (`repo.commit.created`,
`repo.dependencies.changed`, `checks.failed`, `security.secret_detected`, request ids `watch-<name>`), and for every event
with a `suggestedSkill` calls `skills/get`, reads `SKILL.md` with `resources/read`, verifies its SHA-256 digest and size, and
prints `skill <name> verified (<size> bytes)` with the first lines.

```bash
dotnet run --project samples/GuardianWatch -- --repo /path/to/repo
# optional: --server <path to CodebaseGuardian.dll or to the project directory>
```

Then commit something in that repository, for example a fake secret built at runtime (no secret-shaped literal in any file):

```bash
printf 'AWS_ACCESS_KEY_ID=%s%s\n' AKIA IOSFODNN7EXAMPLE > .env.prod && git add -f .env.prod && git commit -m "add env"
```

Ctrl+C sends `notifications/cancelled` for each stream and exits 0. (With SDK 2.2.0 over stdio, cancelling the token of a
request does not reach the server, so the client sends the notification itself; see
[docs/reference/sdk-2.2-notes.md](docs/reference/sdk-2.2-notes.md).)

## Security notes

Spec [section 7](docs/specs/2026-10-04-codebase-guardian-design.md).

- No shell execution: every external process (`git`, `dotnet`, `npm`) runs with an argument list; model-supplied revisions are validated.
- Repo-relative paths are validated against `..` escapes; resource reads resolve only catalog entries and known run ids.
- Secret values are redacted everywhere: tool results, events, logs.
- **HTTP is loopback-only** by default (binds `127.0.0.1`). Binding another address needs `Guardian:HttpAllowRemote=true`, and
  the HTTP transport has **no authentication yet**: anyone who can reach the port can read the repository and run its checks.
- **DNS-rebinding guard**: a request whose `Host` or `Origin` is not loopback is answered with 403 (remote mode drops only the
  `Host` restriction; a non-loopback `Origin` is still refused).

## Limitations

- Events are a **draft** (Triggers & Events working group sketch); the wire format may change.
- State (event log, check runs, watcher cursors) lives in a single process and is lost on restart.
- Dependency auditing covers NuGet and npm only.
- No GitHub integration, webhook delivery or Tasks yet (Epics 2 and 3).
- Conformance: the three Skills scenarios of the MCP conformance suite pass; the base 2026-07-28 suite passes everything that does
  not need the suite's own fixtures. Details: [docs/reference/conformance.md](docs/reference/conformance.md).

## Architecture

```mermaid
flowchart LR
    Client[MCP client / GuardianWatch] <-->|stdio or Streamable HTTP| Host[Hosting: StdioHost, HttpHost]
    Host --> Tools[Tools]
    Host --> Resources[Resources guardian://]
    Host --> Skills[Mcp.Skills: skills/list, skills/get]
    Host --> Events[Mcp.Events: events/list, poll, stream]
    Tools --> Git[Git: process runner and git wrapper]
    Tools --> Checks[Checks: build/test runner]
    Tools --> Security[Security: secret scanner]
    Tools --> Deps[Dependencies: auditor]
    Resources --> Git
    Resources --> Checks
    Watcher[Watching: repository watcher] --> Git
    Watcher --> Security
    Watcher -->|publish| Events
    Checks -->|publish| Events
    Skills --> SkillFiles[(skills/ directories)]
```

`Mcp.Skills` and `Mcp.Events` are generic libraries that never reference the application.

## Development

```bash
dotnet test        # xunit.v3; no network needed, git tests use real temporary repositories
```

```
src/Mcp.Skills        Skills extension library
src/Mcp.Events        Events extension library
src/CodebaseGuardian  the server (Hosting, Git, Watching, Checks, Security, Dependencies, Tools, Resources, skills)
samples/GuardianWatch demo client
tests/                CodebaseGuardian.Tests (includes the end-to-end scenario test)
docs/                 spec, backlog, reference notes
```
