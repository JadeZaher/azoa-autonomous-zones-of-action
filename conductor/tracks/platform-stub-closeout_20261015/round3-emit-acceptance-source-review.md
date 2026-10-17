---
type: Review
status: bounded-source-accepted-runtime-unrun
---

# Emit acceptance store independent source review

Lane B independently reviewed `SurrealActionEmitStore`, `IActionEmitStore`, `ActionEmitReceipt`, `ActionEmitIdentity`, the added `ActionWebhookEvent` fields, shared execution/signing fences and the Emit contract. Root remains the author; this is a separate review pass.

The bounded acceptance slice is source-accepted. Deterministic identity, exact canonical frozen input, original run credential ceiling, current selected grant locks, execution cancellation fence, destination identity/version and scoped typed event/receipt writes join one transaction. Conflict retries preserve the prebuilt body and receipt, and matching committed receipt replay precedes mutable authority. Non-tenant acceptance is deliberately local; tenant acceptance requires a valid registration. This verdict does not cover the pending resolver, worker, delivery transport or client integration.

One correction was required: missing Config deserialized to Undefined and `GetRawText()` threw before durable deterministic rejection. Root added the object-kind guard, and this reviewer reread it. Authored real-store fixtures now include missing and array config. Stale best-effort webhook wording in `EmitNodeConfig` was corrected to the outbox-acceptance contract.

No build, test, schema generation or protocol runtime was executed. Actual transaction, schema, duplicate concurrency and cancellation behavior remain mandatory in the final integrated sweep. The shared typed-statement assignment helper is source-reviewed only; its real persistence fixtures are unrun.

## Resolver addendum

The reviewer separately inspected `ActionEmitService`, `ActionEmitRecoveryTests` and `HasAcceptedEventAsync` after the store review. Exact domain/reference/canonical-input validation and committed receipt recovery precede mutable run/credential reads. Missing receipts reuse original admitted credentials and current grant selection for atomic acceptance, without HTTP. Tenant success checks exact event identities, destination, payload and immutable canonical body hash; original receiver GUID formatting is preserved. The added body-tampering persistence fixture is authored and unrun. No further bounded source defect was found. Resolver source is accepted; delivery, wiring and runtime remain outside this verdict.
