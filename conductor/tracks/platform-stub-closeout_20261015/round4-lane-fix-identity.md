---
type: lane-checkpoint
round: 4
wave: fix-batch
lane: fix-identity
branch: r4/fix-identity
status: ready
---

# Round 4 · fix batch · identity (`r4/fix-identity`)

Base `feat/platform-stub-closeout` @ `21da6183`. Worktree `../oasis-sleek-r4-fix-identity`. Inputs:
`.omc/research/round4-review/RV-L2.md` (L2-1..L2-9), plus coordinator additions RV-X **X-2** (HIGH) and
RV-L1 **RV-L1-03** (LOW). Vocabulary: **Built** = compiled + unit tests run green by filter;
**Authored** = integration/HTTP flow compiled, not run (brief: no container runs; the sweep runs them).

## Published seam (for r4/fix-chains, X-2)

A value-moving path checks **`IValueAccessService.RequireValueAccessAsync(userId, tenantId?, ct)`**
(`Interfaces/Managers/IValueAccessService.cs`) or **`IRealValueKycGate.RequireCurrentApprovalAsync`**
(`Interfaces/IRealValueKycGate.cs`). Both now resolve through `RiskHoldValueGate` (reversal account pause)
over the merged KYC gate. Never inject `IKycGateService` (bare KYC decision, status reads only;
`Architecture/ValueGateCoverageTests` fails if a production type depends on it). A risk-held refusal is
`IsError = true`, message `RiskHoldValueGate.RiskHoldMessage`, no code; map any refusal to 403.

## Scope closed

| id | fix | where |
|---|---|---|
| L2-1 | `HostedSessionOutcome.IsManualReview`, set only by Identomat `MANUAL_CHECK`; flow and KYC handler gate the review extension on it (not `SessionExists`). Veriff `PENDING`/`RESUBMISSION_REQUESTED`, generic `review`, Identomat undecided/`ADDITIONAL_INFORMATION_REQUESTED` never extend. Extension applied once (PENDING → IN_REVIEW only). | `Models/HostedSessions/HostedSessionModels.cs:148`, `Providers/Kyc/IdentomatKycProviderService.cs:439`, `Services/HostedSessions/HostedSessionFlow.cs:345`, `Services/Kyc/KycHostedSessionOutcomeHandler.cs:213,229` |
| L2-2 | Poll asks `IHostedSessionRelevanceProbe` (router → KYC/KYB handler `ProbeAsync`) before any vendor call; Dead → `ABANDONED`/`ATTEMPT_CLOSED` with no `/result/`; due list ordered by `next_poll_at` (NONE first). | `Services/HostedSessions/HostedSessionRecoverySeams.cs`, `HostedSessionOutcomeRouter.cs`, `HostedOutcomePollWorker.cs:63-78`, `KycHostedSessionOutcomeHandler.cs:242`, `Services/Kyb/KybHostedSessionOutcomeHandler.cs:49`, `SurrealHostedSessionStore.Recovery.cs:27` |
| L2-3 | Per-session on-demand cooldown: `IHostedSessionFlow.RefreshOnDemandAsync` + `TryClaimRefreshAsync` CAS on new `hosted_session.last_refresh_at` (30 s, `Kyc:Identomat:OutcomePoll:RefreshCooldownSeconds`); used by user KYC and tenant KYB refresh. Budget fairness: result re-fetches capped at `Kyc:Identomat:ResultRequestSharePercent` (50) of the window so begin/KYB/AML keep headroom. | `HostedSessionFlow.cs:31,275`, `SurrealHostedSessionStore.Recovery.cs` `TryClaimRefreshAsync`, `Managers/KycManager.cs:145`, `Services/Kyb/KybService.cs:125`, `Services/Kyc/IdentomatRateBudget.cs:64,88`, `Settings/KycSettings.cs` |
| L2-4 | Close-out is `TryAbandonPolledAsync`: version CAS that also takes `RESUMING` with an expired lease (clears the lease); a lost close still advances `next_poll_at`. | `SurrealHostedSessionStore.Recovery.cs:33`, `HostedOutcomePollWorker.cs:73-77` |
| L2-5 | Sweep breaks only on `PROVIDER_REQUEST_NOT_SENT` or an open circuit; a per-session 4xx keeps its backoff. User/tenant refresh: 503 only for `NOT_SENT`, otherwise stored status. | `HostedOutcomePollWorker.cs:90`, `KycManager.cs:146`, `KybService.cs:126` |
| L2-6 | Live attempts are never cut at `MaxAgeHours` (now a backstop for Unknown only), so an extended IN_REVIEW is polled until its decision or expiry; any `ABANDONED` KYC admission now retires-and-restarts the open attempt instead of "already terminal". | `HostedOutcomePollWorker.cs:66-71`, `KycManager.cs:328` |
| L2-7 | Early KYB renewal carries a still-effective approval (`effective_approval_*` on `kyb_verification`) until the renewal decides; `KybGate` allows PENDING + carried approval; `TryDecideAsync` clears it in the same CAS; status projection reports `Verified` honestly. | `Services/Kyb/KybService.cs:135`, `KybGate.cs:68`, `Providers/Stores/Surreal/SurrealKybVerificationStore.cs`, `Persistence/SurrealDb/Models/KybVerification.cs` |
| L2-8 / X-2 | `ValueAccessService` depends on `IRealValueKycGate` (the risk-hold-wrapped gate), so every `IValueAccessService` caller inherits the reversal pause. No `Program.cs` edit (the registration order is untouched; `IKycGateService` stays registered for status reads). | `Services/Kyc/ValueAccessService.cs:14`, `Services/Kyc/KycGateService.cs` doc |
| L2-9 | Admin pending queue lists `MANUAL` submissions only. | `KycManager.cs:607` |
| RV-L1-03 | `ListReconciliationAsync` and `ListStalledAsync` select KYC/KYB rows only; fiat creation claims are no longer labelled `NeedsOperator` / `AwaitingProviderOutcome` in the identity queue. | `SurrealHostedSessionStore.cs:153`, `SurrealHostedSessionStore.Recovery.cs:91` |
| test gap | Identomat fake: `Pending` answers `{"result":null}` (no documented undecided shape), new `ManualCheck` answers `MANUAL_CHECK`; `IdentityResultRequests` counter. | `tests/.../Http/Infrastructure/IdentomatVendor.cs`, `Http/AGENTS.md` §identomat-fake |
| CI (owned) | L2 gate inventories rewritten to interface-level rows (stale combined rows + 60 unclassified L2 store methods); duplicate `HostedIdentityCallback.Callback` route row merged. | `tests/AZOA.WebAPI.Tests/Architecture/Inventories/{CrossTenant,RouteAuthorization}/L2.json` |

