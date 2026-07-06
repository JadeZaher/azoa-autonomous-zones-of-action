# Services/Webhooks — outbound webhook delivery workers

Transactional-outbox delivery workers that drain a webhook outbox and POST due events
to tenant-registered endpoints. Both workers share the same discipline: a singleton
hosted loop, a config-driven interval (`WebhookOptions`), a fresh DI scope per tick
(the stores are scoped), a last-ditch guard so a bad tick never tears down the app,
and the SAME security primitives — `WebhookSsrfGuard` (https-only + public-IP allowlist,
re-checked immediately before each POST for DNS-rebind defence), `WebhookHmacSigner`
(replay-resistant timestamped HMAC over a length-prefixed preimage), and the shared
`IWebhookRegistrationStore` (one registration per tenant; strict per-tenant isolation).

## §consent-webhook

`ConsentWebhookDeliveryWorker` drains `consent_webhook_event` (consent lifecycle events:
granted/revoked/expired). Observe-only: it never writes back to `consent_grant`.
tenant-consent-delegation §4 AC7/AC8/H5.

## §quest-webhook

`QuestWebhookDeliveryWorker` (final-hardening F3) drains `quest_webhook_event` — the
GENERIC mirror for arbitrary tenant-defined `quest.emit` events fired by an `Emit`
quest node.

**Why a parallel outbox rather than one generic table.** The consent outbox is a shipped,
security-reviewed path with a fixed event shape (grantId/scopes/participationRef). Rather
than reshape it and risk regressing consent delivery, F3 adds a PARALLEL outbox
(`quest_webhook_event`) with its own POCO/store/emitter/worker, and REUSES the shared
security + registration infra unchanged. A tenant configures ONE `WebhookRegistration`;
that single endpoint receives BOTH its consent events and its quest.emit events, each
signed with the tenant's own secret. Net result: zero consent-path change, full infra
reuse, and a clean generic quest event surface.

**Payload shape.** `{ eventType, runId, nodeId, questId, payload, occurredAt,
idempotencyId }` where `payload` is the tenant's opaque object re-embedded as a nested
JSON object (a malformed stored payload degrades to `{}` rather than failing delivery).
`eventType` is the tenant-defined name from the `Emit` node config (defaults to
`quest.emit`). The HMAC is computed over EXACTLY the serialized body string.

**Best-effort boundary.** The worker never writes back to `quest_run` /
`quest_node_execution`. A dead-lettered event does not fail or roll back the run — the
`Emit` node's serialized `Output` remains the tenant's authoritative settlement surface;
the webhook is a convenience push on top.

**Delivery uses its own named HttpClient** (`WebhookOptions.QuestHttpClientName =
"quest-webhook"`) configured with the SAME SSRF-guarded, `AllowAutoRedirect=false`
primary handler as the consent client, so a 3xx to an internal address surfaces as a
delivery failure instead of being silently followed.
