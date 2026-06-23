# SurrealDB Migration — Plan

> **Amended 2026-05-21.** Wave-1 shipped (618/618 tests green, pass-off gate
> green). Strategic review identified blockers and the homebake-client lever.
> Tasks 4, 6, 8a (saga LIVE), 14 moved into [[surrealdb-client-package]].
> Tasks 1 + SDK-pin enforcement (was task 24) dissolve into the package work.
> Wave-1 portions of tasks 2, 3 are done. The blocker fixes (B1 durability,
> B2 Wormhole VAA correctness, B3 UNIQUE-on-nullable empirical verification)
> land in this track during wave-2 adapter work.
>
> **Amended 2026-05-22.** First wave-2 ultrapilot run landed the 4 aggregates
> with generated SurrealDB POCOs (Wallet, NFT, BlockchainOperation, Bridge)
> plus the Idempotency and Saga stores. Tasks 7 and 8b done; 5, 8 partial.
> The remaining wave-2/wave-3 work is sequenced in
> [CLOSEOUT-RUNBOOK.md](CLOSEOUT-RUNBOOK.md) (5 parallel-friendly ultrapilot
> streams: A=Avatar/Holon/STAR adapters, B=ops guardrails, C=service refactor
> onto IBridgeStore, D=EF deletion, E=cutover gates + sign-off).

## Tasks

### Foundation — wave-1 status
1. ~~Pin `surrealdb.net` exact version + drift check~~ **Dissolved by
   [[surrealdb-client-package]] Phase 6** (replaces vendor SDK with
   `Azoa.SurrealDb.Client`; G4 pin moves to `Directory.Build.props`)
2. [x] **Wave-1 done.** SurrealDB local + test container; integration-test
   harness rebuilt (per-test `test{guid}` namespace isolation, HTTP-based
   seeding, no `EnsureDeleted` teardown). `Program.cs db.Database.Migrate()`
   removed. Harness compiles and runs against container when available.
   **Wave-2 follow-up:** rewire `IntegrationTestBase` to use the new
   `Azoa.SurrealDb.Client` ([[surrealdb-client-package]] Phase 6 task 37)
3. [x] **Wave-1 done for value tables** (`010_wallet`/`020_bridge_tx`/
   `030_swap_state`/`040_nft_ownership`/`050_operation_log`/
   `060_consumed_vaa_ledger`/`070_idempotency_key_store`). **Schema source for
   quest tables:** `conductor/tracks/quest-temporal-fork-model/SURREAL-SCHEMA-HINTS.md`
   (consume verbatim — tables, fields, and indexes; do not rederive).
   **Wave-2 follow-up:**
   (a) re-author as `.mermaid` sources ([[surrealdb-client-package]] Phase 6
   task 35), (b) apply **B1 durability fix** (compose URI →
   `surrealkv://data/azoa.db?sync=every` + boot self-check), (c) apply **B2
   Wormhole VAA index correctness** (drop wrong `(emitter_chain_id, sequence)`
   UNIQUE on `consumed_vaa_ledger`; ADD missing `(wormhole_emitter_chain_id,
   wormhole_emitter_address, wormhole_sequence)` UNIQUE on `bridge_tx`), (d)
   empirically verify **B3 UNIQUE-on-nullable** on running container; document
   in schema README; redesign adapter pattern if NULL collisions found.
   Quest tables still gated on [[quest-temporal-fork-model]]
4. ~~Parameterized SurrealQL query layer + lint gate (G3)~~ **Moved to
   [[surrealdb-client-package]] Phase 3 + Phase 5** (query builder lives in
   `Azoa.SurrealDb.Client`; analyzer SRDB0001 ships from
   `Azoa.SurrealDb.Analyzer`)

