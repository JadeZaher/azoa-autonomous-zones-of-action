---
type: Track Plan
title: ArdaNova production-readiness waves — plan
description: Ten dependency-ordered, collision-proofed waves from the 2026-09-07 vertical review, ordered by what a non-crypto user hits first.
tags: [plan, ardanova-production-readiness-waves_20260907, pending]
timestamp: 2026-09-07T00:00:00Z
resource: ./metadata.json
---

# Plan

Generated from `REVIEW-EVIDENCE.json` (147 findings, 8 verticals, 2 product lenses).
Waves are ordered by the thesis — what a non-crypto user hits FIRST — not by
engineering tidiness. Each wave names its collision risk; two items in one wave
never write the same file. Owner questions that gate a wave are listed on it.

## Wave 0 — Owner gates, honest documents, and a tree that builds ⬜

*Nothing downstream can be planned against documents that describe the wrong model (self-sovereign, sidecar fiat, Sybil hole already closed, tracks marked complete that are 501). The ~81-file in-flight hardening set must land first or every ArdaNova item below edits an untracked file. The seven owner questions gate waves 1-9; the contract must be amended byte-identically in both repos in the same commit window.*

- [ ] **W0.1** (both, S, risk low) — Amend the integration contract (both copies, byte-identical): §2/§11.1 ArdaNova operates the node and custodies users; §11.2 tenant-custodial not self-sovereign; §1 KYC document capture moved to AZOA-hosted Identomat; §3 ArdaNova owns Stripe in+out; §4.1 credential path disabled; §5.1 Project holon = target-state; §7 P7 done; KeyCustodyService path fixed
      *thesis:* Stops the next engineer rebuilding the self-sovereign path that forces users to learn AZOA exists
      *owns:* `oasis-sleek/conductor/ARDANOVA-AZOA-INTEGRATION-CONTRACT.md`, `ardanova/conductor/ARDANOVA-AZOA-INTEGRATION-CONTRACT.md`
- [ ] **W0.2** (ardanova, M, risk low) — ArdaNova doc sync: tracks.md (Phase E header, 08-kyc superseded, 12 phases, azoa-user-onboarding overview, AZOA Provider Adapter row), product.md (KYC gates value not creation; governance off-chain; KYB stance), README (Voting 'Complete' -> 501, Algorand-ASA badge, Q2-2025 escrow row), ARCHITECTURE.md (retire Explorer), 09-tokenomics plan/spec (USDCa/Connect claims), 15-token-equity-ui plan (UI exists), treasury-reward plan (Mint vs Transfer), 20-dispute-escrow-ui spec, RUNBOOK §6 governance
      *thesis:* Indirect — planning off these docs double-builds UI that exists and assumes a stablecoin rail that does not
      *depends on:* W0.5
      *owns:* `ardanova/conductor/**`, `ardanova/README.md`, `ardanova/documentation/ARCHITECTURE.md`, `ardanova/documentation/FRONTEND_EXPERIENCE.md`
- [ ] **W0.3** (azoa, M, risk low) — AZOA doc sync: RUNBOOK (D9 sidecar-only fiat, D10 status, reconcile D27 vs D36, D36 'sign-without-export' wording is false today), conductor/product.md rewritten to the RUNBOOK §1 goal + two-consumer table, README.md:108/122, API_SYNC.md credential issue route, tracks.md (credential issuance landed, governance D39/D40/D42 landed), governance-vote-of-record and credential-third-party-issuance track spec/plan/metadata, docs/TENANT-CUSTODIAL-ONBOARDING.md 'only wallet creation is skipped' claim, IKeyCustodyService/KeyCustodyService stale 'FOLLOW-UP' comments
      *thesis:* Indirect — reviews cannot check thesis alignment against a product.md that does not state the thesis
      *owns:* `oasis-sleek/conductor/RUNBOOK.md`, `oasis-sleek/conductor/product.md`, `oasis-sleek/conductor/tracks.md`, `oasis-sleek/conductor/tracks/governance-vote-of-record_20260906/**`, `oasis-sleek/conductor/tracks/credential-third-party-issuance_20260906/**`, `oasis-sleek/README.md` …
