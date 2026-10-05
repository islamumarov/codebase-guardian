# Remediation order

Do these in order. Stopping after step 2 is already a large improvement; step 4 is optional.

1. Revoke and rotate. Ask the user to revoke the credential at its provider and issue a new one. Anyone who cloned, fetched or saw the commit may have the old value, so removing it from the repository does not make it safe again.
2. Replace in code. Read the new value from an environment variable, a secret store or an untracked configuration file. Commit that change normally.
3. Check for other copies. Run `scan_secrets` with `scope` set to `working_tree`, and with `scope` set to `commit` for the commits that touched the file, to find copies of the same credential.
4. Remove from history, only with the user's consent. Rewriting history changes the hash of every commit after the secret. That breaks open pull requests, forces every collaborator to re-clone or reset, invalidates signatures and requires a force-push, which can overwrite work that others pushed in the meantime. It also does not help with clones that already exist. Offer it as an option, explain these costs, and prefer it only for a secret that has been revoked already or for a repository that was never shared.
5. Prevent a repeat. Suggest a pre-commit scan, and a `.guardianignore` entry only for confirmed false positives.

If the user agrees to rewrite history, give them the exact commands to run and let them run them. Do not run them for them.
