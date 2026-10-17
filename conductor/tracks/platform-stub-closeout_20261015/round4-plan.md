---
type: implementation-plan
round: 4
status: final
---

# Round 4 reconciled plan — platform-stub-closeout

Base: `feat/platform-stub-closeout` @ `8f89396` (tree = `0522fdf` + docs). Inputs:
`round4-assessment-adversarial.md` (`ADV F-xx`), `round4-assessment-optimism.md` (`OPT Fx`/`OPT §n`),
`round3-join-ledger.md` (`LEDGER`), the 247-error list (`ERR:<line>`), the adversarial plan critiques
(`CRIT`). Static plan; nothing built or run.

**Revision history.** R1 draft (17 packages) → critic REJECT-with-changes → R2 (7 worktree lanes, owner
D-1..D-4) → critic re-review ACCEPT-WITH-CHANGES → **R3 final** (owner D-5..D-8 and recorded defaults; merge
check script, conflict precedence, per-lane inventories, L4→L5 order fix; per-lane + cross-cutting review on
the full fail-closed class list with bounded re-checks; opus helpers inside big lanes; KYB builder pinned to an
`APPROVED` row; local-artifact restore for compile only).

**Closing vocabulary:** *Done* = executed evidence. *Built* = compiled, unit-covered. *Authored* = not
compiled. "Approved at source review" is never written as "closed".

## 0. Decisions

### 0.1 Owner decisions (settled 2026-10-17)

| # | Decision | Plan consequence |
|---|---|---|
| D-1 | **SurrealForge 1.3.0 is final** (commit `b5a7d52`; integration 47/47 on 3.2.4/3.1.4 × memory/RocksDB; unit 1202); the owner publishes it to NuGet. Library bugs found later → 1.3.1, owner publishes | W0 step 1 bumps `AZOA.WebAPI.csproj:109,110,113`, `tests/AZOA.WebAPI.IntegrationTests/AZOA.WebAPI.IntegrationTests.csproj:30`, `tests/AZOA.WebAPI.Tests/AZOA.WebAPI.Tests.csproj:45` to 1.3.0. Until it is on nuget.org, L1 may restore from `C:\Users\atooz\Programming\Projects\surrealforge\artifacts\1.3.0` through a **user-level** NuGet source, for compile checks only. Never commit a `nuget.config` pointing at a local path. Once published (same commit), remove the source, clear the cached `surrealforge.*\1.3.0`, restore from nuget.org |
| D-2 | **Compile-only builds per lane are allowed** (`dotnet build`, `tsc`, SDK build). No tests, lint gates, schema goldens or E2E before the sweep | §3.2 |
| D-3 | **`AwaitingSignature`: submit-signed route**: accept the client-signed transaction, verify its exact bytes against the saved plan, broadcast, reconcile to terminal; the idempotency key leaves `InProgress`; stuck rows get an operator-visible outcome | L5 |
| D-4 | **Execution model**: 5–7 executor lanes, each in its own worktree with self-review and fix loops, merging its own branch; then one comprehensive independent review, one fix batch, then the sweep (one pass + ≤3 batches, stop rule, KYC/Stripe/exactly-once gates first) | §3, §7, §8 |
| D-5 | **Delegated child credentials**: build the missing pieces; enable in test configs; production on only after the route-authorization gate and the tenant-branch flows pass the sweep | L4 |
| D-6 | **KYB gates** custodial accounts / custodial KYC, tenant-on-behalf value scopes (at `ITenantConsentGate`), and payout price schedules | L2 |
| D-7 | **Exchange/DEX**: declare the modules; production on per chain only after that chain's fixture passes; anything still off at the end → `deferred.md` | L5 (+L3 exchange semantics) |
| D-8 | **Tenant-priced payouts are funded from the tenant's own balance, not the node's** (min-price default rejected) | L3: tenant-funded payout source, balance-coverage check before any send, exactly-once accounting of the tenant debit |
| D-8a | Tenant float is credited with Stripe `balance_transaction.net` — tenants bear their own processing/dispute fees (2026-10-17) | L3 D-8, merged `9f484c6b` |
| D-8b | Tenant-funded collects return units to a tenant-attributed reserve partition `tenant-payout:{tenant}` (2026-10-17) | L3 D-8, merged `9f484c6b` |
| D-8c | Cross-scope arbitrage closed: `on_ramp_lot.pricing_scope_key` caps a node-funded exit at the lot's sold price (2026-10-17) | L3 D-8, merged `9f484c6b` |
| D-9 | **Schema moves by generation reset**: fresh DB (`dev-up -ResetDb`; Railway SurrealDB volume reset before the next deploy); no baselines/forward migrations for round-4 changes; deploy path `surrealforge up --no-reconcile`; P2 becomes a sandbox data-compatibility rehearsal (2026-10-17) | W1/L1p/L6; Railway reset is an owner step at deploy time |

**Defaults recorded (owner accepted):** IDENTOMAT-RECOVERY closed via retry + `/external-api/delete/`;
Veriff/generic live → `deferred.md` unless test credentials arrive; Ethereum/Solana value-custody parity stays
design-only with an owner line in `deferred.md`; minimal first-party UI; the live Identomat drill needs the
owner's configs (L2 raises it at lane start). The rest of the unrecorded-deferral set (WTT six-route,
Tinyman/Jupiter fixtures, recipient-stage Solana costs, legacy Wormhole + `wtt` resolver, operator fee
control, exchange advertisement, WTT stage-key readiness as *degrade*) is done in R4.

### 0.2 Previously settled — not re-asked

- **Governance gates:** "require independent verdict before lifting those gates" (`plan.md:59`). The lift in `3d08ace` (`NotEnabledActionNodeTypes.cs:8` blocks only `CredentialRevoke`) is reverted; gates lift only by a verdict recorded after green race fixtures (L1).
- **Signature-verified External wallets** are a round-4 follow-up (RUNBOOK §3) → L4.
- SDK `DexAdapter`/`JupiterAdapter`/`TinymanAdapter`: retire only (RUNBOOK §3) → L7.
- WTT under a tenant credential: own custodial wallets only, relayer fee `"0"` (RUNBOOK §3).

### 0.3 Stated assumptions (departures from RUNBOOK §3 "SurrealForge gaps are fixed in SurrealForge")

| Assumption | Why AZOA-side | Reversal cost |
|---|---|---|
| **A-1** SRDB0001 sites migrate to an AZOA `Core/Surreal/SurrealIdentifierTemplate.cs` (token allowlist `^[a-z0-9_]+$`, one localized suppression), matching `SurrealGeneratedStatementScope` | Analyzer-correct today; 1.3.0 is final | Low: swap to a SurrealForge identifier-template builder later; ~36 mechanical call sites. Logged as a SurrealForge follow-up |
| **A-2** Clawback `Failed` key: attempt-suffixed claim key on the reversal receipt (`ReversalCompensationService.cs:826-831`) | Ledger re-claim semantics would change every SurrealForge consumer | Low: adopt a ledger re-claim API if added; one service. SurrealForge follow-up |
| **A-3** Frontend typecheck runs in the sweep | RUNBOOK §7 step 7.6 and root `AGENTS.md` supersede the older no-frontend-typecheck memory | none |
| **A-4** Payout-account setup is first-party only (ADV F-H8); dev KYC defaults to Identomat with fakes; gates never accept MANUAL; AML monitor polling on wherever an Identomat config id is set; warning baseline 25 | Security and dev-default fixes | low |

## 1. Ground truth that supersedes the assessments

