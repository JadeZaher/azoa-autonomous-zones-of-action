# OASIS Sleek — Runbook

**Last updated:** 2026-06-05 PM2 (dev-up full-stack composition + 2 pre-existing dev-mode boot bugs fixed)
**Branch:** `api-safety-hardening` (7 commits ahead of upstream)
**Last commit:** `137992c feat(mermaid-aggregates): Phase B — generated slice + master diagrams`
**Suite:** 540/540 unit green; 0 errors, 19 warnings (baseline).

This document is the day-to-day reference for the active work. For
historical track-by-track context see [conductor/tracks.md](conductor/tracks.md).
For the SurrealDB entity convention see
[Persistence/SurrealDb/CONVENTION.md](Persistence/SurrealDb/CONVENTION.md).

---

## 1. Status snapshot

### Recently shipped (last 10 commits)

- **`HEAD`** (pending — 2026-06-05 PM2) — **Dev-up full-stack
  composition.** Three new artifacts so a contributor can clone-and-run:
  - [`docker-compose.dev.yml`](docker-compose.dev.yml) — SurrealDB +
    WebAPI + Frontend with healthcheck-gated dependency order. WebAPI
    container's [`docker-entrypoint.sh`](docker-entrypoint.sh) waits for
    SurrealDB to be reachable then runs `oasis-surreal up` (apply
    every committed schema + every Migrations/ file) before launching
    the host. Schema CLI is now shipped in the same image alongside
    `OASIS.WebAPI.dll`.
  - [`dev-up.sh`](dev-up.sh) + [`dev-up.ps1`](dev-up.ps1) — root-level
    orchestrators that auto-detect docker compose v2, docker-compose v1,
    podman-compose, or `podman compose` and bring the stack up. `--logs`
    tails combined output; `--rebuild` rebuilds images; `--clean` wipes
    the surrealdb_data volume.
  - [`dev-down.sh`](dev-down.sh) + [`dev-down.ps1`](dev-down.ps1) —
    teardown helpers with `--wipe` / `-Wipe` to drop the volume.

  Companion: [`appsettings.Development.json`](appsettings.Development.json)
  gets a full `SurrealDb` block (`Endpoint`, `Namespace`, `Database`,
  `User`, `Password`) targeting `http://127.0.0.1:8000` for host-direct
  dev runs that talk to a locally-running SurrealDB instance.

  **Two pre-existing bugs surfaced + fixed during the verification**:
  - `McpToolRegistry` was Singleton consuming Scoped `IMcpTool`
    implementations — invalid captive dependency that crashed
    `BuildServiceProvider` whenever
    `ASPNETCORE_ENVIRONMENT=Development`. Now Scoped (the registry is
    a 5-entry Dictionary so per-request rebuild is free).
  - The boot-time SurrealDB reachability probe used `SELECT 1 AS ok`
    which is invalid SurrealQL (SELECT requires FROM in 1.5+).
    Switched to `RETURN 1` — the SurrealDB-idiomatic no-op.

  End-to-end verified: host-run WebAPI boots against the local
  SurrealDB at `127.0.0.1:8000`, `/health` returns 200 with
  `storage-db: Healthy`, swagger.json serves at /swagger/v1/swagger.json.
- **`HEAD-1`** (2026-06-05 PM) — **Migration `up` CLI + live
  integration test.** New `oasis-surreal up` subcommand applies
  `Generated/Schemas/` then `Migrations/` (lexical order in each, both
  funnel through the `schema_migration` ledger). The runner now
  bootstraps the configured namespace + database on first apply
  (`DEFINE NAMESPACE IF NOT EXISTS` + `DEFINE DATABASE IF NOT EXISTS`)
  so a fresh SurrealDB server needs no out-of-band setup. New
  `MigrationRunnerLiveTests` integration test (in
  `Oasis.SurrealDb.Schema.Tests`, tagged `Category=Live`) applies all
  26 schemas to a live SurrealDB at `localhost:8000` then re-runs to
  prove idempotency. The live test caught **5 real SurrealDB syntax
  errors in the emitter** that the byte-equivalence tests missed:
  - regex operator `=~` is not valid SurrealQL — switched to
    `string::matches($value, "...")`
  - `FLEXIBLE` modifier goes AFTER `TYPE` not before
  - `FLEXIBLE` requires `object` or `array<object>` (bare `array` fails)
  - bootstrap `schema_migration` DDL needed `IF NOT EXISTS`
  - SurrealDB 2.x rejects `UPDATE x:y` on a missing record — switched
    to `UPSERT x:y` which auto-creates
  All five fixes shipped in this session. End-to-end CLI verified
  against the local instance: 26 schemas APPLIED on first run, 26 SKIP
  on second run (clean idempotent no-op). New
  [`Migrations/README.md`](Persistence/SurrealDb/Migrations/README.md)
  documents the data-migration naming convention + authoring rules.
