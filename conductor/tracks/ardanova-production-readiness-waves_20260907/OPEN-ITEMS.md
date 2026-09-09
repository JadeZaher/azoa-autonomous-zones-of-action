---
type: Track Open Items
title: ArdaNova production-readiness waves — open items after closeout
description: Every stub, fail-closed seam, CRITICAL finding, operator action and piece of debt left by the 11-wave program, each with a file:line and exactly what unblocks it.
tags: [open-items, ardanova-production-readiness-waves_20260907, shipped-with-debt]
timestamp: 2026-09-08T00:00:00Z
resource: ./metadata.json
---

# Open items — ArdaNova production-readiness program

**Written 2026-09-08 at closeout.** The program executed all 11 waves and delivered 53 of
54 items across 18 commits (AZOA `7dbfc8a`…`8328c07`, ArdaNova `ea9b9d1`…`3ffa223`). Every
wave shipped with debt. This file exists because *an honest open-items list beats a tidy
document*: `plan.md`'s per-wave **As built** notes cite these ids, and nothing here is
recorded only as prose.

**How to read a row.** *Severity* is the adversarial reviewer's, not the implementer's.
**CRITICAL** = value can move wrongly, or private data is readable by someone who should
not read it, on a shipped configuration. **STUB** = a deliberate, labelled, fail-closed
seam — it refuses, it says why, and it names its own unblock; a stub is not a defect, an
*unlabelled* stub is. **OPERATOR** = nothing to build; someone with a credential or an
account has to act.

> **The single most dangerous class in this repo, restated.** *A claim in prose is not a
> control in code.* Before writing "X is gated/disabled/enforced/validated", grep for the
> enforcement. When you build a control, wire the thing that READS its result in the same
> change. When a guard has a self-healing path, ask what it heals *over*. AZOA has **two**
> dispatch seams — the legacy `ActionManager` and the durable `ActionNodeStepHandler` — and
> every gate must be enforced at BOTH. The reviews below found **sixteen** new instances of
> this class; the running ledger is `conductor/RUNBOOK.md` §2.

---

## A. Fix before anything else — CRITICAL, on a shipped configuration

