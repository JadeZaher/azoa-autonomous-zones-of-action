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
back to its subject/user id; anonymous requests partition by the trusted
remote IP. Never partition directly from `X-Api-Key`: anonymous endpoints would
let callers rotate invalid header values to evade the IP quota.

Tenant custodial routes add a second, endpoint policy keyed by the authenticated
tenant subject plus a SHA-256 digest of the canonical external subject. The
ordinary global API-key partition still applies as the tenant aggregate ceiling.
This two-level composition prevents one external subject from consuming the
entire tenant budget without allowing subjects to evade the aggregate limit.

## operator-login partition (node-admin-rbac-cli NEW-1)

`RateLimitPartitionKey.Address` keys the `operator-login` limiter by full IPv4 address (IPv4-mapped IPv6
unwrapped) and by the **/64** prefix for IPv6. A single subscriber normally owns a whole /64, so per-address
IPv6 buckets let one attacker rotate through 2^64 fresh buckets. The policy is shared by the legacy
`POST /api/operator/session` and the node-admin `auth/challenge`, `auth/session`, `enroll/*` routes; for both,
coarser IPv6 buckets only make throttling more conservative (a shared /64 shares 20 attempts per 15 min). The
global and financial limiters keep their identity/IP partitioning unchanged.
