# Node operator console

## Trust boundary

The operator console is deliberately separate from the ordinary Azoa account
dashboard. `/operator/login` exchanges `NodeOperator__Username` and
`NodeOperator__Password` with the API through a same-origin Next route. The
dedicated `token_use=node_operator` JWT is stored only in an HttpOnly,
SameSite=Strict cookie scoped to `/api/operator`; it is never copied into the
Azoa SDK or browser storage.

Every console request goes through the explicit allowlist in
`app/api/operator/[...path]/route.ts`. Mutations require a same-origin `Origin`
and `x-azoa-operator-request: 1`. Request bodies are rebuilt from permitted
fields, upstream response bodies are size-bounded, and backend errors are
reduced to safe operator copy. The API's dedicated operator policy remains the
authorization authority; the shell verifies access through `GET
/api/operator/session` before rendering.

Normal session end clears only the current browser's HttpOnly cookie. Global
revocation is a separate, explicitly confirmed `POST session/revoke` action for
incidents and handoffs; it invalidates every operator session before clearing
the local cookie. Operator sessions are capped by the API at 30 minutes.

## Configuration boundary

The UI may edit only secret-free provider policy: display name, enabled state,
policy version, and assurance level. Adapter identity is immutable in this UI.
The shared operator contract caps display names at 80 characters in both the
form and BFF, matching the backend manager boundary.
Provider API keys, webhook secrets, operator credentials, and deployment
secrets remain in Railway or another host secret store. The console may report
`requiredConfigurationKeys`, `missingConfigurationKeys`, configured booleans,
profile version, and `trustRevision`, but never reads a secret back.

Provider readiness is green/selectable only for the exact `READY` code plus
enabled and available capability. Other codes remain distinct: `DISABLED`,
`PROFILE_NOT_CONFIGURED`, `ADAPTER_UNAVAILABLE`, `SECRETS_NOT_CONFIGURED`,
`POLICY_INVALID`, and `SELECTION_REQUIRED`.

## KYC decisions

Tenant self-selection lives in the ordinary authenticated `/kyc` route and
derives tenant ownership from the login token; the browser never supplies a
tenant id. It uses only the API-filtered ready-provider catalog and optimistic
selection revision.

The operator review queue uses minimized records and never displays provider
payloads, identity documents, document URLs, or session identifiers. External
provider outcomes cannot be manually overridden. Any Development-only manual
simulation must be explicitly authorized by the API capability returned for
that queue item; adapter-name guessing is not an authorization signal.

Provider or tenant policy changes explicitly warn that attempts and approvals
under the old provenance become stale and require re-verification.

Sensitive operator and tenant policy mutations require a login no older than
ten minutes. The API returns a stable recent-login signal. The operator BFF
clears its scoped cookie and returns through `/operator/login`; tenant
self-service offers `/login`. Both validate an internal return path so the
person resumes the page where the action began.

## Audit history

`/operator/audit` is a read-only view of KYC control-plane changes. It shows
minimized before/after policy fields, tenant/provider references, the
token-derived actor reference, revision, and timestamp. It must never infer or
display provider credentials, verification payloads, document references,
identity evidence, or session material.

The BFF accepts only `limit`, opaque base64url `cursor`, canonical `tenantId`,
canonical lowercase `providerKey`, and the three stable action filters. Unknown,
duplicate, oversized, or malformed query values fail at the same-origin
boundary. Cursors are round-tripped unchanged; the browser treats them only as
opaque pagination tokens.

## Interaction rules

- Operator and tenant actions use at least 44px touch targets.
- Consequential changes require an impact confirmation tied to the exact draft.
- Rejection requires a reason; the reviewer identity is token-derived.
- Conflicts refresh canonical state and explain why the draft was reset.
- Monitoring states when data was generated and never implies a live feed.
