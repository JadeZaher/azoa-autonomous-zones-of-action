---
type: design
status: accepted-for-staged-implementation
---

# Workflow-origin Hold mandate mapping

Root independently accepted this mapping with the execution-state and exact-record
clarifications below. Runtime remains unrun. The shared ActionExecutionFence is unchanged.

## Original admission and explicit cost consent

The Hold adapter uses its existing frozen ActionFrozenNodeInput and selected
WorkflowSigningWitness, requiring action:execute and transfer:sign within the
original authenticated credential ceiling. Its deterministic command identity is
bound to run/node/actor/tenant and canonical frozen input, not a caller-supplied
replacement run. Exact selected grant IDs remain part of that source.

AutoRelease=true alone cannot activate a mandate. If the original frozen node
requests delayed release, the domain first persists its exact resolved Hold draft,
recipient wallet, full server quote and independent cost limits as an
AwaitingConsent operation. This is a parked action, with no reserve draw or Hold
liability yet. The actual run actor reviews that exact disclosure and explicitly
accepts its saved hash through the Hold continuation API. This supports bound inputs
whose recipient/amount were not knowable before workflow execution; no fabricated
pre-run quote or implicit action acknowledgement substitutes for cost consent.

AwaitingConsent is a domain state only. The engine's node remains Running and the
run is Running/AwaitingReconciliation as already permitted by ActionExecutionFence.
Continuation preserves or atomically resumes that exact execution through the
existing engine transition; it never invents a run state or marks a node Succeeded
before the effect commits.

The accepted transaction revalidates the original Running action source using
ActionExecutionFence, exact saved node signing witness, actual accepting credential,
current user/authentication watermark and selected scopes/grants. It composes the
existing HoldLocalCommandPlan and reserve mutation with immutable command receipt,
mandate terms and original source record in one transaction. A conflict commits
none of them. Recovery before acceptance performs no local accounting or signing.
Once this transaction completes, the node's Hold effect is confirmed; ordinary
engine completion can mark that original node Succeeded. The eventual release is a
separate visible operation referenced by its mandate, not a second success claim
for the opening node.

## Persisted source fields and delayed fence

The mandate adds a discriminated workflow source containing the exact execution
record ID, RunId, NodeId,
FrozenInputHash, original opening command/receipt identity and canonical selected
WorkflowSigningWitness. The source is written only in the opening transaction.
Original authority and explicit cost acceptance both remain immutable; an explicit
reauthorization appends evidence without changing source, asset, recipient or terms.

The delayed mandate fence reads/locks the actual action_run and action_node_execution
records with typed record references. It requires:

- run UserId/ActingTenantId equal the original actor/tenant; run status in Running,
  AwaitingReconciliation or Succeeded only;
- node RunId/NodeId match, State=Succeeded, canonical FrozenInput SHA256 matches the
  saved hash, and SigningAuthorityJson matches the source witness;
- the immutable successful opening HoldCommandReceipt proves this exact liability,
  asset/network/protocol, owner, quantity and mandate source; no latest graph lookup;
- active mandate version/hash/current authorization, database cutoff/expiry and
  ActiveReleaseMandateId all match;
- saved credential ceiling, current active user/authentication watermark, current
  exact grant identities/scopes/expiry, and current exact beneficiary wallet binding.

Cancelled, Failed, Compensating, Suspended, timer/signal waits and an opening node
that is not yet Succeeded cannot authorize a new delayed effect. Run status locking
serializes with cancellation; original Hold liability locking serializes with
refund/chaining/compensation. A completed opening source is not replaced by a later
run, retry or fork. Revoking the mandate does not rewrite already authorized bytes.
Submitted wire observation and finalized accounting continue under the immutable
original receipt even if the run later becomes terminally unsuccessful.

## Reuse and boundaries

The existing HoldRailExecutor and exact-wire provider modules remain the only
transfer path. Mandate-use receipts retain the exact source and authority revision
used for each preparation and first-send CAS. Worker execution never signs recipient
acceptance, raises cost caps or replaces a generation. Recipient acceptance remains
an actual participant request and its own immutable authority/wire evidence.

The current direct-mandate path stays separate: it may attach only to an already
proven owned liability after its own explicit disclosure. Workflow opening does not
pretend to be that direct user path and never synthesizes JWT times. The UI must show
opening completion separately from scheduled/participant-pending release and retain
both identities for recovery.

Required fixtures: stale consent acceptance racing cancellation/rerun;
crash before/after atomic opening; no draw while AwaitingConsent;
same-key changed frozen input; original grant revocation; node not-yet-Succeeded;
run Succeeded permitting exact delayed authority; cancellation racing first-send;
refund/chaining racing release; new fork cannot inherit mandate; recipient absence
never invokes custody; expired accepted JWT with live source versus changed
authentication watermark; terminal proof still accounted after cancellation.