### X-2 call-site decisions (all value-moving, all now see the pause)

`AllocationManager` (allocation, `:180,:320`), `ReserveExchangeManager` (exchange `:182`, preview `:320`),
`HoldCommandService*` (admission, execute, recovery, workflow), `HoldReleaseMandateService:39`,
`FungibleTokenManager:57`, `FungibleCreationService:329`, `NftLifecycleService` (mint),
`DeploymentService:179` + `.Recovery:136,201`, `ConsumerGovernanceService:100` (value-weighted vote).
None needed an edit: the decision moved into the shared service. `GetDecisionAsync` (readiness
projection) now reports `VerificationRequired` for a risk-held account (fail-closed, accepted).

## Hunks

None. No `Program.cs`, `appsettings*.json` or compose edits. New settings have code defaults
(`RefreshCooldownSeconds = 30`, `ResultRequestSharePercent = 50`).

## POCO deltas

- `hosted_session.last_refresh_at option<datetime>` (on-demand refresh cooldown; NONE = never refreshed; the
  CAS binds a captured null as NONE, same as `next_poll_at`).
- `kyb_verification.effective_approval_expires_at option<datetime>`, `effective_approval_evidence_json
  option<string>`, `effective_approval_authority_revision option<string>`.
- Goldens regenerated with the project generator (`AZOA_REGENERATE_GOLDENS=1`, 136 tests green). D-9
  generation reset: no migration. No unique index added (all new writes are single-record CAS).

## Compile and tests

- `dotnet build azoa.sln -c Debug` → **0 errors, 2 warnings** on a no-incremental rebuild (both
  pre-existing in fiat files, none in touched files).
- Unit tests by filter (`Kyc|Kyb|ValueGateCoverage|OAuthDoesNotSatisfyKyc|TenantConsentGate|TenantCustodial|
  AllocationConsentGate|HostedSession|Identomat|AttributePocoByteEquivalence|FlowchartRegeneration|RiskHold|Reversal`)
  → **813 passed, 0 failed**.
- `CrossTenantGateTests` / `RouteAuthorizationGateTests`: no remaining L2 rows or L2 gaps; the remaining
  failures belong to other lanes (below).
- `node scripts/round4/merge-check.mjs` → ok. `secret-scan` → no hits.

## Tests authored (flow-tests rules)

Built (unit, run):
- `ValueAccessServiceTests` (rewritten, table-driven): real `RiskHoldValueGate` over identity/ledger stubs;
  approved+clear allows, flagged/unreadable ledger/unverified refuse, tenant and first-party. Cut: the old
  `Times.Once` interaction assertion (tautology).
- `ValueGateCoverageTests`: no production type depends on `IKycGateService`. Cut while authoring: a
  constructor-signature assertion that restated the implementation.
- `HostedSessionFlowTests.Only_a_vendor_manual_review_reaches_the_progress_hook` (PENDING,
  RESUBMISSION_REQUESTED, REVIEW, MANUAL_CHECK).
