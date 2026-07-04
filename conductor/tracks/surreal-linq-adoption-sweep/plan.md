---
type: track-plan
track: surreal-linq-adoption-sweep
status: pending
created: 2026-07-01
---

# SurrealDB LINQ Adoption Sweep — Plan

Phased build order for [spec.md](spec.md). This is an **adoption** track: the
query infrastructure (Tiers 2 & 3) already ships from `surreal-linq-graph-query`.
Small, single implementation phase gated by a verification phase.

## Phase 0 — Re-verify the census (no code changes)

**Goal:** lock the exact migratable-SELECT list so the plan can't drift on a
stale number. The spec asserts 12 raw `.Of(` production sites / ~4 migratable
SELECTs — confirm before touching anything.

Tasks:
1. Run the census grep (documented so it's reproducible):
   ```bash
   # all production raw .Of( sites (excl tests + packages)
   grep -rn 'SurrealQuery\.Of(' --include='*.cs' . \
     | grep -viE '/(tests|Tests)/|/packages/'
   ```
2. Classify each hit: `SELECT` (migratable) vs write/DDL/txn (stays raw).
3. For each migratable SELECT, record: file:line, current SurrealQL, target POCO
   type, whether a `SurrealContext` is already in scope at the call site
   (→ Tier 3) or not (→ Tier 2), and the exact `Where` predicate shape.
4. For each migratable SELECT, check the predicate against `ExpressionTranslator`
   support (see `packages/Azoa.SurrealDb.Client/Query/ExpressionTranslator.cs`
   header for the supported operator list). Flag any that need a new operator.
5. **Write the confirmed list into this plan's "Phase 0 findings" section below**
   before proceeding. If the count differs from the spec, note the delta.

**Decision gate:** if any migratable SELECT needs a translator operator that does
not exist, that operator's addition becomes a Phase 1 sub-task with its own unit
test; if it's genuinely inexpressible (e.g. an exotic SurrealQL function), the
site **stays raw with a one-line pointer comment** and is struck from the
migration list. No silent wrong translation, no client-side eval.

### Phase 0 findings (fill in during execution)

_Expected (from spec, to be confirmed):_

| file:line | current SurrealQL | POCO | ctx in scope? | tier | translator gap? |
|---|---|---|---|---|---|
| SurrealQuestStore.cs:318 | `SELECT * FROM quest_template` | QuestTemplate POCO | verify | 2 or 3 | none (bare From) |
| SurrealQuestStore.cs:428 | `SELECT * FROM quest_node_template` | QuestNodeTemplate POCO | verify | 2 or 3 | none (bare From) |
| SurrealNftStore.cs:232 | `SELECT * FROM holon_nft_binding WHERE avatar_nft_id = $x` | HolonNftBinding | verify | 2 or 3 | none (`==`) |
| SurrealNftStore.cs:332 | `SELECT * FROM wallet_nft_binding WHERE avatar_nft_id = $x` | WalletNftBinding | verify | 2 or 3 | none (`==`) |

_Note:_ the two bare `SELECT *` template reads are the lowest-risk starters —
no WHERE, so `SurrealQuery<T>.From()` with no predicate is a one-line swap.
Confirm the POCO types exist and implement `ISurrealRecord`.

## Phase 1 — Migrate the read sites

**Goal:** convert each confirmed migratable SELECT to Tier 2/3 per the Phase 0
table. One site per commit-sized change; apply all, then verify once (per user
policy — no test-per-fix loop).

Order (lowest → highest risk):
1. `SurrealQuestStore.cs:318` — bare template read → `SurrealQuery<QuestTemplate>.From()`.
2. `SurrealQuestStore.cs:428` — bare template read → `SurrealQuery<QuestNodeTemplate>.From()`.
3. `SurrealNftStore.cs:232` — `.Where(b => b.AvatarNftId == avatarNftId)`.
4. `SurrealNftStore.cs:332` — `.Where(b => b.AvatarNftId == avatarNftId)`.

Per site:
- Prefer **Tier 3** (`ctx.Set<T>()…ToListAsync`) IF a `SurrealContext` is already
  constructed/injected at the store (check the store ctor). Otherwise **Tier 2**
  (`SurrealQuery<T>.From()…`) dispatched through the existing `_executor`
  — smaller diff, no new dependency, keeps the executor path identical.
- Keep the same materialization semantics (list vs single). If the raw site used
  `QuerySingleAsync`, use `FirstOrDefaultAsync`/`SingleOrDefaultAsync`
  accordingly.
- Verify the POCO's `[JsonPropertyName]` / column mapping matches the raw
  column name (`avatar_nft_id`) so the translated predicate targets the right
  column. This is the one real correctness risk — the translator resolves the
  column from the property attribute, not the raw string.

**Translator sub-tasks (only if Phase 0 found a gap):** add the operator to
`ExpressionTranslator`, parameterized, with a unit test mirroring the existing
translator test style. Expected: none needed (all sites are bare `From` or `==`).

## Phase 2 — Document the tier policy

**Goal:** stop future stores reaching for raw SELECTs by habit.

1. Add/extend `Providers/Stores/Surreal/AGENTS.md` with a `§query-surface`
   section (terse, per the user's directory-doc convention):
   - Tier 3 (`ctx.Set<T>()`) = default for reads.
   - Tier 2 (`SurrealQuery<T>.From()`) = reads where no context is threaded.
   - Raw `SurrealQuery.Of(...)` = writes / DDL / atomic-conditional ONLY, and
     must carry a one-line reason.
2. Leave a one-line pointer at each store's remaining raw write site
   (e.g. `// raw: conditional atomic UPDATE — see AGENTS.md §query-surface`) so
   the escape-hatch uses read as intentional, not un-migrated.

## Phase 3 — Verify once (end of track, per user policy)

Single integrated sweep — NOT per-fix:
1. `dotnet build` — zero new warnings vs the 28-warning baseline
   ([[build-warning-baseline-2026-06-16]]).
2. `dotnet test` — green, with attention to quest / nft / saga /
   `api-safety-hardening` suites (the ones touching the migrated stores).
3. SRDB0001 analyzer — clean (no new concatenated SurrealQL).
4. Confirm diff scope: read-path store files + translator tests (if any) +
   one AGENTS.md. **No write-path or saga-store file appears in the diff.**
5. Separate review pass (not self-approval): route to `code-reviewer` /
   `verifier` per the OMC execution protocol — the authoring lane does not
   approve its own migration.

## Risk register

| Risk | Likelihood | Mitigation |
|---|---|---|
| POCO column name ≠ raw string column | low | Phase 1 per-site `[JsonPropertyName]` check; integration test asserts same rows returned |
| A site needs an unsupported operator | low (all are `==`/bare) | Phase 0 gate → add translator op + test, or keep raw with pointer |
| Accidental write-path change | low | AC #4 diff-scope check in Phase 3 |
| Tier 3 chosen but no context in scope | med | default to Tier 2 unless ctor already has `SurrealContext`; don't introduce a new dependency just to hit Tier 3 |

## Rollback

Each site is an independent, mechanical read-path swap dispatched through the
same `_executor`. Revert any single site by restoring its `SurrealQuery.Of(...)`
body — no schema, migration, or write-path coupling. The track is fully
resumable and can ship partially (e.g. the two template reads only) if needed.
