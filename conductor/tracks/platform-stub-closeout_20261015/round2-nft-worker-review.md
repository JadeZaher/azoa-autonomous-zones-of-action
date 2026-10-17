---
type: Review
title: Restricted NFT reconciliation source review
status: Source reviewed including overlap fixture; runtime unverified
---

Coordinator independently inspected the reconciler, worker, AdvanceAsync guards, typed pending query, in-memory store and authored reconciliation tests. No blocking source defect was found in this bounded pass. The reconciler excludes preparation and participant-acceptance states, rotates UpdatedAt using CAS before provider work, observes or replays exact persisted authorized transactions, and stops before preparing a subsequent stage. Terminal projection retains the existing atomic lifecycle/ownership commit. Per-record worker exceptions are isolated and cancellation propagates.

Authored tests cover submitted convergence, accepted-stage transition without delivery signing, no-effect evidence without replacement signing, excluded states and oldest-batch outage fairness. Requested additions cover a concurrent worker/interactive state transition and AwaitingRefundDestinationAcceptance. This is source evidence only; the tests have not run. Final integration must also verify service registrations and real Surreal pending-query/CAS behavior.

Follow-up reread accepted the added deterministic CAS-loss barrier test: a concurrent transition to Prepared causes reload and returns without signing or sending. AwaitingRefundDestinationAcceptance is covered by the exclusion theory. Confirmed projection now commits before provider lookup, avoiding an unnecessary provider configuration dependency after final chain evidence is durable. These additions remain unexecuted.
