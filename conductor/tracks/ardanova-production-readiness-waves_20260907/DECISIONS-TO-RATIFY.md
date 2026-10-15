---
type: Decisions
title: Decisions currently being made by code
tags: [decisions, ardanova-production-readiness-waves_20260907]
timestamp: 2026-09-07T00:00:00Z
---

# Decisions to ratify or reject

Each of these is a product decision that code has already made implicitly. Ratify
it into the runbook as a numbered decision, or reject it and schedule the reversal.

- **ArdaNova users are tenant-custodial AZOA child users; a consumer person never holds an AZOA credential, seed phrase, or export path. Claim-to-self-sovereign is a future opt-in.**
  - implied by: AzoaCustodialAccountController -> PUT /api/tenant/custodial-accounts/{user.id} with a tenant:provision CustodyApiKey; AzoaAvatarController 410 Gone; WalletController.Export gated by RecentFirstPartyLogin that child users cannot satisfy — while contract §11.2 and ArdaNova tracks.md Phase E still 'lock' self-register/self-run with no tenant:provision.
  - recommended: Ratify. Amend contract §11.2/§2/§4.1 in both repos in one commit window; delete AzoaAvatarService, IAzoaAvatarNode, AzoaAvatarNodeAdapter, AzoaPublicNodeClient; tick conformance plan item 1.
- **ArdaNova talks to Stripe directly (Checkout + webhooks) and owns valuation; AZOA receives already-decided amounts and holds no payment-provider secret.**
  - implied by: StripeCheckoutGateway.cs + StripeWebhookController.cs in ArdaNova; fiat-sidecar has zero ArdaNova references and embeds its own TOKENS_PER_CENT price schedule; RUNBOOK D9 says the opposite ('fiat reaches Stripe only through fiat-sidecar').
  - recommended: Ratify (pending Q1-B). Retire fiat-sidecar or demote it to a reference for other tenants; fix RUNBOOK D9 and contract §3.
- **Project creation and collaboration are NOT gated on KYC; only value-bearing actions are.**
  - implied by: AzoaValueEligibilityService.cs:61 'You can still create projects and collaborate while setup is pending' and create/page.tsx:952 — while conductor/product.md:20 says 'Identity verification gating project creation'.
  - recommended: Ratify the code; fix product.md:20 so nobody re-gates the presentation step.
- **A presented project lives only in ArdaNova Postgres; AZOA learns of a project only through allocation/funding calls. No Project holon mirror at publish.**
  - implied by: ProjectServices.CreateAsync/PublishAsync make no AZOA call; PublishLifecycleDefinitionsAsync has no caller; contract §5.1 presents HolonCreate -> Emit(project.created) as as-built.
  - recommended: Ratify (status quo, consistent with contract :71 'Domain lifecycle = Consumer-owned'); label contract §5.1 target-state; revisit only when a ProjectShare token needs a holon anchor at FUNDED.
- **Task reward allocation uses AllocationKind.Mint (new ASA per reward) and the AZOA funding settlement asks AZOA to mint 'USD' cents.**
  - implied by: AzoaAllocationService.AllocateRewardAsync Kind=Mint (:62-67) vs treasury-reward plan.md:41 'kind: Transfer'; FundingIntentService.cs:408-428 assetCode='USD'; AlgorandProvider.MintAsync = CreateAsaCoreAsync.
  - recommended: REJECT. Retire the Mint kind on both callers; settle in project-token units from an AZOA 'IssueUnits' kind (Wave 9) or not at all until then (Q4).
- **Both sponsor and project owner must be fully KYC-approved with a provisioned custodial wallet before checkout; worker and owner before a paid task is accepted; assignee on task create when equityReward>0.**
  - implied by: AzoaValueEligibilityService.IsReadyForValueAction (:46-55) called from FundingIntentService, TaskCommerceService, TasksController, OpportunitiesController; re-checked at every outbox dispatch with money parked on Retry if either lapses.
  - recommended: REJECT pending Q3; make readiness rail-aware (fiat needs no wallet), move worker gate to settlement, add a hold state instead of a Retry loop.
- **The hold that backs a worker's stake is reserved from the project's contributor pool (ProjectTokenConfig.contributorSupply), not the owner's personal shares and not a platform advance.**
  - implied by: Three sources on disk: TaskEscrow.shareId/funderId (owner shares), gated-commerce spec (project supply), Task Bounty DAG (treasury refund); none implemented; AcceptBidAsync reserves nothing.
  - recommended: Ratify contributor pool — matches 09-tokenomics and needs no per-owner wallet action.
