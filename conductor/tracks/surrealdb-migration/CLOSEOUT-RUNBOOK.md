> **Stream E COMPLETE 2026-05-22 -- track CLOSED; this runbook is archive material.** All 5 pre-cutover gates (G1, G2, G3, G5, G7) passed via the 5 gate tests under tests/AZOA.WebAPI.IntegrationTests/Gates/; SIGN-OFF.md committed; plan.md tasks 19-23 + 25-27 ticked. See [SIGN-OFF.md](SIGN-OFF.md).

# SurrealDB Migration — Close-Out Runbook

> **Purpose.** Parallel-execution playbook to finish the remaining
> wave-2 work, ship wave-3 (EF deletion), and clear the pre-cutover
> gates. Designed for multi-instance `/ultrapilot` execution with
> haiku/sonnet workers and an opus code-review-and-fix pass per
> stream.
>
> **Companion docs.**
> [`spec.md`](spec.md) (goals + guardrails G1–G7),
> [`plan.md`](plan.md) (canonical task list),
> [`api-safety-hardening/RESIDUAL-RISK-RUNBOOK.md`](../api-safety-hardening/RESIDUAL-RISK-RUNBOOK.md)
> (gating items from the predecessor track that must remain green
> post-migration).

## Track dependencies (read first)

```
architecture-decoupling      ──┐
api-safety-hardening         ──┤
surrealdb-client-package     ──┼──►  surrealdb-migration  ──►  mcp-surface
surrealdb-schema-source-gen  ──┘            ▲
                                            │
quest-temporal-fork-model    ───────────────┘  (gates tasks 9, 10, 11)
```

- **Upstream (shipped):** [`architecture-decoupling`](../architecture-decoupling)
  (per-aggregate `IXxxStore` seam),
  [`api-safety-hardening`](../api-safety-hardening) (`IIdempotencyStore`,
  reconciliation, conditional-update primitives),
  [`surrealdb-client-package`](../surrealdb-client-package) (homebake
  `Azoa.SurrealDb.Client` + analyzer + Mermaid→`.surql` source-gen),
  [`surrealdb-schema-source-gen`](../surrealdb-schema-source-gen)
  (POCO source-gen).
- **Upstream (in-flight):** [`quest-temporal-fork-model`](../quest-temporal-fork-model)
  — owns the runtime/definition split (`Quest`/`QuestNode` vs.
  `QuestRun`/`QuestNodeExecution`). Gates [`plan.md`](plan.md) tasks
  9, 10, 11 and the `IQuestStore` portion of task 5.
- **Downstream (blocked):** [`mcp-surface`](../mcp-surface) — blocked on
  this track's completion.

## Current state — what just shipped (wave-2 adapters)

The first wave-2 ultrapilot run landed the four aggregates that have a
generated SurrealDB POCO under `AZOA.WebAPI.Generated.SurrealDb.*`:

| Aggregate | Store | Schema | POCO |
|---|---|---|---|
| Wallet | [`Providers/Stores/Surreal/SurrealWalletStore.cs`](../../../Providers/Stores/Surreal/SurrealWalletStore.cs) | `010_wallet` | `Wallet` |
| NFT | [`Providers/Stores/Surreal/SurrealNftStore.cs`](../../../Providers/Stores/Surreal/SurrealNftStore.cs) | `040_nft_ownership` + inline | `NftOwnership` |
| BlockchainOperation | [`Providers/Stores/Surreal/SurrealBlockchainOperationStore.cs`](../../../Providers/Stores/Surreal/SurrealBlockchainOperationStore.cs) | `050_operation_log` | `OperationLog` |
| Bridge | [`Providers/Stores/Surreal/SurrealBridgeStore.cs`](../../../Providers/Stores/Surreal/SurrealBridgeStore.cs) | `020_bridge_tx` + `060_consumed_vaa_ledger` | `BridgeTx`, `ConsumedVaaLedger`, `OperationLog` |
| Idempotency | [`Core/Idempotency/SurrealIdempotencyStore.cs`](../../../Core/Idempotency/SurrealIdempotencyStore.cs) | `070_idempotency_key_store` | `IdempotencyKeyStore` |
| Saga | [`Services/Sagas/SurrealSagaStore.cs`](../../../Services/Sagas/SurrealSagaStore.cs) | `080_saga_steps` (NEW) | inline `SagaStepPoco` |

DI flipped in [`Program.cs`](../../../Program.cs) lines 247–254,
350–351, 373–374. Integration tests under
[`tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/`](../../../tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/)
(6 files, 59 `[SkippableFact]`s). Build state: 0 errors, 17 warnings
(within baseline).

**Closed plan.md tasks:** 5 (partial — 4 of 7 in-scope stores),
6 (dissolved by `surrealdb-client-package`), 7, 8 (partial — adapter
seam only, services still on DbContext), 8b, A7 (test-skip pattern).

## What's still open

| # | Bucket | Tasks |
|---|---|---|
| Wave-2 finish | task 5 completion + task 8 full | Avatar/Holon/STAR adapters; refactor `ReconciliationService` + `CrossChainBridgeService` onto `IBridgeStore` |
| Ops guardrails | 12, 13, 15, A1 | durability self-check (B1), backup/restore drill, OTEL on `ISurrealExecutor`, perf budgets |
| Quest graph (gated) | 9, 10, 11 | quest DAG via `RELATE`, holon polyhierarchy edges, single `ExecutionOrder` — blocked on `quest-temporal-fork-model` |
| Wave-3 EF removal | 16, 17, 18 | delete `AZOADbContext`/`Migrations/`/`Ef*Store.cs`/Npgsql packages |
| Cutover gates | 19–23, 25–27 | G1 crash, G2 idempotency, G7 reconciliation drill, G5 restore drill, G3 injection suite, harness port, sign-off |

This runbook closes everything **except** the quest-graph tasks (which
sequence after the gating track) and the package-pin housekeeping (A4,
A5 — already done implicitly by `surrealdb-client-package`).

## Execution strategy

