# Data Backfill Migrations — Specification

## Status
Pending. Created 2026-05-27. **Tier 2** (infrastructure / operator
tooling). No deployed customers — backfills today are a greenfield
concern: rewrite historical rows when a schema column changes shape or
a derived field is introduced. Track exists so the operator workflow
is in place before we hit our first real backfill (the F6 FK rewrite
after Phase C).

## Goal
A **first-class data-backfill primitive** — author idempotent,
restartable, observable one-shot data mutations next to the schema DDL
they accompany. Each backfill is a code-defined unit (not a free-form
script), registered with the existing `azoa-surreal` CLI, with an
opinionated execution + observability shell so the operator workflow
is identical across backfills.

## Why (decision rationale)
Schema DDL migrations are already solved:
[Persistence/SurrealDb/Schemas/*.surql](../../../Persistence/SurrealDb/Schemas/)
files are applied by `azoa-surreal migrate up` (the runner at
[packages/Azoa.SurrealDb.Schema/Migration/MigrationRunner.cs](../../../packages/Azoa.SurrealDb.Schema/Migration/MigrationRunner.cs)),
with a `schema_migration` ledger preventing re-application. That covers
*structure*. It does **not** cover *content*.

The known forward demand:
- **F6 FK rewrite** (RUNBOOK §4.3 / surrealdb-migration SIGN-OFF F6) —
  after Phase C generator emits `record<table>` types, every existing
  row that carries a foreign-key string needs its value rewritten as a
  SurrealDB `record::table:id` literal. ~10 columns across quest,
  bridge, holon, dapp_composition aggregates.
- **Quest cutover dependency-persistence gap** (surrealdb-migration
  D4) — when `quest_dependency.surql` lands its hand-written counterpart,
  any historical `Quest.Dependencies` arrays embedded on `quest` need
  to be split out into the new table.
- **Embedding backfill** — when an embedding provider ships post-MCP,
  every existing holon + quest row needs its `embedding` field
  populated from the row's textual content.
- **Polyhierarchy graph remodel** (surrealdb-migration task 10) — when
  holon parent links flip from `parent_holon_id` scalar to a native
  `RELATE` edge, every existing row needs its scalar rewritten as an
  edge.

Each of the above is an *idempotent, restartable, single-direction*
data mutation. Today the workflow would be: hand-author a `.surql`
script per backfill, run it once manually, hope no one reruns it. That
is brittle. The fix is to give backfills the same "registered, ledger-
tracked, restartable" treatment that DDL migrations already enjoy.

### Chosen: C# backfill modules registered with `azoa-surreal backfill`
Rejected alternatives:
- **SQL-only `.surql` files** — fine for tiny row-rewrites but cannot
  express row-by-row logic with conditional branches (e.g. F6 needs to
  parse a Guid('N') hex string + know the target table name). The
  generator-derived record-type form is itself code-defined, so the
  backfill that produces it should live next to that code.
- **Ad-hoc `dotnet run`-style one-off scripts** — re-introduces the
  "run once, hope no one reruns it" problem and bypasses the
  observability shell.
- **Out-of-process migrations runner (Flyway-style)** — heavyweight,
  external dep, and doesn't share the existing
  `ISurrealConnection`/typed-query layer the rest of the codebase
  depends on.

The C# module approach reuses the existing
`Azoa.SurrealDb.Client.SurrealQuery<T>` typed builder, the
`SurrealIdentifier` safety layer (G3 injection defence), and the
`ISurrealConnection` abstraction — so a backfill author writes code
that looks like the production stores, not a parallel script
ecosystem.

## Architecture

### Core abstractions
- **`IBackfill`** — minimal contract:
  - `string Name { get; }` — stable identity, used as the
    `data_migration` ledger key (mirrors `schema_migration` shape).
  - `Task<BackfillResult> RunAsync(BackfillContext ctx, CancellationToken ct)`
  - Idempotency contract documented on the interface: re-running a
    completed backfill MUST be a no-op (or fully re-derive the same
    target state from current input).
- **`BackfillContext`** — provides `ISurrealConnection`, structured
  logger, progress reporter, cancellation token. No DI service locator
  pattern — the backfill module receives only what its declared
  signature needs.
- **`BackfillResult`** — `RowsScanned`, `RowsRewritten`, `RowsSkipped`,
  `Errors`. Operators see these in the CLI output + the
  `data_migration` ledger.

### Data ledger
- New SurrealDB table `data_migration` (mirrors `schema_migration`):
  `name`, `applied_at`, `applied_by`, `rows_rewritten`, `result_json`.
  Hand-authored `.surql` schema (this table is the bootstrap for the
  backfill runner; itself doesn't get backfilled).
- Insert-wins on `name` prevents accidental re-application. Operator
  can opt-in to re-apply via `--force` (records a new row each time;
  the ledger is append-only history).

### Execution shell
- Each backfill runs in a single SurrealDB transaction *per batch*
  (not the whole backfill) — restartability requires checkpoint-friendly
  batching. Backfill author declares a batch size; the shell loops
  until the source query returns zero rows.
- Every batch emits an OpenTelemetry span + structured log line with
  `name`, `batch_index`, `rows_rewritten`, `elapsed_ms`. This is the
  observability hook for prod backfills.
- The shell catches and bubbles exceptions: a backfill failure rolls
  back the in-flight batch (not the whole backfill) and records the
  exception in the `data_migration` row with `result_json.error`.
  Subsequent runs resume from where the failed batch was; the
  idempotency contract ensures that's safe.

### CLI
- `azoa-surreal backfill list` — show registered backfills + their
  ledger status (`Pending` / `Applied` / `Failed`).
- `azoa-surreal backfill apply <name> [--batch <n>] [--dry-run]` —
  execute a specific backfill.
- `azoa-surreal backfill apply-all [--dry-run]` — apply every
  `Pending` backfill in registration order.

### Code location
- `packages/Azoa.SurrealDb.Schema/Backfill/` — abstractions +
  registry + execution shell. Reuses `MigrationRunner`'s ledger
  pattern.
- `AZOA.WebAPI/Persistence/SurrealDb/Backfills/<NNN>_<name>.cs` —
  authored backfill modules sit next to the schema source files so
  authors trip over them when touching adjacent DDL. Numbered to give
  a default registration order (lower = earlier).

## Acceptance
1. F6 backfill exists as a registered module that converts every FK
   string column on `quest`, `quest_node`, `quest_edge`,
   `quest_dependency`, `quest_run`, `quest_node_execution`,
   `bridge_tx`, `holon`, `dapp_series`, `dapp_series_quest` from
   `string` to `record<target_table>`. Idempotent across re-runs.
2. CLI subcommands `list`, `apply`, `apply-all` work end-to-end against
   the testcontainer SurrealDB.
3. `data_migration` ledger persists across container restarts; runs
   that touched zero rows still record a row (so operators can prove
   the backfill was run, not skipped).
4. Concurrent invocation of the same backfill name is prevented by the
   ledger insert-wins primitive (operator gets a clear
   "already-applied" error, not a half-run).
5. Failure of one batch records the exception + leaves prior batches
   committed; rerun resumes from the failed batch.
6. 540/540 unit suite green; new tests cover the registry +
   idempotency contract via a fake `ISurrealConnection`.
7. `azoa-surreal backfill apply-all` against a fresh namespace is
   a no-op (no backfills registered initially; F6 lands as its own
   slice once Phase C is done).

## Out of scope
- Schema DDL migrations — already solved by `azoa-surreal migrate up`.
- Engine version migrations (SurrealDB 1.5 → 2.x) — separate concern.
- Cross-environment data export/import (devnet → testnet) — separate
  concern; addressed by future env-migration playbook.
- Generic "ETL" tooling — backfills here are intra-DB rewrites, not
  source → DB pipelines.

## Dependencies
- Builds cleanly on the existing `MigrationRunner` shape. No new
  external packages.
- F6 consumer is gated on Phase C (generator FK emission). The
  abstraction + CLI land first (this track); F6 lands as a follow-up
  slice once Phase C settles.

## Related tracks
- **surrealdb-migration** (`[x]` 2026-05-24) — F6 follow-up filed in
  SIGN-OFF.md is the canonical first consumer.
- **surrealdb-schema-source-gen** — Phase C generator change is what
  motivates the F6 backfill.
- **architecture-decoupling** (`[x]`) — provides the observability +
  health surface this track plugs into.
- **api-safety-hardening** (`[x]`) — `IIdempotencyStore` is conceptual
  kin (insert-wins ledger). Backfills are similar shape but
  intentionally lighter — no compensation, no retry policy beyond
  "rerun the CLI."
