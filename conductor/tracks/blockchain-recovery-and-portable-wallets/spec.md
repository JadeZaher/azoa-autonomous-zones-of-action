# Track: blockchain-recovery-and-portable-wallets

## Overview

Two related hardening pieces needed before ArdaNova drives real value through AZOA
quests:

1. **Reconcile-before-retry for chain-action quest nodes** (the urgent, high-risk
   piece). Today a `Grant`/`Transfer`/`Swap`/`FungibleTokenCreate` node that
   submits a tx and then loses confirmation is **retried blindly** by the saga
   (`RetryPolicy.Default` = 5 attempts). For an on-chain mint that means a
   **double-mint** risk: attempt 1 broadcast and landed, but the confirmation read
   timed out, so attempts 2..5 mint again. The bridge/operation paths already solve
   this with `IReconciliationService` + `ChainVerdict`, but **that logic was never
   wired into quest nodes**. This track extends chain-truth verification to the
   quest engine and adds the missing provider primitive the existing reconciler
   itself documents as a gap.

2. **Portable wallet model (exportable custody)** (the slower, less-urgent piece).
   AZOA custodies Platform wallets by default; users must be able to **take a
   wallet elsewhere** — export the key to self-custody or import into another
   platform. The spine exists (`ExportWalletAsync`, `Wallet.WalletType`,
   `Avatar.OwnerTenantId/ExternalUserId`) but export is unguarded and there is no
   record that a wallet has been externally exposed.

These ship independently; **piece 1 is the prototype in this track** (it is what
makes the engine unsafe for value today). Piece 2 is specified here for continuity
and lands as a follow-up.

Decisions locked with the user (2026-06-21):
- Wallet portability = **exportable custody** (custodial by default, key export as
  escape hatch).
- Recovery driver = **operator-driven + auto-retry** (auto-retry transient, surface
  stuck runs to an operator who chooses redo-from-step / skip / compensate).
- Sync-uncertainty = **reconcile-before-retry** (query chain state before re-submit;
  never blindly re-broadcast).

## Relationship to other tracks

- **Builds on** the `api-safety-hardening` reconciliation work
  ([Services/Reconciliation/ReconciliationService.cs](../../../Services/Reconciliation/ReconciliationService.cs)) —
  reuses `ChainVerdict`/`ClassifyTx` by promoting them out of the service.
- **Builds on** [[durable-workflow-engine]] — the retry/compensation seam lives in
  [QuestNodeStepHandler.cs](../../../Services/Quest/Workflow/QuestNodeStepHandler.cs).
- **Builds on** [[quest-temporal-fork-model]] — `ForkAsync` IS the operator
  "redo-from-step" primitive; this track surfaces it, it does not reinvent it.
- **Unblocks** the ArdaNova custodial-orchestrator integration (Grant-bounty flow
  becomes safe for real value).

## Part 1 — Reconcile-before-retry (the prototype)

### The problem precisely

`QuestNodeStepHandler.ExecuteAsync` (step 3–4): a chain-action handler runs, and on
`result.IsError` the node is recorded `Failed` and the step returns `StepResult.Fail`,
which the saga retries up to `RetryPolicy.MaxAttempts` (5). **Nothing between
attempts asks the chain whether the previous attempt's tx actually landed.** The
handlers (`GrantNodeHandler` etc.) call `INftManager.MintAsync` which broadcasts;
a broadcast-then-confirmation-timeout surfaces as `IsError`, indistinguishable from
"never broadcast." Re-running mints again.