```
┌─────────────────────────────────────────────────────────────────────┐
│ Phase 1 — three parallel windows (start within ~30 seconds)         │
│                                                                     │
│  ┌────────────┐  ┌────────────┐  ┌────────────┐                     │
│  │ Stream A   │  │ Stream B   │  │ Stream C   │                     │
│  │ adapters   │  │ ops guards │  │ reconcile  │                     │
│  └─────┬──────┘  └─────┬──────┘  └─────┬──────┘                     │
└────────┼───────────────┼───────────────┼─────────────────────────────┘
         │               │               │
         └───────┬───────┘               │      (B can still be running)
                 ▼                       ▼
┌─────────────────────────────────────────────────────────────────────┐
│ Phase 2 — sequential after A + C land                               │
│                                                                     │
│         ┌─────────────────────────────┐                             │
│         │ Stream C2 — pre-D gap close │  (added 2026-05-22)         │
│         └──────────────┬──────────────┘                             │
│                        ▼                                            │
│         ┌─────────────────────────────┐                             │
│         │ Stream D0 — test rewiring   │  (added 2026-05-22)         │
│         └──────────────┬──────────────┘                             │
│                        ▼                                            │
│         ┌─────────────────────────────┐                             │
│         │ Stream D — EF deletion      │                             │
│         └──────────────┬──────────────┘                             │
└────────────────────────┼────────────────────────────────────────────┘
                         ▼
┌─────────────────────────────────────────────────────────────────────┐
│ Phase 3 — sign-off                                                  │
│                                                                     │
│         ┌─────────────────────────────┐                             │
│         │ Stream E — cutover gates    │                             │
│         └─────────────────────────────┘                             │
└─────────────────────────────────────────────────────────────────────┘
```

**Model routing inside each stream.** Workers default to sonnet
(`oh-my-claudecode:executor`); trivial scaffolds use haiku
(`executor-low`); the closing code-review uses opus
(`oh-my-claudecode:code-reviewer`) with a single opus
(`executor-high`) fix pass if findings.

---

## Phase 1 — three parallel windows

Open three Claude Code instances in the repo and paste one prompt per
window. They touch disjoint file sets; you can launch all three within
~30 seconds.

### Stream A — finish [`plan.md`](plan.md) task 5 (Avatar / Holon / STAR adapters)

**Scope.** Author the three missing `.mermaid`/`.surql` schemas, build
their adapters mirroring the wave-2 pattern, and flip DI. Quest portion
stays gated on `quest-temporal-fork-model` — skip it.

**Prompt:**

```
/ultrapilot

Goal: close wave-2 task 5 (conductor/tracks/surrealdb-migration/plan.md)
for the Avatar, Holon, STAR aggregates. Quest stays gated on
quest-temporal-fork-model — SKIP it.

Pattern to mirror exactly: the just-landed wave-2 stores at
Providers/Stores/Surreal/SurrealWalletStore.cs (simple aggregate) and
Providers/Stores/Surreal/SurrealNftStore.cs (multi-table). Same
Guid("N") id encoding, same AZOAResult mapping, same SRDB0001-clean
query shape (single-literal SurrealQuery.Of, no string concatenation —
the analyzer is one-hop only so a helper method indirection is also
acceptable per SurrealBridgeStore.cs:BuildConditionalUpdateSql).

Decomposition (use haiku for explore, sonnet for the 3 workers):

  W1 (sonnet, isolated):
    - Persistence/SurrealDb/Schemas/source/090_avatar.mermaid (SCHEMAFULL,
      mirror 010_wallet.mermaid annotations style)
    - Persistence/SurrealDb/Schemas/090_avatar.surql (hand-author)
    - Providers/Stores/Surreal/SurrealAvatarStore.cs
    - tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/SurrealAvatarStoreTests.cs
    Inline POCO inside the store (no source-gen pass this round; mark
    "replace with generated POCO when source-gen catches up"). Field
    shape from Models/Avatar.cs.

  W2 (sonnet, isolated):
    - Persistence/SurrealDb/Schemas/source/100_holon.mermaid
    - Persistence/SurrealDb/Schemas/100_holon.surql
    - Providers/Stores/Surreal/SurrealHolonStore.cs
    - tests/.../SurrealHolonStoreTests.cs
    Field shape from Models/Holon.cs. IHolonStore.QueryAsync takes a
    HolonQueryRequest with 7 optional filters — chain conditional
    WHEREs against a SurrealQuery.Of parameterized base.

  W3 (sonnet, isolated):
    - Persistence/SurrealDb/Schemas/source/110_star.mermaid
    - Persistence/SurrealDb/Schemas/110_star.surql
    - Providers/Stores/Surreal/SurrealStarStore.cs
    - tests/.../SurrealStarStoreTests.cs
    Field shape from Models/STARODK.cs.

All tests use [SkippableFact] + Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
"...") — match the pattern in SurrealWalletStoreTests / SurrealNftStoreTests
(NOT the early-return pattern).

Sequential coordinator pass (you, sonnet):
  - Flip the 3 Program.cs:247-254 EF lines for Avatar/Holon/STAR to the
    new Surreal counterparts (keep Quest on EF — gated on
    quest-temporal-fork-model).
  - dotnet build AZOA.WebAPI.csproj — assert 0 errors, ≤17 warnings.

Closing pass (opus, ONE invocation):
  Spawn oh-my-claudecode:code-reviewer with model=opus on the diff.
  Focus: SRDB0001 clean, AZOAResult shape parity with the Ef*Store
  counterpart, Guid("N") encoding consistent across all three, no
  silent DateTime-Kind drift.
  If findings, spawn ONE executor-high to fix them; re-build.

Done = build green + plan.md task 5 boxes ticked for Avatar/Holon/STAR.
```

### Stream B — ops guardrails ([`plan.md`](plan.md) tasks 12, 13, 15, A1)

**Scope.** Five small independent deliverables. A7 (test-skip pattern)
already shipped during the wave-2 sequential pass — drop from this
stream.

**Prompt:**

