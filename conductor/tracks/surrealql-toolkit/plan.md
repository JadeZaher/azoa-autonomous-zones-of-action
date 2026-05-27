# SurrealQL Toolkit — Program Plan

This is the umbrella sequencing layer. Each entry maps to a discrete
constituent track that owns its own spec + plan. The umbrella's job is
to keep the constituent tracks sequenced so they ladder cleanly into
the vision (see [spec.md](spec.md)).

## Wave 0 (already shipped — foundation, retroactively named)
- ✅ `surrealdb-client-package` — runtime + parser + analyzer
- ✅ `surrealdb-schema-source-gen` Phase 1-6 — Roslyn POCO + query builder
- ✅ `oasis-surreal` CLI (migrate / generate / validate)
- ✅ Aggregate slice diagrams (RUNBOOK §4 Phase B, `137992c`)

## Wave 1 — Close the schema → row gap (current)
Tracks that finish the schema-side codegen story so a developer can go
from `.mermaid` to a populated, type-safe row with no manual SurrealQL.

1. [ ] **RUNBOOK §4 Phase C — generator multi-table + FK emission**
   (not a separate track; lives in `surrealdb-schema-source-gen`). FK
   columns emit as `record<target_table>` not `string`. Unlocks the
   typed traversal story.
2. [ ] **[data-backfill-migrations](../data-backfill-migrations/spec.md)**
   Phase 1 — backfill runner shell + `data_migration` ledger + CLI.
3. [ ] **data-backfill-migrations Phase 2** — F6 FK rewrite (depends
   on §4 Phase C). First real backfill consumer.

## Wave 2 — Close the dev feedback loop
Tracks that make iterating on a schema feel like Prisma — drift
detection + db-pull + watch mode.

4. [ ] **[surrealql-drift-detection](../surrealql-drift-detection/spec.md)** —
   diff deployed namespace vs local `.surql`. `oasis-surreal drift`.
5. [ ] **[surrealql-db-pull](../surrealql-db-pull/spec.md)** —
   reverse-engineer `.mermaid` from a running namespace.
   `oasis-surreal db pull`.
6. [ ] **Watch mode** (folded into `surrealdb-schema-source-gen` or
   its own slice) — `oasis-surreal watch` regen's POCOs + slice
   diagrams on file change.

## Wave 3 — Observability + studio
The "wow" surface that lets non-developers (operators, support, even
agents via MCP) interact with the data without writing SurrealQL.

7. [ ] **[surrealql-studio](../surrealql-studio/spec.md)** — read-only
   browse + typed query + EXPLAIN. Avatar-scoped via `McpAuthMiddleware`
   pattern.
8. [ ] **MCP integration of studio primitives** — extend the
   `mcp-surface` track with structured-introspection tools (table
   list, schema describe) leveraging studio's metadata layer.

## Wave 4 — Public packaging
Only meaningful once Waves 1-3 stabilize + OASIS dogfoods the surface
in prod for ≥3 months.

9. [ ] **[surrealql-toolkit-packaging](../surrealql-toolkit-packaging/spec.md)** —
   public NuGet packaging + docs site + sample repos + versioning
   policy + breaking-change SLA.

## Cross-wave concerns (tracked here, not in constituent tracks)
- **Versioning** — `oasis-surreal` follows semver; pre-1.0 while OASIS
  is the only consumer; 1.0 lands with Wave 4.
- **Telemetry** — every CLI invocation emits an OpenTelemetry span so
  dogfooding produces real usage data. Telemetry sink is opt-in for
  public packaging.
- **Docs** — every constituent track contributes a `docs/`
  fragment that the packaging track collates into a unified site.

## Anti-goals (decisions to refuse on principle)
- **Don't ship Wave 3 before Wave 1 + 2.** Studio without drift
  detection is a "look at the data" toy; the value compounds when
  iterating, and you only iterate well with drift + db-pull in place.
- **Don't grow the dependency surface gratuitously.** Each new
  external package needs an ADR-style "why not homebake?" answer in
  the constituent track's spec.
- **Don't fork OASIS resourcing.** When a wave would compete with an
  OASIS-customer-facing slice, OASIS wins. Toolkit is a
  by-product, not a primary product.
