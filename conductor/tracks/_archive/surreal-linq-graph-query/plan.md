# SurrealDB LINQ + Graph Query Layer — Plan

Build order: **adopt + broaden the existing translator first (it already works),
then add the `IQueryable` provider on top, then the `DbContext`/unit-of-work seam,
then the graph traversal operators (the differentiator), and finally the
WebSocket live-query socket (the second differentiator, gated on the version
pin).** Every phase keeps the existing **quest**, **saga**, and
**`api-safety-hardening`** suites green — those are the regression gate. Tests
run **once at the end of each phase**, not per-fix (per working preference).

The guiding principle from the audit: **the foundation
(`Query/ExpressionTranslator.cs` + `Query/SurrealQuery{T}.cs`) exists and is
correct but unused.** So Phase 1 is mostly *plumbing it into a real consumer and
filling the operator gaps*, not green-field translator work.

## Decisions

| # | Decision | Choice (recommended) | Rationale / evidence |
|---|----------|----------------------|----------------------|
| **D1** | Build a new query stack or extend the existing one? | **Extend.** Build the `IQueryProvider` directly on top of `SurrealQuery{T}` + `ExpressionTranslator`. | Both already translate predicates correctly and are tested; they are merely unconsumed (`SurrealQuery<` matches only test files outside `packages/`). A rewrite would discard working, analyzer-blessed code. |
| **D2** | `IQueryable` from day one, or finish the eager typed builder first? | **Finish the eager builder + broaden the translator in Phase 1; add `IQueryable` deferral in Phase 2.** | Lets a real store adopt the typed surface immediately (proving the translator) before the deferred-execution machinery exists. De-risks the big provider piece. |
| **D3** | Where does the `DbContext`/unit-of-work flush go? | **Batch `SaveChangesAsync` into the existing buffering `SurrealTransaction`** (`Transaction/SurrealTransaction.cs` — already accumulates statements and flushes one `BEGIN..COMMIT`). | The single-round-trip transaction is the natural unit-of-work boundary; SurrealDB 3.x stateless-HTTP already forces this buffering shape. No new infra. |
| **D4** | Change-tracking depth? | **Lightweight identity map + snapshot-on-load**, NOT full EF proxy/relationship-fixup. Track `[Id]`-keyed entities, diff on `SaveChanges`, emit `CREATE`/`UPDATE CONTENT`/`DELETE`. | Full EF fixup is over-engineered for SurrealDB's record model and the homebake preference (minimize deps). Snapshot diffing covers the 90% case. |
| **D5** | How are graph traversals expressed? | **A fluent `Traverse(r => r.Out<TEdge>().To<TTarget>())` / `.In<>().From<>()` surface** that the translator emits as `->edge->table` / `<-edge<-table` arrow paths. Edges are the existing `[RelateEdge]` POCOs (`ForkedFrom`, `Executes`). | Reuses modeled edges; the translator's `ValidateFieldPath` already permits `->edge` arrow tokens (`SurrealQuery.cs` field-path guard), so the emit side is a known-safe extension. |
| **D6** | Relationship-based computation surface? | **A `Graph.Count(...)` / graph-relative aggregate helper** that rides the same arrow-path emit (`count(->member->)`). | Same emit machinery as D5; keeps graph aggregates and traversals one consistent surface. |
| **D7** | RELATE write path? | **Adopt the existing unused `Query/RelateBuilder.cs`** as the sanctioned typed RELATE; surface it through the context (`ctx.Relate(from).Via<TEdge>().To(to).WithContent(...)`). | The builder is built, parameterized, and tested — it just has no caller. Adoption, not construction. |
| **D8** | Live-query transport? | **New `WebSocketSurrealConnection` alongside the HTTP one** (do NOT bolt LIVE onto `HttpSurrealConnection`). `ExecuteLiveAsync<T>` is a THIRD execution verb on the executor seam (`QueryAsync` one-shot / `ExecuteAsync` mutation / `ExecuteLiveAsync` stream). | HTTP `/sql` physically cannot carry `LIVE SELECT` pushes; SurrealDB streams diffs only over WS RPC. A separate connection keeps the HTTP path untouched and the live path opt-in. |
| **D9** | Live-query .NET surface? | **`IAsyncEnumerable<LiveNotification<T>>`** — `await foreach`, cancellation token issues `KILL <uuid>` + closes the subscription. | Idiomatic .NET streaming; composes with the typed builder (emit `LIVE SELECT` instead of `SELECT`, translator change is just the verb). |
| **D10** | Sequence live before or after the version pin? | **After.** Phases 1-4 are version-independent and proceed now; Phase 5 (live socket) lands once `surrealdb-major-upgrade` settles the WS/RPC + `surrealdb.net` surface. | Soft dep — the WS protocol and SDK surface differ across 1.5/2/3.x (`surrealdb-3x-upgrade-progress`). Building the socket against a moving pin wastes work. |
| **D11** | Adoption scope in THIS track? | **2-3 representative stores**, lineage being the marquee (`GetLineageAsync` raw loop → one traversal query). Full-fleet migration is a follow-up sweep. | Proves every layer end-to-end on real consumers without a churny all-stores rewrite. |

