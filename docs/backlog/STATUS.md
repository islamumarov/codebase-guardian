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
Task 3: complete (commits bfdb9b4..e0aeb5e, review clean after 1 fix round; suite 99/99)
Ruling (T3): commit timestamps go on the wire through the shared `CodebaseGuardian.Json.UtcTimestampJsonConverter` (UTC, `yyyy-MM-ddTHH:mm:ss.fffZ`), applied by attribute on model properties; later tasks that emit timestamps reuse it — the global constraint binds and no later task defines a converter — if wrong: swap the attribute for serializer-wide options.
Ruling (T3): null properties (e.g. `headSha` on an unborn repo) are omitted from tool structured content and resource JSON, not emitted as `null`, and `recent_commits` returns `{commits:[...]}` — the SDK omits nulls, structured content must be an object, and the spec names no shape — if wrong: one serializer option and a wrapper rename.
Task 3: minor (deferred): `committedAt` now has an unconstrained (`{}`) schema in `recent_commits`' outputSchema because of the property-level converter; a schema transform could restore `type: string, format: date-time`.
Task 3: minor (deferred): `ToolErrors` messages from `ArgumentException` keep the "(Parameter 'x')" suffix.
Task 3: minor (deferred): the resource serializer options duplicate the SDK's tool serialization; a parity test would catch drift.
Task 4: complete (commits 098c353..60ed19d, review clean; suite 141/141)
Task 4: minor (deferred): `SkillCatalog.Load` lets IOException/UnauthorizedAccessException from size/read/walk escape raw instead of collecting them into `SkillValidationException`.
Task 4: minor (deferred): `FrontmatterParser` turns explicitly tagged plain scalars (`!!int 5`) into strings and keeps `.inf`/`.nan` as strings; `1e999` parses to Infinity and would break `ToJsonString` — reject tagged and non-finite scalars.
Task 4: minor (deferred): the skills walk adds non-regular files (FIFOs, sockets), which would hang `ReadAllBytes`; skip or reject them.
Task 4: minor (deferred): empty subdirectories never enter the directory index, so `TryListDirectory` returns false for them.
Task 4: minor (deferred): a bad file in a nested skill is reported for both outer and inner skill; nested files are re-hashed per skill.
Task 4: minor (deferred): literal U+FEFF characters in FrontmatterParser.cs and FrontmatterParserTests.cs (use `﻿`); the symlink test returns silently instead of `Assert.Skip`; no tests for 501-char/non-string `compatibility` or the 16 MiB boundary.
Task 5: complete (commits abd0526..5acde46, review clean; suite 162/162 verified by controller)
Task 5: minor (deferred): the skills `resources/list` filter appends skill entries to every page; append only when `cursor` is null if app resource listings ever paginate.
Task 5: minor (deferred): calling `WithSkills` twice registers the handlers twice; use `TryAddEnumerable` or throw.
Task 5: minor (deferred): `SkillJson.Cacheable` moves nodes between objects just to order keys; build the result directly.
Task 5: minor (deferred): no tests for `resources/read` on a directory URI (-32602), for `ttlMs`/`cacheScope` on the blob path, or for `resultType` on `resources/read`.
Task 6: complete (commits 1dde25a..7149b41, review clean after 1 fix round; suite 224/224)
Ruling (T6): a same-epoch cursor ahead of the head is a gap (`Truncated=true`, head cursor, no events), not an error; `InvalidCursorException` is only for undecodable cursors — wire-format B11 makes `truncated` the single gap signal — if wrong: one branch in `InMemoryEventLog.Read`.
Ruling (T6): supplied event ids starting with the log's generated prefix `evt_<8hex>_` are rejected with `ArgumentException` — otherwise they can collide with generated ids and break dedupe — if wrong: suffix generated ids on collision instead.
Ruling (T6): `IEventLog.GetSequence` does not check the epoch; stream loops (Task 8) must derive sequences only from cursors returned by `Read` — the class documents this — if wrong: add an epoch check to `GetSequence`.
Task 6: minor (deferred): the `IEventLog.GetSequence` interface doc lacks the "use cursors returned by Read" caveat (only the class has it) — Task 8 should rely on the class doc.
Task 6: minor (deferred): `WaitAboveHeadIsNotCompletedByPublishThatDoesNotPassIt` asserts `IsCompleted == false` and could pass against the old code by scheduling luck.
Task 6: minor (deferred): exceptions from an `EventDefinition.Matches` predicate propagate out of `Read`; Task 7 may wrap them.
Task 6: minor (deferred): stray indentation at InMemoryEventLog.cs:33.
Task 7: complete (commits eca8742..2a0107b, review clean after 1 fix round; suite 250/250)
Ruling (T7): an exception from an `EventDefinition.Matches` predicate during `events/poll` becomes `-32603` with a generic message naming no predicate detail, and is logged server-side with the event name — predicate failures are server bugs and must not leak internals — if wrong: change one catch in `EventsConfigureOptions`.
Ruling (T7): `Mcp.Events` exposes internals to `CodebaseGuardian.Tests` (`InternalsVisibleTo`) so the parser's `Unsupported` path, unreachable through poll, has a unit test — if wrong: drop the attribute and the one test.
Task 7: minor (deferred): the `-32603` `McpProtocolException` does not carry the predicate exception as inner exception (SDK 2.2.0 has the `(string, Exception, McpErrorCode)` constructor).
Task 7: minor (deferred): calling `WithEvents` twice registers duplicate handlers; no test for the `InvalidCursorException` backstop in the poll handler; no test for `"arguments": null`.
Task 8: complete (commits 99b8424..e8412bc, review clean after 1 fix round; suite 263/263; stream tests stable over 6 consecutive runs)
Ruling (T8): on stdio the `events/stream` shutdown result (`{"resultType":"complete","_meta":{}}`) may be lost when the host stops the transport before the final frame is written (seen ~1 in 8 with a full `host.StopAsync`); accepted — wire-format B6 says the server sends it "whenever the server can write a final frame" and clients must not depend on it; the test raises `StopApplication()` to prove the handler result — if wrong: delay transport shutdown until stream handlers complete.
Ruling (T8): a `Matches` predicate failure mid-stream ends the stream with `-32603`; `notifications/events/error` (B6) as a recoverable path is left for later — the brief defers error/terminated notifications — if wrong: send the error notification and continue.
Task 8: follow-up for Task 14: verify the `events/stream` shutdown result frame is delivered over Streamable HTTP when the server initiates the close.
Task 8: minor (deferred): no deterministic test of the mid-stream `Truncated` branch (an `IEventLog` decorator registered before `WithEvents` could force it) and none of the faulted-wait rethrow.
Task 8: minor (deferred): `EventStreamHandler` logs under the `EventsConfigureOptions` category; a wait that ends cancelled without `ct` firing would re-loop (impossible with the in-memory log); the raw-pipe test name no longer covers its shutdown assertion; the parser still validates `maxEvents` on `events/stream` although the stream ignores it.
Task 9: complete (commits 125d12c..b3b24cf, review clean after 1 fix round; suite 303/303; watcher tests stable over 3 runs)
Ruling (T9): `poll_events` without a cursor reads from `OldestCursor` with `NewestFirst`, so `truncated` is always false there — without a cursor the caller has no position for a gap to refer to — if wrong: report truncation when the log has evicted anything.
Ruling (T9): an unborn repository gaining its first branch emits no `repo.branch.changed`; its first commit is announced as `repo.commit.created` — the brief does not require the branch event — if wrong: emit `from: null` on the first snapshot with a branch.
Ruling (T9): the watcher is always registered and no-ops when `WatchEnabled` is false (brief: "hosted watcher when `WatchEnabled`") — options bind after registration — if wrong: none functionally.
Ruling (T9): a commit is remembered as announced only after its event publishes; a failed publish keeps that branch's old head so it is retried next poll (other branches still advance) — "never lose announcements" — if wrong: a persistently failing publish holds back later commits on that one branch.
Task 9: minor (deferred): `announced` handed to `IRepositoryChangeHandler` is oldest-first per branch, not globally; a rebase emits transient `repo.branch.changed` to/from detached (`to: null`); debounced `repo.files.changed` publishes use `CancellationToken.None`; the ignore set is case-insensitive (`Bin`/`OBJ` ignored on Linux); negative assertions use a fixed 1.2 s settle delay.
Ruling (baseline, 2026-10-05 routine): baseline at 5e7fedf was red twice (6 `TempGitRepoIsolationTests`) because merge 5e7fedf kept b082e52's `CreateStartInfo` environment block and dropped d9d31f4's (GIT_* prefix drop, HOME/XDG_CONFIG_HOME/USERPROFILE → empty dir, LC_ALL=C). Instead of a `Baseline red` stop, a subagent restored d9d31f4's block in 28af347 (test infrastructure only; the prefix drop subsumes b082e52's GIT_CONFIG_* removal); controller verified the diff equals d9d31f4's block and the suite is 314/314 — the fix is a mechanical restore of the user's own commit, so a stop would only waste a run — if wrong: revert 28af347.
Task 10: complete (commits a1e0f88..d7ee892, review clean after 1 fix round; suite 348/348 verified by controller)
Ruling (T10): auto-checks run off the watcher poll loop — `AutoChecksCommitHandler` queues the newest sha into a capacity-1 DropOldest channel drained by its own hosted service — awaiting a run of up to 15 min inside the handler stalled every other repo event; "once per batch, newest commit" is kept and commits arriving mid-run collapse into the newest — if wrong: await in the handler again (one method).
Ruling (T10): a timed-out run's summary is "timed out after <N> minutes" and a check executable that cannot be started is a failed run (exit -1, summary = the exception message, `checks.failed` published) — the brief's "exit code <n>" fallback would read "exit code -1" for both and hide the cause — if wrong: two lines in `CheckRunner`.
Task 10: minor (deferred): `CheckRunner` lets a `PublishAsync` failure fail `run_checks` after the run was stored; catch and log like the watcher's publish-safely.
Task 10: minor (deferred): `TestOutputParser` matches `failed:`/`total:` anywhere in a line, case-insensitive, so stray stderr lines can override the summary; anchor markers to line start.
Task 10: minor (deferred): the run fields are spelled three times (`CheckRunResult`, `LatestCheckRun`, event payload); `AutoChecks` registration reads raw configuration, not bound `GuardianOptions`; `CheckCommandResolver` throws if the repo root is missing; a commit queued at shutdown is dropped; the missing-executable test carries unused harness setup.
Task 11: complete (commits 27b8ff2..35bf9ac, review clean; suite 401/401 verified by controller; process deviation: tests were written after the implementation, RED shown by stashing src — reviewer found the tests assert real behaviour)
Ruling (T11): `.guardianignore` applies to working-tree scans only; staged, commit and commit-handler scans report ignored paths too — the brief lists it under "Working tree" only — if wrong: apply `GuardianIgnore` in `ScanPatch` (expect noise from fixtures until then).
Ruling (T11): `security.secret_detected` from `scan_secrets` carries `commitSha: null` when no commit arg is given, not an omitted key — the event schema types it `["string","null"]`, as for `checks.*` — if wrong: one payload property.
Task 11: minor (deferred): `SecretScanner` ignores `DiffSummary.Truncated` — a staged/commit patch over 5 MB is cut silently and secrets past the cut are missed with `count: 0`; at least log a warning (security follow-up worth doing before merge).
Task 11: minor (deferred): `PatchParser` ignores combined diffs (`diff --cc`/`@@@`) of merge commits and does not octal-decode quoted paths (untested); working-tree scan follows tracked symlinks outside the repo; `SecretRulesTests` passes `null!` for `IGitRepository`.
Task 12: complete (commits 602dde5..49e2cae, review clean after 2 fix rounds; suite 426/426 verified by controller)
Ruling (T12): when the vulnerable check succeeds and the outdated check fails, the ecosystem is `Status:"failed"` with Reason "outdated check failed: …" but keeps its vulnerable packages — dropping vulnerability data because the lesser check failed hides what matters most — if wrong: one method (`WithOutdatedAsync`).
Ruling (T12): `audit_dependencies` returns summary text plus structured content AND advertises an outputSchema (`UseStructuredContent = true`, `OutputSchemaType = typeof(DependencyAuditReport)`, SDK 2.2.0 supports it), and emits nullable properties as JSON `null` — the inferred schema lists nullable properties as `required`, so omitting them (Ruling T3's convention) makes content fail its own schema; for tools with an output schema, schema conformance overrides T3's null-omission — if wrong: restore WhenWritingNull and strip nullable names from `required`.
Ruling (T12): `ProgressRelay` is shared (`Tools/ProgressRelay.cs`) by `run_checks` and `audit_dependencies`; later tools reuse it — if wrong: none.
Task 12: follow-up (before merge): `repo_status` (`branch`, `headSha`, `upstream`) and `diff_summary` (`files[].insertions`, `files[].deletions`) omit nulls that their inferred outputSchema marks `required`, so schema-validating clients reject e.g. `repo_status` on any branch without upstream; apply the T12 fix (emit nulls) and a schema-conformance test — the final review must not let this through.
Task 12: minor (deferred): NuGet keeps only the first advisory per package (severity may understate); the keep-vulnerable test covers npm only, not NuGet or bad outdated JSON; target selection LINQ in `DependencyAuditor` is dense.
