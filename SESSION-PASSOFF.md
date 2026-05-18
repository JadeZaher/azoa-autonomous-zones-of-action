# SESSION PASS-OFF — for a fresh Claude Code session

You are picking up the OASIS API mid-stream with **no prior conversation
context**. This file is self-contained: read it fully, then execute.

> **Mission:** (A) land the api-safety-hardening §4 cleanup, then (B) execute
> the `architecture-decoupling` track (Tier 1 — the SurrealDB precondition).
> Keep the safety suite green as a regression gate throughout.

---

## 0. Orientation (read first)

- Repo: `c:\Users\atooz\Programming\Projects\oasis-sleek` — .NET 8 WebAPI
  (`OASIS.WebAPI.csproj`), Postgres/EF now, SurrealDB later.
- Git: branch **`api-safety-hardening`** (off `main`), 3 commits, **not pushed**:
  `5f85c18` safety spine · `3b6d27a` saga skeleton+docs · `fc66616` VAA verifier.
  Keep working on this branch (or branch from it); do not push without being asked.
- Method: this codebase uses **Conductor tracks** — `conductor/tracks/<track>/{spec.md,plan.md}`,
  index in `conductor/tracks.md`. Work the plan task list; keep docs truthful.
- Read these before touching code: `AGENTS.md` (build/test/stack ops),
  `GO-TO-PROD.md` (launch gates + the §4 cleanup list),
  `conductor/tracks/api-safety-hardening/RESIDUAL-RISK-RUNBOOK.md`,
  `conductor/tracks/architecture-decoupling/{spec.md,plan.md}`,
  `conductor/tracks/durable-saga-orchestration/spec.md`.

## 1. Standing constraints / preferences (do not relearn the hard way)

- **Greenfield, not live — no customers, no data.** Prefer clean re-architecture;
  **no backward-compat/migration/dual-write shims**. The DB can be dropped &
  re-migrated freely.
- **Homebake / minimize external deps.** Bouncy Castle (secp256k1) was the *one*
  approved exception, for crypto only. Don't add NuGet without strong cause.
- **No overengineering. Self-documenting code over narrative comments; extract
  shared helpers over duplication.** A prior review flagged exactly this — honor it.
- **Config-driven**, not hardcoded; tests load real `appsettings.json`.
- **Don't run the frontend typecheck** (known pre-existing noise). Gates are
  `dotnet build` + the unit suite.
- Delegation that worked well here: **phased waves of parallel workers with
  disjoint file ownership**, a foundation/contract pass first when many workers
  share a spine, then a sequential integration pass for shared files
  (`Program.cs`, `OASISDbContext.cs`), then a verification pass. Don't run 5
  concurrent `dotnet build`s (bin/obj lock) — workers edit, you build once.
- Commit on the feature branch; end commit messages with
  `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`.
  (PowerShell here-strings mangle `git commit -m` — write the message to a temp
  file and `git commit -F`.)

## 2. Build / test / verify

```powershell
dotnet build OASIS.WebAPI.csproj -c Debug        # MUST be 0 errors; 17 warnings = known baseline, don't chase
dotnet test tests/OASIS.WebAPI.Tests/OASIS.WebAPI.Tests.csproj -c Debug   # currently 564/564, 0 failed
pwsh scripts/passoff.ps1                          # the code sign-off gate: build + suite + safety asserts; exit 0 = green
```

- `tests/run-tests.ps1` auto-spins the persistent **podman** `oasis-postgres`
  container (localhost:5441, db/user/pass `oasis`). Container runtime here is
  **podman** (no docker). Data persists between runs by design.
- **Integration tests (`OASIS.WebAPI.IntegrationTests`) are a known deferred
  follow-up owned by `surrealdb-migration`** — they were built for ephemeral
  EF-InMemory. The **unit suite (564/564) + `scripts/passoff.ps1` are the
  authoritative gate.** Do not rabbit-hole on the integration harness.
- Stale `dotnet`/`OASIS.WebAPI` hosts can lock `bin\Debug\net8.0\OASIS.WebAPI.dll`
  before `dotnet ef` — stop them first.

## 3. What is DONE (don't redo)

