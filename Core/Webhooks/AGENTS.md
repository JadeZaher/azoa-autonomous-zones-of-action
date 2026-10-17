# Webhook transport and authentication primitives

Webhook destinations require HTTPS, no URL credentials or fragment, and a maximum
2048-character URI. Preflight rejects any nonpublic DNS answer. The connect
callback independently resolves and validates every candidate, then connects to
those exact addresses, preserving ordinary TLS hostname verification. Proxies,
cookies, default credentials and redirects are disabled. Connect timeout is ten
seconds and response headers are limited to 32 KiB. Both delivery workers use
headers-only completion, dispose responses without reading bodies, and refuse
outbound bodies above one MiB before signing or HTTP dispatch.

IPv6 permits public unicast in 2000::/3 except IETF special-purpose 2001::/23,
documentation 2001:db8::/32 and 3fff::/20, and 6to4 2002::/16. This deliberately
excludes special-purpose protocol services even where some suballocations are
globally reachable. Scoped addresses, multicast and all other reserved ranges
are refused. IPv4-mapped addresses and the well-known NAT64 /96 are classified
using the embedded IPv4 address; network-specific translation prefixes are refused.
Source: https://www.iana.org/assignments/iana-ipv6-special-registry/iana-ipv6-special-registry.xhtml
(captured in .omc/research/webhook-ipv6-registry-source.json).

DNS socket failures are validation failures. Unexpected implementation errors
propagate. Connection fallback handles socket failures only; cancellation and
other errors dispose the current socket and propagate to the worker boundary.
The URL guard alone does not authorize a delivery: durable registration/version,
secret binding and attempt leases belong to the delivery domain.
