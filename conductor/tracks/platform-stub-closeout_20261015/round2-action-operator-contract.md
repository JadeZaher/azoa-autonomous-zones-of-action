---
type: design
status: implemented-source-review-runtime-pending
---

# Audited action recovery

The root author proposed this contract and the action-engine author independently accepted it before implementation. Recovery has a read-only inspection route and an explicit retry command. Inspection returns execution/run/node identity, version, state, domain and intent hash. It never invokes a resolver or exposes frozen input, signing context, user payload or provider evidence bodies.

Retry requires a recent node-operator session, an exact execution version and intent hash, an operator-scoped request ID and a substantive reason. The server accepts only its registered, reviewed local/governance/deployment/NFT recovery capabilities. It verifies the frozen graph and actor/tenant, then atomically fences run/node authority, exact parked saga identity and original admission payload hash, increments the execution version, wakes that one saga step and creates an immutable audit receipt. The stored payload, including a previous signal, is preserved. Replaying an accepted request returns its receipt without waking a later park generation.

Retry resumes the original authorized recovery. Local/governance recovery may commit its original operation under current authority checks; deployment/NFT recovery may observe or replay already authorized wire and recover admission, but does not grant fresh participant signatures. Provider attachment is handled by the original domain's verified recovery path. This endpoint accepts no replacement operation ID, output, success assertion or reset. Legacy rows without immutable correlation remain inspectable and quarantined; missing evidence cannot prove an economic effect absent.

The generic saga operator controller refuses direct requeue/cancel for action workflows, including compensation rows. The dedicated recovery endpoint handles parked forward nodes; owner-authorized workflow cancellation preserves pending-effect reconciliation. The operator dashboard and SDK expose inspect/history/retry, preserve request identity after response loss, and use the existing same-origin BFF and recent-session controls.

Independent review found and corrected existing opaque saga-key encoding and signal-payload preservation mismatches. Real Surreal fixtures exercise the production admission/claim/park/signal stores, concurrent duplicate recovery, stale versions, cancellation, changed payload/gate/intent, replay after another park generation and legacy refusal. SDK/UI replay fixtures are authored. All runtime checks remain unrun until the integrated sweep.
