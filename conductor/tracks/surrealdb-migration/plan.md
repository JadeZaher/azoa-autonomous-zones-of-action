# SurrealDB Migration — Plan

> **Amended 2026-05-21.** Wave-1 shipped (618/618 tests green, pass-off gate
> green). Strategic review identified blockers and the homebake-client lever.
> Tasks 4, 6, 8a (saga LIVE), 14 moved into [[surrealdb-client-package]].
> Tasks 1 + SDK-pin enforcement (was task 24) dissolve into the package work.
> Wave-1 portions of tasks 2, 3 are done. The blocker fixes (B1 durability,
> B2 Wormhole VAA correctness, B3 UNIQUE-on-nullable empirical verification)
> land in this track during wave-2 adapter work.

## Tasks

### Foundation — wave-1 status
1. ~~Pin `surrealdb.net` exact version + drift check~~ **Dissolved by
   [[surrealdb-client-package]] Phase 6** (replaces vendor SDK with
   `Oasis.SurrealDb.Client`; G4 pin moves to `Directory.Build.props`)
2. [x] **Wave-1 done.** SurrealDB local + test container; integration-test
   harness rebuilt (per-test `test{guid}` namespace isolation, HTTP-based
   seeding, no `EnsureDeleted` teardown). `Program.cs db.Database.Migrate()`
   removed. Harness compiles and runs against container when available.
   **Wave-2 follow-up:** rewire `IntegrationTestBase` to use the new
   `Oasis.SurrealDb.Client` ([[surrealdb-client-package]] Phase 6 task 37)
3. [x] **Wave-1 done for value tables** (`010_wallet`/`020_bridge_tx`/
   `030_swap_state`/`040_nft_ownership`/`050_operation_log`/
   `060_consumed_vaa_ledger`/`070_idempotency_key_store`). **Schema source for
   quest tables:** `conductor/tracks/quest-temporal-fork-model/SURREAL-SCHEMA-HINTS.md`
   (consume verbatim — tables, fields, and indexes; do not rederive).
   **Wave-2 follow-up:**
   (a) re-author as `.mermaid` sources ([[surrealdb-client-package]] Phase 6
   task 35), (b) apply **B1 durability fix** (compose URI →
   `surrealkv://data/oasis.db?sync=every` + boot self-check), (c) apply **B2
   Wormhole VAA index correctness** (drop wrong `(emitter_chain_id, sequence)`
   UNIQUE on `consumed_vaa_ledger`; ADD missing `(wormhole_emitter_chain_id,
   wormhole_emitter_address, wormhole_sequence)` UNIQUE on `bridge_tx`), (d)
   empirically verify **B3 UNIQUE-on-nullable** on running container; document
   in schema README; redesign adapter pattern if NULL collisions found.
   Quest tables still gated on [[quest-temporal-fork-model]]
4. ~~Parameterized SurrealQL query layer + lint gate (G3)~~ **Moved to
   [[surrealdb-client-package]] Phase 3 + Phase 5** (query builder lives in
   `Oasis.SurrealDb.Client`; analyzer SRDB0001 ships from
   `Oasis.SurrealDb.Analyzer`)

### Adapter behind the seam (wave-2 — REQUIRES [[surrealdb-client-package]] sub-wave 1.5a)
5. [ ] Implement SurrealDB adapters for every per-aggregate interface from
   `architecture-decoupling` (`IAvatarStore`/`IWalletStore`/`IHolonStore`/
   `IQuestStore`/`INftStore`/`IBridgeStore`) using `Oasis.SurrealDb.Client`.
   Quest portion gated on [[quest-temporal-fork-model]]
6. ~~Single-field conditional state transitions~~ **Dissolved by
   [[surrealdb-client-package]] Phase 3 task 14** — `.UpdateOnly(table, id)
   .Where(field, value).Set(field, value)` is the G2 primitive in the
   builder. This track's task: use it correctly in every adapter that
   touches `bridge_tx` / `operation_log` status fields
7. [ ] Port consumed-VAA ledger + idempotency-key store adapters to the new
   client using the package's `SurrealResponse` per-statement model (closes
   the C5 "multi-statement swallow" risk root). Preserve the existing
   `IConsumedVaaLedger` + `IIdempotencyKeyStore` interfaces from
   [[api-safety-hardening]]
8. [ ] Preserve chain reconciliation (G7) against SurrealDB-stored state
8a. ~~Replace polling `ISagaTrigger` with LIVE-query~~ **Moved to
    [[surrealdb-client-package]] Phase 8–10** (LIVE transport ships in
    sub-wave 1.5b; adoption pattern changes from "REPLACE polling" to
    `Trigger = Both` opt-in, polling stays default until 90-day soak)
8b. [ ] Port saga/outbox tables (`OutboxMessage`/`SagaStepRecord`) to
    `SCHEMAFULL` `.mermaid` sources; preserve claim-due-step conditional-
    transition semantics (G2) via the new builder primitive. Schema lives
    under `Persistence/SurrealDb/Schemas/source/`

