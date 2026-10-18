---
type: lane-checkpoint
round: 4
wave: W3
lane: fix-core2
branch: r4/fix-core2
base: 75b23986
---

# Round 4 · fixer `r4/fix-core2` (L6 findings 1, 2, 4)

Scope: `round4-lane-L6-verification.md` stop-the-line findings 1 (CRITICAL DI deadlock), 2 (HIGH typed
`UpdateOnly/DeleteOnly` without `Where`), 4 (MEDIUM unset-required / single-query `Combine`).

## DONE

| Finding | Fix (file:line) | Evidence |
|---|---|---|
| **1 CRITICAL** host deadlock: `ActionManager → DeploymentActionResolver → DeploymentService(IFrozenActionRuntime) → factory over ActionManager` | `Program.cs:1371` `IFrozenActionRuntime` → `Managers/DeferredFrozenActionRuntime.cs` (captures the scope `IServiceProvider`, resolves `ActionManager` per call, so constructing `DeploymentService` no longer constructs `ActionManager`). Edited in place. Rationale `Managers/AGENTS.md` §DeferredFrozenActionRuntime; pointer added to `tests/.../Hosting/AGENTS.md` | `Hosting/ProgramCompositionTests` **5/5 green in 1 s** (was: 90 s timeout) against throwaway `surrealdb:v3.1.4 --user 0:0 memory` on :8060 with the full generated schema applied to `azoa/azoa`. Real `dotnet run` (Development, `SurrealRuntime__Endpoint=…:8060`, test-mode Fiat secrets) printed **"Now listening on http://127.0.0.1:5160" after 4 s**, `GET /health` → 200, reconciliation sweep ran |
| **2 HIGH** `SurrealActionLocalMutationStore.cs:51,59` (holon/wallet delete) | `:99,:105` `Where(user_id == owner of the witnessed read row)` | new `SurrealActionLocalMutationTests.OwnerDeletesWitnessedWalletAndHolonInOneLocalCommand` green |
| 2 (missed by the first ratchet) `…Witnesses.cs` `AppendHolonNulls/AppendWalletNulls` | `:134,:149` `Where(user_id == owner just upserted)` | `SurrealActionNftHolonLinkTests` green (was red) |
| 2 `SurrealActionSigningAuthorizationStore.cs:85,87` | `:86` run `status IN (Running, AwaitingReconciliation)`; `:89` step `status = Parked` (both asserted earlier in the batch) | `SurrealWorkflowSigningAuthorityTests` green |
| 2 `SurrealHolonStore.cs:157` | `:158` `Where(user_id == owner just read)`; 0 affected → refusal "Holon changed before it could be deleted." | `SurrealHolonStoreTests` green |
| 2 `SurrealWalletStore.cs:69` | `:70-75` read then `Where(user_id == owner)`; messages aligned to `Saved.`/`Deleted.` (store tests asserted them) | `SurrealWalletStoreTests` green (3 were red on message drift) |
| 2 `SurrealFungibleCreationStore.cs:50` | `:51` `Where(version == expected.Version)` (the lock statement kept it) | new `SurrealFungibleProjectionTests.RenewedAuthorizationPointsTheOperationAtTheNewAuthorityExactlyOnce` green (bump once, replay reads back, one audit row) |
| 2 `SurrealGovernanceStore.Finalization.cs:52` | `:53` `Where(definition_hash matches AND final_result_json = NONE)` (what `$_finalizing` locked) | `SurrealGovernanceTests` (finalize race/early/replay) green |
| 2 `SurrealNftLifecycleStore.cs:279` | `:310` `Where(version == authority.Version)` (the version the batch just wrote) | `SurrealNftLifecycleStoreTests` (all partials) green after fixing a bare-id test seed (`Ownership(...)` `UserId = "user:" + …`) |
| **4 MEDIUM** "required field cannot be unset" `parent_holon_id` | `SurrealHolonStore.cs:400,405,446,451` `HolonPoco` `[References(..., Optional = true)]` mirrors canonical `Persistence/SurrealDb/Models/Holon` (SurrealForge reads optionality from `[References]` before the column type). Not a schema source; no `.surql` change | `SurrealActionNftHolonLinkTests` green |
| 4 "Combine requires at least two queries" | `SurrealFungibleCreationStore.cs:180` lone progress statement sent directly when no effect-authorization fence is needed (single statement = atomic) | `SurrealFungibleProjectionTests` (SubmittedRecovery*) green |
| Ratchet | `SurrealQlSourceRulesTests` 7 `no-where` `KnownViolations` entries removed; `SurrealQlSourceRules.ConditionalMutationWithoutWhere` now follows builders split across statements to their first `Build()` (2 mutation-pinned cases added) | rules + transaction gate + deployment/local/wallet/holon unit tests 220/220 |

