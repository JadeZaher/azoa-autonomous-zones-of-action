# SurrealDB LINQ + Graph Query Layer — Specification

## Status
**`[ ]` NOT STARTED.** Tier 1 (infrastructure / DX). Umbrella track for an
EF-Core-style typed query surface over SurrealDB **extended with SurrealDB-native
graph operations** (RELATE, `->edge->` traversal, FETCH, relationship-based
computation) and a **live-query socket** (`ExecuteLiveAsync`) — the two
capabilities EF Core has no equivalent for and that make this worth more than a
SurrealDB-flavored EF clone.

Branch (to be created off a clean tree once in-flight `api-safety-hardening`
work is committed): **`surreal-linq-graph-query`**.

## Why now / as-built baseline

A read-only audit (2026-06-17) established the honest starting point:

- The **read/query side is ~30% of EF-Core parity; the schema/mapping side is
  ~70%.** The graph layer as a *typed API* is **0%** — it exists only as
  hand-written strings.
- **The translator foundation already exists and works but is dead in
  production.** `Query/ExpressionTranslator.cs` translates
  `Expression<Func<T,bool>>` → parameterized SurrealQL predicate; `SurrealQuery{T}.cs`
  is a typed builder over the untyped `SurrealQuery`. **Both are referenced only
  by their own unit tests** — every production store (`Providers/Stores/Surreal/*`,
  `Services/Sagas/SurrealSagaStore.cs`) hand-writes raw `SurrealQuery.Of("...")`.
  Closing the gap is therefore mostly *finishing + adopting*, not *building from
  scratch*.
- **The graph gap is concrete.** `SurrealQuestRunStore.GetLineageAsync` walks the
  `parent_run_id` scalar chain in a **client-side C# loop** with a comment that
  "the typed builder does not yet have a fluent graph-traversal helper" — even
  though the `forked_from` RELATE edge is written. We write graph edges and never
  read them as a graph. That is exactly this track's headline.
- **The live-query socket is a clean additive extension** but blocked on transport:
  the current transport is HTTP `/sql` (`Connection/HttpSurrealConnection.cs`),
  which is request/response and **cannot carry `LIVE SELECT` push notifications**.
  SurrealDB streams live diffs only over its **WebSocket (RPC) protocol**.
  `ExecuteLiveAsync` needs a second, WebSocket-backed connection alongside the
  HTTP one. This was already foreseen — the `surrealdb-client-package` track
  deferred "1.5b (WebSocket+LIVE+saga adoption)" opportunistically; this track
  is where that lands.

## Parity scorecard (target → done)

| Capability | EF Core | Baseline | This track's target |
|---|---|---|---|
| Parameterized-query safety (analyzer-enforced) | yes | ✅ done (ahead of raw EF) | keep; live + graph inherit it |
| Code-first DDL from POCO attributes | yes | ✅ ~done | unchanged (reuse) |
| Migrations | model-diff scaffold | 🟡 file + checksum, no auto-diff | out of scope (separate track) |
| `Expression<Func<>>` predicate translation | yes | 🟡 built, **unused** | **adopt + broaden** (Phase 1) |
| `IQueryable`/`IQueryProvider` | yes | ❌ none | **deliver** (Phase 2) |
| `DbContext` / `DbSet` / repository seam | yes | ❌ none (hand-rolled per store) | **deliver** (Phase 3) |
| Change tracking / unit-of-work / `SaveChanges` | yes | ❌ none | **deliver** (Phase 3, batched into `SurrealTransaction`) |
| Joins / GroupBy / aggregates / projections | yes | ❌ `.Select` does flat field lists only | **partial** (Phase 2; aggregates Phase 4) |
| Navigation properties / `Include` / FETCH | yes | ❌ FETCH is a string clause, unused | **deliver via FETCH + graph fetch** (Phase 4) |
| **Graph: RELATE / `->edge->` traversal** | N/A (Surreal upside) | ❌ raw-string only; `RelateBuilder` unused | **deliver typed traversal** (Phase 4 — the differentiator) |
| **Live query socket (`ExecuteLiveAsync`)** | N/A (Surreal upside) | ❌ no WS transport | **deliver** (Phase 5 — the differentiator) |

## Goal

Give application code (stores, managers, future SDK) a **typed, composable,
analyzer-safe** query surface over SurrealDB that:

1. Reads like LINQ/EF for the relational 80%:
   `ctx.QuestRuns.Where(r => r.Status == Running).OrderBy(r => r.CreatedAt).Take(20)`
   — deferred, composable, server-translated.
2. Reads like **graph traversal** for the SurrealDB 20%:
   `ctx.QuestRuns.Traverse(r => r.Out<ForkedFrom>().To<QuestRun>())`
   → `->forked_from->quest_run`, with relationship-based computation
   (`Count(r.Out<Member>())`, graph-relative aggregates) on the same arrow-path
   support — so `GetLineageAsync` becomes one traversal query, not a C# loop.
