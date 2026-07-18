---
type: plan
track: integration-test-isolation-debt
status: active
updated: 2026-07-17
---

# Integration-test CI gate plan

- [x] Give every test factory a unique SurrealDB namespace shared by its app
  host and direct setup clients.
- [x] Retire the historical Bucket A–D failure tail.
- [x] Fix the three failures exposed by the 2026-07-11 full-suite run.
- [x] Add pinned SurrealDB 3.1.4 RocksDB startup, readiness diagnostics, and the
  non-chaos integration project to `.github/workflows/ci.yml`.
- [x] Diagnose the unfiltered post-fix run: 314 passed, one skipped, and only
  the documented machine-dependent wallet p99 performance budget failed.
- [x] Run the complete default correctness suite after the final fixes:
  `310 passed, 1 intentional skip, 0 failed` in Release on 2026-07-11
  (`Category!=Chaos&Category!=Perf`, 23m55s).
- [x] Repair the first repository-CI run's deterministic preconditions: commit
  the SDK lockfile required by `npm ci`, and pass CI's `azoa-ci-surrealdb`
  container name explicitly to the G5 backup/restore drill while keeping its
  local default.
- [x] Supply the explicit `Simulated/Devnet` provider configuration needed by
  the Live-mode integration host's treasury-governance test; production and
  the default Live chain remain unchanged.
- [ ] Keep the local-only `Category=Perf` budgets strict and isolate the wallet
  read p99 before archive. A quiet rerun on 2026-07-12 passed saga due-scan and
  bridge insert, but `WalletGetById` measured 59.3 ms against its 50 ms budget;
  the direct-record query path needs environment or transport evidence before
  any change to the budget is considered.
- [x] Observe a green repository CI check on the workflow change: GitHub Actions
  run `29630076208` for `96129bb` passed the SDK's 15 files/183 tests, 1,538
  unit tests with one intentional skip, and 344 integration tests with one
  intentional skip on a clean SurrealDB 3.1.4 container.
- [ ] Diagnose and repair the remaining CI-only
  `PutTreasuryDestination_AsNodeGovernor_PersistsGetRoundTripsAndAudits` 500;
  local reproduction is blocked until the project SurrealDB owns port 8000.
- [ ] Archive the track only after both verification items are checked.
