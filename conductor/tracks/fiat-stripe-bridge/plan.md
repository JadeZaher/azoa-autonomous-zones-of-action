# Fiat Stripe Bridge — Plan

Source spec: [spec.md](spec.md)
Integration contract (produced by Phase 3): [docs/INTEGRATION-CONTRACT.md](docs/INTEGRATION-CONTRACT.md)

## Decisions to record before starting

The following require a choice between valid approaches. Record the decision
here (replace the `[ decision ]` marker) before executing the corresponding
phase.

| # | Decision required | Recommendation |
|---|-------------------|----------------|
| D1 — Allocation surface | Expose a **new dedicated allocation endpoint** vs. reuse the existing `NftController.Mint`/`Transfer` (`Controllers/NftController.cs:42,54`) directly. | **New dedicated endpoint** (`POST /api/allocation` or `POST /api/wallet/{avatarId}/allocate`) so the idempotency + KYC + provision-if-absent composition lives in one place. It internally calls `GenerateWalletAsync` (`WalletManager.cs:218`) then the existing mint/transfer surface (`INftManager.cs:10-11`). The raw NFT endpoints stay as-is. `[ decision ]` |
| D2 — Idempotency store | Where the idempotency record lives: reuse the faucet's deterministic content-key approach (`WalletManager.cs:386-392`, no persistence) vs. a **persisted allocation-idempotency record** keyed by `(tenantApiKeyId, idempotencyKey)`. | **Persisted record.** The faucet's content-addressed key is dedup-safe for a pure faucet dispense, but a value-bearing allocation needs the *original result* returned on replay, which requires storing it. Key on `(ApiKeyId from claims, Idempotency-Key header)`. `[ decision ]` |
| D3 — KYC fail-closed scope | Does pre-KYC wallet **provisioning** (zero-balance) get blocked, or only the value-bearing **allocation**? | Per `kyc-module` policy. Default: **provisioning allowed pre-KYC, allocation blocked** until approved — so a wallet address can exist before funds move. Confirm against `kyc-module` before coding. `[ decision ]` |
| D4 — Asset descriptor | What "allocate N of asset A" means on-chain: NFT mint (`MintAsync`) vs. fungible transfer vs. both behind one descriptor. | A single request DTO with an asset-kind discriminator that dispatches to `MintAsync`/`TransferAsync` (`INftManager.cs:10-11`) or the fungible equivalent from `signing-core-keystone`. `[ decision ]` |

---

## Phase 0 — Confirm dependency surfaces (no code)

- [ ] Confirm `kyc-module` exposes a status-resolution call usable from a
  manager (sync/async, return shape, "approved" predicate). Record the exact
  symbol here once `kyc-module` lands.
- [ ] Confirm `signing-core-keystone` gives a real (non-stub) transfer/mint path
  for the chosen asset kind (D4). If it is still stubbed, the allocation
  side-effect is stubbed and the idempotency/KYC tests still stand.
- [ ] Confirm `tenant-onboarding` issues an `X-Api-Key` with a scope claim that
  authorises allocation; record the scope string. The handler already surfaces
  scope claims (`Core/ApiKeyAuthenticationHandler.cs:81-87`).
- [ ] Confirm `conductor/DEPLOY-STEPS-TODO.md` exists (create it if not) and add
  the `AZOA_TENANT_API_KEY` deploy-stub row. No secret value committed.

## Phase 1 — Allocation primitive (manager)

- [ ] Add `IAllocationManager` (or extend `IWalletManager`) with
  `AllocateAsync(Guid avatarId, AllocationRequest request, Guid callerAvatarId,
  string? clientIdempotencyKey)`. Signature mirrors
  `WalletManager.TopUpAsync(…, clientIdempotencyKey)`
  (`Managers/WalletManager.cs:351`).
- [ ] **Provision-if-absent.** Look up the avatar's wallet for the target chain;
  if none, call `GenerateWalletAsync` (`WalletManager.cs:218-255`). Reuse the
  existing wallet otherwise. Never create a duplicate (uniqueness already guarded
  at `WalletManager.cs:226-231`).
- [ ] **KYC gate (fail-closed).** Before any value-bearing side effect, resolve
  KYC via `kyc-module`; deny on unknown/pending/rejected. Per D3, provisioning
  may precede approval but allocation may not.
- [ ] **Idempotency.** Read/write the persisted allocation-idempotency record
  keyed on `(ApiKeyId, idempotencyKey)` (D2). On a hit, return the stored result
  and perform **no** second mint/transfer. On a miss, execute then persist.
  Absent key ⇒ deterministic content key over `(avatarId, asset, amount)`,
  mirroring the faucet's "never a random per-request key" rule
  (`WalletManager.cs:386-392`).
- [ ] **IDOR resistance.** The allocation target is the route/contract
  `avatarId`; any owner id on the request body is ignored (STARODK precedent in
  project memory). Caller authority comes from the API-key scope, not the body.
- [ ] **Execute allocation** via `INftManager.MintAsync`/`TransferAsync`
  (`Interfaces/Managers/INftManager.cs:10-11`) or the fungible equivalent (D4).
