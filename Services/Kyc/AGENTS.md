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

**Both value gates require TWO independent facts.** `KycGateService` (consumer) and
`Services/Bridge/RealValueKycGate` (bridge / real value) each read a current
policy-bound approval AND the latest AML screening verdict for the same
`(user, tenant)` pair. Identity verification says who a person is; it says nothing
about whether that person is sanctioned. Only `NO_HIT` on a non-truncated result
opens a gate — `HIT` denies, `MANUAL_REVIEW` denies, a TRUNCATED hit list denies, a
store error denies, and a MISSING result denies. Fail-closed on missing is the load
bearing case: an approval with no screening row is an unanswered question, not a
clean subject, and the whole screening surface is decorative without it.

The refusal is the same sentence every other refusal uses, and `GetKycStatusAsync`
still reports `APPROVED` for an AML-denied subject. That is deliberate: identity
genuinely was approved, and distinguishing an AML denial at the consumer boundary is
tipping off. The operator-side logs carry the discriminating detail.

`KycRuntimeSafety.GuardStartup` rejects configured manual/mock KYC or an admin
override when Production or Mainnet is selected. The admin override key is not
an implementation seam: it remains unsupported until an audited, durable
override ledger and reviewer workflow are introduced.

## §identity-binding

`KycIdentityBindingService` records which real person a verified user is, without
retaining the document that proved it. `KycPersonKeyDeriver` is the only place the
recipe exists; see `Persistence/SurrealDb/Models/AGENTS.md` §kyc-identity-binding
for the storage shape and the D23/D25 decisions.

Both derivations fail closed. A missing or short root secret, an absent document
number, or an absent issuing state all return null and refuse the binding. A
partially-keyed identity is worse than no identity: it would collide people.

**The Sybil refusal lives at the WRITE.** `BindUserAsync` appends the caller to
`user_ids` CONDITIONALLY — only when the binding is empty or already contains it —
and `BindAsync` reads the result back to confirm. An unconditional
`array::concat` adds the caller to every existing binding, after which
"this person is already bound to a different user" is unobservable by construction
and the D3/D23 check is a tautology. The condition is expressed inside the statement,
not as a preceding read, so two concurrent binds for one person key cannot both win.
The refusal is per uniqueness SCOPE: a second tenant verifying the same human is
legitimate, and refusing it would itself disclose that the human exists elsewhere.

`EvaluateEligibilityAsync` is the per-operation opt-in surface for asking the same
question ahead of a person-scoped benefit. **No production operation opts in yet** —
it is reachable only from tests. That is a gap, not a design: state it plainly rather
than reading its existence as coverage.

**Every consumer-visible denial is the same sentence.** `KycEligibilityDecision`
carries a single `Message` for all refusals and keeps the discriminating
`Cause` `[JsonIgnore]`d. "This person already claimed via another user" would tell
a tenant that its subject exists elsewhere on the node, which is precisely the
cross-tenant fact the per-tenant salting exists to hide. The operator-side cause
survives for support and audit; it must never reach a response body.

## §aml-screening

Folded into the Identomat adapter (D24), not deferred. Four constraints shape the
design and none of them is a detail:

**Monitoring has no push channel at all.** The vendor's callback type list contains
six values and none is screening-related — this is not "an untrusted webhook", it is
the absence of any signal. `KycScreeningMonitorPollWorker` is therefore a cold poll
against `list-screening-monitoring-records` with a `date` high-water mark. Because
that endpoint returns only `fullName`/`id`/`lastUpdatedDate`, a record whose
timestamp advanced forces a follow-up `search-screening-person` to learn what
actually changed.

**The poll is batched because the budget is shared.** One paginated list call per
cycle covers every monitored identity. A per-identity loop would collide with the
600 req/min per-IP ceiling that KYC session traffic is already drawing on, and
exceeding it bans the node for an hour — the poller would take the node's own KYC
offline to check on sanctions lists. Page count per cycle is bounded, a mid-cycle
budget refusal stops the cycle cleanly, and a failed cycle parks on backoff.

**The risk decision is ours.** There is no APPROVED/REJECTED enum on the screening
surface. `KycScreeningRiskPolicy` derives hit / manual-review / no-hit from `score`
plus `topics`. `topics` is an OPEN string set matched case-insensitively, never a
closed C# enum: the vendor publishes no exhaustive list, so an enum would reject
legitimate future values. A blocking topic blocks at any returned score, because the
score measures identity-match confidence, not listing severity.

**`already-exists` is success, but it is NOT another tenant's row.**
`monitor-screening-query` keys on the query SHAPE (`fullName` + `birthdayTimeFrom`),
not a person id, and returns `argumentError: "already-exists"` on re-registration.
That is the vendor's own dedup guard confirming the monitor is live; treating it as a
failure would make an ordinary retry look like a broken integration. The exact
registered query fields are persisted because they, not a person id, are the
monitor's key. `scoreThreshold` is persisted alongside every result for the same
reason: the vendor applies it server-side and never echoes it per hit, so without it
a stored empty result set cannot be distinguished from "the threshold was too high".

