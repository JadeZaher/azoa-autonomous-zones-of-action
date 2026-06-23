# SurrealQL Toolkit — Strategic ADR + Program

## Status
ADR + umbrella program. Created 2026-05-27. **Tier 4 (product
direction).** This track is *strategic, not implementation*: it names
the constituent tracks, declares the principles, and sequences the
program. Constituent tracks ship as discrete units; this track is the
north star they ladder into.

## Decision
Build `azoa-surreal` into a **Prisma-CLI-like developer experience
focused on SurrealQL / graph databases**. The pieces already in the
codebase — Mermaid-source schema authoring, Roslyn-emitted typed
POCOs, the `MigrationRunner`, the typed `SurrealQuery<T>` builder,
the `aggregates` slice emitter — are not coincidentally Prisma-shaped.
They were assembled to solve AZOA's needs but together they cover
~40% of what Prisma offers. The strategic call: invest in turning
them into a coherent toolkit because (a) the missing 60% is
infrastructure AZOA needs anyway, (b) Prisma has no first-class graph
story so there is a real niche, (c) public packaging of the toolkit is
a credible side-product even if AZOA is the only consumer for now.

## Vision
**"Prisma for SurrealQL with first-class graph semantics."**

**Authoring surface — see [DESIGN-mermaid-portfolio.md](DESIGN-mermaid-portfolio.md)
(authored 2026-05-27).** Schema is authored as a *portfolio of
Mermaid diagrams*: `erDiagram` for entity shape + FK, `flowchart`
for state machines (driving enum + transition-validator codegen),
`requirementDiagram` for guardrails (driving traceability runbook
codegen). Multiple diagrams per file; one main + many domain files
per the Prisma multi-file convention.

A developer using SurrealDB should be able to:
1. **Author** a schema as a single source of truth (Mermaid, with
   graph-native edge/RELATE syntax)
2. **Generate** typed clients (today: C# POCOs + typed query builder;
   future: TS, Rust)
3. **Migrate** schema (DDL) + data (backfills) safely across
   environments
4. **Introspect** a running DB (drift detection, studio-style browse,
   query EXPLAIN)
5. **Reverse-engineer** existing DBs into schema (`db pull`)
6. **Visualize** the data model (aggregate slices + master diagram —
   shipped 2026-05-27 as Phase B)
7. **Iterate** with a fast feedback loop (watch mode, dev seed data,
   isolated namespaces per test)

…all with a single `azoa-surreal` CLI and the toolkit packaged as
public NuGet (timeline gated on stability + AZOA dogfooding —
nominally 3-6 months post-launch).

## Why now
The codebase has been heading this direction since `surrealdb-client-package`
(homebake SDK + analyzer + schema package, Tier 1.5) and
`surrealdb-schema-source-gen` (Roslyn source gen, Tier 1.6). What
those tracks built **is** the proto-toolkit. Naming the program makes
the next investments cohere rather than feeling ad-hoc.

Three signals motivate codifying now:
1. **Phase C** (RUNBOOK §4.3 Phase C — generator FK emission to
   `record<table>`) is generator surgery that has Prisma-CLI-shaped
   downstream consumers (the `data-backfill-migrations` track exists
   *specifically* to handle the row-rewrite that Phase C necessitates).
   Treating the connected work as a program lets us sequence it
   intentionally.
2. **F6 backfill** (surrealdb-migration SIGN-OFF) is the first
   concrete operator workflow that doesn't fit the existing
   schema-migration runner. Solving it well requires the backfill
   primitive; solving it generically requires the toolkit framing.
3. **MCP-shaped consumers** (the `mcp-surface` track shipped 2026-05-25
   demonstrates that *agents* want introspection / typed access to
   SurrealDB content as much as humans do). A studio surface that
   serves both humans and MCP clients is a natural extension of the
   same primitives.

## Principles (immutable across constituent tracks)
1. **Mermaid is the source of truth.** All other artifacts (POCOs,
   `.surql`, slice diagrams, drift reports, studio metadata) are
   derived. No multiple-source-of-truth states; the generator catches
   drift.
2. **Strict namespacing on annotations.** `@surreal.*` directives are
   gate-listed in `MermaidParser.KnownDirectives`; unknown directives
   are hard parse errors. Drift between authored intent and consumed
   intent is impossible.
3. **Deterministic emit.** Every artifact-emit step is a pure
   function of inputs — byte-stable across machines (already proved
   for `SurqlEmitter` + `AggregateEmitter`). Diffs are signal, not
   noise.
