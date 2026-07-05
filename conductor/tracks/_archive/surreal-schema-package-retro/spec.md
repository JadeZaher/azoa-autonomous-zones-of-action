# Surreal Schema Package Retro — Specification

## Goal

Retrospective and as-built reference for the C#-first schema pipeline that
replaced `surrealdb-schema-source-gen` on 2026-06-03. This track produces no
new code; it closes by absorbing the as-built architecture into
`Persistence/SurrealDb/CONVENTION.md` and fixing the stale references in
`RUNBOOK.md` §4 and §8 that still point at the deleted Mermaid source directory
and the removed `Azoa.SurrealDb.SourceGen` package.

## Background

### What the old pipeline was

`surrealdb-schema-source-gen` (Tier 1.6, now Shipped/SUPERSEDED) described a
Roslyn `IIncrementalGenerator` that read `.mermaid` ER diagrams from
`Persistence/SurrealDb/Schemas/source/` and emitted C# POCOs with typed
`SurrealQuery<T>` and `RecordId<T>` wrappers. The pipeline was Mermaid-first:
diagrams were the authoritative source of truth; C# was the generated output.
The spec described 889+ tests and ongoing FK-emission work.

### What changed on 2026-06-03

The pipeline was inverted. `Persistence/SurrealDb/Schemas/source/` (the Mermaid
source directory) was deleted. `Azoa.SurrealDb.SourceGen` was emptied of source
files (only `bin/` + `obj/` artifacts remained; confirmed by Lane C's execution
on 2026-06-10). The new authoritative flow is:

```
Decorated POCOs in Persistence/SurrealDb/Models/*.cs
  → azoa-surreal generate-from-assembly <dll>
      → AttributeSchemaScanner (Azoa.SurrealDb.Schema)
          → SurqlEmitter       → Persistence/SurrealDb/Generated/Schemas/*.surql
          → MermaidFlowchartEmitter → Persistence/SurrealDb/Generated/Flowcharts/*.flowchart.mermaid
```

C# is now the source of truth; Mermaid diagrams are generated output, not input.

### Why the pivot happened

1. **Authoring friction.** Maintaining `.mermaid` diagrams in parallel with the
   consuming C# code required a double-edit on every schema change. The C# POCO
   is written first in practice; the diagram was an annotation layer that lagged
   behind.
2. **Parser ownership.** A hand-rolled Mermaid ER parser is a non-trivial
   dependency. The C# attribute surface (`[SurrealTable]`, `[SurrealIndex]`,
   `[SurrealRelation]`) is free — Roslyn already understands it.
3. **Roslyn model fit.** `IIncrementalGenerator` is built for in-IDE incremental
   compilation of C# inputs. Running it over non-C# source files (`.mermaid`)
   fought the incremental model and required custom change-detection.
4. **Drift risk.** If `generate-from-assembly` failed silently, the generated
   `.surql` files would drift from the POCOs with no compile-time signal. Moving
   the source to C# attributes makes drift visible as a build error.
5. **Greenfield advantage.** No live customers, no production data (memory:
   `greenfield-prelaunch-no-compat`) — the pivot cost was bounded to one session.

## As-built architecture (authoritative as of 2026-06-10)

### Attribute surface

`packages/Azoa.SurrealDb.Client/Schema/SurrealAttributes.cs`

Three sealed attribute classes decorate POCOs:

- `[SurrealTable(name, schemafull, aggregate)]` — maps a C# class to a SurrealDB
  table. `schemafull` defaults to `true`. `aggregate` names the bounded-context
  slice used by `MermaidFlowchartEmitter` for per-slice flowchart grouping.
- `[SurrealIndex(...)]` — declares an index on one or more fields.
- `[SurrealRelation(...)]` — marks an edge table (RELATE semantics); declares
  `in` and `out` target tables.

### POCOs

`Persistence/SurrealDb/Models/*.cs` — 26 classes as of 2026-06-10:

`ApiKey`, `Avatar`, `BridgeTx`, `ConsumedVaaLedger`, `DappSeries`,
`DappSeriesQuest`, `Executes`, `ForkedFrom`, `HnswHolonEmbedding`,
`HnswQuestEmbedding`, `Holon`, `IdempotencyKeyStore`, `NftOwnership`,
`OperationLog`, `Quest`, `QuestDependency`, `QuestEdge`, `QuestNode`,
`QuestNodeExecution`, `QuestNodeTemplate`, `QuestRun`, `QuestTemplate`,
`SagaSteps`, `StarOdk`, `SwapState`, `Wallet`.

### Generator

`packages/Azoa.SurrealDb.Schema/Generator/AttributeSchemaScanner.cs`

Reflects over a compiled assembly via `azoa-surreal generate-from-assembly <dll>`.
Finds all types decorated with `[SurrealTable]` and projects them onto the
internal `SchemaModel` shape. This feeds both emitters.

`packages/Azoa.SurrealDb.Schema/Generator/SurqlEmitter.cs`

Converts `SchemaModel` to SurrealQL DDL. One `.surql` file per table.
Output: `Persistence/SurrealDb/Generated/Schemas/*.surql` (26 files).

