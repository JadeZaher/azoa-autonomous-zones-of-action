---
type: review
---
# Run panel request and selection source review

Independent lane C review of A/root changes in `frontend/src/components/action-builder/run-panel.tsx` and its `run/__tests__/run-panel.test.tsx`. No test/typecheck/build ran.

Root's bounded correction is sound by inspection: advance/signal/reconcile capture the current selection object, refuse egress while attaching or when its run ID differs from the displayed run, and reject late responses after selection replacement. Controls are disabled while the new selection loads. Polling also checks run/action response identity and selection object equality. The new regression attempts Advance while the second history request is unresolved and asserts no network mutation.

Start persists the exact idempotency key, published hash and economic acknowledgement before HTTP; storage failure prevents egress. Transport failures preserve that request, remount retries the saved request, and successful admission stores the run before clearing the start record. Account/action keyed remount and alive/selection checks prevent stale response attachment. Read attachment failures preserve the unresolved run and block replacement start. Server history supplies recovery discovery beyond browser storage.

No new blocking source defect identified in this bounded pass. This does not claim all action mutations are fully reviewed: Advance/Signal still generate a new key per click, so their end-to-end lost-response semantics should be explicitly covered by domain idempotency tests in the Round3 handler/consumer package. Previously identified stale-preview proven-unadmitted recovery and keyed template authoring remain required work; safe refusal is not feature completion. Runtime UI behavior remains unverified.
