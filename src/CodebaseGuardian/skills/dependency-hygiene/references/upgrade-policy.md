# Upgrade policy

| Situation | Action |
|---|---|
| Vulnerable, fix in a patch or minor release | Upgrade now, then run the checks |
| Vulnerable, fix only in a major release | Upgrade now if the advisory is critical or high; read the changelog and expect code changes |
| Vulnerable, no fixed version | Report; suggest removing or replacing the package, or a documented mitigation |
| Outdated, patch release | Safe to batch with other patch upgrades |
| Outdated, minor release | Upgrade in a group; skim the release notes |
| Outdated, major release | Separate change per package; read the migration guide; schedule rather than rush |

## Order

1. Critical and high vulnerabilities.
2. Moderate and low vulnerabilities.
3. Outdated patch and minor versions.
4. Outdated major versions.

## Verification

After every group call `run_checks`. A green run is the minimum; a failing run means revert that group and report it. Prefer lockfile-only changes for transitive vulnerabilities when the ecosystem supports them.
