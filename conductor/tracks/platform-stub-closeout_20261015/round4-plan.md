---
type: implementation-plan
round: 4
status: revised-for-short-re-review
---

# Round 4 reconciled plan — platform-stub-closeout

Base: `feat/platform-stub-closeout` @ `8f89396` (tree = `0522fdf` + docs). Inputs:
`round4-assessment-adversarial.md` (`ADV F-xx`), `round4-assessment-optimism.md` (`OPT Fx`/`OPT §n`),
`round3-join-ledger.md` (`LEDGER`), the 247-error list (`ERR:<line>`), the adversarial plan critique
(`CRIT #n`). Static plan; nothing built or run.

**Revision 2 (2026-10-17):** critic verdict REJECT-with-changes and four owner decisions. Restructured from
17 packages to **7 executor lanes in git worktrees** with self-review/fix loops, self-merge, one
comprehensive independent review, one fix batch, one sweep. Every CRIT item maps to a section: #1 → L4/L5/L1
scopes; #2 → §3.4; #3 → §8; #5 → §5 W1; #6 → §5 W1 re-sweep; #8 → §7; #9 → §4 ownership maps.

**Closing vocabulary:** *Done* = executed evidence. *Built* = compiled, unit-covered. *Authored* = not
compiled. "Approved at source review" is never written as "closed".

## 0. Decisions

### 0.1 Owner decisions, settled 2026-10-17

| # | Decision | Plan consequence |
|---|---|---|
| D-1 | **SurrealForge 1.3.0 published final by the owner** (final commit `b5a7d52`; integration 47/47 on 3.2.4/3.1.4 × memory/RocksDB; unit 1202). Library bugs found later → 1.3.1, owner publishes | W0 step 1 bumps `AZOA.WebAPI.csproj:109,110,113`, `tests/AZOA.WebAPI.IntegrationTests/AZOA.WebAPI.IntegrationTests.csproj:30`, `tests/AZOA.WebAPI.Tests/AZOA.WebAPI.Tests.csproj:45` once it is on NuGet. No local pack, no rc machinery |
| D-2 | **Compile-only builds per lane are allowed**: `dotnet build`, `tsc`, SDK build. No tests, lint gates, schema goldens or E2E before the sweep | Every lane compile-checks its worktree before merging; tests and gates run once in §8 |
| D-3 | **`AwaitingSignature`: add a submit-signed route** — accepts the client-signed transaction, verifies its exact bytes against the saved plan (DEX/WTT/NFT pattern), broadcasts, reconciles to terminal; the idempotency key leaves `InProgress`; stuck rows get an operator-visible outcome | L5 scope (§4) |
| D-4 | **Execution model**: 5–7 large executor lanes, each in its own worktree, self-review + fix loop, merges its own branch (protocol §3.3); then one comprehensive independent review, one fix batch, then the sweep (one pass + ≤3 batches, stop rule, KYC/Stripe/exactly-once gates first) | §3, §6, §7, §8 |

### 0.2 Previously settled — not re-asked

- **Governance gates:** "require independent verdict before lifting those gates" (`plan.md:59`). The lift in `3d08ace` (`NotEnabledActionNodeTypes.cs:8` blocks only `CredentialRevoke`) is reverted in R4; the gates lift only by a verdict recorded after green race fixtures (L1).
- **Signature-verified External wallets** are a round-4 follow-up (RUNBOOK §3) → L4.
- SDK `DexAdapter`/`JupiterAdapter`/`TinymanAdapter`: retire only (RUNBOOK §3) → L7.
- WTT under a tenant credential: own custodial wallets only, relayer fee `"0"` (RUNBOOK §3).

### 0.3 Stated assumptions (departures from RUNBOOK §3 "SurrealForge gaps are fixed in SurrealForge")

| Assumption | Why AZOA-side | Reversal cost |
|---|---|---|
| **A-1** SRDB0001 sites migrate to an AZOA `Core/Surreal/SurrealIdentifierTemplate.cs` (token allowlist `^[a-z0-9_]+$`, one localized suppression), matching `SurrealGeneratedStatementScope` | Analyzer-correct today; 1.3.0 is published and frozen | Low: swap to a SurrealForge identifier-template builder in a later minor; ~36 call sites, mechanical. Logged as a SurrealForge follow-up |
| **A-2** Clawback `Failed` key: attempt-suffixed claim key on the reversal receipt (`ReversalCompensationService.cs:826-831`) | Ledger re-claim semantics would change every SurrealForge consumer | Low: adopt a ledger re-claim API if SurrealForge adds one; one service. Logged as a SurrealForge follow-up |
| **A-3** Frontend typecheck runs in the sweep | RUNBOOK §7 step 7.6 and root `AGENTS.md` supersede the older no-frontend-typecheck memory | none |
| **A-4** Payout-account setup is first-party only (ADV F-H8); dev KYC defaults to Identomat with fakes; gates never accept MANUAL; AML monitor polling on wherever an Identomat config id is set; warning baseline 25 | Security and dev-default fixes, no product trade-off | low |

## 1. Ground truth that supersedes the assessments

| Fact | Evidence | Consequence |
|---|---|---|
| 1.3.0 contains public anchored `IsUniqueViolation(string?)`, `EnsureAllOk` culprit blame, reserved-word `user` ids, buffered-flush fix, enum casing, captured-null → NONE | D-1; RUNBOOK §2 | OPT F5/F6, ADV F-C5/F-C6/F-H7, D3 close at the bump. All 81 classifier call sites pass `.Message` (grep 73/6/2) → compile unchanged |
| 1.3.0 changes typed-read semantics: enums bind as written; `== capturedNull` now matches absent rows | D-1 | New re-sweep classes (§5 W1): raw lowercase-enum / explicit-NULL writes that typed reads now miss; owner/tenant guards on optional columns that now widen |
| `;` inside a `SurrealQuery.Of` literal still throws | SurrealForge `SurrealQuery.cs:105` | ADV F-H13 stays AZOA (`SurrealFungibleCreationStore.cs:273` → parameter) |
| **No `.surql` on `main` defines `ethereum_preparation`** | `Persistence/SurrealDb/{Generated,Migrations}` grep | No kept database has the stale index; the `REMOVE INDEX IF EXISTS` migration is defensive only. The real Railway risk is new fields/indexes against old rows (§8 P2). RUNBOOK DEPLOY-BLOCKING line is downgraded at the W3 closeout |
| `Program.cs` order is load-bearing | `:1680-1681` registers `IRealValueKycGate`; `:1757` `AddFiatOnRamp`; `:1771` `AddReversalRiskValueGate`, which wraps the last-registered gate into `RiskHoldValueGate` (`ReversalRegistration.cs:130-162`) | §3.4 ordering rule; host test |
| Ordinal enum casts | `SurrealActionRunStore.cs:56,59,92,118` `(Row.ActionRunStatusKind)(int)…` | Removed in W0 before any `[Inside]`/enum reorder |
| Converter-less enums | `NodeFeeSettlement.cs:28,38,112-122`, `NodeFeeAtomicGroup.cs:24,74` have `[Inside("Prepared",…)]` strings but no `JsonStringEnumConverter` (25 other models have it) | Converters + architecture test in W0 |
| Webhook retry only from `DeadLettered` | `Surreal{Action,Consent}WebhookDeliveryStore.Management.cs:56,82-83` | L1 fix |
| Parked-runs UI reports success on zero rows | `frontend/src/app/(dashboard)/admin/parked-runs/page.tsx:180`; sweep returns only results (`ActionController.cs:325-336`) | L4 returns scanned count; L7 shows it |
| Compose provider probing prefers Docker | `dev-up.ps1:119` `Find-Compose`; `frontend/e2e/scripts/e2e-stack.mjs:121` | §8 P-1 |
| Ganache 7.9.2 rejects EIP-1898; Hardhat 2.26.3 passes | OPT F7 | L6 |
| Fungible creation unregistered; `Program.cs:1163` names deleted `Ed25519SignatureVerifier` | grep | L1 W0 |

