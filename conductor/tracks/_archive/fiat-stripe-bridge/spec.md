# Fiat Stripe Bridge — Specification

## Goal

Expose the **thin AZOA-side seam** that a fiat-settlement tenant calls *after*
money has already cleared on its own platform: "provision a custodial wallet for
avatar X if one is absent, and/or allocate N units of asset A into avatar X's
custodial wallet." AZOA contributes the **idempotent, KYC-gated,
tenant-callable wallet-provisioning + asset-allocation primitive** and a
**documented call contract**. AZOA does not run the fiat checkout, the token
economics, or the payout flow.

This is a blockchain-primitives track, not a payments track. The word "Stripe"
appears here only to name where the trigger comes *from*; no Stripe SDK, no
Stripe secret, and no Stripe webhook handler is added to AZOA by this track
(see §Stripe boundary).

## Background

The fiat economic flow lives entirely in the tenant platform. For evidence of
the shape of that flow (read-only reference — **do not port these into AZOA**):

- `IStripeService.CreateCheckoutSessionAsync` / `HandlePaymentSucceededAsync`
  (`…/Application/Services/Interfaces/IStripeService.cs:16,26`) — checkout +
  payment-succeeded webhook handling.
- `StripeService.HandlePaymentSucceededAsync`
  (`…/Application/Services/Implementations/StripeService.cs:145-268`) — the
  tenant, on `payment_intent.succeeded`, retrieves the PaymentIntent
  (`:152-153`), reads metadata (`:159-170`), allocates tokens
  (`AllocateToInvestorAsync`, `:192`), credits the token balance
  (`CreditAsync`, `:204`), processes the treasury 55/30/15 split
  (`ProcessFundingInflowAsync`, `:218`), writes the `ProjectInvestment`
  (`:235-249`), and evaluates Gate 1 (`EvaluateGate1Async`, `:252`).
- `StripeWebhookController.Webhook`
  (`…/API/Controllers/StripeWebhookController.cs:34-132`) — signature
  verification with `Stripe:WebhookSecret` (`:44-62`) and event dispatch.
- Payout/Connect (`CreateConnectedAccountAsync`, `CreatePayoutTransferAsync`,
  `HandlePayoutSucceededAsync`, `HandlePayoutFailedAsync` —
  `IStripeService.cs:44,52,62,70`; `StripeService.cs:294-490`).

**Division of responsibility (the load-bearing decision for this track):** the
dual-gate token model, project-token allocation, treasury, Gate-1/Gate-2
evaluation, ARDA, Stripe Checkout, Stripe Connect, and all Stripe webhook
handling **stay in the tenant platform**. The tenant's
`HandlePaymentSucceededAsync` — *after* it has created its own investment record
and decided the allocation amount — makes one or two HTTP calls into AZOA to
materialise the wallet and move the on-chain/custodial asset. That cross-system
call is the entire surface of this track.

AZOA already has every primitive this needs; the work is to make them safely
**tenant-callable**, **idempotent**, and **KYC-gated**, and to write the
contract down.

### Existing AZOA primitives this track binds together (file:line evidence)

- **Tenant identity via API key.** `ApiKeyAuthenticationHandler`
  (`Core/ApiKeyAuthenticationHandler.cs:28-94`) validates `X-Api-Key`
  (`:14,30`) and emits the *same* claims as JWT — `NameIdentifier` / `sub` /
  `AvatarId` plus per-key `scope` claims (`:72-87`). A tenant authenticates as
  an avatar-scoped principal; controllers cannot tell JWT from API key, which is
  exactly what lets the existing wallet/NFT controllers serve a tenant caller
  unchanged.
- **Wallet provisioning.** `WalletManager.GenerateWalletAsync`
  (`Managers/WalletManager.cs:218-255`) generates a custodial
  (`WalletType.Platform`) keypair, enforces address uniqueness (`:226-231`), and
  encrypts the private key/seed at rest (`:242-243`).
- **Asset allocation surface.** `NftController.Mint`
  (`Controllers/NftController.cs:42-52`) and `NftController.Transfer`
  (`:54-64`) → `INftManager.MintAsync` / `TransferAsync`
  (`Interfaces/Managers/INftManager.cs:10-11`). Both already resolve the caller
  from claims (`GetAvatarIdFromClaims`, `NftController.cs:117-122`).
