---
type: review
status: source corrections accepted; runtime verification pending
---

# Action operator recovery source review

Lane A independently inspected ActionRecoveryOperatorController, ActionOperationRecoveryService, SurrealActionRecoveryStore, ActionRecoveryRequest and ActionRecoveryAudit, following their interaction with action admission, frozen-input recovery and SurrealSagaStore. Client/DI/tests were still in progress. No execution occurred.

## Required fixes

1. **Stored gate encoding mismatch.** The recovery guard compares `gate_id` with raw `operator:{node:N}`. Real `SurrealSagaStore.ParkStepAsync` stores `EncodeOpaqueKey(gateId)`, which prefixes the value and escapes colons. Consequently real parked records cannot pass the operator guard. Reuse one shared storage codec rather than duplicate its private encoding recipe; exercise parking through the real saga store in the persistence regression.
2. **Previously signaled gate payload becomes unrecoverable.** The service reconstructs ActionStepPayload with SignalPayload null and requires exact stored-payload equality. ActionManager deliberately stamps a nonnull signal into a gated step's payload before it executes. If that admitted effect later parks for operator recovery, its original payload no longer equals the reconstructed null-signal payload. Preserve the actual stored payload after validating run/action/actor/node identity; do not replace it or discard the signal. Validate the original admission hash separately because it intentionally predates signal stamping. Add a signaled-gate-to-NeedsOperator recovery fixture.

## Bounded source conclusions

RecentNodeOperatorSession protects the controller. Requests expose only request identity, expected generation, immutable intent hash and audit reason; no transaction/operation ID, output, state or signing override is accepted. The service restricts retries to registered known recovery domains, validates frozen canonical input and run/actor/tenant/node correlation, and the transaction joins ActionExecutionFence with exact execution and parked-step predicates plus an immutable audit. GET projections omit frozen inputs and outputs. Duplicate request receipts preserve the original audit.

No further concrete authority or new-signature bypass was identified in this bounded pass. Approval remains pending both reported fixes and independent reread; final integrated runtime verification remains mandatory.

## Correction reread

The author now reads the original persisted saga payload, validates run/action/actor/node identity while preserving SignalPayload, and separately verifies the original null-signal admission hash. The exact original payload participates in the transaction guard and is not rewritten. Gate/correlation/step comparisons use the shared SagaOpaqueKey codec. ActionWorkflowIdentity.StepId is reused for deterministic lookup. Both reported blockers are resolved by source inspection; authored real parking/signal fixtures and integrated verification remain required.

The reviewer authored the exact codec extraction and SurrealSagaStore substitutions at the coordinator's request. The coordinator subsequently independently accepted their exact `s_`/`_C_` encoding and legacy decode preservation by source inspection. Central scoped registrations for the recovery store and service were added as requested. No runs occurred.

## Persistence and client reread

The real SurrealActionRecoveryTests use the actual admission and saga stores to enqueue, claim, park and signal records. They cover concurrent identical requests producing one audit, an old receipt not waking a later parked generation, preserved signal payloads, changed run/version/input/gate/identity refusal, and legacy quarantine. A resolver that throws if called verifies operator inspection and scheduling do not themselves perform domain effects. These are authored assertions, not runtime results.

The SDK exports the recovery DTOs and bounded inspection/history/retry methods. The BFF admits only the recovery routes and exact audit-request fields. The UI retains the original request body after an unacknowledged response and presents unsupported legacy rows without a reset. The initial page was outside the operator console layout; the author moved it and its tests into `operator/(console)/actions`, restoring the existing shell, navigation and session-expiry handling at the same URL. The UI regression injects an actual lost response. The SDK regression initially described a lost response while returning two successes; this evidence mismatch was reported to its author for correction.

Generic saga requeue and cancellation now refuse action-workflow records, directing callers to their audited recovery or owner-authorized run API. No additional concrete source blocker was identified in this bounded persistence/client review. The reviewer authored separate policy-attribute and generic-saga bypass fixtures at the coordinator's request; those new fixtures await independent review. Final integrated tests, schema generation, typecheck and lint remain mandatory and unexecuted.

Coordinator independently reread the new policy-attribute and generic-saga bypass fixtures with no concrete source defect. The SDK lost-response regression now rejects its first fetch, asserts the returned error result, and retries the identical request body. Actual authorization policy execution fixtures are being authored separately; none of these tests has run.
