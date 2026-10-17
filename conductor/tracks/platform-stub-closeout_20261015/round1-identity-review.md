---
type: review-evidence
---

# Independent KYC/KYB/shared-session review

2026-10-15. Read-only source review; no tests/builds. Reviewer excludes approval of
their own AML/Stripe code. Authors: identomat_explore (KYC/shared sessions),
stub_inventory (KYB). Findings sent to root and owning author.

## Findings

1. **P1 — callback before KYC submission persistence can consume the outcome.**
   `Managers/KycManager.cs` calls provider BeginSessionAsync before creating the
   KycSubmission (approximately lines 206–251). The hosted session is therefore
   callback-addressable before its domain attempt exists. In
   `Services/Kyc/KycHostedSessionOutcomeHandler.cs:75-91`, no active attempt or a
   mismatching attempt returns Success(null). `HostedSessionFlow.ProcessSessionAsync`
   treats this success as terminal Completed/Failed. A callback in that window,
   including after operator recovery of a lost create, can leave a later PENDING
   submission attached to an already-terminal hosted session. Refresh then short-
   circuits instead of applying the outcome. Distinguish not-yet-attached from
   obsolete attempts and retry the former, or persist durable correlation before
   making the provider session callback-addressable. Require a deterministic
   callback-before-submission regression.

2. **P2 — recovery cannot attach ordinary pending KYB sessions.**
   `Controllers/HostedSessionReconciliationController.cs` only accepts a provider
   result that is terminal or exactly KYC ManualCheckOutcome. A valid pending KYB
   result is IN_REVIEW, so the recovery route refuses it. Lost-create recovery needs
   authoritative existence and parent/attempt/subject correlation, distinct from
   an approval verdict. Do not address this by trusting arbitrary pending JSON;
   define and verify the provider's existence/correlation evidence.

3. **P2 — recovered callback alias needs authoritative ownership validation.**
   The same controller accepts operator-supplied ProviderCorrelationId without
   confirming it belongs to the fetched parent session. Store lookup uses
   ProviderSessionId OR ProviderCorrelationId, but the unique index protects only
   ProviderSessionId. A mistaken alias equal to another session's ID/alias creates
   ambiguous routing. Authoritative association and collision rejection are needed
   before attach. Operator access limits the actor; it does not make accidental
   miscorrelation a valid business outcome.

## Positive static evidence (not executed verification)

- Hosted attach/reconcile compare state/version; absence reconciliation changes the
  admission key and retains actor, reason and timestamp. Concurrent attachment and
  reconciliation cannot both win that row update.
- Resumption uses a token, expiry, renewal and token-fenced finish. Domain write
  transactions use a DB-time lease guard that write-locks the hosted row.
- KYC identity binding also fences the exact active submission and tenant/provider
  provenance; KYC terminal update includes observed state/modification/expiry.
- Monitoring failure returns a retryable failure before KYC completion.
- KYB owns a tenant attempt before provider Begin, avoiding the identified KYC
  ordering gap. Its decision checks admission, provider authority, tenant authority,
  attempt version, pending state and lease fence.
- KYB optional evidence/old-attempt fields now use reassigned Unset builders.
  Shared hosted-session optional assignments similarly reassign immutable builders.

The local SurrealForge source confirms TypedUpdateOnlyBuilder.Set/Unset are
immutable. That check also exposed unreassigned mutations in the reviewer's earlier
AML screening-store batch; root was notified separately. It is not self-approved.

Outcome: **not ready for approval** until the actionable findings are resolved and
the final integrated schema/build/test sweep supplies runtime evidence.
