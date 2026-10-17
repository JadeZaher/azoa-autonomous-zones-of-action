---
type: Implementation Handoff
status: Source authored; independent correction applied; runtime unrun
---

# Emit acceptance store

The deterministic receipt and optional tenant event commit in one transaction under the frozen execution/run fence, original canonical credential and current action-execute grant witnesses. Typed registration CAS pins its exact identity, destination revision and URL. Equal receipts replay before mutable checks. Invalid object configuration produces a durable rejected receipt with no event; absent registration remains unaccepted and retryable.

New source: ActionEmitReceipt, IActionEmitStore, ActionEmitIdentity, SurrealActionEmitStore, and optional pinned destination/body fields in the public ActionWebhookEvent POCO. No runtime wiring is claimed. The existing emitter and worker still require replacement by the reviewed contract.

Independent lane B review identified omitted Config causing GetRawText to throw before rejection. The correction checks Config.ValueKind first. Real-store fixture coverage now includes omitted and array Config, original/recovery contention, original replay after actor deactivation, changed input and cancelled run refusal, absent registration and pinned destination after replacement. Fixtures have not run, per the integrated-sweep policy.

Remaining implementation: recoverable handler/domain resolver; typed leased outbox and captured protected secret revision; shared consent compatibility; stale-lease and rotation/disable races; authenticated event discovery and audited retry with SDK/UI. These are required work, not deferrals.

## Recovery resolver and event verification

ActionEmitService validates the exact admitted reference and canonical frozen input. An existing receipt replays before mutable run/consent checks, but tenant success additionally verifies the original outbox record's identity, destination, payload and immutable body hash. Missing receipt recovery selects consent using the original stored run credential and retries the same atomic acceptance. No HTTP runs in recovery. The body retains the existing receiver field names and GUID representation. New unit and real-store corruption fixtures are authored but unrun. Handler/DI and leased worker migration remain required.