## Phase 1 — Adopt + broaden the translator (make the dead code live)
1. `[ ]` Wire `SurrealQuery{T}` into **one** real store as a pilot (a read-only list query, e.g. a saga or quest-run list) to prove the typed predicate path executes against a real SurrealDB and matches the prior raw-string result byte-for-byte.
2. `[ ]` Broaden `ExpressionTranslator`: ranges (`>= && <=` → SurrealDB range or compound), `IN`/`Contains` over collections → `INSIDE`, `string.StartsWith/EndsWith/Contains` → SurrealQL string ops, null-coalescing/`HasValue`, `DateTime` comparisons. Each unsupported node still throws the existing `NotSupportedException` with the fall-back recipe — never silent-wrong.
3. `[ ]` Unit tests for every new operator (mirror `SurrealQueryTypedTests`); assert the emitted SurrealQL + parameter bag exactly.
4. `[ ]` Keep all existing suites green; SRDB0001 analyzer passes.

## Phase 2 — `IQueryable` / `IQueryProvider` (deferred composition)
5. `[ ]` Implement `SurrealQueryable<T>` + `SurrealQueryProvider` over `SurrealQuery{T}`: `CreateQuery` accumulates the expression tree; `Execute` translates the WHOLE tree (Where/OrderBy/ThenBy/Skip/Take/Select) into one `SurrealQuery` at materialization time.
6. `[ ]` Async materializers: `ToListAsync`, `FirstOrDefaultAsync`, `SingleOrDefaultAsync`, `CountAsync`, `AnyAsync` — each maps to the right SurrealQL (`LIMIT 1`, `count()`, etc.).
7. `[ ]` Projection: `Select(x => new { ... })` and `Select(x => new Dto(...))` translate to field-list / computed SurrealQL; fall back cleanly when the projection isn't translatable.
8. `[ ]` Provider unit tests: deferred (no round-trip until materialized), composability (chained Where ANDs), correct single-statement emission. Suites green.

## Phase 3 — `SurrealContext` (DbContext) + unit-of-work
9. `[ ]` `SurrealContext` base + `DbSet<T>`-equivalent (`SurrealSet<T> : IQueryable<T>`) discovered from `[SurrealTable]` POCOs via the existing `SurrealSchemaRegistry`.
10. `[ ]` Lightweight change tracking (D4): identity map keyed on `[Id]`, snapshot-on-load, `Add/Update/Remove/Attach`.
11. `[ ]` `SaveChangesAsync` diffs tracked entities → buffers `CREATE`/`UPSERT`/`UPDATE CONTENT`/`DELETE` into one `SurrealTransaction` `BEGIN..COMMIT` (D3); returns affected count; honors the single-winner conditional-UPDATE discipline where a concurrency token is present.
12. `[ ]` Context unit tests (insert/update/delete round-trips, identity-map dedup, transaction batching). Suites green.

