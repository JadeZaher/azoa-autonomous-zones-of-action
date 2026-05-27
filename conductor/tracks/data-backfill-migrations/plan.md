# Data Backfill Migrations — Plan

Build order: **infrastructure shell first, F6 (first real consumer)
second.** Each phase keeps the 540/540 unit suite green and the
schema-migration runner / `.surql` emit untouched.

## Phase 1 — Backfill runner shell (no real backfills yet)
1. [ ] Author `data_migration` table as a hand-written `.surql` file
   (mirrors `schema_migration` shape: name, applied_at, applied_by,
   rows_rewritten, result_json). Insert-wins UNIQUE on name. NOT
   itself backfilled by the runner (bootstrap concern).
2. [ ] `IBackfill` + `BackfillContext` + `BackfillResult` abstractions
   in `packages/Oasis.SurrealDb.Schema/Backfill/`. Pure C#; reuses
   `ISurrealConnection`, `SurrealQuery<T>`, `SurrealIdentifier`.
3. [ ] `BackfillRunner` — discovery (by `IBackfill` assembly scan +
   explicit registration list), ledger writes via insert-wins, batch
   loop driving `RunAsync` until source query returns zero rows.
   Mirrors `MigrationRunner` pattern.
4. [ ] CLI subcommand wiring in
   `packages/Oasis.SurrealDb.Schema/Program.cs`:
   `oasis-surreal backfill list|apply|apply-all` with the existing
   connection-config resolution order (`--flag` > env var).
5. [ ] OpenTelemetry + structured logging hooks; each batch emits a
   span with `backfill.name`, `backfill.batch_index`, `rows_rewritten`,
   `elapsed_ms`.
6. [ ] Unit tests with a fake `ISurrealConnection`:
   (a) registry discovers + orders backfills by name,
   (b) ledger prevents double-apply,
   (c) `--force` records a new row each time,
   (d) failed batch leaves prior committed + ledger row marks Failed,
   (e) rerun resumes,
   (f) concurrent CLI invocation produces "already-applied" not a
       half-run.
7. [ ] Integration test against the testcontainer SurrealDB:
   apply-all against a fresh namespace is a no-op (empty registry);
   register a trivial sample backfill in the test, apply, verify
   ledger row + idempotent re-apply.

## Phase 2 — F6 FK rewrite (first real consumer, gated on Phase C)
Pre-req: RUNBOOK §4.3 Phase C ships (generator emits FK columns as
`record<table>` not `string`).

8. [ ] `OASIS.WebAPI/Persistence/SurrealDb/Backfills/001_quest_fk_to_record.cs` —
   walk `quest`, `quest_node`, `quest_edge`, `quest_dependency`,
   `quest_run`, `quest_node_execution` and rewrite FK columns
   (avatar_id, quest_id, source_node_id, etc.) from
   `"00abc..."` strings to `record<target_table>` literals via
   `UPDATE quest:id SET avatar_id = type::thing('avatar', $value) ...`
9. [ ] `Backfills/002_bridge_fk_to_record.cs` — same for bridge slice
   (bridge_tx.avatar_id, operation_log.avatar_id+wallet_id,
   consumed_vaa_ledger.bridge_transaction_id).
10. [ ] `Backfills/003_holon_fk_to_record.cs` — holon.avatar_id +
    parent_holon_id; peer_holon_ids array elements stay strings (the
    array is conceptually a tag bag, not strict FK).
11. [ ] `Backfills/004_dapp_composition_fk_to_record.cs` —
    dapp_series.avatar_id + star_odk_id; dapp_series_quest cross-slice
    FKs (dapp_series_id + quest_id).
12. [ ] Concurrency test: spawn 5 parallel CLI invocations of the same
    backfill; exactly one wins, others see "already-applied."
13. [ ] All 28 quest-store integration tests still pass after the
    backfill (when E1 image fix lands and tests can run).

## Phase 3 — Generalize + observability + ops surface
14. [ ] `Backfills/005_holon_polyhierarchy_to_relate.cs` (when
    surrealdb-migration task 10 ships) — rewrite holon.parent_holon_id
    scalar as a `RELATE holon -> parent_of -> holon` edge.
15. [ ] `Backfills/006_embedding_backfill.cs` (when MCP gets a real
    embedding provider) — populate holon.embedding + quest.embedding
    from textual content.
16. [ ] Operator runbook section under `docs/operators/` covering:
    apply order, dry-run interpretation, recovery from a failed batch,
    forensic queries on the `data_migration` ledger.
17. [ ] Chaos test: kill the CLI mid-batch under load; the next
    invocation resumes cleanly with no row double-rewrite + ledger
    reflects the gap accurately.

## Acceptance gate
- F6 backfill ship-ready (Phase 2 done) before the first real customer
  data hits the system.
- Operator runbook references the actual ledger queries; not generic
  "check the docs."

## Out-of-scope follow-ups (track when motivated)
- Forward-only vs reversible distinction: today every backfill is
  forward-only (the data shape is the new truth). A reversibility
  contract (`UndoAsync`) is a sequel concern once we have a real
  rollback story for one of the planned backfills.
- Cross-environment backfill orchestration (apply backfill X on
  devnet → testnet → mainnet in lockstep) — orthogonal to this
  track's scope.
- Auto-discovery of backfills via attribute scanning — explicit
  registration is fine for now (handful of backfills total expected).
