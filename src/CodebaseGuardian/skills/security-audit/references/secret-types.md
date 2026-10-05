# Secret types

Rule ids reported by `scan_secrets` and `security.secret_detected`, what each grants, and what to check.

| `ruleId` | What it is | Impact if real | Typical false positive |
|---|---|---|---|
| `aws-access-key-id` | AWS access key id (starts with a fixed AKIA or ASIA prefix) | Identifies an AWS identity; with its secret key it grants API access | Example ids in documentation |
| `github-token` | GitHub personal, OAuth or app token | Repository access as the token owner | Revoked tokens in old test data |
| `github-fine-grained-pat` | GitHub fine-grained personal access token | Access limited to the repositories and permissions chosen | Placeholder strings in docs |
| `slack-token` | Slack bot, user or app token | Read and post in the workspace | Redacted examples in docs |
| `stripe-live-key` | Stripe live-mode secret key | Charges, refunds and customer data | Test-mode keys are not matched |
| `private-key` | PEM private key block | Impersonation or decryption, depending on use | Public test keys that are deliberately committed |
| `jwt` | JSON Web Token | Session or API access until it expires | Sample tokens in tests, expired tokens |
| `generic-secret-assignment` | A password, secret or token assigned a literal value | Depends on what the value unlocks | Configuration templates, sample values such as the word changeme |

## Reading a finding

1. Strong prefixes (`aws-access-key-id`, `github-token`, `github-fine-grained-pat`, `slack-token`, `stripe-live-key`) are rarely false positives. Assume real.
2. `private-key` is real unless the file is clearly a public test fixture.
3. `jwt` may be harmless if it is expired and unsigned; check what it was issued for before dismissing it.
4. `generic-secret-assignment` is the noisiest rule. Look at the surrounding code before deciding.