## 2. Reconciliation (unchanged substance; condensed)

**Agreements adopted:** nothing runtime is claimable; ~80% of the 247 errors are mechanical once 1.3.0 is
in; D1/D2/D8 break Stripe at three hops and D2 is a class (ADV F-C4 + OPT F9); clawback MEDIUMs are defects
now; DEX/exchange non-activation and carry-forward lists are disguised deferrals; allow-path real-DB fixtures
are the only fail-closed detector; DexAdapter retirement is small.

| # | Disagreement | ADV | OPT | Resolution |
|---|---|---|---|---|
| 1 | Sequencing | WP0 → WP1 → parallel → sweep | fix batch → 6–8 builds → sweep → 3–4 batches | D-2 + D-4: compile-only per lane; one sweep + ≤3 batches + stop rule |
| 2 | Enum casing / captured null | SurrealForge 1.2.2 or per-site | not found | In 1.3.0 (D-1); AZOA audits the semantic change (§5 W1) |
| 3 | Collateral slots | not found | F5 | Fixed in 1.3.0 |
| 4 | IDENTOMAT-RECOVERY | meets the bar | not needed (orphans inert) | Close in R4 (residual OD-6); F-M1 4xx/429 release with it |
| 5 | IDENTOMAT-WORKFLOWS | meets bar; name owner | owner action, ~15 min | OWNER-ACTION, owner Jade Zaher; internal gaps moved to L2 |
| 6 | RETURN in BEGIN…COMMIT | needs probe; 15 sites | settled; 12 sites, all before COMMIT | Settled; gate rule (L6); re-sweep reconciles the count |
| 7 | `NativeAccounts.cs:81` | SDK mismatch | real bug | Real bug (`ERR:96-97`, swapped types) |
| 8 | Design-bearing compile errors | route to lanes | route to lanes | Fixed in W0 by L1 (the tree must compile before lanes branch), then re-verified by the owning lane's self-review and by the comprehensive review (§7) |

## 3. Execution model

### 3.1 Lanes and worktrees

