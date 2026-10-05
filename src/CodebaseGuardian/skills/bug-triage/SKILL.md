---
name: "bug-triage"
description: "Find out why checks fail. Use when a checks.failed event arrives or the user reports a failing build or test. Reads the check log, reproduces with run_checks, isolates the cause with recent_commits and diff_summary and writes an issue report."
license: "MIT"
metadata:
  author: "codebase-guardian"
  version: "1"
---

# Bug triage

Use this when `checks.failed` arrives, or when the user reports that the build or tests fail.

## Steps

1. Read the event payload: `runId`, `command`, `exitCode`, `summary`, `failedTests`, `commitSha` and `timedOut`.
2. Read the log with the resource `guardian://checks/{runId}/log`, using the `runId` from the event. Find the first error, not the last; later errors are often consequences. `guardian://checks/latest` shows the newest run.
3. Reproduce. Call `run_checks` and compare the result with the event. Runs are one at a time and can take minutes.
4. Isolate the change. Call `recent_commits` to see what landed before the failure, then `diff_summary` with `from` and `to` set to the suspect range, or just `from` to compare the working tree. Match the changed files with the failing tests and the first error.
5. Classify with [the triage checklist](references/triage-checklist.md): regression (a recent change broke it), flaky (it passes on a second run with no change) or environment (toolchain, network, missing configuration).
6. Write the report using [the issue template](assets/issue-template.md). Fill every section; write "unknown" rather than guessing.
7. Offer next steps: a fix you can make, or what the user must decide. Do not change code or commit before the user agrees.

## Rules

- Quote only the lines of the log that matter; never paste a whole log, and never paste anything that looks like a secret.
- Call something flaky only after you saw it pass and fail on the same commit.
- If `timedOut` is true, say so; the test result is incomplete.