| Fact | Evidence | Consequence |
|---|---|---|
| 1.3.0 contains public anchored `IsUniqueViolation(string?)`, `EnsureAllOk` culprit blame, reserved-word `user` ids, buffered-flush fix, enum casing, captured-null → NONE | D-1; RUNBOOK §2 | OPT F5/F6, ADV F-C5/F-C6/F-H7, D3 close at the bump. All 81 classifier sites pass `.Message` (grep 73/6/2) → compile unchanged |
| 1.3.0 changes typed-read semantics: enums bind as written; `== capturedNull` matches absent rows | D-1 | Re-sweep and review classes: raw lowercase-enum / explicit-NULL writes; owner/tenant guard widening on optional columns |
| `;` inside a `SurrealQuery.Of` literal still throws | SurrealForge `SurrealQuery.cs:105` | ADV F-H13 stays AZOA (`SurrealFungibleCreationStore.cs:273` → parameter) |
| No `.surql` on `main` defines `ethereum_preparation` | `Persistence/SurrealDb/{Generated,Migrations}` grep | The `REMOVE INDEX IF EXISTS` migration is defensive only; the real Railway risk is new fields/indexes against old rows (§8 P2). RUNBOOK DEPLOY-BLOCKING line downgraded at closeout |
| `Program.cs` order is load-bearing | `:1680-1681` registers `IRealValueKycGate`; `:1757` `AddFiatOnRamp`; `:1771` `AddReversalRiskValueGate` wraps the last-registered gate into `RiskHoldValueGate` (`ReversalRegistration.cs:130-162`) | §3.4 rule + merge check + host test |
| Ordinal enum casts | `SurrealActionRunStore.cs:56,59,92,118` | Removed in W0 before any `[Inside]`/enum reorder |
| Converter-less enums | `NodeFeeSettlement.cs:28,38,112-122`, `NodeFeeAtomicGroup.cs:24,74` (`[Inside]` strings, no `JsonStringEnumConverter`; 25 other models have it) | Converters + architecture test in W0 |
| Webhook retry only from `DeadLettered` | `Surreal{Action,Consent}WebhookDeliveryStore.Management.cs:56,82-83` | L1 |
| Parked-runs UI reports success on zero rows | `frontend/src/app/(dashboard)/admin/parked-runs/page.tsx:180`; sweep returns only results (`ActionController.cs:325-336`) | L4 + L7 |
| Compose provider probing prefers Docker | `dev-up.ps1:119`; `frontend/e2e/scripts/e2e-stack.mjs:121` | §8 P-1 |
| Ganache 7.9.2 rejects EIP-1898; Hardhat 2.26.3 passes | OPT F7 | L6 |
| Fungible creation unregistered; `Program.cs:1163` names deleted `Ed25519SignatureVerifier` | grep | L1 W0 |

## 2. Reconciliation (condensed)

**Agreements adopted:** nothing runtime is claimable; ~80% of the 247 errors are mechanical once 1.3.0 is
in; D1/D2/D8 break Stripe at three hops and D2 is a class (ADV F-C4 + OPT F9); clawback MEDIUMs are defects
now; DEX/exchange non-activation and carry-forward lists are disguised deferrals; allow-path real-DB fixtures
are the only fail-closed detector; DexAdapter retirement is small.

| # | Disagreement | ADV | OPT | Resolution |
|---|---|---|---|---|
| 1 | Sequencing | WP0 → WP1 → parallel → sweep | fix batch → 6–8 builds → sweep → 3–4 batches | D-2 + D-4 |
| 2 | Enum casing / captured null | SurrealForge 1.2.2 or per-site | not found | In 1.3.0; AZOA audits the semantic change |
| 3 | Collateral slots | not found | F5 | Fixed in 1.3.0 |
| 4 | IDENTOMAT-RECOVERY | meets the bar | not needed (orphans inert) | Closed in R4 (recorded default); F-M1 4xx/429 release with it |
| 5 | IDENTOMAT-WORKFLOWS | meets bar; name owner | owner action, ~15 min | OWNER-ACTION, owner Jade Zaher; internal gaps moved to L2 |
| 6 | RETURN in BEGIN…COMMIT | needs probe; 15 sites | settled; 12 sites before COMMIT | Settled; gate rule (L6); re-sweep reconciles the count |
| 7 | `NativeAccounts.cs:81` | SDK mismatch | real bug | Real bug (`ERR:96-97`) |
| 8 | Design-bearing compile errors | route to lanes | route to lanes | Fixed in W0 (the tree must compile before lanes branch); re-verified by owning lane self-review and RV-L1 (§7) |
| 9 | Tenant cash-out pricing (audit-2 H3) | — | — | D-8: tenant-priced payouts debit the tenant's own balance |

## 3. Execution model

### 3.1 Lanes, worktrees, helpers

