---
type: Implementation Handoff
status: Source authored; independent review pending; runtime unrun
---

# Webhook delivery leases

New IActionWebhookDeliveryStore and SurrealActionWebhookDeliveryStore claim due immutable accepted events against their exact event epoch and active pinned registration. Claim serializes destination/secret authorization with management, captures encrypted secret revision, increments attempt count/version and sets a bounded lease. Database time guards due/lease expiry. Completion requires the exact current unexpired holder and clears protected attempt material. Retiring exhausted abandoned or unavailable deliveries is visible and cannot steal an active lease. The original body/destination/idempotency ID never change.

WebhookDeliveryLease excludes encrypted secret from JSON serialization. Symbolic error codes are limited to 128 ASCII letters, digits, underscore or hyphen. Retained ciphertext is cleared on finish or retirement. An authorized in-flight request may finish after management changes, while a subsequent attempt obtains current authorization.

Real-store fixtures authored for concurrent claim winner, rotation preserving old attempt/new revision on retry, stable body/idempotency, stale completion refusal, disable versus active claim, and expired exhausted cleanup. No tests run. Missing handler/worker/DI wiring, event discovery/audited retry clients and consent delivery compatibility remain required implementation, not deferrals.
