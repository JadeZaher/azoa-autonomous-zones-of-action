---
type: Review
status: Bounded transport source accepted; runtime pending
---

# Shared webhook transport review

A independently inspected WebhookSsrfGuard, WebhookHttpTransport, both named
client registrations, both workers' changed body/send paths and
WebhookTransportPolicyTests. No concrete bypass or regression was found in this
bounded foundation. No tests or network delivery were executed.

Both clients use the same guarded socket handler, with proxy, cookies,
credentials and redirects disabled. The socket callback classifies all resolved
addresses before connecting to an exact approved address, keeps normal TLS
hostname validation, disposes failed sockets and propagates cancellation.
IPv6 transition/reserved exclusions and embedded IPv4 checks match the documented
closed policy. Worker requests retain ordinary HTTP request defaults, so this
slice does not opt into a separate HTTP/3 transport outside that callback.

Both changed worker paths enforce the one-MiB UTF8 payload limit before signing
and sending, use ResponseHeadersRead and dispose the response without consuming
an unbounded body. The authored fixtures verify policy/configuration; actual
headers-only and lost-response behavior remains a required later fixture gate.

This acceptance excludes the existing worker durability/registration semantics.
The upcoming leased rewrite must remove the existing broad signing/delivery
exception catches that turn unexpected programming/storage errors into ordinary
delivery outcomes, and prune obsolete verbose setup/comment blocks. Atomic Emit
acceptance, encrypted registration/version binding, leases, replay authority and
SDK/UI remain implementation work, not accepted capability or deferrals.
