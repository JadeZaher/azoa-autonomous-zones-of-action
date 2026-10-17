---
type: review
---

# Independent Round 1 fiat recovery review

Reviewer: stripe_explore. 2026-10-15. Read-only production review of recovery code
by stub_inventory; excluded approval of reviewer's earlier bank lifecycle base.
No tests/builds executed. Active author fixes may follow this snapshot.

## Findings sent to author

1. P2: `Services/Fiat/FiatEventRecovery.cs` selects the first 50 RefundPending rows
   ordered by UpdatedAt. `OnRampRecovery.ReconcileAsync` can return indefinitely
   before updating that field (23-hour unknown outcome, rejected payment evidence,
   provider unavailable). Fifty such rows permanently starve later refunds.
   Persist next-attempt/last-attempt state or use fair bounded pagination; test
   more than fifty rows with permanent failures and a later recoverable refund.
2. Pruning: `StripeWebhookInbox.AdmitAsync` catches every SurrealStatementException
   and returns an existing row. Restrict to duplicate-key conflicts so schema or
   other database errors remain visible rather than being treated as replay.

## Positive source evidence

- Refund owns the exact allocation ledger key; only structured pre-effect failure
  can atomically transfer ownership. Generic Failed and in-progress allocation
  evidence does not authorize a refund. Current package deterministic id is the
  original-key hash; colon encoding applies stored key values, so typed recovery
  addresses the same record as the package ledger.
- Refund reconciliation re-fetches original checkout and validates mode, amount,
  currency, subject-intent correlation and payment intent. Refund metadata and
  amount are validated before settlement. Lost responses retain the stable key;
  blind replay stops within the provider's retention horizon. Terminal failed
  retry requires known authoritative failure and exact attempt counter.
- Failed bank payout recovery fetches bank state within its connected-account
  scope and independently validates transfer destination, amount, mode and intent.
  Bank failure alone never restores units: restitution is capped by cumulative
  authoritative amount-reversed evidence and subtracts already restored units.
- Restitution reservation is durable before unit movement and has a stable key
  covering environment/network/subject/transfer/payout/attempt/cumulative target.
  Final bank reread rejects changed paid/pending state; current row reservation
  is rechecked before the transfer. Pending chain references are reconciled under
  the same movement key instead of issuing a fresh move.
- Durable replay rejects cross-mode inbox rows and Applied is monotonic in outcome
  persistence. Handler exceptions reach the worker boundary and other events run.

## Verification still required

Author fixtures cover partial/full cumulative restitution, paid/pending refusal,
mode mismatch, final-bank-status change, on-ramp no-effect proof transfer, unknown
refund windows, and failed-refund attempt fencing. Real generated-schema coverage
for the no-effect ledger was authored. All remain unexecuted until the integrated
sweep. Add the fairness regression above; no execution approval is implied here.
