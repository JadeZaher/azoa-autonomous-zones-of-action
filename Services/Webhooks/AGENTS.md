# Services/Webhooks - outbound delivery

Workers share HTTPS/public-address enforcement with DNS-bound connection resolution,
a redirect/proxy/cookie/credential-free HTTP handler, bounded request/response headers,
and timestamped HMAC. HTTP response bodies are never buffered for acknowledgment.

## Action webhook

`ActionWebhookDeliveryWorker` consumes the immutable events accepted atomically with
`ActionEmitReceipt`. It claims through `IActionWebhookDeliveryStore`; listing is not
permission to send. Each lease captures the original active destination and current
encrypted secret revision under registration/event serialization. A claimed attempt
may finish after disable or rotation; a subsequent attempt requires fresh authority.
Completion checks exact holder, version and database-time expiry. Stale completion
cannot overwrite a newer claim. Abandoned attempts count toward the retry budget.

The stable canonical body contains eventType, runId, nodeId, actionId, payload,
occurredAt and idempotencyId. No payload repair or destination substitution occurs.
Every request signs those exact bytes with a fresh timestamp and includes the secret
revision. An HTTP 2xx acknowledges delivery only. Lost responses can produce duplicates;
receivers deduplicate the stable idempotency ID. Delivery does not rewrite action state.

Unavailable original destinations, missing acceptance evidence and exhausted attempts
become visible dead letters. Stored errors are bounded symbolic codes. Protected attempt
material is cleared on completion or expired-attempt retirement. Unexpected failures are
logged once at the polling boundary; cancellation propagates. The old generic writer and
best-effort emitter have been removed. Audited retry/discovery client integration remains
required before this closeout is considered complete.

## Consent webhook

`ConsentWebhookDeliveryWorker` delivers granted/revoked/expired events and never changes
consent grants. It already uses the shared protected secret and safe transport. Migration
of its older delivery state machine to equivalent lease/destination guarantees remains
required; do not claim that the action worker alone proves consent delivery parity.

## Protected registration secrets

`WebhookSecretProtector` uses the existing AES-GCM platform key facility. Its authenticated
envelope binds purpose, tenant, registration and secret revision. Cross-identity swaps
fail, missing keys fail visibly, and there is no plaintext fallback. New secrets use 32
random bytes. The audited management flow requires explicit legacy replacement and uses
an exact keyed legacy snapshot. Normal reads never disclose stored signing material.

Create/update/rotation are versioned and audited. Destination and secret revisions are
separate; secret-only rotation keeps destination identity. A generated secret is disclosed
only by the first successful authorized mutation, never by replay. Tenant/operator editors
persist exact commands before mutation and use server-backed withdrawal after uncertainty.

## Action delivery retry

`LeasedWebhookSender` owns only bounded HTTP and signature construction from a
frozen lease. It returns a completion decision; workers perform persistence after
it returns. This keeps database transport failures outside the HTTP retry boundary
and lets action and consent workers share identical wire behavior. Insufficient
lease lifetime returns no completion and starts no HTTP request.

Discovery returns tenant-scoped metadata without payload or protected attempt material.
An explicit audited retry requires the exact dead-letter version, original accepted event
evidence, current management authority and the original active destination revision.
It starts a fresh bounded attempt cycle while retaining lifetime attempts and event identity.
The registration lock, event transition and immutable retry receipt commit atomically.
Same-command response-loss recovery returns that receipt without starting another cycle.
The SDK and tenant/operator panels persist the exact command before sending. Explicit
reconciliation archives it only after an authenticated read proves a strictly newer
event version; this does not cancel a previously accepted delivery.

### Consent delivery cutover

ConsentWebhookDeliveryWorker now uses IConsentWebhookDeliveryStore and LeasedWebhookSender. It sends only immutable-proof-backed events under an exact lease and completes storage outside HTTP error handling. Legacy events lacking original transition evidence are suspended with an explicit reason; they are not rebuilt from mutable grants or redirected to current registrations. Consent metadata and retry endpoints use the same actual tenant/operator authorization binding as action notifications, with separate consent event routes and command identity domain.