- **`HEAD-1`** (2026-06-05 AM) — **Schema graph + closed-set enums.**
  Every FK column on the 17 POCOs now carries `[References(typeof(Target))]`
  (~30 fields touched). The `.surql` emit flips from `string` to
  `record<target>` (or `option<record<target>>`). The RELATE-edge POCOs
  (`ForkedFrom`, `Executes`) emit as native
  `DEFINE TABLE x TYPE RELATION FROM y TO z`. Closed-set enums emit a
  `DEFINE PARAM IF NOT EXISTS $<table>_<column>` block at the top of the
  file, with `ASSERT $value INSIDE $<table>_<column>` on the field.
  Per-slice flowcharts now render outbound edges (cross-slice targets
  appear as dashed-blue ghost nodes); the master flowchart adds a full
  enum legend listing every closed set with its C# enum type name.
  **Known follow-up**: store adapters under `Providers/Stores/Surreal/`
  still write the bare-hex wire format and need to switch to the
  `"table:hex"` prefixed form before integration tests against a live
  SurrealDB will pass. Unit suite: 567/567 green.
- **`HEAD-1`** (2026-06-03) — **C#-first SurrealDB authoring lands
  end-to-end.** 24 attributed POCOs in `Persistence/SurrealDb/Models/`
  replace the entire Mermaid → POCO source-gen path. New
  `OasisSurrealDbOptions` (Connection + Generation sections) consolidates
  connection + generator-output configuration. Schema/flowchart/DBML
  artifacts emit to `Persistence/SurrealDb/Generated/`. `MermaidParser`,
  `MermaidSchemaModel`, `MermaidParseException`, `AggregateEmitter`, the
  entire `Oasis.SurrealDb.SourceGen` package, and the 24 `.mermaid`
  source files are deleted. Byte-equivalence test
  (`AttributePocoByteEquivalenceTests`) discovers every
  `[SurrealTable]`-decorated POCO at runtime and asserts byte-identical
  `.surql` emit. See §3 (rewritten) for the new convention; CONVENTION.md
  + ANNOTATIONS.md likewise rewritten.
- **`HEAD-1`** (2026-05-27) — Phase C design pivoted to C#-first
  (this is the commit `HEAD` above implements).
- **`2326714`** — Initial design doc landed proposing the Mermaid-
  portfolio model (multi-diagram authoring). Superseded same-day by
  the C#-first pivot above; commit preserved in history for the
  pivot reasoning.
- **`d4b546d`** — `surrealql-toolkit` umbrella ADR + 5 constituent
  pending tracks. **Stale as of evening 2026-05-27** — the
  public-toolkit framing is no longer in scope. Clean-up decision
  deferred to next session.
- **`137992c`** — RUNBOOK §4 Phase B shipped (since retired on
  2026-06-03). Aggregate slice emitter + 24 source `.mermaid` files +
  6 slice diagrams under `docs/aggregates/` + master at
  `docs/domain.generated.mermaid`. Replaced by the C#-first flowchart
  emitter; outputs now land at
  [`Persistence/SurrealDb/Generated/Flowcharts/`](Persistence/SurrealDb/Generated/Flowcharts/)
  in the `graph LR` shape.
- **`9bcfd32`** + **`b66a09f`** — RUNBOOK §4 refined to make slice
  files a generated artifact (not hand-authored); §5 sequencing +
  §6 phase table updated. Both also superseded by the C#-first pivot.
