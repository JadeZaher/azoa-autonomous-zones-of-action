# KYC Module — Specification

## Goal

Port ArdaNova's provider-agnostic KYC subsystem into AZOA as a **generic,
reusable identity-verification module** keyed to `Avatar`. ArdaNova offered the
code as a gift; this track lifts the *design* (the `IKycProviderService` seam, a
manual-review default provider, a Veriff adapter behind config, and a reusable
KYC gate) and re-expresses it in AZOA conventions: `Controllers → Managers →
Providers/Stores`, `AZOAResult<T>`, SurrealDB-only persistence, Avatar
identity, IDOR-safe Avatar-scoped routes.

**No ArdaNova branding may leak into AZOA.** Every namespace, type name,
string literal, URL, and user-facing message is genericized. The ArdaNova files
are *read-only reference sources for signatures and behaviour*, not text to copy
verbatim.

## Background

ArdaNova ships a clean KYC subsystem with a provider-agnostic seam. Its shape:

- **Service surface** — `IKycService`
  (`...\ArdaNova.Application\Services\Interfaces\IKycService.cs:6-14`):
  `SubmitAsync`, `GetByIdAsync`, `GetByUserIdAsync`, `GetPendingAsync`,
  `ApproveAsync`, `RejectAsync`, all returning `Result<T>`.
- **Provider seam** — `IKycProviderService`
  (`...\Services\Interfaces\IKycProviderService.cs:7-13`):
  `CreateSessionAsync`, `GetSessionStatusAsync`, `HandleWebhookAsync`,
  `ValidateDocumentsAsync`. This is the swap point between a manual reviewer and
  an external KYC vendor.
- **Gate** — `IKycGateService`
  (`...\Services\Interfaces\IKycGateService.cs:6-11`): `RequireProAsync`,
  `RequireVerifiedAsync`, `GetVerificationLevelAsync` — the access-control guard
  other services call before a sensitive op.
- **Default provider** — `ManualKycProviderService`
  (`...\Services\Implementations\ManualKycProviderService.cs:8-71`): no external
  session; `ValidateDocumentsAsync` enforces MIME allow-list + 10 MB cap; status
  is driven entirely by admin approve/reject.
- **Vendor adapter (stub)** — `VeriffKycProviderService`
  (`...\ArdaNova.Infrastructure\Kyc\VeriffKycProviderService.cs:13-37`): throws
  `NotImplementedException` until the integration is wired; only registered when
  the provider config selects it.
- **Domain** — `KycSubmission`
  (`...\ArdaNova.Domain\Models\Entities\KycSubmission.cs:11-62`) and
  `KycDocument` (`...\Entities\KycDocument.cs:11-43`), plus enums `KycStatus`
  (`...\Enums\KycStatus.cs:3-10`), `KycProvider` (`...\Enums\KycProvider.cs:3-7`),
  `KycDocumentType` (`...\Enums\KycDocumentType.cs:3-10`).
- **Settings** — `KycSettings`
  (`...\ArdaNova.Application\Settings\KycSettings.cs:3-9`): `Provider`,
  `VeriffApiKey`, `VeriffBaseUrl`, `SubmissionExpiryDays`.
- **Controller** — `KycController`
  (`...\ArdaNova.API\Controllers\KycController.cs:10-77`): submit / status /
  get-by-id / pending / approve / reject.

Two impedance mismatches drive the port and are the source of every decision in
`plan.md`:

1. **Result type.** ArdaNova returns `Result<T>` with a `ResultType` discriminator
   (`NotFound` / `ValidationError` / `Forbidden` / `Unauthorized`). AZOA uses
   `AZOAResult<T>` (`Models/Responses/AZOAResult.cs:38-85`) which carries only
   `IsError` + `Message` + `Result`. The Forbidden/NotFound distinction is
   preserved at the manager boundary using the **message-prefix discriminator
   pattern** already proven by STARODK (`Interfaces/Managers/ISTARManager.cs:12-19`,
   `STARODKAuthorizationError`), so the controller can translate to 403/404
   without leaking a new result shape.

