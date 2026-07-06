---
type: spec
track: integration-test-isolation-debt
created: 2026-07-06
status: pending
horizon: post-launch
depends_on: [final-hardening-cutover]
---

# integration-test-isolation-debt — retire the full-suite failure tail

## Why

The full integration suite carries a ~31-failure tail (down from 37) that is
**attributed, not regressive**: shared-SurrealDB-container contention plus
documented pre-existing test-design conflicts. Every failing class passes when
run scoped — except the Bucket A design conflicts, which fail deterministically.
The tail makes the suite useless as a single green/red launch signal. Source of
truth: `conductor/tracks/integration-test-isolation-debt/INTEGRATION-TEST-PASSOFF.md`.

## Scope (buckets from the pass-off, deduplicated)

1. **Per-test namespace isolation** — per-class isolation (max granularity under
   `IClassFixture`) still shares data within a class and forces serial thinking.
   Rebuild the harness so each test method gets its own namespace, or serialize
   the collections that genuinely contend.
2. **Bucket A — IDOR test-design conflicts (~15 tests)** — tests seed a random
   avatar but the controller acts on the authenticated `DefaultAvatarId`
   (IDOR-resistant scoping working as designed). Fix per test: seed with the
   default identity or authenticate as the seeded avatar.
3. **Bucket B — env/repo-layout drift (5 tests)** — stale gate config paths
   (`podman-compose.yml` at root, container-name mismatch), machine-dependent
   perf budget (`WalletGetById_P99_Under50ms`).
4. **Bucket C — store-layer socket race (3 tests)** — pooled `IHttpClientFactory`
   instead of ephemeral clients, DB-visibility ordering, possible xUnit
   collection serialization for `SurrealPerfBudgets`.
5. **Bucket D — factory re-registration (2 tests)** — `TestScheme` collision from
   double `AddAuthentication().AddScheme()`; diagnose
   `Interact_ShouldRemovePeersAndMetadata` peer-removal wiring and the
   `Holon.{Mint,Exchange}` / `STARODK.Deploy` 400s.

## Acceptance

- Full suite (all classes, parallel) is green on a clean container; the suite
  becomes a usable CI gate (unblocks including integration in `.github/workflows/ci.yml`).