- [ ] **OI-13** `CRITICAL` `ardanova` — **The MCP tools serve DRAFT projects to any caller.**
  *Where:* `ardanova-backend-api-mcp/api-server/src/ArdaNova.MCP/Tools/ProjectTools.cs:24,34`.
  `project_get_by_id` / `project_get_by_slug` call `IProjectService.GetByIdAsync` /
  `GetBySlugAsync`, which are the *deliberately unscoped* single-row lookups
  (`ProjectServices.cs:204,216` — "Authorization reads through this, so it must not hide
  drafts"). `ProjectTools` has no auth and no viewer. W2.1's claim that this seam is
  "public-only by construction" enumerated the seven LIST methods and missed the two the MCP
  actually calls. *Unblocks with:* give `ProjectTools` a viewer, or route it through
  `IProjectVisibility.GetVisibleByIdAsync`.

- [ ] **OI-14** `CRITICAL` `ardanova` — **A DRAFT project's field notes are readable by the
  anonymous internet.** *Where:* `ArdaNova.API/Controllers/PostsController.cs:41-51`
  (`[AllowAnonymous]`) → `PostService.GetProjectFieldNotesAsync` (`PostService.cs:91-114`),
  which filters on `projectId` + post visibility and **never consults project visibility**
  (grep for `DRAFT|ProjectStatus` in that file returns zero). The composer defaults to
  `PUBLIC` and is mounted on the draft's own page for any member. Discovery needs no prior
  knowledge: `post.getByUserId` is a `publicProcedure` returning raw rows including
  `projectId`. *Unblocks with:* one `GetVisibleByIdAsync(viewer, projectId)` guard at the top
  of `GetProjectFieldNotesAsync`, and the same guard on the five sibling sub-resource GETs
  (**OI-16**).

- [x] **OI-20** `CRITICAL` `azoa` — **The allocation seam draws a named reserve partition with
  no tenant-ownership guard.** *Closed 2026-09-08 (L0 remediation).* The first pass asked the
  question at two of the SEVEN doors and left the EXCHANGE door open — `ReserveExchangeManager`
  lifts `ProjectAssetId` and `ReservePartition` straight off the request body and hands both to
  `DrawAsync`. Fixed by moving the question to the one place a partition counter MOVES:
  `Services/Reserve/PlatformReserveService.cs:371` asks `ReservePartitionScope.Refuse` on the
  row it has already read, and `ReserveDrawRequest` now carries a REQUIRED positional
  `ReserveDrawAuthority` (`Services/Reserve/IPlatformReserve.cs:132-139`), so a new draw seam
  cannot compile without saying on whose authority it draws. All seven seams state one:
  `AllocationManager.cs:130,676`, `ReserveExchangeManager.cs:741,789,840,849`,
  `PlatformHoldService.cs:199,324,494`, `OffRampService.cs:506,973`,
  `ReversalCompensationService.cs:490`. Proofs: `PlatformReserveTests` "Draw authority" (8
  cases) and `ExchangeDrawAuthorityTests`.

  *Original finding:* *Where:* `Managers/AllocationManager.cs:636` —
  `_reserve.DrawAsync(new ReserveDrawRequest(chain, assetId, partition, …))` where BOTH
  `assetId` and `partition` come straight off the client body. The identical hold seam DOES
  guard it (`Services/Hold/PlatformHoldService.cs:125` → `GuardTenantScope`, :712-725). The
  ownership data exists (`Managers/FungibleTokenManager.cs:246` stamps `actingTenantId`); it
  is simply not read. *Effect once partitions exist:* tenant B drains tenant A's declared
  budget out of the shared reserve. *Unblocks with:* call `GuardTenantScope` (or the same
  predicate) from `AllocationManager` before the draw.

- [x] **OI-21** `CRITICAL` `azoa` — **`GuardTenantScope` fails OPEN on the platform unit
  itself.** *Closed 2026-09-08 (L0 remediation).* The first pass implemented "anyone whose
  acting tenant is absent", which is the same hole one step over: a user-driven quest run
  reaches the hold seam with `ActingTenantId = null` (`ActionNodeStepHandler.cs:212`,
  `ActionManager.cs:834,956` → `HoldNodeHandler.cs:100`) carrying an author-supplied asset id and
  partition name. The predicate now needs a POSITIVE operator signal
  (`ReserveDrawAuthority.NodeOperator`, `Services/Reserve/ReserveDrawAuthority.cs:50`) and
  refuses a caller with no resolvable tenant under EVERY policy
  (`ReserveDrawAuthority.cs:167-173`). Who else may draw the node's own unit is now an operator
  setting — `Reserve:PlatformUnitDrawPolicy`, defaulting to `OperatorOnly`
  (`PlatformReserveService.cs:67`, `appsettings.json:243-245`). The one production rail that
  legitimately draws with no tenant says so explicitly: `OnRampSettlementHandler.cs:388-394`.
  `HoldOpenRequest.DrawAuthority` and `IAllocationManager.AllocateAsync`'s `drawAuthority` both
  DEFAULT to the acting tenant, so a seam that says nothing gets the narrow answer. Proofs:
  `AllocationReserveTenantScopeTests` (both seams, both directions) and `OnRampSagaTests`
  "The_issuance_asks_the_reserve_on_the_operators_authority_and_not_on_a_missing_tenant".

  *Original finding:* *Where:* `Services/Hold/PlatformHoldService.cs:717-718` — `if (owner ==
  Guid.Empty) return null;`. ARDA is minted with `actingTenantId: null`
  (`Services/Reserve/PlatformTokenGenesis.cs:206-212`), so its ledger row has no tenant and
  every tenant holding `transfer:sign` may draw every declared partition of the node's own
  supply — the `contributor` and `investor` partitions W5.2 is slated to declare. *Unblocks
  with:* treat a null tenant on the platform unit as "operator only", not "anyone".

- [ ] **OI-36** `CRITICAL` `ardanova` — **The sponsorship cannot write its settlement row.**
  *Where:* `ArdaNova.Domain/Models/Enums/EconomicSettlementKind.cs:11` gained `EXCHANGE` and
  `HOLD_RELEASE`; `ardanova-client/prisma/schema.prisma:666` (the native Postgres enum) did
  not, and neither wave migration adds them. `FundingIntentService.cs:855` writes
  `kind = EconomicSettlementKind.EXCHANGE` → 22P02, which the catch at
  `FundingIntentService.cs:233-243` misreports as a uniqueness race. *Unblocks with:* a
  migration step of its own (`ALTER TYPE ... ADD VALUE` cannot share a transaction with the
  column add) followed by **OI-37**.

- [ ] **OI-37** `CRITICAL` `ardanova` — **Money moves and the ledger write throws.** *Where:*
  `ArdaNova.Domain/Models/Entities/EconomicSettlement.cs:63,66,69` —
  `azoaGrossBaseUnits` / `azoaFeeBaseUnits` / `azoaNetBaseUnits` are EF-mapped C# properties
  absent from `schema.prisma`, from every `migration.sql`, and from the preflight's own
  expectation list (`ardanova-client/src/lib/commerce/migration-preflight.ts:270-281`), which
  therefore passes on a database that lacks them. Once **OI-36** is hotfixed, dispatch
  succeeds at the node — the sponsor's ARDA IS debited — and then `CompleteAsync`'s
  `SaveChanges` hits 42703 and rolls back the stake, the treasury receipt, `fundingRaised`,
  Gate 1 and the settlement. The outbox then retries forever, replaying the same receipt.
  *Unblocks with:* `ALTER TABLE "EconomicSettlement" ADD COLUMN "azoaGrossBaseUnits" TEXT,
  ADD COLUMN "azoaFeeBaseUnits" TEXT, ADD COLUMN "azoaNetBaseUnits" TEXT;` plus the matching
  `schema.prisma` lines **and** the preflight entries. See **OI-80**: the "persistence
  contract" test cannot catch this class because it never reads a migration.

- [ ] **OI-40** `CRITICAL` `ardanova` — **The "Verified" credential badge is produced by the
  SIMULATED mint on the shipped default.** *Where:*
  `ArdaNova.Application/Services/Implementations/CredentialUtilityService.cs:80-143`. The node
  issuance port is null (**OI-55**), so control falls through to the address-based branch,
  which is gated only on `SupportsAddressBasedCredentialLifecycle` — true for both `Legacy`
  and `Simulated`. `src/ArdaNova.API/appsettings.json:10` ships `"Provider": "Simulated"`,
  which is also `AlgorandProviderSelection.Parse`'s default for an unset value, and
  `ValidateAlgorandProvider` has no production guard on it. `SimulatedAlgorandService`
  fabricates an assetId and txHash, `UpdateMintInfoAsync` persists them, and
  `credential-card.tsx:63-70` renders the member **"Verified — Independently verifiable
  outside ArdaNova"** with the fabricated id shown as a quotable "Reference". Under `Legacy`
  the ASA is real but minted to the PLATFORM's address, so it still proves nothing about the
  member. *Unblocks with:* gate the Verified state (and the mint fallback) on the node
  issuance path only, and add a production guard on `Simulated`.

- [ ] **OI-43** `CRITICAL` `both` — **A node REFUSAL of a sell freezes the seller's units, on
  the shipped default.** *Chain:* `appsettings.json` ships `Exchange:BuyBack.Enabled=false` →
  `Services/Reserve/ExchangeRatePolicy.cs:918` refuses with
  `AzoaErrorCodes.OperationNotAllowed` (as does *every* refusal
  `TreasuryRateExchangePolicy.Refuse` makes, :246-247) → `ExchangeController.cs:280` maps it
  to **409** → `AzoaNodeClient.cs:167` maps 409 to `Result.Conflict` →
  `PayoutService.cs:725-735`'s `declined` set does **not** contain `Conflict`, so :737-746
  parks the row `PROCESSING` with the units still locked and tells the seller "we could not
  confirm this sale just now… support will finish it". Nothing moved; AZOA said no. There is
  no self-service recovery (`PayoutService.cs:403` cancels only `PENDING`;
  `portfolio-contract.ts:48-58` offers no other exit). *Unblocks with:* either add `Conflict`
  to `declined`, or — better — give AZOA a distinct error code for "refused, nothing moved"
  and map it to 4xx-declined, keeping 409 for genuine ambiguity.

- [ ] **OI-51** `CRITICAL` `ardanova` — **The Money view's headline balance can never be read,
  and its refusal text is engineering prose shown to sponsors.** *Where:*
  `ArdaNova.Infrastructure/Azoa/AzoaExchangeClient.cs:120-137` is the registered binding and
  returns `Result.Failure` unconditionally → `TreasuryService.cs:729` →
  `WithoutChainBalance` → `ProjectLedgerService.cs:255-257` →
  `money-tab.tsx:166` renders `balance.unavailableReason` **verbatim** under "What this
  project holds": *"…cannot be read from the node with a tenant credential. AZOA must expose a
  consumer balance read an API-key principal can reach… (today only Operator-policy
  /api/admin/project-treasuries)…"*. That is a node, a tenant credential, an API key, an
  operator policy and two raw admin routes, shown to a non-crypto sponsor as the answer to
  "where is my money". *Unblocks with:* **OI-24**'s tenant-scoped balance route; until then,
  render a plain "Not available yet" and keep the diagnostic server-side.

- [x] **OI-52** `HIGH` `ardanova` — **The project ledger gates NAMES on membership, never
  DATA.** *Closed in L0.3; the sibling leak it left open is **OI-92**.*

  *Original finding:* *Where:* `ArdaNova.Application/Services/Implementations/ProjectLedgerService.cs:209`
  uses `IsProjectMemberAsync` solely to set `mayShowNames`; no entry is withheld.
  `ProjectLedgerController.cs:63` requires only an actor assertion. So any signed-in stranger
  receives every settled sponsorship amount and its USD reference, the units each sponsor
  bought and their release dates, every worker's award size, every buy-back and every
  `azoaOperationId`. The sibling endpoint written by the same programme branches the PAYLOAD
  (`ProjectTokensController.cs:304-312`: headcount + total for non-members). *Unblocks with:*
  branch the payload, not the name field.

---

## B. Cross-repo, release and environment

- [ ] **OI-01** `BLOCKER` `both` — **ArdaNova CI is red by construction and publishing alone
  will not fix it.** `ardanova-client/package.json:61` pins `azoa-sdk@^0.3.0`;
  `package-lock.json:33` and `:4942-4944` still say `0.1.0`. `npm ci` exits **EUSAGE on the
  desync before it reaches the registry**. The registry has only 0.1.0, 0.2.0, 0.2.1, 0.2.2 —
  0.3.0, 0.3.1 and 0.4.0 were all version-bumped in `package.json` and never published.
  `npm whoami` 401s with the token in `~/.npmrc`. *Unblocks with, in order:* (1) a valid
  npm publish token for `azoa-sdk`; (2) a dedicated **single-serial, non-subagent** step that
  runs `npm run build && npm publish` in `oasis-sleek/sdk/azoa-wallet` (its tsc and 204 vitest
  tests are green); (3) `npm install azoa-sdk@^0.4.0` in `ardanova-client` to regenerate the
  lockfile; (4) verify from a **clean clone with no `npm link` / symlink** via
  `npm ci && npm run build`. ⚠ Never re-copy the SDK into a consumer's `node_modules` — a copy
  is what went seven weeks stale before.

- [ ] **OI-02** `MEDIUM` `ardanova` — **Two documents describe a tree that no longer exists.**
  The Wave-0 commit `ea9b9d1`'s message asserts a lockfile fix that is not in the commit and
  cites lines that disprove it (`git show --stat ea9b9d1 -- ardanova-client/package*.json`
  returns empty). `ardanova/conductor/RUNBOOK.md:11` still reads `Branch HEAD: f2e4bc5`, `:12`
  "**81 uncommitted paths… Do not commit them blind**", and `:63` "the ~81 uncommitted files —
  do not commit or modify" — precisely the files that same commit committed. *Unblocks with:*
  a doc-only correction pass in ArdaNova's runbook.

- [ ] **OI-03** `BLOCKER` `ardanova` — **Nobody can run a validated client build locally.**
  `ardanova-client/.env` is missing `AUTH_URL`, `ADMIN_API_KEY` and
  `ACTOR_ASSERTION_HMAC_KEY`, has an `API_KEY` under the schema's 32-byte floor, and sets
  `NODE_TLS_REJECT_UNAUTHORIZED=0` which `src/env.js` rejects. Every wave gate in this program
  built with `SKIP_ENV_VALIDATION=1` (the repo's own documented escape, used by the
  Dockerfile, compose and CI). The code itself typechecks fully — `ignoreBuildErrors:false`.
  *Unblocks with:* populate `.env` from `.env.example` with generated 32-byte secrets. Do
  **not** weaken `src/env.js`.

- [ ] **OI-04** `LOW` `azoa` — **azoa-sdk 0.4.0 is bumped, not published**, so
  `frontend/src/app/(dashboard)/admin/fiat-reversals/page.tsx:27` hand-rolls its own
  `FiatReversalView` instead of importing the SDK type the wave added. Same unblock as
  **OI-01**.

---

## C. Operator actions — no code to write

- [ ] **OI-05** `OPERATOR` — **Stripe Connect platform approval** (Q-X5) is an external review.
  `Fiat:OffRamp:Enabled` defaults FALSE and both `OffRampService.IsAvailable()` and
  `PayoutAccountService.IsAvailable()` report "not available on this node" until it lands.
  Nothing is stubbed around it.
- [ ] **OI-06** `OPERATOR` `azoa` — **A Railway Production deploy from the shipped template
  fails at boot.** `deploy/railway/template.json:83` sets `Kyc__Provider=unavailable`;
  `Program.cs:167` → `KycRuntimeSafety.GuardProductionProviderIsReal` strips
  manual/mock/unavailable from the real-adapter set and THROWS in Production. This is the
  correct behaviour, and the template is wrong. *Do together:* set
  `Kyc__Provider=identomat` **and** update
  `tests/AZOA.WebAPI.Tests/Core/SurrealDeploymentLeastPrivilegeContractTests.cs:174`, which
  pins the string `"unavailable"`. Also set `Registration__AllowPublicSelfServiceInProduction=false`,
  `Kyc__Identomat__ReturnUrl` and `CustodialAccounts__CustodyMode=Platform`
  (`docs/NODE-HOST.md` §3, §8.2, §8.5, §8.10).
- [ ] **OI-08** `OPERATOR` `azoa` — **Identomat vendor credentials.** `Kyc__Identomat__CompanyKey`,
  `__ConfigId`, `__CallbackApiKey` on the `azoa-api` service require a real Identomat partner
  account. Until then identity verification cannot run against the vendor.
- [ ] **OI-09** `OPERATOR` `both` — **ArdaNova has no tenant provisioned on AZOA**, so
  `Azoa__CustodyApiKey` and `Azoa__TenantId` cannot be set, and
  `Azoa__EnableCustodialAccounts` was deliberately left false (flipping it without them 503s
  the whole `/ready` endpoint). *Procedure:* `docs/NODE-HOST.md` §8.6 — register the tenant
  user, mint a tenant-scoped API key with scopes
  `tenant:provision,wallet:manage,kyc:read,kyc:submit`, then set the three Railway variables
  on `Ardanova-BFF`. Note `appsettings.json:216-219` sets
  `Registration:AllowPublicSelfServiceInProduction=false` in Production, so this is
  deliberately not automatable.
- [ ] **OI-45** `OPERATOR` `azoa` — **Two things the fiat on-ramp needs before the first real
  card, neither of which the live-boot guard checks (see OI-29):** declare the `on-ramp`
  partition on the platform unit's reserve ledger (`IPlatformReserve.DeclarePartitionsAsync` —
  which has no REST surface, **OI-22**), and register a first price schedule via
  `POST /api/fiat/price-schedule` with `UnitsPerMinorUnit "100"` (the settled 1 ARDA = $0.01
  base at `Decimals = 2`). Missing either: the card is charged, five redeliveries burn, the
  lot parks `Stuck`, and per **OI-27** nobody sees it.
