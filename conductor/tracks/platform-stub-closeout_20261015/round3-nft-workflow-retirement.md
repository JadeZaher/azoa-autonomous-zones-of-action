---
type: design
---

# Retiring an uncommitted workflow cost acceptance

An acceptance saved by the browser can expire before server admission. Missing audit plus an unchanged execution version remains Unresolved; neither quote expiry nor a client error proves no in-flight admission. The current UI correctly refuses replacement but needs a durable way to retire that request.

Add an explicit authenticated retirement command for the exact original acceptance request, with an immutable retirement receipt. Bind actor, tenant, execution ID/version, request hash and original frozen reference. Under one transaction, current participant consent, exact execution-version CAS, absence of the acceptance audit, and creation of the retirement receipt must commit together. Preserve the original frozen input, signing witness and external reference. Increment the execution version so all suspended acceptance attempts for the old version lose their CAS. Retirement neither cancels the workflow nor signs or creates a mint.

Acceptance and retirement contend on the same execution row. If acceptance won, return its existing lifecycle; if retirement won, reconciliation reports the exact retired request and the UI may offer a fresh quote. Retry reads immutable receipts before mutable authority checks. A lost retirement response must remain recoverable by the original saved acceptance request, without minting a replacement request ID. Changed execution version remains a valid Obsolete proof only with execution-read-before-audit ordering.

Required fixtures: acceptance versus retirement race; retirement response loss and exact replay; retirement after committed acceptance returns Applied; stale acceptance cannot admit after retirement; wrong actor/tenant and altered request refuse; expired quote can be retired without making it fresh; refreshed offer still requires a new explicit acceptance. No runtime execution yet. Independent Hold lane agreed with the atomic retirement design; implementation remains required.

Implementation follow-through: retirement POCO/store transaction, service/controller, SDK and explicit UI control authored. Independent source reviewer accepted the bounded transaction and replay semantics. Database fixtures include acceptance race, quote removal, revocation replay, wrong actor/tenant and altered request; UI fixtures include lost retirement response and acceptance winning. These are authored tests, not runtime evidence; final integrated gates remain required.
