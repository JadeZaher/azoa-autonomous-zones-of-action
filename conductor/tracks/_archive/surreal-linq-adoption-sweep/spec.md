---
type: track-spec
track: surreal-linq-adoption-sweep
tier: 1
status: pending
supersedes_nongoal_of: surreal-linq-graph-query
created: 2026-07-01
---

# SurrealDB LINQ Adoption Sweep — Specification

## Status
**`[ ]` PENDING.** Tier 1 (DX / internal quality). This is the deferred
**follow-up sweep** that the shipped `surreal-linq-graph-query` track explicitly
carved out as a non-goal ("Not a forced cutover of every existing store in this
track… full-fleet migration is a follow-up sweep" — that track's Non-goal #3,
AC #6). The infrastructure is done; this track is **adoption + gap-closing only**.

## Why now / verified baseline (2026-07-01)

The LINQ-like query layer the user asked about **already exists and works** — it
is simply under-adopted. A read-only census of the production tree (excluding
`tests/` and `packages/`) establishes the honest starting point:

- **Raw `SurrealQuery.Of(...)` production call sites: 12, across 6 files.**
  Broken down by intent:
  - **~4 raw `SELECT` sites** — the migratable read surface. Locations:
    - `Providers/Stores/Surreal/SurrealQuestStore.cs:318`
      (`SELECT * FROM quest_template`)
    - `Providers/Stores/Surreal/SurrealQuestStore.cs:428`
      (`SELECT * FROM quest_node_template`)
    - `Providers/Stores/Surreal/SurrealNftStore.cs:232`
      (`SELECT * FROM holon_nft_binding WHERE avatar_nft_id = $avatar_nft_id`)
    - `Providers/Stores/Surreal/SurrealNftStore.cs:332`
      (`SELECT * FROM wallet_nft_binding WHERE avatar_nft_id = $avatar_nft_id`)
  - **The remaining ~8 sites are writes / transaction control / infra** —
    `BEGIN`/`COMMIT` pairs (`SurrealQuestRunStore.cs:114,119`), conditional
    `UPDATE … WHERE`, `RELATE`, `DELETE`, `LET`-var multi-statements
    (`SurrealBridgeStore.cs`, `SurrealNftStore.cs`), and one health-check probe
    (`Observability/StorageHealthCheck.cs`) + startup DDL (`Program.cs`).
    **These KEEP the raw escape hatch** (user decision, 2026-07-01) — see §Write path.

- **5 stores already use the typed `SurrealQuery<T>` surface**, proving the
  ergonomics in production:
  `SurrealBlockchainOperationStore.cs`, `SurrealHolonStore.cs`,
  `SurrealNftStore.cs`, `SurrealQuestRunStore.cs`, `SurrealWalletStore.cs`.

- **The `Services/Sagas/SurrealSagaStore.cs` conditional-UPDATE primitives are
  deliberately raw** (atomic single-winner `UPDATE … WHERE status = …`, the
  api-safety-hardening exactly-once discipline). They are **out of scope** — see
  §Non-goals.

> **Scope correction.** An initial broad exploration estimated "80–120 raw call
> sites." That figure conflated (a) doc-comment examples, (b) ~305 `.WithParam`
> occurrences (many chained on a single query), and (c) the large *test* corpus.
> The true **production** raw-query surface is **12 `.Of(` sites / 6 files**, of
> which only **~4 are migratable SELECTs**. This track is small and precise, not
> a fleet rewrite. Phase 0 re-verifies this census as its first task so the plan
> can't drift on a stale number.

## The three query tiers (context)

| Tier | Entry point | Style | Today |
|---|---|---|---|
| 1. Raw | `SurrealQuery.Of("SELECT …").WithParam(…)` | hand-written SurrealQL | 12 prod sites |
| 2. Typed (eager) | `SurrealQuery<T>.From().Where(x => …).Limit(…)` | fluent, expression-based | 5 stores |
| 3. LINQ (deferred) | `ctx.Set<T>().Where(…).OrderBy(…).ToListAsync()` | full `IQueryable<T>` | tests only |

Tier 3 (`SurrealContext.Set<T>()` → `SurrealQueryable<T>` →
`ToListAsync/FirstOrDefaultAsync/CountAsync/AnyAsync`) is the "no raw SQL,
reads-like-LINQ" surface. It is fully built
(`packages/Azoa.SurrealDb.Client/Query/`) and wired but has **zero production
consumers**. This track's headline is bringing the migratable read sites up to
Tier 2 or 3.

## Goal

Finish adopting the typed/LINQ query surface for the **read path** so that no
production `SELECT` is hand-written as a raw string unless the translator
genuinely cannot express it — while **preserving the raw escape hatch** for
conditional/atomic writes, transaction control, and DDL.

Concretely:

1. Migrate the ~4 raw `SELECT` sites to Tier 2 (`SurrealQuery<T>`) or Tier 3
   (`ctx.Set<T>()`), whichever is the smaller diff per site.
2. Close any `ExpressionTranslator` gap those real queries expose (e.g. the
   `avatar_nft_id = $x` filters are trivial `==`; verify no site needs an
   operator the translator lacks). Any unsupported shape stays raw with a
   one-line pointer explaining why — **never** a silent wrong translation.
3. Establish `ctx.Set<T>()` (Tier 3) as the **documented default** for new read
   code, with the raw `.Of(...)` path reserved (and labelled) for writes/DDL.
4. Leave a directory-level doc (`Providers/Stores/Surreal/AGENTS.md` §query-surface)
   recording the tier policy so future stores don't reach for raw SELECTs by habit.

## Non-goals (explicit)

- **Not** a rewrite of the query infrastructure — Tiers 2/3 exist and ship. This
  is adoption only.
- **Not** a migration of writes off the raw escape hatch. Conditional `UPDATE …
  WHERE`, `RELATE`, `DELETE`, `LET`-multi-statement, `BEGIN/COMMIT`, and the
  saga-store single-winner UPDATE **stay raw by design** (user decision:
  "keep raw escape hatch"). The analyzer (SRDB0001) already guarantees they are
  parameterized, not concatenated.
- **Not** touching `Services/Sagas/SurrealSagaStore.cs` — its atomic-UPDATE
  primitives are the api-safety-hardening exactly-once discipline and are
  intentionally hand-authored.
- **Not** a graph/live-query change — those shipped in `surreal-linq-graph-query`
  and are already typed where used.
- **Not** a public NuGet publish.

## Worked examples (acceptance shapes)

```csharp
// BEFORE (SurrealNftStore.cs:232) — raw
var q = SurrealQuery.Of("SELECT * FROM holon_nft_binding WHERE avatar_nft_id = $avatar_nft_id")
    .WithParam("avatar_nft_id", link);
var rows = await _executor.QueryAsync<HolonNftBinding>(q, ct);

// AFTER — Tier 2 (typed, smaller diff, same executor path)
var q = SurrealQuery<HolonNftBinding>.From()
    .Where(b => b.AvatarNftId == avatarNftId);
var rows = await _executor.QueryAsync<HolonNftBinding>(q, ct);

// OR AFTER — Tier 3 (deferred LINQ, if a SurrealContext is in scope)
var rows = await ctx.Set<HolonNftBinding>()
    .Where(b => b.AvatarNftId == avatarNftId)
    .ToListAsync(ct);
```

```csharp
// STAYS RAW (SurrealBridgeStore.cs conditional UPDATE) — escape hatch, by design
var q = SurrealQuery
    .Of("UPDATE type::record($_t, $_id) SET status = $_completed, completed_at = $_now WHERE status != $_completed RETURN AFTER")
    .WithParam(...);   // conditional/atomic write — Tier 1 is correct here
```

## Acceptance criteria

1. **Census re-verified.** Phase 0 reproduces the raw-`.Of(` production census
   (grep, excluding `tests/` + `packages/`) and records the exact migratable-SELECT
   list in `plan.md`. If the count differs from this spec's 12/~4, the plan is
   updated before any code changes (no drift on a stale number).
2. **Every migratable raw `SELECT` is converted** to Tier 2 or Tier 3, OR carries
   a one-line comment stating the specific translator limitation that keeps it raw.
   Zero un-annotated raw SELECTs remain in production stores.
3. **Translator gaps (if any) are closed** with unit tests in
   `tests/Azoa.SurrealDb.Client.Tests/Query/`, following the existing
   `ExpressionTranslator` test style. If a real site needs an operator the
   translator lacks, add it there (parameterized) rather than working around it
   at the call site.
4. **Writes untouched.** No conditional UPDATE / RELATE / DELETE / BEGIN-COMMIT /
   saga-store site is changed. Diff shows read-path files only (plus tests + one
   AGENTS.md).
5. **Directory doc updated.** `Providers/Stores/Surreal/AGENTS.md` gains a
   `§query-surface` section stating: Tier 3 (`ctx.Set<T>()`) is the default for
   reads; Tier 2 (`SurrealQuery<T>`) when no context is threaded; raw `.Of(...)`
   is reserved for writes/DDL/atomic-conditional and must be labelled.
6. **Build + test green.** `dotnet build` zero new warnings vs the 28-warning
   baseline ([[build-warning-baseline-2026-06-16]]); `dotnet test` green
   including quest/saga/nft/`api-safety-hardening` suites. SDK `tsc` unaffected
   (no SDK change). Per user policy, run the full sweep **once at the end**, not
   after each site.
7. **SRDB0001 still passes** — no new string-concatenated SurrealQL; typed
   builders are parameterized by construction.

## Write path (user decision, 2026-07-01)

**Keep the raw escape hatch.** Reads migrate to LINQ/typed; conditional and
atomic writes stay as `SurrealQuery.Of(...)`, guarded by the SRDB0001 analyzer.
Rationale: `UPSERT … CONTENT`, conditional `UPDATE … WHERE status = …`,
`type::record()` anchoring, `RELATE`, and `LET`-var multi-statements have no
clean LINQ shape, and the saga store's single-winner UPDATE is a deliberate
exactly-once primitive. Forcing them through a builder would add risk for no
ergonomic gain. This is a pragmatic split, not a coverage gap.

## Dependencies & ordering

- **Hard dep (satisfied):** `surreal-linq-graph-query` `[x]` SHIPPED — provides
  every builder this track adopts. No infra work remains.
- **Reuses:** the SRDB0001 analyzer, `SurrealContext`, `SurrealQuery<T>`,
  `ExpressionTranslator`, the async materializers — all additive-free.
- **Touches no other in-flight track.** Independent, low-risk, resumable.

## Effort estimate

Small. ~4 read-site conversions + at most a handful of translator unit tests +
one AGENTS.md section. Single phase of implementation after a verification
Phase 0. See [plan.md](plan.md) for the phased order.
