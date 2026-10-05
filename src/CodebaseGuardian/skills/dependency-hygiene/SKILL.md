---
name: "dependency-hygiene"
description: "Keep dependencies safe and current. Use when repo.dependencies.changed arrives or the user asks about vulnerable or outdated packages. Runs audit_dependencies, fixes critical and high vulnerabilities first, applies the upgrade policy and verifies with run_checks."
license: "MIT"
metadata:
  author: "codebase-guardian"
  version: "1"
---

# Dependency hygiene

Use this when `repo.dependencies.changed` arrives, when `scan.completed` reports `vulnerablePackages` above zero, or when asked about vulnerable or outdated packages.

## Steps

0. For `scan.completed`, read the Markdown report at its `reportUri`: the Dependencies section has a subsection per ecosystem. Confirm with step 2 before you change anything. To start a scan yourself call `full_scan`. It runs as an MCP task; if your client has no task support, call `scan_secrets`, `audit_dependencies` and `run_checks` instead.
1. Read the event payload: `manifests` and `ecosystems` say what changed, `commitSha` says which commit, if any.
2. Call `audit_dependencies`. Set `includeOutdated` to false for a quick vulnerability-only check. The tool queries the package feeds, so it needs network access and can be slow.
3. Read the status of each ecosystem. `skipped` means its toolchain is not installed and `failed` carries a reason. Report these; do not treat them as clean.
4. Rank the findings. Vulnerable packages come before outdated ones. Within vulnerable packages go by severity: critical, high, moderate, low. Direct dependencies before transitive ones.
5. Choose the upgrade for each package with [the upgrade policy](references/upgrade-policy.md): patch and minor upgrades are low risk, major upgrades need a look at the changelog first.
6. Propose the changes to the user, listing package, current version, target version and the advisory link. Edit manifests only after the user agrees.
7. After the upgrade call `run_checks`. If it fails, read `guardian://checks/{runId}/log` and see the `bug-triage` skill; revert the upgrade that caused it instead of piling on fixes.
8. Call `audit_dependencies` again and confirm the vulnerabilities are gone.
9. Report what was fixed, what remains and why (no fixed version, major upgrade deferred, toolchain missing).

## Rules

- Do not upgrade everything at once. One group of related packages per change, so a failure points at its cause.
- Do not downgrade or pin a package to hide an advisory without telling the user.
- A vulnerability without a fixed version is reported, not silently ignored.
