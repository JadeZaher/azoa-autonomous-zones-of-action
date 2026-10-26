# .github — CI notes

## §gitleaks — allowlist review rules

`.gitleaks.toml` (repo root) extends the default gitleaks rules; the CI job passes it with
`--config` for both the history and working-tree passes.

- Allowlist **exact secret values** (`regexTarget = "secret"`, anchored `^…$`), a **pin manifest** that by construction holds only public hashes, or a **history-only
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

The node-admin release range adds five exact reviewed values for seven historical
findings: two deterministic challenge-codec seeds used only in in-memory test
configuration, the published RFC 6238 secret represented in its expected base32
provisioning URI, and two Ed25519 public keys used with a fake Railway executable
and spawn. The public-key values contain no private signing material. Their
anchored value entries preserve every default rule and add no path exclusion.

## §schema-upgrade — the in-place upgrade check (RV-L1-02)

Goldens are `DEFINE … IF NOT EXISTS` and the deploy path runs `surrealforge up --no-reconcile`, so an edit to an
existing definition is never applied implicitly; every other CI database starts empty and cannot notice. The schema
job's "Upgrade from main converges" step applies `origin/main`'s composed set + migrations to `azoa_upgrade`, then
this tree's, and compares every table against `Generated/Schemas` (`scripts/schema_snapshot_diff.py`, shared with
the deploy-path convergence step). It fails on a checksum abort or any difference.

A deliberate break is declared, not hidden: bump `Persistence/SurrealDb/SCHEMA_GENERATION` (D-9 generation reset;
the owner resets the Railway SurrealDB volume before that deploy). Different generations on `main` and the branch
skip the check with a notice. Otherwise the fix is a forward migration whose `DEFINE`s use `OVERWRITE`
(`SurrealQlSourceRulesTests.Forward_migrations_change_definitions_with_OVERWRITE`; shipped migrations are exempt
because their bytes are checksum-ledgered). Generation 2 is the round-4 reset.

### Review 2026-10-26 (CI run 40263034250)

Five history-only commits allowlisted (`[allowlist].commits`): QA LiveTests
session recordings (`tests/AZOA.WebAPI.LiveTests/live-tests/*.jsonl`, legacy
`OASIS.WebAPI.*` paths) that captured test-session API keys minted against
throwaway local dev nodes. Files gone or superseded at HEAD; keys dead by
construction (node-local, short-lived). Commit-scoped so any NEW leak in the
same paths still fails the scan.
