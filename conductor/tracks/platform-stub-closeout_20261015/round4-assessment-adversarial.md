---
type: assessment
round: 4
stance: adversarial
---

# Round 4 adversarial correctness and completeness assessment

Branch `feat/platform-stub-closeout` at `0522fdf`. This was a read-only, static assessment on
2026-10-17: nothing was built, run or edited apart from this file. Four parallel read-only sweeps
fed it (liveness, cross-tenant/IDOR, fail-closed SurrealQL classes, hidden stubs). Their claims
were spot-checked against the source, and claims that did not survive are marked. "CONFIRMED"
means the full path was traced in source. "PLAUSIBLE" means one fact is missing, and the runtime
probe named in §9 settles it.

## 0. Bottom line against the goal

The owner's goal is Identomat KYC/KYB first, then Stripe, with no stubs or deferrals. Measured
against it, **no user journey works end to end today, even in principle**. The reasons are not
the mock boundary; they are internal defects:

1. **The tree has never compiled**, and the test projects have never compiled. 427 test files
   changed and none built.
2. **The generated schema predates rounds 1–3.** 61 tables have no schema and 25 tables are
   missing about 120 fields. KYC approval, hosted-session creation, on-ramp refunds and cash-out
   persistence all write fields the database does not define (F-C2).
3. **A first-party user cannot start KYC at all.** No route calls `IKycManager.BeginAsync`, and
   the tenant-null approval lookup was rewritten so that it can never match (F-C3).
4. **Stripe checkout, payout onboarding and the payment webhook are each broken independently**
   (D1, D8, D2, all still present; F-C4).
5. **KYB approval gates nothing** (F-H2). **Identomat KYC has no refresh path** when a callback
   is lost or fails (F-H1).
6. **Typed reads that filter on enum columns match nothing.** SurrealForge lowercases enum
   values on reads but stores them PascalCase or uppercase on writes. As a result the KYC
   operator queue and active-attempt detection, the parked-run reconcile sweep and the webhook
   due-scan all return zero rows (F-C6).
7. **The fungible-creation service and store are not registered in DI** (F-C7). Platform-token
   genesis, which the on-ramp reserve depends on, cannot resolve them. Development builds
   validate DI at startup, so the dev API should fail to boot.
8. **Several money states have no exit**: a parked cash-out, a parked reversal, a charged
   but refused issuance, and a failed clawback key (F-H11).

Rounds 1–3 built a lot of genuinely careful machinery. The gap now is convergence, plus a small
number of design holes the lane structure did not own.

---

## 1. Findings by severity

### CRITICAL

**F-C1. Nothing compiles, so nothing has runtime evidence (CONFIRMED).**
- *Evidence:* `.omc/research/round3-join-compile-errors-2026-10-16.txt` lists 247 first-wave
  errors (grouped in §6).
- *Why the real count is higher:* the analyzer wave has not run yet. SRDB0001 is an
  error-severity analyzer in `SurrealForge.Analyzer` 1.2.0 (`AZOA.WebAPI.csproj:113`). There are
  36 non-literal `SurrealQuery.Of(...)` call sites outside the two `#pragma`-suppressed files.
  Not every one will trip the analyzer, which matches the ledger's estimate of about 35. The
  largest are
  `SurrealBridgeStore.cs` ×5, `SurrealActionWorkflowAdmissionStore.cs` ×4,
  `SurrealActionLocalMutationStore.Witnesses.cs` ×4 and `DeploymentAuthorityFence.cs` ×4.
  IDE0005 (unused usings) is also a build error in the app.
- *Test projects:* never compiled. `git diff --stat main...HEAD -- tests` shows 427 files and
  +39,102 lines. Examples of drift: `tests/AZOA.WebAPI.Tests/WalletManagerTests.cs` sets the
  removed `WalletCreateModel.WalletType`, and fakes implement the old
  `TryAttachProviderSessionAsync` shape (`tests/AZOA.WebAPI.Tests/Kyc/HostedSessionFlowTests.cs:696`).
- *Why it matters:* every "approved" verdict in rounds 1–3 is static. Round 3 itself found five
  fail-closed classes that static review had missed. All four sweeps in this assessment found
  more of them (F-C2, F-C3, F-H7).
- *Package:* **WP0 Compile convergence** (§8).

**F-C2. Generated schema is pre-track: 61 tables have no schema and 25 tables are missing fields (CONFIRMED).**
- *The comparison:* there are 131 `[SurrealTable]` POCOs in `Persistence/SurrealDb/Models` but
  only 70 files in `Persistence/SurrealDb/Generated/Schemas`. Schemas were last touched by the
  vocabulary sweep `f445f86`, which regenerated from its own `bb512cf` base, not from
  `3d08ace`'s models.
- *Tables with no schema at all include:* `kyb_verification`, `hosted_provider_attempt`,
  `hosted_provider_callback`, `hosted_session_provider_alias`, `ethereum_preparation`,
  `ethereum_dex_allowance`, `nft_lifecycle`, `nft_protocol_cost_quote`, `swap_operation`,
  `swap_stage`, `dex_quote`, all seven `wtt_*` tables, `hold_command*`, `hold_release_mandate*`,
  `exchange_hold_source`, `exchange_purchase_allocation`, all `fungible_creation_*` tables,
  `governance_proposal`, `deployment_operation`, `wallet_auth_challenge_budget` and
  `wallet_auth_identity_migration`.
- *Fields the code writes that existing SCHEMAFULL tables do not define:*
  - `hosted_session.creation_token`, `creation_started_at`, `creation_quarantined_at` and
    `resume_lease_*`. These are read by every predicate in
    `SurrealHostedSessionStore.cs:97-140,226-243`.
  - `kyc_submission.aml_lineage_key`. The gate needs it: `KycGateService.cs:99,114`.
  - `kyc_screening_result.lineage_key`.
  - 10 `on_ramp_lot` refund fields. The refund path uses them: `OnRampRecovery.cs:55-102`.
  - 16 `fiat_payout` fields, 14 `hosted_session` fields, and 12–13 fields on each webhook-event
    table.
- *Why it matters:*
  - SCHEMAFULL tables refuse or strip the new fields. The result is that hosted KYC/KYB begin,
    KYC approval, the AML gate, on-ramp refunds and payouts all fail closed.
  - Tables with no `DEFINE` are created schemaless, with **no UNIQUE indexes**. Lane C's
    single-winner `(protocol, sender, nonce, nonce_slot)` index is what makes the nonce reuse
    safe, and on a fresh database it would be **absent**. That fails open, not closed.
- *Why the ledger understates it:* the round-3 ledger (§Final-sweep prerequisites 1) lists
  about 7 owed fields. The real figure is about 120 fields plus 61 tables, and the golden and
  flowchart tests will churn on all of them.
- *Package:* **WP1 Schema convergence**, which depends on WP0.

**F-C3. First-party KYC is unreachable, so every first-party value gate is permanently closed (CONFIRMED, with one PLAUSIBLE sub-claim).**
- *No begin route.* `KycManager.BeginAsync(Guid userId)` (`Managers/KycManager.cs:115-118`) has
  no caller. `KycController` exposes only `submit`, `status`, `status/summary`, `{id}` and
  `{id}/documents` (`Controllers/KycController.cs:34-117`). That was already true on `main`.
  The only hosted begin is tenant custodial (`Managers/TenantCustodialAccountManager.cs:296`).
- *Submit cannot substitute.* Identomat refuses document submission:
  `CreateSessionAsync` and `ValidateDocumentsAsync` return Failure
  (`Providers/Kyc/IdentomatKycProviderService.cs:215-227`).
- *Manual cannot substitute either.* Both gates hard-refuse it with
  `profile.Provider != KycProvider.MANUAL` (`Services/Kyc/KycGateService.cs:96`,
  `Services/Bridge/RealValueKycGate.cs:102`), even though `KycApprovalTrust.cs:42,86` allows
  manual in Development.
- *Tenant-null lookup regression (PLAUSIBLE, high confidence):*
  - `SurrealKycStore.cs:44-45,58-59` was rewritten in `3d08ace` to
    `Where(item => item.TenantId == tenant)`, where `tenant` is a captured `null`.
  - SurrealForge maps only a literal null to `NONE`. `IsNullLiteral` accepts only a
    `ConstantExpression` (`surrealforge/src/SurrealForge.Client/Query/ExpressionTranslator.cs:257-265,301-306`),
    so a captured null is bound as `NULL`.
  - `main` used an explicit `tenant_id = NONE` branch, and SurrealForge's own comment says NULL
    is distinct from NONE.
  - So even an existing tenant-null approval would never be found.
