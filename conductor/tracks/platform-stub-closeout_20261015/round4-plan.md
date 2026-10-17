---
type: implementation-plan
round: 4
status: draft-for-adversarial-review
---

# Round 4 reconciled plan — platform-stub-closeout

Base: `feat/platform-stub-closeout` @ `8f89396` (tree = `0522fdf` + docs). SurrealForge @ `09bba8d` on
`fix/1.2.1-reserved-tables-and-declared-variables`, version 1.3.0, unpublished. Inputs:
`round4-assessment-adversarial.md` (cited `ADV F-xx`), `round4-assessment-optimism.md` (`OPT Fx`/`OPT §n`),
`round3-join-ledger.md` (`LEDGER`), the 247-error list (`ERR:<line>`). Written 2026-10-17, static only:
nothing built or run.

**Closing vocabulary (binding for every R4 report):** *Done* = executed evidence. *Built* = compiled,
unit-covered. *Authored* = not compiled. "Approved at source review" is never written as "closed".

## 0. Facts that supersede the assessments

| Fact | Evidence | Consequence for this plan |
|---|---|---|
| SurrealForge is **1.3.0**, not 1.2.1 | `Directory.Build.props:6`; CHANGELOG `[1.3.0]` | Bump target is 1.3.0 at `AZOA.WebAPI.csproj:109,110,113`, `IntegrationTests.csproj:30`, `Tests.csproj:45` |
| `IsUniqueViolation(string?)` is public and anchored (`09bba8d`) | `SurrealIdempotencyLedger.cs:374` | All 81 AZOA call sites pass `exception.Message` / `ex.Message` / `error.Message` (grep: 73/6/2) → they compile unchanged. No AZOA helper (ADV §6 row 1 superseded) |
| `EnsureAllOk` blames the culprit slot (`2067ae4`) | `SurrealResponse.cs:107,125` | OPT F5 (collateral slots) fixed for every raw `BEGIN…COMMIT`. No AZOA edits |
| D3 reserved-word `user` record ids fixed (1.2.1 content, carried into 1.3.0) | LEDGER §Round-4 carry-forward (SurrealForge 1.2.1) | ADV F-C5 closes at the bump |
| Buffered-flush collateral blame fixed (`09bba8d`) | commit message | Conflict retries on buffered transactions reach `IsRetryableConflict` |
| **Enum casing** and **captured-null → NONE**: in progress, **not yet committed** | `ExpressionTranslator.cs:662` still `ToLowerInvariant()` at `09bba8d`; `IsNullLiteral` `:257` | ADV F-C6, F-H7 and the F-C3 sub-claim close in the library. No AZOA workarounds. They are 1.3.0 publish blockers (OD-1) |
| `;` inside a `SurrealQuery.Of` literal still throws in 1.3.0 | `SurrealQuery.cs:105` | ADV F-H13 stays AZOA-side: `SurrealFungibleCreationStore.cs:273` binds the text as a parameter |
| `surrealforge up` skips `REMOVE INDEX` without `--allow-destructive` | `docker-entrypoint.sh:92-106`; `ReconcileRunner.cs:197-201` | Explicit migration (WP1) |
| Ganache 7.9.2 rejects EIP-1898; Hardhat 2.26.3 passes | OPT F7 probe P3 | Fixtures move to Hardhat (WP7). `contracts/ethereum/package.json:18` pins ganache today |
| Owner grant: patch SurrealForge freely; push/tag/publish stay with the owner | RUNBOOK §3 | Library-root fixes preferred; scope frozen for 1.3.0 (OD-1) |
| Fungible DI gap re-confirmed | `Program.cs:1347-1352` registers only `FungibleTokenManager`; no `IFungibleCreationService/Store` or worker registration outside tests | WP0a |
| `Program.cs:1162-1163` names deleted `Ed25519SignatureVerifier` | grep | WP0a |
| 130 `[SurrealTable]` POCO files vs 70 generated schema files | `Persistence/SurrealDb/Models`, `Generated/Schemas` | ADV F-C2 confirmed (count off by one vs ADV's 131) |
| Governance gate lift confirmed | `NotEnabledActionNodeTypes.cs:8` blocks only `CredentialRevoke` | OD-7 |

## 1. Reconciliation

### 1.1 Where the assessments agree (adopted as-is)

- Nothing runtime is claimable; ~75–83% of the 247 errors fall to four mechanical moves plus the library bump (ADV §6, OPT §2).
- D1, D2, D8 are live and block Stripe at three hops (ADV F-C4, OPT claim 2). D2 is a *class*, not a site (ADV F-C4 downstream list; OPT F9: `record<t>` rejects `'a'` and `'t:a'` strings, and `record = 't:a'` compares false).
- Lane B's two clawback MEDIUMs are defects on the goal path now, not "before real cards" (ADV F-H3, OPT §4).
- DEX/exchange non-activation and the carry-forward lists are disguised deferrals (ADV §7, OPT §3).
- Stale `ethereum_preparation_nonce` needs an explicit migration (ADV F-H9, OPT F8).
- Allow-path real-DB fixtures are the only detector for fail-closed classes (both; RUNBOOK §8 retro).
- SDK `DexAdapter` retirement is owner-decided and small.

### 1.2 Disagreements and resolutions

| # | Topic | Adversarial | Optimism | Resolution | Evidence / reason |
|---|---|---|---|---|---|
| 1 | Sequencing | WP0 → WP1 → parallel → one sweep | Fix batch A → compile-until-clean (6–8 builds) → env → sweep → 3–4 batches; stop rule | **Hybrid, owner decision OD-2.** Compile, schema generation and environment probes are convergence fix work (W0/W1 + build-only checkpoints at joins); the "one sweep" is the first full execution pass, then ≤3 failure batches with a stop rule | Neither ordering alone works: fixing feature code before the tree compiles (OPT S2 first) means lanes author against a broken tree; ADV's strict order still needs repeated builds |
| 2 | Enum casing / captured null | SurrealForge 1.2.2 or interim per-site | — (not found) | SurrealForge **1.3.0**, no AZOA workaround | Owner fact; RUNBOOK §3 "gaps fixed in SurrealForge" |
| 3 | Collateral error slots | not found | F5, found by probe | Fixed in SurrealForge `2067ae4` | RUNBOOK §2 confirmed on 3.1.4 + 3.2.4 |
| 4 | Classifier helper | AZOA `Core/Surreal` helper with precise match | Public library classifier | Library (`09bba8d`), anchored | §0 |
| 5 | IDENTOMAT-RECOVERY | Meets the bar for automated reconciliation; must not shelter F-M1 | Not needed: the safety property is "evidence only from the attached session", orphans are inert | **Close in R4** (OD-9): orphan supersede + `/external-api/delete/` + inert unknown-session callbacks; F-M1 4xx/429 release in the same package | OPT §3 cites `identomat-pending-result-find-2026-10-15.md` L914-944 (`/delete/`), lane V H1 (standard-mode id = token); ADV agrees F-M1 is internal |
| 6 | IDENTOMAT-WORKFLOWS | Meets bar; name the owner; internal gaps not covered | Owner action, ~15 min + ~8 live sessions; default session needs only the company key | **Re-label OWNER-ACTION**, owner = Jade Zaher; internal gaps (F-C3, F-H1, F-M3) moved to WP2; live drill = OD-12 | Both true: vendor config is external, but the gap list it hid is internal |
| 7 | `RETURN` inside `BEGIN…COMMIT` | Needs probe 7; 15 sites / 14 files | Settled by P1/P2: early RETURN commits but skips later statements; all 12 sites sit directly before `COMMIT` | Settled; gate rule added (WP8); WP9 reconciles the 12-vs-15 count | OPT ✓ row |
| 8 | Swapped args `SolanaProvider.NativeAccounts.cs:81` | Grouped as SDK mismatch | Real argument-order bug | Real bug (types `SolanaNativeMetadata.Value` ↔ `SolanaAccountArtifact` swapped, `ERR:96-97`) → WP0b | Compiler output |
| 9 | Disguised-deferral list | 14 items | 10 items | Union (§7), each resolved | — |
| 10 | Sweep realism / batches | "run once; batch the failures" | 3–4 batches, stop after batch 3 | ≤3 batches + optional 4th only with owner OK; stop rule kept | OPT §5 |
| 11 | WP0 effort | 1–2 days, 2–3 agents | ~1 day compile + 2–4 days sweep | Both kept; WP0 split into 6 slices (§3 W0) | — |
| 12 | Design-bearing compile errors (~40) | Route to lanes (WP2) | Route to owning lane | **Fixed in W0** by an opus slice (WP0b/WP0c), reviewed at the W0 join, because WP1 needs a compiling assembly and W2 lanes need a compiling tree | `surrealforge` CLI scans the built model assembly |

### 1.3 Overlap map (ADV WP list × OPT do-it-now list → this plan)

| ADV WP | OPT §4 items | This plan |
|---|---|---|
| WP-OWNER | SurrealForge hold/publish | WP-SF + OD-1..OD-12 |
| WP0 | S2/S3, D9-adjacent, F5/F6 | WP0 (0.1, 0a–0f) |
| WP1 | stale index, schema regen | WP1 |
| WP2 (lane design + exits) | G2 recipient costs, absence proof, WTT stage-key | WP0b (compile), WP5 (N), WP6a (C), WP6b (A) |
| WP3 | Identomat orphan policy | WP2 |
| WP4 | clawback, D1/D2/D8 | WP0c (D1), WP3a, WP3b |
| WP5 | — | WP2 (KYB) |
| WP6 | cross-tenant/transaction gates | WP8 (gates); fiat F-H8 → WP3a |
| WP7 | DEX/exchange activation | WP6a (+ WP13 exchange semantics) |
| WP8 | — | WP4a (OD-3) |
| WP9 | — | WP10 (OD-7) |
| WP10 | SDK retirement, RPC URL, pruning | WP0f, WP6a, WP5 |
| WP11 | — | WP11 (OD-11) |
| WP12 | operator console fee control | WP12 |
| — | Hardhat, Tinyman, Jupiter, WTT six-route | WP7 |
| — | signature-verified External wallets | WP4a |
| — (§4 coverage gap) | — | WP9 (SurrealQL re-sweep) |
| — (security audits, LEDGER §Round-4 inputs) | — | WP4a, WP4b, WP3a (H3) |

### 1.4 Corrections to the inputs

- RUNBOOK says the ADV fail-closed sweep "never reported"; ADV §4 does report a checked-clean list but names an unread set (`SurrealHoldCommandStore.*`, WTT/swap `results[guards.Count + k]`, `SurrealNodeFeeSettlementStore`, `SurrealSagaStore`, `SurrealGovernanceStore.*`, `SurrealConsentLifecycleStore.*`, `TenantPriceScheduleStore`, 16 non-literal `Of()` bindings). WP9 covers exactly that set.
- SDK `FiatPayoutView` line numbers drifted (LEDGER `client.ts:2278` vs ADV `:2206-2207`); WP0f greps, not line-trusts.
- `SurrealHostedSessionStore.cs` is shared by KYC and fiat: its attach contract (F-M5) is fixed with D1 in W0 (WP0c), then owned by WP2 in W2.

## 2. Owner decisions (ranked by blast radius)

Pattern used in several defaults — **evidence-gated activation**: code completed in R4, enabled in
Development/IntegrationTest/e2e config, production flag flips only when the named sweep gate is green and
recorded. It removes the deferral without shipping unexercised value paths.

**OD-1 SurrealForge 1.3.0 scope freeze and publish (gates the sweep).**
Default: freeze 1.3.0 = `09bba8d` + enum-casing + captured-null→NONE; final opus re-review of
`3aacd25..HEAD`; owner merges → `git tag v1.3.0` → `git push origin v1.3.0` → `gh release create v1.3.0`
before sweep P0. Defer the Failed-key re-claim, `KeyOnly` single-record builder and identifier-template
builder to a 1.4.0 backlog. AZOA compiles against the local pack meanwhile (user-level NuGet source, never
committed). Alternatives: (a) add the three 1.4.0 candidates now (another review cycle, slips the sweep);
(b) ship 1.3.0 without enum/null and add them in 1.3.1 (AZOA KYC queue, run reconcile and webhook due-scan
stay dead through the sweep). Reversal: 1.3.1 patch; low.

**OD-2 Sweep sequencing.** Default: compile convergence, schema generation and environment probes are fix
work, run by the orchestrator only (build-only, no tests) at: W0 (≤4 app + ≤4 solution builds), W1
(generation), and one build checkpoint at the W2 and W3 joins (≤2 builds each); plus one Development boot
probe at the W0 join (ADV probe 6, DI) and throwaway-container probes in W1 (ADV §9 probes 2, 3, 8). "The
sweep" = the first full execution pass of every gate (§5), then ≤3 failure batches re-running only affected
gates; stop rule after batch 3. Alternatives: (a) literal one-shot: no build until the sweep (the 247 + test
projects then *are* the sweep, 6–8 iterations anyway); (b) per-lane builds (concurrent `obj/` contention,
errors from other lanes' half-edits). Reversal: low (sequencing only).

**OD-3 Delegated child credentials (503 today, `Program.cs:216-220`, `TenantController.cs:190-197`).**
Default: evidence-gated enable. WP4a builds the per-endpoint scope allowlist (the route-authorization
inventory produces it), child-token scope handling (`ClaimsPrincipalExtensions.cs:110`), issuing-tenant
revoke checks and acting-tenant reads; IntegrationTest/e2e config enables child JWTs; production flips only
when the route gate and tenant-branch HTTP flows (NFT `RequesterOwns`, `HoldCustodyScope.AdmitsSettlement`,
WTT own-custodial rule) are green. Alternatives: (a) keep disabled, record an owner scope decision (tenant
signing rules stay latent and unexercised); (b) delete tenant branches (tenants never sign on behalf).
Reversal: default→(a) low; (b) high.

**OD-4 KYB scope.** Default: KYB `APPROVED` is required for a tenant to (1) create custodial accounts or
begin custodial KYC (`TenantCustodialAccountManager.cs:296`), (2) exercise any tenant-on-behalf value or
custody scope through `ITenantConsentGate` (`Services/Consent/TenantConsentGate.cs`: fiat checkout/payout
for a user, transfers, NFT/fungible/hold actions), (3) publish a payout price schedule. Not gated: KYB itself,
KYC provider configuration, webhooks, consent management, non-signing API keys. Alternatives: (a) fiat
on-behalf only; (b) all tenant API activity. Reversal: low-moderate (one seam plus fixtures).

**OD-5 Activation: DEX/exchange modules and webhook delivery.** Default: declare `IDexTransactionModule`
on all three providers and `IExchangeTransactionModule` on Algorand (`AlgorandProvider.cs:32-33`) with
per-network config pins (on in Development/IntegrationTest/e2e; production on per chain only after that
chain's protocol fixture is green); set `Webhooks:Enabled=true` in every environment (`WebhookOptions.cs:29`,
`docker-compose.e2e.yml:111`) with an outbox-drain flow. Alternative: prove refusals only (sweep shows
`Unavailable`; DEX/exchange labelled "Not done"). Reversal: config flip; low.

**OD-6 Tenant cash-out pricing (audit-2 H3, `TenantPriceScheduleController.cs:123,289-290` →
`OffRampService.cs:865-866`).** Default: node-funded cash-outs price at `min(tenant schedule, node
schedule)`; tenant prices above the node price are refused until a tenant-funded payout rail exists.
Alternatives: node schedule only; operator approval per schedule; build tenant-funded payouts. Reversal: low.

**OD-7 Governance (gates lifted in `3d08ace` without a verdict).** Default: re-block
GovernanceProposalCreate/Vote/Tally now; WP10 authors real-DB race fixtures and completes the verification
suite; the gate lifts only through a verdict recorded after those fixtures are green in the sweep.
Alternative: static verdict now (repeats the class that missed F5). Reversal: low.

**OD-8 The ten unrecorded deferrals (OPT §3).**

| # | Item | Default | Alternative | Reversal |
|---|---|---|---|---|
| 1 | Veriff / generic-hosted live verification | `deferred.md` VERIFF-LIVE (external: no account) unless the owner supplies Veriff test credentials | Owner scope-out of Veriff/generic | low |
| 2 | Ethereum/Solana value-custody parity | **Owner scope decision** (not a deferral): undeclared capability returns `Unavailable` truthfully; needs a new reviewed settlement contract (LEDGER parity HIGH L39) | Build in R4 (L + contract audit) | low (stays undeclared) |
| 3 | WTT six-route fixtures | Do in R4 (WP7, opus); production WTT stays `Enabled=false` until green | Label "Built, protocol-unverified" | low |
| 4 | Tinyman + Jupiter fixtures | Do in R4 (WP7) | Activation stays test-only | low |
| 5 | Recipient-stage Solana costs | Do in R4 (WP5 after WP6a contract note) | — | low |
| 6 | Legacy Wormhole retirement + `wtt` resolver | Do in R4 (WP6b) | — | low |
| 7 | Operator console fee control | Do in R4 (WP12, BFF + page; backend `NodeGovernanceController` exists) | Scope-out | low |
| 8 | Exchange module advertisement | Do in R4 per OD-5 | Refusals only | low |
| 9 | WTT stage-key readiness gate | Do in R4: **degrade** (typed `Unavailable` + health warning), never block startup | Block startup | low |
| 10 | Signature-verified External wallets | Do in R4 (WP4a, same root as audit-1 M2) | — | low |

**OD-9 IDENTOMAT-RECOVERY.** Default: close in R4 (WP2): after the bounded wait a possibly-sent admission
becomes `OrphanPossible`; one fresh attempt per subject per 24 h through `IdentomatRateBudget`; a late
original response is deleted via `/external-api/delete/` and audited, never attached; callbacks for unknown
or superseded session ids mutate nothing. Replace the entry with a note "vendor idempotent create = optional
hardening; billing question open with Identomat". Alternative: keep operator-only reconciliation and the
deferral. Reversal: low.

**OD-10 Clawback `Failed`-key fix location.** Default: AZOA-side attempt-suffixed claim key after a
definitive failure, stored on the reversal receipt (`ReversalCompensationService.cs:826-831`). Alternative:
SurrealForge ledger re-claim with attempt counter (1.4.0; changes semantics for every consumer). Reversal: low.

**OD-11 End-user KYC/money UI.** Default: minimal first-party pages (verify identity, add funds, cash out)
plus Playwright journeys (WP11); without them no E2E models the goal's user journey (ADV F-M7). Alternative:
SDK-only; journeys model tenant dashboards. Reversal: low (UI only).

**OD-12 Identomat workflows + live drill (owner action).** Default: before sweep P5 the owner creates the two
configurations (OPT §3 steps 1–5: "AZOA KYC" with document+liveness(+screening), "AZOA KYB", company callback
URL + existing `Kyc__Identomat__CallbackApiKey`, IDs into `.env` as `Kyc__Identomat__ConfigId`/`Kyb__ConfigId`,
https `Kyb__ReturnUrl`, tunnel for the drill) and authorises ~8 likely-billable live sessions. Without it,
KYC/KYB end R4 as *Built (vendor-doc fakes)*, not *Done*. Reversal: n/a.

**Assumptions (defaults, no decision needed):** frontend typecheck runs in the sweep (RUNBOOK §7 step 7.6
and root AGENTS.md supersede the older no-frontend-typecheck memory); payout-account setup becomes
first-party only (F-H8); dev KYC provider defaults to Identomat with fakes and the gates never accept MANUAL;
AML monitor polling (`appsettings.json:191`) is on wherever an Identomat config id is set; warning baseline =
25 (root AGENTS.md), new warnings are regressions.

## 3. Work packages

### 3.0 Rules for every package

- **Ownership:** a file belongs to at most one package per wave (tables below). Out-of-glob changes go to the owner as a request in the lane report. No git operations by lanes; the orchestrator commits at joins and scans staged files against every `.env` value.
- **Hot files** (additive-only; `Edit` tool only, never `Write`; re-read immediately before each edit; append inside a `// ── R4 <WP> ──` region you own, ≤15 lines per edit; never reorder or reformat): `Program.cs`; `appsettings*.json` and `docker-compose*.yml` (key ownership: `Kyc:*`/`Kyb:*` WP2, `Fiat:*`/`Stripe*` WP3a, `Dex:*`/`Exchange:*`/`Webhooks:*` WP6a, `Wtt*` WP6b, child-credential flag WP4a); SDK `sdk/azoa-wallet/src/api/client.ts`, `src/api/index.ts`, `src/index.ts` (domain regions); `frontend/src/components/layout/sidebar.tsx` (WP11 owns; WP12 additive); harness infrastructure `tests/AZOA.WebAPI.IntegrationTests/{Http/Infrastructure,Factories}/**`, `frontend/src/test/msw/**`, `frontend/e2e/support/**` (WP8 owns; additive for lanes); directory `AGENTS.md` files (append your own `§` section; root `AGENTS.md` only WP0f in W0, orchestrator afterwards). The orchestrator re-reads every hot file at each join and resolves overlaps.
- **Per-lane specs:** each lane writes its own HTTP flows (`Http/<Domain>*FlowTests.cs`), slices (`frontend/src/test/slices/<domain>*.slice.test.tsx`) and journeys (`frontend/e2e/journeys/<domain>*.spec.ts`) on lane V's harness.
- **Conventions:** root `AGENTS.md` (typed Surreal first, `// raw:` pointers, `inheritdoc`, typed results, no catch-log-rethrow, IDE0005, role-first layout, retired vocabulary — `RetiredVocabularyGuardTests`); generated `.surql` is never hand-edited; POCO edits only for tables the lane owns; record-link columns are set only from `table:id` links or `SurrealRecordParam` (D2 class rule, enforced by WP8).
- **Reviews (W3):** `/code-review high` by a separate opus context prompted to refute, loading `round3-transaction-scope-review.md` and `conductor-okf:code-styleguides`; `/security-review` mandatory on money/auth/custody/PII/user-input lanes (marked **SEC**); `oh-my-claudecode:critic` on contested designs (marked **CRITIC**). ≤2 fix cycles per lane; challenge every carry-forward on a money path; changed-file pruning gate (root AGENTS.md) before hand-back.
- **Acceptance** = source-level criteria (statically checkable at hand-back) + "sweep proves" (executed in §5).

### W0 — library + compile/DI convergence (parallel tracks)

**WP-SF — SurrealForge 1.3.0 completion** · repo `C:\Users\atooz\Programming\Projects\surrealforge` only · opus executor + opus reviewer · owner publishes.
- Scope: enum casing (typed read predicates serialize enums exactly as `SurrealWriter` binds them; `ExpressionTranslator.NormaliseValue` `:657-662`) → ADV F-C6; captured null → NONE (funcletize captured nulls; `IsNullLiteral` `:257-265,301`) → ADV F-H7, F-C3 sub-claim. CHANGELOG `### Changed` entries for both. Freeze per OD-1.
- Acceptance: library unit + integration green on 3.1.4 and 3.2.4 (throwaway containers, reaped); re-review verdict PUBLISH recorded in RUNBOOK §2; `artifacts/1.3.0/*.nupkg` sha256 recorded; after every repack, AZOA deletes `%USERPROFILE%\.nuget\packages\surrealforge.*\1.3.0` before restoring.
- Sweep proves: WP8 enum round-trip and captured-null fixtures against AZOA stores.

**WP0 — AZOA compile and DI convergence.** Error roots from `.omc/research/round3-join-compile-errors-2026-10-16.txt`.

*0.1 Prelude (serialized, sonnet, one agent, before any other slice):*
- Bump the five PackageReference lines to 1.3.0; add user-level source `surrealforge-local` → `artifacts/1.3.0`.
- Rename `Helpers/Encoding.cs` class `Encoding` → `ByteEncoding` (file `Helpers/ByteEncoding.cs`); fix every consumer that meant the helper; BCL users then resolve (`ERR` CS0104 Encoding ×50 + 3 `Encoding.UTF8` in `Helpers/CanonicalJson.cs:15,26`, `Helpers/NftLifecycleIdentity.cs:14`). Rule into `Helpers/AGENTS.md`: never shadow BCL names.
- `using AlgorandEncoder = Algorand.Utils.Encoder;` in the five Algorand files (`ERR:11-44`, ×17).
- Owned: `Helpers/Encoding.cs`, every file carrying a CS0104 `Encoding`/`Encoder` error, the three csproj files.

*0.2 Parallel slices (start after 0.1):*

| Slice | Tier | Scope (ids, file:line) | Owned globs |
|---|---|---|---|
| **WP0a** mechanical + DI + SRDB0001 | sonnet | `.Build()` ×17 (`SurrealEthereumPreparationStore.cs:94`; `SurrealHostedProviderAttemptStore.cs:35,77,89,129,137,146`; `SurrealUserStore.cs:89,153,289,302,314`; `SurrealWalletAuthChallengeStore.cs:66,113`; `WalletAuthIdentityMigrationService.cs:48,123,141`); `using SurrealForge.Client` (`HoldReleaseWorkflowFence.cs:28-31`, `NftWorkflowCostAuthorityFence.cs:28-29`, `ConsumerGovernanceService.cs:77-121`); `SurrealHoldCommandStore.Mandate.cs:34`; `RetryOnConflictAsync<T>` type args (`SurrealWebhookRegistrationStore.Management.cs:117`, `.Withdrawal.cs:48`); `LeasedWebhookSender.cs:38`; `ActionManager.cs:1578`; vocabulary drift `ActionSigningScopes.cs:38`, `DeploymentArtifactCompiler.cs:54`, `NftLifecycleOperationProjection.cs:32` (map to the real `OperationStatus` member; record the mapping for WP5); DI: `Program.cs:1163` → `WalletSignatureVerifier`, register `IFungibleCreationService`, `IFungibleCreationStore`, `FungibleCreationRecoveryWorker` (ADV F-C7); F-H13 `SurrealFungibleCreationStore.cs:273` text → parameter; **SRDB0001 layer**: one `Core/Surreal/SurrealIdentifierTemplate.cs` (every substituted token `^[a-z0-9_]+$`, one localized suppression with `// raw:`), migrate all ~36 non-literal `SurrealQuery.Of` sites (largest `SurrealBridgeStore.cs`×5, `SurrealActionWorkflowAdmissionStore.cs`×4, `SurrealActionLocalMutationStore.Witnesses.cs`×4, `DeploymentAuthorityFence.cs`×4, `WalletAuthIdentityMigrationService.cs`×3) — no per-site pragmas; new `tests/AZOA.WebAPI.Tests/Architecture/HostBuildValidationTests.cs` (real host, `ValidateOnBuild`+`ValidateScopes`, Development config); IDE0005 cleanup in owned files | `Providers/Stores/Surreal/**` except `SurrealHostedSessionStore.cs`; `Core/**`; `Services/{Webhooks,Governance,Auth,Fungible}/**`; `Services/Deployments/DeploymentArtifactCompiler.cs`; `Helpers/{ActionSigningScopes,NftLifecycleOperationProjection}.cs`; `Managers/ActionManager.cs` (one using); `Program.cs` (sole W0 editor after 0.1); the new test file |
| **WP0b** chain design-bearing | opus | Solana `ValidateAddressFormat` never written (17 sites, `ERR:81-119`): new `Providers/Blockchain/Solana/SolanaAddress.cs` over `TryParsePublicKey` (`SolanaProvider.cs:1035`) with explicit on-curve vs PDA-allowed per site, decision table in `Solana/AGENTS.md`; Jupiter fee-cap CS0165 ×9 `SolanaProvider.DexQuote.cs:45-117` (restructure, re-derive caps from the reviewed `round3-dex-*`/`JupiterCostGuard` contract, not by defaulting locals); `NativeArtifact.cs:30-35` CS0165; `SigningContext` null ×6 (`AlgorandProvider.WttSourceQuote.cs:127`, `.WttTargetProof.cs:54`, `EthereumProvider.WttQuote.cs:117`, `.WttTargetQuote.cs:107`, `SolanaProvider.WttSourceQuote.cs:129`, `.WttTargetProof.cs:50`) — supply the context the WTT contract binds, don't make it nullable; Algorand SDK members `ApplicationId` ×4 (`AlgorandGroupReceipt.cs:71`, `WttObservation.cs:50`, `WttTargetObservation.cs:50`, `TinymanSwapReceipt.cs:109`), `AssetCloseTo` (`ValueAcceptance.cs:123`), `long→int` (`:55`, check range, no blind cast); Solnet init-only `Data` ×4 (`NativeAccounts.cs:32`, `WttSourceInstructions.cs:41`, `WttTargetInstructions.cs:88`, `WttSolanaCostGuard.cs:63`), `CloseAccount` programId (`JupiterCleanupWire.cs:32`); **swapped arguments** `NativeAccounts.cs:81`; CS1628 ×3 (`AlgorandProvider.NativeArtifact.cs:92`, `SolanaProvider.NativeArtifact.cs:82-83`); Nethereum `ABIType` ×5 (`EthereumPayloadPermit.cs:35-40`, `EthereumProvider.Nft.cs:200`) verify cascade | `Providers/Blockchain/**` (after 0.1) |
| **WP0c** fiat compile + D1 + F-M5 | opus **SEC** | D1 (`FiatRailProvider.cs:610-617`, `PayoutAccountService.cs:179-246`): claims set `CreationToken`, call `TryBeginCreationAsync` before the Stripe create, attach with the token so `SurrealHostedSessionStore.cs:234-236` can hold; persist the connected-account id; F-M5 honor `expectedStateVersion` (`SurrealHostedSessionStore.cs:231-233`, contract `IHostedSessionStore.cs:50-60`); `OffRampService.cs:452` `requestedQuantity` — recover intent from history (`git log -S requestedQuantity -- Services/Fiat`), decision in `Services/Fiat/OffRamp/AGENTS.md` | `Providers/Fiat/**`; `Services/Fiat/OffRamp/{PayoutAccountService,OffRampService}.cs`; `Providers/Stores/Surreal/SurrealHostedSessionStore.cs`; `Interfaces/**/IHostedSessionStore.cs` |
| **WP0f** SDK + frontend | sonnet | `FiatPayoutView` declaration (grep `client.ts`, `api/index.ts`, `index.ts`); stale swap re-exports (`src/api/index.ts:112-114`); **DexAdapter retirement** (owner decision): delete `src/dex/**`, unwire `src/index.ts:53,65`, `src/wallet.ts`, `src/core/{types,index}.ts`, `src/client/azoa-client.ts`; root `AGENTS.md:86` → "backend DEX module seam (`IDexTransactionModule`)"; one `assertHex64`/`assertHex32` helper applied to DEX (`client.ts:3369-3422`), WTT (`:3464-3510`), Hold/mandate (`:3086-3161`), NFT lifecycle (`:3546-3681`) URL ids (lane A/B/C/N LOWs); SDK `tsc --noEmit` + `npm run build`; frontend typecheck | `sdk/azoa-wallet/**`; `frontend/**`; root `AGENTS.md` |

*0.3 App compile checkpoints* (orchestrator, `dotnet build AZOA.WebAPI.csproj -c Debug`, ≤4): route each error to the slice owning the file (files with no W0 owner go to WP0a, fiat files to WP0c); the SRDB0001 and IDE0005 layers surface here.

*0.4 Test projects* (after the app compiles; ≤4 `azoa.sln` builds): **WP0d** sonnet `tests/AZOA.WebAPI.Tests/**`; **WP0e** sonnet `tests/AZOA.WebAPI.IntegrationTests/**`. Adapt tests to production signatures (e.g. removed `WalletCreateModel.WalletType` in `WalletManagerTests.cs`; old `TryAttachProviderSessionAsync` fakes `HostedSessionFlowTests.cs:696`); never change production to fit a test (send to owner); delete a test only when its premise was deleted, logged in `round4-join-ledger.md` §Test triage.

**W0 join:** `azoa.sln` 0 errors, warnings ≤ 25; SDK tsc/build and frontend typecheck clean (pre-existing frontend failures enumerated, not fixed silently); Development boot probe resolves DI (OD-2); review of WP0b (code-review high), WP0c (code-review high + security-review) and the SRDB0001 helper — fixes run concurrently with W1 (disjoint globs). Commit `r4-w0`.

### W1 — schema

**WP1 — schema regeneration + stale index** · sonnet executor + opus index auditor · depends on W0 join and the 1.3.0 CLI from the local pack.
- Scope: ADV F-C2 (61 tables without schema; ~120 fields missing on 25 tables, e.g. `hosted_session.creation_token` read at `SurrealHostedSessionStore.cs:97-140,226-243`, `kyc_submission.aml_lineage_key` gate `KycGateService.cs:99,114`, 10 `on_ramp_lot` refund fields `OnRampRecovery.cs:55-102`, 16 `fiat_payout`); ADV F-H9 / OPT F8; ADV §4 item 9 `WebhookRegistration.cs:17` → `[Column(Name = "secret")]`.
- Work: regenerate all ~130 tables + flowcharts/goldens with the 1.3.0 Schema CLI; migration `Persistence/SurrealDb/Migrations/2026101x_xxxxxx__remove_stale_ethereum_preparation_nonce.surql` = `REMOVE INDEX IF EXISTS ethereum_preparation_nonce ON TABLE ethereum_preparation;` (README naming); **index audit** table in `Persistence/SurrealDb/AGENTS.md` mapping every single-winner invariant to its generated `UNIQUE` index (at least: `ethereum_preparation (protocol, sender, nonce, nonce_slot)`, KYC person key per tenant (D23), hosted-session admission, hosted provider attempt, idempotency key, fiat event provider id, on-ramp lot per intent, fiat payout per request, reversal clawback receipt, WTT body claim/VAA, swap operation, DEX quote, NFT lifecycle, fungible creation, hold command, consent grant, wallet address) — a missing index is fixed by POCO attribute in W1; enum `[Inside]` lists mirror domain enums.
- Probes (throwaway 3.1.4 container, reaped): undefined field on SCHEMAFULL (ADV probe 2), missing unique index fails open before / one loser after (probe 3), `type::record('t','')` (probe 8, feeds WP2).
- Owned: `Persistence/SurrealDb/{Generated,Migrations,Models}/**` (Models only in W1), schema golden/flowchart directories under `tests/`.
- Acceptance: POCO table count = generated file count; zero hand-edited `.surql`; audit table complete with no unexplained gap. Sweep proves: P2 kept-database rehearsal, golden gate 8.

### W2 — parallel delivery (all globs disjoint)

Stagger for cost: **W2a** (critical path) WP2, WP3a, WP3b, WP4a, WP4b, WP8, WP9 start first; **W2b** WP5, WP6a, WP6b, WP7, WP10, WP11, WP12, WP13 start as W2a agents free up (≤8 concurrent). Disjointness holds across all of W2.

**WP2 — KYC first-party, Identomat liveness, KYB enforcement** · opus · **SEC** **CRITIC** (KYB scope, orphan policy) · depends W1, OD-4, OD-9.
- Scope: ADV F-C3 (no begin route: `KycController.cs:34-117`, `KycManager.cs:115-118` uncalled); F-H1 (no refresh: `KycCallbackController.cs:62-91` always 202, `HostedSessionFlow.cs:337-363`, `HostedIdentityRecoveryWorker.cs:22-32`, operator 404 `HostedSessionReconciliationController.cs:110-118`, MANUAL_CHECK `IdentomatKycProviderService.cs:376-450`); F-M1 (`IdentomatHttpTransport.cs:100-146`, `HostedSessionFlow.cs:162-173`); F-M12 merge `Services/Bridge/RealValueKycGate.cs` into `Services/Kyc/KycGateService.cs` (keep the consumer interface, change only the registration); F-M3/F-M4/D5/D7 (`HostedSessionReconciliationController.cs:46,89`, `KycSettings.cs:121`, `docker-compose.dev.yml:141,145,154`); F-H2 KYB per OD-4; IDENTOMAT-RECOVERY per OD-9 (add `/delete/` to `IdentomatHttpTransport`); ADV §4 item 8 `HostedKycCreationFence.cs:25-27` empty key (IF-guard, per W1 probe); LEDGER audit-1 LOW `KycController.cs:74` status/summary tenant scope.
- Owned: `Controllers/{Kyc,KycCallback,HostedSessionReconciliation,HostedIdentityCallback,IdentomatKybCallback,TenantBusinessVerification,TenantKycConfiguration,AmlReview}Controller.cs`; `Managers/{Kyc*,KycControlPlane,TenantCustodialAccount}Manager.cs`; `Services/{Kyc,Kyb,HostedSessions}/**`; `Services/Bridge/RealValueKycGate.cs`; `Services/Consent/{TenantConsentGate,TenantCustodialAccess}.cs`; `Providers/{Kyc,Kyb}/**`; `Helpers/HostedKycCreationFence.cs`; `Settings/Kyc*.cs`; `Providers/Stores/Surreal/{SurrealKyc*,SurrealKyb*,SurrealHostedSession*,SurrealHostedProviderAttempt*}.cs`; KYC/KYB/hosted POCOs; tests under `**/Kyc/**`, `**/Kyb/**`, `**/HostedSessions/**`, `Http/{IdentityVerification,BusinessVerification,HostedSessionReconciliation,Kyc*}FlowTests.cs`, slices `kyc*`/`business-verification*`.
- Acceptance: `POST /api/kyc/session` (FirstPartyLogin) → `BeginAsync`; `POST /api/kyc/refresh` (owner) and operator refresh for Identomat re-fetch `/result/` (D8: callback is a trigger); a poll worker with backoff covers AwaitingSubject/AwaitingOutcome/expired Resuming/MANUAL_CHECK within `IdentomatRateBudget`; 4xx/429 release, timeouts/5xx stay quarantined; one gate implementation; KYB gate at `ITenantConsentGate` + custodial access; orphan policy as OD-9; dev compose uses Identomat with https return URLs, no mojibake, widget host from settings. Fixtures: real-DB allow path begin→callback→result→gate open; abandon/reject deny; orphan + late-delete; unknown-session callback inert; KYB allow/deny; `KycEvidenceFence` empty-array and multi-guard (LEDGER).
- Sweep proves: KYC/KYB HTTP flows on vendor-doc fakes; with OD-12, live approve/abandon/reject/replay KYC, approve/reject KYB, one lost-response drill, D4 standard-mode `{id,url}` confirmed.

**WP3a — Stripe rail, fiat tenant scope, payout exits** · opus · **SEC** · depends W1, OD-6.
- Scope: D2 `StripeWebhookInbox.cs:467-468` + masked D2-class sites `OnRampSettlementHandler.cs:436,545-547`, `OffRampService.cs:335-337` → `FiatPayoutLedger.cs:112`; D8 `FiatRailProvider.cs:569-595` (persist `expires_at` at first claim, reuse it under the stable key); F-M2 signing-secret list (`StripeWebhookController.cs:69-94`), platform + Connect endpoints documented; F-H8 payout setup first-party only (`FiatPayoutController.cs:105-116`), intents tenant-scoped (`FiatRailProvider.cs:636-660,798-799`; admission key `FiatCheckoutController.cs:281-282`), payout history/replay tenant filter; audit-2 H3 per OD-6 (`OffRampService.cs:865-866`); F-H11 payout `NeedsOperator`/`Reversed` exit (`FiatPayoutOperatorController.cs:167-176`, `OffRampService.cs:667-711`): audited "record provider-verified Paid" and "return units" resolutions that re-read Stripe evidence; LOWs `FiatEventRecovery.cs:24-37` replay cap/backoff, `OnRampRecovery.cs:136-146` 23 h refusal.
- Owned: `Providers/Fiat/**`; `Controllers/{FiatCheckout,FiatPayout,StripeWebhook,TenantPriceSchedule}Controller.cs`; `Services/Fiat/*.cs` (top level); `Services/Fiat/{OnRamp,OffRamp}/**`; fiat POCOs; fiat tests; `Http/Stripe*FlowTests.cs`; slices `fiat*`.
- Acceptance: no bare id reaches a `[References]` column on any fiat path; checkout replay after a lost create response returns the same session; Connect events verify; every parked payout state has an audited exit with provider evidence.
- Sweep proves: Stripe test-mode drill — checkout `4242…` → CLI-signed webhook → credit; refund; payout `000123456789` success and `000111111116` failure; Connect transfer; ADV probe 4 (second onboarding link).

**WP3b — reversal/clawback, money liveness** · opus · **SEC** · depends W1, OD-10.
- Scope: LEDGER clawback MUST-FIX — rung 0 subtracts uncompleted reservations (`ReversalCompensationService.cs:223-225`), pending finish capped at `attributable` (`:716-733`), `Failed` key per OD-10 (`:826-831`); reversal `NeedsOperator` exit (early returns `:176,343`; `/restart` no-op `FiatReversalOperatorController.cs:133-155`) → audited settle-with-evidence; D2-class `FiatReversalEventHandler.cs:239-242`; on-ramp pre-effect refusal → `FailBeforeValueEffectAsync` (`AllocationManager.cs:230,239,269`) so `OnRampRecovery.cs:68-75` refunds; ADV F-M13 node-fee recovery worker registered and wired (`NodeFeeSettlementRecoveryWorker.cs:7`, `AllocationManager.cs:226,894`); challenge: idempotency `InProgress` keys after a crash on a money path (add TTL re-claim if reachable).
- Owned: `Services/Fiat/Reversal/**`; `Managers/{Allocation,NodeFeeSettlement}Manager.cs`; `Services/Governance/NodeFeeSettlement*.cs`; `Providers/Stores/Surreal/{SurrealNodeFeeSettlementStore,SurrealHoldCommandStore.ExchangeClawback,SurrealHoldCommandStore.ExchangeRecovery}.cs`; reversal tests.
- Acceptance: crash-between-reservation-and-claim, definitive-failure and concurrent-pass real-DB fixtures; no over-recovery path (re-trace LEDGER cycle-2 items). Sweep proves: dispute card `4000000000000259` → reversal → clawback; fixtures green.

**WP4a — auth and value routes, route-authorization gate** · opus · **SEC** **CRITIC** (OD-3) · depends W1, OD-3.
- Scope: audit-1 H1 deployments skip signing scopes (routes `DeploymentsController.cs:24,63,86,97`; `DeploymentService.cs:129-197`; `DeploymentService.Quote.cs:79-88`) → capture credential + `SigningAdmissionAuthorityFactory.BindDirectAsync(…, [scopeFor(kind)])`; ADV F-H12 deployment `NeedsOperator` operator resolution (`SurrealDeploymentStore.cs:134-137`, `DeploymentService.Recovery.cs:76-78`); audit-2 H2 dapp-role (`UserController.cs:144-147`, policy `Program.cs:646`, `UserManager.cs:255-277`): API keys need `dapp:manage`, only the operator changes a current manager; audit-1 M1 (`TenantManager.cs:149-151,224-226`), M2 + OD-8 #10 wallet ownership proof via the `WalletAuthManager` challenge with a persisted proof flag (`WalletManager.cs:530-535`); audit-2 M8 legacy bridge scope/consent/no body-supplied reverse address (`CrossChainBridgeService.cs:140-193,857-904`); LOWs: `ClaimsPrincipalExtensions.cs:110`, `CredentialManager.cs:52`, ApiKey rotate (`ApiKeyController.cs:247`, `SurrealApiKeyStore.cs:73-91`), `NetworkController.cs:94`, key-rotation step-up, faucet cap `WalletManager.cs:635`, provisioning email squat `TenantManager.cs:99-100`, `Controllers/AGENTS.md:219`; OD-3 child credentials.
- **Route-authorization gate:** `tests/AZOA.WebAPI.Tests/Architecture/RouteAuthorizationGateTests.cs` + checked-in inventory (route → required signing scope → enforcement call); every POST/PUT/DELETE action must be classified (value-moving or explicitly non-value); value-moving actions must show `BindDirectAsync`/`HasSigningScope`/signing fence in their call path; unclassified new actions fail.
- Owned: `Controllers/{Deployments,User,Wallet,WalletAuth,Tenant,TenantConsent,Consent,Credential,ApiKey,Network,KeyRotation,Bridge,WalletIdentityOperator}Controller.cs`; `Controllers/AGENTS.md`; `Services/Deployments/**`; `Services/CrossChainBridgeService.cs`; `Managers/{User,Wallet,WalletAuth,Tenant,Credential,Consent}Manager.cs`; `Helpers/ClaimsPrincipalExtensions.cs`; `Providers/Stores/Surreal/{SurrealDeployment*,SurrealApiKeyStore,SurrealWallet*,SurrealCredential*,SurrealUserExternalLogin*}.cs`; wallet/deployment/API-key POCOs; the gate test + inventory; tests for these.
- Publishes on day 1 (for WP6b): proof flag name and semantics. Sweep proves: route gate green; deployment/dapp-role/bridge deny + allow flows; tenant-branch flows (OD-3).

**WP4b — resource IDOR** · opus · **SEC** · depends W1.
- Scope: audit-2 H1 holon clone (`HolonController.cs:180-182`, `HolonManager.cs:566,596-639`; skip `!CanRead(child)` as `:390` does); M6 propagate/attach (`HolonManager.cs:120,145,190,433,456-480,577,676`); M9 holon mint/exchange (`HolonController.cs:94-122`, `BlockchainOperationManager.cs:476`); M1–M5 actions (`ActionManager.cs:244-252,548-589,883-891,1002-1005`; `ActionController.cs:118-144,368-526`); LOWs dependency-status leak (`ActionManager.cs:1255-1351`), existence oracles (`DappCompositionManager.cs:556-563`), ActionGraphBundle `DappDevelop`, acting-tenant reads `BlockchainOperationManager.cs:447,453`.
- Owned: `Controllers/{Holon,HolonTypeRegistry,Action,ActionGraphBundle,DappComposition,DappSeries,Search,BlockchainOperation}Controller.cs`; `Managers/{Holon,HolonTypeRegistry,ActionManager*,ActionGraphBundle,DappComposition,Search,BlockchainOperation*}*.cs`; `Helpers/ActionSigningScopes.cs`; tests for these.
- Sweep proves: private-descendant clone refused, owner clone succeeds; run-control scope flows.

**WP5 — NFT/credential lifecycle (lane N)** · opus · **SEC** · depends W1, WP6a contract note.
- Scope: ADV F-H12 renewal for workflow-started / tenant-revoked lifecycles past a confirmed stage (`NftLifecycleService.ActorContinuation.cs:39-44`); ADV F-M11 recipient-stage Solana costs (OD-8 #5); revoke issuing-tenant check (`NftLifecycleService.cs:217,227`); audit-2 M7 bindings survive transfer (`UserNFTService.cs:118-140,194-217`); prune `RefundNodeHandler` + test callers; lane N LOW 6 positional index (`SurrealNftLifecycleStore.cs:225`), LOW 5 fixtures (Refund, Solana gen ≥1, participant lapse, revoke between bind and commit); confirm WP0a's `OperationStatus` mapping.
- Owned: `Services/Users/**`; `Controllers/{Nft,NftReplacementCosts,NftWorkflowCosts,UserNFT}Controller.cs`; `Managers/NftManager.cs`; `Helpers/Nft*.cs`; `Providers/Stores/Surreal/{SurrealNft*,Nft*Fence}.cs`; `Providers/Blockchain/Solana/{SolanaProvider.Nft*,SolanaNftMetadata,SolanaMetadataCreationPolicy}.cs`; `Providers/Blockchain/{Ethereum/EthereumProvider.Nft,Algorand/AlgorandProvider.Nft}.cs`; the `RefundNodeHandler` file; NFT POCOs and tests.
- Sweep proves: NFT protocol fixtures (Hardhat, Agave + Metaplex clone, LocalNet).

**WP6a — activation, DEX/Ethereum, provider hygiene (lane C)** · opus · **SEC** · depends W1, OD-5.
- Scope: OD-5 declarations + config pins + webhook delivery + outbox-drain flow; ADV F-M13 settle-review for type-0/1/access-list winners (`EthereumRpcEnvelope.cs:17-20`); lane C final LOWs (`DexOperatorController.cs:21` log order, CleanupReady self-hold); D6 (`EthereumRecoveryController.cs` provider exceptions → typed results); D9 inject handler/`IHttpClientFactory` (`AlgorandProvider.cs:377`, `EthereumProvider.cs:182`, `SolanaProvider.cs:161` + factory); RPC URL scrub (`EthereumProvider.cs:814`, `SolanaProvider.cs:601`, all providers); ADV §4 item 7 `SurrealSwapOperationStore.cs:51-63`; prune `swap_state`; day-1 contract note on recipient-stage costs for WP5.
- Owned: provider cores `Providers/Blockchain/{Algorand/AlgorandProvider.cs,Ethereum/EthereumProvider.cs,Solana/SolanaProvider.cs}` (hot for WP5/WP6b/WP13: request-only); `Providers/Blockchain/**/{*Dex*,Jupiter*,Tinyman*,Whirlpool*}.cs`; `EthereumRpcEnvelope.cs`, `EthereumProvider.{Preparation,Recovery,FinalizedReceipt,ContractReads}.cs`; provider factory; `Services/{Dex,Webhooks}/**`; `Controllers/{Dex,DexOperator,EthereumRecovery,EthereumOperatorRecovery}Controller.cs`; `Providers/Stores/Surreal/{SurrealSwap*,SurrealDex*,SurrealEthereum*}.cs`; swap/DEX POCOs; DEX tests.
- Sweep proves: Ethereum DEX (Hardhat Uniswap) executes; Tinyman/Jupiter per WP7; webhook drain delivers to the e2e fake.

**WP6b — WTT/Wormhole (lane A)** · opus · **SEC** · depends W1, WP4a day-1 note.
- Scope: OD-8 #6 delete legacy fabricator (`WormholeAdapter.cs:549-555`, registration `Program.cs:1639`) and register a `wtt` action resolver for Bridge/Back behind WTT `Enabled`; OD-8 #9 stage-key readiness degrade + rewrap maintenance (`WttStageKeyReadiness.cs`); exits for publication mismatch and tenant-started-after-revoke (ADV §3); absence proof for exposed-but-unsigned participant messages (adapt the reviewed Jupiter observer); accept the External-wallet proof flag at `WttLifecycleService.cs:263`; lane A final LOWs (`EthereumProvider.WttPreparation.cs:195` → `ReleasedReservation`, chain-agnostic `RetireStageAsync` probe, DB-clock offer-expiry fixture `WttLifecycleFlowTests.cs:440`, address index for other-user custodial recipients, Algorand opt-in path).
- Owned: `Services/Wormhole/**`; `Services/WormholeAdapter.cs`; `Services/Bridge/{WormholeProtocol,BridgeOptions,BridgeProtocolServiceCollectionExtensions}.cs`; new `Services/Actions/Wtt*.cs`; `Controllers/Wtt*Controller.cs`; `Providers/Blockchain/**/*Wtt*.cs`; `Providers/Blockchain/Solana/SolanaProvider.Durable.cs`; `Providers/Stores/Surreal/SurrealWtt*.cs`; WTT POCOs/tests.
- Sweep proves: WTT flow/store tests; six-route protocol proof if WP7 lands it.

**WP7 — protocol fixtures** · sonnet (Hardhat, Tinyman, Jupiter) + opus (WTT six-route) · depends W1, OD-8 #3/#4.
- Scope: OPT F7 — replace `ganache` with `hardhat` 2.26.3 (`contracts/ethereum/package.json:18` + lockfile), move `Fixtures/Ethereum/chain.mjs:8-11` and `dex-chain.mjs:11-30` to a Hardhat JSON-RPC server (random loopback port, private key pipe unchanged), update `Fixtures/Ethereum/AGENTS.md:3,13,21`; Uniswap lockfile audit (10 findings); Tinyman LocalNet (validator approval/clear bytes from algod `GET /v2/applications/{id}`, pin app id/round/sha256; pool logic-sig from the pinned Tinyman SDK); Jupiter/Whirlpool clone (`--clone-upgradeable-program` + one pool's accounts); WTT six-route (EVM core + token bridge on Hardhat with a local guardian; Solana `--bpf-program` canonical ids + `initialize`; Algorand TEAL on LocalNet).
- Owned: `tests/AZOA.WebAPI.IntegrationTests/Fixtures/**`; `tests/AZOA.WebAPI.IntegrationTests/*ProtocolTests.cs` (+ new `Tinyman*`, `Jupiter*`, `Wtt*ProtocolTests.cs`); `contracts/ethereum/{package.json,package-lock.json,scripts/**}`; `.github/workflows/ci.yml` (fixture jobs).
- Acceptance: no `ganache` dependency; fixtures never substitute mocked RPC. Sweep proves: every protocol fixture green or the chain's production pin stays off.

**WP8 — architecture gates, real-DB gate fixtures, mock debt** · sonnet + opus (interleaving design) · depends W1.
- Scope: `TransactionScopeGateTests.cs` (BEGIN ends in COMMIT with THROW guard; `AuthorizedAt|ExternalExposedAt` set only via `CommitStageAsync`; in-batch SELECT carries `// lock:`; no statement after a top-level RETURN inside BEGIN…COMMIT); `CrossTenantGateTests.cs` (owned-resource reads/commands compare acting tenant; inventory-driven); `SurrealQlSourceRulesTests.cs` (no `;` in `Of` literals, no bare-id assignment to `[References]` properties, no `Assign(Key(…))`, SRDB0001 only inside the template helper). Real-DB fixtures `Persistence/Surreal/Round4Gates/**`: enum round-trip for every enum-typed column; captured-null filters (first-party KYC approval, fungible participant list `SurrealFungibleCreationStore.cs:296-297`, owned hold `SurrealHoldCommandStore.cs:53-56`); one THROW-marker and one duplicate case per store family (proves culprit attribution + anchored classifier end to end); **allow path for every `*Fence.cs`** (21); interleaving: allowance claim vs old-op authorize/cancel, exposure vs withdrawal, Hold source consumption vs revocation, concurrent KYC begin, on-ramp lot double claim. Mock debt: `SurrealFungibleProjectionTests.cs`, `SurrealActionNftHolonLinkTests.cs` fake only the chain edge (LEDGER). KYB-approved tenant builder for harness flows (OD-4).
- Owned: the three gate files under `tests/AZOA.WebAPI.Tests/Architecture/`; `tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/Round4Gates/**`; the two mock-debt files; harness infrastructure (hot); comment-only `// lock:` lines in store files with no other W2 owner (list computed at dispatch).
- Sweep proves: gates green; each fixture would go red if its guarded line were inverted (mutation check).

**WP9 — SurrealQL fail-closed re-sweep** · opus, read-only · depends W1.
- Scope: §1.4 unread set; five classes (binding names, strict validator, array captures, positional offsets, 3.x syntax); D2/F9 triage of ~200 typed `[References]` candidates (re-create the `typedlink` script in the session scratchpad); RETURN-site count (12 vs 15).
- Owned: `conductor/tracks/platform-stub-closeout_20261015/round4-sql-sweep.md` only. Findings route to file owners as W3 cycle-1 items; unowned files go to one orchestrator-assigned fixer.

**WP10 — governance** · sonnet + opus reviewer · depends W1, OD-7.
- Scope: re-block three types in `NotEnabledActionNodeTypes.cs:8`; finish the verification suite (`round2-governance-verification-migration.md`); real-DB race fixtures for proposal/vote/tally/finalization (`SurrealGovernanceStore.Finalization.cs:47`); document the lift procedure.
- Owned: `Services/Actions/NotEnabledActionNodeTypes.cs`; `Services/Governance/**` except `NodeFeeSettlement*`; `Providers/Stores/Surreal/SurrealGovernanceStore*.cs`; `Controllers/NodeGovernanceController.cs`; `Managers/NodeGovernanceManager.cs`; governance tests.

**WP11 — first-party UI (if OD-11 = yes)** · sonnet + designer · depends on the route contract below (parallel start), joins after WP2/WP3a.
- Owned: new `frontend/src/app/(dashboard)/{verify-identity,funds,cash-out}/**`; their slices and journeys; `sidebar.tsx`.
- Route contract (owning lane may rename only by editing this line before the W2 join): `POST /api/kyc/session`, `GET /api/kyc/status`, `POST /api/kyc/refresh`; existing `startFiatCheckout`, `beginPayoutAccountSetup`, `startCashOut` SDK methods.

**WP12 — operator console** · sonnet · depends on WP2/WP3/WP4a/WP6 route contracts.
- Scope: ADV F-M10 + OD-8 #7: BFF allowlist (`frontend/src/app/api/operator/[...path]/route.ts:20-43,83`) and pages for DEX settle-review, WTT operator, Ethereum operator recovery, saga, wallet-identity, node fee control, payout/reversal resolutions, Identomat refresh, deployment resolution, per-run reconcile button.
- Owned: `frontend/src/app/api/operator/**`; new subpages under `frontend/src/app/(dashboard)/admin/**`; operator slices.

**WP13 — Hold/exchange completion (lane B)** · opus · **SEC** · depends W1, WP6a declaration.
- Scope: direct hold admission UI + liability-read endpoints; recipient readiness for direct buys/sales on a durable stage (replace the synchronous preparer); buy-back top-up onto the durable transfer path; operator path for retired exchange rows; exchange preview consent oracle (`ExchangeController.cs:86-91` → `ReserveExchangeManager.cs:328-330`); N→B request: Hold participant stages when the original login credential expires.
- Owned: `Services/{Hold,Reserve}/**`; `Managers/ReserveExchangeManager.cs`; `Controllers/{Exchange,HoldCommands,HoldReleaseMandates,PlatformReserve,ProjectTreasuryTenant}Controller.cs`; `Providers/Stores/Surreal/{SurrealHold*,HoldReleaseWorkflowFence}.cs` except the two WP3b files; `Providers/Blockchain/Algorand/AlgorandProvider.Exchange*.cs`; `frontend/src/app/(dashboard)/holds/**`; hold/exchange POCOs/tests; reserve-partition LOW (`PlatformReserveController.cs:170-197`).

### Parked states → exits (ADV §3; every NO-EXIT / EXIT-BROKEN row)

| State | Exit delivered by |
|---|---|
| KYC operator queue / active attempt (enum filter) | WP-SF enum casing; WP8 fixture |
| Identomat AwaitingSubject/AwaitingOutcome/expired Resuming/MANUAL_CHECK | WP2 refresh routes + poll worker |
| ActionRun `AwaitingReconciliation` bulk sweep | WP-SF enum casing; WP12 per-run button |
| Webhook `Pending` | OD-5 enablement (WP6a) + WP-SF enum casing |
| FiatPayout `NeedsOperator`/`Reversed` with units owed | WP3a |
| FiatReversal `NeedsOperator`; rung with `Failed` key | WP3b |
| On-ramp issuance refused after charge | WP3b (`FailBeforeValueEffectAsync`) |
| Fungible creation Submitted/Confirmed/NeedsOperator | WP0a DI + worker; F-H13 |
| NodeFeeSettlement `Prepared` | WP3b |
| Deployment `NeedsOperator` | WP4a |
| NFT past confirmed stage (workflow-started / grant revoked) | WP5 |
| DEX same-call winner type-0/1/access-list | WP6a |
| WTT publication mismatch / tenant-started after revoke | WP6b |
| Retired exchange rows | WP13 |
| Wallet identity migration Conflict (no UI) | WP12 |
| Idempotency `InProgress` keys | WP3b challenge (money paths) |

### W3 — review and fix

- Per W2 package: correctness review + **SEC**/**CRITIC** as marked; WP9 findings enter cycle 1; ≤2 fix cycles, then a one-line verdict per lane in `round4-join-ledger.md` (a lane without a verdict is unreviewed, not done). Lanes failing cycle 2 trigger re-plan (§8).
- W3 join: build checkpoint (`azoa.sln`, SDK tsc/build, frontend typecheck; ≤2 builds, errors routed to owners); orchestrator updates `deferred.md` (§7), `plan.md` checklist, `metadata.json`, RUNBOOK; writes `round4-closeout.md`; commits `r4-w3`; offers `oasis-sleek-38` the hash at this quiet point.

## 4. Wave order and joins

```
W0  ┌ WP-SF (SurrealForge: enum + null → re-review → owner publishes) ───────────────┐
    └ WP0 0.1 prelude → {0a, 0b, 0c, 0f} → app builds → {0d, 0e} → JOIN-0 (review)  │
W1    WP1 schema + probes   ║  W0 review fixes (0a/0b/0c owners)        → JOIN-1     │
W2a   WP2 · WP3a · WP3b · WP4a · WP4b · WP8 · WP9                                     │
W2b   WP5 · WP6a · WP6b · WP7 · WP10 · WP11 · WP12 · WP13           → JOIN-2 (build) │
W3    reviews + ≤2 fix cycles per lane                               → JOIN-3 (build) │
SWEEP P0 requires WP-SF published ◄──────────────────────────────────────────────────┘
      P0–P5 → gates 1–12 → failure ledger → ≤3 batches (stop rule)
MERGE --no-ff to main → sweep on main → READY TO PUSH (owner pushes)
```

Join criteria: JOIN-0 = W0 join (§3); JOIN-1 = WP1 acceptance + W0 review verdicts; JOIN-2 = all W2 hand-backs with lane reports, hot files reconciled, build checkpoint routed; JOIN-3 = every lane has a verdict, build clean, closeout committed.

## 5. Integrated sweep (once, after JOIN-3)

**Prerequisites in order:**
- P0: SurrealForge 1.3.0 on nuget.org; remove `surrealforge-local`; delete cached `surrealforge.*\1.3.0`; restore from nuget.org.
- P1: final schema regeneration for W2 POCO deltas (1.3.0 CLI) + goldens/flowcharts.
- P2: **kept-database rehearsal**: export the dev DB (:8020) into a throwaway 3.1.4 container, run `surrealforge up` (no `--allow-destructive`), assert `ethereum_preparation_nonce` is gone and `(protocol, sender, nonce, nonce_slot)` exists (Railway parity); reap.
- P3: `.\dev-up.ps1 -Reset` once.
- P4: SDK `npm run build`.
- P5: Hardhat, Agave (`solana-test-validator`), AlgoKit LocalNet available; Stripe CLI `listen`; Identomat config ids + tunnel (OD-12).

**Gates (runbook §7 order, extended):** 1 `dotnet build azoa.sln` · 2 unit tests (incl. architecture + host-build) · 3 integration tests on :8020 (incl. Round4Gates and protocol fixtures) · 4 lane V HTTP flows · 5 SDK vitest + build · 6 frontend `npm test`, `test:browser`, lint, typecheck, build, `npm audit` · 7 E2E `node e2e/scripts/e2e-stack.mjs up` · 8 schema goldens · 9 Stripe test-mode drill (WP3a/WP3b list) · 10 Identomat live drill (OD-12) · 11 evidence-gated activation record (which production pins flip) · 12 secret scan of the final diff.

**Failure loop:** every failure goes into `round4-sweep-ledger.md`, classified by root cause, not by test. Batch 1 (class-shaped: schema drift, coercion, positional slots, fixture setup) → batch 2 (failures batch 1 unmasked) → batch 3 (interleaving and journeys). Each batch: fix all, re-run only the affected gates plus gate 1. A fourth batch needs owner approval. **Stop rule:** if batch 3 is not green on KYC, Stripe and the exactly-once fixtures, stop and re-plan.

## 6. Merge-to-main sweep

Preconditions: sweep green, or every residual red has an owner decision recorded in the sweep ledger; `round4-closeout.md` uses the closing vocabulary; `oasis-sleek-38` has merged or confirmed it has nothing pending. Steps: `git switch main` → `git merge --no-ff feat/platform-stub-closeout` → re-run gates 1–8 on `main` (if `main^{tree}` equals the swept feature tree, record the identity and re-run gates 1–3 and 8) → record **READY TO PUSH** in RUNBOOK §2. The owner pushes `main` (~60+ ahead of `upstream/main`) and the SurrealForge branch/tag. Railway stays blocked until the stale-index migration is deployed there (owner step).

## 7. Deferral ledger

Every carry-forward ends round 4 as "do (package)" or a `deferred.md` entry with evidence. Owner = Jade Zaher unless noted.

| Item | Source | Resolution | Evidence / unblock / closure check |
|---|---|---|---|
| Clawback rung 0 + uncapped pending finish | LEDGER MUST-FIX | do — WP3b | crash + concurrent fixtures; dispute drill |
| Clawback `Failed` key | LEDGER MUST-FIX | do — WP3b (OD-10) | definitive-failure fixture |
| Stale `ethereum_preparation_nonce` | ADV F-H9, OPT F8 | do — WP1 migration | P2 rehearsal |
| Ganache EIP-1898 | OPT F7 | do — WP7 Hardhat | protocol fixtures green |
| DEX activation; exchange advertisement | ADV F-H4 | do — WP6a/WP13 (OD-5) | gate 11 record |
| Webhook delivery disabled | ADV F-H10 | do — WP6a (OD-5) | drain flow |
| Delegated child credentials | ADV F-H5 | do — WP4a (OD-3, evidence-gated) | tenant-branch flows + route gate; if OD-3(a): `deferred.md` "Owner scope decisions" |
| Governance gates | ADV F-H6 | do — WP10 (OD-7) | race fixtures; post-sweep verdict line |
| Tinyman / Jupiter fixtures | LEDGER, OPT §4 | do — WP7 | fixture green, else pin stays off |
| WTT six-route fixtures | LEDGER | do — WP7 (OD-8 #3) | fixture green, else "Built, protocol-unverified" |
| Recipient-stage Solana costs | LEDGER, ADV F-M11 | do — WP5 | cost-consent fixture |
| Legacy Wormhole + `wtt` resolver | LEDGER, ADV F-M8 | do — WP6b | legacy type deleted; resolver flow |
| Absence proof (participant messages) | LEDGER | do — WP6b | observer fixture |
| WTT stage-key readiness / rewrap | LEDGER | do — WP6b (degrade) | readiness test |
| Signature-verified External wallets | LEDGER, audit-1 M2 | do — WP4a + WP6b | proof-flag flows |
| Operator console fee control; console coverage | LEDGER, ADV F-M10 | do — WP12 | slices |
| Direct hold UI; durable recipient readiness; buy-back top-up; retired exchange rows | LEDGER from B | do — WP13 | flows + slices |
| RPC URL in chain info; D9 HttpClient; D6 | LEDGER | do — WP6a | source scan; fake-RPC flows |
| Pruning: `swap_state`, Uniswap lockfile, `RefundNodeHandler` | LEDGER | do — WP6a / WP7 / WP5 | grep empty |
| SDK DexAdapter retirement | owner decision | do — WP0f | SDK build; grep empty |
| D1 / D2 / D8 / D5 / D7 | LEDGER D-list | do — WP0c / WP3a / WP3a / WP2 / WP2 | Stripe drill; KYB configured in dev |
| D3 | LEDGER | closes at 1.3.0 bump | WP8 fixtures touching `user` links |
| D4 | LEDGER | confirm in OD-12 drill | standard-mode `{id,url}` observed |
| Transaction-scope, cross-tenant, route gates; allow-path; interleaving | LEDGER, audits | do — WP8, WP4a | gates green |
| Integration mock debt | LEDGER | do — WP8 | only chain edge faked |
| Per-lane slice/journey specs | LEDGER | do — each lane | gates 4/6/7 |
| Lane A/C/N LOW carry-forwards | LEDGER | do — WP6b / WP6a / WP5 | re-review items |
| SurrealForge typed transaction builder, `KeyOnly`, identifier-template builder | LEDGER, review | **SurrealForge 1.4.0 backlog** (not an AZOA deferral; AZOA has working equivalents) | logged in the SurrealForge repo |
| **IDENTOMAT-WORKFLOWS** | `deferred.md` | keep as **OWNER-ACTION (pending)**; owner Jade Zaher; strip the "work implemented" claim of F-C3/F-H1/F-M3 | unblock: two configs + callback (OD-12); closure: live drill evidence in the sweep ledger |
| **IDENTOMAT-RECOVERY** | `deferred.md` | **remove** (OD-9); keep a one-line hardening note | closure: orphan + late-delete + unknown-callback fixtures green |
| **VERIFF-LIVE** (new) | OPT §3 | `deferred.md` entry unless credentials arrive | evidence: no Veriff account; unblock: test credentials; closure: live Veriff session drill |
| **SURREALFORGE-1.3.0-PUBLISH** (new, owner action) | ADV §7 | sweep prerequisite P0, recorded with owner and date | closure: package on nuget.org, AZOA restores it |
| Ethereum/Solana value-custody parity | LEDGER from B | **owner scope decision** (OD-8 #2), new `deferred.md` section "Owner scope decisions (not external blockers)" | revisit trigger: reviewed settlement contract exists |
| Railway secrets, deployed Identomat callback, Stripe deployed webhook | OPT §6 release row | owner release steps, outside the sweep | listed in RUNBOOK release checklist |

## 8. Risks and re-plan triggers

| Risk | Mitigation |
|---|---|
| 1.3.0 publish slips or the enum/null change shifts semantics for an AZOA store | WP0–W3 run on the local pack; WP8 round-trip fixtures; 1.3.1 if needed |
| Test projects carry hundreds of errors (+39k lines, guessed signatures) | WP0d/WP0e split; ≤4 builds; production never bent to fit tests |
| ~40 chain-SDK guesses compile but are wire-wrong | protocol fixtures decide; production pins stay off per chain; label "Built, protocol-unverified" |
| Index audit finds a money invariant with no unique index | fixed in W1 by POCO attribute; a design gap blocks only that lane |
| Hot-file collisions with up to 8 concurrent agents | Edit-only, owned regions, orchestrator reconciliation at joins |
| KYB gate (OD-4) breaks existing tenant flows and fixtures | WP8 KYB-approved tenant builder; WP2 owns the gate fixtures |
| Webhook enablement leaks egress in e2e | lane V hermetic egress; e2e targets fakes only |
| Hardhat `finalized`/`safe` semantics under automine | WP7 asserts tag behavior in the fixture preamble |
| Stripe test-mode Connect onboarding needs a browser step | owner joins gate 9 for one onboarding |
| Identomat billing/time | OD-12 caps at ~8 sessions |
| Cost: ~17 agents, opus-heavy | W2a/W2b stagger; sonnet for mechanical slices; single-sweep discipline |
| Static review keeps missing runtime classes (F5 fell out of one query) | OD-2 probes; allow-path fixtures for every fence |

**Re-plan if:** (1) 1.3.0 is not published by JOIN-3; (2) W0 is not compile-clean within its build caps; (3) WP1 finds a money-path uniqueness invariant that needs redesign; (4) any lane fails its second fix cycle; (5) WP9 finds a new fail-closed class with >10 sites; (6) the owner picks OD-3(a)/(b), OD-5 refusals-only or OD-11 = no (packages shrink; ledger rows move to `deferred.md` "Owner scope decisions"); (7) sweep batch 3 is red on KYC, Stripe or exactly-once.

## 9. For the adversarial plan reviewer

Attack first: (a) the disjointness of W2 globs (especially `Providers/Stores/Surreal/**`, the three provider cores, `Services/Fiat/**` split between WP3a/WP3b, `Services/Governance/**` split between WP3b/WP10); (b) whether fixing design-bearing compile errors in W0 (WP0b/WP0c) front-loads unreviewed money/wire logic; (c) whether OD-2 is still "one sweep"; (d) whether evidence-gated activation is a deferral in disguise; (e) any carry-forward in `round3-join-ledger.md` or either assessment missing from §7.
