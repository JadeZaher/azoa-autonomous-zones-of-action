---
type: lane-checkpoint
round: 4
wave: W2
lane: L2
branch: r4/identity
status: in-progress
---

# Round 4 · L2 identity lane (`r4/identity`, merge #4)

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

### Other D-6 enforcement (L2-owned, already wired)

- `ITenantConsentGate` (`Services/Consent/TenantConsentGate.cs`): every tenant-driven decision that
  passes the grant check also requires `KybCapability.TenantOnBehalfValue`; a denial is audited as
  `TenantSignDenied`. This covers `KeyCustodyService` (all custody decrypts route through the gate) and
  the controller probes in `FiatCheckout`, `FiatPayout`, `Exchange`, `Allocation` controllers.
  **Every tenant-on-behalf fixture in other lanes now needs the KYB builder.**
- Custodial accounts / custodial KYC: `Managers/TenantCustodialAccountManager.cs` `EnsureAsync`,
  `BeginKycAsync`, `SubmitKycAsync` refuse with `TENANT_FORBIDDEN: KYB_REQUIRED: …` (403 through
  `TenantController.TranslateResult`).

### Routes (plan §4.9)

| Route | Policy | Body / result |
|---|---|---|
| `POST /api/kyc/session` | `FirstPartyLogin`, no acting tenant | none → `KycSessionStartModel` (hosted redirect) |
| `GET /api/kyc/status` | `FirstPartyLogin` (unchanged) | `KycSubmissionModel` |
| `POST /api/kyc/refresh` | `FirstPartyLogin`, no acting tenant | none → `KycSubmissionModel` after an authoritative `/result/` re-fetch |
| `POST /api/operator/hosted-sessions/{id}/refresh` | `RecentNodeOperatorSession` | Identomat KYC/KYB sessions now refresh (was 404) → `{ sessionId, status, providerKey }` |
| `GET /api/operator/hosted-sessions/reconciliation` | `RecentNodeOperatorSession` | adds attached Identomat sessions stalled in `AWAITING_*` (`status: "AwaitingProviderOutcome"`) beside `NeedsOperator` admissions |

(Sections below are filled as the lane closes.)
