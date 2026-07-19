# Services/Admin — durable node authority

## Purpose

Every self-hosted Azoa node has one reserved operator identity for configuring
the node, monitoring readiness, assigning tenant KYC policy, and making the
small set of decisions that must remain human. It is deliberately separate
from ordinary avatar login, tenant API keys, and the legacy admin-claim path.

The reserved identity is fixed at avatar id
`a20a0000-0000-4000-8000-000000000001` and email
`node-operator@azoa.invalid`. A node launch supplies these settings from its
secret store:

- `NodeOperator__Username`: 3–64 canonical lowercase characters.
- `NodeOperator__Password`: a generated 24–72 byte secret with no whitespace.
- `NodeOperator__CredentialRevision`: a positive monotonic integer.
- `NodeOperator__SessionMinutes`: 5–30 minutes; 20 is the default.

Production refuses the first launch without a complete seed. Development may
start without it, but the operator console will remain unavailable.

## Seed and rotation contract

`SeedAdminHostedService` reads `admin_bootstrap_state:local` before changing
anything. On the first complete seed it creates the reserved avatar if absent,
hashes the password with bcrypt, and binds the fixed avatar id once. The raw
password is never persisted or returned.

Subsequent launches behave as follows:

- no seed plus a valid durable binding: keep the existing operator;
- the same revision plus the same normalized username and password: no-op;
- the same revision with different credentials: refuse startup;
- a lower revision: refuse rollback;
- a higher revision: atomically rotate username/password and advance the
  durable revision.

Increment `NodeOperator__CredentialRevision` whenever either credential
changes. That revision is also carried in operator-session tokens, making a
credential rotation an explicit trust-boundary change.

## Authentication boundary

`POST /api/operator/session` is the only operator login. It verifies the fixed
binding and issues a short-lived JWT with:

- `token_use=node_operator`;
- `scope=operator:admin` and `scope=node:govern`;
- `operator_revision`, `operator_session_revision`, `auth_time`, and a unique `jti`;
- the reserved avatar id as `sub`.

Operator tokens are not ordinary avatar tokens and must never be accepted as
tenant API keys. The reference frontend keeps the token in a SameSite=Strict,
HttpOnly cookie scoped to its operator BFF. Normal sign-out clears only the
current browser cookie. `POST /api/operator/session/revoke` is the separate,
recent-auth global incident action: it atomically advances the durable session
revision and invalidates every current operator session.

## Login throttling

`NodeOperatorLoginThrottle` bounds attempts by client address and normalized
username. Invalid and missing identities still pay bcrypt work and return the
same credential error; do not add an identity-existence oracle or detailed
login failures. A bounded operator-login window returns the stable
`NODE_OPERATOR_LOGIN_THROTTLED` code with HTTP 429 and an exact `Retry-After`;
the address and address/username limits share `RateLimiting:OperatorLogin`
configuration.

## Legacy compatibility

`AdminBootstrap__SeedEmail` and `AdminBootstrap__SeedSecret` remain only to
detect a partially configured legacy deployment. They are not the setup path
for new nodes and do not replace the durable reserved operator. Production
still refuses a half-configured legacy pair so old deployment mistakes remain
loud.

## Operator procedure

The executable deployment, first-login, rotation, and recovery procedure lives
in `docs/NODE-HOST.md` §8.9 and `RUNBOOK.md`. Provider credentials remain in the
host secret store; the console may report required/missing variable names but
must never read back their values.
