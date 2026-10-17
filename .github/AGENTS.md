# .github — CI notes

## §gitleaks — allowlist review rules

`.gitleaks.toml` (repo root) extends the default gitleaks rules; the CI job passes it with
`--config` for both the history and working-tree passes.

- Allowlist **exact secret values** (`regexTarget = "secret"`, anchored `^…$`) or a **history-only
  file** that no longer exists in the tree. Never a directory, never a rule id, never a glob over
  source — a blanket path hides the next real credential that lands there.
- Each entry carries a one-line reason: published test vector, public on-chain address,
  self-describing fixture key, local-only container default, or prose that matched
  `generic-api-key`.
- Before adding an entry, decode/inspect the value: a JWT gets its issuer and `exp` checked; a
  key gets confirmed as a published vector or a test-only literal. A real credential is rotated
  and purged, not allowlisted.
- Reviewed set (round 4 L6, from CI run 39051444396 plus a local full-history scan):
  83 history / 23 tree findings, all false positives. `tests/live-test-results.md` (deleted in
  `0726c0f9`) held 50 JWTs from a local 2026-06 run: retired issuer `OASIS.WebAPI`, dev key,
  all expired 2026-06-09 — allowlisted by that exact path.
- Mutation check: a scan of a file holding a live-format Stripe key still reports a leak with the
  config applied.
