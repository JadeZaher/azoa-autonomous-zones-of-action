---
type: implementation
status: source-in-progress-runtime-unrun
---

# Hold rail authoring checkpoint

Lane B authored direct admission, explicit quote consent, durable preparation/first-send boundaries, exact finalized accounting and separate actual-recipient acceptance. These types are not registered as replacements for legacy hold callers. This is an intermediate review packet, not completion or source approval.

## Reviewable source

- HoldCommandContracts, HoldCommand and HoldCommandAuthorization carry distinct actor tenant and original custody tenant, immutable rail plan, current consent receipts and separate participant acceptance evidence.
- HoldRailAdmissionPlan creates Pending funded liability or reserves one Held outbound liability without claiming a chain effect.
- SurrealHoldCommandStore Admission/Authorization/Preparation join current exact credential/grant/wallet witnesses, original action authority, active liability, command CAS and database-time release cutoff. Prepared bytes precede first-send authorization.
- HoldRailCompletionPlan and the Completion partial join exact provider evidence, original liability, reserve partition changes and immutable command receipt. Funding becomes Held only after confirmed inbound; reserve release moves held units into issued accounting. Recording landed effects does not demand renewed signing authority.
- Acceptance persists actual recipient consent and separate prepared/submitted/final evidence. Transfer consent cannot stand in for recipient custody. NoEffect retains exact saved transfer bytes and authoritative verdict without replacement or releasing liability.
- HoldRailExecutor has separate interactive preparation and restricted observation/rebroadcast paths. HoldCommandService and its Admission partial provide direct admission, continuation, acceptance and safe scoped progress; no public route cutover yet.

## Authored evidence

HoldRailAdmissionPlanTests cover unpaid funding, identity substitutions and exact finalized inbound evidence. SurrealHoldCommandTests.Rail adds concurrent finalized accounting after credential revocation and a mismatched transaction refusal. Existing provider fixtures cover canonical signed envelopes and actual recipient signing. All fixtures remain unrun under the final integrated sweep policy. Targeted whitespace checks passed.

## Required next implementation

Renewed recipient authorization must preserve original consent history and exact prepared wire. Explicit no-effect replacement and safe cancellation need audited request replay, including version-loss reconciliation. The original auto-release mandate needs exact destination/cost authority and worker integration. Add participant discovery and server-backed command recovery, route/SDK/UI integration, frozen action adapters, authenticated economic-source caller migration and retirement of legacy independent writes. Ethereum/Solana companion parity remains implementation work. None of these are external deferrals.

The separate consent review found a current bootstrap-after-claim authority race; root accepted that finding and owns its repair. The atomic issuer itself is not the identified gap. Account-claim source review is queued for the next independent pass.

The next authoring increment added HoldRailCompletionPlan/Completion, explicit NoEffect persistence, Acceptance participant stages, HoldRailExecutor, and HoldCommandService Admission/continuation/participant-safe progress. SurrealHoldCommandTests.Rail authors actual datastore concurrency/finalization-after-revocation coverage using synthetic provider evidence at the store boundary; it is not an end-to-end chain proof test. Recipient request identity is preserved for replay. Renewed participant credentials and audited generation/cancellation remain the next mandatory source work before route cutover.


## Recovery/discovery increment

Authored (unrun, awaiting independent review): exact acceptance no-effect proof
can rotate the overall generation/quote while archiving original participant
wire, proof, authority and request identity. Replacement clears current consent;
confirmed prior acceptance stays preserved. New real-store fixture covers wrong
proof refusal, evidence preservation, stale-stage refusal and immutable replacement
replay with unchanged reserve counters.

HoldReconciliationWorker and typed store due-page scheduling observe/rebroadcast
only previously authorized Submitted wire. No fresh custody, participant signing,
first-send authorization or generation replacement is available to this worker.
New worker fixture covers dependency failure isolation, lost scheduling race,
exact saved-wire replay and no prepare/authorize calls. Not registered yet.

HoldCommandsController and participant-safe page DTOs now provide explicit
admission/list/get/refresh/continue/accept/replace/cancel contracts. Source cutover
is not complete: legacy caller adapters, SDK/UI, exact automatic-release mandate,
internal economic-source authority and ETH/Solana custody companion parity remain
implementation work, not deferred items. No runtime gates have run.
