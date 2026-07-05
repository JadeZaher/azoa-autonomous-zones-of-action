# SurrealDB Migration — Specification

## Goal
Tier 2: replace EF Core + PostgreSQL + `InMemoryStorageProvider` with
**SurrealDB as the single primary data engine**, behind the persistence seam
from `architecture-decoupling`. Greenfield decision (no production data) —
defensible because there is no migration/dual-write/rollback of live data. The
store is an orchestration/audit layer over the chain (chain = source of truth
for value), which is SurrealDB's strength, not a financial ledger (its weakness).

Realistic effort: **~4–5 weeks** (full storage layer + graph remodel + test
port + guardrails), not the "5–7 days" an earlier draft claimed.

## Removed by decision
PostgreSQL, EF Core (`AZOADbContext`, `EfStorageProvider`, migrations, Npgsql),
`InMemoryStorageProvider`. **No SpacetimeDB, no in-memory hot layer, no
hot/cold split** — premature optimization, consciously deferred.

## Non-negotiable guardrails (acceptance criteria, not best-effort)
- **G1 — Durability forced on.** SurrealKV defaults to `Eventual` (no `fsync`
  before commit). Deploy config sets `surrealkv://data/azoa.db?sync=every`
  in the connection URI (the env-var path attempted in wave-1 was wrong for
  SurrealKV — confirmed against GH #5001 + journalistic-persona research);
  Program.cs boot self-check refuses to start if sync != every;
  crash-durability test in CI.
- **G2 — Idempotency + conditional state guards.** NOT balance CAS (no stored
  balance). (a) deterministic/client idempotency key on every irreversible
  chain op, persisted + checked before broadcast; (b) single-field conditional
  state transitions (`UPDATE x SET status=B WHERE status=A`, assert one row).
  Delivered through the [[surrealdb-client-package]] query builder's
  `.UpdateOnly(table, id).Where(field, value).Set(field, value)` primitive
  with explicit per-statement affected-count return. (Idempotency itself is
  delivered in `api-safety-hardening`; this track preserves it through the
  engine swap.)
- **G3 — Parameterized queries only.** No C# string interpolation into
  SurrealQL ever; enforced by `Azoa.SurrealDb.Analyzer` (SRDB0001 Error
  severity, ships from [[surrealdb-client-package]]).
- **G4 — Pin our own client package.** Wave-1 pinned `SurrealDb.Net 0.10.2`
  (pre-1.0, single small vendor, 3 open data-loss bugs); [[surrealdb-client-package]]
  replaces that dependency with `Azoa.SurrealDb.Client` which we own and
  semver. G4 narrows to "pin `AzoaSurrealDbVersion` in `Directory.Build.props`."
  Integration tests still run against a real container.
- **G5 — Backup/restore is first-class.** Scheduled `surreal export` +
  periodically **exercised** restore drill; schema via the in-tree migration
  runner shipped in [[surrealdb-client-package]] (replaces the archived
  `Odonno/surrealdb-migrations` tool the original spec referenced).
- **G6 — Value tables `SCHEMAFULL`.** Wallets, bridge tx, swap state, NFT
  ownership, operation log: enforced schema + asserts. Authored in `.mermaid`
  source (via [[surrealdb-client-package]] `Azoa.SurrealDb.Schema`),
  generated to `.surql`. Schemaless only for holon/quest flexible attributes
  and MCP context.
- **G7 — Chain reconciliation mandatory.** Re-derive op/bridge truth from
  chain confirmations, never trust the local lifecycle flag (delivered in
  `api-safety-hardening`; must remain green post-migration).

## Graph remodel (the payoff)
Map quest DAG and holon polyhierarchy to native SurrealDB `RELATE` edges +
`->`/`<-` traversal, replacing in-app graph code where it simplifies. Preserve
the iterative DAG validation guarantees (acyclicity, reachability,
single-`ExecutionOrder`).

**Quest tables consume the hand-off doc** `tracks/quest-temporal-fork-model/
SURREAL-SCHEMA-HINTS.md` — that track owns the runtime/definition split
(`Quest`/`QuestNode` = immutable definition; `QuestRun` +
`QuestNodeExecution` = per-attempt state; `forked_from` lineage edge).
Schema work for quest tables (task 3 quest portion, tasks 9–10) is gated
on that hand-off being merged; foundation + value-table schemas
(wallet/bridge/swap/NFT/operation-log) and saga tables proceed in parallel.

## Pre-cutover gate (must pass, not just measure)
- Crash/power-loss: committed orchestration/audit record survives `kill -9` +
  restart and is reconcilable (G1+G7).
- Idempotency/TOCTOU: duplicate + concurrent irreversible op (faucet / bridge
  redeem) → exactly one chain effect (G2).
- Reconciliation drill: kill mid-op → recovery re-derives chain truth (G7).
- Restore drill: export → wipe → import → integrity assertions pass (G5).
- Injection suite: hostile input through every query path (G3).
- Package-pin: build fails if `AzoaSurrealDbVersion` in `Directory.Build.props`
  drifts from the version actually resolved by `Azoa.SurrealDb.Client` (G4).

## Carried over (owned here, not in api-safety-hardening)
- **Integration-test harness rebuild.** `AZOA.WebAPI.IntegrationTests` was
  built for disposable per-factory EF-InMemory DBs; `api-safety-hardening`
  exposed that it cannot run correctly against a shared persistent relational
  DB (destructive `EnsureDeleted`-style teardown + parallel collections racing
  one DB; `Program.cs` `db.Database.Migrate()` is relational-only). A one-off
  Postgres patch was **deliberately not done** — Postgres is being deleted by
  this track. The harness is rebuilt **once, against SurrealDB** here: real
  test container (task 2), schema via the gated migration job (not app boot),
  no destructive shared-DB teardown, deterministic isolation. Until then the
  unit suite (537+ tests incl. all exactly-once / replay / reconciliation
  safety tests) is the authoritative gate (per the api-safety-hardening
  runbook).
- **Saga trigger → LIVE queries (delivered via [[surrealdb-client-package]]
  sub-wave 1.5b).** `durable-saga-orchestration` ships a pluggable
  `ISagaTrigger` (polling impl now). [[surrealdb-client-package]] adds
  `LiveQuerySagaTrigger` ALONGSIDE polling, opt-in per saga, with
  `Trigger = Both` default (LIVE primary + polling 60s backup that asserts
  no-missed-events). Polling stays the default until a 90-day reliability
  soak passes. **The original "REPLACE polling" plan was struck** after the
  archaeological persona surfaced SurrealDB's documented LIVE contract
  ("single-node + best-effort-ordered" — issues #5068, #5014, #5160, #5070
  open) and the futurist persona warned against deleting the fallback.
  Reconciliation (G7) is still a saga-resume concern, not a separate sweep.
- **api-safety-hardening pre-launch gates.** The gating items in
  `tracks/api-safety-hardening/RESIDUAL-RISK-RUNBOOK.md` §4 (e.g.
  `IVaaSignatureVerifier`, migration baseline, distributed rate-limit store)
  are tracked in that runbook and must be reconfirmed post-migration (G2/G7).

## Postgres fallback — REMOVED
The original spec documented a "fall back to Postgres for audit ledger if
G1/G2/G7 fail chaos testing" exit ramp. **Decision 2026-05-21: Postgres is
fully deprecated.** No fallback ramp exists. The mitigation against
single-engine risk is now [[surrealdb-client-package]] (own the client + the
schema tooling + the analyzer, so the engine itself becomes the only external
dependency). Strategic-review item A9 (standing Postgres CI shadow) is
correspondingly dropped.

## Dependencies
Requires `architecture-decoupling` (the seam). Requires
`api-safety-hardening` (idempotency/reconciliation must exist before swapping
engines under value paths). **Wave-2 adapter work (tasks 5, 6, 7, 8, 8a, 8b)
requires [[surrealdb-client-package]] sub-wave 1.5a complete** (HTTP client +
query builder + Mermaid schema tool + analyzer relocated; SDK-pin removed).
LIVE-query saga adoption requires [[surrealdb-client-package]] sub-wave 1.5b.
Quest-table schema gated on `quest-temporal-fork-model`. Blocks `mcp-surface`.