- `KycHostedSessionOutcomeHandlerTests`: manual-review extension table (Veriff pending/resubmission, generic
  review, Identomat undecided/more-info/manual-check via the real `MapOutcome`); `ProbeAsync` table
  (open, in-review, expired, retired, decided, other-session, missing).
- `KycQuotaAndBootGuardTests.RateBudget_CapsResultRefetchesSoBeginAndScreeningKeepHeadroom`.

Authored (integration, compiled, not run):
- `KycFirstPartyFlowTests`: unsubmitted attempt never stretched (refresh + poll, L2-1); refresh cooldown
  (L2-3); poll closes an expired attempt's session with zero vendor calls (L2-2); expired-lease RESUMING row
  on a dead attempt is closed (L2-4); verified person + reversal flag → `IValueAccessService` refuses with
  `RiskHoldMessage` (X-2); existing manual-review test now sets `ManualCheck` explicitly.
- `KybEnforcementFlowTests.An_early_renewal_keeps_the_current_approval_until_the_renewal_is_decided` (L2-7).
- `ProgramCompositionTests.Value_access_checks_go_through_the_reversal_risk_hold` (host: the resolved
  `IValueAccessService` holds a `RiskHoldValueGate`).
- Not authored: per-route HTTP refusals for hold/exchange/fungible with a flagged account. They all call the
  same seam proven above; full route flows need chain/witness setup that belongs to the sweep (L6).

## Checklists

- Transaction scope: every new write is a single-record CAS (`TryAbandonPolledAsync` version CAS,
  `TryClaimRefreshAsync` on `last_refresh_at` without bumping `state_version`, KYB carried fields under the
  existing `version + attempt_id` CAS, cleared inside the fenced `TryDecideAsync`). The KYB carry reads the
  row outside the CAS; the CAS guarantees the overwritten row is the one read.
- Security: no route authz changed; refresh still derives the session from the caller's own attempt/tenant.
  The value gate became strictly narrower (risk hold added). No new PII in logs.
- Fail-closed classes: captured-null binding (`last_refresh_at`) follows the 1.3.0 NONE rule already relied
  on by `next_poll_at`; no raw SurrealQL added; no new enums.
- Pruning gate: no raw SQL, no catch-alls, shared carried-approval rule (`KybGate.HasEffectiveCarriedApproval`)
  used by gate, service and projection; unused usings removed (IDE0005 clean).

## Edits outside owned globs (declared)

- `Interfaces/HostedSessions/IHostedSessionStore.cs` (one doc line: list is KYC/KYB only).
- `tests/AZOA.WebAPI.IntegrationTests/Http/Infrastructure/IdentomatVendor.cs` and `Http/AGENTS.md`
  (harness infra, L6): fake fix ordered by the coordinator.
- `tests/AZOA.WebAPI.IntegrationTests/Hosting/ProgramCompositionTests.cs` (L1 host test): one test added
  for X-2, ordered by the coordinator.
- `tests/AZOA.WebAPI.Tests/Architecture/{ValueGateCoverageTests.cs (new), AGENTS.md}`.

## Needs other lane

- **r4/fix-chains (X-2):** direct WTT / DEX / submit-signed routes must call
  `IValueAccessService.RequireValueAccessAsync` (or `IRealValueKycGate`) before any value effect; see the
  published seam above.
- **r4/fix-money (RV-L1-03 second half):** fiat checkout/payout creation claims now drop out of the identity
  reconcile queue; give abandoned fiat checkout claims a terminal sweep and list stuck fiat claims in the
  fiat operator resolve. Also `tests/AZOA.WebAPI.Tests/Architecture/Inventories/CrossTenant/L3.json` has two
  stale rows (`IHostedSessionStore (fiat checkout).GetByIdAsync via …`, `….ClaimAdmissionAsync via …`): the
  `store`/`method` fields must be the bare interface and method name.
- **Other lanes (gate completeness, sweep):** `CrossTenantGateTests` / `RouteAuthorizationGateTests` still
  list non-L2 gaps (webhook delivery stores, `ExternalAuth.Unlink`, `NodeOperator.*` KYC operator routes —
  `Controllers/NodeOperatorController.cs` is not in the L2 glob).

## Deferred / residual (with trigger)

- A KYB admission closed by the Unknown/max-age backstop while its verification is still PENDING has no
  automatic restart (KYC has one now). Trigger: an operator report of a tenant stuck on "already terminal".
- A dead KYC session whose attempt is already APPROVED (resume committed, finish crashed) closes as
  `ABANDONED/ATTEMPT_CLOSED` rather than converging to `COMPLETED`; no identity effect is lost. Trigger: an
  audit view that keys on session terminal state.
- Identomat's real undecided `/result/` shape is still undocumented; the adapter treats any non-verdict as
  non-terminal without manual review (fail-closed). Confirm in the owner's live drill.
