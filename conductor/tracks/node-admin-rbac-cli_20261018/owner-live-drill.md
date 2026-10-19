---
type: runbook
status: prepared_not_executed
---

# Owner-operated live drill

Run this only after the coordinator records the pushed main revision and successful
API, schema and frontend deployments for that exact revision. The expected
unconfigured node-admin challenge currently returns503 NODE_ADMIN_UNAVAILABLE.
This drill has not run. It is separate from local synthetic UI research and the
open integration, vendor and settlement release conditions.

Use an owner-controlled interactive terminal, outside agent-captured command
output. Keep private keys, passphrases, enrollment tokens, authenticator secrets
and access tokens in that terminal. The agent needs only the final status,
public fingerprint and deployment revision to record completion.

## CLI and root identity

From the reviewed main checkout's `tools/azoa-node-cli` directory, use its built
`dist/cli.js`. The release verification covers the CLI typecheck, build and tests;
if installing it on another machine, use `npm ci` and `npm run build` there.

If you already have a root key, use its existing local key name below. Generate
a new key only when needed; do not overwrite an existing key:

```powershell
node dist/cli.js keygen --name release-root --kind root
node dist/cli.js profile add release-root --origin https://azoa-api-production.up.railway.app --kind root --key-name release-root --use
```

Enter your own mandatory passphrase at the prompt. Record the public fingerprint
privately for comparison; do not send the private key or passphrase to the agent.

## Public root configuration and deployment

The Railway target is project `a876ca12-bbf8-4c84-bce7-2c4268f3296c`, environment
`3443bd75-e5e6-4bf9-9795-a7f2ae809b14`, API service
`ec91fda3-0bcb-48b5-8a5f-cb2442192d22`.

```powershell
node dist/cli.js root env --profile-keys release-root --railway --project a876ca12-bbf8-4c84-bce7-2c4268f3296c --environment 3443bd75-e5e6-4bf9-9795-a7f2ae809b14 --service ec91fda3-0bcb-48b5-8a5f-cb2442192d22
```

This first command is a dry run. Review its public-key/revision diff, then apply
the same reviewed configuration explicitly:

```powershell
node dist/cli.js root env --profile-keys release-root --railway --project a876ca12-bbf8-4c84-bce7-2c4268f3296c --environment 3443bd75-e5e6-4bf9-9795-a7f2ae809b14 --service ec91fda3-0bcb-48b5-8a5f-cb2442192d22 --yes
```

Applying variables can itself trigger a deployment. On the same API service, set
`NodeAdmin__Audience=https://azoa-api-production.up.railway.app`. Redeploy the API
so both public root configuration and audience take effect, then wait for a
healthy deployment of the recorded main revision. Do not lower the root revision
or replace other trusted public keys inadvertently. The coordinator can verify
deployment status using Railway without receiving the private key.

## Bootstrap and independent administrator enrollment

```powershell
node dist/cli.js login --root --profile release-root
node dist/cli.js whoami --profile release-root
node dist/cli.js admins create --username release-owner --display-name "Release owner" --role node-owner --profile release-root
```

Choose the actual administrator username/display name before the create command.
The resulting enrollment token is shown once. Deliver it privately to the
administrator. On the administrator's own machine, with its own CLI installation:

```powershell
node dist/cli.js enroll --origin https://azoa-api-production.up.railway.app --profile-name release-owner
```

The administrator enters the token at the prompt, creates their own key and
enrolls their authenticator. Confirm their public fingerprint through an
independent channel. The root owner then activates the exact returned admin ID:

```powershell
node dist/cli.js admins activate <admin-id> --fingerprint <confirmed-public-fingerprint> --profile release-root
```

On the administrator's machine:

```powershell
node dist/cli.js login --profile release-owner
node dist/cli.js whoami --profile release-owner
node dist/cli.js audit verify --profile release-owner
```

Success requires active administrator identity with the expected role and a
verified audit chain. Root-created owners remain explicitly audit flagged.
All node-admin writes require a recent login; use the CLI's normal step-up flow
if prompted. Capture only non-secret outcomes in the release record. If a step
fails or its response is ambiguous, inspect the actual enrollment/admin state
before issuing a new enrollment or generating replacement keys.