```
/ultrapilot

Goal: close the wave-2/wave-3 ops guardrails from
conductor/tracks/surrealdb-migration/plan.md that don't depend on
adapter work. Four small, independent deliverables — token-cheap.

Decomposition (haiku for trivial edits, sonnet for impls):

  W1 (haiku):
    Task 12 — durability fix (B1; spec.md G1). Edit
    docker-compose.surrealdb.yml: set the SurrealDB URI to
    surrealkv://data/azoa.db?sync=every (was Eventual default). Add a
    boot self-check in Program.cs after
    builder.Services.AddAzoaSurrealDb(...) that resolves
    ISurrealExecutor on startup and runs `INFO FOR DB` (or equivalent
    probe) to refuse start if sync != every. Throw
    InvalidOperationException with a clear remediation message.

  W2 (sonnet):
    Task 15 — OTEL on ISurrealExecutor (spec.md A1/observability). Wrap
    DefaultSurrealExecutor with an InstrumentedSurrealExecutor (in
    AZOA.WebAPI, NOT the package) that emits Activity spans + a
    SurrealMetrics meter (counters: queries, errors; histogram:
    duration_ms; tags: table, statement_kind). Register via decorator
    pattern in Program.cs. Don't touch the package.

  W3 (sonnet):
    Task A1 — perf budgets (spec.md pre-cutover gate). New file
    tests/AZOA.WebAPI.IntegrationTests/Perf/SurrealPerfBudgets.cs.
    xUnit tests asserting:
      - wallet GetById p99 < 50ms over 100 iters
      - bridge_tx insert p99 < 100ms over 100 iters
      - saga_steps due-scan p99 < 200ms over 100 iters
    [Trait("Category","Perf")] so they don't block normal CI; mark
    [SkippableFact] with the same SkipIfSurrealDbUnavailableAsync()
    pattern used by the wave-2 store tests.

  W4 (sonnet):
    Task 13 partial — scripts/surrealdb/backup.ps1 + restore.ps1
    wrapping `surreal export` / `surreal import`. Document RTO target
    in Persistence/SurrealDb/Schemas/README.md (or a new BACKUP.md
    next to it). Note the trade-offs already captured in plan.md
    task 13 (no incremental, no PITR).

Closing pass (opus, ONE invocation):
  oh-my-claudecode:code-reviewer with model=opus on the diff. Focus:
  startup-failure semantics in the boot self-check (must not swallow);
  OTEL meter naming conventions (Activity Source name should be
  "Azoa.SurrealDb"); perf-budget tests skippable on missing container.
  One executor-high fix pass if findings.

Done = dotnet build green + boot self-check fires when sync param
missing + plan.md tasks 12/13/15/A1 ticked.
```

### Stream C — wave-2 task 8 full (reconciliation + bridge service refactor)

**Scope.** Single high-stakes refactor of two services (~1100 lines
combined) onto `IBridgeStore` + `IIdempotencyStore`. NO new behavior.
Existing test suites in
[`tests/AZOA.WebAPI.Tests/Services/`](../../../tests/AZOA.WebAPI.Tests/Services/)
gate the change.

**Prompt:**

```
/ultrapilot

Goal: close wave-2 task 8 (conductor/tracks/surrealdb-migration/plan.md).
Currently Services/Reconciliation/ReconciliationService.cs and
Services/CrossChainBridgeService.cs inject AZOADbContext directly.
Refactor BOTH to inject IBridgeStore + IIdempotencyStore so a wave-3
storage flip is a one-line DI change, not a service rewrite.

This is a single high-stakes refactor — 776-line ReconciliationService
plus the bridge service. NO new behavior; pure ctor/usage swap. Tests
in tests/AZOA.WebAPI.Tests/Services/Reconciliation/ and
tests/AZOA.WebAPI.Tests/Services/CrossChainBridgeServiceTests.cs MUST
continue to pass.

Decomposition (haiku for the inventory, ONE sonnet worker for the
refactor — splitting risks ctor-injection drift):

  W1 (haiku, explore):
    Build a precise inventory: every _db.* access in both services with
    line number, what query shape, what IBridgeStore /
    IIdempotencyStore method already exists for it, and what's MISSING
    from IBridgeStore. Write to .omc/stream-c-inventory.md. Under 200
    lines.

  Coordinator review of inventory: if IBridgeStore is missing any
  method (e.g. a GetBridgesByStatusForReconciliation that takes
  additional filters), extend it FIRST and implement on EfBridgeStore
  + SurrealBridgeStore side-by-side. Treat new IBridgeStore methods as
  ADDITIVE — never rename existing.

  W2 (sonnet, isolated to the 2 services + their tests):
    OWNED FILES:
      - Services/Reconciliation/ReconciliationService.cs
      - Services/CrossChainBridgeService.cs
      - Interfaces/Stores/IBridgeStore.cs                  (additive only)
      - Providers/Stores/EfBridgeStore.cs                  (additive only)
      - Providers/Stores/Surreal/SurrealBridgeStore.cs     (additive only)
      - tests/AZOA.WebAPI.Tests/Services/Reconciliation/* (update mocks)
      - tests/AZOA.WebAPI.Tests/Services/CrossChainBridgeServiceTests.cs
    Replace _db.* with _bridgeStore.* / _idempotency.* calls. Keep
    behavior IDENTICAL. Existing tests gate the refactor.

Sequential coordinator pass (sonnet):
  - dotnet test tests/AZOA.WebAPI.Tests/AZOA.WebAPI.Tests.csproj \
      --filter "FullyQualifiedName~Reconciliation|FullyQualifiedName~CrossChain"
  - 100% pass required.

Closing pass (opus, ONE invocation):
  oh-my-claudecode:code-reviewer with model=opus on the
  ReconciliationService + CrossChainBridgeService diffs.
  Storage-correctness focus: any silent semantic drift
  (e.g. AsNoTracking → tracked equivalent, transaction boundaries
  lost, ExecuteUpdateAsync → store method that no-longer-asserts
  affected count, idempotency-store scope semantics). One
  executor-high fix pass if findings.

Done = both services have ZERO AZOADbContext references, all existing
tests pass, opus reviewer sign-off attached. Update the rationale
comment block at Program.cs:368-376 (currently says "stays on EF until
wave-3") to remove that note.
```

---

## Phase 2 — sequential after A + C land

> **Sequencing (amended 2026-05-22).** Stream C2 first, then Stream D.
> Stream C2 was inserted after a failed Stream D dry-run: the pre-flight
> grep surfaced three production code sites (`Services/Quest/QuestInstantiator.cs`,
> `Core/ApiKeyAuthenticationHandler.cs`, `Controllers/ApiKeyController.cs`)
> that still inject `AZOADbContext` and were never in Stream C's scope.
> Stream D's W1 deletion list would have broken the build at those call
> sites. C2 closes that gap by shipping the missing `IApiKeyStore` and
> `IQuestTemplateStore` seams + Surreal adapters, then flipping the
> three callers. The Stream D pre-flight gate now passes cleanly.

