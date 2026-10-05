# Backlog status

Durable record for [BACKLOG.md](BACKLOG.md), shared by every controller: the interactive session and the scheduled cloud routine. The procedure is in [CONTROLLER.md](CONTROLLER.md).

Append-only. Each controller adds lines, commits them with `chore(backlog): …` and pushes `feat/codebase-guardian`.

## Rulings

Preflight rulings (2026-10-05) are already applied to the BACKLOG.md text. They are listed here so nobody re-decides them.

- Ruling (setup): work in place on `feat/codebase-guardian`, with no worktree — the dedicated feature branch already isolates `main` — if wrong: add a worktree later; history unaffected.
- Ruling R1 (T2): `GetBranchHeadsAsync` keys are short branch names (`main`) — the T9 test and the `branch` payload fields use short names — if wrong: rename keys in one method and its callers.
- Ruling R2 (T4): a configured skills root that does not exist is a validation error — fail fast like every other skill error — if wrong: a one-line change.
- Ruling R3 (T16): `GuardianHttpTestHost` also defaults `Guardian:GitHub:Enabled=false` — otherwise HTTP tests would reach api.github.com — if wrong: none.
- Ruling R4 (T18): both test hosts default `Guardian:GitHub:PollEnabled=false` — tests drive `PollOnceAsync` themselves — if wrong: none.
- Ruling R5 (T14): `HttpHost.Build` forces `Transport = Http` — options must describe the running transport, and webhook enablement reads it — if wrong: an overridden config value (intended).
- Ruling R6 (T14): HTTP tests use real Kestrel on 127.0.0.1:0, not the WebApplicationFactory/TestServer that spec §8 names — SSE streams and cancellation were verified on Kestrel; tests stay in-process and offline — if wrong: swap the host builder in `GuardianHttpTestHost`.
- Ruling R7 (T18): `GitHubEventPoller` is a singleton whenever GitHub is enabled; its hosted-service wrapper is added only when polling is enabled — tests resolve it with polling off — if wrong: none.
- Ruling R8 (T21): the delivery-time SSRF test reads `LastError` from `WebhookSubscriptionStore`, not from a refresh — a refresh of an `http://` URL fails validation once the dev flag is off — if wrong: none.
- Ruling (handoff): controllers push `feat/codebase-guardian` after each completed task; subagents never push — the user approved a cloud routine backed by this private repo — if wrong: the remote holds a copy the user can delete.
- Ruling (handoff): one controller at a time, via the append-only remote lock branch `sdd-lock` (a `held` tip goes stale after 3 h; no deletes or force-pushes, since the cloud git proxy rejects deletion) — the routine and the interactive session must not diverge the branch — if wrong: a run skips needlessly; push a `state=released` commit, or delete `sdd-lock` on GitHub, to unblock.

## Deferred minors

## Progress
Task 1: implemented (commits d37a914..5631323), review pending
Ruling (toolchain, user-directed 2026-10-05): `global.json` pins SDK `10.0.100` (roll-forward `latestFeature`) instead of `11.0.100-rc.1.26425.128`; cloud runs install `dotnet-sdk-10.0` from apt — cloud egress blocks `builds.dotnet.microsoft.com`, and the user said to use .NET 10; projects already target `net10.0` and the suite builds and runs on SDK 10.0.112 — if wrong: re-pin 11 rc in `global.json` where that SDK can be installed.
Ruling (baseline): on SDK 10.0.112 the baseline is 38/39; `TempGitRepo_ignores_global_and_system_git_configuration` fails because `TempGitRepo` does not neutralise env-scoped git config (`GIT_CONFIG_COUNT`/`GIT_CONFIG_KEY_n`/`GIT_CONFIG_VALUE_n`, `GIT_CONFIG_PARAMETERS`) that the cloud proxy injects. This is Task 1 code under review, so it goes to Task 1's fix loop instead of a `Baseline red` stop — if wrong: none; the test exists to catch exactly this.
Task 1: complete (commits d37a914..b082e52, review clean after 1 fix round; b082e52 makes TempGitRepo strip GIT_CONFIG_COUNT/KEY_n/VALUE_n/PARAMETERS; suite 39/39 on SDK 10.0.112)
Ruling (T1): no `NoWarn` added to the csproj — the brief asks for one only "for MCP experimental IDs actually reported", and `dotnet build` reports 0 warnings — if wrong: add the ID when a later task triggers it.
Task 1: minor (deferred): `AddCodebaseGuardian` registers the validator and `GuardianInstructions` with `AddSingleton`, so a second call duplicates them; use `TryAddEnumerable`/`TryAddSingleton`.
Task 1: minor (deferred): `InProcessMcpServer.DisposeAsync` never stops the host if `Client.DisposeAsync()` throws; wrap it in try/finally.
Task 1: minor (deferred): `GuardianCommandLine.Normalize` consumes a positional literal `true`/`false` that follows a bare boolean flag (documented and tested quirk).
Task 2: complete (commits b6e42ca..b09604a, review clean; suite 83/83)
Task 2: minor (deferred): `ProcessRunner` awaits stream drain unbounded on the non-timeout path — a descendant holding stdout/stderr open can hang `RunAsync` past `Timeout` and ignore cancellation; apply the post-kill `WhenAny` grace/linked token to every path.
Task 2: minor (deferred): `ListFilesAsync` reuses the 16 MB numstat cap and ignores `OutputTruncated` (a cut last path can be returned); `ls-files -co` also lists tracked files deleted from the working tree.
Task 2: minor (deferred): `GitRepository.RootPath` resolves sync-over-async on first access (CancellationToken.None, 120 s timeout).
Task 2: minor (deferred): `ProcessRunner` output capped mid-UTF-8 character decodes to U+FFFD (patches already cut on a boundary).
Task 2: minor (deferred): subdirectory `RootPath` test compares `Path.GetFullPath`, which fails on macOS where the temp dir is a symlink; compare real paths.