- [ ] **OI-82** `OPERATOR` `azoa` — `appsettings.json:204` ships `CustodyMode=Disabled`. A
  non-development node resolves custody unavailable and wallet provisioning will not proceed
  until an operator sets `Platform` (or `Node`) **and** a ≥32-char `AZOA__WalletEncryptionKey`.
  Correct fail-closed default, recorded so nobody assumes otherwise.
- [ ] **OI-77** `OPERATOR` `azoa` — **Rotating a chain's platform mnemonic MOVES every project
  treasury address.** The code fails closed and loud
  (`Services/Reserve/ProjectTreasuryService.cs:457`), but the rotation runbook must include
  re-deriving and sweeping every open project treasury *before* rotation.
  `docs/NODE-HOST.md` does not say so yet.

---

## D. Labelled fail-closed stubs — what each refuses, and what turns it on

Every stub below refuses, says why, and names its own unblock. None of them can be mistaken
for a working feature from the outside; several are the *only* reason a CRITICAL above is
latent rather than live.

- [ ] **OI-22** `STUB-SHAPED` `azoa` — **Nothing can declare a reserve partition, so the whole
  reserve/issuance/hold rail is inert.** `Services/Reserve/PlatformReserveService.cs:259
  DeclarePartitionsAsync` has zero production callers and no REST surface anywhere (no
  `PlatformReserveController` exists). `DrawAsync` fails closed on an undeclared partition
  (:373), so every `IssueUnits` allocation and every `Hold`/`Release` refuses at the first
  draw. *Unblocks with:* an operator/tenant route that declares partitions at
  fungible-token-create time, which is also what **OI-24** and **OI-45** need.
- [ ] **OI-24** `STUB` `both` — **No project can be provisioned.**
  `IAzoaProjectTokenNodeGateway` (declared at `ProjectTokenService.cs:1902`) has **zero
  implementations**; `ProvisionAsync` refuses at `:253` with `NodeGatewayUnwiredError` and
  mints nothing. It cannot be built today: AZOA's treasury-resolve and partition-declare
  surfaces are `[Authorize(Policy = "Operator")]` admin routes an API key can never reach.
  *Unblocks with:* (a) AZOA `POST /api/projects/treasury/resolve` (tenant-scoped, tenant id
  from the API key, never the body) and `POST /api/reserve/{assetId}/partitions`; (b)
  `ardanova/.../Infrastructure/Azoa/AzoaProjectTokenNodeGateway.cs` over `IAzoaNodeClient`;
  (c) one line in `Infrastructure/DependencyInjection.cs`.
- [ ] **OI-31** `STUB` `azoa` — **No exchange can settle.**
  `UnavailableExchangeGroupSubmitter` (`Services/Reserve/ExchangeBundleDefinition.cs:318-331`)
  returns `RAIL_UNAVAILABLE` for every chain. An exchange group is two assets, two signers and
  pooled fees; `Models/Blockchain/AtomicTransferGroup.cs:71-80` refuses anything but one
  asset / one source / one signing context. It never falls back to sequential transfers —
  that is exactly the funds-taken/units-undelivered failure atomicity exists to prevent.
  *Unblocks with:* a rail adapter implementing `IExchangeGroupSubmitter` (heterogeneous
  multi-signer group, fee pooling per `ExchangeGroupPlan.UserSignedLegCount`), registered
  before the `TryAdd` at `Program.cs:1723-1725`. **Fix OI-32 and OI-33 in the same change.**
- [ ] **OI-38** `STUB` `ardanova` — **No sponsor can obtain platform units at all.**
  `AzoaFiatRailGateway` is complete server-side and has **no HTTP surface**: no add-funds
  controller, no `apiClient` endpoint, no tRPC procedure.
  `ardanova-client/src/lib/commerce/on-ramp-status.ts:127 ADD_FUNDS_SERVER_ROUTE_PENDING`
  keeps the Continue button disabled and a return with `?intent=` is reported as "we can't
  confirm it" rather than guessed. *Unblocks with:* an `AddFundsController` over
  `IFiatRailGateway` (POST begin, GET status), an `apiClient` endpoint and a tRPC procedure —
  then delete the stub constant.
- [ ] **OI-55** `STUB` `ardanova` — **Credentials are granted but not independently issued.**
  `IAzoaCredentialIssuanceNode` is consumed at `CredentialUtilityService.cs:80,263` and has no
  registered adapter, so `_issuanceNode` is null. *Unblocks with:* (1)
  `Infrastructure/Azoa/AzoaCredentialIssuanceClient.cs` POSTing to
  `/api/credential/issue/{recipientAvatarId}` with a `UserNFTMintModel`-shaped body
  (`IsSoulbound=true`, `IsTransferable=false`), mapping `TokenId`→ProofReference,
  `Id`→IssuanceReference, `MintedDate`→IssuedAt; (2) allow that path in `AzoaNodePathPolicy`
  (`AzoaNodeClient.cs:63-81` hard-refuses everything but the fungible-mint path); (3) one DI
  line, plus a `dapp:manager` + `nft:mint` scoped key. **Fix OI-40 in the same change** — the
  fallback is worse than the gap.
- [ ] **OI-56** `STUB` `ardanova` — **The withdraw leg has no transport.** `IAzoaCashOutNode`
  (`PayoutService.cs:83`) has no implementation; `PayoutService.cs:1206` refuses by name
  (`NoCashOutConfigured`) and `PayoutsController.cs:197` turns a missing surface into a stated
  503. Everything above the port is real. *Unblocks with:* the adapter file in
  `ArdaNova.Infrastructure/Azoa/` plus one DI line.
- [ ] **OI-59** `STUB` `azoa` — **Rung 2 of the reversal recovery ladder refuses.**
  `UnavailableReversedPurchaseProvenance`
  (`Services/Fiat/Reversal/ReversedPurchaseProvenance.cs:80-107`) reports `IsAvailable ==
  false` and returns nothing, because refunding an arbitrary hold a disputing sponsor happens
  to benefit from would rob whichever honest backer actually funded it. Both unwinds are
  fully built. *Unblocks with:* register a real `IReversedPurchaseProvenance` (the per-lot
  link from `on_ramp_lot` to what its units bought) before `AddFiatReversalPipeline`.
- [ ] **OI-60** `STUB` `azoa` — **The buy-back backstop is a seam with no implementation.**
  `IBuyBackBackstop` (`Services/Reserve/ExchangeRatePolicy.cs:835`) is unregistered, so the
  policy never admits a sell the project's treasury cannot itself pay, and the declared
  backstop partition only decides whether a shortfall may be *promised* a fill (see
  **OI-44**). *Unblocks with:* a reserve-to-treasury transfer on the exchange settlement path.
- [ ] **OI-61** `STUB` `ardanova` — **Operator alerts go to the log.** `IOperatorAlertService`
  has no pager implementation and no DI registration; `EconomicOutboxDispatchService.cs:180`
  falls back to `LoggingOperatorAlertService`, so alerts are live with zero registration but
  reach nobody's phone. *Unblocks with:* `services.AddScoped<IOperatorAlertService, …>()`.
- [ ] **OI-62** `STUB-BY-DESIGN` `azoa` — **Selling is refused platform-wide** because no
  `IExchangeLiquidityPolicy` is registered. This is the whole of "users cannot sell yet" and
  is deliberate; W8.2 registers the policy.
- [ ] **OI-68** `STUB` `ardanova` — **Guild bids cannot enter commerce.** A hold has exactly one
  beneficiary and a bid records no split, so a paid guild bid is refused at BOTH ends
  (`OpportunityBidService.cs:261`, `TaskCommerceService.cs:120`). *Unblocks with:* a
  `guildAwardSplit` column on `OpportunityBid` plus the DTO field, then N holds.
- [ ] **OI-47b** `SCOPED` `azoa` — **Project-treasury child-key derivation is Algorand-only.**
  Solana's key is a seed-concat-pubkey pair and Ethereum's a range-checked secp256k1 scalar;
  both fail closed naming the chain (`ProjectTreasuryService.cs:403,564`). Matches the node's
  real posture (Algorand is the only chain with real value). *Unblocks with:* a verified
  Solnet seed→keypair signature and a per-chain post-derivation hook.

---

## E. New instances of "a claim in prose is not a control in code"

Sixteen, found by the wave reviews. Recorded here with their fix shape; the running class
ledger is `conductor/RUNBOOK.md` §2.

- [ ] **OI-10** `HIGH` `both` — **`UnderReview` is plumbed end to end and read by nothing a
  person can see.** The C# enum, the DTO, the service, the gateway and the AZOA SDK all gained
  it; `ardanova-client/src/lib/api/ardanova/endpoints/azoa-custodial-account.ts:3` still reads
  `"Unknown" | "Pending" | "Approved" | "Rejected"` and has no `reviewDueAt`. Grep for
  `reviewDueAt` across both repos returns five plumbing sites and **zero readers**.
  Consequence: `AzoaValueEligibilityService.cs:60-61` maps any non-blank `UnavailableReason`
  to `Conflict`, whose person-facing text is *"This is not a problem with your account —
  please try again shortly."* So someone held for a multi-day sanctions decision is told a
  permanent state is a transient outage, forever, while
  `custodial-account-card.tsx:58-66,77` tells them to redo an identity check they passed and
  prints the raw enum `UnderReview`. *Unblocks with:* extend the client union + DTO, give
  `UnderReview` its own copy, and — with **OI-07** — a route to a human.
