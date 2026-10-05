# Backlog

The implementation plan is [BACKLOG.md](BACKLOG.md). It implements the design spec [docs/specs/2026-10-04-codebase-guardian-design.md](../specs/2026-10-04-codebase-guardian-design.md); where they disagree, the spec wins.

v1 is feature complete (tasks 1 to 23); task-level detail stays in [STATUS.md](STATUS.md).

Tasks run strictly in order. Each one ends with `dotnet test` green and its own commit.

- [STATUS.md](STATUS.md): which tasks are complete, plus every ruling and deferred finding. This is the durable record.
- [CONTROLLER.md](CONTROLLER.md): the procedure that runs the tasks. It covers the subagent loop, the `sdd-lock` branch that allows one controller at a time, and push rules. The interactive session and the scheduled cloud routine ("Codebase Guardian backlog", every 2 hours) both follow it.

| Epic | Tasks |
|---|---|
| 1. Core Guardian (local) | 1–15: hosting, git, repo tools, Skills, Events (poll/stream), watcher, checks, secrets, dependencies, skill content, stateless HTTP, demo client |
| 2. GitHub + webhooks | 16–21: GitHub client, confirmed action tools, GitHub events, HTTP auth, webhook subscriptions, webhook dispatcher |
| 3. Tasks | 22–23: Tasks extension, `full_scan` |

Reference material the tasks cite:

- [skills-events-wire-format.md](../reference/skills-events-wire-format.md): exact JSON shapes for Skills (A-sections) and Events (B-sections).
- [sdk-2.2-notes.md](../reference/sdk-2.2-notes.md): verified MCP C# SDK 2.2.0 behaviour (SDK §-sections).
