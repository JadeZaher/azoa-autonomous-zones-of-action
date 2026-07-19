# Tenant custodial account and KYC contract

Azoa owns the avatar, encrypted platform wallet, KYC submission, and KYC
document-reference ledger. A tenant such as ArdaNova keeps only the returned
public ids/address and readiness status. It never receives signing keys, seed
phrases, encrypted custody material, KYC document references, provider payloads,
or reviewer data.

## Authentication and isolation

Use an Azoa tenant API key whose subject is the tenant avatar id and whose scopes
include `tenant:provision`, `wallet:manage`, `kyc:read`, and `kyc:submit`. The API
never accepts a tenant id in a route or body. Every lookup is scoped to the
authenticated tenant plus its opaque `externalSubject`; another tenant receives
the same not-found result as a missing subject.

### Bootstrap the tenant key

An ordinary avatar cannot self-issue `tenant:provision`. A JWT-authenticated
Azoa operator creates the first key for an existing tenant avatar:

```bash
curl -X POST "$AZOA_URL/api/apikey/tenant" \
  -H "Authorization: Bearer $AZOA_OPERATOR_JWT" \
  -H "Content-Type: application/json" \
  -d '{"tenantAvatarId":"00000000-0000-0000-0000-000000000000","name":"ArdaNova local","expiresInDays":90}'
```

The target id is loaded before creation. The endpoint is `Operator`-policy
only, so API keys cannot call it. It stamps exactly
`tenant:provision,wallet:manage,kyc:read,kyc:submit`, accepts no scope/origin
override, expires in 1–365 days (90 default), disables response caching, and
returns the raw key once. Put that raw value in the tenant secret store; Azoa
persists only its hash/prefix.

## Account lifecycle

- `GET /api/tenant/custodial-accounts/capabilities` reports custody, chain, and
  KYC provider availability.
- `PUT /api/tenant/custodial-accounts/{externalSubject}` requires a stable
  `Idempotency-Key` header and ensures one deterministic avatar plus one
  deterministic platform wallet on the configured chain.
- `GET /api/tenant/custodial-accounts/{externalSubject}` reports the same
  secret-free state without creating anything.

Creation is create-only. User claim may clear the legacy `OwnerTenantId`
correlation, but a retry resolves the immutable tenant-partitioned avatar id and
never rewrites the claimed account. The raw idempotency key is SHA-256 hashed in
a tenant-scoped ledger namespace. Reusing one key for another external subject
is rejected; a completed replay refreshes current KYC/readiness state without a
second identity create. Replay also reruns create-only wallet convergence, so an
identity-only completion can add its missing wallet later under the same key
when custody becomes available.

The response carries `tenantId`, canonical `externalSubject`, compatibility
alias `ardanovaUserId`, optional public
`avatarId`/`walletId`/`walletAddress`, `kycStatus`, `walletReady`, `ready`, and an
optional `unavailableReason`. `ready` means the runtime capabilities are healthy,
the platform wallet has protected custody material, and Azoa's latest KYC status
is `Approved`.

Readiness is intentionally decomposed. `identityReady` means the deterministic
avatar exists; `kycReady` means the selected provider can accept verification;
`walletReady` means the platform wallet is present and usable under the current
custody mode. Capabilities additionally expose `walletProvisioningReady`.
Aggregate `ready` requires all three plus approved KYC. If production KMS/HSM
custody is not installed, ensure still creates the identity and KYC may proceed;
only wallet creation remains skipped/fail-closed.

## KYC lifecycle

- `POST .../{externalSubject}/kyc/session` requires `Idempotency-Key` (16-200
  non-whitespace characters). The key is stable forever for the authenticated
  tenant/external-subject pair and means **ensure an active attempt**. It returns
  either hosted redirect metadata or manual document-reference instructions.
- `POST .../{externalSubject}/kyc/submissions` accepts `{ documents: [...] }`.
  Each document contains `type`, an absolute credential-free HTTPS
  `referenceUrl`, `fileName`, and optional `mimeType`/`fileSizeBytes`.

The tenant response returns only submission id, four-state status, and dates.
Manual approval/rejection remains exclusively on Azoa's JWT-only `Operator`
endpoints. A tenant can never approve its own submission.

`Kyc:SubmissionExpiryDays` controls expiry and is clamped to 1-365 days.
Approvals require an explicit future expiry. Expired, indefinite, retired-policy,
and retired-provider approvals fail the KYC gate and surface as `Rejected` in
the four-state tenant projection. Submission and document-reference rows are
written in one SurrealDB transaction.

Azoa owns attempt sequencing. A retry with the same session key returns the
same unexpired attempt metadata. Once the attempt is rejected or expires, the
same key converges on the next attempt; ArdaNova stores neither an attempt
counter nor a provider session id. A live approval blocks redundant enrollment.
The begin response contains `provider`, flow flags, optional
`verificationUrl`/`expiresAt`, and instructions; never `providerSessionId`.
Begin persists the empty attempt; the first validated submission atomically
attaches its documents, and operator approval is impossible before attachment.

## Provider selection and maturity