3. Exposes a **live-query subscription socket** for push-based reads:
   `await foreach (var n in ctx.QuestRuns.Where(...).ExecuteLiveAsync(ct))`
   yielding `{ action: Create|Update|Delete, record }` over WebSocket, with
   `KILL` on cancellation.

All three must stay **single-engine** (SurrealDB only), introduce **no broker /
external infra**, and inherit the **SRDB0001 injection guardrail** (the analyzer
already forbids string concatenation — typed/live/graph paths are parameterized
by construction).

## Non-goals (explicit)

- **Not** a from-scratch ORM rewrite. The schema/mapping half (`AttributeSchemaScanner`,
  `SurqlEmitter`, attributes) is reused as-is.
- **Not** migration model-diff scaffolding — that stays the
  `data-backfill-migrations` / a future schema-diff track.
- **Not** a forced cutover of every existing store in this track. Adoption is
  proven on **2-3 representative stores** (lineage being the marquee one);
  full-fleet migration is a follow-up sweep.
- **Not** a public NuGet publish — package publish stays deferred (per
  `surrealdb-client-package`).

## Worked examples (the acceptance shapes)

```csharp
// 1. Relational — deferred, composable
var runs = await ctx.QuestRuns
    .Where(r => r.Status == QuestRunStatus.Running && r.AvatarId == avatarId)
    .OrderByDescending(r => r.CreatedAt)
    .Take(20)
    .ToListAsync(ct);

// 2. Graph traversal — replaces the client-side lineage loop
var lineage = await ctx.QuestRuns
    .Key(runId)
    .Traverse(r => r.In<ForkedFrom>().From<QuestRun>())   // <-forked_from<-quest_run
    .ToListAsync(ct);

// 3. Relationship-based computation
var memberCounts = await ctx.Holons
    .Select(h => new { h.Id, Members = Graph.Count(h.Out<Member>()) })
    .ToListAsync(ct);

// 4. Live subscription socket
await foreach (var n in ctx.QuestRuns
    .Where(r => r.AvatarId == avatarId)
    .ExecuteLiveAsync(ct))         // LIVE SELECT ... over WebSocket
{
    // n.Action is Create | Update | Delete; n.Record is QuestRun
}
```

## Acceptance criteria

1. `ctx.Set<T>()` returns a deferred, composable `IQueryable<T>` whose `Where /
   OrderBy / ThenBy / Skip / Take / Select` translate to one SurrealQL statement,
   materialized only on `ToListAsync / FirstOrDefaultAsync / CountAsync`.
2. The `ExpressionTranslator` covers everything it does today **plus** the
   operators Phase 1 adds (string ops, null handling, `IN`, ranges); unsupported
   expressions throw with the existing "fall back to `SurrealQuery.Of`" recipe —
   never silently produce wrong SQL.
3. A `SurrealContext` (DbContext-equivalent) exposes typed sets, tracks loaded
   entities, and flushes inserts/updates/deletes via one buffered
   `BEGIN..COMMIT` (`SurrealTransaction`) on `SaveChangesAsync`.
4. **Graph:** `Traverse(...)` emits correct `->edge->table` / `<-edge<-table`
   paths; `Relate(...)` (replacing the unused `RelateBuilder` adoption) writes
   edges typed; `GetLineageAsync` is rewritten as a **single traversal query**
   and its existing lineage tests stay green.
5. **Live:** `ExecuteLiveAsync<T>` opens a WebSocket, issues `LIVE SELECT`,
   yields typed notifications as an `IAsyncEnumerable`, and `KILL`s the live
   query on cancellation/dispose. Covered by an integration test against a real
   SurrealDB (gated like the other SurrealDB integration tests).
6. **At least 2-3 production stores adopt the typed surface** (lineage being the
   marquee proof); the rest may stay raw-string and are tracked as a follow-up.
7. `dotnet build` zero new warnings (vs the 28-warning baseline); `dotnet test`
   green including all existing quest + saga + `api-safety-hardening` suites.
8. SRDB0001 analyzer still passes — no new string-concatenated SurrealQL; the
   typed/graph/live builders are the sanctioned construction path.

## Dependencies & ordering

- **Soft dep on `surrealdb-major-upgrade`** (the `1.5.4 → 3.x` cutover): the
  WebSocket/LIVE protocol details and the `surrealdb.net` surface differ across
  majors. Phase 5 (live socket) should land **after** the version pin is settled
  — see `surreal-schema-package-retro` / `surrealdb-3x-upgrade-progress` notes.
  Phases 1-4 (LINQ + graph) are version-independent and can proceed now.
- **Reuses** `surrealdb-client-package` (the runtime), `surreal-schema-package-retro`
  (the mapping half), and the SRDB0001 analyzer — no changes to those required
  beyond additive query types.
- **Unblocks** a future `workflow-sdk` and `durable-saga-orchestration`'s
  "LIVE-query trigger" (which `tracks.md` already names as the convergence point
  for the saga trigger).

See [plan.md](plan.md) for the phased build order and decisions.
