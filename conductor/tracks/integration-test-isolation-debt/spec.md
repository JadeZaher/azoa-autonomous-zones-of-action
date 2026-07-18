---
type: spec
track: integration-test-isolation-debt
created: 2026-07-06
status: active
horizon: alpha
depends_on: [final-hardening-cutover]
---

# integration-test-isolation-debt — make integration tests a CI gate

## Why

The historical ~31-failure tail was retired by per-class SurrealDB namespace
isolation plus the Bucket A–D fixes recorded in
`INTEGRATION-TEST-PASSOFF.md`. On 2026-07-11 the complete suite reached 312
passes, one intentional skip, and three newly exposed correctness failures. Each was then
reproduced and fixed: strict SurrealQL fee-CAS composition, fee-CAS conflict
normalization, and a durable-workflow drain race. Focused post-fix reruns are
green. A subsequent unfiltered Release run reached 314 passes and one skip, but
correctly demonstrated that the opt-in `WalletGetById_P99_Under50ms` benchmark
is not stable on developer hardware. Performance and hard-kill chaos categories
remain separately invocable evidence; neither is part of the routine correctness
gate. The remaining work is to make the correctness signal continuous in CI and
collect one complete filtered post-fix run before archive.

## Completed remediation (buckets from the pass-off, deduplicated)

1. **Namespace isolation** — each `WebApplicationFactory` owns a unique
   SurrealDB namespace, and the app plus direct setup clients use that same
   namespace. Methods within a class retain their deliberate shared-fixture
   semantics.
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

## Remaining scope

1. Observe the repository workflow boot the pinned SurrealDB 3.1.4 RocksDB
   container and pass the default correctness suite.
2. Keep the one intentional analyzer skip explicit; database readiness failure
   must fail the job rather than silently turning the suite into a pass.
3. Archive after the repository CI proof is recorded.

## Acceptance

- Default correctness suite (all non-chaos, non-performance classes in
  parallel) is green on a clean SurrealDB 3.1.4 container.
- `.github/workflows/ci.yml` boots that pinned database and runs the integration
  project on every push and pull request.
- A failed database readiness probe fails the job and prints container logs.
- The Windows/Podman hard-kill drill remains opt-in under `Category=Chaos`;
  machine-dependent latency budgets remain opt-in under `Category=Perf`;
  routine CI explicitly excludes both categories.
- Archive only after a complete local post-fix run and the repository CI check
  both pass.

## Verification log

- 2026-07-11, unfiltered Release: 314 passed, one intentional skip, one failed;
  the only failure was the documented opt-in wallet p99 performance budget.
- 2026-07-11, default correctness filter
  (`Category!=Chaos&Category!=Perf`): 310 passed, one intentional skip, zero
  failed in 23m55s.
- 2026-07-17, first repository CI execution on `29c75d6` reached 342 passed,
  one skip, and two failures. G5 targeted the local container name and the SDK
  lockfile was ignored; those deterministic CI preconditions and the
  test-host simulated-provider configuration were repaired before the green
  run below.
- 2026-07-18, GitHub Actions run `29630076208` for `96129bb` passed: SDK 15
  files/183 tests; unit 1,538 passed/one intentional skip; integration 344
  passed/one intentional skip on the pinned clean SurrealDB 3.1.4 container.
  Repository CI proof is complete; the track remains active only for its
  separate opt-in performance-budget evidence.