- *Why it matters:* every first-party value action is permanently closed. That covers fiat
  checkout for self (`FiatRailProvider.cs:477-489`), NFT mint (`NftLifecycleService.cs:54`),
  fungible creation, holds and deployments (via `ValueAccessService.cs:33-35`).
- *Self-sovereignty consequence:* a user who claims sovereignty from a tenant loses value access.
  Their approval is tenant-scoped, and they have no way to re-verify.
- *Package:* **WP3 KYC first-party and refresh**.

**F-C4. The Stripe money path is broken at three independent hops (CONFIRMED; D1, D2 and D8 still present).**
- *Checkout attach (D1):*
  - `Providers/Fiat/FiatRailProvider.cs:610-617` passes `ct` into `creationToken`. This is a
    compile error.
  - Beyond the compile error, the fiat flow never sets `CreationToken` on its claim
    (`:511-523`) and never calls `TryBeginCreationAsync`. So the attach predicate
    `State == Claimed && CreationToken == creationToken && CreationStartedAt != null`
    (`SurrealHostedSessionStore.cs:234-236`) can never hold.
  - Every checkout ends in `RailBusy` after Stripe has already created the session, and the URL
    is lost.
- *Idempotency (D8):* `expiresAt = now + N` is recomputed on every attempt (`:571`) under the
  fixed key `"azoa-fiat-checkout-" + session.Id` (`:595`). Stripe answers `idempotency_error`
  for about 24 hours, and the admission is wedged for that time.
- *Webhook inbox (D2):* `Services/Fiat/StripeWebhookInbox.cs:467-468` writes bare hex into
  `option<record<hosted_session>>` and `option<record<user>>`
  (`Generated/Schemas/fiat_event.surql:45-46`). Every correlated payment webhook returns 500.
- *The same D2 class recurs downstream, currently masked because the inbox fails first:*
  - SurrealForge `SurrealRecordLink.PromoteText` promotes only `table:id` text
    (`surrealforge/src/SurrealForge.Client/Query/SurrealRecordLink.cs:130-155`), so bare hex in a
    `[References]` column is sent as a plain string.
  - **On-ramp lot claim:** `OnRampSettlementHandler.cs:545-547` writes `IntentId = intentKey`,
    `SubjectUserId` and `TenantId` as bare hex into `record<hosted_session>`, `record<user>` and
    `option<record<user>>` (`on_ramp_lot.surql:23-25`) through `SurrealWriter.Create`
    (`OnRampLotLedger.cs:107`). `:436` does the same for `option<record<operation_log>>` (`:62`).
    This code has existed since `2f7944f`, but it was never reached at runtime.
  - **Cash-out claim:** `OffRampService.cs:335-337` → `FiatPayoutLedger.cs:112`
    (`fiat_payout.surql:22,25`).
  - **Reversals (PLAUSIBLE):** `FiatReversalEventHandler.cs:239-242` copies lot ids into four
    record-link columns.
  - *Contrast:* `SurrealHostedSessionStore.ToStorage` (`:399-415`) converts correctly, so the
    convention exists but is not enforced.
  - Fixing only D2 will move the 500 one hop downstream. This needs a store-boundary rule plus
    an architecture test: any `[References]` property set to a value with no `:` is a defect.
- *Payout onboarding (same class as D1):* `Services/Fiat/OffRamp/PayoutAccountService.cs:179-245`
  claims with no creation token and attaches with a 7-argument call. The connected-account id is
  never persisted, so **no cash-out can ever start**.
- *Package:* **WP4 Stripe convergence**.

**F-C5. SurrealForge 1.2.1 is unpublished, so the consent gate and KYC identity binding throw (CONFIRMED; D3 still present; owner-gated).**
- *Evidence:* `AZOA.WebAPI.csproj:109-113` still pins 1.2.0. In the SurrealForge repo the 1.2.1
  branch is at `3aacd25`, with no `v1.2.1` tag, and the local NuGet cache tops out at 1.2.0.
- *What breaks:* 29 `SurrealRecordParam.Of("user", …)` sites throw before sending. They include:
  - `SurrealConsentGrantStore.FindCoveringGrantAsync`, which every tenant-on-behalf-of-user
    consent check uses;
  - `SurrealKycIdentityStore.cs:81,85,142` (person binding at approval);
  - `SurrealKycScreeningStore`;
  - `SurrealUserExternalLoginStore` ×4.
- *Why it matters:* custodial KYC, tenant fiat checkout and payouts, and KYC approval are all
  dead until publish. This is an **owner action** and is in no ledger as a blocker with an owner
  and a date.
- *Package:* **WP-OWNER** (publish, then bump at WP0).

**F-C6. New fail-closed class: typed reads lowercase enum values, so enum-column filters never match (CONFIRMED from library source; found by the liveness sweep).**
- *The library mismatch:* SurrealForge `ExpressionTranslator.NormaliseValue` returns
  `e.ToString().ToLowerInvariant()`. That is true at tag `v1.2.0` and on the unpublished 1.2.1
  branch (`src/SurrealForge.Client/Query/ExpressionTranslator.cs:657-665`), and the library's
  own test pins `"active"` (`tests/.../SurrealQueryTypedTests.cs`). But `SurrealWriter` binds the
  enum name unchanged (`SurrealWriter.cs:106,211`). Only the typed `UpdateOnly`/`DeleteOnly`
  predicates convert back (`SurrealWriter.NormalizePredicateBoundValue`,
  `TypedPredicateCompiler.cs:31-33`).
- *Effect:* every `SurrealQuery<T>.From().Where(...)` or `IQueryable` read that compares an
  **enum-typed** column binds `"pending"` while the row stores `"PENDING"`.
- *KYC sites:*
  - `SurrealKycStore.cs:59-60` (active-attempt guard, so `BeginCoreAsync` cannot see its own
    open attempt);
  - `:68,77` (pending lists);
  - `:124-128` (the operator queue at `NodeOperatorController.cs:88` → `KycControlPlaneManager.cs:365`
    is always empty);
  - the schema stores uppercase (`kyc_submission.surql:13,36-37`).
- *Action-run site:* `SurrealActionRunStore.cs:90-95` (`GetByStatusAsync`). The reconciliation
  sweep and the operator "reconcile parked runs" button scan zero rows, and the UI reports
  success.
- *Webhook site:* `SurrealConsentWebhookDeliveryStore.cs:22-25` (and the action equivalent).
- *Proof that `main` knew:* `main`'s `GetByStatusAsync` comment says explicitly that it compared a
  `string?` "with no enum lowercasing". Round 3 turned the POCO field into an enum and broke it.
  Comparisons against `*Statuses` string constants (fiat, hosted session) are unaffected.
- *Why it matters:* this is the same fail-closed signature round 3 hunted, but larger.
- *Package:* WP0. Fix in SurrealForge (normalize read predicates exactly as writes do, release
  1.2.2), or interim per-site string projections. Add a test that writes and then reads back
  every enum column through a real store.

**F-C7. Fungible creation is never registered in DI (CONFIRMED; found by the liveness sweep).**
- *Evidence:* no `Add*` registration of `IFungibleCreationService`, `IFungibleCreationStore` or
  `FungibleCreationRecoveryWorker` exists anywhere outside tests (repo grep).
- *Who needs them:*
  - `FungibleTokenManager` (registered at `Program.cs:1350-1352`) requires both in its
    constructor (`Managers/FungibleTokenManager.cs:20-23`);
  - so does `FungibleCreationController` (`Controllers/FungibleCreationController.cs:13`).
- *Why it matters:*
  - Development's default `ValidateOnBuild` makes the API fail at `builder.Build()`.
  - Elsewhere, platform-token genesis fails per request (`Program.cs:1347` says genesis needs this
    manager), and genesis is the root of on-ramp reserve issuance.
  - Crashed `Submitted`/`Confirmed` creations have no worker.
- *Package:* WP0, plus a test that builds the real host with `ValidateOnBuild` to catch the
  whole class of missing registrations.

### HIGH