`api-safety-hardening` (Tier 0) is implemented + multi-agent-reviewed
**APPROVE-WITH-SIMPLIFICATIONS**: exactly-once bridge (idempotency claim +
`ConsumedVaas` ledger + conditional `ExecuteUpdateAsync` transitions +
reconciliation), `Secp256k1VaaSignatureVerifier` (Bouncy Castle, fail-closed,
config-driven Guardian sets — devnet shipped+verified; mainnet/testnet are an
ops gate, see `GUARDIAN-SET-SETUP.md`), 33 validators, rate limiting, InMemory
out of prod DI. `durable-saga-orchestration` has a **Phase-1 skeleton only**
(generic module, **no bridge consumer yet**, currently registered+running with
zero consumers — see Mission A item 3). The safety design is sound; the cleanup
below is *localized*, not a rewrite.

## 4. MISSION A — api-safety-hardening §4 cleanup (do FIRST; launch-blocking)

Authoritative list: `GO-TO-PROD.md` §4. Keep `scripts/passoff.ps1` green after each.

1. **Remove the vestigial `xmin`/`Version` concurrency token** — `BridgeTransactionResult`,
   `SagaStepRecord`, the two `OASISDbContext` mappings, the `SqliteTestDbContext`
   override. Never exercised (all flows use conditional `ExecuteUpdateAsync`;
   tests strip it). Pure deletion, zero behavior change. Regenerate the EF
   migration accordingly (greenfield — reset DB freely).
2. **`OperationStatus` enum/constants** for `BlockchainOperation.Status`
   (currently stringly-typed across `BlockchainOperationManager` producer and
   `ReconciliationService` consumer → silent-divergence risk). Use on both sides.
3. **`Sagas:Enabled=false` by default** (or pull saga DI out of `Program.cs`
   behind a Phase-2 flag) — a consumerless hosted loop + migration should not run
   in the pre-launch graph. Keep code + ADR + migration on-branch.
4. **Add `BridgeStatus.Reversing`** (or a phase discriminator) and replace the
   `ReconciliationService.IsReversalInFlight` `CompletedAt`-timestamp heuristic
   with explicit provenance. Highest-value correctness fix; add/keep tests.

SHOULD (not blocking, do if cheap): extract `WormholeDigest.Canonical()` shared
helper (kill the `CrossChainBridgeService` → `WormholeAdapter` static
reach-through); replace the idempotency catch+reflection with
`INSERT … ON CONFLICT DO NOTHING RETURNING` or an injected detector; swap the
`IServiceScopeFactory` store-scope dance for `IDbContextFactory<OASISDbContext>`.

Gate before Mission B: build 0 errors, unit suite ≥564 green, `passoff.ps1`
exit 0. Commit Mission A (+ `GO-TO-PROD.md`, currently uncommitted).

## 5. MISSION B — architecture-decoupling track (Tier 1)

Spec/plan: `conductor/tracks/architecture-decoupling/{spec.md,plan.md}` (22
tasks). It "should follow api-safety-hardening" (now satisfied — value paths are
safe) and **blocks `surrealdb-migration`** (this seam is its precondition).
Summary of the work:

- **Persistence seam (the key enabler):** collapse the god `IOASISStorageProvider`
  (41 methods, 11 entities) **and** the redundant `IQuestRepository` into ONE set
  of per-aggregate, graph-aware interfaces — `IAvatarStore/IWalletStore/IHolonStore/IQuestStore/INftStore/IBridgeStore`.
  EF-backed adapters now (swapped for SurrealDB later, behind this one seam).
  **NOT a generic `IRepository<T>`** (anti-pattern over a graph DB). Migrate all
  managers off the god interface + `ProviderContext.CurrentProvider`; delete the
  god interface, `IQuestRepository`, `QuestRepository`, the god surface of
  `EfStorageProvider`. **CRITICAL:** the bridge/idempotency/reconciliation
  exactly-once + conditional-`ExecuteUpdateAsync` semantics MUST be preserved —
  the api-safety unit tests are the regression gate; if `IBridgeStore` hides the
  conditional UPDATE, keep an intention-revealing method that still does the
  atomic `WHERE Status==expected` + assert-one-row.