2. **Persistence + identity.** ArdaNova persists via EF `IRepository<T>` +
   `IUnitOfWork` and keys KYC to `User.id` (string). AZOA persists via
   **SurrealDB only** through a hand-authored store seam
   (`Interfaces/Stores/ISTARStore.cs`, `Providers/Stores/Surreal/SurrealStarStore.cs`)
   and keys ownership to `Avatar` (`Persistence/SurrealDb/Models/Avatar.cs:20`).
   ArdaNova's `userId` → AZOA `AvatarId` throughout.

ArdaNova also auto-upgrades a `User` to a `VerificationLevel.PRO` enum on approval
(`KycService.cs:184-186`) and the gate reads that enum
(`KycGateService.cs:18-30`). AZOA `Avatar` has only a boolean `IsVerified`
(`Avatar.cs:69-72`) and no `VerificationLevel`. The AZOA gate therefore reads
**KYC submission status** (the most-recent APPROVED submission for the avatar)
rather than a denormalized level on the avatar — this avoids schema-changing the
shared `avatar` table from inside a feature module. See plan decision **D3**.

## Scope (what this track ports)

### 1. Domain model as SurrealDB POCOs

Two hand-authored POCOs under `Persistence/SurrealDb/Models/`, following the
`Holon.cs` / `Avatar.cs` pattern (`SurrealTable`, `Slice`, `Index`, `Id`,
`Column(Order=…)`, `JsonPropertyName`, `Assert`, `Default`; `ISurrealRecord`
with `SchemaNameConst`):

- `KycSubmission` — table `kyc_submission`, keyed by `AvatarId`
  (`References(typeof(Avatar), Optional=true)` like `Holon.cs:50-52`), with
  `Index("kyc_submission_avatar_id")` and `Index("kyc_submission_status")`
  (mirrors ArdaNova's `KycSubmission.cs` fields: `provider`, `status`,
  `reviewer_id`, `review_notes`, `rejection_reason`, `provider_session_id`,
  `provider_result`, `submitted_at`, `reviewed_at`, `expires_at`, `created_date`,
  `modified_date`).
- `KycDocument` — table `kyc_document`, FK `submission_id`, with
  `Index("kyc_document_submission_id")` (mirrors `KycDocument.cs`: `type`,
  `file_url`, `file_name`, `mime_type`, `file_size_bytes`, `metadata`,
  `created_date`).

Enums ported verbatim (values only — no ArdaNova namespace): `KycStatus`
(PENDING, IN_REVIEW, APPROVED, REJECTED, EXPIRED), `KycProvider` (MANUAL,
VERIFF), `KycDocumentType` (GOVERNMENT_ID, PASSPORT, DRIVERS_LICENSE, SELFIE,
PROOF_OF_ADDRESS). Enums persist as their string names via `JsonStringEnumConverter`
consistent with existing SurrealDB POCO conventions (decision **D4**).

A `SurrealKycStore` (`Providers/Stores/Surreal/`) implementing an `IKycStore`
seam (`Interfaces/Stores/IKycStore.cs`), mirroring `SurrealStarStore` —
`ToSurrealId` / `FromSurrealId`, `SurrealLink.ToLink("avatar", …)` for the
`avatar_id` link (`SurrealStarStore.cs:180,218`), inline-or-model-POCO mapping,
`UPSERT … CONTENT … RETURN AFTER`. Store surface (Avatar-scoped where it matters
for IDOR):

- `GetSubmissionByIdAsync(Guid id)`
- `GetLatestSubmissionByAvatarAsync(Guid avatarId)` — most-recent by `submitted_at`
- `GetActiveSubmissionByAvatarAsync(Guid avatarId)` — PENDING or IN_REVIEW
- `GetPendingAsync()` — admin queue
- `UpsertSubmissionAsync(KycSubmission)`
- `GetDocumentsBySubmissionAsync(Guid submissionId)`
- `AddDocumentsAsync(IEnumerable<KycDocument>)`