Seven lanes (§4). Each: `git worktree add ..\oasis-sleek-r4-<lane> -b r4/<lane> feat/platform-stub-closeout`,
its own `obj/`/`bin/`. One lead per lane; big lanes get **opus helpers on disjoint subtrees under the lead**
(§4 lists each helper's subtree), not new lanes. Lanes have full local git permissions within §3.3.

### 3.2 Lane loop (inside the worktree)

1. Implement the lane scope plus its W1 re-sweep findings.
2. Compile-check (D-2): `dotnet build azoa.sln -c Debug` (0 errors, warnings ≤ 25); SDK `npm run build` and frontend `npm run typecheck` when touched.
3. **Self-review and fix, ≤2 iterations**, checklists recorded in the lane record: `round3-transaction-scope-review.md` on every changed store/fence; root `AGENTS.md` conventions + changed-file pruning gate; security checklist on every changed route/service (authz + signing scope, acting-tenant scope, input validation, secrets/PII, idempotency/replay, no bare ids into record links); the fail-closed class list (§7); flow-tests rules for authored tests (exactly-once assertions never weakened).
4. Append the lane's route-authorization and cross-tenant inventory files (§3.5).
5. Write the lane record `conductor/tracks/platform-stub-closeout_20261015/round4-lane-<lane>.md` (`type: lane-checkpoint`): scope closed with file:line; a `## Hunks` section with one fenced block per `Program.cs`/config hunk (info string `hunk path=<file>`); POCO deltas; published contracts; compile result; checklists. Report **"ready to merge"** to the orchestrator.

### 3.3 Merge protocol

**Order** (strict): W0 `r4/core` → W1 `r4/schema` → W2 `r4/platform` (L1) → `r4/identity` (L2) →
`r4/money` (L3) → `r4/security` (L4) → `r4/chains` (L5) → `r4/verification` (L6) → `r4/frontend` (L7) →
fix-batch branches. The orchestrator may swap two adjacent lanes only if the earlier is blocked >½ day and
they share no hot-file region; recorded in both lane records.

**Mechanics:** the orchestrator signals the next ready lane in order; that lane performs its own merge:
1. In its worktree: `git merge feat/platform-stub-closeout` (merge, never rebase). Resolve conflicts: **a conflict hunk in a file owned by an already-merged lane keeps that lane's side**; hot files are edit-only — re-apply your own edit onto the merged text, never restore a stale whole-file copy, never reformat.
2. Compile clean (D-2) on the merged lane tree; run `node scripts/round4/merge-check.mjs`.
3. In the main checkout (clean): `git merge --no-ff r4/<lane>`; `git diff --quiet r4/<lane> HEAD`; run the merge check again on HEAD.
4. Immediately record the merge hash in the lane record (`merged: <hash>` in frontmatter) and commit that one change on `feat/platform-stub-closeout`, so the main checkout is never left dirty. Report "merged".

Never push, never force, never rebase or amend a merged commit, never delete another lane's branch. Secret
scan (every `.env` value vs the staged diff) before each merge. Worktrees are removed after the comprehensive
review.

**`scripts/round4/merge-check.mjs`** (node, no dependencies; written by L1 in W0, ships in merge #1; static,
no tests). Fails with one message per violation:
- `Program.cs` has exactly one `IRealValueKycGate` registration;
- textual order: that registration → `AddFiatOnRamp` → `AddFiatReversalPipeline` → `AddReversalRiskValueGate`;
- `IWormholeAdapter` is still registered;
- every fenced `hunk path=<file>` block in every lane record with a `merged:` hash is still present verbatim (whitespace-normalized) in its file;
- no tracked `nuget.config` contains a local filesystem path.

### 3.4 `Program.cs` ordering rule (L1 writes it into root `AGENTS.md` §Program.cs registration order in W0)

1. A registration that replaces or decorates an existing service is edited **in place**, never appended.
2. Order-dependent sequence, top to bottom: `IRealValueKycGate` (`:1680-1681`) → `AddFiatOnRamp` (`:1757`) → `AddOnRampValueRateSource` → `AddFiatReversalPipeline` → `AddReversalRiskValueGate` (`:1771`); the fiat block's internal order is asserted by its own guards (`:1755`). L2 swaps the KYC-gate implementation at `:1680-1681`; appending it after `:1771` would replace `RiskHoldValueGate` and bypass the reversal risk hold.
3. `IWormholeAdapter` (`:1639`) stays registered (`WormholeProtocol` needs it); L5 deletes only the fabricator method (`WormholeAdapter.cs:549-555`).
4. L4 edits `:216-220` (child JWT) and `:641-652` (`DappRoleAssignment`) in place.
5. New registrations go inside their domain block, never at the end of the file.
6. Host tests (L1, W0; run in the sweep): `ValidateOnBuild` + `ValidateScopes` on the real Development host; `IRealValueKycGate` resolves to `RiskHoldValueGate` wrapping the merged KYC gate; `IWormholeAdapter` resolves; fungible creation service/store/worker resolve.

### 3.5 Shared files

- `appsettings*.json`, `docker-compose*.yml`: key ownership — `Kyc:*`/`Kyb:*` L2; `Fiat:*`/`Stripe*`/`Exchange:*` L3; child-credential flag L4; `Dex:*`/`Wtt*` L5; `Webhooks:*` L1. Edit only your keys.
- **Inventories (per lane, no shared file):** `tests/AZOA.WebAPI.Tests/Architecture/Inventories/RouteAuthorization/<lane>.json` (`route`, `controller`, `action`, `class` = `value-moving`\|`non-value`, `scope`, `enforcement`) and `…/Inventories/CrossTenant/<lane>.json` (`store`, `method`, `tenantCheck`). Each lane classifies only routes/stores it owns; L4 writes the two gate tests that load every file (an unclassified POST/PUT/DELETE or owned-resource method fails).
- `tests/AZOA.WebAPI.IntegrationTests/Builders/TestDataBuilders.cs`: L1 makes it `partial` in W0 and adds the **KYB-approved tenant builder, which writes an `APPROVED` `kyb_verification` row through the real KYB store from day 1**; lanes add `TestDataBuilders.<Lane>.cs`.
- Harness infra (`tests/AZOA.WebAPI.IntegrationTests/{Http/Infrastructure,Factories}/**`, `frontend/src/test/msw/**`, `frontend/e2e/support/**`): L6 owns; others add new files only.
- SDK (`sdk/azoa-wallet/**`, incl. `client.ts`, indexes, `api-version.ts`) and `frontend/**`: **L7 only** in W2; backend lanes publish contracts (§4.9) in their lane record on day 1.
- `Helpers/ClaimsPrincipalExtensions.cs` (L4 lead) and `Helpers/ActionSigningScopes.cs` (L4 helper B): hot files for the other L4 agent.
- **Unlisted files:** any production file matched by no W2 glob belongs to L1 (`r4/platform`) for re-sweep findings.
- Directory `AGENTS.md`: append a section in directories you own; root `AGENTS.md` only by L1 (W0) and L7 (`AGENTS.md:86`).

## 4. Lanes

| Lane | Lead | Helpers (disjoint subtrees) | Branch / merge # |
|---|---|---|---|
| L1 Core + platform | opus | W0: opus A `Providers/Blockchain/**`; opus B fiat subtree | `r4/core` #1, `r4/schema` #2, `r4/platform` #3 |
| L2 Identity | opus | — | `r4/identity` #4 |
| L3 Money | opus | opus: Hold/exchange | `r4/money` #5 |
| L4 Security | opus | opus A: gates + child credentials + deployments; opus B: resource IDOR | `r4/security` #6 |
| L5 Chains | opus | opus: WTT | `r4/chains` #7 |
| L6 Verification | sonnet | opus: interleaving fixtures + WTT six-route | `r4/verification` #8 |
| L7 Frontend/SDK | sonnet | — | `r4/frontend` #9 |

### L1 — Core and platform

**W0 `r4/core`** (only lane in W0; the whole tree is owned; merge gate: `azoa.sln` 0 errors, warnings ≤ 25, SDK + frontend compile clean):
- **Lead:** bump to 1.3.0 (D-1); prelude before helpers start — rename `Helpers/Encoding.cs` class → `ByteEncoding` (CS0104 ×50 + 3 `Encoding.UTF8`; "never shadow BCL names" into `Helpers/AGENTS.md`) and `using AlgorandEncoder = Algorand.Utils.Encoder;` (five files, ×17). Then: `.Build()` ×17 (`SurrealEthereumPreparationStore.cs:94`; `SurrealHostedProviderAttemptStore.cs:35,77,89,129,137,146`; `SurrealUserStore.cs:89,153,289,302,314`; `SurrealWalletAuthChallengeStore.cs:66,113`; `WalletAuthIdentityMigrationService.cs:48,123,141`); `using SurrealForge.Client` (`HoldReleaseWorkflowFence.cs:28-31`, `NftWorkflowCostAuthorityFence.cs:28-29`, `ConsumerGovernanceService.cs:77-121`); `SurrealHoldCommandStore.Mandate.cs:34`; `RetryOnConflictAsync<T>` (`SurrealWebhookRegistrationStore.Management.cs:117`, `.Withdrawal.cs:48`); `LeasedWebhookSender.cs:38`; `ActionManager.cs:1578`; vocabulary drift `ActionSigningScopes.cs:38`, `DeploymentArtifactCompiler.cs:54`, `NftLifecycleOperationProjection.cs:32` (record the `OperationStatus` mapping for L5); SRDB0001 per A-1 (~36 sites; largest `SurrealBridgeStore.cs`×5, `SurrealActionWorkflowAdmissionStore.cs`×4, `SurrealActionLocalMutationStore.Witnesses.cs`×4, `DeploymentAuthorityFence.cs`×4, `WalletAuthIdentityMigrationService.cs`×3; no per-site pragmas); IDE0005; ordinal casts → name mapping (`SurrealActionRunStore.cs:56,59,92,118`); `JsonStringEnumConverter` on `NodeFeeSettlement`/`NodeFeeAtomicGroup` + `Architecture/EnumStorageConverterTests.cs`; F-H13 (`SurrealFungibleCreationStore.cs:273`); DI (`Program.cs:1163` → `WalletSignatureVerifier`; fungible service/store/worker, ADV F-C7); §3.4 rule + host tests; `scripts/round4/merge-check.mjs`; `TestDataBuilders` partial + KYB-approved builder; SDK/frontend compile (`FiatPayoutView`, stale swap re-exports `src/api/index.ts:112-114`); then both test projects (helpers join test subtrees once theirs compile): adapt to production signatures (`WalletManagerTests.cs` removed `WalletType`; `HostedSessionFlowTests.cs:696`), never bend production, never weaken an exactly-once assertion, deletions only when the premise is gone (listed in the lane record).
- **Helper A (opus) `Providers/Blockchain/**`:** Solana `ValidateAddressFormat` never written (17 sites, `ERR:81-119`) → `Solana/SolanaAddress.cs` over `TryParsePublicKey` (`SolanaProvider.cs:1035`), on-curve vs PDA per site in `Solana/AGENTS.md`; Jupiter fee caps CS0165 (`SolanaProvider.DexQuote.cs:45-117`, re-derived from the reviewed contract); `NativeArtifact.cs:30-35`; `SigningContext` null ×6 (`ERR:46,48,62,63,114,117`) — the context the WTT contract binds; Algorand `ApplicationId` ×4 / `AssetCloseTo` / `long→int` (`ERR:9,29,32,45,47,49`); Solnet init-only `Data` ×4 + `CloseAccount` (`ERR:64,93,113,116,127`); swapped arguments `NativeAccounts.cs:81`; CS1628 ×3; Nethereum `ABIType` ×5 cascade.
- **Helper B (opus) fiat subtree** (`Providers/Fiat/**`, `Services/Fiat/OffRamp/{PayoutAccountService,OffRampService}.cs`, `Providers/Stores/Surreal/SurrealHostedSessionStore.cs`, `IHostedSessionStore.cs`): D1 (`FiatRailProvider.cs:610-617`, `PayoutAccountService.cs:179-246`: claim sets `CreationToken`, `TryBeginCreationAsync` before the Stripe create, attach with the token, persist the connected-account id); F-M5 (`SurrealHostedSessionStore.cs:231-233`); `OffRampService.cs:452` `requestedQuantity` (intent from `git log -S`, decision in `Services/Fiat/OffRamp/AGENTS.md`).

**W1 `r4/schema`** (from the clean merged tree; never concurrent with W0): regenerate all ~130 tables + flowcharts/goldens (ADV F-C2); `[Column(Name = "secret")]` on `WebhookRegistration.cs:17`; defensive `REMOVE INDEX IF EXISTS ethereum_preparation_nonce ON TABLE ethereum_preparation;` (dropped if P2 shows it errors on an absent table); **unique-index audit** in `Persistence/SurrealDb/AGENTS.md` (every single-winner invariant → its generated `UNIQUE` index: nonce `(protocol, sender, nonce, nonce_slot)`, KYC person key per tenant D23, hosted admission, provider attempt, idempotency key, fiat event id, on-ramp lot per intent, payout per request, clawback receipt, WTT body/VAA, swap operation, DEX quote, NFT lifecycle, fungible creation, hold command, consent grant, wallet address; missing → POCO fix now); **old-row rule** (a new field on a table that exists on `main` is `option<>` or `[Default]`, else a reviewed backfill migration); `[Inside]` lists mirror domain enums; POCO-table map (§4.8). Owned: `Persistence/SurrealDb/**`, golden/flowchart dirs. Merge gate: POCO table count = generated file count; no hand-edited `.surql`.

**W2 `r4/platform`:** webhooks (ADV F-H10): operator retry from any undelivered state (`DeadLettered`, `Pending` past its backoff budget, expired lease) with a version CAS (`.Management.cs:56,82-83`); `Webhooks:Enabled` on in Development/IntegrationTest/e2e (`docker-compose.e2e.yml:111`), production only after the drain flow and delivery fixtures are green; outbox-drain HTTP flow. Governance (§0.2): re-block GovernanceProposalCreate/Vote/Tally (`NotEnabledActionNodeTypes.cs:8`); finish `round2-governance-verification-migration.md`; real-DB race fixtures (`SurrealGovernanceStore.Finalization.cs:47`); document the verdict-gated lift. Owned: `Services/Webhooks/**`; `Providers/Stores/Surreal/{SurrealActionWebhookDeliveryStore*,SurrealConsentWebhookDeliveryStore*,SurrealWebhookRegistrationStore*,SurrealGovernanceStore*}.cs`; `Controllers/{WebhookEvent,WebhookEventOperator,ConsentWebhookEvent,ConsentWebhookEventOperator,WebhookRegistration,WebhookRegistrationOperator,NodeGovernance}Controller.cs`; `Services/Actions/NotEnabledActionNodeTypes.cs`; `Services/Governance/**` except `NodeFeeSettlement*`; `Managers/NodeGovernanceManager.cs`; unlisted files (§3.5).

### L2 — Identity: KYC, KYB, Identomat, custody gate

- Scope: ADV F-C3 (`KycController.cs:34-117`; `KycManager.cs:115-118` uncalled) → `POST /api/kyc/session` (FirstPartyLogin); F-H1 (`KycCallbackController.cs:62-91` always 202; `HostedSessionFlow.cs:337-363`; `HostedIdentityRecoveryWorker.cs:22-32`; operator 404 `HostedSessionReconciliationController.cs:110-118`; Identomat is absent from `HostedIdentityRecovery.Resolve()` (`HostedIdentityRecovery.cs:16-21`) and creates no `HostedProviderAttempt`, and `ListReconciliationAsync` selects only `Claimed` (`SurrealHostedSessionStore.cs:151-153`) so attached `AWAITING_OUTCOME`/`RESUMING` sessions are invisible; MANUAL_CHECK `IdentomatKycProviderService.cs:376-450`) → user + operator refresh re-fetching `/result/` and a backoff poll worker within `IdentomatRateBudget`; F-M1 4xx/429 release, timeouts/5xx stay quarantined (`IdentomatHttpTransport.cs:100-146`, `HostedSessionFlow.cs:162-173`); F-M12 merge `Services/Bridge/RealValueKycGate.cs` into `Services/Kyc/KycGateService.cs`, registration swapped in place at `Program.cs:1680-1681`; F-M3/F-M4/D5/D7 (`HostedSessionReconciliationController.cs:46,89`, `KycSettings.cs:121`, `docker-compose.dev.yml:141,145,154`); **D-6** KYB enforcement at `ITenantConsentGate`, custodial access and payout price schedules (shared contract with L3), including `KeyCustodyService` call sites; IDENTOMAT-RECOVERY closed (orphan supersede, 1 per subject per 24 h; `/external-api/delete/` for late responses; unknown-session callbacks inert); `HostedKycCreationFence.cs:25-27` IF-guard; `KycController.cs:74` tenant scope. Raises the Identomat configs request to the owner at lane start.
- Owned: `Controllers/{Kyc,KycCallback,HostedSessionReconciliation,HostedIdentityCallback,IdentomatKybCallback,TenantBusinessVerification,TenantKycConfiguration,AmlReview}Controller.cs`; `Managers/{KycManager,KycControlPlaneManager,TenantCustodialAccountManager}.cs`; `Services/{Kyc,Kyb,HostedSessions}/**`; `Services/Bridge/RealValueKycGate.cs`; `Services/Consent/{TenantConsentGate,TenantCustodialAccess}.cs`; `Services/Custody/KeyCustodyService.cs`; `Providers/{Kyc,Kyb}/**`; `Helpers/HostedKycCreationFence.cs`; `Settings/Kyc*.cs`; `Providers/Stores/Surreal/{SurrealKyc*,SurrealKyb*,SurrealHostedSession*,SurrealHostedProviderAttempt*,SurrealPersonOperationClaim*}.cs`; L2 POCOs; KYC/KYB/hosted tests and `Http/{IdentityVerification,BusinessVerification,HostedSessionReconciliation,Kyc*}FlowTests.cs`.
- Acceptance: real-DB allow path begin→callback→result→gate open; abandon/reject deny; orphan + late delete; unknown-session callback inert; KYB allow/deny on all three D-6 capabilities; `KycEvidenceFence` empty-array + multi-guard. Sweep proves: flows on vendor-doc fakes; with owner configs, the live drill (approve/abandon/reject/replay KYC, approve/reject KYB, one lost-response, D4 standard mode).

### L3 — Money: Stripe, fiat, reversal, tenant-funded payouts, Hold/exchange

- **Lead — Stripe rail:** D2 `StripeWebhookInbox.cs:467-468` + masked class sites `OnRampSettlementHandler.cs:436,545-547`, `OffRampService.cs:335-337` → `FiatPayoutLedger.cs:112`, `FiatReversalEventHandler.cs:239-242`; D8 `FiatRailProvider.cs:569-595` (persist `expires_at` at first claim); F-M2 signing-secret list (`StripeWebhookController.cs:69-94`); F-H8 tenant scope (`FiatPayoutController.cs:105-116` first-party setup; intents `FiatRailProvider.cs:636-660,798-799`; admission key `FiatCheckoutController.cs:281-282`; payout history/replay); LOWs `FiatEventRecovery.cs:24-37`, `OnRampRecovery.cs:136-146`; re-verify L1's D1 fix with fixtures.
- **Lead — D-8 tenant-funded payouts** (closes audit-2 H3 at `OffRampService.cs:865-866`: node funds never pay a tenant-priced payout). Design note first in `Services/Fiat/OffRamp/AGENTS.md`, checked by one short opus critic pass before implementation. Requirements:
  - **source:** a per-tenant payout balance (recommended: a `tenant_payout_float` ledger credited only by settled tenant top-ups through the existing Stripe rail, tenant as payer; alternative Stripe account debits from the tenant's connected account, recorded with why rejected or chosen);
  - **coverage check before any send:** inside the batch that admits the payout's money step, reserve a deterministic `tenant_payout_debit:{payoutId}` (UNIQUE) and decrement the available balance under a version CAS; THROW on insufficient funds → typed refusal before any Stripe call;
  - **exactly-once accounting:** one debit per payout, finalized on Paid, released only on authoritative definitive failure or reversal; replay idempotent; invariant `balance = credits − finalized − reserved` covered by a fixture and an operator view;
  - **fixtures:** two concurrent payouts against a balance covering one; payout vs top-up; reversal after Paid; crash between reserve and send.
- **Lead — reversal and parked exits:** clawback MUST-FIX (`ReversalCompensationService.cs:223-225`, `:716-733`, `:826-831` per A-2); reversal `NeedsOperator` exit (`:176,343`; `FiatReversalOperatorController.cs:133-155`); payout `NeedsOperator`/`Reversed` (`FiatPayoutOperatorController.cs:167-176`, `OffRampService.cs:667-711`) → audited record-Paid / return-units resolutions re-reading Stripe evidence; pre-effect refusal → `FailBeforeValueEffectAsync` (`AllocationManager.cs:230,239,269`) so `OnRampRecovery.cs:68-75` refunds; node-fee recovery wired end to end — `NodeFeeSettlementRecoveryWorker.cs:7` hosted (absent from every `AddHostedService<>` today) and `NodeFeeSettlementAtomicGroupReconciler.ReconcileDueAsync` (`:33`; DI-registered `Program.cs:1670-1671`, invoked by nothing) driven by it (`AllocationManager.cs:226,894`; LEDGER identity liveness CRITICAL); idempotency `InProgress` keys after a crash on money paths.
- **Helper (opus) — Hold/exchange** (`Services/{Hold,Reserve}/**`, `Managers/ReserveExchangeManager.cs`, `Controllers/{Exchange,HoldCommands,HoldReleaseMandates,PlatformReserve,ProjectTreasuryTenant}Controller.cs`, `Providers/Stores/Surreal/{SurrealHold*,HoldReleaseWorkflowFence}.cs`, `AlgorandProvider.Exchange*.cs`): direct hold admission endpoints (liability reads); durable recipient-readiness stage for direct buys/sales; buy-back top-up onto the durable transfer path; retired exchange rows operator path; preview consent oracle (`ExchangeController.cs:86-91` → `ReserveExchangeManager.cs:328-330`); Hold participant stage on original-login expiry; reserve partitions (`PlatformReserveController.cs:170-197`); D-7 exchange semantics behind L5's declaration.
- Owned (lead): `Providers/Fiat/**`; `Controllers/{FiatCheckout,FiatPayout,StripeWebhook,TenantPriceSchedule,AllocationReceipt,FungibleCreation}Controller.cs`; `Services/Fiat/**`; `Services/{Fungible,Allocation}/**`; `Managers/{AllocationManager,AllocationReceiptManager,NodeFeeSettlementManager,FungibleTokenManager,NodeTreasuryManager}.cs`; `Services/Governance/NodeFeeSettlement*.cs`; `Providers/Stores/Surreal/{SurrealNodeFeeSettlementStore,SurrealFungible*,SurrealPlatformReserveLedgerStore,SurrealPlatformTokenStore}.cs`; L3 POCOs (incl. the new tenant payout tables); fiat tests, `Http/Stripe*FlowTests.cs`.
- Sweep proves: Stripe test-mode drill — checkout `4242…` → CLI-signed webhook → credit; refund; dispute `4000000000000259` → reversal → clawback; Connect transfer; payout `000123456789` success / `000111111116` failure; a tenant-priced payout debits the tenant balance exactly once and an uncovered one is refused before send; ADV probe 4.

### L4 — Security, routes, platform resources

- **Lead:** audit-2 H2 dapp-role (`UserController.cs:144-147`; `Program.cs:641-652` in place; `UserManager.cs:255-277`); audit-1 M1 (`TenantManager.cs:149-151,224-226`); M2 + External-wallet ownership proof via the `WalletAuthManager` challenge, persisted flag (`WalletManager.cs:530-535`); audit-2 M8 legacy bridge (`CrossChainBridgeService.cs:140-193,857-904`); LOWs `ClaimsPrincipalExtensions.cs:110`, `CredentialManager.cs:52`, ApiKey rotate (`ApiKeyController.cs:247`, `SurrealApiKeyStore.cs:73-91`), `NetworkController.cs:94`, key-rotation step-up, faucet cap `WalletManager.cs:635`, provisioning email `TenantManager.cs:99-100`, `Controllers/AGENTS.md:219`.
- **Helper A (opus) — gates, child credentials, deployments:** `RouteAuthorizationGateTests.cs` and `CrossTenantGateTests.cs` loading the per-lane inventories (§3.5) + L4's own inventory files; **D-5** child credentials (`Program.cs:216-220` in place; `TenantController.cs:190-197`; scope allowlist from the inventory; child-token scope handling; issuing-tenant revoke; acting-tenant reads); audit-1 H1 deployments (routes `DeploymentsController.cs:24,63,86,97`; `DeploymentService.cs:129-197`; `DeploymentService.Quote.cs:79-88`) → capture credential + `SigningAdmissionAuthorityFactory.BindDirectAsync(…, [scopeFor(kind)])`; ADV F-H12 deployment `NeedsOperator` resolution (`SurrealDeploymentStore.cs:134-137`, `DeploymentService.Recovery.cs:76-78`).
- **Helper B (opus) — resource IDOR:** audit-2 H1 holon clone (`HolonController.cs:180-182`, `HolonManager.cs:566,596-639`; skip `!CanRead(child)` as `:390`); M6 (`HolonManager.cs:120,145,190,433,456-480,577,676`); M1–M5 actions (`ActionManager.cs:244-252,548-589,883-891,1002-1005`; `ActionController.cs:118-144,368-526`); dependency-status leak (`ActionManager.cs:1255-1351`); oracles (`DappCompositionManager.cs:556-563`); ActionGraphBundle `DappDevelop`; **`AwaitingReconciliation`** sweep returns `{ scanned, results }` (`ActionController.cs:325-336`, `ActionManager.cs:1761`).
- Owned: `Controllers/{Deployments,User,Wallet,WalletAuth,WalletIdentityOperator,Tenant,TenantConsent,Consent,Credential,ApiKey,Network,KeyRotation,Bridge,Holon,HolonTypeRegistry,Action,ActionGraphBundle,ActionRecoveryOperator,ActionSigningAuthorization,DappComposition,DappSeries,Search,SagaOperator}Controller.cs`; `Controllers/AGENTS.md`; `Services/{Deployments,Consent,Sagas}/**` except L2's two files; `Services/Actions/**` except `NotEnabledActionNodeTypes.cs` (L1), `Wtt*.cs` and `Handlers/RefundNodeHandler.cs` (L5); `Services/CrossChainBridgeService.cs`; `Managers/{UserManager,WalletManager,WalletAuthManager,TenantManager,CredentialManager,ConsentManager,HolonManager,HolonTypeRegistryManager,ActionManager*,ActionGraphBundleManager,DappCompositionManager,SearchManager}.cs`; `Helpers/{ClaimsPrincipalExtensions,ActionSigningScopes}.cs`; `Providers/Stores/Surreal/{SurrealDeployment*,SurrealNativePreparationStore,SurrealApiKeyStore,SurrealWallet*,SurrealCredential*,SurrealUserStore,SurrealUserExternalLogin*,SurrealConsent*,SurrealAction*,SurrealHolon*,SurrealBridgeStore,DeploymentAuthorityFence}.cs` except L1's webhook-delivery stores; L4 POCOs; the gate tests; `Architecture/Inventories/**/L4.json`.
- `HolonController.cs:94-122` is **not** edited for M9: L5 changes the called `BlockchainOperationManager` method in place.

### L5 — Chains: operations, NFT, DEX, WTT

- **Lead — `AwaitingSignature` (D-3):** `POST /api/blockchain/operations/{id}/submit-signed` (owner-scoped, signing scope checked, classified in L5's inventory). Verify the signing payload is byte-identical to the saved plan (Algorand `txn` bytes; Ethereum RLP unsigned payload + recovered signer = owner wallet; Solana message bytes + fee-payer signature); broadcast; reconcile to terminal (`ReconciliationService.cs:82` set); complete/fail the idempotency key so it leaves `InProgress`. Stuck rows past a configurable TTL: re-observe the chain first (the client holds signed bytes); Algorand/Solana past their validity window → terminal abandoned outcome; Ethereum (signed tx never expires, LEDGER parity HIGH L39) → `NeedsOperator` unless a different tx consumed the nonce. Operator-visible list/resolve.
- **Lead — M9 in place:** the holon mint/exchange method at `BlockchainOperationManager.cs:476` gains the NFT mint checks and stops platform-key signing for user assets; acting-tenant reads `:447,453`.
- **Lead — NFT (lane N):** ADV F-H12 renewal past a confirmed stage (`NftLifecycleService.ActorContinuation.cs:39-44`); F-M11 recipient-stage Solana costs; revoke issuing tenant (`NftLifecycleService.cs:217,227`); audit-2 M7 (`UserNFTService.cs:118-140,194-217`); prune `Services/Actions/Handlers/RefundNodeHandler.cs`; LOW 6 `SurrealNftLifecycleStore.cs:225`; LOW 5 fixtures; confirm L1's `OperationStatus` mapping.
- **Lead — DEX/activation (lane C):** D-7 declarations (`IDexTransactionModule` ×3; `IExchangeTransactionModule` at `AlgorandProvider.cs:32-33`) with per-network pins; ADV F-M13 settle-review type-0/1/access-list (`EthereumRpcEnvelope.cs:17-20`); `DexOperatorController.cs:21`; CleanupReady self-hold; D6 (`EthereumRecoveryController.cs`); D9 handler/`IHttpClientFactory` (`AlgorandProvider.cs:377`, `EthereumProvider.cs:182`, `SolanaProvider.cs:161`); RPC URL scrub (`EthereumProvider.cs:814`, `SolanaProvider.cs:601`); `SurrealSwapOperationStore.cs:51-63`; prune `swap_state`. Self-review re-verifies W0 chain fixes (fee caps, WTT signing context, Algorand members, Solnet shapes).
- **Helper (opus) — WTT** (`Services/Wormhole/**`, `Services/WormholeAdapter.cs`, `Services/Bridge/{WormholeProtocol,BridgeOptions,BridgeProtocolServiceCollectionExtensions}.cs`, `Providers/Blockchain/**/*Wtt*.cs`, `SolanaProvider.Durable.cs`, `SurrealWtt*.cs`, `Controllers/Wtt*Controller.cs`, new `Services/Actions/Wtt*.cs`): delete only the fabricator method (`WormholeAdapter.cs:549-555`); `wtt` action resolver for Bridge/Back behind WTT `Enabled`; stage-key readiness degrade + rewrap (`WttStageKeyReadiness.cs`); publication-mismatch and tenant-started-after-revoke exits; absence proof for exposed-but-unsigned participant messages; accept L4's proof flag at `WttLifecycleService.cs:263`; lane A final LOWs (`EthereumProvider.WttPreparation.cs:195`, `RetireStageAsync` probe, DB-clock fixture `WttLifecycleFlowTests.cs:440`, recipient address index, Algorand opt-in).
- Owned (lead): `Providers/Blockchain/**` except `AlgorandProvider.Exchange*.cs` (L3) and the helper's WTT files; `Services/{Dex,Users,Reconciliation,Ethereum,Blockchain}/**`; `Services/Actions/Handlers/RefundNodeHandler.cs`; `Managers/{BlockchainOperationManager*,NftManager}.cs`; `Controllers/{BlockchainOperation,Dex,DexOperator,EthereumRecovery,EthereumOperatorRecovery,Nft,NftReplacementCosts,NftWorkflowCosts,UserNFT}Controller.cs`; `Helpers/Nft*.cs`; `Providers/Stores/Surreal/{SurrealNft*,Nft*Fence,SurrealSwap*,SurrealDex*,SurrealEthereum*,SurrealOperationLog*}.cs`; L5 POCOs; chain tests (not `*ProtocolTests.cs`).

### L6 — Verification: protocol fixtures, real-DB gates, harness

- **Lead (sonnet):** Hardhat 2.26.3 replaces Ganache (`contracts/ethereum/package.json:18` + lockfile; `Fixtures/Ethereum/chain.mjs:8-11`, `dex-chain.mjs:11-30`; `Fixtures/Ethereum/AGENTS.md:3,13,21`); Uniswap lockfile audit; Tinyman LocalNet (validator bytes from algod `GET /v2/applications/{id}`, pinned app id/round/sha256); Jupiter/Whirlpool clone (`--clone-upgradeable-program` + one pool); `Round4Gates` enum round-trip for every enum-typed column; captured-null filters (first-party KYC approval, `SurrealFungibleCreationStore.cs:296-297`, `SurrealHoldCommandStore.cs:53-56`) plus guard-widening negatives; one THROW-marker + one duplicate case per store family; allow path for every `*Fence.cs` (21); `TransactionScopeGateTests.cs` and `SurrealQlSourceRulesTests.cs` (no `;` in `Of` literals, no bare-id `[References]` assignment, no `Assign(Key(…))`, no ordinal enum casts, SRDB0001 only in the template helper); mock debt (`SurrealFungibleProjectionTests.cs`, `SurrealActionNftHolonLinkTests.cs`); `// lock:` on stores with no W2 owner; P2 rehearsal assets `Fixtures/UpgradeRehearsal/**` (one valid row per table on `main` + assertion script).
- **Helper (opus):** interleaving fixtures (allowance claim vs old-op authorize/cancel, exposure vs withdrawal, Hold source consumption vs revocation, concurrent KYC begin, on-ramp lot double claim); WTT six-route (Hardhat core + token bridge with a local guardian; Solana `--bpf-program` + `initialize`; Algorand TEAL on LocalNet).
- Owned: `tests/AZOA.WebAPI.IntegrationTests/{Fixtures,Http/Infrastructure,Factories}/**`; `tests/AZOA.WebAPI.IntegrationTests/*ProtocolTests.cs`; `…/Persistence/Surreal/Round4Gates/**`; the two mock-debt files; the two architecture tests; `TestDataBuilders.cs` (core); `contracts/ethereum/{package.json,package-lock.json,scripts/**}`; `.github/workflows/ci.yml`; `frontend/src/test/msw/**`, `frontend/e2e/support/**`.

### L7 — Frontend, SDK, operator console (merges last)

- SDK: retire `DexAdapter`/Jupiter/Tinyman (`src/index.ts:53,65`, `src/dex/**`, `src/wallet.ts`, `src/core/{types,index}.ts`, `src/client/azoa-client.ts`) + root `AGENTS.md:86`; one `assertHex64`/`assertHex32` on DEX (`client.ts:3369-3422`), WTT (`:3464-3510`), Hold (`:3086-3161`), NFT lifecycle (`:3546-3681`) URL ids; methods for every §4.9 contract; `src/api/api-version.ts`.
- UI: first-party `frontend/src/app/(dashboard)/{verify-identity,funds,cash-out}/**` + sidebar; tenant payout-balance page (D-8 top-up + balance); operator console BFF allowlist (`frontend/src/app/api/operator/[...path]/route.ts:20-43,83`) and pages under `frontend/src/app/operator/(console)/**` (DEX settle-review, WTT, Ethereum recovery, saga, wallet identity, node fee control, Identomat refresh, deployment resolution, stuck `AwaitingSignature`); admin pages `frontend/src/app/(dashboard)/admin/**` (`parked-runs/page.tsx` shows the scanned count and never toasts success on zero rows or error, `:180`; `fiat-payouts`/`fiat-reversals` resolutions; webhook retry from any undelivered state); slices/journeys for every lane.
- Owned: `sdk/azoa-wallet/**`; `frontend/**` except L6's infra paths; root `AGENTS.md:86`. (L7 adds no backend routes, so it has no inventory file; the BFF allowlist is reviewed by RV-L6L7.)

### 4.8 POCO-table ownership map (W2; L1 owns all tables in W0/W1)

| Lane | Tables (`Persistence/SurrealDb/Models`) |
|---|---|
| L1 | `ActionWebhookEvent`, `ConsentWebhookEvent`, `WebhookEventRetryAudit`, `WebhookRegistration`, `WebhookRegistrationAudit`, `GovernanceCommandReceipt`, `GovernanceProposal`, `NodeGovernance*`, `NodeFeeSchedule`, `IdempotencyKeyStore`, `DataMigration`, `SagaSteps`, `AdminBootstrapState`, `Hnsw*`, `Ecosystem*`, `Executes`, `ForkedFrom`, `LocalEntityMutationLock` |
| L2 | `Kyc*`, `KybVerification`, `HostedSession*`, `HostedProvider*`, `TenantKycProviderSelection`, `PersonOperationClaim` |
| L3 | `FiatEvent`, `FiatPayout`, `FiatReversal`, `OnRampLot`, `TenantPriceSchedule`, new tenant payout balance/debit tables, `Hold*`, `ExchangeHoldSettlementReceipt`, `ExchangeHoldSource`, `ExchangePurchaseAllocation`, `PlatformReserveLedger`, `ProjectTreasuryLedger`, `PlatformToken`, `NodeFeeSettlement`, `NodeFeeAtomicGroup`, `NodeFeeAudit`, `NodeTreasury*`, `Fungible*` |
| L4 | `User`, `UserExternalLogin`, `OAuthExchangeCode`, `ApiKey`, `Wallet`, `WalletAuth*`, `ConsentGrant`, `ConsentAudit`, `ConsentLifecycle*`, `ConsentParticipation*`, `Holon`, `HolonType`, `Action*` (except `ActionWebhookEvent`), `ActionGraphBundle`, `DappSeries*`, `Deployment*`, `NativePreparation`, `NativeQuote`, `BridgeTx`, `ConsumedVaaLedger` |
| L5 | `Nft*`, `Wtt*`, `SwapOperationRecord`, `SwapStageRecord`, `SwapState` (prune), `DexQuoteRecord`, `EthereumDexAllowance`, `EthereumPreparation`, `EthereumSignerCheckpoint`, `OperationLog` |

Generated `.surql` is regenerated only at §8 P1, never hand-edited.

### 4.9 Route contracts (published day 1; renamed only by editing this table before the lane merges)

| Route | Lane |
|---|---|
| `POST /api/kyc/session`, `GET /api/kyc/status`, `POST /api/kyc/refresh`; `POST /api/operator/hosted-sessions/{id}/refresh` (Identomat) | L2 |
| payout resolve (record-Paid / return-units) and reversal settle-with-evidence under `/api/admin/fiat-payouts/{id}` and `/api/admin/fiat-reversals/{id}`; tenant payout balance read + top-up | L3 |
| wallet ownership challenge + proof under `/api/wallet/{id}`; deployment operator resolution; `runs/reconcile-sweep` → `{ scanned, results }` | L4 |
| `POST /api/blockchain/operations/{id}/submit-signed`; operator list/resolve for stuck `AwaitingSignature` | L5 |
| webhook operator retry (any undelivered state) | L1 |

## 5. Wave order

```
W0  L1 r4/core (lead + opus helpers A/B): bump → prelude → parallel subtrees → tests → compile → MERGE #1
W1  L1 r4/schema (clean tree) ∥ SurrealQL re-sweep (opus, read-only)                       → MERGE #2
W2  L1p · L2 · L3(+helper) · L4(+2 helpers) · L5(+helper) · L6(+helper) · L7 in worktrees
    "ready to merge" → orchestrator signals in order L1p → L2 → L3 → L4 → L5 → L6 → L7 (#3–#9)
W3  per-lane + cross-cutting review (§7) → ONE fix batch with bounded re-checks
SWEEP §8 (one pass, ranked gates first) → ≤3 failure batches → MERGE-TO-MAIN §9
```

**W1 SurrealQL re-sweep** (opus, read-only on the post-W0 tree; output `round4-sql-sweep.md`, each finding
tagged with its owning lane so it becomes W2 lane work): ADV §4's unread set (`SurrealHoldCommandStore.*`,
WTT/swap `results[guards.Count + k]`, `SurrealNodeFeeSettlementStore`, `SurrealSagaStore`,
`SurrealGovernanceStore.*`, `SurrealConsentLifecycleStore.*`, `TenantPriceScheduleStore`, the 16 non-literal
`Of()` bindings) against the full class list (§7); D2/F9 triage of ~200 typed `[References]` candidates;
RETURN-site count.

## 6. Lane merge readiness

Compile clean on the merged lane tree; merge check passes; lane record complete (scope, checklists, `## Hunks`,
POCO deltas, contracts, inventory files); `// lock:` on changed stores; no edits outside owned globs except
declared hot-file keys; secret scan clean.

## 7. Comprehensive independent review → one fix batch

**Fail-closed class list** (applied by every reviewer, the W1 re-sweep and lane self-review): binding names;
array captures; positional offsets; strict validator (SQL-declared variables); lowercase-enum and explicit-NULL
raw writes that typed reads now miss; captured-null guard widening on owner/tenant optional columns; bare ids
into `[References]` columns; `;` inside `Of` literals; ordinal enum casts; converter-less enums; statements
after a top-level RETURN.

Separate contexts prompted to refute, loading `round3-transaction-scope-review.md`,
`conductor-okf:code-styleguides` and the flow-tests rules; findings in `round4-review-ledger.md`.

| Reviewer | Tier | Range / scope |
|---|---|---|
| RV-L1 | opus | `git diff 8f89396..<merge #2>` plus merge #3: compile convergence (SRDB0001 rewrites inside authority fences, `ActionSigningScopes`, `Services/Auth`, chain fee caps, WTT signing context, fiat D1/F-M5), schema audits, webhooks, governance; `/security-review` |
| RV-L2 | opus | merge #4 diff; KYC/KYB/Identomat; `/security-review` |
| RV-L3 | opus | merge #5 diff; Stripe, reversal, D-8 tenant-funded payouts, Hold/exchange; `/security-review` |
| RV-L4 | opus | merge #6 diff; routes, IDOR, child credentials, deployments; `/security-review` |
| RV-L5 | opus | merge #7 diff; submit-signed exact bytes, NFT, DEX, WTT; `/security-review` |
| RV-L6L7 | opus | merges #8–#9; fixture/test quality (no tautologies, mutation check, exactly-once assertions not weakened), SDK, operator BFF allowlist (`/security-review`), UI honesty |
| RV-X cross-cutting | opus | `git diff <merge #2>..HEAD`: DI order vs §3.4, gate inventories complete across lanes, transaction scope across lane seams, cross-lane contracts (KYB gate consumers, proof flag, submit-signed), security at seams |

**One fix batch:** ≤3 fixers by domain in fresh worktrees (`r4/fix-<domain>`), same merge protocol. **Every
CRITICAL/HIGH fix gets a bounded re-check (one pass, scoped to the finding) by the reviewer that raised it
before its merge.** Then `round4-closeout.md`, RUNBOOK/`plan.md`/`metadata.json` updates, commit, and a merge
hash offered to `oasis-sleek-38`.

## 8. Integrated sweep (once)

**Prerequisites, in order:**
- **P-1 environment:** rootless podman, Docker-API pipe gone. Verify which compose provider works (`docker compose version`, `podman compose version`, `docker-compose`); `dev-up.ps1:119` and `e2e-stack.mjs:121` try `docker compose` first, so pin the working one (the e2e script honors its configured override). SurrealDB RocksDB containers need `--user 0:0` (dev, e2e, P2). Podman machine up, disk free.
- **P0:** SurrealForge 1.3.0 restored from nuget.org (D-1; local source removed, cache cleared).
- **P1:** final schema regeneration for W2 POCO deltas + goldens/flowcharts; re-run the unique-index audit and the old-row rule for invariants W2 added (incl. D-8 tables).
- **P2 upgrade rehearsal (the real Railway risk):** throwaway 3.1.4 container (`--user 0:0`, RocksDB); from a `main` worktree apply `main`'s schema and L6's seeded rows; then `surrealforge up` the new schema without `--allow-destructive`; assert every audited UNIQUE index exists, the defensive `REMOVE INDEX` is a no-op, every seeded old row stays writable through a typed write; reap.
- **P3:** `.\dev-up.ps1 -ResetDb`.
- **P4:** SDK `npm run build`.
- **P5:** Hardhat, Agave (`solana-test-validator`), AlgoKit LocalNet; Stripe CLI `listen`; Identomat config ids + tunnel (owner action).

**Gates — ranked; one pass:**
1. `dotnet build azoa.sln`.
2. **Ranked first:** KYC/KYB (unit + integration + HTTP flows filtered to `Kyc|Kyb|HostedSession`); **Stripe** (fiat unit/integration/HTTP flows incl. D-8 fixtures + the test-mode drill); **exactly-once** (`Round4Gates` interleaving, duplicate/marker, allow-path fences, clawback crash fixtures, host tests).
3. Remaining unit tests (architecture gates) → remaining integration tests on :8020 (protocol fixtures) → remaining HTTP flows → SDK vitest + build → frontend `npm test`, `test:browser`, lint, typecheck, build, `npm audit` → E2E `node e2e/scripts/e2e-stack.mjs up` → schema goldens.
4. Identomat live drill (owner configs).
5. **Evidence-gated activation record:** each production pin (per-chain DEX/exchange per D-7, webhooks, child credentials per D-5, WTT) flips only on its named green gate; any pin left off becomes a `deferred.md` entry (external blocker) or an owner line under "Owner scope decisions".
6. Secret scan of the final diff.

**Failure loop:** every failure goes into `round4-sweep-ledger.md`, classified by root cause. Batch 1
(class-shaped) → batch 2 (unmasked) → batch 3 (interleaving/journeys); each batch fixes all of its classes in
worktrees under the merge protocol; money/auth fixes get a `/security-review` spot-check; re-run only the
affected gates plus gate 1, ranked gates first. A fourth batch needs owner approval. **Stop rule:** if batch 3
is not green on KYC, Stripe and exactly-once, stop and re-plan.

## 9. Merge-to-main sweep

Preconditions: sweep green, or every residual red has an owner line; closeout uses the closing vocabulary;
`oasis-sleek-38` merged or idle. `git switch main` → `git merge --no-ff feat/platform-stub-closeout` → re-run
gates 1–3 (if `main^{tree}` equals the swept tree, record the identity and re-run gates 1, 2 and the goldens) →
**READY TO PUSH** in RUNBOOK §2. The owner pushes `main`; Railway deploy and secrets stay owner steps.

## 10. Owner actions outstanding (no open decisions)

1. Publish SurrealForge 1.3.0 (`b5a7d52`) to nuget.org — blocks sweep P0, not the lanes.
2. Create the Identomat KYC and KYB configurations, register the company callback, put the ids in `.env`, and authorize ~8 live sessions (OPT §3 steps 1–5; L2 raises it) — otherwise KYC/KYB end as *Built (vendor-doc fakes)*.
3. Optional: Veriff test credentials (else VERIFF-LIVE stays in `deferred.md`).
4. Approve a fourth sweep batch if batch 3 needs it.
5. Push `main` and the Railway release steps after READY TO PUSH.

## 11. Deferral ledger

| Item | Resolution | Closure check |
|---|---|---|
| Clawback rung 0 / uncapped finish / `Failed` key | L3 (A-2) | crash, definitive-failure, concurrent fixtures; dispute drill |
| Audit-2 H3 tenant cash-out pricing | L3 (D-8 tenant-funded) | exactly-once debit + coverage fixtures |
| Stale nonce index | L1 W1 defensive migration; not deploy-blocking | P2 |
| Ganache EIP-1898 | L6 Hardhat | protocol fixtures |
| DEX activation; exchange advertisement | L5/L3 (D-7) | gate 5; off pins → `deferred.md` |
| Webhook delivery disabled; retry only from DeadLettered | L1 (evidence-gated) | drain flow; gate 5 |
| Delegated child credentials | L4 (D-5) | tenant-branch flows + route gate |
| Governance gates | L1 (§0.2) | race fixtures; post-sweep verdict line |
| `AwaitingSignature` stuck rows | L5 (D-3) | submit-signed + stuck-row fixtures |
| `AwaitingReconciliation` false success; ordinal casts | L4 + L7; L1 W0 | scanned-count flow; architecture rule |
| Converter-less enums | L1 W0 | architecture test |
| Tinyman/Jupiter/WTT six-route fixtures | L6 | green, else pin off → `deferred.md`/owner line |
| Recipient-stage costs; legacy Wormhole + resolver; absence proof; stage-key readiness; External-wallet proof | L5 ×4; L4 | fixtures |
| Operator console coverage + fee control; direct hold endpoints/UI; durable recipient readiness; buy-back top-up; retired exchange rows | L7; L3 | slices + flows |
| RPC URL leak; D9; D6; pruning (`swap_state`, Uniswap lockfile, `RefundNodeHandler`) | L5; L6 | source scan; grep empty |
| SDK DexAdapter retirement | L7 | SDK build; grep empty |
| D1 / D2 / D8 / D5 / D7 / D3 / D4 | L1 W0 / L3 / L3 / L2 / L2 / 1.3.0 / live drill | Stripe drill; dev KYB configured; live drill |
| Transaction-scope, cross-tenant, route gates; allow-path; interleaving; mock debt | L6, L4 | gates green |
| Lane A/C/N LOWs | L5 | self-review + RV-L5 |
| SurrealForge typed transaction builder, `KeyOnly`, identifier-template builder, ledger re-claim | SurrealForge follow-ups (A-1, A-2) | logged in the SurrealForge repo |
| IDENTOMAT-WORKFLOWS | OWNER-ACTION, owner Jade Zaher; internal gaps removed from the entry | live drill evidence |
| IDENTOMAT-RECOVERY | removed (recorded default) | orphan/late-delete/unknown-callback fixtures |
| VERIFF-LIVE (new) | `deferred.md` unless credentials | live Veriff session |
| Ethereum/Solana custody parity | design-only; owner line in `deferred.md` "Owner scope decisions" | revisit when a reviewed settlement contract exists |
| Release steps (Railway secrets, deployed Identomat callback, Stripe deployed webhook) | owner, outside the sweep | RUNBOOK release checklist |

## 12. Agent count and cost (estimate)

| Phase | Runs | Tier |
|---|---|---|
| W0 | 3 (L1 lead + 2 helpers) | 3 opus |
| W1 | 2 (L1 lead + re-sweep) | 2 opus |
| W2 | 13 (7 leads + 5 helpers + 1 short D-8 design critic); peak ≤10 concurrent by starting L6/L7 after L2–L5 are underway | 10 opus, 2 sonnet, +1 short opus |
| W3 | 7 reviewers + ≤3 fixers + ~4 bounded re-checks | opus reviewers; fixers by domain |
| Sweep | ≤3 batches × ≤3 fixers + security spot-checks (typically ~6 runs) | mostly sonnet; opus on money/auth |

About **35–45 agent runs**, of which ~10 are short bounded passes (re-checks, spot-checks, the D-8 critic).
Still 7 lanes (vs 17 packages in R1); the increase over R2's 22–28 comes from the critic-required opus helpers
and per-lane reviewers. Cumulative **~60–120M tokens, ~85% opus** (W0 ~9–15M, W1 ~2–4M, W2 ~30–60M, W3
~14–29M, sweep ~4–12M). Wall-clock **~5–7 days** (W0 ~1 d, W1 ½ d, W2 2–2.5 d, W3 1–1.5 d, sweep 1–3 d).
Largest uncertainty: W0 test-project convergence and sweep batch 1.

## 13. Risks and re-plan triggers

| Risk | Mitigation |
|---|---|
| W0 is one lane on the critical path | two opus helpers on disjoint subtrees; RV-L1 reviews the whole W0+W1 range |
| Merge conflicts on `Program.cs`, POCOs, config | §3.4 rule; merge check at every merge; merged-lane precedence; ownership maps; host tests |
| Lane self-review misses defects | per-lane + cross-cutting independent review on the full class list; bounded re-checks of CRITICAL/HIGH fixes |
| D-8 is new money design | design note + opus critic pass before code; interleaving and crash fixtures; RV-L3 `/security-review` |
| 1.3.0 semantic change breaks a raw or guard path | W1 re-sweep; L6 round-trip and guard-widening fixtures; 1.3.1 via the owner |
| Chain SDK guesses compile but are wire-wrong | protocol fixtures decide; production pins stay off per chain → `deferred.md` |
| Old rows unwritable after new non-optional fields | W1 old-row rule; P2 rehearsal |
| Rootless podman / compose provider | P-1 before anything runs |
| KYB gate breaks tenant fixtures | day-1 builder with an `APPROVED` `kyb_verification` row |
| Cost | sonnet lanes where mechanical; helpers only on disjoint subtrees; single-sweep discipline |

**Re-plan if:** W0 is not compile-clean after ~1.5 days; W1 finds a money-path uniqueness invariant needing
redesign; a lane cannot merge compile-clean (with a passing merge check) after two attempts; the re-sweep finds
a new fail-closed class with >10 sites; the review returns a CRITICAL the single fix batch cannot close; the
D-8 design critic rejects the funding source twice; sweep batch 3 is red on KYC, Stripe or exactly-once.