One company key serves the WHOLE NODE, so the vendor dedups a query shape across
every tenant: tenant B registering a person tenant A already monitors gets
`already-exists` while holding no row of its own. Handing back A's row would hand
over A's `subject_user_id` and A's `tenant_id` — a cross-tenant leak through a
shared key, which this repo has now shipped three times. So:
`kyc_screening_monitor` is unique on `(tenant_scope, query_full_name,
query_birthday_time_from)` mirroring `kyc_identity`; `GetMonitorByQueryAsync`
requires a scope and refuses a row from another one; and the id of the shared
upstream monitor is recovered through `GetProviderMonitorIdByQueryAsync`, which
projects the opaque id ONLY and is structurally incapable of returning a row.
`kyc_screening_monitor_provider` is deliberately NOT unique for the same reason: one
upstream monitor legitimately backs one local row per tenant. Removal is scoped too —
the upstream registration is only unregistered when no other tenant still shares it.

**A page you did not read is not an absence of hits.** `search-screening-person`
returns no `total` and documents no sort order, so a single unpaged window silently
drops everything past it AND cannot detect that it saturated. The search pages until
a short read; a later page failing fails the whole search rather than keeping the
prefix; and exhausting `SearchMaxPages` on a full page marks the result
`results_truncated`, which is forced to manual review and is never a pass.

**The poll's high-water mark never advances past a record it could not classify.** A
STORE error on the monitor lookup is not "not ours": advancing the mark past it
filters that record out of every future cycle, so a sanctions change on it would
never be seen again. A store error aborts the cycle and parks on backoff; only a
genuine "no local monitor" is skipped. A changed shared monitor costs ONE upstream
search fanned out across every tenant row that registered it, not one call per
tenant.

## §tenant-quota

`IKycTenantQuota` sits in front of the shared provider budget. Because the vendor's
cap is per-IP and therefore node-wide, one consumer's begin-session storm would
otherwise consume the whole allowance and lock every other tenant out of KYC — a
cross-tenant denial of service, not a noisy-neighbour annoyance. Node-direct flows
share one partition rather than being exempt; they contend with each other, which is
the deliberate price of not letting an unbounded number of direct users each hold an
individual allowance. The limits are config constants meant to be tuned once real
traffic exists.

**Both counters are per PROCESS while the vendor's ceiling is per IP.** With N
replicas behind one egress IP an undivided ceiling lets the deployment spend N times
the node's allowance and earn the one-hour ban the budget exists to avoid — so
`Kyc:Identomat:ReplicaCount` divides both `SharedRequestsPerMinute` and the tenant
quota down. It defaults to 1; raise it BEFORE scaling out. A genuinely shared counter
would need a durable one, which is a larger change than this ceiling justifies today.

## §retention-sweep

`retention_expires_at` is written on `kyc_identity`, `kyc_screening_result`, and
`kyc_screening_monitor`. `KycIdentityRetentionSweepWorker` is what makes it more than
decoration — a registered-but-uncalled erasure service is worse than an absent one,
because it looks like the policy is enforced. The worker makes no vendor calls, so it
draws nothing from the shared per-IP budget and needs no operator opt-in; forgetting
to enable erasure is a compliance failure, not a tuning choice.

Erasure covers the AML trail, not just the binding. `kyc_screening_result` and
`kyc_screening_monitor` each retain a full name and a date of birth as the vendor's
query key, so tombstoning only `kyc_identity` would leave a name and a birthday on
the node forever and make the Art. 17 erasure a fiction. The verdicts, provider ids,
scopes, and audit timestamps survive; only the two PII fields are cleared. Screening
rows also carry their OWN retention stamp because a poll writes new results for years
after the binding was made, so they must be swept on their own schedule and not only
in the slipstream of a binding erasure.

## §production-boot-guard

`KycRuntimeSafety.GuardProductionProviderIsReal` fails Production boot when
`Kyc:Provider` does not name a real adapter. It reads `Kyc:Provider` directly and
diffs it against `KycRegisteredAdapters.RealKeys`, mirroring
`BlockchainProviderFactory.GuardAgainstUnregisteredEnabledChains`: the runtime
selection path substitutes `UnavailableKycProviderService` for an unknown key, so
asking the runtime would hide the exact defect the guard exists to catch. Without
it, a misconfigured production node boots healthy and fails closed on every real
verification instead of failing at start. `GuardStartup` is the complementary
direction — it rejects an explicitly selected simulation provider.

Configuration mutations append non-secret before/after evidence to
`kyc_control_audit`. The operator-only `GET /api/operator/kyc/audit` surface is
bounded and optionally filters by tenant, built-in provider key, or one of the
three stable configuration action values. Its opaque unpadded-base64url cursor
anchors the exclusive descending `(occurred_at,id)` position; newer audit rows
inserted between requests cannot shift, duplicate, or skip the remaining page.
Secret configuration is never accepted by or projected through that timeline.