The existing reconciler's own comment names the missing primitive
([ReconciliationService.cs:676](../../../Services/Reconciliation/ReconciliationService.cs#L676)):
> there is no provider capability that cleanly distinguishes "dropped/failed" from
> "not yet observed" … A dedicated provider method (e.g.
> `GetTransactionConfirmationAsync` returning an explicit Confirmed/Dropped/Pending
> tri-state) would let reconciliation also auto-fail genuinely-dropped txs.

### Design

#### 1.1 New shared chain-truth type (`Models/Blockchain/ChainConfirmation.cs`)

Promote the private `ChainVerdict` tri-state to a shared, public type so both the
reconciler and the quest engine consume one source of truth. Add `Pending` (seen in
mempool / not-yet-confirmed) as distinct from `Unknown` (not found at all / RPC
error):

```csharp
public enum ChainConfirmation
{
    Confirmed,    // tx is on-chain and succeeded
    FailedOnChain,// tx is on-chain and reverted/failed
    Pending,      // tx is observable but not yet confirmed (still safe to wait)
    Unknown       // not found / RPC error — AMBIGUOUS, never auto-act
}
```

#### 1.2 New provider primitive (`IBlockchainProvider`)

Add ONE method; default-implement it on the base provider in terms of the existing
`GetTransactionStatusAsync` so non-overriding providers keep working:

```csharp
/// <summary>
/// Explicit confirmation tri-state for a previously-broadcast tx. Unlike
/// GetTransactionStatusAsync (provider-inconsistent dictionary), this returns a
/// normalized verdict so callers can safely decide advance-vs-retry-vs-wait
/// WITHOUT re-broadcasting. The default base implementation derives it from
/// GetTransactionStatusAsync; providers that can distinguish "dropped" from
/// "not yet seen" (e.g. via mempool lookup) SHOULD override to return Pending
/// vs Unknown precisely.
/// </summary>
Task<AZOAResult<ChainConfirmation>> GetTransactionConfirmationAsync(
    string txHash, CancellationToken ct = default);
```

- **Base default** (in `BaseBlockchainProvider`): call `GetTransactionStatusAsync`,
  run the promoted `ClassifyTx` logic, map its `Unknown` to `ChainConfirmation.Unknown`.
  This keeps the conservative "IsError ⇒ Unknown, never fail" invariant.
- **Algorand override** (follow-up, not v1): use pending-transaction-information to
  return `Pending` when the tx is in the pool, sharpening Unknown→Pending.

#### 1.3 Handlers record their tx hash (so it is probeable)

Reconcile-before-retry needs a tx hash to probe. Today `GrantNodeHandler` serializes
the whole `AZOAResult` into node `Output` but does not surface the tx hash in a
stable place. Add: chain-action handlers stamp the broadcast tx hash into the
`QuestNodeExecution` via a new nullable field `TxHash` on the execution row, set by
the handler result. Concretely, extend `QuestNodeHandlerResult` with an optional
`TxHash`/`ChainType`, and have `QuestNodeStepHandler` persist them onto the execution
row alongside `Output`.

```csharp
// QuestNodeHandlerResult gains:
public string? TxHash { get; init; }
public string? ChainType { get; init; }
// QuestNodeExecution gains nullable TxHash, ChainType (SurrealDB additive columns).
```

Handlers read the tx hash from the mint/transfer result's `Parameters` bag (the
`ReadAssetId`/`ReadChainId` pattern already in `GrantNodeHandler` — add a `ReadTxHash`).

#### 1.4 The reconcile-before-retry hook (`QuestNodeStepHandler`)

The seam is the **forward-failure path** (step 4, `if (result.IsError)`). Before
returning `StepResult.Fail` (which triggers a retry that may re-broadcast), if the
node `RequiresChainCapability` AND a tx hash was recorded, probe the chain:

```
on chain-action node failure with a recorded TxHash:
  verdict = provider.GetTransactionConfirmationAsync(txHash)
  switch verdict:
    Confirmed     -> the effect ALREADY LANDED. Do NOT retry. Record node
                     Succeeded (reconciled), re-derive output, self-advance.
    FailedOnChain -> genuinely failed on-chain. Safe to retry (re-broadcast) OR
                     compensate per retry budget — return StepResult.Fail.
    Pending       -> in mempool, may still confirm. Park the run in
                     AwaitingReconciliation (new non-terminal state); the
                     reconciliation sweep re-checks and resumes/fails it.
    Unknown       -> ambiguous. Park in AwaitingReconciliation (NEVER blind
                     retry). Operator/sweep resolves.
```

This converts "blind retry" into "verify, then act," eliminating the double-mint.

#### 1.5 New run lifecycle state (`QuestRunStatus.AwaitingReconciliation`)

A non-terminal park state for "submitted but confirmation unknown." Add to the enum;
it is NOT terminal (`IsTerminal()` unchanged). Surfaced to operators alongside
`Failed`/`Suspended` via the existing `GetByStatusAsync` query. From this state the
operator action is **redo-from-step = `ForkAsync(runId, failedNodeId, reason)`** once
truth is known, or the reconciliation sweep auto-resolves it.

#### 1.6 Quest-node reconciliation sweep

Extend `IReconciliationService` (or a sibling `IQuestReconciliationService`) with
`ReconcileQuestNodesAsync` mirroring `ReconcileOperationsAsync`: scan
`AwaitingReconciliation` runs older than the staleness threshold, probe their stuck
node's recorded `TxHash`, and advance (Confirmed) / fail (FailedOnChain) / leave +
flag hard-stuck (Pending/Unknown). Reuse all the conservative invariants verbatim —
**observe only, never re-broadcast.**

#### 1.7 Operator surface

- `GET /api/quest/runs?status=AwaitingReconciliation` (and `Failed`) — list stuck
  runs (the store method `GetByStatusAsync` exists; add the controller route).
- `POST /api/quest/runs/{runId}/fork` already exists → document as redo-from-step.
- `POST /api/quest/runs/{runId}/reconcile` — manual targeted re-probe (mirrors
  `ReconcileBridgeTransactionAsync`).

### Part 1 invalid-mode handling (the "invalid modes" the user asked for)

Distinguish two failure classes so retry policy differs:
- **Transient/chain failure** (RPC down, tx reverted) — eligible for auto-retry /
  reconcile (above).
- **Invalid-config failure** (malformed node config, missing required field, decimals
  out of range, unknown asset) — NOT retriable; re-running can never succeed. The
  handler returns a distinguished `QuestNodeResults.Invalid(msg)` that the step
  handler maps to an immediate terminal `Failed` **without** consuming retry budget
  and **without** chain probing (nothing was broadcast). This prevents wasting 5
  attempts on a config that is dead on arrival.

```csharp
// QuestNodeHandlerResult gains a Retriable flag (default true). Invalid() sets it false.
```

## Part 2 — Portable wallet model (exportable custody, follow-up)

### Design

#### 2.1 Account ⇄ wallet linkage (ArdaNova-side, documented here for the seam)

ArdaNova stores `(ardanovaUserId → azoaAvatarId → walletId)`. On the AZOA side the
linkage already exists via `Wallet.AvatarId` + `Avatar.ExternalUserId`. No AZOA
schema change needed for linking; the change is on the export/portability side.

#### 2.2 Safe export flow (`WalletManager.ExportWalletAsync` hardening)

Current export returns the key with only ownership scoping. Harden:
- **Fresh re-auth required**: export requires a recent step-up auth claim (a
  short-TTL `wallet:export` scope minted by a re-authentication endpoint), not just
  a valid session.
- **Rate-limited** to the `financial` policy.
- **Audit-logged**: every export writes an audit row (avatar, wallet, timestamp,
  api key / ip).
- **Mark exposed**: set a new `Wallet.ExportedAt` timestamp. Once set, the wallet is
  flagged "externally exposed" — UI warns, and automated platform signing MAY be
  configured to refuse it (tenant policy) since the key is no longer exclusively
  custodied.

#### 2.3 Import / bring-your-own (links to External wallet path)

The existing `ConnectWalletAsync` (External wallet, address + pubkey only, no key)
already covers "bring an Algorand wallet you control elsewhere." Document this as the
non-custodial portability path; no new code beyond strengthening the signature
verification noted as lightweight today.

## Acceptance Criteria

### Part 1 (prototype — this track's deliverable)
- [ ] AC1 — `ChainConfirmation` enum (`Confirmed/FailedOnChain/Pending/Unknown`) in
      `Models/Blockchain/`; `ChainVerdict`/`ClassifyTx` promoted from
      ReconciliationService to a shared classifier (reconciler refactored to consume it,
      behavior unchanged — existing reconciliation tests still green).
- [ ] AC2 — `IBlockchainProvider.GetTransactionConfirmationAsync` + conservative base
      default in `BaseBlockchainProvider` (IsError ⇒ Unknown, never FailedOnChain).
- [ ] AC3 — `QuestNodeHandlerResult` gains `TxHash`, `ChainType`, `Retriable`;
      `QuestNodeExecution` gains nullable `TxHash`/`ChainType` (additive SurrealDB columns).
- [ ] AC4 — Chain-action handlers stamp the broadcast tx hash onto the result
      (`GrantNodeHandler` first; Transfer/Swap/FungibleTokenCreate follow the precedent).
- [ ] AC5 — `QuestNodeStepHandler` reconcile-before-retry hook: on chain-action node
      failure with a recorded tx hash, probe and branch advance/retry/park per §1.4.
- [ ] AC6 — `QuestRunStatus.AwaitingReconciliation` (non-terminal); `IsTerminal()`
      unchanged; park projection wired.
- [ ] AC7 — Invalid-config failures use `QuestNodeResults.Invalid` (Retriable=false) →
      immediate terminal Failed, no retry, no chain probe.
- [ ] AC8 — `ReconcileQuestNodesAsync` sweep + manual `POST /runs/{id}/reconcile`.
- [ ] AC9 — `GET /api/quest/runs?status=...` operator list route.
- [ ] AC10 — Tests: (a) Confirmed-on-retry does NOT re-broadcast (double-mint guard);
      (b) FailedOnChain retries; (c) Unknown parks AwaitingReconciliation; (d) invalid
      config fails immediately without consuming retries; (e) sweep advances a
      since-confirmed parked run. `dotnet build` 0 errors, zero new warnings vs baseline.

### Part 2 (follow-up)
- [ ] AC11 — Export requires step-up `wallet:export` scope; rate-limited; audited;
      sets `Wallet.ExportedAt`.
- [ ] AC12 — `ExportedAt`-flagged wallets surfaced; optional tenant policy to refuse
      platform signing on exposed wallets.

## Out of scope / follow-ups
- Algorand mempool-precise `Pending` override (v1 base default maps to Unknown).
- Solana/Ethereum confirmation overrides (Algorand-first).
- Automatic compensation choreography changes — compensation stays as-is; this track
  only prevents the unsafe blind-retry before compensation is ever reached.
- ArdaNova-side `IBlockchainProvider`/`IPaymentProvider` seam (lives in the ArdaNova repo).
```