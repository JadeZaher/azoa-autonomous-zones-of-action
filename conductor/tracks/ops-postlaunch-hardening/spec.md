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

**Reconciled 2026-09-07 @ `b2576f4`:** items 6 and 9 were verified already satisfied
and are struck through with their evidence in place. Item 10 was added — it is the
deferral `action-invitations-approval` forwarded here on shipping. Items 1-5, 7 and 8
were re-read and remain open as written.

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
6. ~~**Admin console gaps** — saga-operator/dead-letter view and key-rotation
   view.~~ **SATISFIED (verified 2026-09-07 @ `b2576f4`).** Both pages exist:
   `frontend/src/app/(dashboard)/admin/sagas/page.tsx` and
   `frontend/src/app/(dashboard)/admin/key-rotation/page.tsx` (an `/admin/roles`
   operator page landed alongside them). This item was inherited from the
   `frontend-demo-harness` §G5 audit, which recorded the gap before the pages were
   built; nothing closed it here.
7. **God-object splits** — `Managers/ActionManager.cs` (~98 KB) and
   `Services/CrossChainBridgeService.cs` (~59 KB) split along existing seams.
8. **Key-rotation pending-key volume** — operator doc + compose/railway wiring so
   the pending-key marker survives ephemeral containers (`NODE-HOST §8.2`).
9. ~~**D1-L2 forward-compat note** — if actions become shareable/public, value-node
   actor derivation must switch `action.UserId` → `run.UserId`.~~ **SATISFIED
   (verified 2026-09-07 @ `b2576f4`).** The switch already happened, under the
   C1 authorization-inversion fix rather than under this item. Every context is
   constructed from `run.UserId`, at all three dispatch sites —
   `Managers/ActionManager.cs:795` and `:917`, and
   `Services/Actions/Workflow/ActionNodeStepHandler.cs:211` — and
   `Models/Actions/ActionNodeExecutionContext.cs:69` documents the invariant:
   "ALL identity side-effects ... run under this user, NOT the action owner."
   Handlers consume it as `context.ActingUserId` throughout `Services/Actions/Handlers/`.

10. **`ActionAccessRequest` status-CAS primitive** — forwarded here by
    `action-invitations-approval` when that track shipped; it is the reason those
    three review findings were deferred rather than bolted on. All three want a
    conditional-UPDATE store path mirroring `Core/Surreal/SurrealTransientConflict.cs`:
    (a) approve is not atomic across the action and request stores, so a failed
    second write leaves a user invited with a still-`Pending` request;
    (b) the terminal-state guard is read-check-write, so concurrent Decide+Withdraw
    can both pass; (c) the ≤1-Pending invariant is a manager read-then-create rather
    than a unique index or conditional CREATE on
    `(action_id, requester_user_id, status='Pending')`.

## Acceptance

- Each item lands with its own test evidence; no item blocks alpha; track can
  ship incrementally (it is a backlog track, phases optional).
