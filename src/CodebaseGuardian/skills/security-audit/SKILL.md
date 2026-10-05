---
name: "security-audit"
description: "Triage possible secrets in the repository. Use when a security.secret_detected event arrives, or before sharing code. Confirms findings with scan_secrets, classifies them by rule id and orders remediation: revoke first, rewrite history only with consent."
license: "MIT"
metadata:
  author: "codebase-guardian"
  version: "1"
---

# Security audit

Use this when `security.secret_detected` arrives, when `scan.completed` reports `secretFindings` above zero, or when the user asks whether secrets are in the repository.

Never write a secret value into a message, file, issue or comment. Refer to a finding by `ruleId`, `path` and `line` only.

## Steps

0. For `scan.completed`, read the Markdown report at its `reportUri` first: the Secrets table lists `Rule`, `Path`, `Line` and a redacted value for each finding. Then continue with step 2 using `working_tree`. To start a scan yourself call `full_scan`. It runs as an MCP task; if your client has no task support, call `scan_secrets`, `audit_dependencies` and `run_checks` instead.
1. Read the event payload. For every entry in `findings` note `ruleId`, `path`, `line` and the redacted value. Note `commitSha` and `source`.
2. Confirm each finding with `scan_secrets`. For a finding from a commit call it with `scope` set to `commit` and `commit` set to the `commitSha`. For pending changes use `staged`; for everything on disk use `working_tree`.
3. Classify each confirmed finding by its `ruleId` with [the secret types table](references/secret-types.md). It tells you what the credential grants and how likely a false positive is.
4. Decide whether it is real. Look at the line with `diff_summary` using `from` and `to` around the commit. Placeholders, documentation examples and test fixtures are usually not real. If unsure, treat it as real.
5. For every real secret follow [the remediation order](references/remediation.md): revoke and rotate first, then replace it in the code, and only with the user's consent rewrite history.
6. After the fix, call `scan_secrets` again with the same scope and confirm the finding is gone. If the file is a known false positive, suggest an entry in `.guardianignore` and let the user add it.
7. Report: findings by `ruleId`, which are real, what was revoked or still needs doing by the user, and whether history rewriting was proposed.

## Rules

- Revoking the credential is the user's action at the provider. You cannot do it; say exactly which credential and where.
- A secret that was committed must be treated as leaked, even if the commit was never pushed.
- Do not rewrite history, force-push or delete branches without explicit consent. See the remediation file for why.