## Phase 4 — Graph operators (THE DIFFERENTIATOR)
13. `[ ]` Traversal surface (D5): `Traverse(r => r.Out<TEdge>().To<TTarget>())` and `.In<TEdge>().From<TSource>()`; translator emits `->edge->table` / `<-edge<-table`; depth `.{n}` and recursion supported where SurrealQL allows. Reuse the `[RelateEdge]` POCOs.
14. `[ ]` Typed FETCH / eager-load: `.Fetch(r => r.SomeLink)` emits a real `FETCH` clause (today's string clause is unused) so a link column materializes its target — the `Include`-equivalent.
15. `[ ]` Relationship-based computation (D6): `Graph.Count(x.Out<TEdge>())` and graph-relative aggregates emit `count(->edge->)` etc. inside a projection.
16. `[ ]` Adopt typed RELATE (D7): surface `Query/RelateBuilder.cs` through the context; write a `forked_from` edge via the typed path.
17. `[ ]` **Marquee proof:** rewrite `SurrealQuestRunStore.GetLineageAsync` from the client-side `parent_run_id` loop into ONE `<-forked_from<-quest_run` traversal query; its existing lineage tests stay green and now exercise a real graph read. Adopt the typed surface in 1-2 more stores (D11).
18. `[ ]` Graph unit + integration tests (arrow-path emit correctness; lineage traversal against a real SurrealDB). Suites green.

## Phase 5 — Live-query socket `ExecuteLiveAsync` (gated on version pin — D10)
19. `[ ]` `WebSocketSurrealConnection` (D8): WS RPC connect/auth/use-ns-db, send `LIVE SELECT`, receive push frames, `KILL` on teardown. Lives alongside `HttpSurrealConnection`; opt-in, does not touch the HTTP path.
20. `[ ]` `ExecuteLiveAsync<T>(SurrealQuery, ct)` → `IAsyncEnumerable<LiveNotification<T>>` (D9): the third executor verb; typed deserialization of `{ action, record }`; cancellation issues `KILL <uuid>` and completes the stream.
21. `[ ]` Typed entry point: `ctx.Set<T>().Where(...).ExecuteLiveAsync(ct)` — same builder, emit `LIVE SELECT` instead of `SELECT` (verb swap in the translator).
22. `[ ]` Integration test against a real SurrealDB (gated like other SurrealDB integration tests): subscribe → mutate in a second connection → assert the matching Create/Update/Delete notifications arrive → cancel → assert `KILL` and stream completion.
23. `[ ]` Confirm SRDB0001 covers the live path (parameterized by construction). Suites green.

## Verification (run ONCE at the end of each phase, and a full sweep at track close)
24. `[ ]` `dotnet build` — zero new warnings vs the 28-warning baseline.
25. `[ ]` `dotnet test` — green incl. all existing quest / saga / `api-safety-hardening` suites + the new translator/provider/context/graph/live tests.
26. `[ ]` SRDB0001 analyzer passes — no new string-concatenated SurrealQL.
27. `[ ]` `GetLineageAsync` is a single traversal query (no client-side loop); ≥2-3 stores on the typed surface.
28. `[ ]` `ExecuteLiveAsync` proven end-to-end against a real SurrealDB.
29. `[ ]` spec.md scorecard updated to as-built; SurrealDB sole engine; no broker / external infra introduced.

## Launch / dev helper
A `launch-surreal-linq.ps1` / `.sh` pair at the repo root (added with this track)
builds the three packages, runs the package + new query/graph/live test
projects, and (for Phase 5) brings up a SurrealDB to exercise the live socket.
See the script header for flags. This is the "iterate on the query layer without
spinning the whole dev stack" loop.