- [ ] Return `AZOAResult<…>` consistent with existing managers (no bare
  objects).

## Phase 2 — Controller endpoint

- [ ] Add the allocation endpoint (D1) on a controller gated by `[Authorize]`
  so the multi-scheme policy admits the `X-Api-Key` principal
  (`ApiKeyAuthenticationHandler.cs`). Resolve the caller via
  `GetAvatarIdFromClaims` exactly like `NftController.cs:117-122` /
  `WalletController.cs:211-216`.
- [ ] Read the `Idempotency-Key` header with the same helper shape as
  `WalletController.ReadIdempotencyKey` (`Controllers/WalletController.cs:176-185`)
  — client key wins, blank ⇒ null ⇒ server deterministic fallback.
- [ ] Apply `[EnableRateLimiting("financial")]` as on
  `WalletController.cs:153` (value-bearing endpoint).
- [ ] Map manager `AZOAResult` to `Ok`/`BadRequest`/`Unauthorized` in the
  house pattern (cf. `WalletController.cs:166-168`).
- [ ] Confirm no Stripe SDK / `Stripe:*` config key is referenced anywhere in
  the new code.

## Phase 3 — Integration contract doc

- [ ] Author `docs/INTEGRATION-CONTRACT.md`: the ordered calls the tenant's
  `HandlePaymentSucceededAsync` (`StripeService.cs:145`) makes **after** writing
  its `ProjectInvestment` (`StripeService.cs:235-249`):
  1. (optional) ensure wallet — or rely on provision-if-absent inside allocate;
  2. `POST` allocation with `X-Api-Key: <tenant key>` and
     `Idempotency-Key: <stable per-payment key, e.g. the PaymentIntent id>`.
- [ ] Include request/response JSON shapes and a **prose sequence diagram**:
  fiat settles on tenant → tenant writes investment + decides amount → tenant
  calls AZOA allocate (idempotent, KYC-gated) → AZOA provisions wallet if
  absent + mints/transfers → AZOA returns result → tenant records the AZOA
  reference.
- [ ] State explicitly that **token economics, treasury, and Gate evaluation
  remain in the tenant** (`StripeService.cs:181-258`) and that AZOA receives an
  already-decided amount.
- [ ] Refer to the caller only as "the fiat-settlement tenant" — **no brand
  name** anywhere in the doc.

## Phase 4 — Tests

- [ ] **Idempotency.** Duplicate `AllocateAsync` with the same
  `(ApiKeyId, idempotencyKey)` ⇒ second call returns the first result and the
  mint/transfer mock is invoked **exactly once**. (xUnit + Moq + FluentAssertions,
  Builder pattern per `IntegrationTests/Builders/TestDataBuilders.cs`.)
- [ ] **KYC fail-closed.** Target avatar with pending/rejected/unknown KYC ⇒
  allocation rejected, mint/transfer mock **never** invoked.
- [ ] **Provision-if-absent.** No existing wallet ⇒ `GenerateWalletAsync` path
  taken once; existing wallet ⇒ reused, not duplicated.
- [ ] **IDOR.** Body-supplied owner id different from the contract `avatarId` is
  ignored; allocation targets the authorised avatar only.
- [ ] (Optional, once the harness namespace-isolation follow-up lands)
  integration test through the WebAPI host with an `X-Api-Key` principal.

## Phase 5 — Verification gates

- [ ] `dotnet build` — **0 warnings** (nullable enabled, per `workflow.md`
  Quality Gate 1).
- [ ] `dotnet test` — green, including the four new unit tests above.
- [ ] Swagger lists the new allocation endpoint.
- [ ] Grep the AZOA solution for `Stripe` — zero hits in `src`/controllers/
  managers/config (only this track's `conductor/` docs may name it, and only as
  the trigger source).
- [ ] Grep the AZOA solution for the tenant brand name — **zero hits**.
- [ ] Grep for `Stripe:SecretKey` / `Stripe:WebhookSecret` — **zero hits**;
  confirm `AZOA_TENANT_API_KEY` is a deploy-stub in
  `conductor/DEPLOY-STEPS-TODO.md`, not a committed value.
- [ ] Move `tracks.md` row for `fiat-stripe-bridge` to `[x]` Shipped.

## Commit strategy

One commit per phase, house convention `[fiat-stripe-bridge] <imperative verb>`:

- `[fiat-stripe-bridge] add idempotent KYC-gated allocation manager`
- `[fiat-stripe-bridge] expose tenant-callable allocation endpoint`
- `[fiat-stripe-bridge] document tenant→AZOA allocation contract`
- `[fiat-stripe-bridge] add replay + KYC-fail-closed unit tests`

## Known follow-ups (filed separately)

- If the asset-allocation amounts ever need on-chain settlement guarantees
  beyond mint/transfer (multi-step atomicity), that is bridge/saga territory
  (`durable-saga-orchestration`), not this track.
- Per-tenant scope granularity (allocate vs. provision-only API keys) belongs to
  `tenant-onboarding` once scope strings are finalised.
