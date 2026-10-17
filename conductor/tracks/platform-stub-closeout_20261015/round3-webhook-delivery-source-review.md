---
type: Review
status: bounded-source-accepted-runtime-unrun
---

# Independent delivery store review

Lane B reviewed SurrealActionWebhookDeliveryStore and its Unavailable partial against the accepted Emit/delivery contract. The destination and secret revision are locked alongside an exact event epoch lease. Database-time due and expiry fences prevent stale claims/completions; finishing clears protected attempt material. Retiring exhausted or unavailable events cannot steal an active lease. Prebuilt conflict retries preserve the captured body, secret and holder rather than regenerating authorization.

No new bounded source blocker was found. A temporarily restored destination can conservatively leave an event dead-lettered after the documented race; audited retry is required implementation. Old generic event writers must be retired during worker integration so accepted identity/body fields remain immutable and epoch changes remain authoritative. The review does not accept the not-yet-integrated worker, domain wiring, delivery client or runtime behavior. Real persistence and overlap fixtures are authored but have not executed.

## Worker and handler integration review

Lane B separately reread ActionWebhookDeliveryWorker, EmitNodeHandler, same-instance resolver/store/worker registrations and replacement fixtures. One defect was found and corrected: the HTTP catch boundary included the delivered datastore completion, which could relabel a Surreal HttpRequestException as receiver transport failure. The corrected boundary covers only HTTP send/classification; persistence exceptions bubble unchanged. The new 204-plus-completion-failure fixture asserts one Delivered attempt, no Retry mutation, and the original exception.

No further bounded source blocker was found in this integration. Exact claimed body/revision/HMAC, guarded DNS-bound transport, bounded timeout below lease, headers-only responses, stale-claim no-send and stable operation references are represented in source and authored fixtures. Old best-effort/generic outbox writers are removed. This accepts the reviewed integration source only; audited retry/discovery, consent delivery parity and all runtime gates remain outside this verdict.

## Audited event management review

Lane B separately reviewed SurrealActionWebhookDeliveryStore.Management, WebhookEventManagement, retry/status models and audit, tenant/operator controllers, and real-store retry fixtures. No new bounded source blocker was found. Management uses actual principal binding; mutations join current management credential and tenant activity, exact dead-letter event epoch, unchanged original registration destination and immutable keyed audit. Lifetime attempts remain monotonic while the explicit retry generation resets only its cycle budget. Replays return the original receipt and changed terms conflict. Metadata excludes body/ciphertext and sanitizes unrecognized legacy error text.

Authored fixtures cover concurrent same-command receipt, lifetime/cycle distinction, original bytes, changed reason and replacement destination/cross-tenant refusal. Tests remain unrun. Client recovery semantics and consent-worker parity are outside this bounded backend verdict; runtime acceptance still awaits the final integrated sweep.

## Shared sender extraction

Lane B independently reread LeasedWebhookSender and its ActionWebhookDeliveryWorker caller after extraction. No new bounded source blocker was found. The sender consumes only a frozen lease, preserves exact body/signature headers and bounded HTTP classification, checks cancellation before work and refuses insufficient remaining lease lifetime. Backoff clamps negative exponents. Persistence completion remains outside the HTTP catch boundary. The insufficient-lifetime fixture is authored but unrun; this verdict does not assert delivery runtime behavior or consent integration completeness.
