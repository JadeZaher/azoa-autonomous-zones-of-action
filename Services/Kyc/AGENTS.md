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
for value-bearing consumer actions. It delegates to the host's `IRealValueKycGate`, which
is `RiskHoldValueGate` (reversal account pause) wrapping `KycGateService`; the KYC decision
resolves the current provider through `IKycProviderService` and the provider registry. This
keeps consumer policy out of AZOA while ensuring a manual, unavailable, stale, or untrusted
provider approval never opens a value path. Projects and non-value collaboration must not use
this gate.

**Value paths never see the bare KYC gate (round-4 fix batch, RV-L2 L2-8 / RV-X X-2).** F-M12
merged the gates, but `IKycGateService` stayed registered bare while only `IRealValueKycGate` is
wrapped by `AddReversalRiskValueGate`, so allocation, exchange, hold, fungible creation, NFT
lifecycle, deployments and consumer governance (all via `IValueAccessService`) ignored the
chargeback pause: buy by card, dispute, move the units out before clawback.
`ValueAccessService` now takes `IRealValueKycGate`, so every caller inherits the pause with no
per-call-site change and no `Program.cs` edit. `IKycGateService` remains registered for status
reads only; `Architecture/ValueGateCoverageTests` fails if any production type takes it as a
dependency. **Seam for other lanes:** a value-moving path checks
`IValueAccessService.RequireValueAccessAsync` (user/tenant readiness) or `IRealValueKycGate`
directly — never `IKycGateService`. Call-site decisions (all value-moving, all keep the pause):
`AllocationManager` (allocation), `ReserveExchangeManager` (exchange + preview),
`HoldCommandService*`/`HoldReleaseMandateService` (holds), `FungibleTokenManager`/
`FungibleCreationService` (token creation), `NftLifecycleService` (mint), `DeploymentService*`
(native deployment), `ConsumerGovernanceService` (value-weighted votes). The risk-hold refusal
message is `RiskHoldValueGate.RiskHoldMessage` (no `KYC_FORBIDDEN:` prefix); callers already
treat any error as a refusal.

**The value gate requires TWO independent facts.** `KycGateService` is the node's one
KYC decision: it implements both `IKycGateService` (status) and `IRealValueKycGate`
(the inner gate the risk hold wraps) — round-4 F-M12 deleted the near-verbatim fork
`Services/Bridge/RealValueKycGate`. It reads a current
policy-bound approval AND the latest AML screening verdict for the same
`(user, tenant, approved attempt lineage)` tuple. Identity verification says who a person is; it says nothing
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

`EvaluateEligibilityAsync` answers the BIND-TIME question — one person, one user per
scope, asked when a verification lands. It is not the per-operation surface; that is
`IPersonUniquenessGate` (§person-uniqueness), which D3 adds on top. Do not merge the
two: D1 explicitly permits one human to hold many verified users, and collapsing
bind-time uniqueness into per-operation uniqueness would refuse the legitimate case.

**Every consumer-visible denial is the same sentence.** `KycEligibilityDecision`
carries a single `Message` for all refusals and keeps the discriminating
`Cause` `[JsonIgnore]`d. "This person already claimed via another user" would tell
a tenant that its subject exists elsewhere on the node, which is precisely the
cross-tenant fact the per-tenant salting exists to hide. The operator-side cause
survives for support and audit; it must never reach a response body.

## §person-uniqueness

`IPersonUniquenessGate` (`Interfaces/Managers/IPersonUniquenessGate.cs`,
implemented by `Services/Kyc/PersonUniquenessGate.cs`) is the D3 per-operation
one-human-one-claim gate. **Presence of a `PersonUniquenessRequirement` IS the
opt-in.** An operation that declares none gets no check at all, which is the
default for all 43 existing node types — a uniqueness gate applied to everything
would refuse ordinary repeat work.

**Per-tenant is the default and it cannot be reached by forgetting.**
`PersonUniquenessScope.Tenant = 0`, so a default-constructed record, an omitted
JSON field, and a null-coalesced value all land there. `Global` requires an
explicit `"scope": "Global"` and is logged at Warning on every resolution, so an
operator can audit each cross-tenant question the node was ever asked. D23's
accepted residual risk is the inverse — an operator who FORGETS to opt a
Sybil-sensitive operation into `Global` gets no cross-tenant protection. That is
a decided trade; do not "fix" it by defaulting to `Global`.

**The Warning fires on the RESOLVED scope, not the declared one (W9.2).** The
arithmetic is worth writing down because it is not obvious:
`KycPersonKeyDeriver.ScopeValue(null) == GlobalScopeSentinel`, so for a caller with
no acting tenant "per-tenant" and "node-global" are literally the same domain. A
tenant-less caller declaring `Tenant` therefore lands in the node-global domain, and
`PersonUniquenessGate` logs it — with `declaredScope` beside the resolved one, so the
two cases can be told apart in the audit trail. Auditing the DECLARED scope would
have left the quietest cross-tenant path on the node unlogged.