### Stream C2 — pre-D gap closure ([`plan.md`](plan.md) task 8 follow-up)

**Scope.** Two small, additive seams that Stream C didn't claim. NO
behaviour change at the controller / handler / instantiator layer —
pure ctor swap from `AZOADbContext` to the new store interface, plus
the Surreal-backed adapter mirroring the wave-2 pattern.

**Why now (not deferred).** Stream D can't proceed until every
production injection site is gone, and `quest-temporal-fork-model` is
weeks out. Quest TEMPLATE lookups are read-only catalog data
(`QuestTemplate` / `QuestNodeTemplate` definitions) — definition-side,
not the runtime `quest_run` / `quest_node_execution` split that
`quest-temporal-fork-model` owns — so there's no track collision.

**Prompt:**

```
/ultrapilot

Goal: close the two production-code AZOADbContext injection sites
Stream C didn't claim, so the Stream D pre-flight gate clears. Two
isolated workers — touch disjoint files. NO behaviour change at the
caller layer; pure ctor swap.

Pattern to mirror: SurrealIdempotencyStore.cs (deterministic-id O(1)
record-id pattern + UNIQUE-aware claim, see Core/Idempotency/) and
SurrealSagaStore.cs (multi-table embedded POCO + Guid("N") encoding,
see Services/Sagas/). Schemas mirror the 080_saga_steps.mermaid
SCHEMAFULL annotation style.

Decomposition (sonnet, 2 isolated workers):

  W1 (sonnet) — ApiKey infrastructure:
    OWNED FILES (additive):
      - Persistence/SurrealDb/Schemas/source/120_api_key.mermaid
      - Persistence/SurrealDb/Schemas/120_api_key.surql
      - Interfaces/Stores/IApiKeyStore.cs
      - Providers/Stores/Surreal/SurrealApiKeyStore.cs
      - tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/SurrealApiKeyStoreTests.cs
    OWNED FILES (flip — drop AZOADbContext):
      - Core/ApiKeyAuthenticationHandler.cs   (resolve IApiKeyStore via
                                                IServiceScopeFactory — handler
                                                is itself singleton)
      - Controllers/ApiKeyController.cs        (ctor-inject IApiKeyStore)

    Schema (table api_key, SCHEMAFULL): id (string), avatar_id (string,
    indexed), name (string), key_hash (string, UNIQUE), key_prefix
    (string), created_date (datetime), expires_at (option<datetime>),
    last_used_at (option<datetime>), revoked_at (option<datetime>),
    is_active (bool, default true), scopes (option<string>).

    IApiKeyStore surface (minimal — match exactly what the two callers
    need, nothing more):
      - Task<ApiKey?> GetByHashAsync(string keyHash, CancellationToken ct)
      - Task<IReadOnlyList<ApiKey>> ListByAvatarAsync(Guid avatarId, CancellationToken ct)
      - Task<ApiKey?> GetByIdForAvatarAsync(Guid id, Guid avatarId, CancellationToken ct)
      - Task CreateAsync(ApiKey apiKey, CancellationToken ct)
      - Task<bool> RevokeAsync(Guid id, Guid avatarId, DateTime revokedAt, CancellationToken ct)
      - Task<bool> DeleteAsync(Guid id, Guid avatarId, CancellationToken ct)
      - Task TouchLastUsedAsync(Guid id, DateTime lastUsedAt, CancellationToken ct)
        (fire-and-forget from the handler — must not throw)

    Storage encoding: SurrealDB record id = Guid("N") lowercase hex
    (same pattern as SurrealSagaStore.ToSurrealId). UNIQUE on key_hash
    via a DEFINE INDEX api_key_unique_hash.

  W2 (sonnet) — QuestTemplate catalog:
    OWNED FILES (additive):
      - Persistence/SurrealDb/Schemas/source/130_quest_template.mermaid
      - Persistence/SurrealDb/Schemas/130_quest_template.surql
      - Persistence/SurrealDb/Schemas/source/140_quest_node_template.mermaid
      - Persistence/SurrealDb/Schemas/140_quest_node_template.surql
      - Interfaces/Stores/IQuestTemplateStore.cs
      - Providers/Stores/Surreal/SurrealQuestTemplateStore.cs
      - tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/SurrealQuestTemplateStoreTests.cs
    OWNED FILES (flip — drop AZOADbContext):
      - Services/Quest/QuestInstantiator.cs
      - tests/AZOA.WebAPI.Tests/Quest/QuestInstantiatorTests.cs (replace
        DbContext fixture with IQuestTemplateStore mock)

    Schema (table quest_template, SCHEMAFULL): id, name, description
    (option<string>), author_avatar_id, parameters (string — JSON
    schema), version, is_public (bool), tags (array<string>).
    Nodes + Edges embed as FLEXIBLE typed arrays of objects on the
    parent template row (no separate quest_template_node /
    quest_template_edge tables — they are never queried independently;
    keep the lookup O(1) per template).
    Schema (table quest_node_template, SCHEMAFULL): id, name, node_type
    (string, ASSERT INSIDE enum), description, default_config (string),
    config_schema (string), input_schema (string), output_schema
    (string), version, author_avatar_id, is_public, tags (array<string>).

    IQuestTemplateStore surface:
      - Task<QuestTemplate?> GetTemplateAsync(Guid templateId, CancellationToken ct)
        (returns template WITH Nodes + Edges populated — single-row read)
      - Task<QuestNodeTemplate?> GetNodeTemplateAsync(Guid nodeTemplateId, CancellationToken ct)

    QuestInstantiator flip: replace
      `_dbContext.QuestTemplates.FindAsync(templateId)` with
      `_templateStore.GetTemplateAsync(templateId, ct)`, and
      `_dbContext.QuestNodeTemplates.FindAsync(...)` with
      `_templateStore.GetNodeTemplateAsync(...)`. Add CancellationToken
      to InstantiateAsync's signature (additive — callers update).

    Unit-test flip: QuestInstantiatorTests' test fixture currently
    constructs an in-memory AZOADbContext (SqliteTestContext). Replace
    with a `Mock<IQuestTemplateStore>` that returns pre-built templates
    on the matching ids. Existing assertions stay identical — they
    operate on the produced Quest, not the data source.

Sequential coordinator pass (sonnet, ONE invocation):
  - Wire DI in Program.cs (2 new lines, scoped lifetime):
      builder.Services.AddScoped<IApiKeyStore,
          AZOA.WebAPI.Providers.Stores.Surreal.SurrealApiKeyStore>();
      builder.Services.AddScoped<IQuestTemplateStore,
          AZOA.WebAPI.Providers.Stores.Surreal.SurrealQuestTemplateStore>();
  - dotnet build AZOA.WebAPI.csproj   → 0 errors
  - dotnet test AZOA.WebAPI.Tests --filter "FullyQualifiedName~ApiKey|FullyQualifiedName~Quest"
      → all green
  - Re-run the Stream D pre-flight gate. Production-code gate must be
    empty (XML-doc comment stragglers in
    Services/Sagas/SagaProcessorHostedService.cs,
    Services/Sagas/SagaStep.cs,
    Services/Reconciliation/ReconciliationHostedService.cs,
    Interfaces/IReconciliationService.cs,
    Models/ConsumedVaaRecord.cs are NON-blockers — Stream D's W3 will
    rewrite them when those files are touched anyway).

Closing pass (opus, ONE invocation):
  oh-my-claudecode:code-reviewer with model=opus on the Stream C2 diff.
  Focus: UNIQUE-on-key_hash correctness (no NULL collision risk — B3),
  IApiKeyStore.TouchLastUsedAsync must not throw (fire-and-forget
  contract), QuestInstantiator behaviour parity (same exception messages,
  same Quest output shape), no leaked Microsoft.EntityFrameworkCore using
  directives in the new files. One executor-high fix pass if findings.

Done = ApiKey + Quest callers all AZOADbContext-free, Stream D
pre-flight gate clears, build + test green.
```

