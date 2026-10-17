---
type: Verification
status: Authored and incomplete; no execution yet
title: Governance acceptance migration
---

The old in-memory governance world allowed any verified child to administer a proposal, mutable questions before the first vote, unrestricted early tally and a separate best-effort ballot receipt. Those semantics are replaced by principal-only administration, authority records immutable from creation, database cutoff, and atomic command receipts. Tests must exercise the new contract rather than keep obsolete constructors or silently emulate the retired path.

Existing hash stability/collision/close-time tests and generic uniqueness scope tests remain. The shared fake identity/claim ledger remains for scope tests. Proposal rewrite cases now test unconditional projection immutability. Cast-time tally evidence tests refuse malformed evidence rather than omit a voter. Actual transaction cases live in `SurrealGovernanceTests`: simultaneous creation, cancellation, atomic receipt recovery, revote replay, generic claim bypass, current membership/KYC/AML/selection checks, cutoff, forged ballot Holons, final snapshot and participation-origin membership.

Still required before source acceptance: policy revision fencing, full service/handler creation validation and tenant-administration tests, interaction and subtree mutation coverage after the local planner lands, simultaneous cancellation versus commit, receipt recovery after cancellation, finalizer race and changed-intent conflict, positive/negative live participation expiry, newly added member exclusion, proof that offboarding after casting does not alter the finalized result, and review of all migrated assertions. No test/build/schema gate has run. This file is an implementation checklist, not a deferral.

The independent reviewer found the action cancellation race; the shared `ActionExecutionFence` correction has source review only. Runtime database behavior and generated schemas remain pending the integrated sweep.

Follow-up authoring: policy absent-to-present and changed-version refusal, receipt replay after cancellation and changed-intent conflict, live/expired participation, offboarding/rejection after casting without tally loss, metadata interaction/direct clone immutability, typed null/options/window/quorum/scope failures, child-admin refusal and dedicated-store dependency guard now have fixtures. The obsolete unbounded loose tally helper was removed after a reference search found no consumers. The shared KycEvidenceFence joins voting and verification/AML mutations; profile/selection rows are locked at vote commit. LocalEntityMutationFence.Holons joins creation/finalization projections to local graph mutation serialization.

Still pending: complete positive service journey, two-transaction cancellation/evidence append races, concurrent finalizers, subtree planner coverage and independent final reread. All added cases remain unrun.

The positive service journey now exercises provisioned-principal creation through the real handler and exact frozen resolver replay after cancellation. Unprovisioned principal/child denial and concurrent finalizers are authored. Failed frozen validation now commits an immutable failed governance command receipt under the same action fence; recovery returns Failed without allowing a competing success after rejection. The real-store failed-receipt replay fixture is authored. Independent review requested. No tests have executed.
