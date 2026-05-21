# SurrealDB Schemas

SCHEMAFULL value-table schemas for OASIS Sleek (G6 guardrail).
Applied once via the gated migration job (task 14 in the surrealdb-migration plan)
— NOT at app boot.

## File ordering

Files are applied in lexical order of their numeric prefix. Base tables must
always come before any edge tables or tables that reference them.

| File | Table | Depends on |
|---|---|---|
| `010_wallet.surql` | `wallet` | — |
| `020_bridge_tx.surql` | `bridge_tx` | — |
| `030_swap_state.surql` | `swap_state` | — |
| `040_nft_ownership.surql` | `nft_ownership` | — |
| `050_operation_log.surql` | `operation_log` | — |
| `060_consumed_vaa_ledger.surql` | `consumed_vaa_ledger` | `bridge_tx` (soft audit ref only) |
| `070_idempotency_key_store.surql` | `idempotency_key_store` | — |

## Apply semantics

Each file contains a self-contained block of `DEFINE TABLE` + `DEFINE FIELD` +
`DEFINE INDEX` statements for one aggregate. Apply with:

```
surreal import --conn <url> --user <user> --pass <pass> --ns oasis --db oasis <file>
```

Or via the gated migration job (preferred — ensures versioning and rollback hooks).

## Statement formatting conventions

- One statement per logical block (fields grouped under their table definition).
- Each statement ends with a `;`.
- Comments use `--` on their own line, never inline after a `DEFINE` keyword —
  this prevents accidental statement truncation in some parser versions.
- Field blocks sorted: required fields first, optional fields after, then indexes.

## Adding a new schema file

1. Choose the next available prefix (e.g. `080_<table>.surql`).
2. Add `DEFINE TABLE <name> SCHEMAFULL;` — no exceptions.
3. Add `DEFINE FIELD` for every persisted field with a `TYPE` clause.
4. Add `DEFINE INDEX` for natural keys and idempotency keys (`UNIQUE`).
5. Update the allowlist in
   `tests/OASIS.WebAPI.Tests/SurrealDb/Schemas/SchemaFilesParseTest.cs`
   (`AllowedTableNames` array) — the test will fail if your table is missing.
6. Add required fields to `SchemaShapeTest.cs` (`ExpectedTableFields`).
7. Run `dotnet test tests/OASIS.WebAPI.Tests` — both schema test classes must pass.

## Quest tables — DEFERRED

**Do NOT add quest tables here until the quest-temporal-fork-model track's
`SURREAL-SCHEMA-HINTS.md` is merged.**

The following tables are reserved for that track and must NOT appear in any
file under this directory until the hand-off document is merged:

- `quest`
- `quest_node`
- `quest_edge`
- `quest_run`
- `quest_node_execution`
- `quest_template`
- `quest_dependency`
- `quest_node_template`

The schema parse test (`SchemaFilesParseTest.NoSchemaFile_ContainsForbiddenQuestTables`)
enforces this and will fail if any of these tables are added prematurely.

## Deferred tables (wave 2+)

- **holon** — polyhierarchy remodel via `RELATE` edges (plan task 10); may
  remain schemaless for flexible attributes. Wave 2 decides.
- **avatar** — flexible enough that schemaless may be the right call; wave 2 decides.
- **saga_step** / **outbox_message** — plan task 8b; added once the SurrealDB
  saga trigger replaces the EF polling implementation.
