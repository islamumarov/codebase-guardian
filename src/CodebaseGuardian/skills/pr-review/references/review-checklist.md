# Review checklist

## Correctness

- [ ] The change does what its subject says, and nothing else important.
- [ ] Edge cases are handled: empty input, null, large input, concurrency, errors.
- [ ] Errors are not swallowed; failure paths leave a consistent state.
- [ ] No leftover debugging code, commented-out blocks or unfinished markers.

## Tests

- [ ] New behaviour has a test; a bug fix has a test that fails without the fix.
- [ ] Tests check behaviour, not implementation details.
- [ ] Existing tests were not weakened or deleted to make the change pass.

## Security

- [ ] `scan_secrets` with `scope:"commit"` reported nothing, or every finding is handled.
- [ ] Input from outside is validated before it reaches a command, query, path or template.
- [ ] No new permissions, open ports or disabled checks without a reason.
- [ ] New dependencies are justified; see the `dependency-hygiene` skill.

## Naming and design

- [ ] Names say what things are and do.
- [ ] No duplicated logic that an existing helper already covers.
- [ ] The change is small enough to understand; large mixed commits are flagged.

## Documentation

- [ ] Public behaviour changes are reflected in the README or docs.
- [ ] Comments explain why, not what.
- [ ] The commit message explains the reason for the change.
