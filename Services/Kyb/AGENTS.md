# Tenant business verification

The business subject is the tenant organization. The tenant ID is always the authenticated
owner user, never a request body field. First-party login owners and scoped tenant API keys
can use the API; API keys additionally require `tenant:provision` and `kyc:read`/`kyc:submit`.
Every service read, begin and outcome application rechecks an active owner and at least one
current provisioning credential through shared `TenantAuthority`. A delegated child cannot
use the owner surface. The node operator is not an organization applicant.

`kyb_verification` is one current authority per tenant. New attempts replace this current
projection by version CAS; historical hosted sessions retain their admission, provider ID,
terminal verdict and timestamps. A pending attempt is resumed even if a browser loses its
request key. A terminal attempt requires a new request key to begin a fresh review. Expiry
or a policy/configuration revision change requires a new attempt. No business approval is
promoted into personal KYC or AML approval, and existing value gates remain authoritative.

The authority fingerprint covers the operator policy version, business workflow ConfigId,
provider host and company credential. Operators must change PolicyVersion when editing
the vendor workflow in place. A callback from an older attempt cannot decide the current
attempt. Completion atomically fences the current hosted resumption lease and CAS-updates
the pending business record; a stale worker cannot commit after lease takeover.

The hosted flow owns external-create admission and callback resumption. KYB uses the modern
`/begin` with its own saved workflow and `/result` with camelCase companyKey/sessionId.
The parent business ID stays server-side. A callback is only a trigger; its own verdict is
ignored. Authenticated refresh calls the same authoritative processing path and verifies
tenant plus admission ownership before passing the hosted session to the shared flow.

The parser requires one approved company step with name/registration/country, at least one
approved beneficiaries step with linked verification sessions, and one approved kyb-review.
Unknown/missing states remain pending. An explicit rejected final review is terminal denial.
The documentation uses singular `sessionId` in prose and plural `sessionIds` in its example:
both are supported; conflicting or malformed dual representations are rejected. Optional
positions and vendor internal IDs are not required. Missing internal IDs use a stable hash
of the linked session IDs. Beneficiary names, birth dates, contact data, documents and session
URLs have no persistence fields; public status exposes only a participant count. Server-only
business evidence retains minimal registration identifiers and beneficiary session linkage.

## KYB gate (D-6)

Owner decision D-6 (round 4): KYB gates three tenant capabilities. `IKybGate`
(`KybGate.cs`) is the single check — the tenant's `kyb_verification` row must be `APPROVED`,
carry business evidence, be unexpired and match the node's current `AuthorityRevision` (a
config/policy change re-verifies every tenant). Approval already requires at least one
beneficiary, so the row is the proof. Refusal is `KYB_REQUIRED` with a generic message; store
faults bubble to the HTTP boundary rather than being caught (fail closed either way).

| Capability | Enforced at |
|---|---|
| `TenantOnBehalfValue` | `Services/Consent/TenantConsentGate.cs`, after the grant check, for every tenant-driven decision — so every `KeyCustodyService` decrypt and the controller consent probes (fiat checkout/payout, exchange, allocation). Denial is audited `TenantSignDenied`. |
| `CustodialAccess` | `Managers/TenantCustodialAccountManager.cs` `EnsureAsync`, `BeginKycAsync`, `SubmitKycAsync` (403 `TENANT_FORBIDDEN: KYB_REQUIRED`). |
| `PayoutPriceSchedule` | Contract published for the money lane (L3): schedule registration and tenant-priced payout admission. |

The check is a typed read before the operation, not an in-batch fence: a KYB row changes only
when the tenant starts a new verification, and the custody/consent fences that follow already
linearize the operation itself. Test fixtures seed an approved row with
`TestDataBuilders.KybApprovedTenant(tenantId).SeedAsync(services)`.

## §renewal — early renewals keep the current approval (fix batch, RV-L2 L2-7)

`kyb_verification` is one row per tenant. An APPROVED tenant that begins again with a new
Idempotency-Key (renewing early) used to overwrite the row with PENDING, and the gate denied all
three D-6 capabilities at once — every consent-gated custody decrypt and tenant payout stopped
until the new review finished. `KybService.CarryEffectiveApproval` now copies a still-effective
approval (unexpired, current authority revision, with evidence) into
`effective_approval_{expires_at,evidence_json,authority_revision}` on the replacement attempt
(also across an orphan supersede of that renewal). `KybGate` allows a PENDING row whose carried
approval is unexpired and on the current authority revision. The renewal's own decision clears
the carried fields in the same CAS (`TryDecideAsync`): an approval replaces it, a rejection ends
it. A renewal that is abandoned leaves the carried approval effective until its original expiry.
The single-row design was kept on purpose: a second attempt table would duplicate the admission,
supersede and decide CAS paths for a capability that only needs "previous approval still counts".

## Operator setup

1. In Identomat Manage, create separate saved KYC and KYB configurations. KYB must collect
   company registration details, beneficiaries/representatives, their KYC or nested KYB
   session links, and a final KYB review. Do not substitute an arbitrary KYC configuration.
2. Set `Kyc__Identomat__ConfigId` and `Kyb__ConfigId` to those IDs. `Kyb__PolicyVersion`
   identifies the reviewed policy; `Kyb__ReturnUrl` is an optional public HTTPS return page.
3. In Identomat **Company settings**, set the callback URL to the node's public HTTPS
   `/api/kyc/callback/identomat`. Set the same secret as `Kyc__Identomat__CallbackApiKey`
   (at least 32 characters). The shared company ingress checks both flow-specific session
   correlations. KYB intentionally consumes the company callback key, not a second key.
   `/api/kyb/identomat/callback` is also available for installations using a dedicated company.
4. Supply `Kyc__Identomat__CompanyKey` using host-managed secrets. Development alone may
   read the ignored root `.env` alias `IDENTOMAT_API_KEY` for this documented companyKey.
   Its name alone does not establish successful vendor authentication.
5. After vendor setup, verify a real non-production business workflow, authoritative result,
   callback redelivery, expired/rejected cases, and tenant isolation. Until then automated
   HTTP fixture results prove adapter behavior, not vendor account readiness.

The documented `get-session-config` endpoint needs an already-known sessionConfigId.
No configuration-list discovery endpoint was found in the official API reference. The
dashboard setup remains an external prerequisite, explicitly accepted by the user while
mock HTTP workflows exercise the real implementation.

Official sources verified 2026-10-15: https://docs.identomat.com/developer-tools/api-reference,
https://docs.identomat.com/developer-tools/developer-guide/kyb-know-your-business,
https://docs.identomat.com/developer-tools/callbacks. Captures are under `.omc/research/identomat-*`.
