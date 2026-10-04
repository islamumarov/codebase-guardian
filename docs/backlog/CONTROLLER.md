# Backlog controller procedure

How any controller (the interactive session or the scheduled cloud routine) continues [BACKLOG.md](BACKLOG.md). It condenses the superpowers `subagent-driven-development` process. When that skill is available, invoke it and use this file for the project-specific parts: lock, [STATUS.md](STATUS.md), toolchain and push rules.

Durable state lives in git. [STATUS.md](STATUS.md) records completed tasks, rulings and deferred minors. Scratch files live in `.superpowers/sdd/BACKLOG/`, which is git-ignored and per checkout. Shell variables do not survive between commands, so keep values you need later (lock SHA, BASE, run start) in files there.

## 1. Ground rules

- **Authority.** The spec `docs/specs/2026-10-04-codebase-guardian-design.md` binds. The plan argues from it, and your rulings settle the rest. Record each ruling in STATUS.md as `Ruling: <decision> — <why> — <cost if wrong>`.
- **Branches.** Work on `feat/codebase-guardian` only. Never force-push it, never touch `main`, never merge, never open pull requests. The only other ref you write is the lock branch `sdd-lock`.
- **Roles.** Subagents implement and review. The controller never edits product code or tests itself.
- **Pushing.** Subagents never push. The controller pushes `feat/codebase-guardian` after each completed task.
- **Concurrency.** One implementer at a time. One controller at a time (section 3).
- **When to stop.** Stop, with a note in STATUS.md, only for:
  - an irreversible or destructive operation;
  - a security-sensitive action;
  - a side effect outside this branch and the lock branch;
  - a plan so broken that every path forward is a guess.

  Everything else: decide, record the ruling, continue.

## 2. Setup (every run)

```bash
git fetch --prune origin
git checkout feat/codebase-guardian
git pull --ff-only origin feat/codebase-guardian
git config user.email >/dev/null || { git config user.name "$(git log -1 --format=%an)"; git config user.email "$(git log -1 --format=%ae)"; }
mkdir -p .superpowers/sdd/BACKLOG && printf '*\n' > .superpowers/sdd/.gitignore
date -u +%s > .superpowers/sdd/BACKLOG/run-start
```

**.NET.** `global.json` pins SDK `11.0.100-rc.1.26425.128` (roll-forward `latestFeature`). Projects target `net10.0`, so tests also need the .NET 10 runtimes. If `dotnet --list-sdks` lacks either, install both:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --version 11.0.100-rc.1.26425.128 --install-dir "$HOME/.dotnet"
bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
```

Every later command needs `export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"` in the same shell call, and every subagent dispatch must say so too. If installation fails, stop and report the error. Release the lock if you hold it.

**Baseline.** Acquire the lock (section 3), then run `dotnet test` at the repo root.
- If it fails, run it once more.
- If it fails again, append `Baseline red at <sha7>: <failing test names>` to STATUS.md, commit, push, release the lock and stop.

## 3. Lock: one controller at a time

The lock is the remote branch `sdd-lock`, an append-only chain of empty-tree commits. Each subject reads `sdd-lock state=<held|released> owner=<owner>`.

- Every change is a fast-forward push of a new commit on top of the current tip. The cloud git proxy rejects branch deletion, and force-pushes are never needed.
- A non-fast-forward rejection means another controller moved the lock first.
- A `held` tip older than 3 hours is stale and may be taken over.

**Check** (before any change):

```bash
git fetch --prune origin
git rev-parse -q --verify origin/sdd-lock && echo "age=$(( $(date +%s) - $(git log -1 --format=%ct origin/sdd-lock) ))s tip=$(git log -1 --format=%s origin/sdd-lock)"
```

If the tip says `state=held` and its age is under 10800 s, another controller is working. End the run at once, with no changes and the final message `skipped: <tip subject>`.

**Acquire, heartbeat, release.** One helper does all three. STATE is `held` or `released`; OWNER is `routine-<UTC timestamp>` or `interactive-<session id>`:

```bash
W=.superpowers/sdd/BACKLOG
# PARENT: acquire -> current remote tip (empty if the branch does not exist yet); heartbeat/release -> $(cat $W/lock-sha)
PARENT=$(git rev-parse -q --verify origin/sdd-lock || true)
NEW=$(git commit-tree "$(git hash-object -t tree -w /dev/null)" ${PARENT:+-p "$PARENT"} -m "sdd-lock state=STATE owner=OWNER")
git push origin "$NEW:refs/heads/sdd-lock" && echo "$NEW" > $W/lock-sha
```

- **Acquire** before any change. If the push is rejected, another controller won the race: end the run with no changes.
- **Heartbeat** (`state=held`, parent `lock-sha`) after every completed task. If it is rejected, you lost the lock: stop immediately and push nothing else.
- **Release** (`state=released`, parent `lock-sha`) at the end of every run that acquired the lock.

