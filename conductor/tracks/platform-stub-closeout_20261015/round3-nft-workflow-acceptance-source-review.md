---
type: review
status: source-accepted-bounded
---

# NFT workflow acceptance store review

Independent Lane B review. Runtime: NOT RUN. This covers acceptance persistence,
not yet-unwired resolver, first-send authority or participant clients.

SOURCE_VERDICT: ACCEPTED for this bounded store slice after corrections below.

The store composes original signing credentials/grants, actual accepting consent,
ActionExecutionFence, exact keyed execution version/signing/reference, database
quote expiry, immutable audit and lifecycle creation. Known conflicts reread the
same immutable audit. Canonical receipt integrity and frozen draft comparison
prevent caller-selected execution or request replacement in the inspected path.

Initial finding: QuotedIntentHash was checked only for shape. The workflow Claim
must compare the actual candidate's initial shared intent and recipient wallet to
that digest before creating the lifecycle. Draft/payer matching alone misses fee
intent and recipient account substitutions, recreating the stranded-operation gap
fixed for direct Mint. Add actual candidate mismatch regressions without audit or
lifecycle writes. Independent reread confirmed the author now reconstructs the
shared initial intent for supported self-mint, requires the fee quote, and compares
the full digest and recipient wallet before any write. Tampered recipient and fee
fixtures now assert no audit or lifecycle creation.

The new WorkflowCosts fixture uses SurrealTransientConflict and needs the
SurrealForge.Client.Idempotency import in that file. Concurrent acceptance/cancel
fixtures need bounded WaitAsync so a broken barrier/transaction fails rather than
indefinitely blocking the eventual integrated sweep. The import is corrected and
both overlap fixtures now bound the await to fifteen seconds.

Before workflow activation, subsequent preparation/first-send must also bind this
exact acceptance receipt and original witness's current watermark/grants. Existing
ActionExecutionFence alone checks run/node authority, not a subsequently revoked
signing grant. Already authorized exact-wire observation and final accounting must
remain independent of fresh signing permission.

## Subsequent-effect authority delta

SOURCE_VERDICT: ACCEPTED bounded after independent reread of
NftWorkflowCostAuthorityFence, the command receipt link, lifecycle Fenced integration
and added revocation fixture. The accepted receipt is now carried in the command
and checked against the immutable audit. New effect transitions lock the exact
original execution/signing/reference/frozen hash and compose original credential
watermark/grants with existing action authority. Observation-only updates do not
require new signing permission. The authored fixture covers revoked source before
Preparing/Submitted targets and continued observation; runtime remains NOT RUN.

## Service and resolver integration

SOURCE_VERDICT: ACCEPTED bounded source-only for NftWorkflowCostConsentService,
NftWorkflowCostsController, disclosure DTO, lifecycle admission routing and the
ActionNftOperationService changes. Quote and acceptance use actual captured
credentials; acceptance retains the exact original execution and signing witness.
Matching receipt replay precedes mutable permission checks. Reconciliation reads
execution version before the immutable audit, so absence alone cannot retire a
saved request. Solana workflow recovery parks before fresh dispatch; accepted
metadata remains bound to the original draft while the separate receipt binds cost
consent. The authored fixtures cover parked recovery and ordered reconciliation.
SDK/UI workflow acceptance remains required. Runtime gates are NOT RUN.
