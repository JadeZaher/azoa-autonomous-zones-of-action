# Forwarded-header trust

Forwarded client scheme/IP affects HTTPS behavior, audit context, and anonymous
rate-limit partitions. The default is disabled for directly reachable/self-hosted
nodes. A deployment may either enumerate trusted proxy IPs/networks or explicitly
enable trust-all together with `EdgeOnlyDeploymentAcknowledged=true`; the latter
is reserved for a platform edge that makes the application port unreachable.

Never clear the framework trust lists unconditionally. A forged
`X-Forwarded-For` from a direct client would otherwise rotate rate-limit keys.

## Rate-limit identity

Rate-limit partitions use only authenticated server-issued identity claims.
An ordinary authenticated API key partitions by `ApiKeyId`; a key carrying
`tenant:provision` partitions by its authenticated tenant subject so key
rotation cannot evade the tenant aggregate ceiling. An authenticated user falls
back to its subject/avatar id; anonymous requests partition by the trusted
remote IP. Never partition directly from `X-Api-Key`: anonymous endpoints would
let callers rotate invalid header values to evade the IP quota.

Tenant custodial routes add a second, endpoint policy keyed by the authenticated
tenant subject plus a SHA-256 digest of the canonical external subject. The
ordinary global API-key partition still applies as the tenant aggregate ceiling.
This two-level composition prevents one external subject from consuming the
entire tenant budget without allowing subjects to evade the aggregate limit.