Production re-grep (every `UpdateOnly/DeleteOnly` in tracked non-test `.cs`, split builders included): **0** without `Where`.

Guard rationale table: `Providers/Stores/Surreal/AGENTS.md` §Typed conditional mutations always carry a guard.

## Transaction-scope checklist

Every guard restates state already read/locked/written earlier in the same transaction (or, for the two store
deletes, in the same call), so the allow path is unchanged; statement count and RETURN/`GetValues` indices are
unchanged everywhere except `SurrealFungibleCreationStore.UpdateAsync`'s non-authorizing branch (one statement,
index 0, as before). Concurrent-change outcome: the two store deletes now refuse (0 affected) instead of deleting
a row whose owner changed.

## Test runs (throwaway `surrealdb:v3.1.4` :8060, reaped)

- `ProgramCompositionTests` 5/5; host start bounded, 1 s.
- Store classes `SurrealActionLocalMutationTests|SurrealActionNftHolonLinkTests|SurrealFungibleProjectionTests|SurrealGovernanceTests|SurrealHolonStoreTests|SurrealNftLifecycleStoreTests|SurrealWalletStoreTests|SurrealWorkflowSigningAuthorityTests|SurrealActionAdmissionSafetyTests|FenceAllowPathInventoryTests|StoreFamilyGuardInventoryTests|CapturedNullGuardTests|SurrealNodeGovernance*`:
  first pass 154/159 (5 reds: 3 wallet message drift, 2 NFT bare-id seed) → fixed → re-run of the red classes 78/78 + 2 new tests 2/2.
- How to reproduce locally: the test endpoint const is `127.0.0.1:8020`; I pointed it at :8060 only for the run (reverted,
  not committed) and set `SurrealRuntime__Endpoint`/`SurrealDb__Endpoint` env vars; Development binds `SurrealRuntime`
  (`azoa/azoa`), so the generated schema must be applied there first (`ProgramCompositionTests` otherwise fails on
  "namespace 'azoa' does not exist", not on DI).

## Compile and checks

- `dotnet build azoa.sln -c Debug --no-incremental`: 0 errors, **17 warnings** (≤ 25).
- `node scripts/round4/merge-check.mjs`: ok.

## NOT DONE (out of this fixer's scope; still open from L6)

- L6 finding 3 (`FiatReversalEventHandler.cs:240` bare `LotId`), finding 5 (liability read rule), finding 6 (LOW).
- No real-DB test for a concurrent owner change between `HolonManager`/`WalletManager` ownership check and the store
  delete (behaviour is now a refusal; the stronger fix is passing the caller-verified owner into
  `IHolonStore/IWalletStore.DeleteAsync`, an interface change across ~8 implementers).
- `SurrealTestDefaults.Endpoint` is a hard-coded const; a throwaway-port run needs a local edit (consider an env override).

## Hunks

```hunk path=Program.cs
// Deferred per-call resolution: a factory over ActionManager here deadlocks host start (see Managers/AGENTS.md §DeferredFrozenActionRuntime).
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IFrozenActionRuntime, AZOA.WebAPI.Managers.DeferredFrozenActionRuntime>();
```

No `appsettings*.json` change. POCO deltas: none on schema sources. Published contracts: none.