`packages/Azoa.SurrealDb.Schema/Generator/MermaidFlowchartEmitter.cs`

Converts `SchemaModel` to Mermaid ER flowcharts. Produces one diagram per
aggregate slice plus a master `domain` diagram.
Output: `Persistence/SurrealDb/Generated/Flowcharts/*.flowchart.mermaid` (7 files):
`bridge`, `dapp_composition`, `domain`, `identity`, `quest`, `quest_templates`,
`wallet_nft`.

### CLI

`packages/Azoa.SurrealDb.Schema/Migration/MigrationRunner.cs`

Commands available via the `azoa-surreal` tool:

| Command | Status |
|---------|--------|
| `generate-from-assembly <dll>` | Implemented |
| `migrate up` | Implemented |
| `migrate status` | Implemented |
| `migrate dry-run` | Implemented |
| `migrate reset` | Implemented |
| `migrate down` | Stubbed — manual rollback only (intentional pre-launch) |

### Acceptance gate

`AttributePocoByteEquivalenceTests` — verifies that the generated `.surql` bytes
produced from the decorated POCOs are stable across runs. This replaces the old
Roslyn generator test suite that was deleted with `Azoa.SurrealDb.SourceGen`.

### Packages remaining in the tree

| Package | Status |
|---------|--------|
| `packages/Azoa.SurrealDb.Client/` | Active — attributes, query builder, connection, JSON |
| `packages/Azoa.SurrealDb.Schema/` | Active — scanner, emitters, migration runner, CLI |
| `packages/Azoa.SurrealDb.Analyzer/` | Active — SRDB0001 Roslyn diagnostic (injection prevention) |
| `packages/Azoa.SurrealDb.SourceGen/` | Removed by Lane C (only `bin/`+`obj/` remained) |

## SRDB0001 analyzer (orthogonal — keep)

`packages/Azoa.SurrealDb.Analyzer/SurrealQlSafetyAnalyzerDiagnostic.cs`

Bans string-interpolated or concatenated SurrealQL outside the parameterized
`SurrealQuery` builder. This rule is orthogonal to schema generation — it
catches injection risks at compile time regardless of whether the schema source
is Mermaid or C#. It should be kept and is not affected by this retro.

## Lessons / principles

1. **Invert pipelines early in greenfield work.** When the natural authoring
   order is C# → diagram but the pipeline runs diagram → C#, the friction is
   constant. If there are no users to migrate, pivot before the gap compounds.
2. **Source of truth lives where authoring naturally happens.** For a .NET
   project, that is C# code. Diagrams are a useful visualisation output; they
   are not a useful authoring input when the consumer is a compiler.
3. **Visualisation output is fine; visualisation input is a trap.** Mermaid
   flowcharts generated from attributes are low-cost, high-value documentation.
   Mermaid diagrams as the *source* that drives code generation require a parser
   that the team must own and maintain indefinitely.
4. **A Roslyn analyzer that catches a specific failure mode is worth keeping.**
   SRDB0001 costs nothing to maintain and prevents a real class of injection
   bugs. Its value is independent of how the schema is authored.

## Acceptance criteria

- [ ] `Persistence/SurrealDb/CONVENTION.md` exists and contains the as-built
  architecture description (attribute surface, POCO authoring walkthrough,
  `azoa-surreal generate-from-assembly` invocation) so future contributors do
  not need to read this retro to understand the system.
- [ ] `RUNBOOK.md` §4 stale references to `Persistence/SurrealDb/Schemas/source/`
  (deleted Mermaid source directory) are updated to point at the C#-first surface
  (`Persistence/SurrealDb/Models/` + `azoa-surreal generate-from-assembly`).
- [ ] `RUNBOOK.md` §8 stale references to `Azoa.SurrealDb.SourceGen` package
  are updated to `Azoa.SurrealDb.Schema` (the active package).
- [ ] `conductor/tracks/surrealdb-schema-source-gen/spec.md` has a SUPERSEDED
  banner at the top pointing at this retro track.
- [ ] Grep across the repo for any remaining references to the deleted
  `Persistence/SurrealDb/Schemas/source/` path or `Azoa.SurrealDb.SourceGen`
  package name returns zero hits outside of historical documents (this spec,
  the old spec, SIGN-OFF notes).
- [ ] `tracks.md` row for `surreal-schema-package-retro` moves to `[x]` Shipped.

## Out of scope

- Re-litigating the Mermaid-first vs C#-first decision.
- New schema features (FK-emission, relationship parsing enhancements).
- Changes to `AttributeSchemaScanner`, `SurqlEmitter`, or `MermaidFlowchartEmitter`.
- Documentation work beyond `CONVENTION.md` and the two RUNBOOK sections.

## Tier

Tier 1.6 — matching the superseded `surrealdb-schema-source-gen` track.
Knowledge capture and documentation only; no executable changes.

## Dependencies

- Lane B (surrealql-toolkit archive) and Lane C (SourceGen package removal) are
  predecessors in cleanup scope. This track absorbs their cleanup into the
  canonical doc. Either close first or run in parallel; this track's doc work is
  independent of their deletion work.
- No runtime or build dependencies.