Seven lanes (§4). Each lane: `git worktree add ..\oasis-sleek-r4-<lane> -b r4/<lane> feat/platform-stub-closeout`,
its own `obj/`/`bin/` (parallel compile checks don't collide). A lead executor per lane; L1 (W0), L3 and L5
may run up to two helpers **inside their own worktree on disjoint subdirectories**. Lanes get full local git
permissions within the protocol below.

### 3.2 Lane loop (inside the worktree)

1. Implement the lane scope plus its W1 re-sweep findings (§5).
2. Compile-check (D-2): `dotnet build azoa.sln -c Debug` (0 errors, warnings ≤ 25); `npm run build` in `sdk/azoa-wallet` and frontend `npm run typecheck` when the lane touched them.
3. **Self-review and fix, ≤2 iterations**, recording each checklist in the lane record:
   `round3-transaction-scope-review.md` checklist on every changed store/fence; root `AGENTS.md` conventions + changed-file pruning gate (raw SurrealQL, catch-alls, duplicated helpers, `inheritdoc`, stale comments, IDE0005, retired vocabulary); security checklist on every changed route/service (authz + signing scope, acting-tenant scoping, input validation, secrets/PII, idempotency and replay, no bare ids into record links); flow-tests rules for authored tests (real collaborators, no tautologies, exactly-once assertions never weakened).
4. Merge (§3.3). Lane record: `conductor/tracks/platform-stub-closeout_20261015/round4-lane-<lane>.md` (`type: lane-checkpoint`): scope closed with file:line, `Program.cs` hunks, POCO changes, published route contracts, compile result, self-review checklists, merge hash.

### 3.3 Merge protocol

- **Order** (strict; the orchestrator may swap two adjacent lanes only if the earlier is blocked >½ day and they share no hot-file region, recorded in both lane records): W0 `r4/core` → W1 `r4/schema` → W2 `r4/platform` (L1) → `r4/identity` (L2) → `r4/money` (L3) → `r4/security` (L4) → `r4/chains` (L5) → `r4/verification` (L6) → `r4/frontend` (L7) → fix batch branches.
- Before merging: `git merge feat/platform-stub-closeout` into the lane branch (merge, not rebase, so shared history is never rewritten); resolve conflicts by the ordering rule (§3.4) and ownership maps (§4); compile clean (D-2) on the merged lane tree.
- Merge: in the main checkout (kept clean during merge windows; the orchestrator commits its docs first), take `.git/r4-merge.lock`, `git merge --no-ff r4/<lane>`, confirm the tree equals the compiled lane tree (`git diff --quiet r4/<lane> HEAD`), release the lock, record the merge hash in the lane record.
- Never push, never force, never rebase or amend a merged commit, never delete another lane's branch. Secret scan (every `.env` value vs the staged diff) before each merge. Worktrees are removed after the comprehensive review.

### 3.4 `Program.cs` ordering rule (L1 writes it into root `AGENTS.md` §Program.cs registration order in W0)

1. A registration that replaces or decorates an existing service is edited **in place** at the existing line, never appended.
2. Order-dependent sequences, top to bottom: `IRealValueKycGate` (`:1680-1681`) → `AddFiatOnRamp` (`:1757`) → `AddOnRampValueRateSource` → `AddFiatReversalPipeline` → `AddReversalRiskValueGate` (`:1771`); the fiat block's internal order is asserted by its own guards (`:1755`). L2's KYC-gate merge swaps the implementation at `:1680-1681`; appending it after `:1771` would replace `RiskHoldValueGate` and bypass the reversal risk hold (money-safety regression).
3. `IWormholeAdapter` (`:1639`) stays registered — `WormholeProtocol` needs it; L5 deletes only the fabricator method (`WormholeAdapter.cs:549-555`).
4. L4 edits `:216-220` (child JWT) and `:641-652` (`DappRoleAssignment` policy) in place.
5. New registrations go inside their domain block next to related ones, never at the end of the file.
6. Host tests (L1, W0; run in the sweep): `ValidateOnBuild` + `ValidateScopes` on the real Development host; `IRealValueKycGate` resolves to `RiskHoldValueGate` wrapping the merged KYC gate; `IWormholeAdapter` resolves; `IFungibleCreationService`/`IFungibleCreationStore`/`FungibleCreationRecoveryWorker` resolve.

### 3.5 Other shared files

- `appsettings*.json`, `docker-compose*.yml`: key ownership — `Kyc:*`/`Kyb:*` L2; `Fiat:*`/`Stripe*`/`Exchange:*` L3; child-credential flag L4; `Dex:*`/`Wtt*` L5; `Webhooks:*` L1. Edit only your keys.
- `tests/AZOA.WebAPI.IntegrationTests/Builders/TestDataBuilders.cs`: L1 makes the class `partial` in W0 and adds the **KYB-approved tenant builder on day 1**; each lane adds `TestDataBuilders.<Lane>.cs`.
- Harness infra (`tests/AZOA.WebAPI.IntegrationTests/{Http/Infrastructure,Factories}/**`, `frontend/src/test/msw/**`, `frontend/e2e/support/**`): L6 owns; other lanes add new files only.
- SDK (`sdk/azoa-wallet/**` incl. `src/api/client.ts`, `src/api/index.ts`, `src/index.ts`, `src/api/api-version.ts`) and all of `frontend/**`: **L7 only** in W2. Backend lanes publish route contracts (§4.9) in their lane record by day 1; L7 implements them.
- **Unlisted files:** any production file matched by no W2 glob in §4 belongs to L1 (`r4/platform`) for re-sweep findings and fixes; no lane edits an unlisted file without recording it.
- Directory `AGENTS.md`: append a section in directories you own; root `AGENTS.md` edited only by L1 (W0: §Program.cs order) and L7 (`AGENTS.md:86` DexAdapter wording).

## 4. Lanes

### L1 — Core and platform · opus lead (+2 sonnet helpers in W0) · branches `r4/core` (W0), `r4/schema` (W1), `r4/platform` (W2)

**W0 `r4/core` — compile and DI convergence** (the only lane in W0; must merge a compiling tree):
- Step 1: bump the five SurrealForge references to 1.3.0 (D-1).
- Prelude: rename `Helpers/Encoding.cs` class → `ByteEncoding` (CS0104 ×50 + 3 `Encoding.UTF8`; never shadow BCL names, rule into `Helpers/AGENTS.md`); `using AlgorandEncoder = Algorand.Utils.Encoder;` in five files (`ERR:11-44`, ×17).
- Mechanical (helper 1): `.Build()` ×17 (`SurrealEthereumPreparationStore.cs:94`; `SurrealHostedProviderAttemptStore.cs:35,77,89,129,137,146`; `SurrealUserStore.cs:89,153,289,302,314`; `SurrealWalletAuthChallengeStore.cs:66,113`; `WalletAuthIdentityMigrationService.cs:48,123,141`); `using SurrealForge.Client` (`HoldReleaseWorkflowFence.cs:28-31`, `NftWorkflowCostAuthorityFence.cs:28-29`, `ConsumerGovernanceService.cs:77-121`); `SurrealHoldCommandStore.Mandate.cs:34`; `RetryOnConflictAsync<T>` (`SurrealWebhookRegistrationStore.Management.cs:117`, `.Withdrawal.cs:48`); `LeasedWebhookSender.cs:38`; `ActionManager.cs:1578`; vocabulary drift `ActionSigningScopes.cs:38`, `DeploymentArtifactCompiler.cs:54`, `NftLifecycleOperationProjection.cs:32` (record the `OperationStatus` mapping for L5); **SRDB0001** per A-1 across ~36 sites (largest `SurrealBridgeStore.cs`×5, `SurrealActionWorkflowAdmissionStore.cs`×4, `SurrealActionLocalMutationStore.Witnesses.cs`×4, `DeploymentAuthorityFence.cs`×4, `WalletAuthIdentityMigrationService.cs`×3), no per-site pragmas; IDE0005.
- Design-bearing (lead, opus): Solana `ValidateAddressFormat` never written (17 sites, `ERR:81-119`) → `Providers/Blockchain/Solana/SolanaAddress.cs` over `TryParsePublicKey` (`SolanaProvider.cs:1035`), on-curve vs PDA per site in `Solana/AGENTS.md`; Jupiter fee caps CS0165 (`SolanaProvider.DexQuote.cs:45-117`, re-derived from the reviewed contract); `NativeArtifact.cs:30-35`; `SigningContext` null ×6 (WTT quote/proof, `ERR:46,48,62,63,114,117`) — the context the WTT contract binds; Algorand `ApplicationId` ×4 / `AssetCloseTo` / `long→int` (`ERR:9,29,32,45,47,49`); Solnet init-only `Data` ×4 + `CloseAccount` (`ERR:64,93,113,116,127`); **swapped arguments** `NativeAccounts.cs:81`; CS1628 ×3; Nethereum `ABIType` ×5 cascade; fiat D1 (`FiatRailProvider.cs:610-617`, `PayoutAccountService.cs:179-246`: claim sets `CreationToken`, `TryBeginCreationAsync` before the Stripe create, attach with the token, persist the connected-account id) + F-M5 (`SurrealHostedSessionStore.cs:231-233`) + `OffRampService.cs:452` `requestedQuantity` (intent from `git log -S`, decision in `Services/Fiat/OffRamp/AGENTS.md`).
- Correctness defects found by CRIT/ADV: ordinal casts → name-based mapping (`SurrealActionRunStore.cs:56,59,92,118`); `JsonStringEnumConverter` on `NodeFeeSettlement`/`NodeFeeAtomicGroup` enums + `tests/AZOA.WebAPI.Tests/Architecture/EnumStorageConverterTests.cs` (every enum POCO property with `[Inside]` strings has a string converter); F-H13 `SurrealFungibleCreationStore.cs:273`.
- DI: `Program.cs:1163` → `WalletSignatureVerifier`; register fungible service/store/worker (ADV F-C7); §3.4 rule + host tests.
- Tests (helper 2, after the app compiles): compile both test projects; adapt tests to production signatures (e.g. `WalletManagerTests.cs` removed `WalletType`, `HostedSessionFlowTests.cs:696` old attach fake); never bend production; never weaken an exactly-once assertion; deletions only when the premise is gone, listed in the lane record. `TestDataBuilders` partial + KYB-approved tenant builder.
- SDK/frontend compile only: declare `FiatPayoutView`; drop stale swap re-exports (`src/api/index.ts:112-114`); SDK build + frontend typecheck clean.
- Owned (W0): the whole tree. Merge gate: `azoa.sln` 0 errors, warnings ≤ 25, SDK + frontend compile clean.

**W1 `r4/schema`** (branch from feat after the W0 merge; schema generated from the clean tree, never concurrently with W0):
- Regenerate all ~130 tables + flowcharts/goldens with the 1.3.0 CLI (ADV F-C2: 61 tables missing, ~120 fields, e.g. `hosted_session.creation_token`, `kyc_submission.aml_lineage_key`, `on_ramp_lot` refund fields, `fiat_payout`); `[Column(Name = "secret")]` on `WebhookRegistration.cs:17`; defensive migration `REMOVE INDEX IF EXISTS ethereum_preparation_nonce ON TABLE ethereum_preparation;` (dropped if P2 shows it errors on an absent table).
- **Unique-index audit** in `Persistence/SurrealDb/AGENTS.md`: every single-winner invariant → its generated `UNIQUE` index (nonce `(protocol, sender, nonce, nonce_slot)`, KYC person key per tenant D23, hosted admission, provider attempt, idempotency key, fiat event id, on-ramp lot per intent, payout per request, clawback receipt, WTT body/VAA, swap operation, DEX quote, NFT lifecycle, fungible creation, hold command, consent grant, wallet address); missing → POCO attribute fix now.
- **Old-row rule:** a new field on a table that exists on `main` is `option<>` or `[Default]`, else a reviewed backfill migration. `[Inside]` lists mirror domain enums (after the W0 cast removal).
- POCO-table ownership map (§4.8) written into `Persistence/SurrealDb/AGENTS.md`.
- Owned: `Persistence/SurrealDb/**`, golden/flowchart dirs. Merge gate: POCO table count = generated file count; no hand-edited `.surql`; compile clean.

**W2 `r4/platform`:**
- Webhooks (ADV F-H10, CRIT #1): operator retry from any undelivered state (`DeadLettered`, `Pending` past its backoff budget, expired lease) with a version CAS (`.Management.cs:56,82-83`); due-scan liveness (enum casing via 1.3.0); `Webhooks:Enabled` evidence-gated — on in Development/IntegrationTest/e2e (`docker-compose.e2e.yml:111`), production on only after the drain flow and delivery fixtures are green (§8 gate record); outbox-drain HTTP flow.
- Governance (§0.2): re-block GovernanceProposalCreate/Vote/Tally (`NotEnabledActionNodeTypes.cs:8`); finish the verification suite (`round2-governance-verification-migration.md`); real-DB race fixtures for proposal/vote/tally/finalization (`SurrealGovernanceStore.Finalization.cs:47`); document the verdict-gated lift.
- Owned (W2): `Services/Webhooks/**`; `Providers/Stores/Surreal/{SurrealActionWebhookDeliveryStore*,SurrealConsentWebhookDeliveryStore*,SurrealWebhookRegistrationStore*,SurrealGovernanceStore*}.cs`; `Controllers/{WebhookEvent,WebhookEventOperator,ConsentWebhookEvent,ConsentWebhookEventOperator,WebhookRegistration,WebhookRegistrationOperator,NodeGovernance}Controller.cs`; `Services/Actions/NotEnabledActionNodeTypes.cs`; `Services/Governance/**` except `NodeFeeSettlement*`; `Managers/NodeGovernanceManager.cs`; root `AGENTS.md` §Program.cs order; host tests; webhook/governance tests.

### L2 — Identity: KYC, KYB, Identomat, custody gate · opus · `r4/identity`

- Scope: ADV F-C3 (no begin route: `KycController.cs:34-117`; `KycManager.cs:115-118` uncalled) → `POST /api/kyc/session` (FirstPartyLogin); F-H1 (`KycCallbackController.cs:62-91` always 202; `HostedSessionFlow.cs:337-363`; `HostedIdentityRecoveryWorker.cs:22-32` skips Identomat; operator 404 `HostedSessionReconciliationController.cs:110-118`; MANUAL_CHECK `IdentomatKycProviderService.cs:376-450`) → user + operator refresh re-fetching `/result/` (D8) and a backoff poll worker within `IdentomatRateBudget`; F-M1 4xx/429 release, timeouts/5xx stay quarantined (`IdentomatHttpTransport.cs:100-146`, `HostedSessionFlow.cs:162-173`); F-M12 merge `Services/Bridge/RealValueKycGate.cs` into `Services/Kyc/KycGateService.cs`, registration swapped **in place** at `Program.cs:1680-1681` (§3.4); F-M3/F-M4/D5/D7 (`HostedSessionReconciliationController.cs:46,89`, `KycSettings.cs:121`, `docker-compose.dev.yml:141,145,154`); KYB enforcement per residual OD-2 at `ITenantConsentGate` and custodial access, including `KeyCustodyService` call sites affected by the gate change; IDENTOMAT-RECOVERY per residual OD-6 (orphan supersede within budget, `/external-api/delete/` for late responses, unknown-session callbacks inert); `HostedKycCreationFence.cs:25-27` empty key IF-guard; LEDGER audit-1 LOW `KycController.cs:74`.
- Owned: `Controllers/{Kyc,KycCallback,HostedSessionReconciliation,HostedIdentityCallback,IdentomatKybCallback,TenantBusinessVerification,TenantKycConfiguration,AmlReview}Controller.cs`; `Managers/{KycManager,KycControlPlaneManager,TenantCustodialAccountManager}.cs`; `Services/{Kyc,Kyb,HostedSessions}/**`; `Services/Bridge/RealValueKycGate.cs`; `Services/Consent/{TenantConsentGate,TenantCustodialAccess}.cs`; `Services/Custody/KeyCustodyService.cs`; `Providers/{Kyc,Kyb}/**`; `Helpers/HostedKycCreationFence.cs`; `Settings/Kyc*.cs`; `Providers/Stores/Surreal/{SurrealKyc*,SurrealKyb*,SurrealHostedSession*,SurrealHostedProviderAttempt*,SurrealPersonOperationClaim*}.cs`; L2 POCOs (§4.8); KYC/KYB/hosted tests and `Http/{IdentityVerification,BusinessVerification,HostedSessionReconciliation,Kyc*}FlowTests.cs`.
- Contracts (§4.9): KYC begin/status/refresh; operator Identomat refresh.
- Acceptance: real-DB allow path begin→callback→result→gate open; abandon/reject deny; orphan + late delete; unknown-session callback inert; KYB allow/deny; `KycEvidenceFence` empty-array and multi-guard (LEDGER). Sweep proves: KYC/KYB flows on vendor-doc fakes; with residual OD-8 the live drill (approve/abandon/reject/replay KYC, approve/reject KYB, one lost-response, D4 standard mode).

### L3 — Money: Stripe, fiat, reversal, Hold/exchange, reserve · opus lead + 1 sonnet helper (Hold/exchange) · `r4/money`

- Stripe rail: D2 `StripeWebhookInbox.cs:467-468` + masked class sites `OnRampSettlementHandler.cs:436,545-547`, `OffRampService.cs:335-337` → `FiatPayoutLedger.cs:112`, `FiatReversalEventHandler.cs:239-242`; D8 `FiatRailProvider.cs:569-595` (persist `expires_at` at first claim); F-M2 signing-secret list (`StripeWebhookController.cs:69-94`); F-H8 tenant scope (`FiatPayoutController.cs:105-116` first-party setup; intents `FiatRailProvider.cs:636-660,798-799`; admission key `FiatCheckoutController.cs:281-282`; payout history/replay); audit-2 H3 per residual OD-4 (`OffRampService.cs:865-866`); LOWs `FiatEventRecovery.cs:24-37`, `OnRampRecovery.cs:136-146`. Re-verify L1's D1 fix with fixtures.
- Reversal: clawback MUST-FIX (`ReversalCompensationService.cs:223-225`, `:716-733`, `:826-831` per A-2); reversal `NeedsOperator` exit (`:176,343`; `FiatReversalOperatorController.cs:133-155`).
- Parked money exits (ADV F-H11): payout `NeedsOperator`/`Reversed` (`FiatPayoutOperatorController.cs:167-176`, `OffRampService.cs:667-711`) → audited record-Paid / return-units resolutions re-reading Stripe evidence; pre-effect issuance refusal → `FailBeforeValueEffectAsync` (`AllocationManager.cs:230,239,269`) so `OnRampRecovery.cs:68-75` refunds; node-fee recovery worker wired (`NodeFeeSettlementRecoveryWorker.cs:7`, `AllocationManager.cs:226,894`); idempotency `InProgress` keys after a crash on money paths (TTL re-claim if reachable).
- Hold/exchange (helper): direct hold admission UI endpoints (liability reads); durable recipient-readiness stage for direct buys/sales; buy-back top-up onto the durable transfer path; retired exchange rows operator path; preview consent oracle (`ExchangeController.cs:86-91` → `ReserveExchangeManager.cs:328-330`); Hold participant stage on original-login expiry (N→B request); reserve partitions LOW (`PlatformReserveController.cs:170-197`).
- Owned: `Providers/Fiat/**`; `Controllers/{FiatCheckout,FiatPayout,StripeWebhook,TenantPriceSchedule,Exchange,HoldCommands,HoldReleaseMandates,PlatformReserve,ProjectTreasuryTenant,AllocationReceipt,FungibleCreation}Controller.cs`; `Services/Fiat/**`; `Services/{Hold,Reserve,Fungible,Allocation}/**`; `Managers/{AllocationManager,AllocationReceiptManager,NodeFeeSettlementManager,ReserveExchangeManager,FungibleTokenManager,NodeTreasuryManager}.cs`; `Services/Governance/NodeFeeSettlement*.cs`; `Providers/Stores/Surreal/{SurrealHold*,HoldReleaseWorkflowFence,SurrealNodeFeeSettlementStore,SurrealFungible*,SurrealPlatformReserveLedgerStore,SurrealPlatformTokenStore}.cs`; `Providers/Blockchain/Algorand/AlgorandProvider.Exchange*.cs`; L3 POCOs; fiat/hold/exchange tests, `Http/Stripe*FlowTests.cs`.
- Acceptance: no bare id reaches a `[References]` column on a fiat path; lost-create replay returns the same session; Connect events verify; every parked money state has an audited exit; crash-between-reservation-and-claim, definitive-failure and concurrent-pass fixtures. Sweep proves: Stripe test-mode drill — checkout `4242…` → CLI-signed webhook → credit; refund; dispute `4000000000000259` → reversal → clawback; Connect transfer; payout `000123456789` success / `000111111116` failure; ADV probe 4.

### L4 — Security, routes, platform resources · opus · `r4/security`

- Audit-1 H1 deployments skip signing scopes (routes `DeploymentsController.cs:24,63,86,97`; `DeploymentService.cs:129-197`; `DeploymentService.Quote.cs:79-88`) → capture credential + `SigningAdmissionAuthorityFactory.BindDirectAsync(…, [scopeFor(kind)])`; ADV F-H12 deployment `NeedsOperator` resolution (`SurrealDeploymentStore.cs:134-137`, `DeploymentService.Recovery.cs:76-78`).
- Audit-2 H2 dapp-role (`UserController.cs:144-147`; policy `Program.cs:641-652` in place; `UserManager.cs:255-277`); audit-2 H1 holon clone (`HolonController.cs:180-182`, `HolonManager.cs:566,596-639`, skip `!CanRead(child)` as `:390`); M6 propagate/attach (`HolonManager.cs:120,145,190,433,456-480,577,676`); M1–M5 actions (`ActionManager.cs:244-252,548-589,883-891,1002-1005`; `ActionController.cs:118-144,368-526`); dependency-status leak (`ActionManager.cs:1255-1351`); oracles (`DappCompositionManager.cs:556-563`); ActionGraphBundle `DappDevelop`.
- Audit-1 M1 (`TenantManager.cs:149-151,224-226`), M2 + External-wallet proof flag via the `WalletAuthManager` challenge (`WalletManager.cs:530-535`); audit-2 M8 legacy bridge (`CrossChainBridgeService.cs:140-193,857-904`); LOWs: `ClaimsPrincipalExtensions.cs:110`, `CredentialManager.cs:52`, ApiKey rotate (`ApiKeyController.cs:247`, `SurrealApiKeyStore.cs:73-91`), `NetworkController.cs:94`, key-rotation step-up, faucet cap `WalletManager.cs:635`, provisioning email `TenantManager.cs:99-100`, `Controllers/AGENTS.md:219`.
- **`AwaitingReconciliation`** (CRIT #1): sweep returns `{ scanned, results }` (`ActionController.cs:325-336`, `ActionManager.cs:1761`); per-run reconcile unchanged.
- Delegated child credentials per residual OD-1 (`Program.cs:216-220` in place; `TenantController.cs:190-197`).
- Gates: `tests/AZOA.WebAPI.Tests/Architecture/RouteAuthorizationGateTests.cs` + checked-in inventory (every POST/PUT/DELETE classified; value-moving actions show `BindDirectAsync`/`HasSigningScope`/signing fence; unclassified fails); `CrossTenantGateTests.cs` (owned-resource reads/commands compare acting tenant).
- Owned: `Controllers/{Deployments,User,Wallet,WalletAuth,WalletIdentityOperator,Tenant,TenantConsent,Consent,Credential,ApiKey,Network,KeyRotation,Bridge,Holon,HolonTypeRegistry,Action,ActionGraphBundle,ActionRecoveryOperator,ActionSigningAuthorization,DappComposition,DappSeries,Search,SagaOperator}Controller.cs`; `Controllers/AGENTS.md`; `Services/{Deployments,Consent,Sagas}/**` except L2's two files; `Services/Actions/**` except `NotEnabledActionNodeTypes.cs` (L1), `Wtt*.cs` and `Handlers/RefundNodeHandler.cs` (L5); `Services/CrossChainBridgeService.cs`; `Managers/{UserManager,WalletManager,WalletAuthManager,TenantManager,CredentialManager,ConsentManager,HolonManager,HolonTypeRegistryManager,ActionManager*,ActionGraphBundleManager,DappCompositionManager,SearchManager}.cs`; `Helpers/{ClaimsPrincipalExtensions,ActionSigningScopes}.cs`; `Providers/Stores/Surreal/{SurrealDeployment*,SurrealNativePreparationStore,SurrealApiKeyStore,SurrealWallet*,SurrealCredential*,SurrealUserStore,SurrealUserExternalLogin*,SurrealConsent*,SurrealAction*,SurrealHolon*,SurrealBridgeStore,DeploymentAuthorityFence}.cs` except L1's webhook-delivery stores; L4 POCOs; the two gate tests; tests for these.
- Contracts: wallet ownership challenge/proof; deployment operator resolution; reconcile-sweep response shape.

### L5 — Chains: NFT, DEX, WTT, operations · opus lead + 1 sonnet helper (WTT) · `r4/chains`

- **`AwaitingSignature` submit-signed (D-3):** `POST /api/blockchain/operations/{id}/submit-signed` (owner-scoped, signing scope checked). Verify the signed transaction's signing payload is byte-identical to the saved plan (Algorand `txn` bytes; Ethereum RLP unsigned payload + recovered signer = owner wallet; Solana message bytes + fee-payer signature), broadcast, reconcile to a terminal state (`ReconciliationService.cs:82` set), complete/fail the idempotency key so it leaves `InProgress`. Stuck rows past a configurable TTL: re-observe the chain first (the client holds signed bytes and may broadcast itself); Algorand/Solana past their validity window → terminal abandoned outcome; Ethereum (signed tx never expires, LEDGER parity HIGH L39) → `NeedsOperator` unless a different tx consumed the nonce. All outcomes operator-visible.
- Audit-2 M9 holon mint/exchange bypass (`BlockchainOperationManager.cs:476`) and acting-tenant reads (`:447,453`); L4's `HolonController.cs:94-122` routes to the checked method (contract).
- NFT (lane N): ADV F-H12 renewal past a confirmed stage (`NftLifecycleService.ActorContinuation.cs:39-44`); F-M11 recipient-stage Solana costs (contract note first, from the DEX side of this lane); revoke issuing-tenant (`NftLifecycleService.cs:217,227`); audit-2 M7 bindings survive transfer (`UserNFTService.cs:118-140,194-217`); prune `RefundNodeHandler`; LOW 6 `SurrealNftLifecycleStore.cs:225`; LOW 5 fixtures; confirm L1's `OperationStatus` mapping.
- DEX/activation (lane C): residual OD-3 declarations (`IDexTransactionModule` ×3, `IExchangeTransactionModule` on Algorand `AlgorandProvider.cs:32-33`) with per-network pins, production on per chain only after its protocol fixture is green; ADV F-M13 settle-review type-0/1/access-list (`EthereumRpcEnvelope.cs:17-20`); `DexOperatorController.cs:21` log order; CleanupReady self-hold; D6 (`EthereumRecoveryController.cs`); D9 handler/`IHttpClientFactory` injection (`AlgorandProvider.cs:377`, `EthereumProvider.cs:182`, `SolanaProvider.cs:161`); RPC URL scrub (`EthereumProvider.cs:814`, `SolanaProvider.cs:601`); `SurrealSwapOperationStore.cs:51-63`; prune `swap_state`.
- WTT (lane A, helper): delete only the fabricator method (`WormholeAdapter.cs:549-555`; registration `:1639` stays, §3.4); `wtt` action resolver for Bridge/Back behind WTT `Enabled`; stage-key readiness **degrade** + rewrap (`WttStageKeyReadiness.cs`); publication-mismatch and tenant-started-after-revoke exits; absence proof for exposed-but-unsigned participant messages; accept L4's proof flag at `WttLifecycleService.cs:263`; lane A final LOWs (`EthereumProvider.WttPreparation.cs:195`, `RetireStageAsync` probe, DB-clock fixture `WttLifecycleFlowTests.cs:440`, recipient address index, Algorand opt-in).
- Re-verify W0 design-bearing chain fixes in self-review (fee caps, WTT signing context, Algorand members, Solnet shapes).
- Owned: `Providers/Blockchain/**` except `AlgorandProvider.Exchange*.cs` (L3); `Services/{Dex,Wormhole,Users,Reconciliation,Ethereum,Blockchain}/**`; `Services/WormholeAdapter.cs`; `Services/Bridge/{WormholeProtocol,BridgeOptions,BridgeProtocolServiceCollectionExtensions}.cs`; new `Services/Actions/Wtt*.cs`; `Services/Actions/Handlers/RefundNodeHandler.cs`; `Managers/{BlockchainOperationManager*,NftManager}.cs`; `Controllers/{BlockchainOperation,Dex,DexOperator,EthereumRecovery,EthereumOperatorRecovery,WttBridge,WttOperator,Nft,NftReplacementCosts,NftWorkflowCosts,UserNFT}Controller.cs`; `Helpers/Nft*.cs` (incl. `NftLifecycleOperationProjection.cs`); `Providers/Stores/Surreal/{SurrealNft*,Nft*Fence,SurrealSwap*,SurrealDex*,SurrealEthereum*,SurrealWtt*,SurrealOperationLog*}.cs`; L5 POCOs; chain tests (not `*ProtocolTests.cs`).

### L6 — Verification: protocol fixtures, real-DB gates, harness · sonnet lead + opus for interleaving and WTT six-route design · `r4/verification`

- Protocol fixtures: Hardhat 2.26.3 replaces Ganache (`contracts/ethereum/package.json:18` + lockfile; `Fixtures/Ethereum/chain.mjs:8-11`, `dex-chain.mjs:11-30`; `Fixtures/Ethereum/AGENTS.md:3,13,21`); Uniswap lockfile audit (10 findings); Tinyman LocalNet (validator bytes from algod `GET /v2/applications/{id}`, pinned app id/round/sha256); Jupiter/Whirlpool clone (`--clone-upgradeable-program` + one pool); WTT six-route (Hardhat core + token bridge with a local guardian; Solana `--bpf-program` + `initialize`; Algorand TEAL on LocalNet).
- Real-DB gate fixtures `tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/Round4Gates/**`: enum round-trip for every enum-typed column; captured-null filters (first-party KYC approval, `SurrealFungibleCreationStore.cs:296-297`, `SurrealHoldCommandStore.cs:53-56`) **plus guard-widening negatives** (a captured-null owner/tenant guard must not admit another subject's absent-owner row); one THROW-marker and one duplicate case per store family; **allow path for every `*Fence.cs`** (21); interleaving: allowance claim vs old-op authorize/cancel, exposure vs withdrawal, Hold source consumption vs revocation, concurrent KYC begin, on-ramp lot double claim.
- Architecture tests: `TransactionScopeGateTests.cs` (BEGIN ends in COMMIT with a THROW guard; `AuthorizedAt|ExternalExposedAt` only via `CommitStageAsync`; in-batch SELECT has `// lock:`; nothing after a top-level RETURN), `SurrealQlSourceRulesTests.cs` (no `;` in `Of` literals; no bare-id assignment to `[References]`; no `Assign(Key(…))`; no ordinal enum casts; SRDB0001 only in the template helper). `// lock:` annotations: each lane annotates its own stores; L6 annotates stores with no W2 owner.
- Mock debt: `SurrealFungibleProjectionTests.cs`, `SurrealActionNftHolonLinkTests.cs` fake only the chain edge.
- Upgrade rehearsal assets for §8 P2: `tests/AZOA.WebAPI.IntegrationTests/Fixtures/UpgradeRehearsal/**` (one valid row per table that exists on `main`, plus an assertion script).
- Owned: `tests/AZOA.WebAPI.IntegrationTests/{Fixtures,Http/Infrastructure,Factories}/**`; `tests/AZOA.WebAPI.IntegrationTests/*ProtocolTests.cs`; `…/Persistence/Surreal/Round4Gates/**`; the two mock-debt files; the two architecture tests; `TestDataBuilders.cs` (core file); `contracts/ethereum/{package.json,package-lock.json,scripts/**}`; `.github/workflows/ci.yml`; `frontend/src/test/msw/**`, `frontend/e2e/support/**` (L6 owns these infra paths; L7 adds new files there only).

### L7 — Frontend, SDK, operator console · sonnet · `r4/frontend` (merges last)

- SDK: `DexAdapter`/Jupiter/Tinyman retirement (`src/index.ts:53,65`, `src/dex/**`, `src/wallet.ts`, `src/core/{types,index}.ts`, `src/client/azoa-client.ts`) + root `AGENTS.md:86`; one `assertHex64`/`assertHex32` on DEX (`client.ts:3369-3422`), WTT (`:3464-3510`), Hold (`:3086-3161`), NFT lifecycle (`:3546-3681`) URL ids; methods for every §4.9 contract (incl. submit-signed, KYC begin/refresh, wallet proof, payout/reversal resolutions, deployment resolution); `src/api/api-version.ts` paths.
- First-party UI per residual OD-7: `frontend/src/app/(dashboard)/{verify-identity,funds,cash-out}/**`, sidebar.
- Operator console: BFF allowlist `frontend/src/app/api/operator/[...path]/route.ts:20-43,83` (**SEC** in §7) and pages under `frontend/src/app/operator/(console)/**` for DEX settle-review, WTT, Ethereum recovery, saga, wallet identity, node fee control, Identomat refresh, deployment resolution, stuck `AwaitingSignature` operations.
- Admin pages `frontend/src/app/(dashboard)/admin/**`: `parked-runs/page.tsx` shows the scanned count and never toasts success on zero rows or on error (`:180`); `fiat-payouts`, `fiat-reversals` call the new resolutions; webhook retry from any undelivered state.
- Frontend slices/journeys for every lane (`frontend/src/test/slices/<domain>*`, `frontend/e2e/journeys/<domain>*`).
- Owned: `sdk/azoa-wallet/**`; `frontend/**` except L6's infra paths; root `AGENTS.md:86`.

### 4.8 POCO-table ownership map (W2; L1 owns all tables in W0/W1)

| Lane | Tables (POCO files in `Persistence/SurrealDb/Models`) |
|---|---|
| L1 | `ActionWebhookEvent`, `ConsentWebhookEvent`, `WebhookEventRetryAudit`, `WebhookRegistration`, `WebhookRegistrationAudit`, `GovernanceCommandReceipt`, `GovernanceProposal`, `NodeGovernance*`, `NodeFeeSchedule`, `IdempotencyKeyStore`, `DataMigration`, `SagaSteps`, `AdminBootstrapState`, `Hnsw*`, `Ecosystem*`, `Executes`, `ForkedFrom`, `LocalEntityMutationLock` |
| L2 | `Kyc*`, `KybVerification`, `HostedSession*`, `HostedProvider*`, `TenantKycProviderSelection`, `PersonOperationClaim` |
| L3 | `FiatEvent`, `FiatPayout`, `FiatReversal`, `OnRampLot`, `TenantPriceSchedule`, `Hold*`, `ExchangeHoldSettlementReceipt`, `ExchangeHoldSource`, `ExchangePurchaseAllocation`, `PlatformReserveLedger`, `ProjectTreasuryLedger`, `PlatformToken`, `NodeFeeSettlement`, `NodeFeeAtomicGroup`, `NodeFeeAudit`, `NodeTreasury*`, `Fungible*` |
| L4 | `User`, `UserExternalLogin`, `OAuthExchangeCode`, `ApiKey`, `Wallet`, `WalletAuth*`, `ConsentGrant`, `ConsentAudit`, `ConsentLifecycle*`, `ConsentParticipation*`, `Holon`, `HolonType`, `Action*` (except `ActionWebhookEvent`), `ActionGraphBundle`, `DappSeries*`, `Deployment*`, `NativePreparation`, `NativeQuote`, `BridgeTx`, `ConsumedVaaLedger` |
| L5 | `Nft*`, `Wtt*`, `SwapOperationRecord`, `SwapStageRecord`, `SwapState` (prune), `DexQuoteRecord`, `EthereumDexAllowance`, `EthereumPreparation`, `EthereumSignerCheckpoint`, `OperationLog` |

Generated `.surql` is regenerated only at §8 P1 (W2 POCO deltas), never hand-edited.

### 4.9 Route contracts (published day 1; renamed only by editing this table before the lane merges)

| Route | Lane |
|---|---|
| `POST /api/kyc/session`, `GET /api/kyc/status`, `POST /api/kyc/refresh`; `POST /api/operator/hosted-sessions/{id}/refresh` (Identomat) | L2 |
| payout resolve (record-Paid / return-units) and reversal settle-with-evidence under the existing `/api/admin/fiat-payouts/{id}` and `/api/admin/fiat-reversals/{id}` roots | L3 |
| wallet ownership challenge + proof under `/api/wallet/{id}`; deployment operator resolution; `runs/reconcile-sweep` → `{ scanned, results }` | L4 |
| `POST /api/blockchain/operations/{id}/submit-signed`; operator list/resolve for stuck `AwaitingSignature` | L5 |
| webhook operator retry (any undelivered state) | L1 |

## 5. Wave order

```
W0  L1 r4/core: bump 1.3.0 → prelude → mechanical ∥ design-bearing → tests → compile clean → MERGE #1
W1  L1 r4/schema (clean tree) ∥ SurrealQL re-sweep (read-only, opus)                  → MERGE #2
W2  L1 r4/platform · L2 · L3 · L4 · L5 · L6 · L7 in parallel worktrees
    merges in order: L1p → L2 → L3 → L4 → L5 → L6 → L7 (each: merge feat, compile, --no-ff)
W3  comprehensive independent review (§7) → ONE fix batch (≤3 executors, same protocol)
SWEEP §8 (one pass, ranked gates first) → ≤3 failure batches → MERGE-TO-MAIN §9
```

**W1 SurrealQL re-sweep** (CRIT #6; opus, read-only on the post-W0 tree; output
`round4-sql-sweep.md`, each finding tagged with the owning lane from §4 so it becomes lane work in W2):
ADV §4's unread set (`SurrealHoldCommandStore.*`, WTT/swap `results[guards.Count + k]`,
`SurrealNodeFeeSettlementStore`, `SurrealSagaStore`, `SurrealGovernanceStore.*`, `SurrealConsentLifecycleStore.*`,
`TenantPriceScheduleStore`, the 16 non-literal `Of()` bindings); the five classes (binding names, strict
validator, array captures, positional offsets, 3.x syntax); D2/F9 triage of ~200 typed `[References]`
candidates; RETURN-site count; **new 1.3.0 classes**: ordinal enum casts; raw writes of lowercase enum
literals or explicit `NULL` that typed filters now read; captured-null guard widening on owner/tenant
optional columns; converter-less enums.

## 6. Lane self-merge readiness (per lane)

Compile clean on the merged lane tree; lane record complete (scope closed with file:line, self-review
checklists, Program.cs hunks with line anchors, POCO deltas, contracts); `// lock:` annotations on changed
stores; no edits outside owned globs except declared hot-file keys; secret scan clean.

## 7. Comprehensive independent review → one fix batch

After merge #9 (all lanes), separate contexts prompted to refute, loading `round3-transaction-scope-review.md`
and `conductor-okf:code-styleguides`; findings consolidated in `round4-review-ledger.md`:

| Reviewer | Tier | Scope |
|---|---|---|
| R1 correctness + transaction scope (money, identity) | opus | L2, L3 incl. L1's W0 D1/F-M5 fix |
| R2 correctness + transaction scope (chains, platform, security) | opus | L4, L5, L1 (W0 design-bearing fixes, ordinal casts, webhooks, governance) |
| R3 `/security-review` | opus | money/auth/custody/user-input across all lanes, explicitly: L1 SRDB0001 rewrites inside authority fences, `ActionSigningScopes`, `Services/Auth`; chain fee caps and WTT signing context; submit-signed exact-byte verification; operator console BFF allowlist; governance; KYB gate; child credentials |
| R4 test quality (`flow-tests`) | sonnet | every authored/edited test: real flows, no tautologies, mutation check, mock debt, **no weakened exactly-once assertion** (diff of assertions in edited tests) |

Then **one fix batch**: ≤3 executors by domain in fresh worktrees (`r4/fix-<domain>`), same merge protocol;
money/auth fixes get an R3 spot-check before merge. Then `round4-closeout.md`, RUNBOOK/`plan.md`/`metadata.json`
updates, commit, and a merge hash offered to `oasis-sleek-38`.

## 8. Integrated sweep (once)

**Prerequisites, in order:**
- **P-1 environment:** podman is rootless and the Docker-API pipe is gone. Verify which compose provider works (`docker compose version`, `podman compose version`, `docker-compose`); `dev-up.ps1:119` and `e2e-stack.mjs:121` try `docker compose` first, so pin the working one (the e2e script honors its configured override). SurrealDB RocksDB containers need `--user 0:0` (dev, e2e and P2 containers). Confirm the podman machine is up and disk is free.
- **P0:** SurrealForge 1.3.0 restored from NuGet (D-1).
- **P1:** final schema regeneration for W2 POCO deltas + goldens/flowcharts; **re-run the unique-index audit and the old-row rule** for invariants W2 added.
- **P2 upgrade rehearsal (the real Railway risk):** throwaway 3.1.4 container (`--user 0:0`, RocksDB); from a `main` worktree apply `main`'s schema and L6's seeded rows; then `surrealforge up` the new schema without `--allow-destructive`; assert every audited UNIQUE index exists, the defensive `REMOVE INDEX` migration is a no-op, and every seeded old row stays writable through a typed write; reap.
- **P3:** `.\dev-up.ps1 -ResetDb`.
- **P4:** SDK `npm run build`.
- **P5:** Hardhat, Agave (`solana-test-validator`), AlgoKit LocalNet; Stripe CLI `listen`; Identomat config ids + tunnel (residual OD-8).

**Gates — ranked; one pass:**
1. `dotnet build azoa.sln`.
2. **Ranked first:** KYC/KYB (unit + integration + HTTP flows filtered to `Kyc|Kyb|HostedSession`), **Stripe** (fiat unit/integration/HTTP flows + gate 11), **exactly-once** (`Round4Gates` interleaving, duplicate/marker, allow-path fences, clawback crash fixtures, host tests).
3. Remaining unit tests (architecture gates included) → remaining integration tests on :8020 (protocol fixtures included) → remaining HTTP flows → SDK vitest + build → frontend `npm test`, `test:browser`, lint, typecheck, build, `npm audit` → E2E `node e2e/scripts/e2e-stack.mjs up` → schema goldens.
4. Stripe test-mode drill (L3 list). Identomat live drill (residual OD-8).
5. **Evidence-gated activation record:** each production pin (per-chain DEX/exchange, webhooks, child credentials, WTT) flips only on its named green gate; any pin left off becomes a `deferred.md` entry (external blocker) or an owner line under "Owner scope decisions".
6. Secret scan of the final diff.

**Failure loop:** every failure goes into `round4-sweep-ledger.md`, classified by root cause. Batch 1
(class-shaped) → batch 2 (unmasked) → batch 3 (interleaving/journeys); each batch fixes all of its classes in
worktrees under the merge protocol, money/auth fixes get a `/security-review` spot-check, then re-runs only the
affected gates plus gate 1; ranked gates re-run first. A fourth batch needs owner approval. **Stop rule:** if
batch 3 is not green on KYC, Stripe and exactly-once, stop and re-plan.

## 9. Merge-to-main sweep

Preconditions: sweep green, or every residual red has an owner line; closeout uses the closing vocabulary;
`oasis-sleek-38` merged or idle. `git switch main` → `git merge --no-ff feat/platform-stub-closeout` →
re-run gates 1–3 (if `main^{tree}` equals the swept tree, record the identity and re-run gates 1, 2 and the
goldens) → **READY TO PUSH** in RUNBOOK §2. The owner pushes `main`; Railway deploy and its secrets stay owner
steps.

## 10. Residual owner decisions (ranked by blast radius; defaults recommended)

**Evidence-gated activation:** the code lands in R4, the flag is on in Development/IntegrationTest/e2e, and
production flips only on a recorded green gate.

1. **OD-1 Delegated child credentials** (503 today). Default: evidence-gated enable (L4 builds the allowlist from the route inventory, child-token scope handling, issuing-tenant revoke, acting-tenant reads). Alt: keep disabled as an owner scope decision; delete tenant branches. Reversal: low (delete: high).
2. **OD-2 KYB scope.** Default: KYB `APPROVED` required for custodial accounts/custodial KYC, every tenant-on-behalf value or custody scope via `ITenantConsentGate`, and payout price schedules. Alt: fiat on-behalf only; all tenant API activity. Reversal: low-moderate.
3. **OD-3 DEX/exchange activation.** Default: declare the modules, evidence-gated production pins per chain. Alt: refusals only (labelled Not done). Reversal: config flip.
4. **OD-4 Tenant cash-out pricing (audit-2 H3).** Default: node-funded payouts at `min(tenant, node)`; above-node prices refused. Alt: node schedule only; operator approval; tenant-funded rail. Reversal: low.
5. **OD-5 Remaining unrecorded deferrals:** #1 Veriff/generic live → `deferred.md` VERIFF-LIVE unless test credentials arrive; #2 Ethereum/Solana custody parity → owner scope decision (needs a reviewed settlement contract); #3 WTT six-route and #4 Tinyman/Jupiter fixtures → do (L6); #5 recipient-stage Solana costs, #6 legacy Wormhole + `wtt` resolver, #7 operator fee control, #8 exchange advertisement (per OD-3), #9 WTT stage-key readiness (degrade) → do. (#10 settled, §0.2.)
6. **OD-6 IDENTOMAT-RECOVERY.** Default: close via orphan supersede (1 per subject per 24 h) + `/delete/` + inert unknown callbacks; replace the entry with a hardening note. Alt: keep operator-only + deferral.
7. **OD-7 First-party UI.** Default: minimal verify-identity / add-funds / cash-out pages + journeys. Alt: SDK-only.
8. **OD-8 Identomat workflows + live drill (owner action).** Default: owner creates the two configs (OPT §3 steps 1–5) and authorizes ~8 live sessions before P5; otherwise KYC/KYB end as *Built (vendor-doc fakes)*.

## 11. Deferral ledger

| Item | Resolution | Closure check |
|---|---|---|
| Clawback rung 0 / uncapped finish / `Failed` key | L3 (A-2) | crash + definitive-failure + concurrent fixtures; dispute drill |
| Stale nonce index | L1 W1 defensive migration; not deploy-blocking (§1) | P2 |
| Ganache EIP-1898 | L6 Hardhat | protocol fixtures |
| DEX activation; exchange advertisement | L5/L3 (OD-3) | gate 5 record |
| Webhook delivery disabled; retry only from DeadLettered | L1 (evidence-gated) | drain flow; gate 5 |
| Delegated child credentials | L4 (OD-1) | tenant-branch flows + route gate |
| Governance gates | L1 (§0.2) | race fixtures; post-sweep verdict line |
| `AwaitingSignature` stuck rows | L5 (D-3) | submit-signed + stuck-row fixtures |
| `AwaitingReconciliation` false success; ordinal casts | L4 + L7; L1 W0 | scanned-count flow; architecture rule |
| Converter-less enums | L1 W0 | architecture test |
| Tinyman/Jupiter/WTT six-route fixtures | L6 (OD-5) | green, else pin off → `deferred.md`/owner line |
| Recipient-stage costs; legacy Wormhole + resolver; absence proof; stage-key readiness; External-wallet proof | L5 / L5 / L5 / L5 / L4 | fixtures |
| Operator console coverage + fee control; direct hold UI; durable recipient readiness; buy-back top-up; retired exchange rows | L7 / L3 | slices + flows |
| RPC URL leak; D9; D6; pruning (`swap_state`, Uniswap lockfile, `RefundNodeHandler`) | L5 / L6 | source scan; grep empty |
| SDK DexAdapter retirement | L7 | SDK build; grep empty |
| D1 / D2 / D8 / D5 / D7 / D3 / D4 | L1 W0 / L3 / L3 / L2 / L2 / 1.3.0 / OD-8 drill | Stripe drill; dev KYB configured; live drill |
| Transaction-scope, cross-tenant, route gates; allow-path; interleaving; mock debt | L6, L4 | gates green |
| Lane A/C/N LOWs | L5 | lane self-review + R2 |
| SurrealForge typed transaction builder, `KeyOnly`, identifier-template builder, ledger re-claim | SurrealForge follow-ups (A-1, A-2) | logged in the SurrealForge repo |
| IDENTOMAT-WORKFLOWS | OWNER-ACTION, owner Jade Zaher; internal gaps removed from the entry | live drill evidence |
| IDENTOMAT-RECOVERY | removed (OD-6) | orphan/late-delete/unknown-callback fixtures |
| VERIFF-LIVE (new) | `deferred.md` unless credentials | live Veriff session |
| Ethereum/Solana custody parity | owner scope decision (new `deferred.md` section) | revisit when a reviewed settlement contract exists |
| Release steps (Railway secrets, deployed Identomat callback, Stripe deployed webhook) | owner, outside the sweep | RUNBOOK release checklist |

## 12. Agent count and cost (estimate)

| Phase | Agents | Tier |
|---|---|---|
| W0 | 1 lead + 2 helpers | opus + 2 sonnet |
| W1 | L1 lead (continues) + 1 re-sweep reviewer | opus + opus |
| W2 | 7 lane leads + 2 helpers (L3, L5); peak 9 concurrent | 4 opus (L2–L5) + L1 opus + L6/L7 sonnet (L6 escalates design to opus) + 2 sonnet |
| W3 | 4 reviewers + ≤3 fixers | 3 opus + 1 sonnet; fixers sonnet/opus by domain |
| Sweep | inline orchestrator + ≤3 fixers per batch × ≤3 batches | mostly sonnet; opus on money/auth |

About **22–28 agent runs** (the 17-package draft needed ~50 with per-lane reviews and two cycles). Cumulative
cost **~45–80M tokens, ~70% opus** (W0 ~8–12M, W2 ~25–40M, review + fix ~8–14M, sweep batches ~4–12M).
Wall-clock **~5–7 days**: W0 1–1.5 d, W1 ½ d, W2 2–3 d, review + fix 1 d, sweep 1–3 d. The largest
uncertainty is W0's test-project convergence and sweep batch 1.

## 13. Risks and re-plan triggers

| Risk | Mitigation |
|---|---|
| W0 is one lane on the critical path (247 errors + SRDB0001 + ~427 test files) | in-worktree helpers; opus lead on design-bearing errors; owning lanes and R2 re-verify |
| Merge conflicts on `Program.cs`, POCOs, config | §3.4 rule, ownership maps, strict order, compile clean before every merge, host tests |
| Lane self-review misses what an independent reviewer would catch | one comprehensive independent review with four lenses before any fix batch; R3 spot-checks money fixes |
| 1.3.0 semantic change (enum casing, null → NONE) breaks a raw or guard path | W1 re-sweep classes; L6 round-trip and guard-widening fixtures; 1.3.1 via the owner |
| Chain SDK guesses compile but are wire-wrong | protocol fixtures decide; production pins stay off per chain |
| Old rows unwritable after new non-optional fields | W1 old-row rule; P2 rehearsal |
| Rootless podman / compose provider breaks dev-up or E2E | P-1 before anything runs |
| KYB gate breaks tenant fixtures | day-1 KYB-approved tenant builder (L1 W0) |
| Cost overrun | sonnet for mechanical lanes; helpers only where disjoint; single-sweep discipline |

**Re-plan if:** W0 is not compile-clean after ~1.5 days; W1 finds a money-path uniqueness invariant needing
redesign; a lane cannot merge compile-clean after two attempts; the re-sweep finds a new fail-closed class with
>10 sites; R1–R4 return a CRITICAL that the single fix batch cannot close; the owner picks OD-1 keep-disabled,
OD-3 refusals-only or OD-7 SDK-only (lanes shrink; rows move to "Owner scope decisions"); sweep batch 3 is red
on KYC, Stripe or exactly-once.