- [ ] **OI-11** `HIGH` `azoa` — **Case-insensitive registration uniqueness is enforced on one of
  three writer paths, and the rule it replaced was stronger.** `Managers/UserManager.cs:39,71`
  canonicalises; the new SurrealDB indexes are case-**sensitive**;
  `Managers/TenantManager.cs:116` writes `model.Email.Trim()` with no case folding. So
  `Alice@Example.com` and `alice@example.com` both persist, and `UserManager.cs:99` then
  resolves login with an `OrdinalIgnoreCase` `FirstOrDefault` — an ambiguous pick between two
  password hashes. The deleted O(n) scan would have rejected the second registration.
  Secondary: `DescribeRegistrationFailureAsync` reintroduces `GetAllAsync` on the failure
  path, so an anonymous caller can force a full-table scan.
- [ ] **OI-12** `MEDIUM` `azoa` — **Two key-minting seams, one gate.**
  `Managers/WalletManager.cs:382-384` gates `GenerateWalletAsync`;
  `BootstrapWalletAsync` (:423) mints at :438 with **no custody check at the manager**, gated
  only at its single caller. It is on the public interface
  (`Interfaces/Managers/IWalletManager.cs:22`), so the second caller inherits nothing.
- [ ] **OI-15** `HIGH` `ardanova` — **The publish rule is enforced at one seam.**
  `ProjectServices.cs:482-483` guards `PublishAsync` against a CANCELLED project;
  `UpdateAsync` at :440 is `if (dto.Status.HasValue) project.status = dto.Status.Value;` with
  no transition check, then stamps `publishedAt` at :445. `status` is on the wire at both
  entry points. Any project manager can move CANCELLED → PUBLISHED, bypassing `PublishAsync`
  and its idempotency.
- [ ] **OI-23** `HIGH` `azoa` — **W3.3 and W3.6 cancel each other.**
  `Controllers/AllocationController.cs:215` maps `IssueUnits` to `AzoaScopes.TransferSign` and
  403s without a live grant carrying it; `Managers/ConsentManager.cs:88-97` **rejects** any
  value-signing scope under `Participation` origin, which is the origin of the implied grant
  W3.6 exists to issue (pinned by `TenantProvisionedGrantTests.cs:73`). `IssueUnits` is the
  default `AllocationKind`, so it 403s for every custodially onboarded person. Ships green
  because `AllocationConsentGateTests` mocks `ITenantConsentGate` and has no `IssueUnits`
  case. *Root cause:* an issuance credits the recipient and signs with the PLATFORM key — an
  outbound value-SPENDING scope is the wrong label for it.
- [ ] **OI-25** `HIGH` `azoa` — **The hold sweep's "no acting tenant" claim is false.**
  `HoldSweepWorker.cs:127` passes `ActingTenantId: null`, and
  `PlatformHoldService.cs:752` absorbs it (`actingTenantId ?? ParseUserId(row.TenantId)`), so
  `ActingAs(tenant, TransferSign, beneficiary)` is attached anyway. The sweep therefore
  inherits **OI-23** and cannot pay out a first-time custodial beneficiary at all — which is
  the whole point of "this is yours in N days".
- [ ] **OI-27** `HIGH` `azoa` — **Instance seven: the read that surfaces "charged and not
  credited" has zero production callers.** `IOnRampLotLedger.ListNeedingOperatorAsync`
  (`Services/Fiat/OnRamp/OnRampLotLedger.cs:70`, impl :209, filtering `{Refused, Stuck}`
  :216) is referenced only by two test fakes. No controller, no admin page, no SDK method
  touches `on_ramp_lot`. Meanwhile EVERY charged-and-uncredited outcome parks the lot and then
  tells Stripe to **stop retrying** (`OnRampSettlementHandler.cs:283,293,318,327,339,363` and
  the redelivery short-circuit at :143). The write is durable; nothing reads it. Compare the
  reversal side, which DID ship its reader.
- [ ] **OI-28** `HIGH` `azoa` — **The account pause is enforced at neither dispatch seam.**
  `RiskHoldValueGate` wraps `IRealValueKycGate`, whose only three production consumers are
  checkout begin, the on-ramp credit and the bridge. `grep -rln 'IRealValueKycGate|RiskHold'
  Services/Actions/` returns **nothing** — not one of ~45 node handlers consults it. A flagged
  payer can move the disputed units through the workflow engine while the compensation saga is
  still walking its ladder, and once the units leave the balance rung 2 (**OI-59**) is a stub,
  so the reversal falls straight to a recorded platform loss.
- [ ] **OI-29** `MEDIUM` `azoa` — **The live-boot guard proves registration, not capability.**
  `FiatRailStartup.Validate` (`Providers/Fiat/FiatRailProvider.cs:1046`) refuses live boot
  without an `IFiatSettlementHandler` and is documented as meaning "a node cannot charge a
  real card while nothing is able to credit it". It checks that a TYPE is in the container.
  The two things that actually decide creditability — a declared `on-ramp` partition and a
  registered price schedule (**OI-45**) — are unchecked, and `docs/FIAT-RAIL.md:97-101`
  falsifies its own guarantee four lines later. Both are checkable at boot; both stores are
  already in the container.
- [ ] **OI-32** `HIGH` `azoa` — **A reconciler-confirmed exchange loses every money field on its
  own receipt route.** `Managers/ReserveExchangeManager.cs` `BuildOperation` (:940-1010) sets
  fourteen parameters and never sets `IdempotencyParameterNames.ResultPayload`, unlike every
  sibling (`AllocationManager.cs:492,556,673`; `CredentialManager.cs:240`).
  `ReconciliationService.cs:853-857` then falls back to the raw tx hash, so
  `GET /api/exchange/receipt/{key}` returns `Completed` with `AmountSpent`, `AmountReceived`,
  `ProjectCreditAmount`, `Rate`, `RateSource` and `SettlementReference` **all null**, and a
  redelivered POST returns *"the original exchange result could not be replayed"* for an
  exchange that succeeded.
- [ ] **OI-33** `HIGH` `azoa` — **An accepted-but-unconfirmed group that later fails has no
  compensation.** `ReserveExchangeManager.cs:353-360` records the project-treasury credit on
  ANY non-error `SubmitAsync` result; the `submission.Confirmed` check is at :404.
  `CompensateAsync` (:791-848) is reachable only from :335 and :345, i.e. before the rail
  accepted anything. The reconciler observes and never mutates by design. So a dropped group
  leaves the treasury permanently credited and the partition permanently drawn — and because
  that same treasury balance is the availability control for Sell, a later sell pays real
  units out against a phantom credit.
- [ ] **OI-35** `MEDIUM` `ardanova` — **`TreasuryService.ReconcileAsync` reports success having
  reconciled nothing.** Its two refusals fire on "no read model" and "unreadable", never on an
  empty set — and the set is *always* empty today, because the only writer of
  `treasuryReference`/`treasuryChain` is unreachable (**OI-24**). So the loop body never runs
  and :357-361 stamps `lastReconciliationAt = UtcNow` and returns success. Both regression
  tests arrange at least one project.