- **`295d67c`** — `mcp-surface` track closed. Read-only MCP server at
  `/mcp` (ModelContextProtocol.AspNetCore 1.3.0) behind JWT+ApiKey
  multi-scheme. 5 tools (quest reachability, holon traversal, NFT graph,
  avatar-scoped read, HNSW vector search). +5 unit tests (540/540 green),
  13 integration tests gated on E1. Write tools deferred; runtime
  evidence + F9–F12 latent-item review pending E1 image fix.
- **`24a7403`** — `surrealdb-migration` Phase D (wave-2 commit).
  3 SurrealQuest stores (1595 LOC) + 6 `.surql` schemas (150/160/170/
  190/200/230) + 28 integration tests. G2 single-winner claim primitive
  + fork write-pairing via BEGIN/COMMIT. DI flipped at
  [Program.cs:267-298](Program.cs#L267-L298). Task 9 closed.
- **`8f1eee1`** — RUNBOOK.md + tracks.md consolidation.
- **`d318bcb`** — `CONVENTION.md` partial-class extension pattern.
- **`92ede75`** — 8 source-gen'd POCOs for quest + dapp-composition;
  dapp-composition slice end-to-end.

### Working tree

Clean — only `conductor/.conductor_session_log` modified
(auto-generated, ignored in commits). Nothing else in flight.

### Active phase

**Phase C — redesigned again, now C#-first.** Two design iterations
landed in this session before settling. The Mermaid-portfolio
direction (multi-diagram authoring across `schema/*.mermaid`) was the
right *response* to the original Phase C scope but solved the wrong
layer. The user steer that resolved it: ".NET-first, OASIS-internal,
no public-toolkit framing, source of truth = decorated C# POCOs."

Short version: schema source = **decorated C# POCOs in this repo**.
Attributes declare shape (FK, indexes, ASSERT inputs, state-machine
binding); partial classes carry validation + domain helpers. The
existing Roslyn source-gen is **inverted** — instead of `.mermaid` →
POCO, it scans the C# attribute surface and emits `.surql` + a single
DBML manifest (`docs/schema.dbml`) + state-machine code + a guardrail
traceability runbook. See
[DESIGN-mermaid-portfolio.md](conductor/tracks/surrealql-toolkit/DESIGN-mermaid-portfolio.md)
(filename retained for git history; contents now describe the
C#-first model).

The pivot is OASIS-internal in scope. The `surrealql-toolkit`
umbrella + 4 sibling tracks (drift, db-pull, studio, packaging) are
stale-but-in-tree pending a clean-up decision next session; the
`data-backfill-migrations` track stays valid (F6 still has to happen).

**This session is design-only.** No code, no file moves. Next
session begins with the prototype slice (`wallet_nft`, 3 entities)
to prove byte-equivalent `.surql` output before mechanically
migrating the rest.

### Pending decisions

- **Phase C trigger** — generator multi-table parsing + FK emission
  lands after Phase B is validated (see §4.3). Recommended order:
  Phase B → Phase C → Phase E (Quest cutover) so the generator settles
  on the new authoring layout before the Quest aggregate moves to
  source-gen'd POCOs.
- **Environment E1 unblocker** — `surrealdb/surrealdb:v1.5.4` slim
  image lacks `surrealkv`; integration tests across the repo
  SkippableFact-skip cleanly. Fix: pin to `v1.5.4-dev` or swap the
  start URI to `rocksdb://data/oasis.db?sync=every`. Affects ALL
  integration tests, not just MCP / wave-2.

---

## 2. Conventions in force

| Convention | Source | Applies to |
|---|---|---|
| SurrealDB entity = hand-authored attributed POCO + partial extensions | [Persistence/SurrealDb/CONVENTION.md](Persistence/SurrealDb/CONVENTION.md) | All SurrealDB-backed aggregates (24 POCOs as of 2026-06-03) |
| No EF Core migrations on new work | [memory/greenfield-prelaunch-no-compat](.claude/projects/c--Users-atooz-Programming-Projects-oasis-sleek/memory/greenfield-prelaunch-no-compat.md) | Pre-launch, no customers/data |
| Integration tests on testcontainer Postgres / SurrealDB | [memory/integration-tests-persistent-postgres](.claude/projects/c--Users-atooz-Programming-Projects-oasis-sleek/memory/integration-tests-persistent-postgres.md) | All `OASIS.WebAPI.IntegrationTests` |
| Bridge tier-0 hardening invariants | [api-safety-hardening RESIDUAL-RISK-RUNBOOK §4](conductor/tracks/api-safety-hardening/RESIDUAL-RISK-RUNBOOK.md) | Bridge value flow |
| TDD on bug fixes + features | [conductor/skills/tdd-workflow](conductor/) | Default |

---

## 3. SurrealDB convention recap (C#-first, post-2026-06-03)

Full doc: [Persistence/SurrealDb/CONVENTION.md](Persistence/SurrealDb/CONVENTION.md).
Attribute reference: [packages/Oasis.SurrealDb.Client/Schema/ANNOTATIONS.md](packages/Oasis.SurrealDb.Client/Schema/ANNOTATIONS.md).

**Source of truth:** decorated C# POCOs in
[Persistence/SurrealDb/Models/](Persistence/SurrealDb/Models/), namespace
`OASIS.WebAPI.Persistence.SurrealDb.Models`. Each POCO is a `partial class`
implementing `Oasis.SurrealDb.Client.ISurrealRecord`, carrying
`[SurrealTable]` + per-field `[Column]` / `[Assert]` / `[Inside]` /
`[Default]` / `[Index]` attributes plus `[JsonPropertyName]` for the
wire shape.

**Generated artifacts** live in
[Persistence/SurrealDb/Generated/](Persistence/SurrealDb/Generated/):
- `Schemas/<table>.surql` — DDL emitted from the attribute scan
- `Flowcharts/<slice>.flowchart.mermaid` + `Flowcharts/domain.flowchart.mermaid` — `graph LR` visualization
- `Dbml/schema.dbml` — DBML diff manifest (opt-in via `OasisSurrealDbOptions.Generation.EmitDbml`)

**Configuration:** [`OasisSurrealDbOptions`](packages/Oasis.SurrealDb.Client/Schema/OasisSurrealDbOptions.cs)
binds to `SurrealDb` in `appsettings.json` with `Connection` + `Generation`
subsections. Env overrides (`OASIS_SURREAL_*`) preserved for CLI invocations.

**Adapter / extension code** lives in sibling partial-class files in
the same namespace — `Persistence/SurrealDb/Models/<Name>.Extensions.cs`
(domain predicates, Guid conversions) or
`Persistence/SurrealDb/Models/<Name>.Validation.cs` (FluentValidation
`OnValidating` hooks). DTOs + in-memory transients stay in
`OASIS.WebAPI.Models.*`.

**Acceptance gate:** [`AttributePocoByteEquivalenceTests`](tests/OASIS.WebAPI.Tests/Persistence/SurrealDb/AttributePocoByteEquivalenceTests.cs)
discovers every `[SurrealTable]`-decorated type at runtime, emits its
`.surql` via the attribute scanner, and asserts byte-identical match
against `Persistence/SurrealDb/Generated/Schemas/<table>.surql`.
Adding a new POCO automatically extends coverage; missing or drifted
golden file fails CI.

**Retired:** the Mermaid pipeline (`MermaidParser`, `MermaidSchemaModel`,
`MermaidParseException`, `AggregateEmitter`, the entire
`Oasis.SurrealDb.SourceGen` package, the 24 `.mermaid` source files,
and the `OASIS_WebAPI.Generated.SurrealDb` namespace) was deleted in
the 2026-06-03 cleanup. The `.surql` files under the legacy
`Persistence/SurrealDb/Schemas/` directory were superseded by the
`Generated/Schemas/` files.

---

## 4. Mermaid visualization restructure (active — Phase B starting)

**Goal:** elevate the 24 isolated single-table `.mermaid` files into a
true visual data model. The user observation: mermaid's value is the
relationship arrows, not the per-table annotations.

**Approach: slice files are GENERATED, not hand-authored.** The 24
`.mermaid` sources remain the single source of truth. Two new pieces
of authoring metadata land inside each source:

1. `%% @surreal.slice "<slice-name>"` on the entity header — declares
   which aggregate slice the entity belongs to.
2. Real Mermaid relationship lines (`a ||--o{ b : "label"`) — the
   `.surql` emitter already ignores `Relationships`, so adding them
   has no runtime effect; they exist purely for the visual layer.

A new `oasis-surreal aggregates` subcommand reads `source/*.mermaid`,
groups entities by `@surreal.slice`, emits one
`docs/aggregates/<slice>.mermaid` per group plus a concatenated
`docs/domain.generated.mermaid` master diagram checked into git so
GitHub renders it inline on the repo landing page.

### 4.1 Target slice membership

| Slice | Entities |
|---|---|
| `quest` | quest, quest_node, quest_edge, quest_dependency, quest_run, quest_node_execution |
| `quest_templates` | quest_template, quest_node_template |
| `dapp_composition` | dapp_series, dapp_series_quest |
| `bridge` | bridge_tx, saga_steps, consumed_vaa_ledger, idempotency_key_store, operation_log |
| `wallet_nft` | wallet, nft_ownership, swap_state |
| `identity` | avatar, api_key, holon, star_odk |

Cross-slice references (e.g. `dapp_series_quest.quest_id` → `quest`)
are declared on the FK-owning side per §7.1; the master diagram shows
them in full while the slice diagrams show them with a clear
"(cross-slice)" label.

### 4.2 Parser + utility changes

- `packages/Oasis.SurrealDb.Schema/Mermaid/MermaidParser.cs` —
  register `slice` in `KnownDirectives` (line ~47). Strict-namespacing
  contract preserved.
- `packages/Oasis.SurrealDb.Schema/Cli/` — new `AggregateEmitter`
  alongside `SurqlEmitter`. Reads a directory of source files, groups
  entities by `@surreal.slice` annotation value, emits one slice file
  per group + the concat master.
- `packages/Oasis.SurrealDb.Schema/Program.cs` — new `aggregates`
  subcommand: `oasis-surreal aggregates --source <dir> --out <dir>`.

### 4.3 Generator changes (Phase C — substantial)

The Roslyn `IIncrementalGenerator` at
[packages/Oasis.SurrealDb.SourceGen/](packages/Oasis.SurrealDb.SourceGen)
needs three updates:

1. **Multi-table per file** — currently parses one entity per
   `.mermaid` via `AdditionalTextsProvider`. Migrate to parse
   multiple `erDiagram` table blocks per file. POCO emission stays
   1:1 with table blocks (one `.g.cs` per table); the change is just
   in the parser.
2. **Relationship parsing** — Phase B already adds the relationship
   lines to source files; Phase C wires them into the schema model
   for FK emission.
3. **FK emission to `.surql`** — emit `ASSERT type::is::record($value,
   <target_table>)` clauses on FK fields, and `DEFINE TABLE
   <edge_name> SCHEMAFULL TYPE RELATION FROM <a> TO <b>` blocks for
   native graph edges. Aligns with surrealdb-migration F6 follow-up
   (FK columns as `record<table>` not bare `string`).

**Sequencing:** Phase B (slice annotations + aggregate emitter) lands
first to validate the visual model. Phase C (generator updates +
multi-table-per-file collapse) lands once Phase B is validated.

### 4.4 Migration of existing 24 files

Phase B leaves the existing
`Persistence/SurrealDb/Schemas/source/*.mermaid` files in place — it
only **adds** `@surreal.slice` annotations + relationship lines. Phase
C may collapse the 24 files into multi-table aggregates if the
parser change makes that ergonomic; the slice annotations remain
valid across either layout because they are entity-level, not
file-level.

---

## 5. Forward sequencing — what unblocks what

```
        ┌─────────────────────────────────────────────┐
        │   Phase B (HERE) — Mermaid aggregate slices  │
        │   @surreal.slice + relation lines on 24       │
        │   source files. `oasis-surreal aggregates`    │
        │   emits 6 docs/aggregates/*.mermaid + master  │
        │   docs/domain.generated.mermaid. Generator    │
        │   POCO/.surql output unchanged.               │
        │   ~2-3h                                       │
        └────────────────────┬───────────────────────┘
                             │ (1) visual model validated
                             ▼
        ┌─────────────────────────────────────────────┐
        │   Phase C — Generator multi-table + FK        │
        │   Parser: multi-table per file                │
        │   Recognize relationship arrows               │
        │   Emit FK ASSERTs + RELATION blocks to .surql │
        │   Migrate generator to read aggregates/       │
        │   Delete 24 single-table .mermaid files       │
        │   ~4-6h                                       │
        └────────────────────┬───────────────────────┘
                             │ (2) generator on new layout
                             ▼
        ┌─────────────────────────────────────────────┐
        │   Phase E — Quest aggregate cutover           │
        │   Partial-class extensions for Quest,         │
        │   QuestNode, QuestEdge, QuestDependency,      │
        │   QuestRun, QuestNodeExecution                │
        │   Delete hand-written Models/Quest/*.cs       │
        │   Rewire wave-2 stores + 34 handlers +        │
        │   755-line QuestManager + tests               │
        │   ~7-9h                                       │
        └────────────────────┬───────────────────────┘
                             │ (3) cutover complete
                             ▼
        ┌─────────────────────────────────────────────┐
        │   Phase F — quest-api endpoint gaps           │
        │   18 missing endpoints + 12 missing manager   │
        │   methods (node/edge/dependency CRUD,         │
        │   activate, execute-next, execution-state,    │
        │   topological-order, complete/fail-quest,     │
        │   instantiate-from-template, publicOnly       │
        │   filter, list-by-status/dappSeriesId)        │
        │   ~2-3h on the post-cutover surface           │
        └────────────────────┬───────────────────────┘
                             │ (4) endpoints close
                             ▼
        ┌─────────────────────────────────────────────┐
        │   Phase G — dapp-composition close-out        │
        │   Integration tests (SurrealDB testcontainer) │
        │   Swagger smoke verification                  │
        │   Track `[~]` → `[x]`                         │
        │   ~1-2h                                       │
        └─────────────────────────────────────────────┘
```

**Why this order:**

1. Phase B before C so we ratify the visual layout before paying
   generator-rewrite cost on the wrong shape.
2. Phase C before E so the Quest cutover targets the final
   generator surface, not a moving one.
3. Phase E before F so the new endpoints sit on the post-cutover
   surface (saves ~3h of duplicate rework).
4. dapp-composition integration tests after the Quest cutover so the
   real Surreal-backed Quest pipeline can drive an end-to-end
   compose → generate → deploy test.

---

## 6. Phased plan (next ~3 weeks)

| Phase | Work | Effort | Status |
|---|---|---|---|
| A. Runbook + tracks consolidation | RUNBOOK.md, tracks.md prune | 1-2h | ✓ Shipped 2026-05-23 (`8f1eee1`) |
| B. Mermaid aggregate slices (visualization-only) | Annotate 24 source `.mermaid` files with `@surreal.slice` + Mermaid relationship lines. Add `oasis-surreal aggregates` subcommand that emits 6 `docs/aggregates/*.mermaid` + `docs/domain.generated.mermaid`. Generator POCO/.surql output unchanged. | 2-3h | ✓ Shipped 2026-05-27 (`137992c`) |
| C. C#-first schema authoring (redesigned again) | Invert the Roslyn source-gen: schema source = decorated C# POCOs (attributes for shape, partial classes for validation). Generator emits `.surql` + `docs/schema.dbml` + state-machine code + guardrail runbook. Mermaid sources retire once byte-equivalent `.surql` is proven on the prototype slice. See [DESIGN-mermaid-portfolio.md](conductor/tracks/surrealql-toolkit/DESIGN-mermaid-portfolio.md) (filename kept for git history; contents now describe the C#-first model). | TBD — sized after prototype slice | **NEXT (prototype `wallet_nft` slice first)** |
| D. Wave-2 commit + integration | Commit the 3 SurrealQuest stores + tests + `230_quest_graph_edges.*`. | 1h | ✓ Shipped 2026-05-27 (`24a7403`) |
| E. Quest aggregate cutover to generated POCOs | Partial-class extensions + delete hand-written + rewire wave-2 stores + 34 handlers + QuestManager + tests. Aliases vanish. | 7-9h | After C |
| F. quest-api endpoint gaps | 18 missing endpoints + 12 missing manager methods on the post-cutover surface | 2-3h | After E |
| G. dapp-composition close-out | Integration tests against testcontainer Postgres + Swagger smoke | 1-2h | After F |
| H. Frontend demo harness `frontend-demo-harness` track | shadcn/ui demo harness, 6 phases | 8-10 days | Independent; can start any time |
| I. `durable-saga-orchestration` Tier 1 | Reusable durable-saga + transactional-outbox module | TBD | After surrealdb-migration (done) |
| J. `mcp-surface` Tier 3 | MCP server over SurrealDB graph | — | ✓ Shipped 2026-05-25 (`295d67c`) |
| K. `surrealql-toolkit` strategic program | Umbrella ADR — "Prisma for SurrealQL with first-class graph semantics." Names 5 constituent tracks (data-backfill-migrations, surrealql-drift-detection, surrealql-studio, surrealql-db-pull, surrealql-toolkit-packaging). Wave 0 foundation already shipped via surrealdb-client-package + surrealdb-schema-source-gen + Phase B. | strategic | After Phase C unlocks Wave 1 |

---

## 7. Open questions / pending decisions

1. **Aggregate boundary for the slice files** — §4.1 proposes 6
   slices. Edge case: `dapp_series_quest` references `quest`
   (different slice). Two answers: (a) declare cross-slice
   relationships in the slice that *owns* the FK side, (b) require a
   master slice for cross-aggregate joins. Default to (a) until we
   hit pain.
2. **Mermaid syntax for cross-aggregate refs** — mermaid `erDiagram`
   does not natively support qualified entity names from other
   diagrams. Workaround: all aggregates emit into the same global
   namespace; the concat step deduplicates entity declarations.
   Document this in CONVENTION.md when Phase B lands.
3. **Concat tooling** — PowerShell vs `dotnet tool` vs MSBuild target.
   PowerShell keeps the dependency surface zero (Windows-native);
   MSBuild target couples it to `dotnet build` so it can't drift.
   Default to MSBuild target (regen on build) since dev-machine
   touches `.mermaid` slices but rarely touches PowerShell.

---

## 8. Where to look for what

| Question | Document |
|---|---|
| "What's the right C# pattern for a new SurrealDB entity?" | [Persistence/SurrealDb/CONVENTION.md](Persistence/SurrealDb/CONVENTION.md) |
| "How do I add a new field to an existing entity?" | The relevant `.mermaid` in [Persistence/SurrealDb/Schemas/source/](Persistence/SurrealDb/Schemas/source/) + rebuild |
| "Where is the source generator?" | [packages/Oasis.SurrealDb.SourceGen/](packages/Oasis.SurrealDb.SourceGen) |
| "What does the API surface look like?" | [conductor/tracks/blockchain-devnet-providers/docs/PROVIDERS.md](conductor/tracks/blockchain-devnet-providers/docs/PROVIDERS.md) |
| "What invariants does the bridge enforce?" | [conductor/tracks/api-safety-hardening/RESIDUAL-RISK-RUNBOOK.md](conductor/tracks/api-safety-hardening/RESIDUAL-RISK-RUNBOOK.md) |
| "What's the quest temporal model?" | [conductor/tracks/quest-temporal-fork-model/ADR.md](conductor/tracks/quest-temporal-fork-model/) |
| "How are quest tables intended to live in SurrealDB?" | [conductor/tracks/quest-temporal-fork-model/SURREAL-SCHEMA-HINTS.md](conductor/tracks/quest-temporal-fork-model/SURREAL-SCHEMA-HINTS.md) |
| "What's the MCP surface look like?" | [conductor/tracks/mcp-surface/CATALOG.md](conductor/tracks/mcp-surface/CATALOG.md) |
| "Which track is which?" | [conductor/tracks.md](conductor/tracks.md) |
