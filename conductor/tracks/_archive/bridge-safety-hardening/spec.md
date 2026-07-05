# Track: bridge-safety-hardening

## Overview

The cross-chain bridge ([Services/CrossChainBridgeService.cs](../../../Services/CrossChainBridgeService.cs),
`ICrossChainBridgeService`) is flagged unsafe pre-launch ([[bridge-unsafe-pre-launch]]).
The 2026-06-21 code map found the **idempotency interface is solid** (content-addressed
keys, canonical VAA digest for replay), but **VAA-redemption atomicity is unproven**:
the `SaveVaaFetchResultAsync` state mutation is separate from the idempotency claim,
and the `ConsumedVaas` replay ledger is referenced in comments but its atomicity
isn't demonstrated. A fetch-succeeds-but-save-fails window leaves semi-committed state.

This is the **gate** on real value movement. [[project-asset-fractionalization]]'s
platform→project token bridge step MUST NOT move real value until this ships
(decision 2026-06-21).

## Goals

1. **Atomic VAA redemption** — the consume-VAA claim and the redeem effect commit
   atomically (or are made idempotently recoverable): a crash between fetch, save,
   and redeem must replay to a single correct outcome, never a double-redeem or a
   lost redeem.
2. **Explicit `ConsumedVaas` ledger** — a durable, atomically-checked replay ledger
   keyed by the canonical VAA digest; a redeemed VAA can never be redeemed twice.
3. **Replay/reversal correctness** — `ReverseBridgeAsync` and re-delivered initiate/
   redeem calls with the same idempotency key produce exactly-once effects.
4. **Crash-recovery reconciliation** — a parked/in-progress bridge tx is recoverable
   to a terminal state by reconciliation against chain truth (mirrors the
   AllocationManager "leave InProgress, settle from chain" discipline).

## Scope

- Audit + harden `CrossChainBridgeService` initiate / fetch-VAA / redeem / reverse /
  complete paths for atomicity and exactly-once semantics.
- Implement/verify the `ConsumedVaas` ledger with an atomic claim.
- Tests: concurrent double-redeem, crash-between-fetch-and-save, replayed initiate,
  reversal-after-partial, VAA-digest collision across producers.
- Feature flag: `BridgeRealValueEnabled` (default false) — the bridge node in
  fractionalization stays testnet/no-value until this track sets it true.

## Acceptance Criteria

- [ ] AC1 — VAA redemption is atomic / idempotently recoverable; no double-redeem
      under concurrent or crash-replay conditions (tests prove it).
- [ ] AC2 — `ConsumedVaas` ledger: durable, atomic claim, canonical-digest keyed.
- [ ] AC3 — Initiate/redeem/reverse are exactly-once under same-idempotency-key replay.
- [ ] AC4 — Crash-recovery reconciliation drives in-progress tx to terminal state.
- [ ] AC5 — `BridgeRealValueEnabled` flag gates real-value movement; off by default.
- [ ] AC6 — Security review pass (the bridge handles value + external VAAs).
- [ ] AC7 — `dotnet build` + tests green, zero new warnings vs baseline.

## Relationship

- **Gates** [[project-asset-fractionalization]] (platform→project value bridge).
- Supersedes the warning in [[bridge-unsafe-pre-launch]] once shipped.
