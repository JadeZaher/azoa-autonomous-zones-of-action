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
An authenticated API key partitions by `ApiKeyId`; an authenticated user falls
back to its subject/avatar id; anonymous requests partition by the trusted
remote IP. Never partition directly from `X-Api-Key`: anonymous endpoints would
let callers rotate invalid header values to evade the IP quota.