**F-H1. Identomat KYC has no refresh path, so lost or failed callbacks strand the attempt (CONFIRMED).**
- *The callback hides failures:* the KYC callback answers 202 on every path
  (`Controllers/KycCallbackController.cs:62-91`), so Identomat never retries.
- *The "retry" never comes:* a failed domain resumption is released as `AwaitingOutcome` with the
  comment "the next callback will re-claim it" (`Services/HostedSessions/HostedSessionFlow.cs:337-363`).
  No next callback arrives.
- *The worker skips Identomat:* `HostedIdentityRecoveryWorker` sweeps only `HostedProviderAttempt`
  rows, which belong to Veriff and generic-hosted (`HostedIdentityRecovery.cs:14-19`,
  `HostedIdentityRecoveryWorker.cs:22-32`).
- *The operator route skips Identomat:* `POST /api/operator/hosted-sessions/{id}/refresh` returns
  404 for Identomat sessions (`HostedSessionReconciliationController.cs:110-118`).
- *No user route:* there is no user refresh route.
- *Manual review strands too:* `MANUAL_CHECK` maps to non-terminal `AwaitingOutcome`
  (`IdentomatKycProviderService.cs:376-450`), and nothing polls it.
- *Contrast:* KYB has a refresh route (`KybService.cs:99-117`). KYC does not.
- *Why it matters:* the user has really verified with Identomat, but the platform keeps them at
  PENDING until the attempt expires. The fix is a paid re-verification.
- *Package:* **WP3**.

**F-H2. KYB approval is consumed by nothing (CONFIRMED).**
- *Evidence:* no code outside `Services/Kyb`, `Providers/Kyb` and the two KYB controllers
  references `IKybService`, `IKybVerificationStore` or KYB status (repo grep).
- *What tenants can do unverified:* run custodial KYC, open user checkouts, start payouts and
  register webhooks.
- *Why it matters:* "especially the KYC/KYB is set up". KYB is a hosted flow with a status page
  and no consequence, which is a semantic stub. `kyb_verification` also has no schema (F-C2),
  and dev compose sets an http `Kyb__ReturnUrl` (`docker-compose.dev.yml:145`), so D7 is
  unresolved.
- *Package:* **WP5 KYB enforcement**. It needs an owner decision on which tenant capabilities KYB
  gates.

**F-H3. Lane B's two clawback must-fix MEDIUMs are unfixed (CONFIRMED).**
- *Rung 0 skips unfinished reservations:* uncompleted clawback reservations are skipped
  (`Services/Fiat/Reversal/ReversalCompensationService.cs:223-225`).
- *The pending finish is uncapped:* it is not capped at `attributable`
  (`:716-726`; the cap at `:731-733` applies only to new reservations).
- *A definitive failure wedges the rung:* a `Failed` idempotency key returns `Retry` forever,
  because SurrealForge `TryClaimAsync` never re-claims a Failed key (`:826-831`).
- *Why it matters:* the ledger marks these "must-fix before real cards". They are money
  over-recovery and wedge paths.
- *Package:* **WP4**.

**F-H4. Exchange and DEX are complete in source but switched off, which is a disguised deferral (CONFIRMED).**
- *Exchange:* no provider declares `IExchangeTransactionModule`. Algorand implements all three
  methods (`AlgorandProvider.Exchange.cs:21`, `AlgorandProvider.ExchangeObservation.cs:14,32`),
  but its declaration list omits the interface (`AlgorandProvider.cs:32-33`). The lane B note
  says "the module stays unadvertised, so exchanges fail closed until independent review"
  (`round3-lane-B-resume.md:39-41`). That review has since happened: APPROVE WITH CARRY-FORWARD.
- *DEX:* no provider declares `IDexTransactionModule`, although Tinyman, Jupiter and EVM stage code
  exists (`AlgorandProvider.DexPreparation.cs:17-70`, and others).
- *Why it matters:* reserve buy and sell, and all swaps, are 100% unavailable. Neither appears in
  `deferred.md`, and the gating reviews have passed, so this is an unexecuted activation rather
  than a blocker.
- *Package:* **WP7 Activation**.

**F-H5. Delegated child credentials are disabled platform-wide (CONFIRMED; pre-existing since `d2a71e0`).**
- *Evidence:*
  - The JWT handler fails every child credential (`Program.cs:216-220`).
  - Issuance returns 503 (`Controllers/TenantController.cs:190-197`).
  - API-key signing credentials carry `TenantId = null` (`Services/Signing/SigningAdmissionAuthorityFactory.cs:34-49`).