### 2. Provider-agnostic seam + providers

- `IKycProviderService` (`Interfaces/Providers/IKycProviderService.cs`) — the
  genericized port of ArdaNova's `IKycProviderService.cs:7-13`, returning
  `AZOAResult<T>`:
  - `CreateSessionAsync(Guid avatarId, IReadOnlyList<KycDocumentModel> documents)`
  - `GetSessionStatusAsync(string providerSessionId)`
  - `HandleWebhookAsync(string payload)`
  - `ValidateDocumentsAsync(IReadOnlyList<SubmitKycDocumentModel> documents)`
- `ManualKycProviderService` (`Providers/Kyc/ManualKycProviderService.cs`) — the
  **default**. Ports `ManualKycProviderService.cs:8-71` exactly in behaviour:
  no external session (returns the avatar id as a pseudo-session id), status
  PENDING until admin review, and the document validator with the same
  **genericized** MIME allow-list (`image/jpeg`, `image/png`, `image/gif`,
  `image/webp`, `image/bmp`, `application/pdf`) and 10 MB cap.
- `VeriffKycProviderService` (`Providers/Kyc/VeriffKycProviderService.cs`) — port
  of the stub (`VeriffKycProviderService.cs:13-37`). All four methods throw
  `NotImplementedException` with a **generic** message ("Veriff integration not
  yet configured. Set Kyc:Provider=manual to use the manual review provider.").
  Registered only when `Kyc:Provider == "veriff"`. **Veriff secrets are a
  deploy-stub** — see Scope item 6.

### 3. `KycManager` (AZOA naming)

`KycManager : IKycManager` (`Managers/KycManager.cs`,
`Interfaces/Managers/IKycManager.cs`), the genericized port of `KycService.cs`,
returning `AZOAResult<T>` and depending on `IKycStore` + `IKycProviderService`.
Methods (Avatar-scoped):

- `SubmitAsync(SubmitKycModel model, Guid avatarId)` — rejects when an active
  (PENDING/IN_REVIEW) submission already exists for the avatar (ports
  `KycService.cs:41-47`); validates documents via the provider; persists
  submission + documents; calls `CreateSessionAsync`; stamps
  `provider_session_id`.
- `GetStatusAsync(Guid avatarId)` — most-recent submission for the avatar (ports
  `GetByUserIdAsync`, `KycService.cs:120-133`).
- `GetByIdAsync(Guid submissionId, Guid avatarId)` — **IDOR-scoped**: loads by id
  then requires the loaded submission's `AvatarId == avatarId` (see Scope item 5),
  unlike ArdaNova's unscoped `GetByIdAsync` (`KycService.cs:108-118`).
- `ListDocumentsAsync(Guid submissionId, Guid avatarId)` — **IDOR-scoped** the
  same way.
- Admin surface: `GetPendingAsync()`, `ApproveAsync(Guid submissionId, Guid
  reviewerAvatarId, string? notes)`, `RejectAsync(Guid submissionId, Guid
  reviewerAvatarId, string? notes, string? rejectionReason)` — port
  `KycService.cs:165-218`. On approval the manager flips submission status to
  APPROVED and sets `Avatar.IsVerified = true` (decision **D3** — replaces
  ArdaNova's `UpdateVerificationLevelAsync(... PRO)` at `KycService.cs:184-186`).

DI registration in `Program.cs` alongside the existing manager/store block
(`Program.cs:290-291` stores, `:393` managers): `IKycStore → SurrealKycStore`,
`IKycManager → KycManager`, and `IKycProviderService → ManualKycProviderService`
**or** `VeriffKycProviderService` selected by `Kyc:Provider` config.

### 4. KYC gate (reusable guard for other managers)

- `IKycGateService` (`Interfaces/Managers/IKycGateService.cs`) — genericized port
  of `IKycGateService.cs:6-11`, returning `AZOAResult<bool>` /
  `AZOAResult<KycStatus>`:
  - `RequireVerifiedAsync(Guid avatarId)` — succeeds when the avatar has an
    APPROVED KYC submission; otherwise returns a **Forbidden-prefixed** error
    (mirrors the STARODK discriminator pattern,
    `ISTARManager.cs:12-19`) carrying a generic message ("KYC verification
    required. Complete identity verification to unlock this feature.").
  - `GetKycStatusAsync(Guid avatarId)` — the avatar's current effective status.
- `KycGateService : IKycGateService` (`Managers/KycGateService.cs`) — reads via
  `IKycStore` (ports `KycGateService.cs:18-52` but keyed off submission status,
  not a `VerificationLevel` enum — decision **D3**).
- **A reusable gate seam other AZOA managers call.** Provide a
  `KycAuthorizationError` static (Forbidden/NotFound prefixes, exactly like
  `STARODKAuthorizationError`, `ISTARManager.cs:12-19`) so a calling controller
  can translate the gate result to 403 without a new result type. The gate is
  injected into a consuming manager and invoked before the sensitive op.

### 5. `KycController` — Avatar-scoped with IDOR guards

`KycController : ControllerBase` (`Controllers/KycController.cs`), `[Authorize]`,
mirroring `STARODKController` (`Controllers/STARODKController.cs:14-118`):

- Reads the authenticated avatar via `GetAvatarIdFromClaims()` (copy the helper
  at `STARODKController.cs:113-118` — `NameIdentifier`/`sub` claim → `Guid`).
- `POST /api/kyc/submit` — submit for the authenticated avatar
  (`model.AvatarId`, if present on the body, is **ignored**; authenticated avatar
  is authoritative — the STARODK precedent at `STARManager.cs:62-66`).
- `GET /api/kyc/status` — status for the authenticated avatar (no `{avatarId}` in
  the route; the avatar comes from the token, closing ArdaNova's
  `GET /status/{userId}` IDOR at `KycController.cs:28-33`).
- `GET /api/kyc/{id:guid}` — get-by-id, scoped: 404/403 if the submission is not
  owned by the authenticated avatar.
- `GET /api/kyc/{id:guid}/documents` — documents for an owned submission only.
- Admin: `GET /api/kyc/pending`, `POST /api/kyc/{id:guid}/approve`,
  `POST /api/kyc/{id:guid}/reject` — gated by an admin policy/role (decision
  **D5**); `reviewerAvatarId` comes from the admin's token, not the body.
- A private `TranslateResult(...)` that maps the manager's message-prefix
  discriminator to 403/404/400, copied from `STARODKController.cs:100-111`.

### 6. Deploy-stub for Veriff secrets + config

- `KycSettings` (`Settings/KycSettings.cs`) ported from
  `KycSettings.cs:3-9`: `Provider` (default `"manual"`), `VeriffApiKey`
  (nullable, **never** committed), `VeriffBaseUrl` (default the public Veriff
  station API base), `SubmissionExpiryDays` (default 0 = never expires). Bound
  from the `Kyc` config section.
- `appsettings.json` gains a `Kyc` section with `Provider: "manual"` and **empty**
  Veriff secret placeholders.
- **`conductor/DEPLOY-STEPS-TODO.md`** (create if absent) gains a KYC entry:
  "Set `Kyc:Provider=veriff` and supply `Kyc:VeriffApiKey` / `Kyc:VeriffBaseUrl`
  from the deploy secret store before enabling Veriff. Manual provider needs no
  secrets." This is the **deploy-stub flag** — the Veriff path is wired but
  inert until those secrets are provisioned out-of-band.

### 7. No brand leak

Every `ArdaNova.*` namespace becomes `AZOA.WebAPI.*`. Every user-facing string,
URL, route, and comment that references ArdaNova, its product names, or its
`/settings/verification` path is genericized. A grep gate (Acceptance) proves
zero `ArdaNova` / `Karma`-style brand tokens in the new files.

## Acceptance criteria

- [ ] `KycSubmission` + `KycDocument` SurrealDB POCOs authored under
  `Persistence/SurrealDb/Models/`, following the `Holon.cs` attribute pattern,
  keyed to `AvatarId`, with the listed indexes; both implement `ISurrealRecord`.
- [ ] `KycStatus`, `KycProvider`, `KycDocumentType` enums ported (values only)
  under the AZOA namespace.
- [ ] `IKycStore` seam + `SurrealKycStore` implementation authored, mirroring
  `SurrealStarStore` id-encoding + `avatar` link conventions.
- [ ] `IKycProviderService` seam + `ManualKycProviderService` (default) +
  `VeriffKycProviderService` (stub) authored; provider selected by `Kyc:Provider`.
- [ ] `IKycManager` + `KycManager` authored, returning `AZOAResult<T>`, with the
  message-prefix discriminator for Forbidden/NotFound.
- [ ] `IKycGateService` + `KycGateService` authored with a `KycAuthorizationError`
  static; one consuming example documented (see cross-track seam below).
- [ ] `KycController` authored, `[Authorize]`, Avatar-scoped, IDOR-guarded
  (route + authenticated avatar; body `AvatarId` ignored); admin endpoints gated.
- [ ] All managers/providers/stores registered in `Program.cs` DI.
- [ ] `KycSettings` + `Kyc` config section + `appsettings.json` placeholders; Veriff
  secrets flagged as a deploy-stub in `conductor/DEPLOY-STEPS-TODO.md`.
- [ ] Unit tests for `KycManager`, `KycGateService`, and `ManualKycProviderService`
  (mirroring ArdaNova's `KycServiceTests` / `KycGateServiceTests` /
  `ManualKycProviderServiceTests` as a behavioural guide), plus IDOR tests on the
  get-by-id / documents paths. xUnit + FluentAssertions + Moq + Builder pattern
  (per `conductor/workflow.md` + project memory).
- [ ] `dotnet build` zero-warning green (nullable enabled — `workflow.md:18`).
- [ ] `dotnet test` green.
- [ ] Grep gate: **zero** `ArdaNova` (or other ArdaNova brand tokens) in any new
  AZOA file.
- [ ] `tracks.md` row for this track moves to `[x]` Shipped.

## Cross-track seam: wallet-generate / mint gating

`WalletManager` (`Managers/WalletManager.cs`) and `NftController` are owned by
other tracks. This track does **not** rewrite them. Instead it:

1. Ships the reusable `IKycGateService` + `KycAuthorizationError` so a future
   wallet/mint change can call `await _kycGate.RequireVerifiedAsync(avatarId)`
   and translate a Forbidden result to 403 before generating a wallet or minting.
2. Documents **one worked example** in `plan.md` (the exact call site shape and
   the controller translation) as the integration contract, so the owning track
   can wire it with a one-line guard. Actual wiring of `WalletManager` /
   `NftController` is **out of scope** here and noted as a follow-up seam.

## Out of scope

- Real Veriff API integration (session creation, webhook signature verification,
  status polling) — the adapter stays a deploy-gated stub.
- Live wiring of `WalletManager` / `NftController` KYC guards (cross-track seam;
  documented example only).
- A frontend verification UI / SDK methods (separate frontend track).
- Document upload/storage (the model stores a `file_url`; blob storage is out of
  scope — same posture as ArdaNova).
- Adding a `VerificationLevel` enum or any new column to the shared `avatar`
  table (decision **D3** keeps the gate reading submission status instead).
- Integration tests via the WebApplicationFactory host — blocked on the pending
  `integration-test-namespace-isolation` harness fix (project memory); unit
  coverage only for this track.

## Tier

Tier 1 — a new self-contained feature module. Independent of in-flight work; the
only external touchpoints are the documented (not executed) wallet/mint gating
seam.

## Dependencies

None hard. The module is self-contained behind its own store + provider + manager
seams. The wallet/mint **gating integration** touches `WalletManager` /
`NftController`, but those are deferred as a documented cross-track seam, so this
track can land independently.