### Adapter behind the seam (wave-2 — REQUIRES [[surrealdb-client-package]] sub-wave 1.5a)
5. [~] **Partial — 7 of 8 in-scope stores done (2026-05-22).** Wave-2 round 1:
   Wallet, NFT, BlockchainOperation, Bridge SurrealDB adapters landed under
   `Providers/Stores/Surreal/` against the generated POCOs at
   `AZOA.WebAPI.Generated.SurrealDb.*`. Wave-2 round 2 (CLOSEOUT-RUNBOOK
   Stream A, same day): Avatar, Holon, STAR landed with hand-authored
   090/100/110 `.mermaid` + `.surql` and inline POCOs (mark "replace with
   generated POCO when source-gen catches up"). DI flipped in
   `Program.cs:244-262` for all 7. **Remaining:** Quest portion still gated
   on [[quest-temporal-fork-model]]
6. ~~Single-field conditional state transitions~~ **Dissolved by
   [[surrealdb-client-package]] Phase 3 task 14** — `.UpdateOnly(table, id)
   .Where(field, value).Set(field, value)` is the G2 primitive in the
   builder. This track's task: use it correctly in every adapter that
   touches `bridge_tx` / `operation_log` status fields. **Done in wave-2
   (2026-05-21).** `SurrealBridgeStore.TryTransitionBridgeStatusAsync` +
   `SurrealBridgeStore.TryTransitionOperationStatusAsync` +
   `SurrealBlockchainOperationStore.TryTransitionStatusAsync` +
   `SurrealSagaStore.TryClaimDueStepAsync` all route through the primitive
   (or its parameterized `SurrealQuery.Of` equivalent where multi-field
   SETs are required); each returns `AffectedCount()` verbatim
7. [x] **Done (2026-05-21).** `SurrealIdempotencyStore` ports the EF
   `IdempotencyStore` to SurrealDB with deterministic SHA-256(key) record
   ids; closes the C5 "multi-statement swallow" risk by inspecting
   `response[i].IsOk` per statement. Consumed-VAA portion folded into
   `SurrealBridgeStore.TryInsertConsumedVaaAsync` (UNIQUE on digest + on
   `(emitter_chain_id, emitter_address, sequence)` triple — closes the B2
   gap). `IConsumedVaaLedger` was never a separate interface; the dedup
   lives in `IBridgeStore`
8. [~] **Partial (2026-05-21).** Adapter seam ready: `SurrealBridgeStore` +
   `SurrealIdempotencyStore` match the EF semantics byte-for-byte (proven
   by the wave-2 integration test suite). **Remaining:**
   `ReconciliationService` and `CrossChainBridgeService` still inject
   `AZOADbContext` directly — refactor onto `IBridgeStore` +
   `IIdempotencyStore` in [CLOSEOUT-RUNBOOK.md](CLOSEOUT-RUNBOOK.md)
   Stream C so the wave-3 storage flip is a one-line DI change
8a. ~~Replace polling `ISagaTrigger` with LIVE-query~~ **Moved to
    [[surrealdb-client-package]] Phase 8–10** (LIVE transport ships in
    sub-wave 1.5b; adoption pattern changes from "REPLACE polling" to
    `Trigger = Both` opt-in, polling stays default until 90-day soak)
8b. [x] **Done (2026-05-21).** `Persistence/SurrealDb/Schemas/source/
    080_saga_steps.mermaid` (SCHEMAFULL, 16 fields, 4 indexes) + the
    generated `.surql` shipped. `SurrealSagaStore` preserves the G2
    claim-due-step single-winner contract via a parameterized
    conditional UPDATE (`WHERE id == $id AND status == 'Pending' AND
    next_run_at <= $now` → SET → `AffectedCount() == 1`).
    `OutboxMessage` is NOT a separate model — `SagaStepRecord` is the
    unified outbox table

### Graph remodel
9. [x] **Done (2026-05-24, Task 9 close-out).** Three SurrealDB-backed stores landed (`Providers/Stores/Surreal/SurrealQuestStore.cs` 538 LOC, `SurrealQuestRunStore.cs` 322 LOC, `SurrealQuestNodeExecutionStore.cs` 352 LOC) on six schema files (150_quest, 160_quest_node, 170_quest_edge, 190_quest_run, 200_quest_node_execution, 230_quest_graph_edges with `forked_from` + `executes` RELATION tables). G2 single-winner claim primitive at `SurrealQuestNodeExecutionStore.cs:280-307`. Fork write-pairing via BEGIN/COMMIT at `SurrealQuestRunStore.cs:88`. DI flipped at `Program.cs:267-298`. Build clean; unit suite 535/535. 28 integration tests authored (runtime-gated on E1 image fix). See `SIGN-OFF.md` "Post-Stream-E residual close-out -- Task 9 (2026-05-24)". Schema source: `conductor/tracks/quest-temporal-fork-model/SURREAL-SCHEMA-HINTS.md`
   (consume verbatim). Model quest nodes/edges via `RELATE` edges per that
   doc (definition `quest`/`quest_node`/`quest_edge`; runtime `quest_run` +
   `quest_node_execution` with `forked_from` + `executes` edges). Reimplement
   DAG validation (acyclicity within a single quest definition) using the
   `Azoa.SurrealDb.Client` builder's `.Relate()` + `.Fetch()` helpers or
   retain the iterative validator. **Gated on [[quest-temporal-fork-model]]
   hand-off**
10. [ ] Model holon polyhierarchy via graph edges; port query / propagate /
    compose / move-subtree using the package builder
11. [ ] Single authoritative `ExecutionOrder` (carry the
    `architecture-decoupling` fix forward; still a definition-side property
    per the fork-model split)

### Operations (guardrails — wave-2/3)
12. [x] **Done (2026-05-22, CLOSEOUT Stream B W1).** B1 durability landed in
    `docker-compose.surrealdb.yml:42` (`surrealkv://data/azoa.db?sync=every`
    on the `surreal start` command line). `Program.cs` boot self-check
    refuses to start unless (a) `SurrealDb:G1DurabilityAcknowledged=true` is
    set in configuration (operator audit-trail ack that compose was reviewed
    — SurrealDB 1.5.x exposes no SQL surface to read fsync mode back from
    the server at runtime), and (b) the server is reachable via `SELECT 1`
    through the same `ISurrealExecutor` the rest of the app uses
    (`Program.cs` boot block). `IntegrationTest` environment skips both
    checks (test container is brought up per-test by the harness)
13. [~] **Partial — backup/restore scripts + RTO doc landed (2026-05-22,
    CLOSEOUT Stream B W4).** `scripts/surrealdb/backup.ps1` and
    `restore.ps1` wrap `surreal export` / `surreal import` via `docker exec`
    against the `azoa-surrealdb` container; `$ErrorActionPreference=Stop`,
    `SURREAL_ROOT_PASS` env-var override, exit-code preserved, restore
    confirms before replaying unless `-Force`. RTO target (15 min) +
    trade-off acknowledgement (no incremental, no PITR) documented in
    `Persistence/SurrealDb/Schemas/README.md`. **Remaining for full close:**
    scheduled backup job (CI cron) + periodically-run restore drill — sequenced
    into Stream E G5 gate (plan.md task 22)
14. ~~Schema migration via gated job (`surrealdb-migrations`/`surrealkit`)~~
    **Replaced by [[surrealdb-client-package]] Phase 4** (`azoa-surreal
    migrate` CLI from `Azoa.SurrealDb.Schema` — original tools archived /
    immature)
15. [x] **Done (2026-05-22, CLOSEOUT Stream B W2).** OTEL decorator landed in
    `Observability/InstrumentedSurrealExecutor.cs` wrapping the package's
    `DefaultSurrealExecutor` via a true decorator (the existing DI descriptor
    is removed and re-registered through `ActivatorUtilities` so there's no
    dangling registration). `ActivitySource` and `Meter` both named
    `Azoa.SurrealDb` (matches package namespace). Emits `surrealdb.queries`
    + `surrealdb.errors` counters and a `surrealdb.duration_ms` histogram
    tagged with `statement_kind` (SELECT/INSERT/UPDATE/DELETE/CREATE/RELATE/
    INFO/OTHER/MULTI) and `table`. Error path still records duration +
    queries-counter so dashboards don't look like the query disappeared.
    The homebake package stays observability-agnostic (decorator lives in
    `AZOA.WebAPI`, not the package). **Deferred to a follow-up:** query
    plan capture, slow-query log, index-use stats, connection-pool depth —
    none are exposed by SurrealDB 1.5.x via the SDK surface yet

### Strategic-review additions (wave-2)
A1. [~] **Partial — three of four budgets landed (2026-05-22, CLOSEOUT
    Stream B W3).** `tests/AZOA.WebAPI.IntegrationTests/Perf/
    SurrealPerfBudgets.cs` asserts `Wallet GetById p99 < 50ms`, `BridgeTx
    insert p99 < 100ms`, `SagaSteps due-scan p99 < 200ms` — each over 100
    iterations, FluentAssertions for the budget assertion, linear-interpolation
    percentile helper. Class is `[Trait("Category","Perf")]` so default CI
    (`--filter "Category!=Perf"`) excludes it; tests use `[SkippableFact]`
    + `Skip.IfNot(_surrealAvailable, ...)` matching the wave-2 store-test
    pattern; namespace isolation via `test{guid}`. **Remaining:** holon
    traversal budget — blocked on the Avatar/Holon/STAR adapters from
    CLOSEOUT Stream A, follow-up after that lands
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
A7. [x] **Done (2026-05-22).** Wave-2 integration tests
    (`tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/*Tests.cs`)
    use `[SkippableFact]` + `Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
    ...)` so a missing container produces SKIPPED, not silently PASSED.
    `Xunit.SkippableFact` wired into the integration tests csproj
A8. [ ] Rewrite this spec.md's G4 rationale once [[surrealdb-client-package]]
    Phase 6 deletes the vendor SDK pin entirely — current G4 wording already
    updated, but tighten once the deletion lands
A10. [ ] G7 chain-reconciliation as chaos-tested CI gate — insert N ops,
    `docker kill -9`, restart, reconciliation re-derives status from chain
    RPC fixtures, assert truth matches. This is the actual insurance that
    makes the audit ledger fungible (analogical-persona Pattern A)

### Remove EF (wave-3)
16. [x] **Done (2026-05-22, CLOSEOUT Stream D).** `Data/` (containing
    `AZOADbContext.cs` + `SeedData.cs`), `Migrations/` (all 5 migration
    files), every `Providers/Stores/Ef*Store.cs` (Avatar, Wallet, Holon,
    BlockchainOperation, STAR, Quest, NFT, Bridge, QuestRunStore stub,
    QuestNodeExecutionStore stub), `Core/Idempotency/IdempotencyStore.cs`
    (EF impl), and `Services/Sagas/EfSagaStore.cs` deleted. The
    `Npgsql.EntityFrameworkCore.PostgreSQL` + `Microsoft.EntityFrameworkCore.Design`
    package refs dropped from `AZOA.WebAPI.csproj`; the
    `Microsoft.EntityFrameworkCore.InMemory` + `Microsoft.EntityFrameworkCore.Sqlite`
    refs dropped from `AZOA.WebAPI.Tests.csproj`. Pre-cleared by **Stream
    C2** (ApiKey + QuestTemplate seams + Surreal adapters — closed the
    pre-flight gate's `AZOADbContext` injection sites) and **Stream D0**
    (test rewiring: 6 EF-bound test files deleted; ReconciliationServiceTests
    + CrossChainBridgeServiceTests rewired onto in-memory FakeBridgeStore +
    FakeIdempotencyStore preserving every test name + assertion verbatim).
    `IQuestStore` (instantiated-Quest CRUD) sits on a new
    `InMemoryQuestStore` transition adapter pending the
    [[quest-temporal-fork-model]] runtime store landing.
17. [x] **Done (2026-05-22, CLOSEOUT Stream D).** The `db.Database.Migrate()
    + SeedData.SeedAsync(db)` block in `Program.cs` deleted along with the
    `using Microsoft.EntityFrameworkCore;` + `using AZOA.WebAPI.Data;`
    imports, the `AddDbContext<AZOADbContext>` registration, the
    `ConnectionStrings:AZOADatabase` config entry, and the
    `AZOA:DefaultProvider` / `AZOA:FailOverMode` config keys.
    `Observability/StorageHealthCheck.cs` rewritten to probe SurrealDB
    via `ISurrealExecutor` (`RETURN 1`) instead of `AZOADbContext`.
18. [x] **Done (2026-05-22, CLOSEOUT Stream D).** Solution-wide grep for
    `Npgsql|EntityFramework` in `.cs` and `.csproj` files returns empty
    after the wave-3 deletion. Production-code grep for `AZOADbContext`
    is empty (modulo a few `// no AZOADbContext` self-referential
    descriptions in integration-test harness XML-doc comments — kept
    because the description "NO EF DEPENDENCIES" is still load-bearing
    for harness intent). `dotnet build azoa.sln` → 0 errors, 19
    warnings (baseline); `dotnet test tests/AZOA.WebAPI.Tests` → 523/523
    pass.

### Pre-cutover gate — all must PASS (wave-3)
19. [x] **Done (2026-05-22, CLOSEOUT Stream E).** G1 evidence: `tests/AZOA.WebAPI.IntegrationTests/Gates/G1_CrashDurabilityTest.cs:97` (real SIGKILL + restart + 40-row durability assertion) and `tests/AZOA.WebAPI.IntegrationTests/Gates/G1_CrashDurabilityTest.cs:193` (static sync=every config gate). G7 evidence shared with task 21. See [SIGN-OFF.md](SIGN-OFF.md).
20. [x] **Done (2026-05-22, CLOSEOUT Stream E).** Evidence: `tests/AZOA.WebAPI.IntegrationTests/Gates/G2_IdempotencyTocTouTest.cs:69` (50 concurrent redeems through real SurrealIdempotencyStore + SurrealBridgeStore, exactly one chain effect) and `tests/AZOA.WebAPI.IntegrationTests/Gates/G2_IdempotencyTocTouTest.cs:277` (20 concurrent TryClaimDueStepAsync, exactly one winner). See [SIGN-OFF.md](SIGN-OFF.md).
21. [x] **Done (2026-05-22, CLOSEOUT Stream E).** Evidence: `tests/AZOA.WebAPI.IntegrationTests/Gates/G7_ReconciliationDrillTest.cs:44` (orphaned Redeeming row + orphaned InProgress idempotency, single ReconcileBridgeAsync pass with truth-providing stub, row converges to Completed; idempotent re-run scans nothing). See [SIGN-OFF.md](SIGN-OFF.md).
22. [x] **Done (2026-05-22, CLOSEOUT Stream E).** Evidence: `tests/AZOA.WebAPI.IntegrationTests/Gates/G5_RestoreDrillTest.cs:122` (13-table seed -> backup.ps1 -> REMOVE NAMESPACE -> restore.ps1 -> SHA-256 round-trip byte-equal). P1 residual F1 (backup/restore.ps1 docker-vs-podman gap) closed same day -- both scripts now auto-detect docker (preferred) or podman via `Find-ContainerRuntime`. See [SIGN-OFF.md](SIGN-OFF.md).
23. [x] **Done (2026-05-22, CLOSEOUT Stream E).** Evidence: `tests/AZOA.WebAPI.IntegrationTests/Gates/G3_InjectionSuiteTest.cs:100` (hostile input through 4 controller paths, wallet row-count invariant) + `tests/AZOA.WebAPI.IntegrationTests/Gates/G3_InjectionSuiteTest.cs:201` (positive WithParam byte-exact round-trip) + `tests/AZOA.WebAPI.IntegrationTests/Gates/G3_InjectionSuiteTest.cs:280` (live `dotnet build` SRDB0001 analyzer fixture). See [SIGN-OFF.md](SIGN-OFF.md).
24. ~~SDK-pin test green~~ **Replaced by**: build fails if
    `AzoaSurrealDbVersion` in `Directory.Build.props` drifts from the
    version actually resolved by `Azoa.SurrealDb.Client` (G4)

### Verification
25. [x] **Done (2026-05-22, CLOSEOUT Stream E).** Integration test harness rebuilt around per-test SurrealDB namespace isolation (`IntegrationTestBase.cs`, `test{guid}` namespace, REMOVE NAMESPACE teardown). The 5 gate tests + the wave-2 Persistence/Surreal/*Tests.cs + Perf/SurrealPerfBudgets.cs all run against the real SurrealDB container via [SkippableFact]+SkipIfSurrealDbUnavailableAsync. Unit suite (523/523) green via `dotnet test tests/AZOA.WebAPI.Tests` per plan.md task 18.
26. [x] **Done (2026-05-22, CLOSEOUT Stream E).** `dotnet build azoa.sln` returns 0 errors / 19 warnings (within baseline tolerance per plan.md task 18).
27. [x] **Done (2026-05-22, CLOSEOUT Stream E).** [SIGN-OFF.md](SIGN-OFF.md) committed. All 7 guardrails (G1-G7) demonstrably met with evidence file:line pointers. Post-F1-close, only G1 + G7 carry documented PASS-WITH-RESIDUAL notes (intrinsic SurrealDB 1.5.x fsync-introspection limit; SIGKILL-vs-model crash trade-off where G1 owns the literal SIGKILL evidence and G7 owns the recovery contract). api-safety-hardening section 4 cross-check: zero regressions, one resolved (integration test harness), one architecturally N/A (EF migration baseline).

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