---

### Stream D0 — test rewiring ([`plan.md`](plan.md) task 17 follow-up)

**Scope.** When Stream D's W1 deletes the `Ef*Store.cs` family and
`Core/Idempotency/IdempotencyStore.cs`, six test files stop compiling
because they wire those EF types directly. Stream D0 closes that gap
before Stream D runs, so the "dotnet test → all green" coordinator
gate stays honest.

**Discovered.** 2026-05-22 (during the first Stream D dry-run after
Stream C2 landed). The original Stream D prompt's W1 deletion list
covered production code but not the test-side fallout; that scope
gap is closed here.

**Test impact inventory:**

| File | LOC | Action | Rationale |
|---|---|---|---|
| `tests/AZOA.WebAPI.Tests/Core/IdempotencyStoreTests.cs` | 435 | DELETE | Tests the EF `IdempotencyStore` impl directly. The Surreal equivalent at `tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/SurrealIdempotencyStoreTests.cs` covers the replacement contract at full fidelity (UNIQUE-key claim, conditional state transitions, multi-statement inspection). |
| `tests/AZOA.WebAPI.Tests/Sagas/SagaProcessorTests.cs` | 285 | DELETE | Tests the saga single-winner / lease-reclaim / retry / compensation / dead-letter flows against the EF saga store + SQLite. The store-level contract is covered byte-for-byte by `SurrealSagaStoreTests` (`TryClaimDueStep_SecondConcurrentCaller_Loses`, `GetDueStepIds_ReclaimsStaleLeases`, `ScheduleRetry_FromInProgress_BumpsAttempt_AndPushesNextRunAt`, `DeadLetterStep_FromInProgress_TransitionsAndSetsFlag`). The processor's tick-orchestration logic (which calls those store methods in sequence) is trivial enough that a future replacement should use `Mock<ISagaStore>` — out of scope for D0. |
| `tests/AZOA.WebAPI.Tests/Sagas/SagaTestHarness.cs` | 139 | DELETE | Helper for the above; no other consumers. |
| `tests/AZOA.WebAPI.Tests/TestSupport/SqliteTestContext.cs` | 122 | DELETE | Provides EF SQLite test harness. All consumers either delete or refactor away from it. |
| `tests/AZOA.WebAPI.Tests/Services/Reconciliation/ReconciliationServiceTests.cs` | 818 | REFACTOR | Stream C flipped `ReconciliationService` to inject `IBridgeStore` + `IIdempotencyStore`, but the test harness (`SqliteReconHarness`) still wires `EfBridgeStore` + EF `IdempotencyStore` behind SQLite. Refactor to use `FakeBridgeStore` + `FakeIdempotencyStore` (NEW — see below) so the existing 11 high-value safety-invariant tests (`KillMidRedeem_ConvergesToChainTruth_Once_AndIdempotent`, `OnChainExplicitNegative_MarksFailed_ConditionalUpdateRespected`, `BridgeConfirmedTerminal_SettlesOrphanedInProgressIdempotency_AndIdempotentOnRerun`, `ReversingState_NotAdvanced_FlaggedForManualIntervention`, etc.) still run. All assertions preserved verbatim. |
| `tests/AZOA.WebAPI.Tests/Services/CrossChainBridgeServiceTests.cs` | 1008 | REFACTOR | Same situation as Reconciliation. 23 high-value tests including `ConcurrentDoubleRedeem_ResultsInExactlyOneMint`, `ReplayedVaa_IsRejected_NoSecondMint`, `DuplicateWormholeInitiate_YieldsOneBridgeRow_OneOnChainLock`, `CrashBeforeSave_DoesNotDoubleMintOnRetry` — these depend on the UNIQUE-on-key semantic that the FakeBridgeStore must enforce. |

**New test-only helpers (NEW FILES):**

