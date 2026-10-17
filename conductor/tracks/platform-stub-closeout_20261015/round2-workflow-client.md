---
type: Review Notes
status: Source authoring complete; independent review and execution pending
---

# Ordinary workflow admission client

Start requests now require the caller's persisted idempotency key. Both SDK execute and workflow start encode the same explicit consent and expected published-version hash used by the backend's immutable invocation identity. A preview endpoint exposes the declared economic effects and version hash. Repeating a request after response loss retains its original key and disclosure; admission is not completion.

The fluent run driver acquires child authority before starting a child run and uses it for status reads as well as advance/signal. Actor changes after queueing are rejected so pending calls cannot silently change principals. Template instantiation remains an explicit authoring boundary; consumers needing restart-safe recovery persist the resulting action before preview/admission.

Source fixtures cover lost start response followed by identical retry, missing key, consent without a version hash, child status credentials, immutable actor selection, and existing ordered start/advance/signal calls. They have not been executed. The ordinary frontend panel is assigned to lane A for persisted request/account scope and explicit disclosure.

The operator recovery allowlist now includes the registered read resolver. A real Surreal fixture verifies a parked original step can be awakened without executing a read during operator inspection/retry. Independent source review pending; no runtime claim.

Independent SDK review found fluent template start re-instantiated on retry, changing action-scoped run identity and risking duplicate effects. Root removed automatic instantiate from that shortcut pending the required Round 3 keyed template-authoring implementation. Existing explicit instantiate/persist action/preview/start remains supported and is covered by the source fixture. This is an open required implementation item, not a deferral or completed convenience API.
