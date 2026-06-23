# SurrealQL db pull — Specification

## Status
Pending. Created 2026-05-27. **Tier 3.** Constituent track of the
[[surrealql-toolkit]] program (Wave 2). See umbrella
[ADR](../surrealql-toolkit/spec.md) for the strategic framing.

## Goal
`azoa-surreal db pull` — reverse-engineer `.mermaid` source files
from a running SurrealDB namespace. Mirrors Prisma `db pull`. Closes
the loop for users who land on the toolkit via an existing DB rather
than a clean slate.

## Why
Today the toolkit assumes you start from `.mermaid` sources and emit
everything else. New users who already have a SurrealDB schema cannot
adopt the toolkit without manually re-authoring every table as
Mermaid. `db pull` removes that adoption friction.

## Acceptance
1. `azoa-surreal db pull --namespace <ns>` reads `INFO FOR DB +
   INFO FOR TABLE *` and writes one `.mermaid` file per table to a
   target directory (default: `Persistence/SurrealDb/Schemas/source`).
2. Output is byte-identical to a hand-authored Mermaid file
   describing the same table — modulo author comments which cannot be
   recovered. Annotations are emitted with sensible defaults
   (`@surreal.schemafull`, `@surreal.assert` from deployed ASSERT
   clauses).
3. RELATE-edge tables emit with `string in / string out` field shape
   matching the existing convention.
4. Indexes preserved (name, fields, uniqueness).
5. `--diff` mode shows what `db pull` would change without writing,
   complementing drift detection.
6. Round-trip test: pull a schema, regenerate `.surql` from the
   pulled `.mermaid`, apply the regenerated `.surql` to a fresh
   namespace, pull again → byte-identical (modulo timestamps).

## Approach
- Reuse the `SchemaIntrospector` from [[surrealql-drift-detection]].
- New `MermaidEmitter` (counterpart to `SurqlEmitter`) walks the
  introspected `LiveSchemaModel` and renders Mermaid.
- Format choices favor the existing repo style (4-space indent,
  `@surreal.fieldgroup` annotations per field, etc.).

## Dependencies
- [[surrealql-drift-detection]] for the introspector.

## Out of scope
- Inferring `@surreal.aggregate` / `@surreal.note` / `@surreal.slice`
  annotations from deployed schema (impossible — these are author
  intent, not deployed state). Pull emits placeholders with
  `TODO: author-supplied` comments.
- Pulling data alongside schema (this is `db pull`, not `db dump`).

See [plan.md](plan.md) for phase-by-phase build order (TBD).
