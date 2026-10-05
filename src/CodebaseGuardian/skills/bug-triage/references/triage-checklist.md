# Triage checklist

## Read

- [ ] Which command failed and with which exit code?
- [ ] What is the first error in the log?
- [ ] Which tests failed? Do they share a file, class or feature?
- [ ] Did the run time out?

## Reproduce

- [ ] `run_checks` fails the same way (same tests, same error).
- [ ] If it passes: run once more. Pass then fail on the same commit means flaky.

## Isolate

- [ ] `recent_commits` lists what changed since the last green run (check `guardian://checks/latest` history in events).
- [ ] `diff_summary` shows files that relate to the failing tests.
- [ ] The first bad commit is identified, or the range is narrowed.

## Classify

| Class | Signs |
|---|---|
| Regression | Fails consistently; related files changed recently |
| Flaky | Result changes without a code change; timing, ordering or random data involved |
| Environment | Error mentions a missing tool, permission, port, network or file outside the repository |

## Close

- [ ] Report written from the issue template.
- [ ] The user knows what you propose and what you did not change.