| File | Purpose |
|---|---|
| `tests/AZOA.WebAPI.Tests/TestSupport/FakeBridgeStore.cs` | In-memory `IBridgeStore` implementation. Uses a `ConcurrentDictionary<string, BridgeTransactionResult>` for `bridge_tx` and `ConcurrentDictionary<Guid, BlockchainOperation>` for `operation_log`, plus a `ConcurrentDictionary<string, ConsumedVaaRecord>` for the consumed-VAA ledger. Conditional-update primitives (`TryTransitionBridgeStatusAsync`, `TryInsertConsumedVaaAsync`) implement the same single-winner contract as `SurrealBridgeStore`: predicate-check + atomic mutate under a per-row `lock`, return `AffectedCount==1` on win and `0` on loss. `TryInsertConsumedVaaAsync` enforces UNIQUE on digest + on the (emitter_chain, emitter_address, sequence) triple — the B2 invariants. |
| `tests/AZOA.WebAPI.Tests/TestSupport/FakeIdempotencyStore.cs` | In-memory `IIdempotencyStore` implementation. `ConcurrentDictionary<string, IdempotencyRecord>` keyed by idempotency key. `TryClaimAsync` uses `TryAdd` for the insert-wins primitive; `CompleteAsync` / `FailAsync` use a per-row `lock` for the InProgress→terminal transition. Same exactly-once contract as `SurrealIdempotencyStore`. |

**Prompt:**

```
/ultrapilot

Goal: rewire AZOA.WebAPI.Tests so it still compiles + passes after
Stream D deletes the Ef*Store family + the EF IdempotencyStore. NO
behaviour change in any test assertion — preserve every existing
test name, every Should/Assert verbatim. Only fixture wiring changes.

Decomposition (sonnet, 4 isolated workers):

  W1 (sonnet) — Fake-store helpers (NEW):
    OWNED FILES:
      - tests/AZOA.WebAPI.Tests/TestSupport/FakeBridgeStore.cs
      - tests/AZOA.WebAPI.Tests/TestSupport/FakeIdempotencyStore.cs

    FakeBridgeStore must implement every method on IBridgeStore. Use
    ConcurrentDictionary<string,BridgeTransactionResult> +
    ConcurrentDictionary<Guid,BlockchainOperation> +
    ConcurrentDictionary<string,ConsumedVaaRecord>. Conditional-update
    primitives (TryTransitionBridgeStatusAsync,
    TryTransitionOperationStatusAsync, TryInsertConsumedVaaAsync) must
    enforce single-winner semantics via per-row lock + predicate-check.
    TryInsertConsumedVaaAsync enforces UNIQUE on digest AND on
    (emitter_chain_id, emitter_address, sequence) triple — the B2
    invariant ReplayedVaa_IsRejected_NoSecondMint depends on this.

    FakeIdempotencyStore: ConcurrentDictionary<string,IdempotencyRecord>
    keyed by Key. TryClaimAsync uses TryAdd (returns Won=false on
    collision and replays the existing record). CompleteAsync /
    FailAsync use per-row lock + InProgress predicate.

  W2 (sonnet) — Reconciliation tests rewire:
    OWNED FILES (refactor):
      - tests/AZOA.WebAPI.Tests/Services/Reconciliation/ReconciliationServiceTests.cs

    Replace SqliteReconHarness's SqliteTestContext + EfBridgeStore + EF
    IdempotencyStore with FakeBridgeStore + FakeIdempotencyStore (from
    W1). Replace all `db.BridgeTransactions.Add(...)` /
    `db.IdempotencyRecords.Add(...)` seed paths with
    `_fakeBridge.SaveBridgeAsync(...)` /
    `_fakeIdempotency.SeedAsync(...)`. Replace all
    `db.BridgeTransactions.AsNoTracking().Single(b => b.Id == id)`
    assert paths with `_fakeBridge.GetBridgeByIdAsync(id)`. KEEP every
    test name, every assertion's expected value, every test sequence.
    No new tests; no removed tests.

  W3 (sonnet) — CrossChainBridge tests rewire:
    OWNED FILES (refactor):
      - tests/AZOA.WebAPI.Tests/Services/CrossChainBridgeServiceTests.cs

    Same pattern as W2 but for CrossChainBridgeServiceTests.
    ConcurrentDoubleRedeem + ReplayedVaa + DuplicateWormholeInitiate
    tests depend on the FakeBridgeStore enforcing the UNIQUE-on-VAA-
    digest semantic — if W1's FakeBridgeStore lands that contract
    correctly, these tests pass unchanged. KEEP every test name + every
    assertion verbatim.

  W4 (haiku) — deletions:
    DELETE these files entirely:
      - tests/AZOA.WebAPI.Tests/Core/IdempotencyStoreTests.cs
      - tests/AZOA.WebAPI.Tests/Sagas/SagaProcessorTests.cs
      - tests/AZOA.WebAPI.Tests/Sagas/SagaTestHarness.cs
      - tests/AZOA.WebAPI.Tests/TestSupport/SqliteTestContext.cs

    No replacements — coverage moves to the integration-tests project's
    Surreal*StoreTests. Confirm no other test file references these
    types via grep before deleting.

Sequential coordinator pass (sonnet):
  - dotnet build AZOA.WebAPI.Tests.csproj → 0 errors (the project
    file may need EF/Npgsql package refs stripped — they get removed
    in Stream D W2 anyway, so leave them for now if the unit tests
    don't transitively need them)
  - dotnet test AZOA.WebAPI.Tests → all green (every previously-
    passing test still passes; every previously-failing test still
    fails for the same reason)

Closing pass (opus, ONE invocation):
  oh-my-claudecode:code-reviewer with model=opus on the W1+W2+W3 diff.
  Focus: FakeBridgeStore TryInsertConsumedVaaAsync correctly enforces
  BOTH UNIQUE invariants (digest AND triple — not just one);
  FakeIdempotencyStore TryClaimAsync returns the EXISTING record on
  collision (not a fresh InProgress); rewire preserves every test name
  and assertion. One executor-high fix pass if findings.

Done = dotnet test AZOA.WebAPI.Tests → 0 failures (skipped is fine
where SkippableFact applies), file deletions confirmed, no EF/Npgsql
using directives remaining in any test file.
```

---

### Stream D — wave-3 EF deletion ([`plan.md`](plan.md) tasks 16, 17, 18)

**Pre-flight gate.** Streams A, C, C2, and D0 must all be complete. B
may still be in flight.

**Prompt:**

