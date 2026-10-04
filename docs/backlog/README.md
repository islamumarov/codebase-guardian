# Backlog

The implementation plan is [BACKLOG.md](BACKLOG.md). It implements the design spec [docs/specs/2026-10-04-codebase-guardian-design.md](../specs/2026-10-04-codebase-guardian-design.md); where they disagree, the spec wins.

Tasks run strictly in order. Each one ends with `dotnet test` green and its own commit.

| Epic | Tasks | Status |
|---|---|---|
| 1. Core Guardian (local) | 1–15: hosting, git, repo tools, Skills, Events (poll/stream), watcher, checks, secrets, dependencies, skill content, stateless HTTP, demo client | Not started |
| 2. GitHub + webhooks | 16–21: GitHub client, confirmed action tools, GitHub events, HTTP auth, webhook subscriptions, webhook dispatcher | Not started |
| 3. Tasks | 22–23: Tasks extension, `full_scan` | Not started |

Reference material the tasks cite:

- [skills-events-wire-format.md](../reference/skills-events-wire-format.md): exact JSON shapes for Skills (A-sections) and Events (B-sections).
- [sdk-2.2-notes.md](../reference/sdk-2.2-notes.md): verified MCP C# SDK 2.2.0 behaviour (SDK §-sections).