| `Kyc:Provider` | Flow | Availability |
|---|---|---|
| `manual` | Validated HTTPS document references plus Azoa operator review | Development only; production-disabled until private upload/review lifecycle exists |
| `veriff` | Vendor-hosted verification | Fail-closed adapter stub; not production-ready |
| `hosted` or `generic-hosted` | Operator-configurable hosted-provider seam | Fail-closed scaffold; not production-ready |
| any other value | None | Unknown-provider service, unavailable by design |

Generic hosted scaffold keys are `Kyc:Hosted:ProviderName`,
`Kyc:Hosted:BaseUrl`, `Kyc:Hosted:ApiKey`, `Kyc:Hosted:WebhookSecret`,
`Kyc:Hosted:SessionPath`, and `Kyc:Hosted:StatusPath`. Environment-variable
forms replace `:` with `__`, for example `Kyc__Hosted__ApiKey`. `BaseUrl` must
be a credential-free non-loopback HTTPS origin, paths must be relative, and
`StatusPath` must contain `{sessionId}`. Secrets belong only in the deploy
secret store.

Every node must also configure a trust profile. Defaults are intentionally
empty, so an available adapter alone cannot authorize value:

| Key | Meaning |
|---|---|
| `Kyc:ApprovalPolicy:PolicyVersion` | Operator-controlled version. Changing it invalidates older attempts and approvals. |
| `Kyc:ApprovalPolicy:AssuranceLevel` | Provider-neutral assurance label the active adapter is reviewed to meet. Exact-match by design. |
| `Kyc:ApprovalPolicy:TrustedProviderKeys` | Allow-list containing the active provider key (`veriff`, or the normalized hosted `ProviderName`). |
| `Kyc:ApprovalPolicy:AllowManualInDevelopment` | Explicit simulator opt-in; `MANUAL` additionally requires Development, `Blockchain:Mode=Simulated`, and real-value bridging disabled. It never satisfies a value-operation KYC gate. |

Environment-variable arrays use numeric segments, for example
`Kyc__ApprovalPolicy__TrustedProviderKeys__0=veriff`. A versioned provenance
envelope is stamped server-side when the attempt begins. Document attachment,
operator approval, tenant readiness, and external-authority KYC gates all
require an exact match with the current provider, policy version, and assurance
level. Manual simulation approvals remain visible as simulation state but never
unlock value operations. Missing legacy provenance fails closed and requires a new attempt.
`KYC_PROVIDER_CHANGED` or `KYC_POLICY_CHANGED` on document submission means the
client must call the session endpoint first; documents are never revalidated
against a different provider on an old attempt.

Even with every hosted key supplied, capabilities remain unavailable. A hosted
adapter is not safe to enable until it implements one durable idempotent attempt,
persists the session before returning a redirect, verifies signed webhook
headers over the raw request body, deduplicates provider event ids, maps the
event to the attempt/avatar, and CAS-updates terminal status. Likewise the
Veriff adapter is currently a contract stub, not a working integration.

## Deployment gate

Set `CustodialAccounts:Enabled=true` and choose a supported wallet chain. The
only current bootstrap custody mode is
`CustodialAccounts:CustodyMode=DevelopmentOnly`; it is accepted exclusively
when the host environment is `Development`, `Blockchain:Mode=Simulated`, and a
32+ character `AZOA:WalletEncryptionKey` exists. It creates an encrypted
development key but deliberately stores no recovery seed phrase.

Base/production configuration is `CustodyMode=Disabled`. Setting
`CustodyMode=KmsHsm` also reports unavailable today because no production
KMS/HSM adapter is registered. A live or non-development runtime therefore
cannot report tenant custody ready. Implement and review a production custody
adapter before enabling real-value wallet onboarding; do not reinterpret the
config KEK as KMS custody. Identity provisioning and available KYC providers are
independent of this wallet gate.

Base/production KYC configuration is `Kyc:Provider=unavailable` with bounded
30-day submission expiry. `Kyc:Provider=manual` is honored only in Development;
an explicit production override still resolves to unavailable. Development
sessions expire after 30 minutes by default. Production enablement requires an
Azoa-owned private upload lifecycle, content scanning, retention/deletion
policy, and reviewed operator workflow; arbitrary external HTTPS references are
not accepted as production KYC evidence.

Tenant custodial routes are throttled twice: a per-external-subject policy
prevents one user from consuming every request, while a global tenant partition
is the aggregate ceiling across key rotation. External subjects are hashed
before they become limiter keys.

## Baseline checkpoint and next track

This contract is the stopping-point baseline for the generic multi-tenant
operator, KYC, and custodial-account work. It does not claim production custody,
live provider verification, or real-value bridge readiness; those remain
fail-closed as described above.

The next named track is **Arda Nova financial workflow conformance**:

- define a versioned Arda Nova↔Azoa contract matrix for account bootstrap, KYC,
  wallet readiness, idempotency, and financial-operation outcomes;
- exercise that matrix through local and CI end-to-end integration coverage;
- expose a clean SDK/API dependency seam that future agents and partner
  applications can consume without reaching into Azoa internals or sharing a
  worktree.