- [ ] **OI-41** `HIGH` `ardanova` — **The worker's 90-day restriction is written to a field
  nothing reads.** `ProjectTokenService.cs:1250` computes `allocation.isLiquid` from the
  window AND the gate. Grep returns **zero readers** of `TokenAllocation.isLiquid` on any
  spend path: `SwapService.cs:76` and `PayoutService.cs:51` both read
  `TokenBalance.isLiquid`, which `TokenBalanceService.cs:184,335` recomputes from the **gate
  alone**, in the very same approval call. And `ProjectGateService.cs:87` bulk-sets every
  contributor allocation liquid on gate activation. Not exploitable today only because both
  spend seams are disabled; the user-visible contradiction already ships ("you can move it
  from ⟨date⟩" beside a card that reads the units as liquid).
- [ ] **OI-42** `HIGH` `ardanova` — **The whole escrow lifecycle gates on a state nothing can
  produce.** `TaskEscrowServices.cs:154,188,227` all require `FUNDED`; `CreateAsync` hardcodes
  `NONE` and its route is 501; the only production writer of `FUNDED` is inside the dispute
  REJECT branch, which requires DISPUTED, which requires FUNDED — a closed loop with no entry.
  `ReleaseDialog`, `EscrowDetailCard` and `DisputeForm` are mounted nowhere. The wave also
  **deleted** the honest "read-only contract" banner from `src/app/admin/disputes/page.tsx`
  and replaced it with copy asserting the page adjudicates.
- [ ] **OI-46** `HIGH` `ardanova` — **One member can manufacture a decision recorded as PASSED.**
  Quorum floors at 1% (`GovernanceServices.cs:638`), so `VotesNeededForQuorum(40,1) = 1`
  (:534). The proposal's AUTHOR may publish it (`ProjectsController.cs:904`), vote on it, and
  CLOSE it (`ProjectsController.cs:864` explicitly allows `proposal.CreatorId == ActorId`),
  and close pulls `votingEnd` to now and resolves in the same request. With one ballot the
  threshold test passes at any threshold up to 100. Members 2..N never see the vote open. No
  test covers author-close. It moves no money — but the decision of record is falsifiable by
  one person.
- [ ] **OI-69** `MEDIUM` `azoa` — **The SDK "parity" guard asserts the SDK against itself.**
  `sdk/azoa-wallet/src/api/parity-w10-3.test.ts` stubs `fetch` and compares the produced URL
  against a string literal typed into the same file; it never reads a `.cs` file or a route
  attribute. Renaming `[HttpGet("availability")]` on `FiatPayoutController` leaves all 13
  tests green. The only real route-vs-constant check was a one-off human grep — the same shape
  as the previously-logged "CI grep that existed nowhere". *Unblocks with:* read the
  controller sources in the test, or stop calling it a controller-parity guard.

---

## F. Thesis defects — a non-crypto sponsor still meets the chain

The practical test: a wallet address, a mnemonic, a gas fee, a chain name, a signed challenge
or the word "token" where "funds"/"stake"/"units" would do is a **defect**.

- [ ] **OI-19** `OWNER RULING` — **The ask is entered in ARDA, with dollars as a derived
  caption.** `projects/create/page.tsx:1787-1789` welds an `ARDA` badge to the funding-goal
  input and :2929-2932 prints "500,000 ARDA — about $5,000 at today's rate", so a founder
  asking for $5,000 types `500000`. `project-create-contract.ts:523-524` says "the wizard
  shows a sponsor the dollar figure, not this code" and :525 exports the ticker. **This is
  plan text faithfully implemented** (W2.4: "denominated in ARDA, with a USD reference"), so
  it needs an owner ruling, not a bug fix. The settled rate base says users think in dollars.
- [ ] **OI-54** `HIGH` `ardanova` — `components/credentials/credential-card.tsx:135-141` renders
  `assetId ?? mintTxHash` as the visible value of a "Reference" row in `font-mono` — a base32
  Algorand id in front of a member. Under the shipped provider it is also fabricated
  (**OI-40**). The claim that the field is "never rendered as text" is false.
- [ ] **OI-85** `HIGH` `ardanova` — `app/credentials/[id]/page.tsx:148,160,164,183-197,210`
  renders "Blockchain Status", "Minted on Algorand", "…verified on the Algorand blockchain",
  raw `mintTxHash`/`revokeTxHash` in mono, and "Algorand Standard Asset — ASA #{assetId}";
  `components/credentials/credential-card.tsx:137-141` repeats `ASA #`. Five chain words and
  two transaction hashes on a page any member reaches from their own credential.
- [ ] **OI-84** `MEDIUM` `azoa` — `frontend/src/components/layout/sidebar.tsx:60-77` renders an
  **un-role-filtered** nav with a "DeFi" group containing "Blockchain", "Swap" and "Bridge",
  plus "Holon Query". Pre-existing, but Wave 4 added two links into that same ungated
  structure without fixing it.
- [ ] **OI-83** `MEDIUM` `ardanova` — `app/portfolio/page.tsx:404` still renders
  `shortenReference(payout.sourceProjectTokenConfigId)` as `abcd1234…ef5678` under "Source
  configuration" (full GUID in the tooltip at :402) — the exact string the Wave-6 commit
  message claims was removed. Same page: "tokenomics API", "A token balance is not proof of
  equity" (:136), "Token balance records" (:285), a "Tokens" column (:377), `Wallet` icons.
- [ ] **OI-49** `MEDIUM` `ardanova` — `app/governance/page.tsx:453,884` render the raw wire enum
  **`SHARES`** (the client-side map is keyed on `"Token"`, which the serialiser never emits),
  and :948-949 render "⟨n⟩ total voting power" — the exact vocabulary W9.1 claims is gone from
  both tabs. It is gone from the tabs and alive on the browse page the same commit edited.
- [ ] **OI-30** `MEDIUM` `azoa` — **The rail-vocabulary scrubber is called at 1 of 15 exits.**
  `ConsumerSafeErrors.ToConsumerSafeFailure` is invoked only at
  `Managers/AllocationManager.cs:238`; the other fourteen failure returns pass raw text
  through. Concretely :131 returns `NodeGovernanceGuard.cs:114-116` verbatim — *"Node
  governance disallows allocation:IssueUnits on chain 'Algorand'. Allowed chains: Algorand,
  Solana."* — which the wave's own `LeaksRailDetail` guard would flag on three terms.
- [ ] **OI-86** `LOW` `azoa` — `Controllers/HoldController.cs:429-431` documents `HoldView` as
  carrying "NO address and NO rail reference"; fields two and three are `string Chain` and
  `string AssetId`. Address is genuinely omitted; the second half of the sentence is not true.
- [ ] **OI-87** `LOW` `azoa` — `frontend/src/components/action-builder/node-catalog.ts:136-139`
  — the four new Escrow node labels are excellent ("Set Funds Aside", "Pay Out", "Give Back",
  "Hand On") but their `defaultConfigs` put `chainType: 'Algorand'`, `assetId`,
  `reservePartition` and a raw `beneficiaryUserId` GUID in front of a project owner; the
  builder's chain-name surface went from 3 to 7 occurrences.
- [ ] **OI-88** `LOW` `ardanova` — five user-visible strings on the sponsorship flow say "the
  node" (`invest/page.tsx:674,790,797`, `invest/success/page.tsx:160,176`), and
  `components/equity/portfolio-holding-card.tsx:70-73` shows "API: liquid" / "API:
  time-locked" — developer jargon in a member-facing badge.
- [ ] **OI-89** `MEDIUM` `ardanova` — `TreasuryService.cs:647-649,663-668` put `TreasuryAddress`
  (a chain wallet address) and `IsChainBacked` on `ProjectBuyBackFigures`, exposed at
  `TreasuryController.cs:88,102` to any authenticated user with no project scoping. A curious
  sponsor is one API call from a raw chain address.
- [ ] **OI-90** `LOW` `ardanova` — `projects/[slug]/commerce/page.tsx` tells a worker "It is
  held until then so you are not taxed on it before you can actually realise it" — unqualified
  tax advice rendered as fact.

---

## G. Correctness and hygiene debt

- [ ] **OI-07** `HIGH` `azoa` — **The AML manual-review queue has a UI and no backend.**
  `KycControlPlaneManager.ListAmlReviewsAsync` / `DecideAmlReviewAsync` and the
  `api/node-operator/kyc/aml-reviews[/{userId}/decision]` routes do not exist.
  `frontend/src/app/(dashboard)/kyc/page.tsx:369` detects 404/501 and renders an amber banner;
  flagged people stay `UnderReview` and every value gate stays closed. It fails closed, but a
  person under review has **no route to a human** (compounds **OI-10**). *Also:* the panel
  belongs in `frontend/src/app/operator/(console)/reviews/page.tsx`, not the tenant dashboard.
  *Design note:* no schema change is needed — a decision writes a new screening row that
  supersedes by `screened_at`.
- [ ] **OI-16** `HIGH` `ardanova` — **Draft sub-resources still leak.**
  `GET /api/projects/{projectId}/{resources|milestones|members|updates|comments}` are not
  gated by project visibility and the client calls them from public procedures. One
  `GetVisibleByIdAsync` guard at the top of each.
- [ ] **OI-17** `MEDIUM` `ardanova` — `LocalStorageService` enforces **no** extension and **no**
  size limit (unlike `S3StorageService`) and `Local` is the DEFAULT when `STORAGE_PROVIDER` is
  unset — the same two-seam defect one level down. It also cannot serve a presigned PUT
  (`GetPresignedUploadUrlAsync` returns a multipart POST endpoint), so the wizard's upload
  fails under `Storage:Provider=Local` with an honest message.
- [ ] **OI-18** `MEDIUM` `ardanova` — `AttachmentsController.cs:65,84` takes the owner from
  `[FromHeader(Name = "X-User-Id")]` with no class-level `[Authorize]`, so any holder of the
  shared API key mints presigned upload URLs under any user's prefix. The claim that
  "attachment ownership is bound to the authenticated caller" holds only for the tRPC route.
  The 25 MB cap is conditional (`FileSize` is optional) and advisory (a presigned PUT carries
  no content-length condition).
- [ ] **OI-26** `MUST SETTLE BEFORE MAINNET GENESIS` `azoa` — **Genesis supply is capped at
  `int.MaxValue` base units** by `IAlgorandASAModule.CreateASAAsync(int total)`;
  `FungibleTokenManager.TryValidateSupply:401` refuses above it and
  `PlatformTokenGenesis.cs:526-533` refuses with `PLATFORM_TOKEN_SUPPLY_CEILING`.
  `PlatformTokenGenesisDefaults.TotalBaseUnits` is 2,000,000,000 (20,000,000.00 units at 2
  decimals). **Widening this after genesis does not raise a minted asset's supply — it
  requires a second asset.** *Unblocks with:* widen `CreateASAAsync`'s total to
  `ulong`/`BigInteger`, `TryValidateSupply`, and the `checked((int)…)` cast, together.
- [ ] **OI-34** `HIGH` `ardanova` — **The ARDA rate divides by an unguarded zero.**
  `ExchangeService.GetArdaValueExactAsync` (:384-404) guards only
  `ardaCirculatingSupply == 0`; a treasury whose three bucket balances are all `0.0` — the
  genesis state — returns `Success(0m)`, and `:277` / `:315` then divide by it →
  `DivideByZeroException`, an unhandled 500 on a value path. `SwapService.cs:98-99` added
  exactly that `<= 0` guard over the identical computation in the same commit. *Deeper:* those
  backing figures are the same database doubles Wave 5 nulls out of `GetStatusAsync` as too
  untrustworthy to **show** — yet they price every rate ArdaNova declares to the node.
- [ ] **OI-39** `MEDIUM` `ardanova` — **Consent revoke is prose.** The AZOA route exists
  (`DELETE /api/tenant/consent/provisioned`); reaching it needs a gateway method, a service, a
  controller, an apiClient endpoint and a tRPC mutation. `settings/verification/page.tsx:486`
  states the permission and the standing right to withdraw it and **disables** the button with
  a visible note.
- [ ] **OI-44** `HIGH` `azoa` — **The buy-back queue is a promise no code can keep.** (a) The
  seller is never told their place: `QueueAsync` composes "you are number ⟨n⟩ in line" and
  returns it under `ConsumerSafeErrorCodes.InsufficientFunds`, which
  `ExchangeController.Translate` does not map, so it falls to `BadRequest`, and ArdaNova
  re-words every `BadRequest` by kind. (b) Nothing drains it: `IBuyBackBackstop` has no
  implementation (**OI-60**), so "backed by the platform, so it will be filled in turn" cannot
  come true. (c) A retry does **not** keep its place — ArdaNova derives the correlation from a
  fresh `Guid.NewGuid()` row, so each retry takes a new place while the abandoned one holds
  capacity for `QueueEntryTtl` (24h) and is summed into `reserved`, making the next attempt
  strictly harder. (d) The backstop is never decremented, so one declared partition "backs"
  every queued seller simultaneously.
- [ ] **OI-47** `MEDIUM` `azoa` — **`InteractAsync` is a third governance write seam with no
  electorate check.** `HolonManager` applies `ProposalElectorateRejection` on Create, Update,
  Get and `ScopeReadable`; `InteractAsync` (:256-313) gets only the FREEZE, and on an unvoted
  proposal the freeze is `Unclaimed`. `POST /api/holon/{id}/interact` with
  `SetMetadata { electorateTenant: "node" }` writes exactly the row W9.2 says cannot exist.
- [ ] **OI-48** `MEDIUM` `azoa` — **The proposal's QUESTION is not frozen.** Commit `924ec47`
  says "the question, its options, its electorate and its uniqueness domain stop moving", and
  `Services/Governance/AGENTS.md:151` says moving the question is the decisive attack. The
  question lives in `Holon.Name`/`Description`; `FrozenProposalMetadataKeys` is metadata-only
  and `UpdateAsync` merges Name/Description **after** the freeze check. A test pins this as
  deliberate (`GovernanceProposalRewriteTests.cs:404`), so it is a disclosed decision — but
  the author can still change "Should we fund the roof repair?" to "Should we dissolve the
  co-op?" after the ballots are in.
- [ ] **OI-50** `PROCESS` `azoa` — **The governance phase has no independent verdict.** Phase 4
  of `conductor/tracks/governance-vote-of-record_20260906/plan.md` requires an adversarial
  review in a **separate context**; W9.2 wrote the code and the join gate verified and
  extended it, so neither can be its reviewer. The three governance node types stay in
  `NotEnabledActionNodeTypes` until it lands.
- [ ] **OI-53** `MEDIUM` `ardanova` — **The sponsor-name consent flag has no writer.**
  `ProjectInvestment.showBackerName` is READ at `ProjectLedgerService.cs:303` and
  `ProjectTokensController.cs:323`, declared at `ProjectInvestment.cs:50`, and set by nothing:
  neither `CreateFundingIntentDto`, nor `createCheckoutInput`, nor `BuildInvestment` carries
  it; the invest-page checkbox is local component state. The column is permanently `false`.
  Direction of failure is safe (no name is ever shown), but the naming feature is decorative,
  and `ProjectLedgerService.cs:442` separately hard-codes `consented: true` on the
  TokenAllocation naming path, which is the one place it is NOT safe.
- [ ] **OI-57** `BLOCKER` `ardanova` — **A Prisma migration for `PayoutRequest`'s two exact-money
  columns was never written**; every `PayoutRequest` read throws until it lands. This is the
  one Wave-8 handoff that fails at RUNTIME, not at build time.
- [ ] **OI-58** `MEDIUM` `ardanova` — **The retired card rail is orphaned, not gone.**
  `IStripeService` is still registered (`Application/DependencyInjection.cs:143`) and
  `StripeService.HandlePaymentSucceededAsync` — which creates `ProjectInvestment`, allocates
  tokens, credits balance and evaluates Gate 1 — is still fully implemented with zero callers.
  `ProjectInvestment.stripePaymentIntentId` and the `StripeWebhookEvent` entity remain. One
  new controller action re-opens a complete second money path outside every control this
  program built. W8.3 was to delete it; four dangling references
  (`IStripeService.cs`, `StripeDtos.cs`, `StripeServiceTests.cs`, the DI line) remain.
- [ ] **OI-63** `MEDIUM` `azoa` — `Managers/BlockchainOperationManager.cs:227-233` —
  `DeriveIdempotencyKey`'s Exchange arm hashes `SourceTokenId`, `TargetTokenId` and
  `ExchangeRate` but **not the amount**, so two exchanges of different sizes at the same rate
  collapse onto one key and the second replays the first's result without executing. Pinned by
  `IdempotencyKeyStabilityGoldenTests`, so the key change and the golden update must move
  together.
- [ ] **OI-64** `MEDIUM` `azoa` — `Services/Fiat/TenantPriceScheduleStore.cs:422` — the D16
  stamp goes dark on the second tenant: `TryGetLatestAsync` returns null (recorded as "never
  priced") whenever more than one scope holds a current price for the same (chain, asset).
  Since the on-ramp prices the single platform unit, the moment a second tenant registers a
  schedule every stamped operation node-wide becomes unpriced, on a `LogWarning` with no
  alert. The refusal is right; the claim "the price that sold the units stamps the operation"
  holds only on a single-tenant node, which is not this model.
- [ ] **OI-65** `MEDIUM` `azoa` — `Services/Fiat/StripeWebhookInbox.cs:152` maps
  `AcceptedPending` → `FiatEventStatuses.Received` "so a sweep or a redelivery picks it up",
  while :143 answers 202 meaning "stop retrying". Grep for `FiatEventStatuses.Received` returns
  the writer and no reader: no sweep, no operator surface over `fiat_event`.
- [ ] **OI-66** `MEDIUM` `ardanova` — **Nothing in the product can approve a worker's
  completion.** `approveWorkCompletion` exists end to end (controller, service, tRPC) and
  `grep "api.projectTokens."` over the client returns no caller, so the worker's commerce page
  reads "Yours once the work is approved" permanently.
- [ ] **OI-67** `MEDIUM` `ardanova` — **"New proposals are blocked once the opportunity is not
  OPEN" is a client render gate only.** `OpportunityBidService.CreateAsync` never reads
  `opportunity.status`; the tRPC procedure checks only guild membership. A direct call posts a
  bid on a CLOSED or FILLED opportunity, and `AcceptAsync` gates only on BID status.
- [ ] **OI-70** `LOW` `azoa` — "No token or API key participates in any of these calls" is false:
  the node-transparency SDK methods go through the same `request()` path, which sets
  `Authorization: Bearer` whenever `config.token` is present. The supporting test proves a
  property of a token-less fixture.
- [ ] **OI-71** `LOW` `ardanova` — `ProjectLedgerController.cs:32-55` keeps a hand-composition
  fallback (`registered ?? new ProjectLedgerService(...)`), so a missing DI registration
  becomes invisible instead of a startup failure, and the hand-built instance can silently
  diverge. Self-healing over the failure it should surface.
- [ ] **OI-72** `MEDIUM` `azoa` — `Services/Custody/KeyCustodyService.cs:206` — the
  `WithPlatformSigningKeyAsync(bool, Func)` overload runs **no consent gate**, unlike its three
  siblings at :123/:185/:198. Needs a caller grep and, if any tenant-driven op reaches it, the
  gate.
- [ ] **OI-73** `LOW` `azoa` — `Core/AzoaScopes.cs:130` — `OperationScopeMap["Burn"] = { nft:mint }`,
  so the `nft:mint` in the tenant-provisioned implied grant would also authorise a
  tenant-driven BURN. Nothing is open today (`NftManager.cs:249-260` builds Burn with no
  `ActingTenantId`), but any future tenant-driven burn needs its own scope first.
- [ ] **OI-74** `LOW` `azoa` — Two configuration fail-opens: `ReserveOptions.ExpectedAddresses`
  ships blank so the reserve address-mismatch guard is inert until an operator fills
  `Reserve__ExpectedAddresses__Algorand`; and `_options.Chains.Count == 0` means *no
  restriction* (`PlatformReserveService.cs:409`, mirrored at `PlatformHoldService.cs:704`), so
  any environment overriding the `Reserve` section without repeating `Chains` silently reverts
  to unrestricted.
- [ ] **OI-75** `LOW` `azoa` — The draw replay window is a 128-entry FIFO shared by all draws
  against one asset's ledger row (`PlatformReserveService.cs:78`, evicted :385-387). A retried
  draw whose key has aged out is applied a second time. Both live callers hold outer
  idempotency anchors, so the claim "a retried draw cannot double-count a partition" is
  unconditional where the code is not.
- [ ] **OI-76** `LOW` `azoa` — `Persistence/SurrealDb/Generated/Schemas/kyc_authority_guard.surql`
  is an **orphan golden**: a real, live table created by a hand-written migration with no
  attributed POCO. The byte-equivalence guard iterates POCOs, not goldens, so an orphan passes
  silently. Do not delete it (the table is read); give it a POCO or an explicit allowlist.
- [ ] **OI-78** `LOW` `ardanova` — `ardanova-client/prisma/schema.prisma` is not synced with the
  Wave-5 and Wave-6 migrations; the next `prisma migrate dev` reports drift and offers to drop
  the columns. One sync pass covers both.
- [ ] **OI-79** `MEDIUM` `azoa` — **The declared rate is recorded and never checked against the
  amounts it priced.** `Services/Reserve/ExchangeRatePolicy.cs` `DescribeRateRefusal`
  (:167-199) checks currency, asset identity, rail, positivity and freshness, and never that
  `PlatformAmount ≈ ProjectUnitAmount × rate` — while all three are consumer-supplied on the
  same body. The value is then stamped on the receipt and on `BlockchainOperation.ExchangeRate`
  as the audit record of the price. AZOA deliberately does not *price* (D7); a consistency band
  on a rate it chooses to *record* is a different control, and it is absent.
- [ ] **OI-80** `HIGH` `ardanova` — **The guard that should have caught OI-36/OI-37 cannot.**
  `tests/ArdaNova.Application.Tests/Policies/EconomicSettlementPersistenceContractTests.cs` is
  named a persistence contract test and is entirely C# reflection over attributes; nothing in
  it reads `schema.prisma`, a migration, or a database. `migration-preflight.ts` *could* catch
  it and its expectation list was not extended. Same failure mode as the golden-drift guard in
  the RUNBOOK ledger.
- [ ] **OI-81** `LOW` `both` — **Directory-level docs owed** (repo convention: the "why" lives in
  `AGENTS.md`): `azoa/Services/Reserve/AGENTS.md` does not exist and should carry the three-way
  distinction between the platform reserve, a project treasury and `NodeTreasuryDestination`;
  `ardanova/.../Infrastructure/Azoa/AGENTS.md` and `Infrastructure/Outbox/AGENTS.md` need the
  settlement-gateway, on-ramp and dispatch-ceiling paragraphs their code comments point at;
  `ArdaNova.API/HealthChecks/` has none at all; `azoa/Services/Custody/AGENTS.md`'s closing
  "Still out of scope" paragraph is stale.
- [ ] **OI-86b** `MEDIUM` `ardanova` — **`SwapService.ExecuteSwapAsync` still refuses with a
  stale message** ("until secure provider settlement, reconciliation, and account eligibility
  checks are available") now that settlement exists; and a parked `PROCESSING` payout has **no
  automatic reconciler** — it is settled only when an operator calls
  `POST /api/Payouts/{id}/process`. `GetPendingPayoutsAsync` was widened so parked rows at
  least reach the operator's list.
- [ ] **OI-88b** `MEDIUM` `ardanova` — **`FundingIntentStatusDto` carries neither
  `releaseNotBefore` nor `holdingReasonDetail`**, so the success page's "yours after N days"
  falls back to a hardcoded 30 and HOLDING/FAILED/REJECTED sponsorships cannot show the
  server's own sentence. Both values are already written onto the entity. Also:
  `CreateFundingIntentDto` has no `displayNameConsent` field (**OI-53**), and a
  `SETTLEMENT_PENDING` intent is promoted to SETTLED only when somebody reads it — there is no
  hosted promoter.
- [x] **OI-89b** `MEDIUM` `ardanova` — **The first sponsorship of any project is unpriceable.**
  `ExchangeService.GetProjectUnitValueExactAsync` computes `fundingRaised / totalSupply`, which
  is 0 before anyone has sponsored, so `TryQuote` refuses at `FundingIntentService.cs:373`
  ("This project has no price yet, so nothing was taken.") and no row or node call happens.
  Correct refusal, missing feature. *Unblocks with:* an offering price (e.g.
  `fundingGoal / totalSupply` when `fundingRaised == 0`).
  **CLOSED 2026-09-09, and the *Unblocks with* line above was WRONG on units.**
  `ProjectTokenConfig.fundingGoal` carried the founder's ARDA figure (the create wizard
  badged its input `ARDA`) while `fundingRaised` accumulates the sponsorship's DOLLAR
  reference, so `fundingGoal / totalSupply` divided ARDA into a field declared as USD: the
  first sponsor of every project was charged about a hundred times the founder's price and
  received about a hundredth of the promised units. Fixed by making the ask DOLLARS end to
  end (D52): the wizard asks in dollars, `ProvisionProjectFundingRequest.FundingGoalUnit` is
  required on the wire and refused unless it is `ProjectFundingUnits.Ledger`, and every read
  of the pair goes through `ArdaNova.Domain/Policies/ProjectFundingLedger.cs` - including
  Gate 1, which had the same mismatched comparison. The whole read side already rendered the
  goal with `formatUSD`, so the wizard was the only surface in the other unit.
  The offering price now holds for the WHOLE primary raise (`RaiseIsOpen`), because
  `raised / totalSupply` collapses the price the moment one sponsorship settles; the
  backer-set price takes over only once the ask is MET, where the two formulas are equal.
  Sponsorship also gained a supply ceiling and now counts its units
  (`ProjectStakePartitions.DescribeSponsorDraw`; `investorSupply`/`allocatedSupply` move at
  settlement) - it previously bounded nothing at all.
- [ ] **OI-91** `MEDIUM` `both` — **Owed tests, named.** No test file was inside any wave's owns
  list, so several controls ship unpinned: viewer-scoped `ProjectService` cases (anonymous
  search excludes a DRAFT; non-member gets NotFound not Forbidden by id AND slug; double
  publish keeps the first `publishedAt`); `PostsActorBoundaryTests`; `AzoaSettlementGateway`
  (fiat-code refusal, terms/amount mismatch, attestation lapse, over/under-delivery); the
  `ValueStampDimension` and `TenantDeclaredValueRateSource` refusals; the electorate/quorum and
  close-vs-cancel governance cases; `GetProjectMoneyAsync`'s balance-unavailable and
  anti-double-count assertions. Three existing suites will go **assertion-red** by design and
  need updating, not relaxing: `GovernanceServiceTests` (no electorate mock → Forbidden),
  `ProjectMemberValueBoundaryTests` (voting-power update is now 400, not 501), and
  `AllocationControllerTests` (an API-key allocation with no consent grant now fails).


---

## G2. Found during the L0 remediation review (2026-09-08)

- [x] **OI-92** `HIGH` `ardanova` — **OI-52's harm survived one route over, unauthenticated.**
  *Where:* `ProjectTokensController.GetAllocations` (`{configId}/allocations`) and
  `GetAllocationsByPbi` (`allocations/by-pbi/{pbiId}`) carried NO `[Authorize]` and no membership
  check — the class declares only `[ApiController]`/`[Route]` — and returned `TokenAllocationDto`
  with `RecipientUserId`, `TokenAmount`, `EquityPercentage`, `DistributionTxHash` and
  `DistributedAt` for every allocation on a project. So L0.3 stopped a signed-in non-member
  reading per-worker award sizes at `/money` while an ANONYMOUS caller read the same facts, plus
  raw internal user ids and a settlement reference, here. *Closed 2026-09-08:* both routes now
  carry `[Authorize(Policy = ActorAssertion)]` and branch the PAYLOAD on `IsProjectMemberAsync`
  — the `GetInvestors` precedent in the same file — answering a non-member with
  `ProjectAllocationSummaryDto` (count, total units, total equity, distributed count) and
  nobody's identity. The client discriminates the two shapes (`tokenAllocationViewSchema`,
  `equity-tab.tsx` `AllocationSummary`). Proof: `ProjectAllocationDisclosureTests`.

- [x] **OI-93** `HIGH` `ardanova` — **The money page told sponsors a funded project had raised
  nothing.** *Where:* `projectMoneyViewSchema` omitted `summary`, `entriesVisible` and
  `entriesHiddenNote`, so `z.object` stripped all three off a correct server response
  (`ProjectLedgerService.cs:254-264` builds them). `money-tab.tsx` then saw
  `entries.length === 0` and rendered "NOTHING HAS MOVED YET" on a sponsored project, while
  printing the names note in the same view — two statements contradicting each other on the one
  screen a sponsor is asked to act on. *Closed 2026-09-08:* the contract moved to
  `ardanova-client/src/lib/contracts/project-money-contract.ts` (so it is testable at all) and
  carries all three fields; the tab renders a `SupportSummary` card and, for a non-member, an
  `EntriesWithheld` state that says the movements are private rather than absent. Proof:
  `project-money-contract.test.ts` — including a case that REFUSES a response with no summary
  rather than defaulting one.

  *Proof corrected, and the headline marked, 2026-09-09 (L0 remediation):* the contract test's
  fixture said `"$1,250.00"` — a string the server never produces. `ProjectLedgerService`
  `FormatAmount:680` is `value.ToString("F" + scale, InvariantCulture)`, so `sponsorshipTotalUsdText`
  and `usdReferenceText` cross the wire bare, as `"1250.00"`; the assertions were therefore about
  the fixture, not the wire, and would have stayed green whatever the real string looked like. The
  fixtures now carry the server's real shape. That shape is printed in a `text-2xl` mono headline
  directly beneath a balance rendered as `1250.00 ARDA`, i.e. two large figures, one labelled a
  unit and one labelled nothing, on the screen a sponsor acts from — so the dollar figures are
  marked at the SCREEN by `ardanova-client/src/lib/commerce/money-figures.ts` `asDollarFigure`,
  which passes the digits through untouched (the server sends text precisely so the record is
  exact) and returns null rather than a lone `$`. Proof: `money-figures.test.ts`, whose inputs are
  `FormatAmount`'s real output. Presentation only: it does not bear on **OI-19**, which is about
  which unit the ASK is entered in and still needs an owner ruling.

- [x] **OI-94** `MEDIUM` `ardanova` — **The credential proof was gated at one screen of two, and
  had no producer at all.** *Where:* `credential-card.tsx` gated on
  `isIndependentlyVerifiable === true` while `app/credentials/[id]/page.tsx:83-95` still built
  its report from identifier PRESENCE, so a row carrying a Simulated-fabricated `assetId` read
  "Verification pending" on the card and "Verified" on the detail page — the card's own
  doc-comment promising they could not drift was false as written. And the flag had ZERO
  producers: three repo-wide hits, all inside the card. *Closed 2026-09-08:* the gate moved INTO
  the shared translator (`credential-proof.ts` `wasIndependentlyIssued`), which now refuses every
  proof reading and every quotable reference unless the flag is `=== true`, so a third screen
  cannot drift either. *Producer corrected 2026-09-09 (L0 remediation):* the first producer
  read `mintTxHash`, which is exclusive of the CODE and wrong about the DATA — the removed
  address-based path wrote that same column from `MintSoulboundASAAsync(_platformAddress, …)`
  and the simulated provider fabricated `assetId`, so every row predating the node path mapped
  to `true` and read "Verified" on both screens, a REGRESSION on the card. The flag now reads
  `MembershipCredential.issuedThroughNodeAt`, a column of its own (`MappingProfile.cs`
  `IsIndependentlyVerifiable`), written in exactly one place
  (`MembershipCredentialServices.UpdateMintInfoAsync`) and only when the caller states
  `CredentialProofOrigin.IssuanceNode` — a REQUIRED DTO field whose default value clears the
  column, so a new writer cannot reach it without answering which path issued. Nullable with no
  backfill: a legacy row reads null, which is the fail-closed reading, and recovers through the
  existing idempotent re-verification route. `GetCredentialWithChainDataAsync` asks the same
  question before it reads the chain at all, so the server never emits `ChainVerified` for a
  credential the node did not issue. Migration:
  `20260909002000_credential_issuance_path_column`. Proofs: `CredentialIssuancePathMappingTests`
  (including "the legacy platform-held mint shape does not read as verifiable"),
  `MembershipCredentialServiceTests` (the column is stamped for the node origin and cleared for
  any other), `CredentialUtilityServiceTests` ("reports nothing for a credential the node never
  issued") and `credential-proof.test.ts`.

- [ ] **OI-95** `LOW` `ardanova` — **Five sibling reads on `ProjectTokensController` remain
  unauthenticated.** *Where:* `GET config/{id}`, `POST config/metadata/batch`,
  `GET config/by-project/{projectId}`, `GET config/{configId}/supply`,
  `GET config/{configId}/gate`. Unlike the allocation routes these carry only project-level
  facts — supply, funding goal, gate state — which a public sponsorship page legitimately
  shows, so they were left open DELIBERATELY rather than by omission. Recorded so the decision
  is visible: if the project page ever stops being public, these five move with it.
  *Corrected 2026-09-09 (L0 remediation):* this row said FOUR and omitted
  `POST config/metadata/batch` (`ProjectTokensController.GetMetadata` →
  `GetMetadataByIdsAsync`), a batch read of the same project-token metadata by id — the same
  harm class as the four already accepted, but a decision record that undercounts is how the
  next reviewer concludes the surface was enumerated. The count is now enforced rather than
  asserted: `ProjectTokenPublicReadSurfaceTests` reflects over the controller and fails if the
  set of routes carrying no `[Authorize]` is anything other than exactly these five.

---

## H. Deliberately accepted, recorded so nobody rediscovers them as surprises

- Stripe REST is called over raw `HttpClient` rather than the Stripe.NET SDK — a deliberate
  consequence of the "minimise external dependencies" rule plus an unowned `.csproj`. Wire
  format and API version are pinned (`StripeGateway.ApiVersion = "2024-06-20"`).
- Head-of-line blocking in the buy-back queue is deliberate; `QueueEntryTtl` (24h) is what
  stops an absent seller blocking it forever. If small orders should jump a large stalled one,
  that is a policy decision.
- The buy-back queue place is retired at **admission**, not at settlement, so an
  admitted-then-unsettled sell loses its place and re-enters at the back. Correct but unfair.
- `HoldFundingSource.Funder` (a hold funded from a person's own account) refuses fail-closed:
  moving units IN needs an idempotency anchor on the inbound leg or a retried open charges the
  funder twice.
- `AllocationKind.Transfer` is dead for tenants and was deliberately not resurrected: the
  manager stamps the op with the tenant as grantor, so the deep seam looks for a
  tenant-to-itself grant that can never exist. Both sides refuse, so nothing is open.
- `DelegatedVotesController`/`DelegatedVoteService` are inert — `new Vote` exists only at
  `GovernanceServices.cs:283`, so a delegation can never produce a ballot. Either honour it in
  the tally or remove it from the API surface.
- No React component test harness exists in `ardanova-client` (`vitest.config.ts` is
  `environment: 'node'`, `src/app/**` excluded, zero `.test.tsx`, no
  `@testing-library/react`), so no client component in this program has a component test.


---

## L3 remediation — corrections to the record (2026-09-09)

The L3 review returned PASS-WITH-DEBT. Six of its eight findings were fixed, one was fixed
with a correction to the finding itself, and one is partly refuted. These lines exist because
the wave's commit messages are the text this ledger and RUNBOOK §2 get written from, and three
sentences in ArdaNova `263c837` assert controls that commit did not contain.

**CORRECTION to ArdaNova `263c837` (OI-54 / OI-85).** Its body reads *"the identifier lives in
a href, never in visible text"*. **There is no href.** `credentials/credential-card.tsx:98-141`
and `credentials/[id]/page.tsx` contain neither a link nor an identifier: L3.2 deliberately
built none, on the reasoning recorded in that file's own doc-comment (*"a credential is a
certificate, not a receipt"*). The credential surface IS clean of the identifiers OI-54/OI-85
named; the sentence describing HOW is false. Not repaired by building an href — that would put
a quotable identifier back on a member-facing screen. **The record is corrected instead.**

**CORRECTION-AND-FIX to ArdaNova `263c837` (OI-88).** Its body reads *"'API: liquid' / 'API:
time-locked' become 'Available now' / 'Available from &lt;date&gt;'"*. At `263c837` the badge read
**"Not available yet"** with no date, because `TokenBalanceDto` carried no release date — a fact
the same commit disclosed four paragraphs later, contradicting itself. The date was real all
along (`TokenAllocation.workerHoldReleasesAt`, already summed by `UnitWindow.Restrict`); it was
never carried to the wire. **Now built:** `TokenBalanceDto.RestrictedUntil`, populated in
`TokenBalanceService.WithLiveLiquidityAsync`, rendered by `portfolio-holding-card.tsx`
`describeAvailability`. A hold with no date still reads "Not available yet" — a promise nobody
can date is one nobody may spend.

**PARTLY REFUTED (OI-19, the test claim).** `263c837` reads *"entering 5000 yields a goal of
500,000 base units"*. That sentence is **wrong about what the goal is**: the stored goal is
**5,000 dollars** (`PROJECT_FUNDING_UNIT = 'USD'`), and 500,000 is the derived ARDA caption.
The reviewer added that 500,000 "is only the derived caption figure" and is **not asserted** by
a test — that half is refuted: `project-create-contract.test.ts:376` asserts
`convertDollarsToPlatformUnits(5000, null) === 500_000` and `:392` asserts the caption reads
`500,000 ARDA`. The figure is tested; the commit sentence mislabels it.

- [x] **OI-30** — **RE-CLOSED at the counter.** The first closure guarded `AllocationManager`,
  the join added `ReserveExchangeManager`, and **five** further call sites of the same
  `NodeGovernanceGuard` still returned `governance.Message` verbatim:
  `Managers/FungibleTokenManager.cs:125`, `Managers/NftManager.cs:94/:160/:236`,
  `Managers/HolonManager.cs:709-710`. Guarding doors one at a time is how OI-20/21 were falsely
  closed in L0. The guard's return type is now `NodeGovernanceDecision`
  (`Services/Governance/NodeGovernanceDecision.cs`), whose `ConsumerMessage` is scrubbed **at
  construction** and which has **no member that could carry rail detail**; the operator wording
  goes to the guard's own `ILogger` and is dropped. Changing the type made the compiler walk all
  six doors. `ConsumerSafeExitCoverageTests` pins the member list by reflection.

- [x] **OI-86** — **the mirror closed too.** The C# record dropped `Chain`/`AssetId`; the SDK's
  `HoldView` kept both as REQUIRED `string`, so a consumer trusting the type got `undefined`.
  Removed, and `sdk/azoa-wallet/src/api/hold-view-parity.test.ts` now compares the interface's
  keys to the controller record's parameters by reading `Controllers/HoldController.cs` — the
  cross-boundary check OI-69 said a "parity" test must be. The controller doc-comment no longer
  claims "no rail reference": `ReservePartition` and `FundingSource` remain, and it says so.

- [x] **OI-96** `MEDIUM` `ardanova` — **NEW, opened by the L3 review, fixed in this remediation.**
  The sponsor's spend surface was ARDA-denominated while the founder's ask was inverted to
  dollars, so the two halves of the money story disagreed (D52). Now dollars end to end:
  `lib/commerce/sponsor-amount-contract.ts` (parse / convert / format, 9 tests), the invest page,
  its success receipt, `equity-preview.tsx` and `ConversionChain`. The `?amount=` handoff is
  dollars. The wire contract is UNCHANGED — the route still settles in platform units, which is
  now a caption ('Settles as … ARDA from your balance'), never a thing anyone types.
  `sponsor-surface-denomination.test.ts` pins it: the ticker may appear only in two named
  settlement captions across the four files, and no label, chip, affix or refusal may name it.

- [x] **OI-97** `MEDIUM` `ardanova` — **NEW, opened and fixed.** "Dollar min/max validation is
  enforced" was true of one client path. `funding-tab.tsx:198` was a second funding-goal field
  parsing with a bare `Number()` and checking only `goal > 0`; the tRPC boundary was
  `z.number().positive()` with no ceiling and no cent rule; `ProjectTokenService.ValidateDeclaration`
  validated the UNIT and never the magnitude. Now `ProjectFundingLedger.DescribeUnusableGoalAmount`
  states the rule once and every writing door calls it — `ValidateDeclaration`,
  `CreateConfigAsync`, `ProjectServices.CreateAsync`/`UpdateAsync` — with the two client schemas
  and the second field mirroring it. `ProjectFundingDeclarationTests` proves the counter does not
  move: no config row, no save.

- [x] **OI-98** `MEDIUM` `azoa` — **NEW, opened and fixed.** The frontend vocabulary ratchet was
  keyed on `(file, word)`, so "the list can only shrink" was true of the pair list and false of
  the violation count: any amount of new seed-phrase copy could be added to a file already
  recorded for it. `known-debt.json` now records a COUNT per pair (28 files / 78 pairs / **139**
  spans, the same coverage), and the gate fails both when a count rises and when it is not
  lowered after a clean. Proven to bite on a real probe: *"1 recorded, 2 found"*.

- [x] **OI-99** `LOW` `azoa` — **NEW, opened and fixed.** `ConsumerSafeExitCoverageTests` proved
  its funnel with three literal substring scans and a LINE count, so a refusal with its
  initializer members reordered, or assembled over two statements, satisfied all three, and two
  scrubber calls on one line counted as one. The scans are regexes over offsets now, and the
  load-bearing rule is that `IsError` is only ever WRITTEN at or after the scrubber.