- **ARDA is an internal accounting unit, never shown to a sponsor or worker; users are quoted in USD and ownership %.**
  - implied by: invest/page.tsx shows 'Informational ARDA equivalent' and 'Reference value per ARDA'; swap-preview/conversion-chain teach TOKEN -> ARDA -> TOKEN; ARDA has no genesis so those rows can never render a number.
  - recommended: Ratify (Q4); collapse ExchangeService to project-token -> USD; move ARDA/rate math behind an advanced statement view.
- **External Algorand wallets (Pera/Defly/WalletConnect) and Tinyman AMM swaps are NOT on the ArdaNova user path; project-token exchange is strictly treasury-rate.**
  - implied by: 'Wallets' is a first-class sidebar item for every user; /settings/wallets asks for a public address and a signed challenge; nothing backend depends on a primary external wallet; SwapService.ExecuteSwap is a stub; 09-tokenomics spec says 'no AMM'.
  - recommended: Ratify. Remove the nav item and route until a 'claim to external wallet' feature is designed; drop Tinyman from ArdaNova's path (fix the SDK decimals default regardless because the SDK ships publicly).
- **Tenant-provisioned child users receive an implied, revocable consent grant scoped to the provisioning tenant, created inside ProvisionChildAsync and surfaced/revocable from ArdaNova settings.**
  - implied by: ConsentController is FirstPartyLogin-only; TenantConsentController is GET/DELETE; ProvisionChildAsync creates no grant; KeyCustodyService refuses tenant-driven signing without a live grant — so either the user must log into AZOA to click 'allow' (thesis-hostile) or tenant-key ops on child users bypass consent entirely (unverified which branch fires).
  - recommended: Ratify implied grant; add a test that a plain tenant-key allocation against a child user with no grant is refused.
- **Backer visibility: aggregate (count + total) for non-members; itemised for project members; named only with per-pledge consent captured at checkout.**
  - implied by: InvestorTable lists 'User {12 chars of id}' + USD to any signed-in user; GET /api/projecttokens/{configId}/investors has no [Authorize]; ProjectInvestmentDto carries StripePaymentIntentId to that route.
  - recommended: Ratify aggregate-by-default; strip StripePaymentIntentId from the non-admin DTO regardless.
- **One idempotency-key convention for a funding event: `{avatarId}:funding-intent:{intentId}`.**
  - implied by: AzoaAllocationService uses the Stripe PaymentIntent id verbatim; the durable outbox uses `funding-intent:{intentId}`; a receipt reconcile must use whichever was sent.
  - recommended: Ratify the intent-bound form; delete AllocationKeys.FundingSettlement.
- **AZOA never prices; ArdaNova owns USD-at-settlement and may stamp its decided USD figure onto the AZOA operation via a 'TenantDeclared' rate source (rate + source + timestamp stored, nothing fabricated by the node).**
  - implied by: IValueRateSource has only UnpricedValueRateSource and is unregistered; Services/Analytics/AGENTS.md forbids a rate source; ArdaNova has the rate (ExchangeService) but never passes it.
  - recommended: Ratify; it keeps D16's invariant and makes ArdaNova ledger rows re-derivable for statements.
- **Identomat is the launch KYC vendor; Veriff/GenericHosted stay fail-closed stubs; KYB/entity screening has no vendor path and projects are treated as natural-person founders until Stripe Connect business verification is used for entities.**
  - implied by: Kyc:Provider=unavailable in base config; only IdentomatKycProviderService implements IHostedSessionProvider; D24 records no company-screening endpoint.
  - recommended: Ratify Identomat; record the KYB stance in product.md; revisit when a co-op raises funds as an entity.
- **The infrastructure vendor's name ('Azoa') and the custodial Algorand address are not shown to end users.**
  - implied by: verification page copy 'Azoa manages identity checks…'; custodial-account-card renders compactReference(walletAddress) and the avatar GUID.
  - recommended: Ratify: 'our verification partner', status shows 'Ready' / 'Not created' only; address behind an Advanced disclosure at most.
- **D26 (Ethereum, Solana SPL, Wormhole shipped against evidence) stands; not re-litigated. Explorer UI is retired from the ArdaNova roadmap in favour of a plain-language project ledger.**
  - implied by: RUNBOOK D26 owner direction; ARCHITECTURE.md:161,634,1743 still list an /explorer route.
  - recommended: Ratify both; weigh D26 only when sequencing Wave 9 effort.