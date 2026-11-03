# .github — CI notes

## Exact-c7 storage-maintenance publication branch

Only `codex/c7-storage-maintenance-image-20261102` replaces the registered
`conformance-release.yml` with a publication-only workflow. Do not merge this
replacement into main or PR3. Its job has no Railway credential, production
environment, deployment action, schema job, frontend image or provider call.
This maintenance job references no GitHub environment, so it cannot implicitly
create the absent `conformance-promotion` environment. Root/C explicitly reviewed
this publication-only boundary; the exact W review and authorized manual dispatch
are its gates. No server-side required-reviewer protection is claimed or altered.
The normal main release workflow and its promotion policy remain untouched.

The checked-out workflow revision W is reviewed separately from build source C.
BuildKit receives the hardcoded remote Git context at exact C=c7cfa4af, not the
tooling checkout. The GHA attestation binds W and the emitted image-index digest;
the index binds the platform manifest and maximum BuildKit provenance containing
C as an exact Git material. Never substitute an OCI revision label for this chain.

Publication is digest-only in this repository's fixed GHCR package. No mutable
tag is created or replaced, including on a workflow rerun. The evidence artifact
name includes W, publication run ID and attempt, with overwrite disabled. A
failed public-pull check does not grant permission to expose an existing private
package: investigate its ownership/association and review visibility separately.

Fresh CI is generated on `codex/c7-storage-validation-20261102`, whose ref points
to exact C without a new commit. CI signs this explicitly named branch, not main.
The publication workflow requires that new successful push run, attempt1, its
specific artifact ID and unexpired signed source metadata. The verified GHA
statement must name CI41461162520/attempts/1 and bind the exact bundle hash;
timestamps and run ID alone cannot exclude a later attempt. The old main artifact
is retained unchanged. The standard c7 Dockerfile is intentionally used because
it is the deployed API recipe; no conformance-release filesystem changes or
evidence directory are silently added. This image is a storage-maintenance
candidate and cannot be represented as the normal main-promotion release.

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