- **The idempotency pattern to mirror.** `WalletController.TopUp`
  (`Controllers/WalletController.cs:152-169`) reads an optional
  `Idempotency-Key` header (`ReadIdempotencyKey`, `:176-185`) and threads it to
  `WalletManager.TopUpAsync(…, clientIdempotencyKey)`
  (`Managers/WalletManager.cs:351,390-392`). The contract is explicit: a
  client-supplied key wins; absence falls back to a **deterministic
  content-addressed key**, and **no random per-request key is ever generated**
  (`WalletManager.cs:386-392`, `WalletController.cs:160-163`). This is the exact
  semantics the allocation primitive must adopt.

## Why idempotency is a hard requirement here

`bridge-unsafe-pre-launch` (project memory) records that the cross-chain bridge
shipped with **no idempotency, replay protection, or atomicity** and was marked
Tier 0 "unsafe before any value flows." A fiat-triggered allocation is a
value-flow on a webhook-driven trigger — exactly the failure surface that bit
the bridge. Stripe (and any at-least-once webhook source) **will** redeliver
`payment_intent.succeeded`; the tenant's `HandlePaymentSucceededAsync`
(`StripeService.cs:145`) can itself be retried by the tenant's own webhook
controller (`StripeWebhookController.cs:73`). Therefore the AZOA allocation
primitive **must** dedupe on the tenant's idempotency key so a redelivered
webhook never double-mints / double-transfers. This is a correctness
requirement, not a nicety. We do not repeat the bridge's mistake.

## Stripe boundary (no AZOA-side Stripe secrets)

Because the economic flow stays in the tenant platform, **AZOA holds no Stripe
secrets.** Specifically:

- AZOA does **not** add `Stripe:SecretKey` (cf. tenant
  `StripeService.cs:67`).
- AZOA does **not** add `Stripe:WebhookSecret` or a webhook controller (cf.
  tenant `StripeWebhookController.cs:44-62`). Webhook signature verification is
  the tenant's job; AZOA trusts the tenant via the API key, not via a Stripe
  signature.
- The only credential AZOA introduces is the **tenant's AZOA API key**, which
  is a deploy-time secret already modelled by the API-key infrastructure
  (`Models/ApiKey.cs`, SHA-256 hashed at rest). Its provisioning is a
  deploy-stub: record it in `conductor/DEPLOY-STEPS-TODO.md` (create this file
  as the canonical deploy-stub registry if absent) as
  `AZOA_TENANT_API_KEY` — never committed.

If a later track genuinely needs an AZOA-side Stripe touchpoint (none is in
scope here), its secret must be flagged as a deploy-stub in the same registry,
never inlined.

## Scope

### In scope

1. **An idempotent, KYC-gated asset-allocation primitive** callable by a tenant
   API key. It accepts: target `AvatarId`, asset descriptor (chain + asset id /
   NFT mint request), amount, and the tenant's idempotency key (from the
   `Idempotency-Key` header, mirroring `WalletController.cs:164`). It:
   - provisions a custodial wallet for the avatar **if absent** (reusing
     `GenerateWalletAsync`, `WalletManager.cs:218`), returning the existing one
     otherwise;
   - performs the allocation via the existing mint/transfer surface
     (`INftManager.MintAsync` / `TransferAsync`,
     `INftManager.cs:10-11`, or the asset equivalent);
   - **dedupes** on the idempotency key so a replayed call returns the original
     result without re-executing the on-chain/custodial side effect;
   - **rejects** the call when the target avatar's KYC status is not approved.
2. **KYC gating** wired to the `kyc-module` dependency. The primitive must fail
   closed: unknown / pending / rejected KYC ⇒ allocation denied, no wallet side
   effect that constitutes value transfer. (Wallet *provisioning* with zero
   balance may be permitted pre-KYC if `kyc-module` allows it; the
   value-bearing allocation must not.)
