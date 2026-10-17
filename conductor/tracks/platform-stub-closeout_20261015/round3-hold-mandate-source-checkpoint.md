---
type: handoff
status: source-review-pending
---

# B2 mandate source checkpoint

Author: Lane B. Runtime: NOT RUN. No service/worker activation requested yet.

The accepted amendment now has immutable server-offer and acceptance storage,
exact owner disclosure, audited revocation/renewal, current-source preparation and
first-send fences, deterministic operation binding, restricted participant behavior,
due scheduling, and finalized mandate accounting. Controllers expose disclosure,
acceptance, owner discovery, exact renewal and revocation with actual authenticated
credentials. Acceptance is distinct from settlement.

Saved management requests have a read-only reconciliation result: Applied from an
exact immutable receipt; Obsolete only after a strictly higher durable version and
a second absent receipt read; otherwise Unresolved. The original request must be
retained on infrastructure errors or equal-version reads. Unit fixtures cover the
receipt committing between the two reads.

Primary source inventory:

- Models/Blockchain/HoldCommandContracts.cs and ValueCustodyContracts.cs.
- HoldReleaseMandate, HoldReleaseMandateOffer and HoldMandateUseReceipt POCOs;
  HoldLedger.ActiveReleaseMandateId.
- IHoldReleaseMandateStore; SurrealHoldReleaseMandateStore and Offer, Management,
  Discovery partials; HoldReleaseMandateFence.
- SurrealHoldCommandStore.Mandate and existing Admission, Authorization,
  Preparation, Completion, Replacement integrations.
- HoldReleaseMandateService and Execution/Management partials;
  HoldReleaseMandateWorker; HoldReleaseMandatesController.
- HoldReleaseMandateIdentity and independent cost-category fixtures;
  SurrealHoldCommandTests.Mandate real-store disclosure/revoke/first-send/final
  accounting fixtures. SurrealHoldCommandTests.AcceptanceRecovery preserves
  saved unsigned transfer intent and participant no-effect evidence on replacement.

The source admits only persisted mandate IDs with mutually exclusive authority.
At each preparation/save/first-send transition it creates an immutable use receipt
pinning the exact current mandate revision, authority receipt, terms and wire hash.
The same transaction checks current revocation, database cutoff/expiry and exact
reserved liability. Original JWT lifetime is omitted only within that transaction;
key/user/grant revocation and grant expiry remain current. No recipient preparation
or replacement generation is authorized by the mandate worker. Submitted accounting
can complete after revocation, then makes the original mandate terminal.

Remaining implementation, in order:

1. Independent review corrections, concurrent revoke/egress and delayed-credential
   fixtures, workflow-origin mandate contract/atomic source integration (concrete
   mapping in round3-hold-workflow-mandate-mapping.md). Automatic release cannot be inferred from
   the legacy boolean.
2. Frozen Hold/Release/HoldRefund/ReleaseIntoHold adapters, exact downstream source
   linkage, current economic source receipts and legacy IPlatformHold caller/worker
   cutover.
3. SDK/UI quote disclosure, persisted request keys, recipient acceptance, mandate
   management and safe recovery discovery.
4. Ethereum/Solana value-custody companion parity against the same exact quote and
   participant authority contract.
5. Full changed-file pruning, independent source handoff, central registration and
   schema inventory. Runtime validation remains the final integrated sweep.

These are required work packages, not external deferrals. B1's broader lifecycle,
provider/client source review and final activation/runtime also remain pending.

## Workflow opening source checkpoint

Authored, awaiting independent source review; runtime NOT RUN. New workflow opening
store composition parks an immutable disclosure without drawing reserve. Explicit
actor acceptance commits the existing local Hold/reserve plan, source-bound mandate
and exact acceptance receipt in the same transaction. The source records the exact
execution record, frozen hash, original signing witness and original opening receipt.
Delayed release requires that exact node to have succeeded and excludes cancelled,
failed, compensating or unrelated executions. Original acceptance receipt replay
precedes mutable authority checks and never redraws reserve.

Review paths: SurrealHoldCommandStore.WorkflowOpening.cs and shared CommitLocalCore,
HoldReleaseWorkflowFence.cs, HoldCommandService.WorkflowOpening.cs,
HoldWorkflowOpeningIdentity.cs, HoldCommandsController accept-opening route,
and SurrealHoldCommandTests.WorkflowMandate.cs. The authored fixtures cover no draw
while parked, cancelled original source, exact accepted disclosure, successful-node
requirement for delayed release, and original receipt replay after cancellation.

Still required: real frozen quartet adapter, provider-produced disclosure integration,
clients, legacy caller cutover and provider parity. Disclosure recovery and overlap
fixtures are now authored in the slices below and await source/runtime review.
No registration or activation is requested by this checkpoint.

The read-only reconcile-opening endpoint now resolves saved acceptance by reading
the scoped command version before the exact audit. A higher version without that
audit fences the old CAS as Obsolete; equal/lower remains Unresolved, exact audit
is Applied, and any storage error propagates. The unit recovery fixture enforces
that read ordering and absence of mutation.

## Unaccepted disclosure recovery slice

Awaiting independent source review, runtime NOT RUN. WorkflowOpeningRecovery adds
server-quoted replacement with unchanged original mandate bounds and full owner
withdrawal. Command version CAS plus immutable recovery audit compete atomically
against opening acceptance; neither path touches reserve or liability. Expired
quote replacement returns a new command version requiring explicit acceptance.
An expired immutable mandate deadline requires withdrawal and a new authorized run,
not a silently extended permission.

New routes: replace-opening and withdraw-opening. The service resolves exact
replacement audit replay before provider quotation. Integration fixtures now cover
expired disclosure replacement, stale acceptance refusal, exact replacement replay
and acceptance-versus-withdrawal overlap with exactly one reserve outcome.

Child ReleaseIntoHold uses the same disclosure composition but binds the new mandate
to plan.Child and its immutable inherited definition. Its fixture verifies parent
settlement, distinct child mandate/source and conserved reserve-held units. Original
workflow signing credential/grants and exact saved signing JSON are now rechecked
alongside ActionExecutionFence for later rail consent/preparation/first-send, so
fresh participant consent cannot replace a revoked original source.