**Four steps, always all four.** Canonicalize the operation key ->
`KycPersonKeyDeriver.ScopeValue(tenantId)` (the ONE canonical scope rule, reused
rather than re-derived) -> `IKycIdentityStore.GetByUserAsync` -> claim. Step three
is the reverse lookup the store previously lacked: it could go person key -> row
but not user -> row, so nothing could resolve an authenticated user to a person
key. Step four picks the key by scope — `person_key` for `Tenant`,
`global_person_key` for `Global`. That branch is the FIRST production wiring of
`global_person_key`; `GetByGlobalPersonKeyAsync` stays node-operator support and
audit only, because the gate needs a claim ledger, not a person -> users fan-out.

**The refusal lives at the WRITE, exactly as it does for the binding.**
`IPersonOperationClaimStore.TryClaimAsync` decides the winner inside one
conditional statement on the deterministic record id, and the gate decides refusal
by comparing the returned `user_id` to its own. A preceding "is it free?" read is
passed by BOTH concurrent claimants and makes the gate a tautology. The SurrealQL
`SET` ordering is load-bearing — see the comment in
`SurrealPersonOperationClaimStore`.

**One sentence, always.** Every refusal — no identity in scope, identity erased,
person already claimed through another user, unconfigured root secret, unusable
operation key, unreachable store — returns
`Success(KycEligibilityDecision.Ineligible(cause))` with
`KycIdentityBindingService.IneligibleMessage`. Three constraints keep that honest:

- **`IsError` is never a refusal.** An error result travels a different controller
  path (4xx/5xx vs a 200 carrying `IsEligible=false`), and that status-code
  difference is a perfectly usable oracle. `IsError` is reserved for programming
  errors — a null requirement, an empty user id — which cannot depend on whether
  the subject exists elsewhere.
- **Unavailability is a DENIAL, not an outage signal.** A 503 that appears only for
  already-claimed people is the leak wearing a hat. This deliberately diverges from
  `BindAsync`, which may surface `DependencyUnavailable` because binding happens
  before any person-scoped question is being asked about a specific third party.
- **Timing.** The gate does not return early when the identity lookup comes back
  null; it performs the claim round trip against a sentinel key derived from the
  user id (prefixed `!`, which unpadded-base64url person keys can never contain),
  so "no identity" and "already claimed" cost the same two round trips. Stated
  honestly: this narrows the channel to sub-round-trip noise, it does not close it.

**The caller must not re-widen it.** A consumer of this gate has to fail with a
byte-identical message whether the refusal came from here, from a closed proposal,
from an unknown option, or from a resource the runner cannot read. A node whose
failure text is "already voted" for one cause and "proposal closed" for another has
rebuilt the oracle one layer up. Telling a tenant "you already voted" reveals
nothing cross-tenant and would be better UX — it is still not allowed, because the
moment the message varies by cause the caller must correctly re-derive which scope
it was in to know whether varying is safe, and that conditional is the bug.

**Compensation is mandatory.** A caller that wins a claim and then fails its side
effect MUST call `ReleaseAsync`, or the person is permanently locked out by a claim
that bought them nothing. `ReleaseAsync` is scoped to the holder, so a release by a
non-holder returns `Success(false)` instead of freeing someone else's claim.