4. **G3 injection safety propagates everywhere.** Every code path
   that takes a runtime string and weaves it into SurrealQL routes
   through `SurrealIdentifier` validation. The Analyzer (SRDB0001) is
   the live enforcement layer; the toolkit's runtime APIs preserve it.
5. **Insert-wins primitives for state ledgers.** `schema_migration`,
   `data_migration`, future `drift_check`, `studio_session` — every
   "did-this-happen?" ledger uses the same conditional-UPDATE pattern
   the G2 idempotency contract relies on.
6. **Homebake, minimize deps.** No new external NuGet packages
   gratuitously. The toolkit ships with the same dependency tree as
   the rest of the AZOA surface.
7. **Graph-native by default.** RELATE-edge tables, recursive `->`
   traversals, and HNSW vector indexes are first-class — not bolted on
   to a table-relational mental model. This is the differentiator vs
   Prisma's relational core.

## Constituent tracks

### Shipped (Tier 1-2 foundation, retroactively part of the program)
- **`surrealdb-client-package`** (`[x]`) — homebake `Azoa.SurrealDb.Client` /
  `.Schema` / `.Analyzer`. The toolkit's runtime + parser + safety
  layers.
- **`surrealdb-schema-source-gen`** (`[~]`) — Roslyn generator emitting
  POCOs + typed query builder from Mermaid. The codegen pillar.

### Active
- **`surrealql-aggregates`** (folded into RUNBOOK §4 Phase B) — slice
  diagram emitter; visualization pillar. Phase B shipped 2026-05-27
  (`137992c`); Phase C (generator multi-table + FK emission) follows.

### Pending — discrete deliverables, each shippable on its own
- **[`data-backfill-migrations`](../data-backfill-migrations/spec.md)** —
  C# backfill modules registered with `azoa-surreal backfill apply`.
  First concrete consumer = F6 FK rewrite (Phase 2 of that track).
- **[`surrealql-drift-detection`](../surrealql-drift-detection/spec.md)** —
  diff a deployed namespace against the local Mermaid sources;
  produce a human-readable drift report. Mirrors Prisma `migrate diff`.
- **[`surrealql-studio`](../surrealql-studio/spec.md)** — read-only
  browse / typed query / EXPLAIN UI for a running namespace.
  Avatar-scoped (reuses the `mcp-surface` auth pattern) so it's
  multi-tenant safe.
- **[`surrealql-db-pull`](../surrealql-db-pull/spec.md)** — reverse-
  engineer `.mermaid` sources from a running database namespace.
  Mirrors Prisma `db pull`. Closes the loop for users who land on the
  toolkit via an existing DB rather than a clean slate.
- **[`surrealql-toolkit-packaging`](../surrealql-toolkit-packaging/spec.md)** —
  public NuGet packaging + docs site + samples. Gates on the other
  tracks stabilizing. Nominal timeline: 3-6 months post-AZOA-launch.

## Out of scope (intentionally)
- **Multi-language client codegen** (TS / Rust / Go). Possible future
  expansion but not a foundational requirement; the C# generator
  proves the codegen primitive.
- **Cloud-hosted control plane.** The CLI is the surface; we are not
  building "SurrealDB Cloud."
- **Marketplace of community-authored schemas / migrations.**
  Speculative.

## Success criteria
1. A new developer joining AZOA can set up a local SurrealDB +
   apply schema + run drift check + browse data in ≤10 minutes using
   only `azoa-surreal` subcommands. No README hand-holding beyond
   one-liner installs.
2. A schema change that adds a column emits the new POCO + the new
   `.surql` + an updated slice diagram + a runnable backfill stub —
   in one `azoa-surreal regen` invocation.
3. Drift detection catches any divergence between
   `Persistence/SurrealDb/Schemas/*.surql` and the deployed namespace
   before deploy.
4. Studio surface lets an operator inspect the bridge/saga state
   machines on prod without writing SurrealQL.
5. The toolkit is published to a public NuGet feed with ≥1 external
   user before end of year (success indicator, not gate).

## Sequencing
See [plan.md](plan.md) for phase-by-phase build order. Short version:
backfills next (the F6 hard requirement), then drift detection
(small, high-leverage), then studio (the visible "wow" demo), then
db-pull, then public packaging.

## Related work
- **AZOA product** ([product.md](../../product.md)) — the toolkit is
  a by-product; AZOA remains the primary product. If toolkit-shaped
  investment ever forks AZOA's roadmap, AZOA wins.
- **RUNBOOK §4 Phase C** — the generator FK emission feeds the
  toolkit's "schema → record-typed everything" promise.
