# KYC service policy

`KycApprovalTrust` binds an attempt to the active provider key, operator policy
version, and assurance level in a strict versioned JSON envelope stored in the
existing `KycSubmission.ProviderResult` field. Reusing that field keeps this
hardening schema-compatible; an eventual live adapter must preserve the envelope
and keep vendor evidence outside it or introduce a reviewed versioned envelope.

Missing/malformed envelopes and changed trust profiles are intentionally stale.
`MANUAL` additionally requires an explicit Development-only opt-in, simulated
blockchain mode, and disabled real-value bridging. It is never accepted by a
value-operation gate, even if an operator accidentally allow-lists its key.
Both value gates require an explicit future expiry.

Tenant-driven attempts always carry an explicit tenant id plus the tenant
selection version and provider trust revision. Begin, submit, status, operator
review, and every value gate resolve that exact tenant authority; direct-user
flows use the node-default authority and never fall back from a tenant lookup.
The persistence transaction writes deterministic tenant/provider/attempt guard
records before creating an attempt. Those shared write-conflict records make a
concurrent provider change, tenant selection change, or second Begin lose
instead of leaving a stale or duplicate active row.

Manual review is an explicitly labelled non-authoritative simulation. Approval
still requires at least one validated, persisted HTTPS document reference and
is available only when the host is in Development, `Blockchain:Mode=Simulated`, and
`Blockchain:Bridge:RealValueEnabled=false`; responses expose
`developmentSimulation=true`. Production, live-chain, real-value, and
external-provider outcomes remain fail-closed. The Veriff and generic-hosted
implementations are configuration-safe scaffolds, not production adapters,
until signed provider outcome handling is implemented.

## Value-access decision

`IValueAccessService` is the single server-side participant-readiness decision
for value-bearing consumer actions. It delegates to `IKycGateService`, which in
turn resolves the current provider through `IKycProviderService` and the
provider registry. This keeps consumer policy out of AZOA while ensuring a
manual, unavailable, stale, or untrusted provider approval never opens a value
path. Projects and non-value collaboration must not use this gate.

`KycRuntimeSafety.GuardStartup` rejects configured manual/mock KYC or an admin
override when Production or Mainnet is selected. The admin override key is not
an implementation seam: it remains unsupported until an audited, durable
override ledger and reviewer workflow are introduced.

Configuration mutations append non-secret before/after evidence to
`kyc_control_audit`. The operator-only `GET /api/operator/kyc/audit` surface is
bounded and optionally filters by tenant, built-in provider key, or one of the
three stable configuration action values. Its opaque unpadded-base64url cursor
anchors the exclusive descending `(occurred_at,id)` position; newer audit rows
inserted between requests cannot shift, duplicate, or skip the remaining page.
Secret configuration is never accepted by or projected through that timeline.
