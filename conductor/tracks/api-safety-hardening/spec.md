# API Safety Hardening — Specification

## Goal
Make the API safe to move real cross-chain value **before launch**. This is
Tier 0: engine-independent correctness that blocks any real value flow,
independent of the storage engine. Derived from a 2026-05-16 code trace.

## Architectural principle (source of truth)
The **blockchain is the source of truth for value**. The AZOA store is an
orchestration + custody + metadata + audit layer — `Wallet` has **no balance
field** (`Models/Wallet.cs:6-19`); balance is derived live from chain RPC every
read (`WalletManager.cs:186-194`). Therefore there is **no stored-balance
lost-update risk**. The real risk is **duplicate/irreversible chain actions and
stale-state decisions**. Every fix below targets that surface, not DB balance
races.

## Confirmed defects (file:line evidence)

### Bridge — unsafe to move value through (`Services/CrossChainBridgeService.cs`)
1. `BridgeStatus.Minted` is dead code; flow is `VAAReady→Redeeming→Completed`
   (status writes only at `:135`, `:161`; enum at
   `Models/Responses/BridgeTransactionResult.cs:84`).
2. **Concurrent double-mint (TOCTOU):** redeem guard reads status at
   `:129-130`; on-chain mint at `:147`; first persistence at `:154/:163`.
   Scoped service ⇒ concurrent `POST /{id}/redeem` get separate DbContexts,
   both see `VAAReady`, both mint.
3. **No dedupe key:** `AZOADbContext.cs:204-210` — all three bridge indexes
   non-unique; nothing on `LockTxHash` or Wormhole
   `(emitterChain,emitterAddress,sequence)`. (`QuestEdge` uses `.IsUnique()` at
   `:241` — omission is conspicuous.)
4. **No VAA replay protection:** `VaaBytes`/`Digest` stored, never uniqueness-
   enforced; `VerifyVAAAsync` (`WormholeAdapter.cs:136-156`) checks only sig
   count/version and signature verification is stubbed.
5. **No atomicity:** initiate locks on-chain (`WormholeAdapter.cs:57`) before
   inserting the tracking row (`:303-304`) — save failure strands funds.
   Trusted flow never checks `mintResult.IsError` (`:335,351`).
   `ReverseBridgeAsync` (`:189-205`) does no on-chain reversal.

### Irreversible chain ops — no real idempotency
- `BlockchainOperationManager.ExecuteAsync` creates a fresh-GUID op per call,
  no "already done" check; `BaseBlockchainProvider.ExecuteWithRetryAsync`
  retries broadcasts with no dedupe.
- **`OperationIdGenerator` is a counterfeit idempotency key** — doc-comment
  claims "deterministic" but appends `DateTime.UtcNow.Ticks`
  (`OperationIdGenerator.cs:19,32`) ⇒ identical requests yield different IDs.
- `AlgorandFaucet.DispenseAsync` (`Core/AlgorandFaucet.cs:33-79`) is the one
  server-side broadcaster — a retried `POST /topup` double-dispenses real
  chain value. Transfer/swap avoid this only because they are client-sign
  stubs (incidental, not a safeguard).
- **No reconciliation:** op/bridge status is a local lifecycle flag, never
  re-derived from chain confirmations. A crash mid-flight leaves an op neither
  safely retriable nor known-complete.

### Other Tier 0 gaps
- Validation: 9 validators vs ~31 request models; every swap/wallet/quest/
  bridge model unvalidated.
- No rate limiting / API-key metering anywhere (no `AddRateLimiter`).
- `InMemoryStorageProvider` registered in production DI
  (`Program.cs:164-169`) with asymmetric broken NFT stubs
  (`InMemoryStorageProvider.cs:282-295`): saves fake-succeed, loads error,
  lists empty-succeed ⇒ silent NFT data loss if selected.

## Required behavior (acceptance)
- Same logical irreversible op (bridge redeem, faucet dispense, future
  server-side submit) executes its chain effect **exactly once** under
  duplicate and concurrent requests.
- Bridge state transitions are atomic single-field conditional writes
  (`… WHERE status=expected`, assert exactly one row changed); `Redeeming`
  persisted before any on-chain call; partial-failure compensation; dead
  `Minted` removed or correctly wired.
- A reconciliation path re-derives true op/bridge status from chain
  confirmations.
- Every financial/quest/bridge request model has a FluentValidation validator.
- Rate limiting + per-API-key metering enforced.
- `InMemoryStorageProvider` is test-only, not in production composition.

## Residual risk & ops runbook (first-class artifact)
Implementation + multi-agent code review complete (1 CRITICAL + 4 HIGH + 4
MEDIUM found, all fixed, closure re-review **APPROVE**; prod build 0 errors;
537/537 unit tests green incl. concurrent-double-redeem, replayed-VAA,
idempotency-primitive, faucet single-submit, reconciliation).

**[RESIDUAL-RISK-RUNBOOK.md](RESIDUAL-RISK-RUNBOOK.md)** is the authoritative
operational companion to this spec — pre-launch gating checklist (§4: e.g.
`IVaaSignatureVerifier` must be implemented+registered before real Wormhole
value flow — currently fail-closed by design), stuck/failed-bridge ops
procedures, and the post-review closure addendum. It is part of this track's
deliverable, not an orphan note: consult it before any value flow and before
the `surrealdb-migration` cutover.

## Follow-ups (owned elsewhere, tracked)
- **Async retries / revocations at scale** → `durable-saga-orchestration`
  (reusable durable-saga + transactional-outbox module; bridge is consumer #1;
  the idempotency/reconciliation primitives here carry forward unchanged).
- **Integration-test harness rebuild** → `surrealdb-migration` (the harness was
  EF-InMemory-per-factory; rebuilt once against SurrealDB, not patched for
  Postgres). Unit suite is the authoritative gate until then.

## Dependencies
None. Highest priority; precedes all other tracks. Idempotency + reconciliation
work (G2/G7) carries forward unchanged into `surrealdb-migration`; the same
primitives underpin `durable-saga-orchestration`.
