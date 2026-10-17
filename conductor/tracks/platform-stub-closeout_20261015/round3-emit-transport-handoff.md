---
type: implementation
status: awaiting-independent-source-review
---

# Emit shared transport foundation

Both named webhook clients now use WebhookHttpTransport.CreateHandler: no ambient
proxy, cookies, credentials or redirects, bounded connection/header limits and
existing validated-address socket binding. Action and consent workers request
ResponseHeadersRead and reject outbound bodies over one MiB before signing/send.
WebhookSsrfGuard rejects URL credentials/fragments, scoped/multicast/reserved IPv6
and transition prefixes; documentation and the IANA source are in Core/Webhooks.

New WebhookTransportPolicyTests cover address boundaries, credential URL rejection
before DNS, mixed multicast resolution and handler configuration. No execution.
Actual headers-only/lost-response delivery fixtures are still required with the
leased worker rewrite, along with registration encryption, atomic Emit acceptance,
resolver, leases, retry audit and SDK/UI. This is not completed Emit capability.
