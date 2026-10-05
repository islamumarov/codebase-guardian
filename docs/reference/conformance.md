# Conformance results (Task 14)

Server under test: Codebase Guardian over stateless Streamable HTTP, run on 2026-10-05.

## Suite version

`npx -y @modelcontextprotocol/conformance@latest` resolves to **0.1.16**, which has **no** `sep-2640-skills-*` scenarios and no
`2026-07-28` scenarios (`list` shows none). Those exist only in the **alpha** dist-tag, **0.2.0-alpha.12**, which is what every
result below used. Pin it: `npx -y @modelcontextprotocol/conformance@0.2.0-alpha.12`.

## Command lines

```bash
# server, background
dotnet run --project src/CodebaseGuardian -- --transport http --repo . --urls http://127.0.0.1:5199 &
C="npx -y @modelcontextprotocol/conformance@0.2.0-alpha.12"; U=http://127.0.0.1:5199/mcp

$C list                                   # discover scenarios
$C server --url $U --scenario sep-2640-skills-enumeration
$C server --url $U --scenario sep-2640-skills-manifest
$C server --url $U --scenario sep-2640-skills-directory
$C server --url $U --spec-version 2026-07-28                # the 20-scenario active suite
$C server --url $U --spec-version 2026-07-28 --scenario <id>  # draft/extra: dns-rebinding-protection, server-stateless,
                                                              # caching, http-header-validation,
                                                              # http-custom-header-server-validation, sep-2164-resource-not-found
```

## Skills scenarios (the acceptance target)

| Scenario | Result |
|---|---|
| `sep-2640-skills-enumeration` | 32/32 SUCCESS, 0 failed, 0 warnings |
| `sep-2640-skills-manifest` | 6/6 SUCCESS |
| `sep-2640-skills-directory` | 7/7 SUCCESS |

Every check in all three scenarios (including `wire-schema-valid`) is SUCCESS: no FAILURE, no WARNING.

## Base 2026-07-28 active suite (20 scenarios)

| Result | Scenarios |
|---|---|
| Pass | `tools-list` (4/4), `server-sse-multiple-streams` (2/2), `resources-list` (2/2) |
| Fail: needs the suite's fixture, absent by design | `tools-call-simple-text`, `-image`, `-audio`, `-embedded-resource`, `-mixed-content`, `-error`, `-with-progress` (tools `test_*` do not exist); `resources-read-text`, `-read-binary`, `resources-templates-read` (`test://…` resources); `completion-complete` (no `completion/complete`); `prompts-list`, `prompts-get-simple`, `-with-args`, `-embedded-resource`, `-with-image` (Guardian declares no prompts) |
| Fail then fixed | `dns-rebinding-protection`: first run `localhost-host-rebinding-rejected` FAILURE (Host `evil.example.com` got 200). See below. |

These fixture failures are not defects: Guardian is a repository server, not the suite's reference server. They are
"Method not available" / "Unknown tool|resource" errors for primitives it intentionally does not expose.

### Fixed in this task: DNS rebinding

A loopback-bound server with repository access must not answer a request whose `Host` or `Origin` names another host (a
web page can point an attacker's hostname at 127.0.0.1). `HttpHost` now answers 403 for any non-loopback `Host`/`Origin`
unless `Guardian:HttpAllowRemote` is true. After the fix `dns-rebinding-protection` is 2/2 SUCCESS. Covered by
`HttpTransportTests`.

## Other 2026-07-28 scenarios, run individually

| Scenario | Result |
|---|---|
| `http-header-validation` | 14/14 SUCCESS |
| `sep-2164-resource-not-found` | 3/3 checks pass; 1 WARNING `sep-2164-data-uri` (SHOULD: error `data` should echo the requested URI). Comes from the SDK's built-in `resources/read` error; not changed here. |
| `caching` | 7/8; FAILURE `sep-2549-prompts-list-caching-hints` only because `prompts/list` does not exist. tools/resources/templates/read hints SUCCESS. |
| `server-stateless` | 24/28 SUCCESS, 2 SKIPPED by the suite (prompts/tools list-changed on subscription). The 4 FAILUREs are all "Not testable: server does not list the diagnostic tool `test_missing_capability` / `test_streaming_elicitation` / `test_logging_tool`" (`sep-2575-server-rejects-undeclared-capability`, `sep-2575-missing-capability-http-400`, `sep-2575-http-server-no-independent-requests-on-stream`, `sep-2575-server-no-log-without-loglevel`). Fixture-dependent, not a behaviour failure. |
| `http-custom-header-server-validation` | 1/6: five FAILUREs "Not testable: server exposes no tool with x-mcp-header annotations". Guardian defines none. |

## Events over HTTP: final result frame on server-initiated close

Question carried from Task 8: is the final `events/stream` frame `{"resultType":"complete","_meta":{}}` delivered over
Streamable HTTP when the server closes the stream? Observed: **yes**, deterministically, on host shutdown
(`WebApplication.StopAsync` while a stream is open): the client's `events/stream` request completes with that result.
Pinned by `HttpTransportTests.Events_stream_receives_the_final_complete_frame_when_the_server_closes_it` (stable over repeated runs).
Client cancellation sends no result, as the spec says (`Events_stream_delivers_active_then_events_and_stops_after_the_client_cancels`).
