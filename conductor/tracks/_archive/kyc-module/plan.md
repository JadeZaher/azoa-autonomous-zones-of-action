# KYC Module — Plan

Source spec: [spec.md](spec.md)
Port source (read-only reference): ArdaNova KYC subsystem
(`...\ardanova-backend-api-mcp\api-server\src\`), offered as a gift. **No
ArdaNova branding may leak into AZOA.**

## Decisions to record before starting

Each decision below has two valid approaches. The chosen path is recorded inline;
revisit only if a phase surfaces a blocker.

| # | Decision | Resolution |
|---|----------|------------|
| **D1** | `Result<T>` → `AZOAResult<T>` mapping. ArdaNova's `Result<T>` carries a `ResultType` discriminator (`KycController.cs:68-74`); `AZOAResult<T>` (`Models/Responses/AZOAResult.cs:38-85`) has only `IsError`/`Message`. How is Forbidden vs NotFound vs ValidationError preserved? | **Message-prefix discriminator**, exactly as STARODK does (`Interfaces/Managers/ISTARManager.cs:12-19`, `STARODKAuthorizationError`). Add `KycAuthorizationError { Forbidden = "KYC_FORBIDDEN: ", NotFound = "KYC_NOT_FOUND: " }`. The controller's `TranslateResult` maps the prefix to 403/404 (copy `STARODKController.cs:100-111`). No new result shape, no SDK churn. |
| **D2** | Persistence seam. ArdaNova uses EF `IRepository<T>` + `IUnitOfWork` + AutoMapper (`KycService.cs:13-37`). | **Hand-authored SurrealDB store**, mirroring `SurrealStarStore` (`Providers/Stores/Surreal/SurrealStarStore.cs`). `IKycStore` is the seam; no AutoMapper (manual `ToPoco`/`FromPoco` like `SurrealStarStore.cs:161-227`). `avatar_id` stored as a SurrealDB record link via `SurrealLink.ToLink("avatar", …)` (`SurrealStarStore.cs:180`). Id encoding `Guid.ToString("N").ToLowerInvariant()` (`SurrealStarStore.cs:155-159`). |
| **D3** | Approval side-effect + gate source of truth. ArdaNova upgrades `User.verificationLevel = PRO` on approval (`KycService.cs:184-186`) and the gate reads that enum (`KycGateService.cs:18-30`). AZOA `Avatar` has only `IsVerified` bool (`Avatar.cs:69-72`), no `VerificationLevel`. | **Do NOT add a `VerificationLevel` enum/column to the shared `avatar` table from inside a feature module.** On approval, set `Avatar.IsVerified = true`. The **gate reads KYC submission status** (most-recent APPROVED submission for the avatar) via `IKycStore`, not a denormalized level. This keeps the `avatar` schema untouched and the gate authoritative against the KYC ledger. (If a multi-tier level is later needed, it becomes its own track.) |
| **D4** | Enum persistence. | **Persist enums as their string names** (`JsonStringEnumConverter`) to match the readable, value-name posture of the ported enums and keep DB rows human-legible. Verify against `Persistence/SurrealDb/CONVENTION.md` during Phase 1; if the convention mandates ints, switch — record the override here. |
| **D5** | Admin authorization for `pending`/`approve`/`reject`. ArdaNova's `KycController` is unauthenticated on these (`KycController.cs:42-61`). | **Require an admin policy/role** on the admin endpoints (`[Authorize(Policy = "Admin")]` or equivalent already in `Program.cs`). Confirm the existing admin policy name during Phase 5; if none exists, gate behind `[Authorize]` + a role claim check and note the gap as a follow-up. `reviewerAvatarId` always comes from the admin's token, never the body. |
| **D6** | Veriff adapter. | **Port the stub only** (`VeriffKycProviderService.cs:13-37`) — all four methods throw `NotImplementedException` with a **generic** message. Registered only when `Kyc:Provider == "veriff"`. Secrets are a deploy-stub (Phase 6). Real Veriff integration is explicitly out of scope. |

---

## Phase 1 — Domain model + enums + store seam

- [ ] **Enums** under `AZOA.WebAPI` namespace (values from `KycStatus.cs:3-10`,
  `KycProvider.cs:3-7`, `KycDocumentType.cs:3-10`): `KycStatus`, `KycProvider`,
  `KycDocumentType`. No `ArdaNova` namespace.
- [ ] **`KycSubmission` POCO** — `Persistence/SurrealDb/Models/KycSubmission.cs`,
  table `kyc_submission`, following `Holon.cs:15-95` attribute layout
  (`[SurrealTable]`, `[Slice("identity")]`, `[Index]`, `[Id]`,
  `[Column(Order=…)]`, `[JsonPropertyName]`, `[Assert]`, `ISurrealRecord` +
  `SchemaNameConst`). Fields mirror `KycSubmission.cs:14-51`: `avatar_id`
  (`References(typeof(Avatar), Optional=true)` — `Holon.cs:50-52`), `provider`,
  `status`, `reviewer_id`, `review_notes`, `rejection_reason`,
  `provider_session_id`, `provider_result`, `submitted_at`, `reviewed_at`,
  `expires_at`, `created_date`, `modified_date`. Indexes:
  `kyc_submission_avatar_id`, `kyc_submission_status`. Drop EF navigation
  properties (`KycSubmission.cs:52-60`) — not persisted, same as `Holon.cs:18`.
- [ ] **`KycDocument` POCO** — `Persistence/SurrealDb/Models/KycDocument.cs`,
  table `kyc_document`. Fields mirror `KycDocument.cs:14-38`: `submission_id`,
  `type`, `file_url`, `file_name`, `mime_type`, `file_size_bytes`, `metadata`,
  `created_date`. Index: `kyc_document_submission_id`.
- [ ] **`IKycStore`** — `Interfaces/Stores/IKycStore.cs`, mirroring
  `ISTARStore.cs:6-27` doc-comment style. Methods per spec Scope §1
  (`GetSubmissionByIdAsync`, `GetLatestSubmissionByAvatarAsync`,
  `GetActiveSubmissionByAvatarAsync`, `GetPendingAsync`, `UpsertSubmissionAsync`,
  `GetDocumentsBySubmissionAsync`, `AddDocumentsAsync`), all `AZOAResult<T>`.
- [ ] **`SurrealKycStore`** — `Providers/Stores/Surreal/SurrealKycStore.cs`,
  mirroring `SurrealStarStore.cs`: `ISurrealExecutor` ctor, `try/catch` +
  `CaptureException` per method (`SurrealStarStore.cs:47-50`), `ToSurrealId`/
  `FromSurrealId`, `SurrealLink.ToLink("avatar", …)` for `avatar_id`, manual
  `ToPoco`/`FromPoco`, `UPSERT type::record(...) CONTENT ... RETURN AFTER`
  (`SurrealStarStore.cs:112-119`). Owner-scoped active-submission query mirrors
  the name+avatar lookup at `SurrealStarStore.cs:61-66`.

## Phase 2 — Provider seam + providers

- [ ] **Models** for the seam (`Models/` or `Models/Requests/`):
  `SubmitKycModel`, `SubmitKycDocumentModel`, `KycDocumentModel`,
  `KycSubmissionModel` (the AZOA-side DTOs replacing ArdaNova's
  `SubmitKycDto`/`KycDocumentDto`/`KycSubmissionDto`). `AvatarId` (Guid)
  replaces `UserId` (string) throughout.
- [ ] **`IKycProviderService`** — `Interfaces/Providers/IKycProviderService.cs`,
  genericized port of `IKycProviderService.cs:7-13` → `AZOAResult<T>`:
  `CreateSessionAsync(Guid avatarId, IReadOnlyList<KycDocumentModel>)`,
  `GetSessionStatusAsync(string providerSessionId)`,
  `HandleWebhookAsync(string payload)`,
  `ValidateDocumentsAsync(IReadOnlyList<SubmitKycDocumentModel>)`.
- [ ] **`ManualKycProviderService`** — `Providers/Kyc/ManualKycProviderService.cs`,
  port of `ManualKycProviderService.cs:8-71`. Same MIME allow-list
  (`ManualKycProviderService.cs:10-18`) + 10 MB cap (`:20`) + validation rules
  (`:43-70`). `CreateSessionAsync` returns the avatar id as a pseudo-session id
  (`:22-28`). No external session, status PENDING until admin review.
- [ ] **`VeriffKycProviderService`** — `Providers/Kyc/VeriffKycProviderService.cs`,
  port of the stub (`VeriffKycProviderService.cs:13-37`). All methods throw
  `NotImplementedException` with a **generic** message (no ArdaNova/Veriff
  onboarding URLs). XML doc-comment notes it is config-gated.

## Phase 3 — KycManager

- [ ] **`KycAuthorizationError`** static (Forbidden/NotFound prefixes) — colocate
  with `IKycManager` like `STARODKAuthorizationError` in `ISTARManager.cs:12-19`.
- [ ] **`IKycManager`** — `Interfaces/Managers/IKycManager.cs`. Methods per spec
  Scope §3, all `AZOAResult<T>`, Avatar-scoped.
- [ ] **`KycManager`** — `Managers/KycManager.cs`, depending on `IKycStore`,
  `IKycProviderService`, `IAvatarStore` (for the `IsVerified` flip on approval).
  Port behaviour from `KycService.cs`:
  - `SubmitAsync` — active-submission guard (`KycService.cs:41-47`) → provider
    `ValidateDocumentsAsync` (`:50-52`) → persist submission + documents
    (`:54-88`) → `CreateSessionAsync` + stamp `provider_session_id` (`:90-97`).
  - `GetStatusAsync(avatarId)` — latest submission (`:120-133`).
  - `GetByIdAsync(submissionId, avatarId)` — **IDOR-scoped**: load by id, then
    require `submission.AvatarId == avatarId` or return
    `KycAuthorizationError.Forbidden` (the STARODK ownership check,
    `STARManager.cs:51-52,71-72`). ArdaNova's `GetByIdAsync` (`:108-118`) is
    unscoped — this is the deliberate hardening.
  - `ListDocumentsAsync(submissionId, avatarId)` — same IDOR scoping.
  - `GetPendingAsync` (`:135-151`), `ApproveAsync` (`:165-192`), `RejectAsync`
    (`:194-218`). On approve: status → APPROVED, then `Avatar.IsVerified = true`
    via `IAvatarStore` (**D3** — replaces `UpdateVerificationLevelAsync(PRO)` at
    `:184-186`). `reviewerAvatarId` is the param, never body-supplied.

## Phase 4 — KYC gate

- [ ] **`IKycGateService`** — `Interfaces/Managers/IKycGateService.cs`,
  genericized port of `IKycGateService.cs:6-11` → `AZOAResult<bool>` /
  `AZOAResult<KycStatus>`: `RequireVerifiedAsync(Guid avatarId)`,
  `GetKycStatusAsync(Guid avatarId)`. (ArdaNova's `RequireProAsync` collapses to
  `RequireVerifiedAsync` under **D3** — single APPROVED gate, no PRO tier.)
- [ ] **`KycGateService`** — `Managers/KycGateService.cs`, reads `IKycStore`.
  `RequireVerifiedAsync` succeeds iff the avatar has an APPROVED submission;
  otherwise returns `KycAuthorizationError.Forbidden + "<generic message>"`
  (ports `KycGateService.cs:18-43` but keyed on submission status, with the
  ArdaNova `/settings/verification` URL **removed**).
- [ ] **Cross-track seam example (documented, not executed).** Record the exact
  integration contract for the owning wallet/mint track:

  ```csharp
  // In e.g. WalletManager.GenerateWalletAsync(... Guid avatarId ...):
  var gate = await _kycGate.RequireVerifiedAsync(avatarId);
  if (gate.IsError) return Fail<...>(gate.Message); // carries KYC_FORBIDDEN: prefix

  // In the controller (copy STARODKController.cs:100-111):
  //   message starts with KycAuthorizationError.Forbidden -> 403
  ```

  This is the *only* contact point with `WalletManager` / `NftController`; their
  actual edit is out of scope (spec "Cross-track seam").

## Phase 5 — Controller

- [ ] **`KycController`** — `Controllers/KycController.cs`, `[ApiController]`,
  `[Route("api/[controller]")]`, `[Authorize]`, mirroring `STARODKController.cs`.
  Endpoints per spec Scope §5. Copy `GetAvatarIdFromClaims()`
  (`STARODKController.cs:113-118`) and `TranslateResult` (`:100-111`).
  - `POST submit`, `GET status`, `GET {id:guid}`, `GET {id:guid}/documents` —
    Avatar-scoped; body `AvatarId` ignored (STARODK precedent,
    `STARManager.cs:62-66`); no `{avatarId}` in any route (closes ArdaNova's
    `GET /status/{userId}` IDOR, `KycController.cs:28-33`).
  - `GET pending`, `POST {id:guid}/approve`, `POST {id:guid}/reject` — admin-gated
    per **D5**; `reviewerAvatarId` from the admin token.

## Phase 6 — Config + deploy-stub

- [ ] **`KycSettings`** — `Settings/KycSettings.cs`, port of `KycSettings.cs:3-9`:
  `Provider` (default `"manual"`), `VeriffApiKey` (nullable), `VeriffBaseUrl`
  (default public Veriff station base), `SubmissionExpiryDays` (default 0).
- [ ] **`appsettings.json`** — add a `Kyc` section: `"Provider": "manual"`, empty
  Veriff secret placeholders. Bind `KycSettings` in `Program.cs`.
- [ ] **`Program.cs` DI** — alongside the existing store/manager block
  (`Program.cs:290-291`, `:393`): `IKycStore → SurrealKycStore`,
  `IKycManager → KycManager`, `IKycGateService → KycGateService`, and
  `IKycProviderService →` Manual **or** Veriff selected by `Kyc:Provider`.
- [ ] **`conductor/DEPLOY-STEPS-TODO.md`** (create if absent) — add the KYC
  deploy-stub entry: provision `Kyc:VeriffApiKey` / `Kyc:VeriffBaseUrl` and set
  `Kyc:Provider=veriff` from the deploy secret store before enabling Veriff;
  manual provider needs no secrets.

## Phase 7 — Tests

xUnit + FluentAssertions + Moq + Builder pattern
(`IntegrationTests/Builders/TestDataBuilders.cs`, per `conductor/workflow.md` +
project memory). Mirror the ArdaNova test files as behavioural guides:

- [ ] **`KycManagerTests`** (guide: `KycServiceTests.cs:15-646`) — submit
  happy-path + active-submission rejection + document-validation failure;
  get-status latest-wins; approve → IsVerified flip + status transition; reject
  with reason; approve/reject on already-terminal status → validation error;
  not-found paths. **Plus IDOR tests** (no ArdaNova analogue):
  `GetByIdAsync_DifferentAvatar_ReturnsForbidden`,
  `ListDocumentsAsync_DifferentAvatar_ReturnsForbidden`.
- [ ] **`KycGateServiceTests`** (guide: `KycGateServiceTests.cs:11-239`) —
  `RequireVerifiedAsync` success when an APPROVED submission exists; Forbidden
  (with the generic message + `KYC_FORBIDDEN:` prefix) when none/last is
  REJECTED/PENDING; not-found when the avatar has no submission;
  `GetKycStatusAsync` returns the current effective status.
- [ ] **`ManualKycProviderServiceTests`** (guide: ArdaNova
  `ManualKycProviderServiceTests`) — `ValidateDocumentsAsync`: empty list →
  error; missing file URL → error; disallowed MIME → error; oversize (>10 MB) →
  error; valid set → success. `CreateSessionAsync` returns the avatar id;
  `GetSessionStatusAsync`/`HandleWebhookAsync` return PENDING.

## Phase 8 — Verification gates

- [ ] `dotnet build` — **zero warnings** (nullable enabled, `workflow.md:18`).
- [ ] `dotnet test` — green.
- [ ] Swagger UI lists the new `/api/kyc/*` endpoints (`workflow.md:20`).
- [ ] **Brand-leak grep gate**: `grep -ri "ardanova" <new files>` → **no hits**.
  Also confirm no leaked `/settings/verification` URL or ArdaNova product strings.
- [ ] All new managers/providers/stores resolve from DI at startup (no missing
  registration).
- [ ] Move `tracks.md` row for `kyc-module` from Pending to `[x]` Shipped with a
  one-line summary (format per `tracks.md:33`).

## Known follow-ups (filed separately)

- **wallet/mint KYC wiring**: the documented gate seam (Phase 4) must be wired
  into `WalletManager.GenerateWalletAsync` and the mint endpoint on `NftController`
  by their owning track — one-line guard + controller 403 translation. Out of
  scope here.
- **Veriff real integration**: session creation, webhook signature verification,
  status polling — promote the stub to a real adapter once Veriff secrets are
  provisioned (deploy-stub in `DEPLOY-STEPS-TODO.md`).
- **KYC integration tests** via WebApplicationFactory — blocked on the pending
  `integration-test-namespace-isolation` harness fix (project memory); add once
  the per-test SurrealDB namespace reaches the WebAPI executor.

## Commit strategy

One commit per phase, `[kyc-module] <imperative verb> <subject>`
(`workflow.md:9-15`), e.g. `[kyc-module] add KycSubmission + KycDocument SurrealDB POCOs`,
`[kyc-module] add provider-agnostic IKycProviderService seam + manual provider`,
`[kyc-module] add KycManager with IDOR-scoped get-by-id`,
`[kyc-module] add reusable KYC gate + KycAuthorizationError`,
`[kyc-module] add Avatar-scoped KycController`,
`[kyc-module] flag Veriff secrets as deploy-stub`,
`[kyc-module] add KycManager + gate + manual-provider unit tests`. Each commit
builds zero-warning green.
