# Guardian events

Every event has a name, a timestamp and a JSON payload. Payloads that have a natural follow-up include `suggestedSkill`.

## repo.commit.created

A commit reachable from a local branch that the watcher had not seen. Argument: `branch` (optional filter).
Payload: `sha`, `shortSha`, `branch`, `author`, `committedAt`, `subject`, `filesChanged`, `insertions`, `deletions`, `files`, `suggestedSkill` (`skill://pr-review/SKILL.md`).

## repo.branch.changed

HEAD now points at a different branch. Payload: `from`, `to`, `headSha`. `to` is empty for a detached HEAD.

## repo.files.changed

Working-tree edits, debounced; build output and dependency folders are ignored. Argument: `pathPrefix` (optional filter).
Payload: `paths` (at most 200), `count`.

## repo.dependencies.changed

A dependency manifest changed, in the working tree or in a commit. Payload: `manifests`, `ecosystems`, `commitSha`, `suggestedSkill` (`skill://dependency-hygiene/SKILL.md`).

## checks.completed

Any check run finished. Payload: `runId`, `command`, `exitCode`, `passed`, `timedOut`, `durationMs`, `summary`, `failedTests`, `logUri`, `trigger`, `commitSha`. `logUri` is a `guardian://checks/{runId}/log` resource.

## checks.failed

A check run failed; `checks.completed` is also emitted. Same payload plus `suggestedSkill` (`skill://bug-triage/SKILL.md`).

## security.secret_detected

The scanner found something in a new commit or an explicit scan. Payload: `source`, `commitSha`, `findings`, `suggestedSkill` (`skill://security-audit/SKILL.md`). Each finding has `ruleId`, `path`, `line` and a redacted value. The secret itself is never part of the event.
