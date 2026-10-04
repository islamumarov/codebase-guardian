# Codebase Guardian

An MCP server that turns an AI agent into a reactive and proactive software engineering assistant for a Git repository.

- **Tools**: inspect the repo, run checks, scan for secrets, audit dependencies.
- **Resources**: live repo status, recent commits, check logs, skill files.
- **Skills** (`io.modelcontextprotocol/skills`): packaged workflows such as `security-audit`, `dependency-hygiene`, `bug-triage`, `pr-review`.
- **Events** (draft Triggers & Events extension): `events/list`, `events/poll`, `events/stream`, webhook subscriptions.
- **Tasks** (`io.modelcontextprotocol/tasks`): long-running repo scans and dependency audits.
- **Stateless 2026-07-28 core**: stdio, plus stateless Streamable HTTP.

Built on the MCP C# SDK 2.2.0 and .NET 10.

Status: under construction. See [docs/backlog](docs/backlog/README.md).
