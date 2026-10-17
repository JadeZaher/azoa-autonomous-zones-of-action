---
type: Review
title: Action engine source review during implementation
status: Corrections required; full package still in progress
---

Coordinator inspected the typed execution store, operation identity/resolver and modified admission/replay seams. Execution updates now use a version and optional state predicate rather than an unconditional post-read update. The recovery coordinator validates envelope version, run actor/tenant, canonical frozen input hash and registered domain before dispatch. These are source observations, not runtime results.

Two concrete findings were sent to the author:

1. Rail admission resolves the handler's rail from node.Config before applying binding.ResolvedJson. Bindings can therefore select a different effective rail from the one gated. Gate the resolved immutable input before admission/egress and test a bound rail differing from the default.
2. Successor enqueue still uses StepExistsAsync followed by EnqueueNextStepAsync; the underlying saga insertion uses a fresh GUID without a unique action-run/node claim. Concurrent replay can enqueue duplicate successors. Add atomic deterministic action successor admission with immutable payload comparison or equivalent receipt/enqueue transaction, preserving other saga families' compensation semantics. A read-before-create guard is not exactly-once evidence.

Handler adapters, frozen runtime integration, local mutation planners, operator journeys and governance remain in progress. No full-package approval or executed-test claim is made here.

Bounded frozen-runtime/admission reread confirms deterministic successor record identities with immutable admission payload hashes, typed record links, full frozen envelope actor/run/node/hash checks and version-CAS execution updates. Source review caught invited-user admission comparing bare GUID against persisted user:N link strings; author corrected the predicate and added graph/snapshot equality. Positive invited-user persistence coverage remains requested. Full DAG parity, local receipts and handler integration are still in progress; no runtime verdict.

## Atomic initial-node admission follow-up

The scheduler now waits for Running siblings before compensation and stops scheduling new Pending nodes after an unhandled failure. A remaining race exists between that scheduler read and TryAdmitAsync: the latter only compares the node version. A suspended worker can admit after a sibling fails and the run moves to reconciliation. The author accepted a transactionally validated scheduler witness with run and execution version serialization, including terminal-node writers and compensation decisions. This correction and its interleaving fixtures remain in progress; existing admitted effects must remain observable.

The scheduler epoch correction now atomically increments the run version with each node outcome and admits a fresh node only against the exact scheduler version and Running state. Stale decisions retry. Root bounded reread found no additional concrete blocker in that correction; a real-store stale-epoch fixture is authored, not run. Occurrence boundaries separately wait for all prior source nodes, allow skipped branches and require failed branches to be recovered. Full adapter/client closure remains pending.

## Grant post-confirmation attachment

Root authored ActionNftHolonLinkService after lane B reviewed its contract. It reuses the real manager over the local overlay and the existing atomic receipt store. The receipt pins the complete confirmed lifecycle descriptor and is replayed before current ownership checks; a previously bound token without this receipt cannot be overwritten or inferred as this grant. Real-store concurrent attachment, post-plan binding/owner/cancellation changes, and replay-after-owner/readiness-change fixtures are authored. Lane B integration and independent source review pending; no tests run.