- **God manager:** `QuestManager` (908 lines, 9 ctor deps) has a ~315-line
  34-case `ExecuteNodeInternalAsync` switch. Extract `IQuestNodeHandler` (one per
  `QuestNodeType`, 34 in `QuestEnums.cs`), DI map, registry lookup; trim ctor
  deps; handler unit tests.
- **Bug:** `ExecutionOrder` computed twice (`QuestDagValidator.cs:97-102` vs
  `QuestManager.cs:187-196`) — single authoritative computation.
- **Hygiene:** `SwapManager` static cache → `IMemoryCache` (bounded + expiry);
  `ProviderContext.Activate()` returns provider (no mutable scoped state);
  implement-or-delete `AutoFailOver`/`AutoReplication`; move
  `db.Database.Migrate()` (`Program.cs`) to a gated job (greenfield — acceptable
  to keep interim, but the seam is the point).
- **Observability (SurrealDB prerequisite):** OpenTelemetry traces + metrics +
  request-correlation IDs in structured logs; `AddHealthChecks` + `/health`
  exposing `IProviderHealthMonitor`. Add the OTel packages (justified — this is
  the one place new deps are expected).

**Acceptance = the architecture-decoupling pass-off** (spec.md §Acceptance):
one per-aggregate interface set; god interface + `IQuestRepository` deleted; all
managers on the new seam; QuestManager dispatch is a handler registry (315-line
switch gone, open/closed); `ExecutionOrder` once; swap cache is `IMemoryCache`
bounded+expiry; OTel traces + `/health` live; request correlation in logs.
Extend `scripts/passoff.ps1` (or add a sibling gate) to assert: build 0
warnings, full unit suite green INCLUDING the api-safety safety tests
(regression), **zero remaining references to the deleted god interface**, and
`/health` responds. Update `conductor/tracks.md` (mark `[x]` when acceptance
met) + the track plan checkboxes.

Suggested phased execution (proven pattern): (1) foundation pass — design +
author the per-aggregate interfaces + `IQuestNodeHandler` contract (the shared
spine), publish the contract; (2) parallel workers with disjoint ownership —
per-aggregate EF adapters, manager migrations, the 34 quest handlers, the
ExecutionOrder/IMemoryCache/ProviderContext fixes, OTel/health — each owns
disjoint files; (3) sequential integration of `Program.cs` DI + delete the dead
god interface; (4) verification pass — build, full suite, passoff, zero-refs
check, architect review. Keep the api-safety safety tests green at every step.

## 6. First actions for this session

1. `git log --oneline -3` + `git status` to confirm branch/state; read this file's
   referenced docs (§0).
2. Run `pwsh scripts/passoff.ps1` — confirm you start from green (564/564, exit 0).
3. Execute **Mission A** (4 items, phased; small). Re-run passoff; commit
   (include the uncommitted `GO-TO-PROD.md`).
4. Execute **Mission B** via the phased plan; keep passoff + safety tests green;
   land observability; satisfy the acceptance gate; update conductor docs/index.
5. Final: architect/code review pass with the same lens (anti-patterns,
   overengineering, simpler alternates); summarize why + next steps; update
   `GO-TO-PROD.md` and the runbook.

## 7. Gotchas

- The persistence-seam refactor is large and underpins the value paths — phase
  it, never break the api-safety exactly-once/replay/reconciliation tests
  (your safety net). If a refactor makes one fail, the refactor is wrong, not
  the test.
- EF migration churn is fine (greenfield: drop & re-migrate). Don't write
  "table exists" compat code.
- Don't introduce a generic repository; per-aggregate intention-revealing
  methods only (it's a graph DB next).
- Saga module: leave it (Phase-1, disabled per Mission A item 3) — its bridge
  adoption + the unimplemented transactional-outbox claim are
  `durable-saga-orchestration` Phase 2, NOT this session.
- `ProviderContext`/provider-selection/health/decorator infra is intertwined
  with the god interface — plan its adaptation/removal explicitly (plan task 6).

---

**Created:** 2026-05-18 · pass-off from the api-safety-hardening session.
Branch `api-safety-hardening` · code gate green (564/564, passoff exit 0) ·
review verdict APPROVE-WITH-SIMPLIFICATIONS.