## 4. Where work stands

- Tasks with a `Task <N>: complete` line in STATUS.md are done. If STATUS.md contains `ALL TASKS COMPLETE`, release the lock and end the run with `backlog complete`.
- The next task is the lowest N in 1..23 without a complete line.
- If the last STATUS.md line for task N reads `Task <N>: implemented (commits <A7>..<B7>), review pending`, the implementation already exists:
  - Skip the implementer dispatch. Start at step 2 (review) with range `<A>..<B>`.
  - Tell the reviewer the implementer's report is unavailable.
  - If fixes are needed, dispatch a fresh implementer with the brief, the findings and the commit range.
- Otherwise `BASE=$(git log -1 --format=%H -- docs/backlog/STATUS.md)`. Save it to `$W/task-<N>-base`.
- If `git rev-list --count $BASE..HEAD` is not 0, a previous controller stopped mid-task N. Its commits are a partial implementation:
  - Dispatch a fresh implementer with the brief.
  - Tell it: "Commits `<BASE7>..<HEAD7>` already implement part of this task. Verify them against the brief, finish what is missing, and run the full suite."
  - Then continue the loop from there.

## 5. Per-task loop

Files (with `W=.superpowers/sdd/BACKLOG`):

```bash
# brief for task N (same extraction as the superpowers task-brief script)
awk -v n="$N" '/^```/{f=!f} !f && /^#+[ \t]+Task[ \t]+[0-9]+/{t=($0 ~ ("^#+[ \t]+Task[ \t]+" n "([^0-9]|$)"))} t' docs/backlog/BACKLOG.md > $W/task-$N-brief.md
# global constraints (once per run)
{ sed -n '/^\*\*Spec:\*\*/,/^- `docs\/reference\/sdk-2.2-notes.md`/p' docs/backlog/BACKLOG.md; echo; sed -n '/^## Global Constraints/,/^## Review Focus/p' docs/backlog/BACKLOG.md | sed '$d'; } > $W/global-constraints.md
# review package for a range
{ echo "# Review package: $FROM..$TO"; git log --oneline $FROM..$TO; git diff --stat $FROM..$TO; git diff -U10 $FROM..$TO; } > $W/review-$N-$(git rev-parse --short $FROM)-$(git rev-parse --short $TO).diff
```

**Models.** Always set the model explicitly.

| Role | Model |
|---|---|
| Implementers | `sonnet` (`haiku` only when the brief contains the complete code) |
| Reviewers | `sonnet`; `opus` for Tasks 6, 8, 17, 20, 21 (cursor, stream, confirmation and webhook security logic) |
| Fix rounds 4–5 | `opus` |
| Final whole-branch review | `opus` |

**Steps:**

1. **Implement.** Dispatch the implementer with the template below. Wait for its short report.
   - DONE → step 2.
   - DONE_WITH_CONCERNS → read the concerns. Correctness or scope concerns get fixed before review.
   - NEEDS_CONTEXT → give the missing context, re-dispatch.
   - BLOCKED → change something: more context, a more capable model, or a smaller piece. Never retry unchanged.
2. **Review.** Write the review package for `BASE..HEAD`. Dispatch the reviewer with the template below.
3. **Fix loop.** It runs when the review reports spec ❌, any Critical or Important finding, or a ⚠️ item you confirmed as a real gap.
   - Minor findings never enter the loop. Append them to STATUS.md as `Task <N>: minor (deferred): <one-liner>`.
   - At most 5 rounds. A round is one fix dispatch plus one scoped re-review of `FIX_BASE..HEAD`, where FIX_BASE is the head the previous review saw.
   - Rounds 1–3: resume the same implementer (SendMessage) with the open findings verbatim. If you can't resume it, dispatch a fresh one with brief, report file and findings.
   - Rounds 4–5: a fresh `opus` implementer, told "A prior implementer attempted this task R−1 times; you own it now; read the report file."
   - After round 5, adjudicate each open finding. Park it with a ruling if the reviewer is wrong, or if it is real but nothing builds on it. If it is real and load-bearing, rule on the smallest unblocking change and carry it into the next task. Every adjudication is a `Ruling:` line in STATUS.md.
4. **Complete.** Append to STATUS.md:
   - `Task <N>: complete (commits <BASE7>..<HEAD7>, review clean)` or `…, <K> parked)`
   - any new `Ruling:` and deferred-minor lines

   Then commit, push, and send the lock heartbeat:

   ```bash
   git add docs/backlog/STATUS.md
   git commit -m "chore(backlog): record task <N> complete" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
   git push origin feat/codebase-guardian
   ```
5. **Time budget (scheduled runs only).** Do not start a new task once 80 minutes have passed since `run-start`. A task in flight stops at a clean point: a complete line, or a recorded `Task <N>: fix round <R>/5 (...)` line in STATUS.md, committed and pushed.
6. **Cloud runs.** Dispatch subagents in the foreground (`run_in_background: false`). An idle cloud VM pauses after a few minutes without activity, and a controller waiting on a background child looks idle.

### Implementer dispatch template

```
You are implementing Task <N>: <title> of the Codebase Guardian backlog (MCP C# SDK 2.2.0, .NET 10).
Read first — your requirements, with exact values to use verbatim: <abs path>/.superpowers/sdd/BACKLOG/task-<N>-brief.md
Binding global constraints: <abs path>/.superpowers/sdd/BACKLOG/global-constraints.md
Reference: docs/reference/sdk-2.2-notes.md and docs/reference/skills-events-wire-format.md (sections the brief cites). Do not read the whole plan.
Context: <one line on where the task fits>; <interfaces/decisions from earlier tasks the brief cannot know>; <rulings from STATUS.md that touch this task>.
Toolchain: <dotnet PATH export if installed under $HOME/.dotnet>.
Work from <repo root> on branch feat/codebase-guardian. Test-first. While iterating run focused tests; run the full `dotnet test` once before committing.
Commit with the brief's commit message plus the trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push. Never switch branches.
Do all the work yourself: never spawn subagents, never spawn a reviewer.
If anything is unclear, report NEEDS_CONTEXT with specific questions; if stuck, BLOCKED with what you tried. Bad work is worse than no work.
Before reporting, self-review your diff: completeness, YAGNI, real-behaviour tests, pristine test output.
Write the full report to <W>/task-<N>-report.md: what you built, tests and results, TDD evidence (RED command + failing output; GREEN command + passing output), files changed, self-review findings, concerns.
Reply with only: Status (DONE | DONE_WITH_CONCERNS | BLOCKED | NEEDS_CONTEXT), commits (short SHA + subject), one-line test summary, concerns, report path.
If resumed with review findings: fix them, re-run the covering tests, append a fix report (changes, tests, command, output) to the same report file, reply with the same short contract.
```

### Reviewer dispatch template

```
Review one task's implementation: spec compliance first, then code quality. Task-scoped gate, not a merge review.
Requirements: <W>/task-<N>-brief.md. Binding constraints: <W>/global-constraints.md.
Implementer's claims (unverified — check them against the diff): <W>/task-<N>-report.md.
Diff: <review package path> (BASE <sha>, HEAD <sha>). Read it once; it is your view of the change. Inspect code outside it only for a concrete named risk, one focused check per risk.
Read-only: never change the working tree, index, HEAD or branches. Never spawn subagents.
Do not re-run the suite; run one focused test only for a specific doubt. Warnings in the reported test output are findings.
Part 1 spec compliance: Missing / Extra / Misunderstood, with file:line. Requirements you cannot verify from the diff: list as ⚠️ for the controller.
Part 2 quality: separation of concerns, error handling, DRY, edge cases, tests that verify real behaviour, file structure per the brief.
Severity: Critical/Important = the task cannot be trusted until fixed (incorrect or fragile behaviour, missed requirement, swallowed errors, tests that assert nothing, verbatim duplicated logic — plan-mandated defects are still Important, labelled plan-mandated). Polish = Minor.
Output: "### Spec Compliance" (✅ or ❌ list, plus ⚠️ list), "### Strengths", "### Issues" (Critical / Important / Minor, each with file:line, what, why, fix), "### Assessment" (Task quality: Approved | Needs fixes, one-line reasoning). No preamble.
```

### Scoped re-review template

```
Re-review one fix round of Task <N>. Requirements: <W>/task-<N>-brief.md. Report with appended fix reports: <W>/task-<N>-report.md.
Findings under verification (verbatim): <list>.
Fix diff: <review package for FIX_BASE..HEAD>. Read-only; never spawn subagents; do not re-run the suite.
For each finding: ADDRESSED or NOT ADDRESSED with file:line evidence ("attempted" is not addressed). Then: new breakage in the fix diff (severity, file:line) or "None"; out-of-scope observations (non-blocking) or "None"; verdict: all addressed with no new Critical/Important, or the open list.
```

## 6. After Task 23

1. **Final review.** Write the package for `29486f4..HEAD`, where 29486f4 is the scaffold commit and the merge base with `main`. Dispatch an `opus` reviewer for a whole-branch merge review. Point it at STATUS.md's rulings and deferred minors, so it can triage which must be fixed before merge.
2. **Fix wave.** If it finds issues, dispatch ONE fix implementer with the complete list, then exactly one scoped re-review. Adjudicate what remains as `Ruling:` lines.
3. **Close out.** Append `ALL TASKS COMPLETE (final review: clean | <K> residual, see rulings)` to STATUS.md, then commit, push and release the lock. Merging is the user's decision; do not merge.

## 7. End of every run

- STATUS.md is committed and pushed.
- The lock is released.
- The final message lists:
  - tasks completed this run;
  - rulings made this run;
  - anything that needs the user.