- [ ] **W0.4** (both, S, risk low) — Publish azoa-sdk 0.3.0 (or drop ArdaNova's ^0.3.0 pin) so ArdaNova CI is not red by construction
      *thesis:* None directly; unblocks every ArdaNova client PR
      *owns:* `oasis-sleek/sdk/azoa-wallet/package.json`, `ardanova/ardanova-client/package.json`, `ardanova/ardanova-client/package-lock.json`
- [ ] **W0.5** (ardanova, M, risk medium) — Land the in-flight ~81-file hardening set (AzoaValueEligibilityService, FundingIntentService, AzoaAllocationService, invest page, escrow 501/503s) on a branch that builds; then delete the dead self-sovereign path (AzoaAvatarService, IAzoaAvatarNode, AzoaAvatarNodeAdapter, AzoaPublicNodeClient, AzoaAvatarController 410 stub, DI registration)
      *thesis:* Removes the one code path that would post a user's real email to a shared node and fail on 'username taken'
      *owns:* `ardanova/ardanova-backend-api-mcp/api-server/src/** (in-flight set as reported by git status)`, `ardanova/.../ArdaNova.Application/Services/Implementations/AzoaAvatarService.cs`, `ardanova/.../ArdaNova.Infrastructure/Azoa/AzoaPublicNodeClient.cs`, `ardanova/.../ArdaNova.API/Controllers/AzoaAvatarController.cs`, `ardanova/.../ArdaNova.Application/DependencyInjection.cs`

⚠ **Collision:** W0.2 and W0.5 both touch ardanova files: W0.5 lands the in-flight set first (code), W0.2 edits only conductor/docs afterward. W0.1 owns the contract file in BOTH repos; W0.2/W0.3 must not touch it.

## Wave 1 — Onboarding without crypto: identity + KYC live in production, no wallet on the fiat path ⬜

*The first thing a non-crypto user hits after Google sign-in is 'Start account setup' returning Conflict 'unavailable' (EnableCustodialAccounts=false) and, if enabled, an Identity check that is 'not configured on this node' (Kyc:Provider=unavailable, empty trust profile) and a wallet step that can never be Ready (CustodyMode=Disabled). This wave makes identity + KYC real in production and stops the wallet from blocking fiat actions at all, per Q3/D9. Also strips the two genuine crypto leaks (address on the account card, 'Wallets' nav). Parallel with Wave 2.*

**Gated on owner questions:** Q3

- [ ] **W1.1** (both, M, risk medium) — Rail-aware eligibility: RequireReadyForFiatAsync (identity + KYC per Q3 policy, no walletId/walletAddress); keep chain-rail readiness separate; distinguish 'service unavailable' from 'not ready' instead of swallowing exceptions as Forbidden; mirror in the client contract's isValueActionAccountReady
      *thesis:* A card-only sponsor no longer needs a custodial Algorand wallet provisioned; an AZOA outage stops reading as 'you did not finish setup'
      *depends on:* W0.5
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/AzoaValueEligibilityService.cs`, `ardanova/.../ArdaNova.Application/Services/Interfaces/IAzoaValueEligibilityService.cs`, `ardanova/.../tests for eligibility`, `ardanova/ardanova-client/src/lib/contracts/project-create-contract.ts`
- [ ] **W1.2** (ardanova, S, risk low) — Strip crypto from the settings and account surfaces: account card shows 'Ready'/'Not created' (no address, no GUID, no 'Azoa'); remove 'Wallets' from the sidebar and hide /settings/wallets behind an explicit Advanced opt-in; verification page drops 'wallet custody' copy, adds status re-poll on window focus after the Identomat tab; payouts page drops 'MANAGE EXTERNAL WALLETS' as its only CTA; update components/azoa/AGENTS.md status language
      *thesis:* Direct — the only places a sponsor is asked to think about wallets, addresses, mnemonics, or signed proofs
      *owns:* `ardanova/ardanova-client/src/components/azoa/custodial-account-card.tsx`, `ardanova/ardanova-client/src/components/azoa/AGENTS.md`, `ardanova/ardanova-client/src/components/app-sidebar.tsx`, `ardanova/ardanova-client/src/app/settings/wallets/page.tsx`, `ardanova/ardanova-client/src/app/settings/verification/page.tsx`, `ardanova/ardanova-client/src/app/settings/payouts/page.tsx`
- [ ] **W1.3** (ardanova, S, risk low) — First-run onboarding: soft 'Finish account setup' prompt on the dashboard after first sign-in so the identity check happens before intent, not at checkout (Track 17 minimum)
      *thesis:* Friction moves out of the moment of generosity
      *owns:* `ardanova/ardanova-client/src/app/dashboard/page.tsx`, `ardanova/ardanova-client/src/components/onboarding/setup-prompt.tsx (new)`
- [ ] **W1.4** (azoa, M, risk medium) — AZOA KYC production readiness: confirm where approval ExpiresAt is stamped and split Kyc:ApprovalValidityDays (12 months) from SubmissionExpiryDays (session, 30 min–30 days); check Identomat /begin for a return/redirect parameter and send it; write the operator runbook step (create ArdaNova tenant user -> operator issues tenant key -> tenant logs in first-party and selects Identomat via PUT /api/tenant/kyc/provider -> set PolicyVersion/AssuranceLevel/TrustedProviderKeys + CompanyKey/ConfigId/CallbackApiKey/PersonKeySecret); store the tenant user's credential in the deploy secret store
      *thesis:* Without it 'Identity check' is 'not configured'; a 30-day approval would force monthly re-verification of every sponsor
      *owns:* `oasis-sleek/Managers/KycManager.cs`, `oasis-sleek/Providers/Kyc/IdentomatKycProviderService.cs`, `oasis-sleek/appsettings.json (Kyc section)`, `oasis-sleek/docs/NODE-HOST.md`, `oasis-sleek/tests/.../Kyc/*`
- [ ] **W1.5** (azoa, M, risk medium) — AML non-clearance recourse: add an UnderReview status to the tenant custodial-account projection (no AML reason disclosed), define the manual-review SLA, make sure the AZOA operator KYC console lists the review queue with an approve/deny action
      *thesis:* A false-positive sanctions-name match (common for ordinary people) currently strands a legitimate sponsor forever with no path
      *owns:* `oasis-sleek/Services/Kyc/KycHostedSessionOutcomeHandler.cs`, `oasis-sleek/Managers/TenantCustodialAccountManager.cs`, `oasis-sleek/Models/Responses/TenantCustodialAccount*.cs`, `oasis-sleek/frontend/src/app/(dashboard)/kyc/page.tsx`
- [ ] **W1.6** (ardanova, S, risk low) — ArdaNova KYC projection: consume UnderReview ('Under review — contact support'), remove the legacy KycService PRO writer so AZOA is the single writer of verificationLevel, add an audited operator-only 'unbind AZOA account' action for node-rebuild recovery of the unique azoaAvatarId
      *thesis:* Two writers to verificationLevel currently disagree; a dev volume wipe Conflict-locks every user until the column is nulled by hand
      *depends on:* W1.5
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/AzoaCustodialAccountService.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/KycService.cs`, `ardanova/.../ArdaNova.API/Controllers/AdminAzoaBindingController.cs (new)`, `ardanova/ardanova-client/src/app/admin/kyc/page.tsx`
- [ ] **W1.7** (ardanova, S, risk low) — Production config: Azoa:EnableCustodialAccounts=true with CustodyApiKey (identity + KYC proceed; wallet stays skipped until Wave 8), Identomat secrets on the AZOA Railway service, startup validation that names the missing leg in the status unavailableReason
      *thesis:* Turns 'Start account setup' from a 409 into a working identity flow
      *depends on:* W1.4
      *owns:* `ardanova/.../ArdaNova.API/appsettings.json`, `ardanova/.env.example`, `ardanova/.../ArdaNova.Infrastructure/Azoa/AzoaSettings.cs`, `Railway variables (both services)`

⚠ **Collision:** W1.2 and W1.3 both edit ardanova-client: W1.2 owns settings/* + custodial-account-card + app-sidebar; W1.3 owns dashboard/page.tsx only. W1.4 and W1.5 both touch AZOA KYC: W1.4 owns KycManager/Identomat/appsettings; W1.5 owns the outcome handler + TenantCustodialAccountManager status projection. W1.6 depends on W1.5's new status value.

## Wave 2 — Present a problem properly: edit, publish, media, discovery, community surface ⬜

*This is the social core and it is half-built for every user, crypto-naive or not: a creator cannot fix a typo, every DRAFT (including partial-failure recovery drafts) is public, no image/PDF ever reaches the record, search misses the problem statement, discovery is a client filter over 50 rows, and the only community REST surface is unauthenticated with caller-supplied identities. No AZOA involvement. Runs in parallel with Wave 1.*

**Gated on owner questions:** Q7

- [ ] **W2.1** (ardanova, M, risk medium) — Backend: public listing defaults to status != DRAFT unless viewer is creator/member; PublishAsync reachable and idempotent; SearchAsync indexes problemStatement/solution/targetAudience; server-side pagination honoured; recovery drafts private
      *thesis:* A person drafting a sensitive problem (health, housing, workplace) is exposed before they chose to present it
      *depends on:* W0.5
      *owns:* `ardanova/.../ArdaNova.Infrastructure/Repositories/ProjectRepository.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/ProjectServices.cs`, `ardanova/.../ArdaNova.API/Controllers/ProjectsController.cs (list/search/publish actions only)`
- [ ] **W2.2** (ardanova, M, risk low) — Client: pass search/category/type/status/page to getAll and paginate server-side; Publish button + Edit link in the detail header for managers; stop rendering description twice (header = summary, Overview = solution)
      *thesis:* The 51st problem on the platform becomes discoverable; creators can choose when they go public
      *depends on:* W2.1
      *owns:* `ardanova/ardanova-client/src/app/projects/page.tsx`, `ardanova/ardanova-client/src/app/projects/[slug]/page.tsx`, `ardanova/ardanova-client/src/server/api/routers/project.ts`
- [ ] **W2.3** (ardanova, M, risk low) — Edit route /projects/[slug]/edit reusing the wizard's step-0 fields against api.project.update; delete the orphaned project-form.tsx
      *thesis:* The home page's 'Iterate — revise or stop' step becomes real; first typo is no longer permanent
      *owns:* `ardanova/ardanova-client/src/app/projects/[slug]/edit/page.tsx (new)`, `ardanova/ardanova-client/src/components/projects/project-edit-form.tsx (new)`, `ardanova/ardanova-client/src/components/project-form.tsx (delete)`
- [ ] **W2.4** (ardanova, L, risk medium) — Media and the ask: wire attachment.getUploadUrls into the wizard's Basic Info step, persist bucket paths as a JSON array (not comma-joined names), add a one-line summary field and an optional funding goal, move the account-status panel to the Team Roles step; render a media strip in OverviewTab and stop printing raw 'Images: {string}' in updates-tab
      *thesis:* 'As much clarity and context as possible' is currently five plain textareas while S3/Local storage sits finished; a compliance panel greets the presenter before the problem field
      *depends on:* W1.1
      *owns:* `ardanova/ardanova-client/src/app/projects/create/page.tsx`, `ardanova/ardanova-client/src/lib/contracts/project-create-contract.ts`, `ardanova/ardanova-client/src/components/projects/overview-tab.tsx`, `ardanova/ardanova-client/src/components/opportunities/updates-tab.tsx`, `ardanova/ardanova-client/src/server/api/routers/attachment.ts`, `ardanova/.../ArdaNova.Application/DTOs/ProjectDtos.cs`
- [ ] **W2.5** (ardanova, L, risk high) — Posts stack per Q7: either delete PostsController/PostService/Post* entities + posts.ts + post.ts router, or harden them (ActorAssertion policy, actor-bound authorId/userId, visibility predicate in GetFeedAsync) and build the project-scoped 'field notes' stream
      *thesis:* Today anyone can post as anyone and read members-only posts on the live Railway API; and there is no community surface where a supporter sees new problems being raised
      *owns:* `ardanova/.../ArdaNova.API/Controllers/PostsController.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/PostService.cs`, `ardanova/.../ArdaNova.Domain/Models/Entities/Post*.cs`, `ardanova/ardanova-client/src/lib/api/ardanova/endpoints/posts.ts`, `ardanova/ardanova-client/src/server/api/routers/post.ts`, `ardanova/ardanova-client/src/components/feed/** (new if option 2)`

⚠ **Collision:** ProjectServices.cs and ProjectRepository.cs belong to W2.1 only; W2.4 must confine backend edits to ProjectDtos.cs (if media mapping needs ProjectServices, land it in W2.1). [slug]/page.tsx belongs to W2.2 (it adds the Edit link and Publish button); W2.3 only creates the new edit route and deletes project-form.tsx. create/page.tsx belongs to W2.4 (media + summary + funding goal + moving the account-status panel to the Team Roles step).

## Wave 3 — Sponsorship pipes (fiat-first): a sponsor's card is charged and their stake is recorded ⬜

*Under Q1-B the sponsor path is: intent -> Stripe Checkout -> webhook -> ArdaNova ledger credit + treasury inflow + intent SETTLED, in one transaction, with AZOA anchoring later. Today it is cut in four places (readiness can never be true, assetId never written, settlement carries 'USD' cents not units, no SETTLED transition). Multi-session wave.*

**Gated on owner questions:** Q1, Q3, Q4

- [ ] **W3.1** (ardanova, S, risk low) — Stripe plumbing: validate Stripe config at startup (not first checkout); per-intent success/cancel URLs (/projects/{slug}/invest/success?intent={id}) carried in StripeCheckoutRequest; line item copy 'Support {project}' not 'Fund Project: {AssetName}'
      *thesis:* Every sponsor returning from Stripe currently lands on a page that cannot find their intent; orphan DRAFT intents burn the idempotency key
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/StripeCheckoutGateway.cs`, `ardanova/.../ArdaNova.Application/DTOs/StripeCheckoutRequest.cs`, `ardanova/.../ArdaNova.API/Program.cs (Stripe options validation)`
- [ ] **W3.2** (ardanova, L, risk high) — Settlement unit + ledger credit: lock the token quantity the sponsor accepted into a typed field on the intent; on verified payment_intent.succeeded credit TokenBalance + ProjectInvestment + TreasuryService.ProcessFundingInflowAsync + Gate 1 evaluation and move the intent to SETTLED in the same transaction; create the intent row only after Stripe config resolves; enqueue the AZOA anchor outbox row (dormant until Wave 9); replace readiness/assetId checks with 'project fundable' (W3.3)
      *thesis:* The core promise — back a project and hold a stake — gets a ledger entry; the success page stops polling forever
      *depends on:* W3.1, W3.3, W3.6
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/FundingIntentService.cs`, `ardanova/.../ArdaNova.Domain/Models/Entities/FundingIntent.cs`, `ardanova/.../ArdaNova.Infrastructure/Migrations/* (new)`, `ardanova/.../tests for FundingIntentService`
- [ ] **W3.3** (ardanova, M, risk medium) — Readiness rework: checkout gate = Stripe configured + project fundable (DB-ledger units allowed, assetId optional); ISelectedNodeSettlementCapability becomes the gate for the AZOA anchor dispatch only; EnableFundingCheckout semantics documented as 'no real gateway is registered' until Wave 9; startup banner/health field 'economy: simulated/disabled' so QA cannot mistake the simulated stack for a working economy
      *thesis:* 'Create secure checkout' stops answering with an operator-facing 409 about attested settlement dispatch
      *owns:* `ardanova/.../ArdaNova.Infrastructure/Azoa/AzoaFundingSettlementReadiness.cs`, `ardanova/.../ArdaNova.Application/Services/Interfaces/IFundingSettlementReadiness.cs`, `ardanova/.../ArdaNova.Infrastructure/DependencyInjection.cs`, `ardanova/.../ArdaNova.Infrastructure/Azoa/AzoaSettings.cs`, `ardanova/.../ArdaNova.API/appsettings*.json`, `ardanova/.../ArdaNova.API/HealthChecks/EconomyModeHealthCheck.cs (new)`
- [ ] **W3.4** (ardanova, M, risk low) — Invest + success pages in plain language: quote USD and 'your share of this project's support' (supply % and units behind a disclosure); remove ARDA rows; map server error codes to human copy client-side; success page gets terminal states (Confirmed / We'll email you / Something went wrong) with a poll timeout; portfolio shows the holding
      *thesis:* The sponsorship screen no longer requires understanding a two-hop token conversion to read the number being paid
      *depends on:* W3.2
      *owns:* `ardanova/ardanova-client/src/app/projects/[slug]/invest/page.tsx`, `ardanova/ardanova-client/src/app/projects/[slug]/invest/success/page.tsx`, `ardanova/ardanova-client/src/lib/commerce/funding-intent-status.ts`, `ardanova/ardanova-client/src/components/equity/equity-preview.tsx`, `ardanova/ardanova-client/src/components/equity/swap-preview.tsx`, `ardanova/ardanova-client/src/components/equity/conversion-chain.tsx` …
- [ ] **W3.5** (both, S, risk low) — Retire the competing fiat design: delete the dead StripeService.HandlePaymentSucceededAsync (keep Connect code), remove fiat-sidecar from AZOA compose (or mark reference-only), fix its stale '@azoa/wallet-sdk' import if kept
      *thesis:* Prevents the next engineer building the sidecar path that puts pricing inside AZOA
      *depends on:* W3.2
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/StripeService.cs`, `oasis-sleek/fiat-sidecar/**`, `oasis-sleek/docker-compose.dev.yml`
- [ ] **W3.6** (ardanova, M, risk medium) — Self-serve fundability: a project owner (not AdminApiKey) creates the ProjectTokenConfig with human terms (name, supply, disclosure) at publish; assetId optional; contributorSupply/investorSupply pools; Funding tab stops saying 'Funding is not configured' for owner-created projects
      *thesis:* The neighbour's project a sponsor wants to back is fundable without an operator hand-editing the database
      *depends on:* W2.1
      *owns:* `ardanova/.../ArdaNova.API/Controllers/ProjectTokensController.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/ProjectTokenService.cs`, `ardanova/ardanova-client/src/components/projects/funding-tab.tsx`, `ardanova/ardanova-client/src/server/api/routers/project-tokens.ts`

⚠ **Collision:** FundingIntentService.cs belongs to W3.2 only; W3.1 owns StripeCheckoutGateway + its request DTO; W3.3 owns readiness/DI/settings; W3.6 owns ProjectTokenService/ProjectTokensController (W3.2 must credit via existing ITokenBalanceService/ProjectTokenService methods without editing them); W3.7 touches StripeService.cs only to delete the dead handler.

## Wave 4 — Sponsorship safe to open: reversals, money math, KYC tiering, backer privacy ⬜

*Wave 3 makes a charge produce a stake; this wave makes it safe to take real cards: chargebacks and refunds are currently discarded on both sides (D10), money is double/int with silent truncation, the KYC gate is uniform, and pledges are visible to strangers with the Stripe PaymentIntent id. Do not open checkout to the public before this wave lands.*

**Gated on owner questions:** Q1, Q3, Q5

- [ ] **W4.1** (ardanova, L, risk high) — Reversal pipeline: handle charge.refunded, charge.dispute.created, charge.dispute.funds_withdrawn/reinstated; add REVERSED/DISPUTED intent states and FUNDING_REVERSAL settlement kind; hold window on TokenBalance (holderClass frozen until the Stripe reversal window elapses) so a day-60 chargeback reverses a frozen stake; sponsor-facing refund request; per Q5
      *thesis:* Stops the money-printer where a disputing sponsor keeps stake while honest backers' cap table is diluted
      *depends on:* W3.2
      *owns:* `ardanova/.../ArdaNova.API/Controllers/StripeWebhookController.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/FundingReversalService.cs (new)`, `ardanova/.../ArdaNova.Domain/Models/Enums/FundingIntentStatus.cs`, `ardanova/.../ArdaNova.Domain/Models/Enums/EconomicSettlementKind.cs`, `ardanova/.../ArdaNova.Domain/Models/Entities/TokenBalance.cs (hold fields)`, `ardanova/.../ArdaNova.Infrastructure/Migrations/* (new)`
- [ ] **W4.2** (ardanova, L, risk medium) — Fixed-scale money: replace double/int on value paths with decimal(18,2)/minor-unit long (ExchangeService, PayoutService, TreasuryService, StripeService math, PlatformTreasuryTransaction, ProjectInvestmentDto); remove (long)/(int) truncations; collapse ExchangeService to project-token -> USD (ARDA internal per Q4); strip StripePaymentIntentId from the non-admin investor DTO
      *thesis:* Cents-level mismatches on a 'transparent' ledger destroy trust disproportionately; a worker converting a small balance is silently truncated to 0
      *depends on:* W3.5
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/ExchangeService.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/PayoutService.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/TreasuryService.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/StripeService.cs`, `ardanova/.../ArdaNova.Domain/Models/Entities/PlatformTreasuryTransaction.cs`, `ardanova/.../ArdaNova.Application/DTOs/TokenomicsDtos.cs` …
- [ ] **W4.3** (ardanova, M, risk medium) — KYC tiering enforcement per Q3: cumulative de-minimis for sponsors on the fiat rail, full KYC for recipients; owner-readiness lapse between checkout and webhook becomes a hold state with auto-refund after N days, not an infinite Retry; attempt ceiling + operator alert on the anchor outbox
      *thesis:* A $50 backer is no longer sent to a passport upload; captured money can no longer sit in limbo because of someone else's KYC
      *depends on:* W1.1, W4.1
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/FundingIntentService.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/EconomicOutboxDispatchService.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/AzoaValueEligibilityService.cs`
- [ ] **W4.4** (ardanova, S, risk low) — Backer privacy: investors endpoint gets [Authorize]; aggregate (count + total) for non-members, itemised for project members, display name not internal id; per-pledge 'show my name' consent captured at checkout
      *thesis:* A $50 pledge stops being visible to strangers
      *depends on:* W4.2
      *owns:* `ardanova/ardanova-client/src/components/equity/investor-table.tsx`, `ardanova/.../ArdaNova.API/Controllers/ProjectTokensController.cs (investors action)`, `ardanova/.../ArdaNova.Domain/Models/Entities/ProjectInvestment.cs (consent flag)`

⚠ **Collision:** W4.1 must not edit FundingIntentService.cs (owned by W3.2 in the previous wave; here it is owned by W4.3) — it adds a new FundingReversalService and edits the webhook controller + enums. W4.2 owns all money-typed entities/DTOs/services (ExchangeService, PayoutService, TreasuryService, TokenomicsDtos); W4.3 owns FundingIntentService only.

## Wave 5 — Work for a stake and get paid: bid, reserve, settle in the ledger, credentials, USD out ⬜

*No path pays a worker today: a worker cannot even submit a bid from the client; accept creates an agreement that never advances; escrow is 501/503; the off-chain distribute path (the thesis-native payout) is 501 and admin-only; payouts are 503 at controller and refused at service. Under Q1-B/Q4 the shortest path needs no crypto: reserve stake in the DB at accept, distribute on owner approval, pay out via Stripe Connect. Multi-session wave.*

**Gated on owner questions:** Q1, Q3, Q4

- [ ] **W5.1** (ardanova, M, risk low) — Worker-side 'Propose terms' form calling opportunityBid.create (or accept an Application with an owner-entered award) so the existing apply flow becomes the paid path
      *thesis:* The schoolteacher can currently apply but never be accepted into paid work — the layer is absent on the client
      *owns:* `ardanova/ardanova-client/src/components/opportunity-detail-view.tsx`, `ardanova/ardanova-client/src/components/opportunities/bid-form.tsx (new)`, `ardanova/ardanova-client/src/hooks/use-opportunity-mutations.ts`
- [ ] **W5.2** (ardanova, XL, risk high) — Stake reservation and settlement in the ledger: collapse equityReward/awardAmount into one award field; AcceptBid creates a TokenAllocation RESERVED against contributorSupply (fails if oversubscribed); owner 'approve completion' moves RESERVED -> DISTRIBUTED via ProjectTokenService.DistributeAsync behind project-owner authorization (un-501 allocate/pbi + distribute); TaskCommerceAgreement advances ACCEPTED -> ESCROW_FUNDED -> SETTLED; worker KYC gate moved from assignment to settlement; guild bids either drop guildId or declare a split at bid time (Q-ratify)
      *thesis:* The thesis-native payout exists in code and no user can trigger it; today an owner can accept ten bids against the same supply
      *depends on:* W3.6, W4.3
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/TaskCommerceService.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/ProjectTokenService.cs`, `ardanova/.../ArdaNova.API/Controllers/ProjectTokensController.cs`, `ardanova/.../ArdaNova.API/Controllers/TasksController.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/TaskServices.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/OpportunityBidService.cs` …
- [ ] **W5.3** (ardanova, M, risk low) — Escrow surface honesty: delete ReleaseDialog/EscrowDetailCard/DisputeForm tx-hash entry (release evidence comes from the settlement, never a form field); drop txHash* from every client contract and CreateTaskEscrowDto; add dispute ResolveAsync + admin action; commerce page and task-economic-state speak 'reserved / paid / disputed' not 'on-node settlement / allocation receipt'; surface AZOA_ACCOUNT_NOT_READY as 'Complete setup' link
      *thesis:* A sponsor paying a worker is currently prompted for a transaction hash; a disputing worker reaches a page with no outcome
      *owns:* `ardanova/ardanova-client/src/components/escrow/**`, `ardanova/ardanova-client/src/lib/commerce/escrow-release.ts`, `ardanova/ardanova-client/src/app/admin/disputes/page.tsx`, `ardanova/ardanova-client/src/app/tasks/[id]/commerce/page.tsx`, `ardanova/ardanova-client/src/components/tasks/task-economic-state.tsx`, `ardanova/.../ArdaNova.Application/Services/Implementations/TaskEscrowServices.cs` …
- [ ] **W5.4** (ardanova, M, risk medium) — Credentials as one truth: membership credential grant un-501 behind project-owner authorization (off-chain valid, chain issuance deferred to Wave 9); XP tier derives from credential tier (or vice versa) so User.tier and MembershipCredential.tier cannot disagree; credential UI says 'Verified' / 'Verification pending' with a 'View proof' disclosure instead of 'ASA #', 'Minted on Algorand', tx hashes
      *thesis:* 'Portable credential' is two DB columns that can disagree, delivered in the vocabulary the thesis forbids; also the gate on ArdaNova's own vote (Wave 6)
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/MembershipCredentialServices.cs`, `ardanova/.../ArdaNova.API/Controllers/MembershipCredentialsController.cs`, `ardanova/.../ArdaNova.API/Controllers/CredentialUtilityController.cs`, `ardanova/ardanova-client/src/server/api/routers/membership-credential.ts`, `ardanova/.../ArdaNova.Application/Services/Implementations/XPEventService.cs`, `ardanova/ardanova-client/src/components/credentials/credential-card.tsx` …
- [ ] **W5.5** (ardanova, XL, risk high) — USD out via Stripe Connect (Q1-B): PayoutService.RequestPayout/ProcessPayout un-503 using existing CreateConnectedAccountAsync + CreatePayoutTransferAsync; Connect Express onboarding at first payout request (the one moment paperwork is expected); withdraw page becomes a real flow; transfer.reversed handled; rewards restricted-until-liquid so the taxable event defers to payout (Q-tax, with counsel)
      *thesis:* Closes the loop: work -> stake -> dollars is currently unreachable at every layer and the UI says so in three places
      *depends on:* W4.2, W5.2
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/PayoutService.cs`, `ardanova/.../ArdaNova.API/Controllers/PayoutsController.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/StripeService.cs (Connect path)`, `ardanova/ardanova-client/src/app/portfolio/withdraw/page.tsx`, `ardanova/ardanova-client/src/server/api/routers/payouts.ts`, `ardanova/.../ArdaNova.Domain/Models/Entities/PayoutRequest.cs`

⚠ **Collision:** W5.2 owns TaskCommerceService/ProjectTokenService/ProjectTokensController/TasksController/OpportunityBidService; W5.3 owns everything under escrow (services, controller, client components); W5.5 owns credentials; W5.6 owns payouts (PayoutService/PayoutsController/StripeService Connect path — W4.2 finished with these in the prior wave). W5.1 is client-only opportunity files.

## Wave 6 — Governance a member can actually use ⬜

*A member is shown vote buttons, then told 'CREDENTIAL_REQUIRED … separate authorized workflow'; an admin who does vote gets a proposal that can never reach PASSED; quorum compares a ballot count to a percentage; behind the 501, voterId and weight come from the request body. Per Q6 option 1 ArdaNova is the vote of record; AZOA's nodes stay gated until a consumer surface exists.*

**Gated on owner questions:** Q6

- [ ] **W6.1** (ardanova, L, risk high) — ArdaNova vote of record: voterId and weight derived server-side from the authenticated actor (never the body); PASSED/REJECTED/EXPIRED resolution written on close/expiry; quorum as % of eligible members; close != cancel; Execute reachable; lift the project-scoped 501s; vote form disabled with plain copy until the member holds a credential (W5.4); remove 'TX:' and 'voting power' from votes-tab/proposals-tab
      *thesis:* A sponsor or worker cannot vote on a project decision today on either system without meeting a crypto or workflow concept
      *depends on:* W5.4
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/GovernanceServices.cs`, `ardanova/.../ArdaNova.API/Controllers/GovernanceController.cs`, `ardanova/.../ArdaNova.API/Controllers/ProjectsController.cs (governance actions)`, `ardanova/ardanova-client/src/server/api/routers/governance.ts`, `ardanova/ardanova-client/src/server/api/routers/project.ts (governance procedures)`, `ardanova/ardanova-client/src/components/projects/proposals-tab.tsx` …
- [ ] **W6.2** (azoa, M, risk medium) — AZOA governance gate exit list (keep gated, close the holes): freeze GovernanceProposal metadata after the first claim in HolonManager.UpdateAsync (or snapshot options/scope/electorate/closesAt onto claim rows); proposal-rewrite attack tests; null-tenant electorate must not enfranchise the whole node; record the Phase 4 adversarial verdict; optional REST for IPersonUniquenessGate if Q6 option 2
      *thesis:* A proposal author can currently move the outcome after members voted — the trust failure this audience notices first
      *owns:* `oasis-sleek/Managers/HolonManager.cs`, `oasis-sleek/Services/Governance/ConsumerGovernanceHolonTypes.cs`, `oasis-sleek/Services/Actions/Handlers/GovernanceProposalCreateNodeHandler.cs`, `oasis-sleek/tests/AZOA.WebAPI.Tests/Governance/**`, `oasis-sleek/Services/Governance/AGENTS.md`

⚠ **Collision:** W6.1 owns ProjectsController governance actions (:766-935) — W2.1 finished with the list/publish actions in Wave 2. W6.2 is AZOA-only.

## Wave 7 — Transparency: where did the money go ⬜

*'Transparent settlement' is the thesis promise and the sponsor has no screen for it — only a backer list and a progress bar; the one plain-language ledger is admin-only. Must come after settlement rows exist (Waves 3-5) or the ledger is permanently empty. AZOA analytics must NOT be exposed to sponsors (self-scoped, unpriced, speaks base units and hashes).*

**Gated on owner questions:** Q1

- [ ] **W7.1** (ardanova, L, risk medium) — Per-project 'Money' view from ArdaNova's own rows (FundingIntent/ProjectInvestment settled, TokenAllocation distributed, PayoutRequest, reversals, platform fee) with human descriptions and USD; azoaOperationId as a hidden 'verify' reference; revive the unused TreasuryTransaction entity or replace it
      *thesis:* Every sponsor hits this on day one: 'you put in $500, the project paid $200 to a designer'
      *depends on:* W4.1, W5.2
      *owns:* `ardanova/.../ArdaNova.API/Controllers/ProjectLedgerController.cs (new)`, `ardanova/.../ArdaNova.Application/Services/Implementations/ProjectLedgerService.cs (new)`, `ardanova/.../ArdaNova.Domain/Models/Entities/TreasuryTransaction.cs`, `ardanova/ardanova-client/src/components/projects/funding-tab.tsx`, `ardanova/ardanova-client/src/components/projects/money-tab.tsx (new)`, `ardanova/ardanova-client/src/server/api/routers/project-tokens.ts` …
- [ ] **W7.2** (azoa, S, risk low) — AZOA: rename node transparency to 'node economic-rules transparency' in docs, add one SDK read + an operator-console page so the surface has a consumer; keep the conformance manifest out of any user-transparency narrative
      *thesis:* None for users; prevents misreporting operator surfaces as sponsor transparency
      *depends on:* W0.4
      *owns:* `oasis-sleek/sdk/azoa-wallet/src/api/client.ts (node-transparency methods)`, `oasis-sleek/sdk/azoa-wallet/src/api/api-version.ts`, `oasis-sleek/frontend/src/app/(dashboard)/node-transparency/page.tsx (new)`, `oasis-sleek/API_SYNC.md`

⚠ **Collision:** W7.1 owns funding-tab.tsx and project-tokens.ts router (W3.6 finished with them). W7.2 is AZOA-only.

## Wave 8 — Custody hardening (AZOA) — prerequisite for any chain-backed value ⬜

*Gated on Q2. Today: the D36 'sign-without-export' seam does not exist (TryDerive returns raw key bytes), per-user keys sit under SHA-256 of one config string with no KDF or envelope, CustodyMode=Disabled does not stop AllocationManager minting config-KEK wallets, the real-value health probe never reads CustodyMode, rotation has a signing-outage window and an ephemeral marker, parked payouts wait for a human, and provisioned users have no consent path. Multi-session; not on the fiat-first critical path.*

**Gated on owner questions:** Q2

- [ ] **W8.1** (azoa, XL, risk high) — Sign-only key handle: replace IPlatformKeyDerivation.TryDerive(secret, out byte[]) with ISigningKeyHandle { SignAsync(payload), PublicKey } resolved per chain; env-backed provider derives in-process; per-wallet random DEK wrapped by the KEK (envelope encryption) in WalletKeyService with a Previous-key window so rotation has no signing outage; ITransactionSigner takes a handle not bytes
      *thesis:* Indirect but total: a leaked KEK plus a DB read currently exposes every custodial key at once; no user knew there was a key to lose
      *owns:* `oasis-sleek/Services/Custody/IPlatformKeyDerivation.cs`, `oasis-sleek/Services/Custody/ISigningKeyHandle.cs (new)`, `oasis-sleek/Services/Custody/PlatformKeyDerivationRegistry.cs`, `oasis-sleek/Services/Custody/PlatformKeyDerivations.cs`, `oasis-sleek/Services/Custody/KeyCustodyService.cs`, `oasis-sleek/Services/Signing/WalletKeyService.cs` …
- [ ] **W8.2** (azoa, L, risk high) — Custody policy enforced at every wallet-creating path (WalletManager.GenerateWalletAsync consults a CustodyPolicy; Disabled fails closed); Production boot guard (CustodyMode must be Disabled or a registered adapter, KEK >= 32 chars and not a committed dev value, rotation marker on a persisted mount); RealValueReadinessHealthCheck reads all three; per-user and total-float caps as AllocationManager invariants with tests
      *thesis:* The user is told their managed account is not ready while a tenant value action can still create and fund one under dev-grade custody; the health probe an operator trusts prints 'custody configured' without checking
      *depends on:* W8.1
      *owns:* `oasis-sleek/Managers/WalletManager.cs`, `oasis-sleek/Managers/AllocationManager.cs`, `oasis-sleek/Services/Custody/CustodyPolicy.cs (new)`, `oasis-sleek/Program.cs`, `oasis-sleek/Observability/RealValueReadinessHealthCheck.cs`, `oasis-sleek/Services/Custody/FilePendingRotationKeyStore.cs` …
- [ ] **W8.3** (both, M, risk high) — Implied consent grant for tenant-provisioned child users created in ProvisionChildAsync, revocable via TenantConsentController and surfaced in ArdaNova settings; test that a plain tenant-key allocation against a child user with no grant is refused; resolve how AllocationController derives actingTenantId from an API-key principal
      *thesis:* Either the user must log into AZOA to click 'allow', or tenant-key ops silently bypass consent — both are wrong
      *owns:* `oasis-sleek/Managers/TenantManager.cs`, `oasis-sleek/Controllers/TenantConsentController.cs`, `oasis-sleek/Controllers/AllocationController.cs`, `oasis-sleek/tests/.../Consent/**`, `ardanova/ardanova-client/src/app/settings/verification/page.tsx (consent revoke affordance)`
- [ ] **W8.4** (azoa, M, risk medium) — Un-park reconciliation: ReconciliationHostedService calls ActionManager.ReconcileAllParkedRunsAsync each cycle with a hard-stuck threshold that raises an operator alert (same pattern as bridge G6); operator console for AwaitingReconciliation runs
      *thesis:* A payout whose confirmation read timed out currently sits 'pending' until a human calls the sweep
      *owns:* `oasis-sleek/Services/Reconciliation/ReconciliationService.cs`, `oasis-sleek/Services/Reconciliation/ReconciliationHostedService.cs`, `oasis-sleek/Managers/ActionManager.cs (reconcile-all entry)`, `oasis-sleek/frontend/src/app/(dashboard)/admin/parked-runs/page.tsx (new)`
- [ ] **W8.5** (azoa, M, risk medium) — KEK backup/escrow drill and platform-key rotation runbook (new account + ASA role-transfer script) in NODE-HOST.md; D16 fix: TryReadRailAmount uses ValueDimension.ChainAsset(SourceTokenId) not NativeChainAsset; TenantDeclared rate source that records ArdaNova's decided USD; Swap/Exchange ops stamped
      *thesis:* KEK loss is total and unrecoverable today; the first real rate source would stamp N project-token units as N microAlgos
      *owns:* `oasis-sleek/docs/NODE-HOST.md`, `oasis-sleek/Managers/BlockchainOperationManager.cs`, `oasis-sleek/Services/Analytics/TenantDeclaredValueRateSource.cs (new)`, `oasis-sleek/Services/Analytics/AGENTS.md`, `oasis-sleek/Interfaces/IValueRateSource.cs`

⚠ **Collision:** AllocationManager.cs belongs to W8.2 only (caps + CustodyMode + a call site for the Wave 9 recipient preparer). WalletKeyService.cs belongs to W8.1 (dual-key + envelope). Program.cs boot guards are W8.2.

## Wave 9 — Chain anchor: real project ASAs, IssueUnits, HOLD, live gateway, SDK parity ⬜

*Only after Waves 3-5 give users a working economy and Wave 8 gives AZOA custody worth trusting does the chain become the audit anchor the thesis wants — invisible to users. Today the anchor would mint a fresh ASA per settlement, the fungible-mint DTO does not match AZOA's, a new custodial wallet cannot receive an asset (no opt-in, no min balance), nothing can HOLD, and the only gateway is a Retry stub. Multi-session; chain-side gotchas (opt-in, fees, clawback) must never surface to a user.*

**Gated on owner questions:** Q1, Q2, Q4, Q5

- [ ] **W9.1** (ardanova, L, risk medium) — Project ASA mint: fix AzoaFungibleMintRequest to AZOA's DTO (total + decimals = assetScale) with a contract test against AZOA's OpenAPI; a publish-time/gate->FUNDING server job calls fungible-mint and persists assetId; CredentialUtilityService routes issuance through POST /api/credential/issue/{recipientUserId} via a dapp:manager tenant credential
      *thesis:* Invisible when it works; today flipping Provider=Azoa fails every mint with 'zero total'
      *depends on:* W8.2
      *owns:* `ardanova/.../ArdaNova.Infrastructure/Azoa/AzoaBackedAlgorandService.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/ProjectTokenService.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/CredentialUtilityService.cs`, `ardanova/.../tests/Azoa/FungibleMintContractTests.cs (new)`
- [ ] **W9.2** (azoa, XL, risk high) — AZOA IssueUnits allocation kind (transfer from the ASA reserve the platform already controls, idempotent through the existing ledger) + custodial recipient preparation (fund min balance from the fee-payer account, sign the recipient opt-in via KeyCustodyService, then transfer — atomic group) + caller-supplied Purpose landing in operation_log + fee quote exposed before terms lock; map provider errors to consumer-safe codes before they cross the tenant boundary; Transfer fee>0 no longer hard-denies refunds
      *thesis:* The very first real payout to a real worker otherwise fails with a blockchain error naming an ASA and 'opt-in'
      *depends on:* W8.2, W8.3
      *owns:* `oasis-sleek/Managers/AllocationManager.cs`, `oasis-sleek/Models/Requests/AllocationRequest.cs`, `oasis-sleek/Services/Custody/CustodialRecipientPreparer.cs (new)`, `oasis-sleek/Providers/Blockchain/Algorand/AlgorandProvider.cs`, `oasis-sleek/Managers/NodeFeeScheduleManager.cs`, `oasis-sleek/Persistence/SurrealDb/Models/OperationLog.cs` …
- [ ] **W9.3** (azoa, XL, risk high) — HOLD primitive: Hold / Release / Refund Tier-2 node trio on a platform-custodial holding wallet behind KeyCustodyService, with a hold ledger row (holdId, funder, beneficiary, asset, amount, state) as the idempotency anchor; TEAL escrow later
      *thesis:* D14 is still true — nothing on any layer can hold value; escrow is unbuildable on chain without it
      *depends on:* W8.1, W8.2
      *owns:* `oasis-sleek/Services/Hold/** (new)`, `oasis-sleek/Services/Actions/Handlers/HoldNodeHandler.cs (new)`, `oasis-sleek/Services/Actions/Handlers/ReleaseNodeHandler.cs (new)`, `oasis-sleek/Services/Actions/Handlers/RefundNodeHandler.cs (new)`, `oasis-sleek/Models/Actions/ActionEnums.cs`, `oasis-sleek/Persistence/SurrealDb/Models/HoldLedger.cs (new) + generated schema`
- [ ] **W9.4** (ardanova, L, risk high) — Live settlement gateway in ArdaNova: real IAzoaSettlementGateway implementing ISelectedNodeSettlementCapability around AzoaNodeClient.AllocateAsync (IssueUnits, token units not 'USD') + receipt reconcile via /api/allocation/receipt; confirmed operation -> EconomicSettlement SETTLED; one idempotency key convention; persist Gross/Fee/Net from the receipt for the ledger; attempt ceiling; retire AllocateFundingSettlementAsync/AllocateRewardAsync Mint kinds
      *thesis:* Gives every sponsor and worker settlement an AZOA operation id the ledger can reference — invisibly
      *depends on:* W9.1, W9.2
      *owns:* `ardanova/.../ArdaNova.Infrastructure/Azoa/AzoaSettlementGateway.cs (new)`, `ardanova/.../ArdaNova.Infrastructure/Azoa/DisabledAzoaSettlementGateway.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/AzoaAllocationService.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/EconomicOutboxDispatchService.cs`, `ardanova/.../ArdaNova.Infrastructure/Outbox/EconomicOutboxLeaseStore.cs`, `ardanova/.../ArdaNova.Domain/Models/Entities/EconomicSettlement.cs` …
- [ ] **W9.5** (azoa, M, risk low) — azoa-sdk parity and hygiene: issueCredential, allocation IssueUnits/Purpose types, tenant custodial UnderReview status; Tinyman adapter resolves decimals from the indexer (remove ?? 6); ALGO/ASA amounts as base-unit strings parsed with BigInt; supportsBridging from a capability probe; decide whether ArdaNova's .NET clients are generated from the SDK; publish 0.3.x; API_SYNC
      *thesis:* None for ArdaNova users (browser SDK is read-only); bites any public consumer that signs client-side
      *depends on:* W9.2
      *owns:* `oasis-sleek/sdk/azoa-wallet/src/**`, `oasis-sleek/sdk/azoa-wallet/package.json`, `oasis-sleek/API_SYNC.md`

⚠ **Collision:** AllocationManager.cs: W8.2 finished with it; here W9.2 owns it. AllocationRequest.cs belongs to W9.2 (IssueUnits kind + Purpose); W9.3 HOLD is a separate node trio with its own files + ActionEnums.cs. ProjectTokenService.cs: W5.2 finished; here W9.1 owns it. EconomicOutbox*/AzoaAllocationService belong to W9.4. SDK is W9.5 only.