3. **A documented integration contract** (`docs/INTEGRATION-CONTRACT.md` under
   this track): the exact ordered AZOA API calls the tenant's
   `HandlePaymentSucceededAsync` (`StripeService.cs:145`) should make *after*
   creating its `ProjectInvestment` (`StripeService.cs:235-249`), including the
   `X-Api-Key` and `Idempotency-Key` headers, request/response shapes, and the
   prose sequence diagram (fiat-settles-on-tenant → wallet/asset-on-AZOA).
4. **Replay-safety unit tests** proving the primitive is idempotent under a
   duplicate idempotency key and rejected when KYC is not approved.

### Out of scope

- Stripe Checkout, PaymentIntent retrieval, webhook signature verification,
  metadata parsing — all stay in the tenant (`StripeService.cs:145-268`,
  `StripeWebhookController.cs:34-132`).
- Token economics: allocation math, balance credit, treasury split, Gate-1/2
  (`StripeService.cs:181-258`). AZOA receives an *already-decided* amount.
- Stripe Connect / payout / `CreateConnectedAccountAsync` /
  `CreatePayoutTransferAsync` / `HandlePayout*` (`StripeService.cs:294-490`).
- Any Stripe SDK dependency or Stripe secret in AZOA (see §Stripe boundary).
- The frontend — no UI is added by this track.
- Real signing / submission semantics beyond what `signing-core-keystone`
  provides (this track consumes that primitive; it does not build it).

## Acceptance criteria

- [ ] An asset-allocation primitive (manager method + controller endpoint)
  exists that is callable by an `X-Api-Key` tenant principal and reads the
  `Idempotency-Key` header exactly as `WalletController.cs:176-185` does.
- [ ] The primitive provisions a custodial wallet via `GenerateWalletAsync`
  (`WalletManager.cs:218`) when the avatar has none for the target chain, and
  reuses the existing wallet otherwise.
- [ ] The primitive dedupes on the idempotency key: a duplicate call with the
  same key returns the first call's result and performs **no** second
  mint/transfer side effect. Unit test proves this.
- [ ] The primitive is KYC-gated and **fails closed**: a target avatar whose KYC
  is not approved is rejected with no value-bearing side effect. Unit test
  proves this.
- [ ] No client-supplied body field can redirect the allocation to a different
  avatar than the route/contract specifies (IDOR-resistant, mirroring the
  STARODK precedent in project memory) — caller-supplied owner ids on the body
  are ignored.
- [ ] `docs/INTEGRATION-CONTRACT.md` documents the ordered AZOA calls the
  tenant's `HandlePaymentSucceededAsync` makes after creating its
  `ProjectInvestment`, with headers, request/response shapes, and a prose
  sequence diagram.
- [ ] No Stripe secret (`Stripe:SecretKey`, `Stripe:WebhookSecret`) and no
  Stripe SDK reference is introduced into the AZOA solution. The only new
  deploy-stub is the tenant AZOA API key, recorded in
  `conductor/DEPLOY-STEPS-TODO.md`, never committed.
- [ ] No brand leak: grep the AZOA solution for the tenant brand name — zero
  hits in code, config, comments, or docs (the contract doc refers to "the
  fiat-settlement tenant," never the brand).
- [ ] `dotnet build` passes with **zero warnings** (nullable enabled).
- [ ] `dotnet test` passes, including the new replay/KYC unit tests.
- [ ] `tracks.md` row for this track moves to `[x]` Shipped.

## Tier

Tier 1. A value-bearing, externally-triggered primitive: it must be correct
(idempotent, fail-closed KYC) before any fiat flow points at it, but it sits one
rung below the Tier 0 bridge/signing-safety work it depends on.

## Dependencies

- **signing-core-keystone** — real custodial asset transfer/mint semantics. This
  track consumes the signing primitive for the allocation side effect; without
  it the allocation can only be stubbed.
- **kyc-module** — the gating check. The allocation primitive calls into this to
  resolve the target avatar's KYC status and fail closed when not approved.
- **tenant-onboarding** — API-key tenant identity. Establishes how a tenant
  (the fiat-settlement platform) is issued the avatar-scoped `X-Api-Key`
  (`ApiKeyAuthenticationHandler.cs:14,72-87`) that this primitive trusts, and
  the scope claim that authorises allocation.
