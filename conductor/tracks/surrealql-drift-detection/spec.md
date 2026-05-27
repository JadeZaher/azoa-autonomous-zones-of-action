# SurrealQL Drift Detection — Specification

## Status
Pending. Created 2026-05-27. **Tier 3.** Constituent track of the
[[surrealql-toolkit]] program (Wave 2). See umbrella
[ADR](../surrealql-toolkit/spec.md) for the strategic framing + the
seven principles all constituent tracks share.

## Goal
A `oasis-surreal drift` subcommand that diffs the **deployed schema**
of a SurrealDB namespace against the **locally-authored**
`.surql` sources, producing a human-readable + machine-parsable drift
report. Mirrors Prisma `migrate diff`.

## Why
Today the only way to know a deployed namespace matches the local
schema is to manually re-apply migrations (which would refuse on a
checksum mismatch, but only catches *changes to applied migrations* —
not drift from out-of-band SurrealQL run against the namespace).
Drift detection closes that loop.

## Acceptance
1. `oasis-surreal drift` returns exit 0 when zero drift, exit 1 when
   drift detected. Suitable for CI.
2. Detects: missing fields, added fields, type changes, missing
   indexes, ASSERT changes, missing tables.
3. Detects RELATE/edge-table presence + cardinality drift (the
   graph-native differentiator).
4. Output formats: human-readable diff (`--format text`, default) and
   structured JSON (`--format json`) for tooling.
5. Honors a `.surrealignore` file for fields/tables intentionally
   managed outside source (e.g. `schema_migration` ledger itself).
6. Runs against the testcontainer SurrealDB in <2 seconds on the
   current schema (24 tables).
7. Unit tests cover each drift category with a fake
   `ISurrealConnection` returning canned `INFO FOR DB` payloads.

## Approach
- Reuse `ISurrealConnection` from `Oasis.SurrealDb.Schema`; query
  `INFO FOR DB; INFO FOR TABLE *` to materialize the deployed schema.
- Parse local `.surql` files into a normalized `SchemaModel` (the
  existing `SurqlEmitter` output shape is the canonical form).
- Differ produces a tree of `(table, field, expected, actual,
  drift_kind)` tuples.
- Renderer emits markdown for human format, JSON for tooling.

## Dependencies
- Wave 1 completion (data-backfill-migrations Phase 1) for the
  `data_migration` ledger pattern reuse.
- RUNBOOK §4 Phase C (FK record-type emission) so the differ can
  compare `record<table>` shapes accurately.

## Out of scope
- Auto-generating fix migrations (Prisma's `migrate dev`-style "apply
  the drift back to source" — separate slice; needs human review).
- Cross-version comparison (deployed schema A vs schema B) — first
  ship deployed-vs-local.

See [plan.md](plan.md) for the phase-by-phase build order (TBD —
filled in when Wave 1 lands).