```
/ultrapilot

Goal: delete EF Core + Postgres entirely (conductor/tracks/surrealdb-
migration/plan.md tasks 16, 17, 18). Precondition: Streams A and C
landed (every IXxxStore consumer routes through the store interface;
NO service injects AZOADbContext anymore).

Pre-flight gate (haiku, first; ABORT if this fails):
  grep -rln "AZOADbContext" --include="*.cs" .
    excluding bin/, obj/, frontend/, Migrations/, Data/, tests/
  MUST be empty. If not, ABORT and report which files still need to
  flip — those go back to Stream C as a follow-up, not deleted here.

Decomposition (sonnet, 3 isolated workers):

  W1 (deletions):
    - rm -r Data/                            (AZOADbContext.cs + SeedData.cs)
    - rm -r Migrations/                      (entire directory)
    - rm Providers/Stores/EfAvatarStore.cs
    - rm Providers/Stores/EfWalletStore.cs
    - rm Providers/Stores/EfHolonStore.cs
    - rm Providers/Stores/EfBlockchainOperationStore.cs
    - rm Providers/Stores/EfStarStore.cs
    - rm Providers/Stores/EfQuestStore.cs
    - rm Providers/Stores/EfNftStore.cs
    - rm Providers/Stores/EfBridgeStore.cs
    - rm Providers/Stores/EfQuestRunStore.cs       (obsolete stub)
    - rm Providers/Stores/EfQuestNodeExecutionStore.cs (obsolete stub)
    - rm Providers/Stores/InMemoryQuestRunStore.cs   (replace with Surreal — gated)
    - rm Providers/Stores/InMemoryQuestNodeExecutionStore.cs (gated)
    - rm Core/Idempotency/IdempotencyStore.cs        (EF impl)
    - rm Services/Sagas/EfSagaStore.cs

    NOTE: if quest-temporal-fork-model hasn't shipped a SurrealQuestRunStore
    yet, KEEP the InMemoryQuestRunStore + InMemoryQuestNodeExecutionStore
    files; just confirm no EF residue.

  W2 (csproj + config):
    - Edit AZOA.WebAPI.csproj — remove:
        Npgsql.EntityFrameworkCore.PostgreSQL
        Microsoft.EntityFrameworkCore.Design
        any other Microsoft.EntityFrameworkCore.* refs
    - Edit appsettings.json + appsettings.Development.json —
        remove ConnectionStrings:AZOADatabase
        remove AZOA:DefaultProvider (or set to "SurrealDB" and
          delete the failover plumbing)
    - Grep test csprojs for the same EF/Npgsql refs; remove.

  W3 (Program.cs + observability):
    - Edit Program.cs — drop:
        AddDbContext<AZOADbContext> registration
        any db.Database.Migrate() / EnsureCreated() residue
        AZOA:DefaultProvider config branch
    - Drop the `using Microsoft.EntityFrameworkCore;` import.
    - Drop the `using AZOA.WebAPI.Data;` import.
    - Drop the `using AZOA.WebAPI.Providers.Stores;` import (only Surreal/ used).
    - Replace Observability/StorageHealthCheck.cs with a SurrealDB
      probe (ISurrealExecutor + simple `RETURN 1` query).

Sequential coordinator pass (sonnet):
  - dotnet build AZOA.WebAPI.csproj  → 0 errors
  - dotnet build (entire solution)    → 0 errors
  - dotnet test AZOA.WebAPI.Tests     → all green
  - grep -rln "Npgsql\|EntityFramework" --include="*.cs"   → empty
  - grep -rln "Npgsql\|EntityFramework" --include="*.csproj" → empty

Closing pass (opus, ONE invocation):
  oh-my-claudecode:code-reviewer with model=opus on the entire diff.
  Focus: orphaned using directives, dead code paths left behind,
  config keys still referencing Postgres, test-harness wiring that
  still references AZOADbContext.
  One executor-high fix pass if findings.

Done = task 16/17/18 boxes ticked in plan.md; Postgres entirely gone
from the tree.
```

---

## Phase 3 — final cutover gates

### Stream E — pre-cutover gates ([`plan.md`](plan.md) tasks 19–23, 25–27)

**Scope.** The five guardrail proofs from
[`spec.md` § Pre-cutover gate](spec.md) plus the final sign-off.
Opus thinking is justified here — these tests **are** the
acceptance evidence.

**Prompt:**

```
/ultrapilot

Goal: ship the 5 pre-cutover gates from
conductor/tracks/surrealdb-migration/plan.md tasks 19-23 plus the
final sign-off (25, 26, 27). This is the wave-3 acceptance evidence,
so the test logic is high-stakes — use sonnet for harness scaffolding
and opus for the assertion logic.

Decomposition (5 sonnet workers + 1 opus reviewer):

  W1: tests/AZOA.WebAPI.IntegrationTests/Gates/G1_CrashDurabilityTest.cs
      Guardrail G1 (spec.md). Insert N bridge_tx rows + N saga_steps
      rows, `docker kill -9` the surrealdb container via Testcontainers,
      restart, assert all N survive. Fails closed if sync != every
      (proves task 12's boot self-check would catch it).

  W2: tests/AZOA.WebAPI.IntegrationTests/Gates/G2_IdempotencyTocTouTest.cs
      Guardrail G2. Fire 50 concurrent identical bridge-redeem requests
      with the same Idempotency-Key header, assert exactly one chain
      effect AND exactly one bridge_tx row mutated. Then the same for
      saga_steps.TryClaimDueStep — N=20 concurrent claimers, exactly
      one wins.

  W3: tests/AZOA.WebAPI.IntegrationTests/Gates/G7_ReconciliationDrillTest.cs
      Guardrail G7. Start a bridge, kill mid-op (after lock_tx_hash
      set, before redemption), restart reconciliation with a stubbed
      IBlockchainProvider that returns known-confirmed truth, assert
      the row converges to Completed (not orphaned in Redeeming).

  W4: tests/AZOA.WebAPI.IntegrationTests/Gates/G5_RestoreDrillTest.cs
      Guardrail G5. Drive scripts/surrealdb/backup.ps1 (from Stream B
      W4) → wipe the namespace → drive restore.ps1 → run a smoke read
      on every value table, assert row counts + sample-row checksums
      identical.

  W5: tests/AZOA.WebAPI.IntegrationTests/Gates/G3_InjectionSuiteTest.cs
      Guardrail G3. Hostile input through every controller path that
      lands in a SurrealQL parameter: `' OR 1=1; DROP TABLE wallet;--`,
      embedded `$param` tokens, `type::thing()` injection attempts,
      unicode normalization games. Assert (a) the analyzer
      (SRDB0001) would catch any C# regression, (b) the executor +
      WithParam binding makes the hostile payload land as a literal
      string value, never as SurrealQL syntax.

