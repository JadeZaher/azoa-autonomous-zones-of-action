---
type: lane-checkpoint
round: 4
wave: W2
lane: L2
branch: r4/identity
status: merged
merged: 5f1aa515
---

# Round 4 · L2 identity lane (`r4/identity`, merge #4)

Base `feat/platform-stub-closeout` @ `beae947`. Worktree `../oasis-sleek-r4-identity`. Closing
vocabulary: everything below is **Built** (compiled, unit-covered where noted) or **Authored**
(integration/HTTP flows compiled, not run — the brief forbids container runs; the sweep runs them).

## Published contracts (day 1)

### KYB gate for L3 (D-6, payout price schedules)

- Seam: `AZOA.WebAPI.Services.Kyb.IKybGate` (`Services/Kyb/KybGate.cs`), scoped, registered in the KYB
  block of `Program.cs`.
- Call: `RequireVerifiedTenantAsync(Guid tenantId, KybCapability.PayoutPriceSchedule, ct)`.
- Allow: the tenant's `kyb_verification` row is `APPROVED`, carries business evidence, `expires_at > now`
  and `authority_revision == IdentomatKybProvider.AuthorityRevision` (the node's current KYB config).
- Deny: `IsError = true`, `Code = "KYB_REQUIRED"` (`KybGate.RequiredCode`), message
  `KybGate.RequiredMessage` (generic; never says expired vs rejected vs absent). Map to **403**.
  Store faults throw (fail closed through the HTTP boundary).
- L3 call sites (recommended, both): `Controllers/TenantPriceScheduleController.cs:127` `Register`
  before `_schedules.RegisterAsync` when the scope is a tenant (not the node), and the tenant-priced
  payout admission in `Services/Fiat/OffRamp/OffRampService.cs` (~`:867`, where the tenant schedule is
  resolved) so a lapsed KYB stops payouts priced by an already-published schedule.
- Fixtures: `TestDataBuilders.KybApprovedTenant(tenantId).SeedAsync(services)` (L1) writes a row the
  gate accepts.

### Other D-6 enforcement (L2-owned, wired)

- `ITenantConsentGate` (`Services/Consent/TenantConsentGate.cs:103`): every tenant-driven decision that
  passes the grant check also requires `KybCapability.TenantOnBehalfValue`; a denial is audited as
  `TenantSignDenied`. This covers `KeyCustodyService` (all custody decrypts route through the gate) and
  the controller probes in `FiatCheckout`, `FiatPayout`, `Exchange`, `Allocation` controllers.
  **Every tenant-on-behalf fixture in other lanes now needs the KYB builder.**
- Custodial accounts / custodial KYC: `Managers/TenantCustodialAccountManager.cs:66` (`KybRefusalAsync`),
  called from `EnsureAsync` `:151`, `BeginKycAsync` `:265`, `SubmitKycAsync` `:381`; refusal is
  `TENANT_FORBIDDEN: KYB_REQUIRED: …` (403 through `TenantController.TranslateResult`).

### Routes (plan §4.9)

| Route | Policy | Body / result |
|---|---|---|
| `POST /api/kyc/session` | `FirstPartyLogin`, no acting tenant, rate limit `financial` | none → `KycSessionStartModel` (hosted redirect). 409 uncertain/in-progress/`NeedsOperator`, 429 + `Retry-After` quota, 503 vendor unavailable |
| `GET /api/kyc/status` | `FirstPartyLogin` (unchanged) | `KycSubmissionModel` |
| `POST /api/kyc/refresh` | `FirstPartyLogin`, no acting tenant, rate limit `financial` | none → `KycSubmissionModel` after an authoritative `/result/` re-fetch of the caller's open hosted attempt |
| `POST /api/operator/hosted-sessions/{id}/refresh` | `RecentNodeOperatorSession` | Identomat KYC/KYB sessions now refresh (was 404) → `{ sessionId, status, providerKey }`; audited `OperatorRefresh` |
| `GET /api/operator/hosted-sessions/reconciliation` | `RecentNodeOperatorSession` | adds attached sessions untouched for 1 h as `status: "AwaitingProviderOutcome"` beside `NeedsOperator` admissions (same row shape) |

## Scope closed

| Item | Where | What |
|---|---|---|
| **F-C3** first-party begin | `Controllers/KycController.cs:35` | `POST /api/kyc/session` → `KycManager.BeginAsync`; acting-tenant callers `Forbid` |
| **F-H1** user refresh | `Controllers/KycController.cs:49`, `Managers/KycManager.cs:128` | re-fetches `/result/` through the shared flow for the caller's own hosted attempt |
| **F-H1** outcome poll | `Services/HostedSessions/HostedOutcomePollWorker.cs` | backoff poll (60 s doubling → 60 min, `MaxAgeHours` 720) of attached non-terminal KYC/KYB sessions within `IdentomatRateBudget`; claims by CAS on `next_poll_at`; stops on budget refusal/circuit open; prepared providers skipped |
| **F-H1** operator | `Controllers/HostedSessionReconciliationController.cs:130,55` | Identomat refresh (resolver bound to the current authority) + stalled listing |
| **F-H1** MANUAL_CHECK | `Services/HostedSessions/HostedSessionFlow.cs:311`, `Services/Kyc/KycHostedSessionOutcomeHandler.cs:209`, `Providers/Stores/Surreal/SurrealKycStore.Progress.cs` | vendor manual review moves the attempt to IN_REVIEW and extends expiry to `SubmissionExpiryDays` (the 30-min attempt expiry made every manual review lose) |
| **F-H1** callback 202 | `Controllers/KycCallbackController.cs` (unchanged) | kept: the captured callback docs state no retry semantics a status code could trigger; recovery is the poll. Rationale in `Services/HostedSessions/AGENTS.md` §recovery |
| **F-M1** | `Providers/Kyc/IdentomatHttpTransport.cs:109,123,154`, `HostedSessionFlow.cs:166-184` | 4xx and 429/529 → `PROVIDER_REQUEST_REFUSED`, admission released; 5xx/timeouts stay quarantined. Quota refusal now surfaces `OPERATION_NOT_ALLOWED` + `RetryAfterSeconds` |
| **F-M12** | `Services/Kyc/KycGateService.cs` (implements `IKycGateService` + `IRealValueKycGate`), `Services/Bridge/RealValueKycGate.cs` deleted, `Program.cs` swap **in place** at the single `IRealValueKycGate` registration | host test (outer `RiskHoldValueGate`, inner not) unchanged; merge check passes |
| **F-M3 / D7** | `appsettings.Development.json`, `docker-compose.dev.yml` | dev KYC = Identomat + identomat policy, `AllowManualInDevelopment=false`; return URLs default empty (adapter forwards only public HTTPS); `Kyc__Identity__PersonKeySecret` passed through; KYB provider treats empty `ReturnUrl` as unset (`Providers/Kyb/IdentomatKybProvider.cs`) |
| **F-M4 / D5** | `HostedSessionReconciliationController.cs:67,109,159` | mojibake removed; recovered redirect host checked against configured `ResultBaseUrl`, not hardcoded `widget.identomat.com`. Also repaired mojibake in four owned KYC files |
| **D-6** | see contracts above | KYB at consent gate, custodial access; L3 contract |
| **IDENTOMAT-RECOVERY** | `Managers/KycManager.cs:349-388`, `Services/Kyb/KybService.cs:63,135`, `Providers/Stores/Surreal/SurrealHostedSessionStore.Recovery.cs`, `HostedSessionFlow.cs:206,418`, `Providers/Kyc/IdentomatSessionDeletion.cs` | orphan supersede after `OrphanWaitMinutes` (5), 1 per subject per 24 h, else `NeedsOperator`; interrupted supersede resumes; late create responses deleted via documented `/external-api/delete/` and audited; unknown-session callbacks inert (no row, no write) |
| `HostedKycCreationFence.cs:25-27` + **SQL-09** | `Helpers/HostedKycCreationFence.cs:25`, `Providers/Stores/Surreal/SurrealKycStore.cs:199` | `FROM (IF $has_tenant { [type::record(…)] } ELSE { [] })` |
| `KycController.cs:74` tenant scope | `Controllers/KycController.cs:104` | acting-tenant callers read the tenant-scoped attempt |
| **SQL-13** (KycScreening) | `Providers/Stores/Surreal/SurrealKycScreeningStore.cs:28,301` | explicit `Of(...)` per statement, no `Split(';')` |
| W1 note: monitor lineage | `Services/Kyc/IdentomatScreeningService.cs:41,186` | `lineageKey` required (NONE escaped the UNIQUE index) |
| **A-4** AML monitor poll | `Settings/KycSettings.cs`, `Services/Kyc/KycScreeningMonitorPollWorker.cs:39`, `appsettings.json` | `MonitorPollEnabled` is `bool?`; unset = on wherever an Identomat config id is set |
| Lost completion CAS | `KycHostedSessionOutcomeHandler.cs:199` | a lost completion CAS now retries instead of terminalizing the session without the decision (race with the manual-review mark) |
| Stale attempt outcome | `KycHostedSessionOutcomeHandler.cs:107` | an outcome for a session whose attempt was superseded terminalizes quietly (no infinite poll) |
| CI failures in globs | `tests/AZOA.WebAPI.Tests/Kyc/*`, `Kyb/IdentomatKybTests.cs` | 5 of 5 fixed (quota test rewritten through the real flow; policy name; handler mismatch semantics; KYB refresh on unattached session is a no-op in production `Services/Kyb/KybService.cs:121`) |

### SQL-09 probe (throwaway `surrealdb/surrealdb:v3.1.4`, `--user 0:0`, port 8045, reaped)

- `SELECT * FROM type::record('t', '') WHERE false` → `ERR: Found  for the Record ID but this is not a valid id`
  (CONFIRMED: first-party KYC submission and node-default hosted send failed closed on 3.1.4).
- `SELECT v FROM (IF $h { [type::record('t', $id)] } ELSE { [] })` → `[]` with `$h=false, $id=''`; `[{v:1}]`
  with a real id. Note `IF $h { (SELECT …) }` unwraps a single-row result to an object, hence the array literal.
- `false AND type::record('t','') = NONE` → `false`: AND short-circuits, so the AND-guarded sites
  (`ActionExecutionFence.cs:16-17`, `SurrealActionSigningAuthorizationStore.cs:45-46`,
  `HoldReleaseWorkflowFence.cs:56-57`, `HostedKycCreationFence.cs:20`) are safe.
- `UPSERT []` is a no-op that commits; `UPSERT [a, b]` bumps each guard once (KycEvidenceFence semantics).

## Hunks

```hunk path=Program.cs
// Identomat persists no provider attempt, so its lost callbacks recover through the backoff outcome poll.
builder.Services.AddScoped<AZOA.WebAPI.Services.HostedSessions.IHostedSessionRecoveryStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealHostedSessionStore>();
builder.Services.AddScoped<AZOA.WebAPI.Services.HostedSessions.HostedIdentityProviderResolver>();
builder.Services.AddHostedService<AZOA.WebAPI.Services.HostedSessions.HostedOutcomePollWorker>();
```

```hunk path=Program.cs
builder.Services.AddScoped<AZOA.WebAPI.Services.Kyb.IKybGate, AZOA.WebAPI.Services.Kyb.KybGate>();
```

```hunk path=Program.cs
// all, so this worker is the only change-detection path; it runs wherever an Identomat
// config id is set unless Kyc:Identomat:Screening:MonitorPollEnabled is false.
```

```hunk path=Program.cs
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.IRealValueKycGate,
    AZOA.WebAPI.Services.Kyc.KycGateService>();
```

```hunk path=appsettings.Development.json
    "__comment": "Development uses the same Identomat provider and policy as every environment (round-4 A-4): the value gates never accept MANUAL, so a manual dev default only closed every value flow. Without Kyc__Identomat__* secrets the adapter reports UNAVAILABLE; tests use vendor-doc fakes.",
```

```hunk path=docker-compose.dev.yml
      Kyc__Identomat__ReturnUrl: "${Kyc__Identomat__ReturnUrl:-}"
      Kyc__Identomat__ConfigId: "${Kyc__Identomat__ConfigId:-}"
      Kyc__Identomat__CallbackApiKey: "${Kyc__Identomat__CallbackApiKey:-}"
      Kyc__Identity__PersonKeySecret: "${Kyc__Identity__PersonKeySecret:-}"
      Kyb__ConfigId: "${Kyb__ConfigId:-}"
      Kyb__ReturnUrl: "${Kyb__ReturnUrl:-}"
```

```hunk path=docker-compose.dev.yml
      Kyc__Provider: "${AZOA_KYC_PROVIDER:-identomat}"
```

```hunk path=docker-compose.dev.yml
      Kyc__ApprovalPolicy__PolicyVersion: "${AZOA_KYC_POLICY_VERSION:-identomat-hosted-v1}"
      Kyc__ApprovalPolicy__AssuranceLevel: "${AZOA_KYC_ASSURANCE_LEVEL:-document-and-liveness}"
      Kyc__ApprovalPolicy__TrustedProviderKeys__0: "${AZOA_KYC_TRUSTED_PROVIDER_KEY:-identomat}"
      Kyc__ApprovalPolicy__AllowManualInDevelopment: "${AZOA_KYC_ALLOW_MANUAL_IN_DEVELOPMENT:-false}"
```

`appsettings.json`: one deletion only (`Kyc:Identomat:Screening:MonitorPollEnabled: false` removed so
the A-4 default applies) — a deletion has no verbatim hunk to keep.

## POCO deltas

- `hosted_session` (not baselined): `next_poll_at option<datetime>`, `poll_attempt_count option<int>`,
  non-unique index `hosted_session_poll (state, next_poll_at)`. Old rows: NONE = due now / 0 polls; the
  poll claim CAS reads `next_poll_at` and binds a captured null as NONE (1.3.0), so no backfill is needed.
  Golden regenerated with the project generator (`Generated/Schemas/hosted_session.surql`); 132 golden
  tests green after regeneration. No unique index added; the audit table in
  `Persistence/SurrealDb/AGENTS.md` is unchanged (poll claims are single-record CAS).
- No change to baselined `kyc_submission`.
- Owner decision (after lane start): schema moves by generation reset (fresh DB), so no baseline or
  forward migration is needed for the `hosted_session` fields.

## Compile result

`dotnet build azoa.sln -c Debug` → **0 errors, 22 warnings** (worktree base build: 22; all xUnit
analyzer/MSBuild notes, none in L2 files). `node scripts/round4/merge-check.mjs` → ok.
`node scripts/round4/secret-scan.mjs . "feat/platform-stub-closeout HEAD"` → no hits.
Unit tests run by filter (allowed): `Kyc|Kyb|TenantConsentGateTests|TenantCustodialAccountManagerTests|AllocationConsentGateTests`
→ **554 passed, 0 failed**.

## Tests authored (flow-tests rules)

Flows, all through the highest fast public seam with only the vendor HTTP edge faked:

- `Http/KycFirstPartyFlowTests.cs` — begin → callback (body claims REJECTED) → `/result/` APPROVED → gate
  open (host's real `IRealValueKycGate`), replay changes nothing; vendor reject → gate closed; abandoned
  (expired) attempt + late approval → EXPIRED, gate closed; lost callback recovered by user refresh;
  lost callback recovered by the poll worker, manual review extends the attempt; operator refresh
  (audited); delegated tenant caller refused.
- `Http/KycOrphanRecoveryFlowTests.cs` — lost `/begin` response → 409, no repeat inside the wait,
  supersede after it, orphan vendor session approved later stays inert; second orphan in a day →
  `NeedsOperator`; unknown-session callback with the right key changes no row.
- `Http/KybEnforcementFlowTests.cs` — D-6 allow + deny on all three capabilities (custodial HTTP flow,
  consent gate with the real enrolment grant, KYB gate table: approved/absent/expired/rejected/stale).
- `Persistence/Surreal/SurrealHostedSessionOrphanRecoveryTests.cs` — late create response after
  supersede is deleted and never attaches (flow + real store; the HTTP fake cannot hold a response
  open); 6 concurrent poll claims → exactly one winner; supersede budget scoping.
- `Persistence/Surreal/KycEvidenceFenceTests.cs` — empty array commits/no-op; multi-guard bumps once each.
- `Kyc/IdentomatBeginRefusalTests.cs` (unit, run) — F-M1 table through real adapter + transport + flow.
- Unhappy paths kept: authz denial (delegated caller), vendor reject, abandon, lost callback, lost
  begin response, budget exhaustion — each reachable, distinct, consequential.
- Changed expectations (not weakened): quota test now asserts through the real flow; handler
  "session mismatch" and lost-completion cases now expect quiet terminal / retry respectively (no
  identity-effect assertions removed); one stub returned `Success(null)` from a completion it did not
  model and now echoes the submission.
- Accidental overwrite of `SurrealHostedSessionRecoveryTests.cs` was caught in diff review and
  restored from base before the ready commit.

## Checklists

**Transaction scope** (`round3-transaction-scope-review.md`), per changed store/fence:
- `TryClaimPollAsync`, `TrySupersedeOrphanAsync`, `TryMarkHostedInReviewAsync` — single-record CAS on the
  observed version/value; no multi-record invariant. In-review mark runs inside `KycEvidenceFence`, so it
  serializes with completion; a lost completion CAS now retries (handler `:199`).
- `AppendAuditAsync` — CAS on the audit text only, never bumps `state_version` under a live flow.
- Supersede + retire are two writes; an interruption between them is resumed on the next begin
  (`KycManager.cs:381`), and KYB self-heals (new random session id). Supersede budget is a rate limit
  read outside the CAS — advisory, documented.
- KYB gate is a typed pre-read, not an in-batch fence (row changes only on tenant re-verification);
  documented in `Services/Kyb/AGENTS.md` §KYB gate.
- No DB transaction spans a vendor call; the late-delete runs after the attach CAS loses.

**Security** (changed routes/services): authz — first-party routes `FirstPartyLogin` + acting-tenant
refusal; operator routes `RecentNodeOperatorSession`; acting-tenant scope on status/summary; input — no
new bodies; callback unchanged and opaque; secrets/PII — delete/result bodies never logged, only status
codes; idempotency — poll claims single-winner, supersede CAS single-winner, refresh idempotent through
the flow's lease; no bare ids into record links (subject compared as a `user:` link).

**§7 fail-closed classes**: binding names unchanged; array captures — SQL-09 now always yields an array
(probed); positional offsets unchanged; strict validator — no new LET in loops; lowercase-enum/explicit
NULL raw writes — none; captured-null widening — only `next_poll_at` (intended NONE match), no
owner/tenant column; bare ids into `[References]` — none; `;` in `Of` literals — removed the split;
ordinal casts / converter-less enums — none (`KybCapability` is never persisted); statements after a
top-level RETURN — none added.

**Pruning gate**: IDE0005 clean; new stores typed (no raw SurrealQL added besides the SQL-09 rewrite);
catch-all only in the worker loop; `inheritdoc` on implementations; rationale in
`Services/HostedSessions/AGENTS.md` §recovery, `Services/Kyc/AGENTS.md` (value gate, first-party,
manual review, monitor), `Services/Kyb/AGENTS.md` §KYB gate, `Providers/Kyc/AGENTS.md` §F-M1.

## Inventories

`tests/AZOA.WebAPI.Tests/Architecture/Inventories/RouteAuthorization/L2.json` (14 routes) and
`…/CrossTenant/L2.json` (8 store methods). No value-moving route is owned by L2.

## Edits outside owned globs (declared)

- `Interfaces/Managers/IKycManager.cs` — `RefreshAsync` added (the only implementer is L2's `KycManager`).
- `tests/AZOA.WebAPI.Tests/Consent/AllocationConsentGateTests.cs:373` — compile-forced: passes
  `ApprovedKybGate` to the new `TenantConsentGate` constructor parameter.
- New narrow interfaces live in owned folders instead of widening unlisted ones:
  `Services/HostedSessions/IHostedSessionRecoveryStore.cs`, `Services/HostedSessions/HostedSessionRecoverySeams.cs`,
  `Services/Kyc/IKycHostedProgressStore.cs`.

## Needs other lane

- **L3**: wire `IKybGate` (`KybCapability.PayoutPriceSchedule`) at `Controllers/TenantPriceScheduleController.cs:127`
  and the tenant-priced payout admission (`Services/Fiat/OffRamp/OffRampService.cs` ~`:867`). Tenant-on-behalf
  Stripe fixtures need `TestDataBuilders.KybApprovedTenant` (the consent gate now requires KYB).
- **L4**: `Services/Actions/ValueRailCapabilityGate.cs:79` doc-comment points at deleted
  `Services/Bridge/RealValueKycGate.cs` → `Services/Kyc/KycGateService.cs`. CI failures outside L2 globs:
  `Controllers/TenantControllerRetryableResultTests` (2, `TenantController` capture returns `Forbid`) and
  `Auth/WalletAuthTokenParityTests` (4, `WalletAuthManager`). Tenant-driven fixtures need the KYB builder.
- **L5**: tenant-driven NFT/chain fixtures that go through `ITenantConsentGate` need the KYB builder.
- **L6**: optionally add `/external-api/delete/` to `Http/Infrastructure/IdentomatVendor.cs` so a future
  HTTP flow can observe late deletes (L2 covers it at the flow + real-store seam). The fake answers
  `MANUAL_CHECK` for any undecided session; the docs do not define the undecided `/result/` shape — confirm
  in the live drill.
- **L7**: SDK methods for `POST /api/kyc/session`, `POST /api/kyc/refresh`,
  `POST /api/operator/hosted-sessions/{id}/refresh`; first-party `verify-identity` page (begin → redirect →
  refresh/status); operator console rows with `status: "AwaitingProviderOutcome"` + a refresh action;
  `KYB_REQUIRED` 403 messaging on custodial/tenant pages.

## Deferred / residual (with trigger)

- An orphan whose KYC attempt already expired before the person returned stays `CLAIMED` in the operator
  queue (inert; operator absence-reconcile clears it). LOW; revisit if the queue fills.
- Sessions whose tenant changed KYC provider selection after begin no longer resolve a refresh provider and
  close at `MaxAgeHours`. LOW.
- Billing for superseded/late-deleted vendor sessions is an owner question for Identomat (cost, not capability).

## Owner action — live Identomat drill (raise now)

1. Identomat dashboard → create the **KYC** workflow (document + liveness) → put its id in `.env` as
   `Kyc__Identomat__ConfigId`.
2. Create the **KYB** workflow (company + beneficiaries + review) → `.env` `Kyb__ConfigId`.
3. Company settings → **Callback URL** = `https://<public tunnel or deployed host>/api/kyc/callback/identomat`
   (one URL serves KYC and KYB; KYB callbacks carry `sessionType: "kyb"`) and **Callback API key** = the
   value already in `.env` `Kyc__Identomat__CallbackApiKey` (must match; ≥32 chars).
4. `.env` already has `IDENTOMAT_API_KEY` (company key) and `Kyc__Identomat__CallbackApiKey`. Add
   `Kyc__Identity__PersonKeySecret` (random, ≥32 chars) — approvals cannot bind a person without it.
5. Optional: `Kyc__Identomat__ReturnUrl=https://<tunnel>/verify-identity`,
   `Kyb__ReturnUrl=https://<tunnel>/business-verification` (http/localhost is ignored by design).
6. Authorize ~8 live sessions for the drill (approve / abandon / reject / replay KYC, approve / reject KYB,
   one lost response, D4 standard mode) and ask Identomat: what `/external-api/result/` returns for an
   undecided session, whether creation or completion is billed, and which token `/result/` and `/delete/`
   accept when `additionalInformationRequest` is on.
