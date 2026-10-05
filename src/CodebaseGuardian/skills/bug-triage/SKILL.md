---
name: "bug-triage"
description: "Find out why checks or CI fail. Use when a checks.failed, github.ci.failed or github.issue.opened event arrives or the user reports a failing build. Reproduces with run_checks, isolates the cause with recent_commits and diff_summary and files an issue report."
license: "MIT"
metadata:
  author: "codebase-guardian"
  version: "1"
---

# Bug triage

Use this when `checks.failed`, `github.ci.failed` or `github.issue.opened` arrives, or when the user reports that the build or tests fail.

## Steps

1. Read the event payload: `runId`, `command`, `exitCode`, `summary`, `failedTests`, `commitSha` and `timedOut`.
2. Read the log with the resource `guardian://checks/{runId}/log`, using the `runId` from the event. Find the first error, not the last; later errors are often consequences. `guardian://checks/latest` shows the newest run.
3. Reproduce. Call `run_checks` and compare the result with the event. Runs are one at a time and can take minutes.
4. Isolate the change. Call `recent_commits` to see what landed before the failure, then `diff_summary` with `from` and `to` set to the suspect range, or just `from` to compare the working tree. Match the changed files with the failing tests and the first error.
5. Classify with [the triage checklist](references/triage-checklist.md): regression (a recent change broke it), flaky (it passes on a second run with no change) or environment (toolchain, network, missing configuration).
6. For `github.ci.failed`, open the run `url` from the payload for the GitHub log, then map `headSha` to the local history: `recent_commits` shows whether the commit is here, and `diff_summary` with `to` set to `headSha` shows what it changed. A `headSha` that is not in the local history means you have not fetched it; say so.
7. For `github.issue.opened`, read `title`, `labels` and `body`, then try to reproduce the problem with `run_checks`. If it does not reproduce, say what you tried instead of closing the question.
8. Write the report using [the issue template](assets/issue-template.md). Fill every section; write "unknown" rather than guessing.
9. To file the report on GitHub, call `create_issue` with the report as the body. The server asks the user to confirm before anything is written. Never include secrets or whole logs.
10. Offer next steps: a fix you can make, or what the user must decide. Do not change code or commit before the user agrees.

## Rules

- Quote only the lines of the log that matter; never paste a whole log, and never paste anything that looks like a secret.
- Call something flaky only after you saw it pass and fail on the same commit.
- If `timedOut` is true, say so; the test result is incomplete.