All 5 tests are [SkippableFact] with SkipIfSurrealDbUnavailableAsync().

Final sign-off (opus, ONE invocation):
  oh-my-claudecode:code-reviewer with model=opus across:
    - all 5 gate test files
    - conductor/tracks/surrealdb-migration/spec.md G1-G7 acceptance
      criteria
    - the wave-2 + wave-3 diffs
  Produce a sign-off document at
  conductor/tracks/surrealdb-migration/SIGN-OFF.md linking each
  guardrail to its evidence (file:line of the passing test) plus a
  short paragraph per guardrail on residual risk.

  Cross-check api-safety-hardening/RESIDUAL-RISK-RUNBOOK.md §4 items
  to confirm they're still green post-migration.

Done = SIGN-OFF.md committed + plan.md tasks 19-23, 25-27 ticked + the
surrealdb-migration track is COMPLETE.
```

---

## Launch checklist

```
☐ Phase 1
   ☐ Stream A window opened, prompt pasted, started
   ☐ Stream B window opened, prompt pasted, started
   ☐ Stream C window opened, prompt pasted, started
       (~30 seconds total to launch the 3)

—— wait for A and C to signal complete (B may still be running) ——

☐ Phase 2
   ☐ Stream D window opened (reuse one of A/C's windows after they finish)
   ☐ Confirm A and C both completed before pasting D's prompt
       (D's pre-flight gate will abort otherwise)

—— wait for D to signal complete (B should be done by now too) ——

☐ Phase 3
   ☐ Stream E window opened, prompt pasted
   ☐ Read conductor/tracks/surrealdb-migration/SIGN-OFF.md
   ☐ Ack guardrail evidence; close plan.md task boxes
   ☐ Notify mcp-surface track that its blocker is cleared
```

## Token budget

| Stream | Explore | Impl | Review | Estimate |
|---|---|---|---|---|
| A | haiku | 3 × sonnet | 1 × opus | ~120k |
| B | 1 × haiku | 3 × sonnet | 1 × opus | ~80k |
| C | 1 × haiku | 1 × sonnet | 1 × opus | ~90k |
| D | 1 × haiku gate | 3 × sonnet | 1 × opus | ~70k |
| E | — | 5 × sonnet | 1 × opus | ~140k |
| **total** | | | | **~500k** |

Comparison points: opus-everywhere ≈ 1.5M tokens; serial single-window
execution ≈ 3× wall-clock. The opus pass is reserved for the closing
review of each stream — that's where the model's thinking earns its
tokens.

## Sign-off checklist (replicates [`plan.md`](plan.md) §"Pre-cutover gate")

- [ ] G1 — Crash/power-loss test green (Stream E W1)
- [ ] G2 — Idempotency/TOCTOU test green (Stream E W2)
- [ ] G3 — Injection suite green (Stream E W5, uses `Azoa.SurrealDb.Analyzer`)
- [ ] G4 — Build fails if `AzoaSurrealDbVersion` in
      [`Directory.Build.props`](../../../Directory.Build.props) drifts from
      `Azoa.SurrealDb.Client`
- [ ] G5 — Restore drill green (Stream E W4)
- [ ] G6 — Value tables `SCHEMAFULL` (already shipped wave-1)
- [ ] G7 — Reconciliation drill green (Stream E W3)
- [ ] Build — zero errors, ≤17 warnings baseline maintained
- [ ] [`SIGN-OFF.md`](SIGN-OFF.md) committed with evidence links

## Appendix — key file paths the prompts reference

| Concern | Path |
|---|---|
| Track spec | [`conductor/tracks/surrealdb-migration/spec.md`](spec.md) |
| Track plan | [`conductor/tracks/surrealdb-migration/plan.md`](plan.md) |
| Wave-2 adapter pattern (simple) | [`Providers/Stores/Surreal/SurrealWalletStore.cs`](../../../Providers/Stores/Surreal/SurrealWalletStore.cs) |
| Wave-2 adapter pattern (multi-table) | [`Providers/Stores/Surreal/SurrealNftStore.cs`](../../../Providers/Stores/Surreal/SurrealNftStore.cs) |
| Wave-2 G2 conditional-update pattern | [`Providers/Stores/Surreal/SurrealBridgeStore.cs`](../../../Providers/Stores/Surreal/SurrealBridgeStore.cs) |
| Wave-2 saga schema source | [`Persistence/SurrealDb/Schemas/source/080_saga_steps.mermaid`](../../../Persistence/SurrealDb/Schemas/source/080_saga_steps.mermaid) |
| Mermaid annotation style | [`Persistence/SurrealDb/Schemas/source/010_wallet.mermaid`](../../../Persistence/SurrealDb/Schemas/source/010_wallet.mermaid) |
| DI flip target | [`Program.cs`](../../../Program.cs) lines 244–262, 350–363, 373–378 |
| Reconciliation refactor target | [`Services/Reconciliation/ReconciliationService.cs`](../../../Services/Reconciliation/ReconciliationService.cs) |
| Bridge service refactor target | [`Services/CrossChainBridgeService.cs`](../../../Services/CrossChainBridgeService.cs) |
| Integration test base | [`tests/AZOA.WebAPI.IntegrationTests/IntegrationTestBase.cs`](../../../tests/AZOA.WebAPI.IntegrationTests/IntegrationTestBase.cs) |
| Skippable test exemplar | [`tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/SurrealBridgeStoreTests.cs`](../../../tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/SurrealBridgeStoreTests.cs) |
| Homebake client package | [`packages/Azoa.SurrealDb.Client/`](../../../packages/Azoa.SurrealDb.Client/) |
| Schema source-gen | [`packages/Azoa.SurrealDb.SourceGen/`](../../../packages/Azoa.SurrealDb.SourceGen/) |
| Analyzer (SRDB0001) | [`packages/Azoa.SurrealDb.Analyzer/`](../../../packages/Azoa.SurrealDb.Analyzer/) |