### Graph remodel
9. [ ] **Schema source:** `conductor/tracks/quest-temporal-fork-model/SURREAL-SCHEMA-HINTS.md`
   (consume verbatim). Model quest nodes/edges via `RELATE` edges per that
   doc (definition `quest`/`quest_node`/`quest_edge`; runtime `quest_run` +
   `quest_node_execution` with `forked_from` + `executes` edges). Reimplement
   DAG validation (acyclicity within a single quest definition) using the
   `Oasis.SurrealDb.Client` builder's `.Relate()` + `.Fetch()` helpers or
   retain the iterative validator. **Gated on [[quest-temporal-fork-model]]
   hand-off**
10. [ ] Model holon polyhierarchy via graph edges; port query / propagate /
    compose / move-subtree using the package builder
11. [ ] Single authoritative `ExecutionOrder` (carry the
    `architecture-decoupling` fix forward; still a definition-side property
    per the fork-model split)

### Operations (guardrails — wave-2/3)
12. [ ] Deploy config: `surrealkv://data/oasis.db?sync=every` URI param +
    `Program.cs` boot self-check that refuses to start if sync != every
    (G1; this is **B1** from the strategic review, applied in `docker-
    compose.surrealdb.yml` and consumed by deployment manifests)
13. [ ] Scheduled `surreal export` backup job + documented, periodically-run
    restore drill (G5). Backup format trade-offs (no incremental, no PITR)
    documented; RTO target set
14. ~~Schema migration via gated job (`surrealdb-migrations`/`surrealkit`)~~
    **Replaced by [[surrealdb-client-package]] Phase 4** (`oasis-surreal
    migrate` CLI from `Oasis.SurrealDb.Schema` — original tools archived /
    immature)
15. [ ] Wire OpenTelemetry/metrics to the new `ISurrealExecutor` boundary
    (uses `architecture-decoupling` observability). Wave-2 additions from
    strategic review: query plan capture if exposed by server; slow-query
    log; index-use stats; connection-pool depth

### Strategic-review additions (wave-2)
A1. [ ] Performance budgets: p99 targets for wallet read, bridge_tx insert,
    holon traversal, saga step claim. Pre-cutover gate fails on regression
A2. [ ] Document SurrealDB transaction/isolation model for multi-statement
    bridge writes in `Persistence/SurrealDb/Schemas/README.md` (delivered
    through the package's transaction wrapper — [[surrealdb-client-package]]
    Phase 2 task 8 — this track documents the USAGE contract)
A3. [ ] Extend the `architecture-decoupling` persistence seam interfaces
    with LIVE-subscribe + RELATE-traverse shapes BEFORE wave-2 adopts them
    (avoids the seam leaking SurrealDB types into managers)
A6. [ ] Reserved-word denylist in `SurrealIdentifier.ForTable` (delivered in
    [[surrealdb-client-package]] Phase 3 task 12). This track's job: verify
    after the package lands
A7. [ ] `Skip.If(!IsSurrealDbAvailable, ...)` instead of `Task.CompletedTask`
    in integration tests stubbed during wave-1 (so they appear SKIPPED, not
    silently PASSED)
A8. [ ] Rewrite this spec.md's G4 rationale once [[surrealdb-client-package]]
    Phase 6 deletes the vendor SDK pin entirely — current G4 wording already
    updated, but tighten once the deletion lands
A10. [ ] G7 chain-reconciliation as chaos-tested CI gate — insert N ops,
    `docker kill -9`, restart, reconciliation re-derives status from chain
    RPC fixtures, assert truth matches. This is the actual insurance that
    makes the audit ledger fungible (analogical-persona Pattern A)

### Remove EF (wave-3)
16. [ ] Delete `OASISDbContext`, `EfStorageProvider`, `Migrations/`,
    `InMemoryStorageProvider`, Npgsql + EF Core packages
17. [ ] Remove `db.Database.Migrate()` path entirely (already removed from
    test harness in wave-1; final delete from `Program.cs` here)
18. [ ] Confirm zero EF/Npgsql references remain

### Pre-cutover gate — all must PASS (wave-3)
19. [ ] Crash/power-loss test green (G1+G7)
20. [ ] Idempotency/TOCTOU test green (G2)
21. [ ] Reconciliation drill green (G7)
22. [ ] Restore drill green (G5)
23. [ ] Injection suite green (G3) — uses `Oasis.SurrealDb.Analyzer`
24. ~~SDK-pin test green~~ **Replaced by**: build fails if
    `OasisSurrealDbVersion` in `Directory.Build.props` drifts from the
    version actually resolved by `Oasis.SurrealDb.Client` (G4)

### Verification
25. [ ] Port full test suite to the SurrealDB harness; all passing
26. [ ] `dotnet build` — zero warnings (≤17 baseline)
27. [ ] Sign-off: every guardrail G1–G7 demonstrably met (evidence linked)

## Outstanding strategic-review items dropped from this track
- **A4** (move pin literal to `Directory.Build.props`) — done implicitly by
  [[surrealdb-client-package]] Phase 1 task 4
- **A5** (analyzer on integration tests project) — done by
  [[surrealdb-client-package]] Phase 6 task 31 (analyzer is a
  `ProjectReference` from both production AND integration test csprojs)
- **A9** (Postgres CI shadow / 30-day exit ramp) — dropped; Postgres fully
  deprecated, no fallback ramp (decision 2026-05-21)
- **B4** (multi-statement swallow), **B5** (enum-as-int), **B6** (LIVE
  reliability), **B7** (archived migration tool), **B8** (server pin × SDK
  coupling) — all dissolved by [[surrealdb-client-package]]