- *Why it matters:* the round-3 tenant-scoped signing rules are unreachable at runtime. That
  includes the NFT `RequesterOwns` fix, `HoldCustodyScope.AdmitsSettlement` and the WTT
  "tenant credential delivers only to own custodial wallets" rule. None of them can be exercised
  by a real request or by an HTTP flow. This is a stubbed platform capability, it is not in
  `deferred.md`, and the stated condition ("every accepting endpoint has an explicit scope
  policy") is internal work.
- *Package:* **WP8 Delegated credentials**. Either finish the scope allowlist and enable it, or
  record an owner decision that tenants never sign on a user's behalf and delete the dead tenant
  branches.

**F-H6. Governance action gates were lifted without a recorded verdict (CONFIRMED).**
- *Evidence:* `Services/Actions/NotEnabledActionNodeTypes.cs:8` now blocks only
  `CredentialRevoke`; the change came in `3d08ace`. Meanwhile:
  - `round2-governance-source-review.md:51` says "No gate-lift approval yet";
  - the plan inventory item ("require independent verdict before lifting") is unchecked;
  - the verification suite is "authored and incomplete" (`round2-governance-verification-migration.md`).
- *Compile state:* governance does not compile either (`ConsumerGovernanceService.cs`, 11
  `SurrealLink` errors).
- *Package:* **WP9 Governance verdict**: re-block until a runtime-backed review, or produce the
  verdict.

**F-H7. New fail-closed class: a captured-null LINQ comparison binds NULL, not NONE (PLAUSIBLE, high confidence).**
- *Mechanism:* the same as in F-C3.
- *Sites beyond KYC:*
  - `SurrealFungibleCreationStore.cs:296-297` (`ListParticipantAsync` for first-party);
  - `SurrealHoldReleaseMandateStore.Discovery.cs:15-16`;
  - `SurrealHoldCommandStore.cs:53-56` (`FindOwnedHoldAsync`, where `SurrealLink.ToLink(…, null)`
    returns null).
- *Contrast:* `SurrealHoldCommandStore.cs:26-27` and `SurrealUserStore.cs:215-217` branch to a
  literal `== null` correctly, so authors knew the rule. It is not enforced.
- *Why it matters:* first-party lists come back empty, owned-hold lookups miss, and the KYC gate
  stays closed.
- *Package:* WP0 analyzer or architecture rule, plus fixes. Better: fix the SurrealForge translator
  to funcletize captured nulls (1.2.2).

**F-H8. Fiat cross-tenant ownership gaps (CONFIRMED except where noted; latent child-JWT routes excluded).**
- *Payout setup link (PLAUSIBLE HIGH):* `POST /api/fiat/payouts/{userId}/account`
  (`Controllers/FiatPayoutController.cs:105-116`) lets any tenant with a live consent from U mint
  a fresh Stripe Express onboarding link for U's single, user-global payout account. The
  admission is keyed per subject (`PayoutAccountService.cs:186`). The bank destination belongs to
  U alone, so the route should be first-party only.
- *Checkout intents (MEDIUM, CONFIRMED):* intents are owned by subject only
  (`FiatRailProvider.cs:798-799`; admission key `{subject}:{requestKey}`,
  `FiatCheckoutController.cs:281-282`). A colliding key from tenant T2 returns T1's live payment
  URL, and `GetIntentAsync` (`FiatRailProvider.cs:636-660`) ignores the tenant.
- *Cash-out history (MEDIUM, CONFIRMED):* `GET /api/fiat/payouts/{userId}` and replay ignore the
  tenant.
- *Package:* **WP6 Tenant scope**, plus the architecture gate.

**F-H9. The stale `ethereum_preparation_nonce` unique index is never removed on a kept database (CONFIRMED).**
- *Evidence:*
  - Deploy runs `surrealforge up` (`docker-entrypoint.sh:92-106`), which applies schemas and
    migrations only.
  - `REMOVE INDEX` is emitted only by the destructive reconcile path
    (`surrealforge/src/SurrealForge.Schema/Migration/ReconcileRunner.cs:197-201`).
  - `Persistence/SurrealDb/Migrations` has no removal; the latest file is `20260821_…`.
- *Why it matters:* on Railway, every nonce refill hits a unique violation, so NFT, WTT and DEX
  Ethereum reservations stall. The ledger marks this "deploy-blocking" but only as "verify". It is
  verified now: `up` does not drop the index.
- *Package:* **WP1**: add a reviewed migration.

**F-H10. Webhook delivery is disabled in every environment (CONFIRMED).**
- *Evidence:*
  - The default is off: `Services/Webhooks/WebhookOptions.cs:29` sets `Enabled = false`.
  - Both workers return immediately (`ConsentWebhookDeliveryWorker.cs:18`, `ActionWebhookDeliveryWorker.cs:18`).
  - No `appsettings*.json` or dev compose sets it, and e2e pins `"false"`
    (`docker-compose.e2e.yml:111`).
- *Why it matters:* the stated rationale ("until a real consumer exists") lapsed when round 3
  shipped tenant webhook registration, its UI and its operator console. Events accumulate in the
  outbox and are never delivered.
- *Package:* **WP7**.

**F-H11. Money states with no exit (CONFIRMED except where noted; from the liveness sweep plus this assessor's re-trace).**
- *FiatPayout `NeedsOperator`:*
  - `/advance` maps it to `null` and returns 409 (`Services/Fiat/OffRamp/FiatPayoutOperatorController.cs:167-176`).
  - `/return` (`OffRampService.cs:667-711`) re-parks whenever money may have left, and returns the
    row unchanged when the collection claim is not Completed (`:675-679`).
  - `/recover` requires an authoritatively failed bank payout (`FailedBankPayoutRecovery.cs:18-28`).
  - There is no route that records an operator-verified Paid result.
- *FiatReversal `NeedsOperator`:* both ladder entry points return early on `NeedsPerson`
  (`ReversalCompensationService.cs:176,343`). `/restart` only re-queues the same saga
  (`FiatReversalOperatorController.cs:133-155`), and the UI buttons call these no-op routes.
- *Reversal rung with a `Failed` idempotency key:* `Retry` forever, as described under F-H3.
- *On-ramp issuance refused after the charge (PLAUSIBLE):*
  - pre-effect refusals use plain `FailAsync` (`Managers/AllocationManager.cs:230,239,269`), not
    the no-effect variant;
  - the operator refund then needs `TryReserveRefundAfterNoEffectAsync`
    (`Services/Fiat/OnRamp/OnRampRecovery.cs:68-75`);
  - the result is a charged-but-uncredited payer whom the operator can neither credit nor refund.
- *Why it matters:* Stripe dev mode does not excuse this. These are the recovery paths the plan
  marks unchecked ("operator reconciliation/retry and safe refund").
- *Package:* **WP4**. For each parked money state: an audited operator resolution backed by
  provider evidence (record Paid, return units, settle the reversal), plus flow tests.

**F-H12. NFT and deployment parked states with no exit (CONFIRMED; from the liveness sweep).**
- *NFT lifecycles past a confirmed stage:* when a lifecycle was started by a workflow, or its
  tenant grant was revoked, renewal refuses. It rejects workflow authority, or requires the same
  tenant (`Services/Users/NftLifecycleService.ActorContinuation.cs:39-44`), and the workflow fence
  needs a Running run (`ActionExecutionFence.cs:14-26`). Rent has already been paid, so it is
  stranded.
- *Deployment `NeedsOperator`:* there is no operator route. The worker only re-observes
  (`SurrealDeploymentStore.cs:134-137`), and cancel refuses (`DeploymentService.Recovery.cs:76-78`).
- *Package:* WP2 (lane N), plus a deployment operator resolution.

**F-H13. Holon-attached fungible issuance can never complete (CONFIRMED; found by the fail-closed sweep).**
- *Evidence:* a `;` inside a string literal in `SurrealFungibleCreationStore.cs:270-274` makes
  SurrealForge 1.2.0's `SurrealQuery.Of` throw `ArgumentException` (`v1.2.0 SurrealQuery.cs:117-123`).
- *Why it matters:* this happens after the chain mint. The operation stays `Confirmed` with
  nothing accounted. Details are in §4 item 6.
- *Package:* WP0. Also add a source scan for `;` inside `Of(...)` literals.

### MEDIUM

**F-M13. More inert recovery, from the liveness sweep.**
- *Node fee settlement:* `NodeFeeSettlementRecoveryWorker` is a plain class that nothing
  invokes (`Services/Governance/NodeFeeSettlementRecoveryWorker.cs:7`, no reference in
  `Program.cs`). Every Transfer with a fee leaves a `Prepared` settlement forever
  (`AllocationManager.cs:226,894`). The fee is uncollected, but no user funds are held.
- *AML re-screening poll:* off by default (`MonitorPollEnabled=false`, `appsettings.json:191`).
- *DEX settle-review:* accepts only type-2 envelopes with an empty access list
  (`EthereumRpcEnvelope.cs:17-20`). A type-0/1 same-call winner pins the allowance lane. Latent
  until WP7.
- *Package:* WP4/WP7.

**F-M1. Definitive Identomat refusals park admissions as NeedsOperator (CONFIRMED).**
- *Mechanism:* any non-2xx response, and even a pre-send `HttpRequestException`, maps to
  `DependencyUnavailable` (`IdentomatHttpTransport.cs:100-146`). `HostedSessionFlow.cs:162-173`
  quarantines anything that is not `RequestNotSentCode`. That includes 400 (bad config id), 401
  (bad key) and 429.
- *Why it matters:* every misconfiguration or rate-limit leaves one operator ticket per user. The
  user can only escape when the attempt expires.
- *Package:* WP3. Treat 4xx/429 responses (definitive non-creation) as releasable. Keep timeouts
  and 5xx as uncertain.

**F-M2. Stripe Connect events cannot verify with a single webhook secret.**
- *Evidence:* the controller verifies against one `WebhookSecret` (`StripeWebhookController.cs:69-94`).
- *Why it matters:* `payout.*` and `account.*` events for connected accounts arrive on a Connect
  endpoint, which has its own signing secret. The dev Stripe CLI hides this because it uses one
  secret. Only bank payouts have a fallback poller (`BankPayoutReconciliationWorker`).
- *Package:* WP4. Support a list of signing secrets and document the two endpoints.

**F-M3. Dev defaults contradict the gates.**
- *Manual provider:* dev uses `Kyc:Provider=manual` (`appsettings.Development.json`,
  `docker-compose.dev.yml:154`), but the gates refuse MANUAL (F-C3), so every dev value flow is
  closed by default.
- *KYC return URL:* `Kyc__Identomat__ReturnUrl` points to `/business-verification` over http
  (`docker-compose.dev.yml:141`), which the adapter refuses to forward (`IdentomatKycProviderService.cs:479-500`).
- *KYB return URL:* D7 is the same problem for KYB (`:145`).
- *Package:* WP3/WP5.

**F-M4. D5 is still present.** `widget.identomat.com` is hardcoded and the error text is mojibake
(`20â€“1000`) in `Controllers/HostedSessionReconciliationController.cs:46,89`. The default
`ResultBaseUrl` is in `Settings/KycSettings.cs:121`. *Package:* WP3.

**F-M5. `TryAttachProviderSessionAsync` ignores the caller's expected version.**
`SurrealHostedSessionStore.cs:231-233` overwrites `expectedStateVersion` with a fresh read. The
token and state predicates still protect it, but the documented compare-and-swap contract
(`IHostedSessionStore.cs:50-60`) is false. *Package:* WP4, with the D1 fix.

**F-M6. SDK drift.**
- `FiatPayoutView` is used and re-exported but never declared (`sdk/azoa-wallet/src/api/client.ts:2206-2207`,
  `src/api/index.ts:17`, `src/index.ts:97`).
- `TinymanAdapter`, `JupiterAdapter` and `DexAdapter` are still exported and wired
  (`src/index.ts:65`, `src/wallet.ts`, `src/core/types.ts`), against the owner's "retire only"
  decision.
- *Correction to the stub sweep:* it claimed `/api/admin/fiat-payouts/{id}/recover` has no
  backend route. It does: `Services/Fiat/OffRamp/FiatPayoutOperatorController.cs:83-84,217`.
- *Package:* WP10.

**F-M7. No end-user UI for the goal's flows.**
- *KYC:* the frontend `kyc` page is the tenant's provider selection (`frontend/src/app/(dashboard)/kyc/page.tsx:32`).
  No page starts a user verification.
- *Money:* nothing calls `startFiatCheckout`, `startCashOut` or `beginPayoutAccountSetup` in
  `frontend/src`.
- *Why it matters:* lane V's Playwright journeys cannot model the user journey the goal names.
  Whether first-party UI is in scope is an owner question. The SDK path exists for tenants.
- *Package:* WP11, which needs an owner decision.

**F-M8. Wormhole legacy fabricator is still live on a registered interface.**
`Services/WormholeAdapter.cs:549-555` derives the sequence from `string.GetHashCode()`, which is
randomized per process. The only production caller deliberately refuses
(`Services/Bridge/WormholeProtocol.cs:~50-55`), but the method stays callable via DI
(`Program.cs:1639`). *Package:* WP10: delete it.

**F-M9. The transaction-scope and cross-tenant architecture gates do not exist yet.**
`tests/AZOA.WebAPI.Tests/Architecture/` contains no such test, and the tree has only six
`// lock:` annotations. Both were round-4 commitments (`round3-transaction-scope-review.md`
§Round-4 work; ledger §Cross-tenant gate). *Package:* WP6.

**F-M10. Operator exits without operator-console UI.**
- *Missing from the console:* DEX settle-review, the WTT operator, the Ethereum operator recovery,
  the saga operator and wallet-identity operator routes have no operator-console surface.
- *Why:* the BFF allowlist (`frontend/src/app/api/operator/[...path]/route.ts:20-43,83`) covers
  only on-ramp lots, fiat events, AML, hosted sessions, action recovery, tenants/KYC, webhooks and
  the platform token.
- *Partial exception:* fiat payouts and reversals have dashboard-admin pages.
- *Package:* WP12.

**F-M11. Recipient-stage Solana costs have no participant-accepted quote** (carry-forward from
lane N). Recipient Accept and Transfer spend ATA rent and fees under no cost consent.
*Package:* WP2 lane N.

**F-M12. The KYC gate is duplicated.** `Services/Bridge/RealValueKycGate.cs` is a near-verbatim
fork of `Services/Kyc/KycGateService.cs`: same constructor pair, same MANUAL refusal, same AML
check. That breaks the AGENTS.md helper rule and doubles every fix in F-C3. *Package:* WP3.

### LOW

- **D6 not verified fixed.** `EthereumRecoveryController.cs` maps result errors to 409, but
  provider exceptions still bubble as 500.
- **D9 partially addressed.** Providers accept an optional handler
  (`AlgorandProvider.cs:377`, `EthereumProvider.cs:182`, `SolanaProvider.cs:161`), but the factory
  never passes one.
- **The fiat recovery sweep replays parked events every minute forever.** `AcceptedPending` maps
  to `Received` (`StripeWebhookInbox.cs:178`), and the sweep selects Received/Failed without a cap
  (`FiatEventRecovery.cs:24-37`). Bounded at 50 rows per sweep, ordered by attempt count.
- **Copying SurrealForge's private `IsUniqueViolation` would be a hazard.** It matches the
  substring `"index"` (`SurrealIdempotencyLedger.cs:344-353`). The replacement helper for the 81
  call sites must match the unique-index violation precisely.
- **RPC URL is placed in `GetChainInfoAsync`** (`EthereumProvider.cs:814`, `SolanaProvider.cs:601`).
  Latent, because nothing serves it yet.
- **Exchange preview is a consent-free KYC oracle** (`ExchangeController.cs:86-91` →
  `ReserveExchangeManager.cs:328-330`).

---

## 2. End-to-end journey traces

Legend: **REAL** (implemented against the vendor contract), **MOCK** (mocked by authorization),
**BROKEN** (with the finding).

### (a) Identomat KYC: hosted session → callback → gate

| Hop | Status | Evidence |
|---|---|---|
| First-party user starts KYC | **BROKEN**: no route | F-C3 |
| Tenant custodial start, `POST /api/tenant/custodial-accounts/{ext}/kyc/session` | REAL, blocked by D3/F-C5 (consent grant lookup) | `TenantCustodialAccountManager.cs:296` → `KycManager.cs:121` |
| Durable admission + creation fence | REAL, **BROKEN** at runtime until schema (`creation_token` missing) | `HostedSessionFlow.cs:84-200`; F-C2 |
| `/begin` call | REAL HTTP, MOCK vendor (no config id) | `IdentomatKycProviderService.cs:271-327` |
| Ambiguous create → NeedsOperator → operator attach | REAL; too eager on 4xx | F-M1 |
| Callback auth + correlation | REAL (constant-time key, parent session id per vendor docs; D4 looks right) | `:337-375`; `.omc/research/identomat-recovery-callbacks-2026-10-15.md:266-298` |
| Authoritative re-fetch + lease + outcome handler | REAL | `HostedSessionFlow.cs:253-396` |
| Lost or failed callback, `MANUAL_CHECK` | **BROKEN**: no refresh | F-H1 |
| AML screening + monitor on approval | REAL HTTP; writes `lineage_key` (missing field) | `KycHostedSessionOutcomeHandler.cs:107-165`; F-C2 |
| Person-uniqueness binding | REAL; **BROKEN** under 1.2.0 (`user` record param) | F-C5 |
| Gate (tenant) | REAL logic; needs `aml_lineage_key` persisted | `KycGateService.cs:84-108` |
| Gate (first-party) | **BROKEN** | F-C3, F-H7 |

### (b) Tenant KYB

| Hop | Status | Evidence |
|---|---|---|
| Begin / status / refresh | REAL (route, quota, tenant authority) | `TenantBusinessVerificationController.cs:21-44`; `KybService.cs:25-117` |
| Persistence | **BROKEN**: `kyb_verification` has no schema | F-C2 |
| Vendor | MOCK (no KYB config id); dev return URL refused (D7) | F-M3 |
| Shared callback | REAL (one URL, both flows; `KycCallbackController.cs:79-84`) | — |
| Consequence of approval | **NONE**: KYB gates nothing | F-H2 |

### (c) Stripe on-ramp → funds → off-ramp → reversal

| Hop | Status | Evidence |
|---|---|---|
| Checkout start, KYC gate, return URL allowlist | REAL logic; gate closed for first-party (F-C3) | `FiatRailProvider.cs:441-489` |
| Stripe session create | REAL (test mode) | `:571-597` |
| Attach + resume | **BROKEN** (D1, D8) | F-C4 |
| Tenant scoping of intents | **GAP** | F-H8 |
| Webhook signature | REAL; single secret | F-M2 |
| Inbox write | **BROKEN** (D2) | F-C4 |
| Settlement: re-read session, price schedule, KYC gate, reserve issuance, chain confirmation | REAL logic, but **BROKEN** at the lot claim (D2 class at `:545-547`); awaits chain confirm via sweep (`AcceptedPending`→`Received`) | `OnRampSettlementHandler.cs:116-472`; `FiatEventRecovery.cs:24-37`; F-C4 |
| Charged-but-uncredited refund | REAL (operator route + UI board); **BROKEN** at runtime (`on_ramp_lot` refund fields missing) | `OnRampRecovery.cs:55-102`; `OnRampLotOperatorController.cs:26-37`; F-C2 |
| Payout account onboarding | **BROKEN** (D1 class) + cross-tenant link minting | F-C4, F-H8 |
| Cash-out saga + bank payout lifecycle | REAL in source; unreachable without an account; claim write is D2 class (`OffRampService.cs:335-337`); `fiat_payout` missing 16 fields; parked states have no exit | `OffRampService.cs`, `BankPayoutProcessor.cs`; F-C2, F-C4, F-H11 |
| Payout reversal, chargeback clawback | REAL; two must-fix MEDIUMs open | F-H3 |

---

## 3. Liveness: parked states and their exits

This table merges the liveness sweep with this assessor's trace. Every NO-EXIT and EXIT-BROKEN
row was re-checked against source.

| State | Exit | Verdict | Finding |
|---|---|---|---|
| KYC submission PENDING/IN_REVIEW (operator queue) | queue read is a typed enum filter | **EXIT-BROKEN**: queue always empty | F-C6 |
| Identomat KYC `AwaitingSubject` / `AwaitingOutcome` / expired `Resuming` | vendor callback only (always 202); restart after `ExpiresAt` | **NO operator or automated exit**; verdict lost | F-H1 |
| Identomat `Claimed` unattached (NeedsOperator) | operator reconcile + console | OK; over-triggered | F-M1 |
| Veriff/generic attempts; KYB `PENDING`; AML manual review | worker / tenant refresh / AML route | OK | — |
| ActionRun `AwaitingReconciliation` | bulk sweep and operator button (typed enum read); per-run route works but has no UI button | **EXIT-BROKEN** (bulk) | F-C6 |
| Action/consent webhook `Pending` | workers disabled; due-scan is a typed enum read; retry only from DeadLettered | **EXIT-DISABLED + BROKEN** | F-H10, F-C6 |
| FiatPayout `NeedsOperator` / `Reversed` with units owed | `/advance` 409; `/return` re-parks or no-ops; `/recover` needs a failed bank payout | **NO-EXIT** | F-H11 |
| FiatReversal `NeedsOperator` | recover/settle/restart return early on NeedsPerson | **NO-EXIT** | F-H11 |
| Reversal rung with a `Failed` idempotency key | `Retry` forever | **NO-EXIT** | F-H3 |
| On-ramp issuance refused after the charge | resume replays Failed; refund needs no-effect proof | **NO-EXIT** (PLAUSIBLE) | F-H11 |
| `OnRampLot` Stuck / Refused / RefundPending (normal) | operator resume/refund + sweep + console board | OK in source; RefundPending with a failed create is refused after 23 h (`OnRampRecovery.cs:136-146`) | LOW |
| Fungible creation Submitted/Confirmed/NeedsOperator | worker never registered; service unregistered | **NO-EXIT** | F-C7 |
| NodeFeeSettlement Prepared | recovery worker never invoked | **NO-EXIT** (no user funds held) | F-M13 |
| Deployment `NeedsOperator` | worker only re-observes; no operator route; cancel refuses | **NO-EXIT** | F-H12 |
| NFT past a confirmed stage (workflow-started, or tenant grant revoked) | renewal refuses | **NO-EXIT** | F-H12 |
| DEX same-call winner, type-0/1 or access-list | settle-review throws | EXIT-BROKEN (latent) | F-M13 |
| WTT publication mismatch; tenant-started op after revoke | ResumeProof/RefetchProof preconditions unmet | EXIT-BROKEN (latent) | WP2 lane A |
| Retired exchange rows | log/flag only | owed operator path confirmed missing | WP4 |
| Wallet identity migration Conflict | operator route exists; BFF does not proxy it | OK without UI | F-M10 |
| Idempotency `InProgress` keys | no TTL sweeper | LOW | — |

## 4. Fail-closed classes not yet swept

This merges the fail-closed sweep, which covered every literal `SurrealQuery.Of`, every `Assign`
site, every positional read and every `type::record` literal, with this assessor's trace.

**New classes and instances:**
1. **Enum lowercasing on typed reads:** F-C6.
2. **Captured-null equality:** F-H7.
3. **Schema absence/drift**, which fails both closed and open: F-C2.
4. **Bare ids into record-link columns (D2 class):** `StripeWebhookInbox.cs:467-468`, plus the
   masked downstream sites in on-ramp, cash-out and reversals (F-C4). The sweep's raw scan found
   none outside fiat. About 200 typed-code candidates are untriaged; its script
   `scratchpad/typedlink.py` should be run against each store's `ToStorage` under WP0.
5. **Creation-fence bypass:** both fiat callers claim without `CreationToken`/`TryBeginCreation`
   (F-C4). KYC and KYB are correct (`HostedSessionFlow.cs:91,151-158`).
6. **The `;` rule (HIGH, CONFIRMED; missing from the ledger).** In SurrealForge 1.2.0,
   `SurrealQuery.Of` throws `ArgumentException` on any `;`, including one inside a string
   literal (`git show v1.2.0:…/SurrealQuery.cs:117-123`).
   - `Providers/Stores/Surreal/SurrealFungibleCreationStore.cs:270-274` embeds
     `'…accounted for; the requested Holon…'`.
   - The completion transaction for every holon-attached fungible issuance therefore throws
     after the asset is already minted on chain.
   - `catch (SurrealStatementException)` does not catch an `ArgumentException`, so the row stays
     `Confirmed` with no authority, projection, reserve or genesis activation. This is
     **F-H13**, owned by WP0: bind the text as a parameter.
7. **Strict-mode extra parameter (LOW, currently unreachable).**
   `SurrealSwapOperationStore.cs:51-63` always binds `_swap_operation`, but the cancel branch
   never references it. `CancelBeforePreparationAsync` on an absent row would throw.
8. **Empty record key (MEDIUM, PLAUSIBLE).** `Helpers/HostedKycCreationFence.cs:25-27` evaluates
   `type::record('tenant_kyc_provider_selection', '')` for tenantless sends, even under
   `WHERE false`. If 3.x rejects an empty key, every direct hosted KYC send fails closed. Guard
   it with `IF`. Line 19 also assumes a bare `SubjectUserId`.
9. **Column-name mismatch (LOW).** `Persistence/SurrealDb/Models/WebhookRegistration.cs:17` maps
   `[JsonPropertyName("secret")] LegacySecret`, but the 1.2.0 scanner emits `legacy_secret`
   (`AttributeSchemaScanner.cs:653-660`). After WP1 regeneration, writes to `secret` are
   rejected. Add `[Column(Name = "secret")]`. It is the only such mismatch across all models.
10. **D3 count.** In addition to the 29 `Of("user", …)` sites, about 17 more throw the same way:
    `SurrealRecordParam.OfLink(<user link>)` in nine stores (`SurrealKycStore.cs:236,238`,
    `SurrealApiKeyStore.cs:158,175`, `SurrealAdminBootstrapStateStore.cs:75,132`, and others),
    six in `Mcp/Tools/*`, and `Of(User.SchemaNameConst, …)` in `FiatReversalLedger.cs:331,355`
    and `OnRampLotLookup.cs:105`. Only the 1.2.1 library fix covers all of them (F-C5).
11. **`RETURN` inside `BEGIN…COMMIT` (needs a probe).** 15 sites in 14 files read a top-level
    `RETURN $x` by position, e.g. `SurrealEthereumPreparationStore.cs:65` and
    `SurrealWttOperationStore.cs:103,136`. Slot parity for scripted transactions is verified
    in SurrealForge, but whether `RETURN` truncates transaction output on 3.x is not. Added as
    probe 7.

**Checked clean, per the sweep:**
- binding names: 17 flagged, 16 cleared as batch-level `LET`s;
- remaining `FOR`, closure and non-leading `LET` forms: zero;
- nested `Combine`: zero;
- array captures: 33 checked;
- reserved ids other than `user`: zero across 142 POCO tables;
- positional offsets on the Ethereum preparation, WTT, tenant bootstrap, swap and KYC identity
  stores: verified;
- raw enum parameters: zero;
- removed 3.x syntax: zero.

**Not read line by line (gap owed to WP0 review):** the `SurrealHoldCommandStore.*` family, the
WTT/swap stage stores' `results[guards.Count + k]` reads, `SurrealNodeFeeSettlementStore`,
`SurrealSagaStore`, `SurrealGovernanceStore.*`, `SurrealConsentLifecycleStore.*` and
`TenantPriceScheduleStore`. The 16 non-literal `Of()` sites had their bindings checked only for
`;`.

## 5. Original inventory and checklist: is each package actually closed?

| Package (plan.md) | Source state | Closed? | Evidence |
|---|---|---|---|
| KYC callback active-attempt/CAS | implemented | no: runtime blocked | F-C2, F-C5 |
| Hosted sessions single-create / ambiguous | implemented for KYC/KYB; fiat bypasses the fence | no | F-C4, F-M1 |
| KYB | implemented | no: no schema, no gate, D7 | F-C2, F-H2 |
| Stripe cumulative transfer reversal | implemented (`Providers/Fiat/StripeTransferReversal.cs`, `PayoutReversalEventHandler.cs`) | unverified | — |
| Stripe connected-account payout lifecycle | implemented | no: onboarding broken, Connect secret | F-C4, F-M2 |
| Stripe operator refund, charged-but-uncredited | **implemented** (more than the unchecked box suggests) | no: schema | `OnRampRecovery.cs:55`; F-C2 |
| Wallet auth Eth/Sol | **implemented** (`Services/Signing/WalletSignatureVerifier.cs:17-46`) | no: DI names the deleted `Ed25519SignatureVerifier` (`Program.cs:1162-1163`) | §6 |
| Provider DEX execution | implemented | no: undeclared | F-H4 |
| Solana metadata/ATA/close | implemented | no: written against non-existent Solnet API shapes (`TokenProgram.CloseAccount` arity, init-only `TransactionInstruction.Data`, missing `ValidateAddressFormat`) | §6 |
| Algorand/Solana call/deploy | implemented | no: Algorand SDK member mismatches (`ApplicationCallTransaction.ApplicationId`, `AssetAcceptTransaction.AssetCloseTo`), CS1628 | §6 |
| Wormhole sequence | WTT replaces it (per-chain `…Wtt…Enabled`); legacy refuses | partial: legacy fabricator remains | F-M8 |
| SDK/frontend fiat DTO | reversal DTO single | no: `FiatPayoutView` missing | F-M6 |
| Docs reconciliation | PROVIDERS.md updated (DEX activation note `:22`) | mostly | — |
| Veriff / generic adapters | real | source-closed | `VeriffKycProviderService.cs:49-147` |
| Governance/CredentialRevoke gates | governance un-gated without a verdict | **regressed** | F-H6 |
| NFT/credential lifecycle | implemented | no: no `nft_lifecycle` schema; recipient costs | F-C2, F-M11 |
| Tinyman builder | real bytes | no: undeclared | F-H4 |
| Delivery: git consolidation | branch unmerged, remote push pending | open | plan.md §Initial Git evidence |

---

## 6. The 247 compile errors grouped by root cause

| # | Root cause | Errors / files | Mechanical? | Fix |
|---|---|---|---|---|
| 1 | `SurrealIdempotencyLedger.IsUniqueViolation` is **private** in SurrealForge 1.2.0 *and* 1.2.1 (`SurrealIdempotencyLedger.cs:344`) | 81 / 71 | mechanical + one design choice | one `Core/Surreal` helper with a precise unique-violation match (LOW above) |
| 2 | `Helpers/Encoding.cs:10` shadows `System.Text.Encoding` | 53 / 21 (incl. 3 `Encoding.UTF8`) | mechanical | rename to `ByteEncoding`; never shadow BCL names |
| 3 | `Algorand.Utils.Encoder` vs `System.Text.Encoder` | 17 / 5 | mechanical | alias |
| 4 | Typed builders passed where `SurrealQuery` is expected (missing `.Build()`) | 17 / 5 | mechanical | `.Build()` |
| 5 | Solana partials call Algorand's private `ValidateAddressFormat` (`AlgorandProvider.cs:388`); cascades to 12 CS0165 in `SolanaProvider.DexQuote.cs` and `NativeArtifact.cs` | 17 + 12 / 6 | mostly mechanical | shared Solana validator; re-read definite-assignment logic |
| 6 | `SurrealLink` missing `using` (`ConsumerGovernanceService.cs`) | 11 / 3 | mechanical | `using SurrealForge.Client;` |
| 7 | `null` for a struct `SigningContext` (WTT quote/proof) | 6 / 6 | small design | nullable or sentinel |
| 8 | **Chain SDK API mismatch**: Nethereum `ABIType` (5), Solnet init-only `TransactionInstruction.Data` (4), `TokenProgram.CloseAccount` arity (1), Algorand SDK members (5), `SolanaNativeMetadata` vs `SolanaAccountArtifact` (6) | ~21 / ~14 | **design**: wire builders were authored against imagined APIs, so a compile fix does not prove wire correctness | rewrite against real SDKs, then protocol fixtures (validator/EVM/algod) |
| 9 | `ref`/`out` in lambdas (CS1628) | 3 / 2 | small | restructure |
| 10 | Vocabulary-merge drift: `ActionNodeType.Gate`, `ActionNodeConfig`, `OperationStatus.Confirmed`, `requestedQuantity`, `JsonSerializer` using | ~6 / 6 | mechanical, but semantics need a check (does `Confirmed` map to `Completed`?) | align with the vocabulary sweep |
| 11 | D1 fiat attach (CS1503 argument 7) | 2 / 2 | **design** (F-C4) | creation fence |
| 12 | DI `Ed25519SignatureVerifier` → `WalletSignatureVerifier` | 1 | mechanical | rename |
| 13 | misc (`RetryOnConflictAsync<T>` inference, `MediaTypeHeaderValue`, `long`→`int`) | ~4 | mechanical | — |

**Estimate.** About 205 of the 247 are mechanical (groups 1–6, 10, 12, 13). That is a half-day
for one Sonnet agent with a build loop. About 40 are design-bearing (groups 7–9, 11), and they
sit in WTT, DEX, Solana-native and fiat wire construction.

**After that:** expect a second wave of errors:
- SRDB0001: about 35 sites, one trusted-template helper;
- IDE0005;
- every error in the never-compiled test projects. Assume hundreds, given +39k lines.

Budget WP0 as **1–2 days elapsed with 2–3 agents**, not one build-fix pass.

## 7. `deferred.md`: does each entry meet the bar?

**IDENTOMAT-WORKFLOWS: meets the bar, with two corrections.**
- *What meets the bar:* the external blocker is real (the vendor dashboard is owner-confirmed),
  and the unblock and closure conditions are concrete.
- *Owner is not named:* "platform operator with Identomat access" is a role, not a person.
- *An internal gap is presented as covered:* the entry implies the in-repo work is done, but
  F-H1 (no Identomat refresh), F-C3 (no first-party begin) and F-M3 (return URLs) are internal
  and are not covered by the vendor dependency. Add a line that the mock boundary covers only
  vendor configuration.

**IDENTOMAT-RECOVERY: meets the bar for *automated* reconciliation of ambiguous creates.**
- The missing vendor contract (an idempotency key or a lookup by client attempt) is
  evidence-backed.
- It should not shelter F-M1. Definite 4xx/429 non-creation is an internal policy choice, not a
  vendor gap.

**Disguised deferrals in the ledger that are not external.** None of these names a vendor or
owner dependency, so by the owner's rule each must be closed in round 4, or carry an explicit
owner decision recorded in `deferred.md`:
- DEX activation (F-H4);
- exchange-module advertisement (F-H4);
- delegated child credentials (F-H5);
- governance gate status (F-H6);
- webhook delivery flag (F-H10);
- "Ethereum/Solana value-custody parity (needs … a reviewed Ethereum settlement contract)";
- "Legacy Wormhole mode + action Bridge/Back cutover", which needs a `wtt` action resolver;
- "WTT stage-key readiness gate";
- "direct hold admission UI";
- "buy-back top-up still on the old non-durable path";
- "operator console fee control";
- "signature-verified External wallets";
- "recipient-stage Solana costs";
- "absence proof for exposed-but-unsigned participant messages".

**Missing genuine external entry.** The SurrealForge 1.2.1 publish (F-C5) is owner-gated. It
should appear in `deferred.md` or in the sweep prerequisites, with the owner and a date, because
it blocks KYC and the whole tenant model.

---

## 8. Proposed round-4 work packages

Order matters: nothing runtime can be accepted before WP0 and WP1. Each package lists an owning
lane, its dependencies, the findings it closes and its exit criteria.

| WP | Content | Lane | Depends on | Closes |
|---|---|---|---|---|
| **WP-OWNER** | Publish SurrealForge 1.2.1 (tag, release). Decide: KYB gate scope (F-H2); first-party KYC and money UI in scope? (F-M7); delegated credentials enable vs delete (F-H5); governance re-block vs verdict (F-H6) | owner | — | F-C5 + decisions |
| **WP0** | Compile and DI convergence. Bump SurrealForge; fix groups 1–6, 10, 12, 13; SRDB0001 trusted-template helper; IDE0005; then compile both test projects. Register the fungible creation service, store and worker. Add a host-build test with `ValidateOnBuild` (F-C7). Fix the enum-read casing and captured-null translation at the library level (SurrealForge 1.2.2: normalize read predicates exactly like writes, funcletize captured nulls). Interim per-site fixes are a fallback. Add an enum round-trip test through every store (F-C6, F-H7). Groups 7–9 and 11 go to their lanes. | core (Sonnet ×2) + SurrealForge maintainer | WP-OWNER publish (or a local feed) | F-C1, F-C6, F-C7, F-H7, F-H13, §4 items 7–9 |
| **WP1** | Schema convergence. Regenerate all 131 tables; inspect for dropped and changed indexes; regenerate goldens and flowcharts; migration to `REMOVE INDEX ethereum_preparation_nonce`; verify every unique index each exactly-once invariant relies on actually exists. Run `dev-up -Reset` once. | core | WP0 (model assembly) | F-C2, F-H9 |
| **WP2** | Lane-owned design compile errors (§6 groups 7–9): rewrite WTT/DEX/Solana-native/Algorand wire builders against the real SDKs, then protocol fixtures. Also: the NFT recipient-stage cost quote (F-M11); NFT renewal for workflow-started and tenant-revoked lifecycles past a confirmed stage; a deployment NeedsOperator operator resolution (F-H12); DEX settle-review for type-0/1 and access-list winners; WTT publication-mismatch and tenant-revoke exits. | A (WTT/Solana), C (DEX/EVM/deploy), N (NFT) | WP0 | §6.7–9, F-M11, F-H12 |
| **WP3** | KYC first-party and liveness. `POST /api/kyc/session` (FirstPartyLogin) calling `BeginAsync`; `POST /api/kyc/refresh` plus an Identomat poll worker for AwaitingSubject/AwaitingOutcome with backoff; restore NONE semantics in `SurrealKycStore`; release 4xx/429 admissions (F-M1); one KYC gate (merge `RealValueKycGate`); D5; dev provider/return-URL defaults; operator refresh for Identomat sessions. | KYC lane (new) | WP0, WP1 | F-C3, F-H1, F-M1, F-M3, F-M4, F-M12 |
| **WP4** | Stripe convergence. D1 via the creation fence for checkout and payout-account claims; D8 (pin `expires_at` on first claim, or attempt-suffix the key); D2 (record links); multi-secret webhook verification; lane B clawback MEDIUMs + crash/definitive-failure fixtures; honor `expectedStateVersion` (F-M5). Audited operator resolutions backed by provider evidence for every parked money state: payout NeedsOperator/Reversed, reversal NeedsOperator, Failed-key rungs, and pre-effect issuance refusal via `FailBeforeValueEffectAsync` (F-H11). Wire the node-fee settlement recovery (F-M13). Add the retired-exchange operator path. | B | WP0, WP1 | F-C4, F-H3, F-H11, F-M2, F-M5, F-M13 |
| **WP5** | KYB enforcement (per the owner decision): a gate on the chosen tenant capabilities (custodial accounts, fiat on-behalf, payouts) plus fixtures; D7. | KYC lane | WP3, WP-OWNER | F-H2 |
| **WP6** | Tenant scope. Fiat intent, cash-out and payout-account fixes (F-H8); payout setup becomes FirstPartyLogin; cross-tenant architecture test; transaction-scope architecture test and `// lock:` annotations (F-M9). | security lane | WP0 | F-H8, F-M9, preview oracle |
| **WP7** | Activation. Declare `IExchangeTransactionModule` (Algorand) and `IDexTransactionModule` (three providers) behind config pins; enable webhook delivery in dev/e2e/prod config with an outbox-drain test; record each activation's runtime evidence. | B, C, core | WP0, WP1, WP2 | F-H4, F-H10 |
| **WP8** | Delegated credentials. Either the per-endpoint scope allowlist plus enabling child JWTs plus HTTP flows for the tenant branches, or deletion of the dead tenant branches. | security lane | WP-OWNER, WP6 | F-H5 |
| **WP9** | Governance. Re-add the three governance types to `NotEnabledActionNodeTypes` until a runtime-backed verdict exists; fix the compile errors; real-DB race fixtures. | core | WP0, WP1 | F-H6 |
| **WP10** | Pruning and SDK. `FiatPayoutView`; retire the SDK DEX adapters (owner decision); delete `WormholeAdapter.DeriveSequenceFromTxHash`; remove `RefundNodeHandler`; RPC-URL scrub in `GetChainInfoAsync`. | V/core (Haiku-able) | WP0 | F-M6, F-M8, LOW |
| **WP11** | End-user UI (if the owner says yes): verify identity, add funds, cash out, on lane V's MSW + Playwright harness. | V + designer | WP3, WP4 | F-M7 |
| **WP12** | Operator console coverage: BFF allowlist + pages for DEX settle-review, WTT, Ethereum recovery, saga, wallet identity. | V | WP0 | F-M10 |
| **Sweep** | Integrated build/test/lint/typecheck/schema plus lane V's layers, run once; batch the failures. | all | WP0–WP12 | plan.md §Round 4 |

**Parallelism.**
- WP-OWNER decisions can run now.
- WP0 must serialize ahead of everything else.
- WP1 follows WP0.
- After WP1, run in parallel: WP2 (A/C/N), WP3+WP5 (KYC lane), WP4 (B), WP6+WP8 (security),
  WP10 and WP12.
- WP7 and WP9 join after WP2.
- WP11 trails WP3 and WP4.

## 9. Runtime probes that settle PLAUSIBLE items (run first in the sweep)

1. **Captured-null equality.** On SurrealDB 3.1.4, against a SCHEMAFULL `option<record<user>>`
   field: `CREATE t:1 CONTENT {}; SELECT * FROM t WHERE f = $p` with `$p = null` bound over CBOR.
   Expect zero rows. That confirms F-H7 and the F-C3 sub-claim.
2. **Undefined field on a SCHEMAFULL table.** Write `creation_token` to the current
   `hosted_session`: does 3.1.4 error or strip it? Either outcome confirms F-C2's fail-closed
   half.
3. **Missing unique index.** Two concurrent creates on `ethereum_preparation` with the same
   `(protocol, sender, nonce, nonce_slot)` before WP1 must both succeed (fail-open), and after WP1
   one must lose.
4. **Payout setup link minting.** A Stripe test-mode check of whether a second Express
   `account_onboarding` link for an onboarded account allows changing the bank account (F-H8
   severity).
5. **Enum casing.** Write a `kyc_submission` through `SurrealWriter` with status `PENDING`, then
   run `GetActiveSubmissionAsync`. Expect no row, which confirms F-C6 on the shipped 1.2.0
   binary, not only on the tag source.
6. **DI validation.** `dotnet run` in Development after WP0 compiles. Expect an
   `IFungibleCreationService` resolution failure at build until F-C7 is fixed.
7. **`RETURN` inside a transaction.** Run `BEGIN; LET $x = (CREATE …); RETURN $x; COMMIT;` on
   3.1.4 and count the result slots. This settles the 15 positional-`RETURN` reads (§4 item 11).
8. **Empty record key.** Evaluate `SELECT * FROM type::record('t', '') WHERE false` on 3.1.4
   (§4 item 8).

## 10. Coverage notes and corrected sweep claims

- **IDOR coverage.** Traced end to end: the money controllers (Fiat checkout and payout, Exchange,
  Hold, Mandates, DEX, WTT, Fungible, NFT costs, Allocation receipts), every `*OperatorController`
  and `Admin/*` controller, and the callbacks.
  - *Spot-checked only:* `KycController` (claims-sourced, `LoadOwned`), `TenantBusinessVerificationController`
    (tenant = authenticated login), `TenantKycConfigurationController` (FirstPartyLogin),
    `WebhookEventController` and `ConsentWebhookEventController` (principal passed into the
    service), and `DeploymentsController` (actor + acting tenant passed into the service).
  - *Not traced, owed by WP6:* `TenantController` custodial routes, Consent/TenantConsent,
    Wallet, Credential, Action/ActionGraphBundle, Holon, Search, TenantPriceSchedule, User,
    PlatformReserve and KeyRotation.
- **Corrected sub-agent claims.** The stub sweep said `/api/admin/fiat-payouts/{id}/recover` has
  no backend route. It exists (`Services/Fiat/OffRamp/FiatPayoutOperatorController.cs:83-84,217`).
  The liveness sweep's "Identomat lost webhook = CRITICAL" is held at HIGH (F-H1), because a
  restart after expiry exists, at the cost of a paid re-verification.
- **Latent versus live.** Every tenant-credential (child-JWT) finding is latent while F-H5
  stands. The API-key tenant paths (fiat, custodial KYC) are live and are what F-H8 covers.
