---
type: spec
track: ops-postlaunch-hardening
created: 2026-07-06
status: pending
horizon: post-launch
depends_on: [final-hardening-cutover]
---

# ops-postlaunch-hardening — multi-instance & operational maturity

## Why

Single-instance alpha posture is documented as sufficient pre-launch; these are
the consolidated items that matter once the node runs for real or scales out.
Sources: `docs/RESIDUAL-RISK-RUNBOOK.md`, `docs/GO-TO-PROD.md`,
`_archive/final-hardening-cutover/CLOSEOUT.md`.

## Scope

1. **Distributed rate limiting** — in-memory per-instance limiter today; Redis
   fixed-window counters for multi-instance, plus auth-endpoint brute-force
   limits.
2. **Reconciliation tri-state** — providers cannot distinguish "dropped" from
   "pending"; add `GetTransactionConfirmationAsync(txHash)` returning
   `Confirmed | Dropped | Pending` so reconciliation can settle instead of park.
3. **Orphaned `InProgress` idempotency settlement** — reconciliation self-heals
   the common case; add the operator settlement path for unresolvable keys.
4. **`ProviderHealthMonitorHealthCheck`** — vestigial (always Healthy); rewire to
   a real score source or drop it.
5. **Low-balance alerting hook** — operator signal before hot wallets run dry.
6. **Admin console gaps** — saga-operator/dead-letter view and key-rotation view
   (both API-reachable today, no UI).
7. **God-object splits** — `Managers/QuestManager.cs` (~98 KB) and
   `Services/CrossChainBridgeService.cs` (~59 KB) split along existing seams.
8. **Key-rotation pending-key volume** — operator doc + compose/railway wiring so
   the pending-key marker survives ephemeral containers (`NODE-HOST §8.2`).
9. **D1-L2 forward-compat note** — if quests become shareable/public, value-node
   actor derivation must switch `quest.AvatarId` → `run.AvatarId`.

## Acceptance

- Each item lands with its own test evidence; no item blocks alpha; track can
  ship incrementally (it is a backlog track, phases optional).