**Mandated consumer.** D20's governance vote node is the operation this seam exists
for; the REST twin, if a lane ships one, uses the same requirement object and the
same status code as ordinary ineligibility. D19 (revocation, issuer-authorized),
D21 (per-loan, not per-person) and D16 (a read surface) are deliberately NOT call
sites — wiring the gate into them is scope creep against D3's "operations that
GENUINELY need one-human-one-claim". **Until the governance lane lands, the gate is
reachable only from `PersonUniquenessGateTests`.** State that plainly rather than
reading the seam's existence as coverage: this repo's most-repeated defect is an
adapter that exists while nothing consults it.

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
query_birthday_time_from, lineage_key)` mirroring `kyc_identity`; `GetMonitorByQueryAsync`
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

## Hosted-outcome safety

`KycHostedSessionOutcomeHandler` checks exact user, tenant, provider, session,
active status and unexpired attempt before screening, binding or monitor writes.
A malformed tenant id is rejected rather than treated as the node-default scope.
The observed attempt remains unchanged while external work executes. Completion
uses typed conditional mutation over correlation, trust provenance, original
status, modification time and expiry, preventing an intervening terminal decision
or attempt replacement from being overwritten. A lost race returns no applied
submission id. Approval expiry uses the same operator lifetime as manual review.

AML screening precedes identity binding; monitoring follows the idempotent binding.
Identity approval and AML clearance remain distinct facts. Adverse screening opens
operator review without rewriting verified identity as a document rejection.
The hosted resumption lease cancels work when ownership is lost; the handler checks
cancellation again before its final conditional write.

Hosted identity binding uses `BindHostedAsync`: the store write-locks both the
live hosted lease and the exact active KYC attempt before changing identity
membership. Those checks and the identity write share one transaction, so stale
resumptions cannot bind after lease takeover or attempt retirement. The general
non-hosted binding path uses typed create followed by conditional membership CAS.
An existing identity held by a different user remains untouched.

## AML attempt lineage

The hosted handler derives an opaque SHA-256 correlation from a versioned,
length-delimited JSON array containing the attempt id, session id and verified
subject fields. Only the hash is persisted; document attributes are not retained.
The completed approval stores that same key. Value gates reject a missing key and
read only evidence matching that approval's lineage. A late clean response from an
obsolete attempt therefore cannot overwrite the current attempt's adverse verdict.
Local monitor uniqueness includes lineage, and every ongoing follow-up copies the
monitor lineage. One upstream query can serve multiple attempts without lending
one attempt's evidence to another. Unlinked operator screenings remain audit data.

Hosted approval requires successful durable monitor registration. A registration failure leaves the attempt pending and releases hosted resumption for retry; existing identity binding is idempotent and cannot authorize value without the final approval and matching screening lineage.

## Renewable hosted attempts

KycManager creates the active domain attempt before the external request. Its id
is also the hosted-session id and scopes the admission key. Retries reuse it;
rejected, expired, retired-authority, or real approvals missing lineage can start
a new attempt. Exact-snapshot retirement cannot overwrite a concurrent decision.
Redirect attachment never updates terminal outcomes. Missing-lineage real approval
projects EXPIRED, allowing the existing SDK/UI renewal contract without exposing
AML evidence. Manual Development simulation retains its existing behavior.
Callbacks before legacy domain attachment remain retryable. Approval requires both
screening and durable monitor evidence with the same verified-subject lineage.

## First-party verification and refresh (round 4, F-C3/F-H1)

`POST /api/kyc/session` (`FirstPartyLogin`, no acting tenant) calls `KycManager.BeginAsync`
for the caller's own tenant-less attempt; before it, only tenants could start hosted KYC, so
every first-party value gate stayed closed. `POST /api/kyc/refresh` re-fetches `/result/`
for the caller's open hosted attempt through the shared flow (lost or failed callback) and
returns the status. `GET /api/kyc/status/summary` from an acting-tenant credential reads that
tenant's attempt, never the person's first-party one. Lost callbacks also recover without
anyone asking: see `Services/HostedSessions/AGENTS.md` §recovery.

## Manual review (MANUAL_CHECK)

`MANUAL_CHECK` is non-terminal, so it never opens a gate. But a hosted attempt expires after
`SessionExpiryMinutes` (30), and a terminal verdict that lands after expiry terminalizes the
session with no identity effect — a vendor human review would always lose. When the
authoritative re-fetch reports a genuine vendor manual review, the flow calls the
`IHostedSessionProgressHandler` hook; `KycHostedSessionOutcomeHandler.ObserveProgressAsync`
moves the matching live attempt to `IN_REVIEW` and extends its expiry to
`SubmissionExpiryDays` through `IKycHostedProgressStore` (CAS on the observed row, inside the
evidence fence). A stale or mismatched attempt is a silent no-op.

**Only `MANUAL_CHECK` is manual review (fix batch, RV-L2 L2-1).** The hook first fired on any
non-terminal outcome with `SessionExists`, and Veriff (`verification: null`, i.e. not yet
submitted; `RESUBMISSION_REQUESTED`) and generic hosted (`pending`/`review`) report exactly that.
One callback or `POST /api/kyc/refresh` on a fresh start then flipped it to IN_REVIEW for
30-365 days, so an abandoned start could still be approved late. `HostedSessionOutcome.IsManualReview`
is now the sole trigger and only `IdentomatKycProviderService.MapOutcome` sets it (for
`MANUAL_CHECK`). An undecided Identomat session (no `result`) and
`ADDITIONAL_INFORMATION_REQUESTED` stay PENDING with their short expiry. The extended IN_REVIEW
attempt is what the outcome poll's relevance probe keys on (`ProbeAsync`: live while the attempt
bound to the session is PENDING/IN_REVIEW and unexpired), so the poll keeps fetching until a late
human decision lands or the extended attempt expires (L2-6).

**The admin manual-review queue lists MANUAL submissions only (L2-9).** `GetPendingAsync` skips
hosted IN_REVIEW attempts: `ApproveAsync`/`RejectAsync` refuse every non-MANUAL row, so showing
them gave admins rows they could not act on.

A hosted outcome whose open attempt belongs to a different session (the session's own attempt
was retired or superseded) now terminalizes that session quietly instead of retrying forever.

## Monitor polling and lineage (round 4)

`Kyc:Identomat:Screening:MonitorPollEnabled` is now `bool?`: unset means the AML monitor cold
poll runs wherever an Identomat `ConfigId` is set (plan A-4); `false` still turns it off.
`IdentomatScreeningService.RegisterMonitorAsync` takes a required `lineageKey`: a NONE
`lineage_key` escapes the UNIQUE `kyc_screening_monitor_query` index (W1 probe), so a
lineage-less monitor would not be single-winner.
