---
type: spec
track: final-hardening-cutover
created: 2026-07-05
status: shipped
shipped: 2026-07-05
phase_h_shipped: 2026-07-06
archived: 2026-07-06
reopened: 2026-07-05
reopened_reason: >-
  Phase H (Alpha Gate) added after two independent fresh-eyes audits surfaced 8
  alpha-blocking gaps (simulated-mode prod guard, admin-token mint, frontend
  API-URL build-bake, phantom backup/restore scripts behind the G5 gate, missing
  version/CHANGELOG, no CI, un-root-caused value-path test failures, doc-drift).
supersedes:
  - quest-value-engine-expressiveness
  - bridge-safety-hardening
  - project-asset-fractionalization
  - star-odk-ecosystem-tree
  - durable-saga-orchestration
  - data-backfill-migrations
  - surreal-linq-adoption-sweep
  - surrealdb-major-upgrade
  - blockchain-recovery-and-portable-wallets
---

# Track: final-hardening-cutover

## Goal

**One final track that edges out ALL remaining implementation, so that when it
ships the only work left is: deploy to Railway + provision secrets/config.**

This track absorbs every genuine *code* gap still open across the previously-active
tracks and the `DEPLOY-STEPS-TODO.md` registry. It is the last engineering track
before launch. Its acceptance is deliberately strong: **no stubbed value path, no
half-built feature, no inert engine, no stale bookkeeping.** Anything that remains
after this ships must be an *operator action* (a secret to set, a config to flip,
a `railway up`), never a line of code to write.

Two classes of work are explicitly **out of scope of the code** and moved to the
operator guide (`docs/NODE-HOST.md`): (a) secret/credential provisioning, (b) the
Railway/host deploy itself, and (c) external trust-root config (Wormhole guardian
sets). This track's job is to make all three *sufficient* — i.e. once an operator
does them, the system is correct and complete with no further engineering.

Greenfield pre-launch ([greenfield-prelaunch-no-compat]): no live data, no compat
shims — prefer clean cutover over migration.

## Scope decision (user, 2026-07-05)

The user chose **"include everything remaining"** and **"implement real bridge
primitives now"**. So this track does the maximal cut: real cross-chain value
primitives, all quest expressiveness features, all custody hardening, plus every
correctness/wiring/hygiene item. The only thing that still gates real *mainnet*
value after this ships is operator guardian-set provisioning + the mainnet flip —
both operator actions documented in NODE-HOST.

## Phases

Phases are ordered by risk: correctness blockers first (a shipped feature that
doesn't run is worse than a missing one), then value primitives, then features,
then hygiene, then the doc/bookkeeping close-out.

---

### Phase A — Correctness blockers (things shipped but broken)

**A1. Durable quests are inert under shipped config — FIX.**
`Sagas:Enabled=false` (ADR-0001, premised on "zero consumers") but the durable
quest engine IS a saga consumer: `QuestManager` enqueues run nodes via
`ISagaStore.EnqueueNextStepAsync` under `QuestWorkflowSaga.Name`, and dispatch
happens ONLY in `SagaProcessorHostedService`, which self-disables. Result: a
durable run activates, enqueues its entry node, and never executes. Unit tests
miss it (they drive the handler directly). See [[durable-quests-inert-sagas-disabled]].
- Update/supersede `docs/adr/ADR-0001-sagas-disabled-prelaunch.md`.
- Default `Sagas:Enabled=true` (or make it a hard, boot-checked prerequisite when
  any quest workflow is registered).
- **Add an API-level integration test**: start a workflow run through the HTTP
  surface with the hosted service on; assert the entry node actually executes and
  the run advances. This is the test that would have caught A1.

**A2. Warning drift 28 → 68 → 25 (RESET 2026-07-05).** The tracked baseline
([[build-warning-baseline-2026-06-16]]) was 28; drift peaked at 68 (0 errors). Phase-A2
swept it: **NU1903 (AutoMapper high-severity vuln) fixed by upgrading 12.0.1 → 15.1.3**
(lowest version clearing GHSA-rvv3-g6hj-g44x; `AddAutoMapper`/`MapperConfiguration`
call-sites migrated to the 15.x API), plus every owned CS86xx/CS0618/ASPDEPR005/IDE0065/
IDE0051/IDE0052 warning fixed at source (no blanket `<NoWarn>` added). **New baseline: 25**
(build-summary count), all residual being the reserved crypto/value files deferred to
Wave 2 — `SolanaProvider.cs` (10), `CrossChainBridgeService.cs` (4), `WalletKeyService.cs`
(1, unused `AlgorandAddressFromPublicKey`) — plus 1 benign `EnableGenerateDocumentationFile`
MSBuild suggestion. Zero NEW warnings introduced; zero warnings remain outside those
Wave-2 files. Wave 2 clears the crypto-file residue when it rewrites the signing/bridge path.

---

### Phase B — Real cross-chain value primitives (bridge + signing)

Absorbs `bridge-safety-hardening` (mostly landed) + the remaining `DEPLOY-TODO`
signing stubs (H1) + `blockchain-recovery-and-portable-wallets` P7.

**B1. Real Solana signing (H1).** `SolanaTransactionSigner` is an explicit
fail-loud stub. Implement real Ed25519 Solana signing behind the existing
`ITransactionSigner` seam (mirror `AlgorandTransactionSigner`), plus real Solana
keygen in `WalletKeyService` (replace the Ed25519 placeholder path).

**B2. Real bridge primitives (replace logged-only).** Today `LockForBridgeAsync`
/ `BurnWrappedAsync` are logged-only and `VerifyBridgeProofAsync` returns `true`
unconditionally at the provider level (dead on the hardened path, but must not
lie). Implement real lock/burn transactions through the signer seam, and make the
provider-level proof verify (or delete the always-true method so only the
`WormholeAdapter`/`Secp256k1VaaSignatureVerifier` path exists — no vestigial
always-true verifier anywhere).

**B3. Bridge-safety-hardening close-out.** The kill switch (`RealValueEnabled`),
WHERE-guarded VAA save, avatar-scoped idempotency keys, and crash-resume paths are
already in the tree (commit `3b5ff29`). Finish: the reconciliation-coverage gaps
(Locked no-op loop, hardcoded Devnet, invisible lock-ok/mint-fail — Phase-0
finding #3), and the integrated exactly-once/replay verification sweep.

**B4. Reconcile-before-retry quest wiring (P7).** Foundation shipped
(`ChainConfirmation`, `ChainActionRecovery` decision table, provider
`GetTransactionConfirmationAsync`). Owed: record broadcast `TxHash` on
`QuestNodeExecution`; on chain-action node failure probe chain truth and branch
advance-reconciled / retry / park (`AwaitingReconciliation`); a quest-node
reconciliation sweep + manual re-probe endpoint. **Prevents double-mint on the
Grant-bounty path.**

**B5. Custody hardening (P1 + P2 follow-ups).**
- `WalletKeyService.DecryptPrivateKeyBytes` returning a zeroable `byte[]` so the
  cleartext key never materializes as an immutable string; `KeyCustodyService`
  switches to it and drops the hex-string intermediate.
- Live key-rotation orchestration on top of the shipped `RewrapAsync` primitive:
  dual-key read window, batch re-wrap of every wallet, rollback on partial
  failure, admin endpoint.

*(B3 KMS/HSM custody and B6 mainnet gate from DEPLOY-TODO are OPERATOR items → NODE-HOST, not code. The `IKeyCustodyService` swap seam is already in place so no engineering remains here — only wiring a `KmsKeyCustodyService` impl at deploy time IF the operator chooses KMS.)*

---

### Phase C — Quest value-engine expressiveness (F2–F7)

Absorbs all of `quest-value-engine-expressiveness` except F1 (shipped). Completes
the "anyone can define their own economic value engine" goal.

- **F2 — OnFailure edge arm.** Enum + POCO done. Remaining: skip-loop logic, DAG
  validator, durable engine seam, edge input surfaces.
- **F3 — `quest.emit` webhook events** (generalize the shipped consent outbox to
  arbitrary quest events).
- **F4 — `QuestDependency` enforcement at run start** (`CheckDependenciesAsync`).
- **F5 — opt-in Holon AssetType/metadata registry** (`HolonTypeRegistry` table +
  manager/controller).
- **F6 — unpublish TOCTOU guard** (optimistic concurrency on publish/run-start).
- **F7 — SDK + builder mirrors** (thin: `node-config.ts` + quest-builder).

---

### Phase D — Flagship economic flow (fractionalization + ecosystem tree)

Absorbs `project-asset-fractionalization` + `star-odk-ecosystem-tree`. Depends on
Phase B (real value) + Phase C (F-features). This is the ArdaNova deliverable the
whole ardanova-rails initiative exists for; all its composable rails are shipped,
so it is assembly.

- **D1. `Bridge` + `Back` Tier-2 quest nodes** + 4 builder presets + one canonical
  parameterized fractionalization quest template (peg math stays tenant-side, via
  `Emit`).
- **D2. STAR-ODK ecosystem tree.** `Ecosystem`/`EcosystemNode` on STARODK,
  `AddDappSeriesAsync`/`GetEcosystemAsync`, tree-walking multi-dApp codegen, and
  the React Flow tree UI (reuse the quest-visual-builder canvas).

---

### Phase E — Data + query hygiene

- **E1. surreal-linq-adoption-sweep** (S). Migrate the ~4 raw production `SELECT`
  sites (`SurrealQuestStore`, `SurrealNftStore`) to typed/LINQ tiers; keep the raw
  escape hatch for writes/RELATE/DDL. Add the `§query-surface` policy doc.
- **E2. data-backfill-migrations.** `IBackfill` module primitive + `azoa-surreal
  backfill list/apply` CLI + `data_migration` ledger. First consumer: F6 FK string
  → `record<table>` rewrite. **Refresh the stale `packages/Azoa.SurrealDb.Schema/`
  paths in the spec first** (that dir no longer exists). Greenfield ⇒ low urgency
  but included so no data-rewrite path is left unbuilt.
- **E3. surrealdb-major-upgrade close-out (~70% done).** Compose already pins
  v3.1.4 and `DECISION.md` is landed. Remaining: the ~5 client integration tests
  under 3.x strict-namespace, the G1 crash-durability re-run, and the SurrealQL
  wire-surface audit. *(The RUNBOOK/Railway v1.5.4 → v3.1.4 doc + deploy sweep is
  folded into NODE-HOST / the operator deploy, not code.)*

---

### Phase F — Saga operator surface (rescoped durable-saga-orchestration)

Phase 1 (the reusable saga skeleton) is delivered and has a live consumer (quest
workflow). Phase 2 (bridge-as-saga-consumer) is **dropped** — the bridge was
hardened directly in Phase B, making the saga rewrite redundant (recorded
decision). Remaining, small:
- **F1. Operator/dead-letter surface**: requeue, cancel, and dead-letter inspection
  for parked/failed saga steps (also serves the quest-node reconciliation sweep in
  B4). Close the track as "skeleton delivered + operator surface added; bridge
  adoption intentionally not pursued."

---

### Phase G — Doc + bookkeeping close-out (makes the catalog true)

- **G1. Mark the two already-done tracks shipped.** `user-sovereign-identity` and
  `tenant-consent-delegation` are code-complete with their "owed" security review
  *done and remediated* in commit `10e5dad` (2026-06-22). Flip their ACs, mark
  `[x]` in `tracks.md`, and archive them (see "Restructuring" below).
- **G2. Fold ALL operator/deploy tasks into `docs/NODE-HOST.md`** in a generic
  operator voice (NOT as track TODOs). Source items from `DEPLOY-STEPS-TODO.md`:
  B3 (KMS/HSM custody option), B6 (mainnet enablement checklist), P3 (platform
  account ALGO fee-funding + low-balance alerting), P4 (KYC provider secrets +
  enabling Veriff), P5 wallet-generate KYC decision, P6 (first-tenant onboarding
  execution), guardian-set provisioning (→ GUARDIAN-SET-SETUP), the Railway
  v3.1.4 deploy + version sweep, and H5 (brand-leak CI guard). Each expressed as
  "what the operator does," cross-linked, no AZOA-internal track names.
- **G3. Retire `DEPLOY-STEPS-TODO.md`** once its code items are closed by this
  track and its operator items live in NODE-HOST — leave a one-line pointer.
- **G4. Reconcile `tracks.md`**: this track is the only active one; everything else
  archived. Final single-pass build + full test sweep green, warnings at (new)
  baseline.
- **G5. frontend-demo-harness audit (non-blocking).** The harness is effectively
  built — all 16 dashboard pages + a test-runner page exist (assembled incrementally
  by other tracks). Do a light audit against the functional-test matrix and add any
  page missing for a shipped feature (e.g. a workflow-run driver page). NOT a
  rebuild and NOT launch-blocking; if a gap is large, note it for post-launch
  rather than expanding this track.

## Acceptance criteria

1. Durable quest run executes end-to-end through the HTTP API with the hosted
   service on (A1), proven by a new integration test.
2. Real Solana signing + real bridge lock/burn/verify; no always-true verifier
   and no logged-only value primitive remains in the tree (B1, B2).
3. Bridge exactly-once/replay verified; reconciliation coverage gaps closed (B3).
4. Chain-action nodes reconcile-before-retry; no double-mint path (B4).
5. Custody: zeroable `byte[]` decrypt + live rotation orchestration (B5).
6. Quest F2–F7 all shipped and mirrored in SDK + builder (Phase C).
7. Fractionalization flow + ecosystem tree runnable end-to-end (Phase D).
8. Raw-SELECT sweep done; backfill primitive built; 3.x upgrade closed (Phase E).
9. Saga operator surface exists (Phase F).
10. `docs/NODE-HOST.md` contains every operator/deploy/secret task in generic
    voice; `DEPLOY-STEPS-TODO.md` retired; `tracks.md` shows only this track active
    then archived on ship (Phase G).
11. **Terminal state: the only remaining launch actions are `railway up` +
    provision secrets/guardian sets — zero code.**
12. Single final sweep: `dotnet build` 0 errors / no new warnings; full unit +
    integration suites green.

## Non-goals

- The Railway deploy itself and secret/guardian-set provisioning (operator actions,
  documented in NODE-HOST — that's the whole point).
- `dotnet-client-sdk` — stays **TABLED** per the user's 2026-06-18 decision; a C#
  client is a post-launch convenience for ArdaNova, not a launch blocker. It is the
  ONE active track NOT absorbed here (archived as tabled, not as done).
- H2 soulbound clawback-revoke — post-launch follow-up (mint path shipped; revoke
  is pure follow-on since the platform already holds the clawback role).

## Restructuring performed when this track was created (2026-07-05)

- Absorbed (archived, work moved into this spec): quest-value-engine-expressiveness,
  bridge-safety-hardening, project-asset-fractionalization, star-odk-ecosystem-tree,
  durable-saga-orchestration, data-backfill-migrations, surreal-linq-adoption-sweep,
  surrealdb-major-upgrade, blockchain-recovery-and-portable-wallets.
- Archived as already-shipped (bookkeeping): user-sovereign-identity,
  tenant-consent-delegation.
- Archived as tabled (unchanged): dotnet-client-sdk.
- Result: `final-hardening-cutover` is the sole active track.

---

# Phase H — Alpha Gate (added 2026-07-05, track re-opened)

## Why this phase exists

After the track shipped, two independent fresh-eyes audits (an architecture pass
and a gap/operator-lifecycle pass) reviewed the tree. Verdict: **legitimate,
unusually honest alpha** — every layer graded SOLID; the fail-closed discipline
holds end-to-end; the previously-flagged inert-saga regression is genuinely fixed.
But the "only `railway up` remains" claim was *happy-path* true. It hid the
**operator lifecycle**: guard the one un-guarded dangerous default, mint an admin,
point the UI at the right API, recover data, and know what version shipped.

Two audit items were **already resolved and are NOT in scope** (the auditors read
stale docs — itself finding H8): `Sagas:Enabled=true` is the deliberate default
with a boot guard; the G1 durability gate is live and green on 3.1.4. These
corrections are the *evidence* for the doc-drift sweep, not new work.

This phase closes the **8 alpha-blockers**. It does not re-open v1-scope items
(KMS/HSM custody, distributed rate limiting, real Solana/Wormhole/ETH value routes,
god-object splits) — those remain honest post-alpha follow-ups (see §H-followups).

## Scope (the 8 blockers)

- **H1. Simulated-mode production guard (S).** `BlockchainProviderFactory`
  (`IsSimulatedMode`, provider factory ctor / `GetProvider`) short-circuits **every**
  chain to `SimulatedBlockchainProvider` (fake `sim:tx:` settlement) whenever
  `Blockchain:Mode=Simulated`, with **no `IsProduction()` guard** — the one
  dangerous default that isn't boot-guarded while secrets/CORS/durability/debug all
  are. Add a fail-fast: in a Production environment, `IsSimulatedMode ⇒ throw` at
  construction/boot (mirror the Program.cs secret/CORS guard pattern), so simulated
  settlement can never leak into prod. Cover with a test.
- **H2. Admin-token mint path (S–M).** `AvatarManager.GenerateJwt` emits no
  role/scope claim, so NODE-HOST §8.9 operator onboarding is **not executable** —
  there is no way to mint the first `operator:admin` (`Core/AzoaScopes.cs:33`)
  principal. Provide a real bootstrap: a seed-admin config (env-driven, fail-closed,
  Dev/first-boot only) **or** a one-shot CLI/endpoint that stamps the operator scope,
  wired so the documented onboarding actually works. Fail-closed if the seed secret
  is absent in Production. (Interim `role=Admin` claim keeps working; this is the
  scoped, documented path.) *As-built clarification: `AdminBootstrap:SeedSecret` is
  an **arming toggle**, not a challenge the caller presents — its presence (with a
  matching `SeedEmail`) arms the stamp; the guaranteed property is that no principal
  receives `operator:admin` unless the operator has configured it. Fail-closed at
  boot and at mint on partial config in Production.*
- **H3. Frontend API-URL build-bake + stale SDK ref (S).** `frontend/Dockerfile`
  bakes `NEXT_PUBLIC_API_URL` at **`next build`** time (`ENV` at :28/:42), so the
  runtime environment variable is dead — an operator can't repoint the UI without a
  rebuild. Make the API URL runtime-resolvable (runtime config / entrypoint
  substitution / server-side proxy). Also fix the **stale `@azoa/wallet-sdk`**
  reference in `next.config.js:26` (`transpilePackages`) — the SDK is `@azoa/sdk`
  from `sdk/azoa-wallet` per [[sdk-renamed-oasis-sdk]].
- **H4. backup.ps1 / restore.ps1 are phantom — the G5 gate references files that
  don't exist (M).** `scripts/surrealdb/` does **not exist** on disk, yet
  `tests/AZOA.WebAPI.IntegrationTests/Gates/G5_RestoreDrillTest.cs` and the
  surrealdb-migration sign-off assert a real `backup.ps1`→wipe→`restore.ps1`
  round-trip. A gate that asserts phantom files is worse than no gate — it *lies*.
  **Write real `scripts/surrealdb/backup.ps1` + `restore.ps1`** (wrap
  `surreal export`/`surreal import` via `docker exec`/`podman exec` with the
  `Find-ContainerRuntime` auto-detect the sign-off describes) so the G5 drill is a
  true SHA-256 round-trip, giving the operator an actual disaster-recovery path. If —
  and only if — real scripts prove infeasible this pass, honestly gut the gate
  rather than leave it asserting a lie.
- **H5. Version stamps + CHANGELOG (S).** Nothing pins a tag against. Add an
  informational assembly version to the WebAPI, ensure `sdk/azoa-wallet`
  `package.json` carries a real `version`, and add a root `CHANGELOG.md` capturing
  the alpha. (`LICENSE` already exists at root as Apache-2.0; the SDK already
  declares `"license": "Apache-2.0"` — verify a `LICENSE` file ships **inside** the
  SDK package so the npm artifact isn't license-less, add if missing.)
- **H6. Minimal CI (S).** There is **zero `.github/`**. Add one workflow:
  `dotnet build` (0 errors) + unit tests + SDK `vitest`, on push/PR. Not a
  release/deploy pipeline — just the green-bar guard so regressions surface.
- **H7. Bucket-D value-path triage (M).** The un-root-caused value-path
  integration failures (`Holon.Interact` / `Mint` / `Exchange`, `STARODK.Deploy`)
  in the documented 37-failure integration tail: **investigate, then fix-or-accept
  with written evidence** per finding. No silent acceptance — each stays failing
  only with a root-caused reason recorded (test-design vs product bug).
- **H8. Doc-drift sweep (S).** Stale operator docs actively mislead careful readers
  (both auditors were misled — the proof this is real). Sweep: **`.NET 8`→`.NET 10`**
  everywhere (tree is already `net10.0`); RUNBOOK's SurrealDB `1.5.4`→`3.1.4`; add
  **deprecation banners at the top of** `docs/GO-TO-PROD.md` and
  `docs/RESIDUAL-RISK-RUNBOOK.md` (the latter dated 2026-05-16, citing retired
  Postgres/EF `Models/ConsumedVaaRecord.cs` + `IX_ConsumedVaas_Digest` that are now
  SurrealDB `ConsumedVaaLedger.cs` + a UNIQUE ASSERT) pointing to the authoritative
  `docs/NODE-HOST.md`; fix the dead §7 ref; refresh PASSOFF; and record the two
  audit corrections (sagas-default, G1-live) so the docs stop lying.

## Phase H acceptance criteria

H-AC1. In a Production host environment, booting with `Blockchain:Mode=Simulated`
  **throws at startup** (fail-fast); Dev/IntegrationTest still allow it. Test proves
  both directions.
H-AC2. A documented, fail-closed path mints an `operator:admin`-scoped principal;
  NODE-HOST §8.9 onboarding is executable as written.
H-AC3. Frontend API URL is resolvable **without a rebuild**; `next.config.js` no
  longer references `@azoa/wallet-sdk`.
H-AC4. `scripts/surrealdb/backup.ps1` + `restore.ps1` exist and the G5 restore-drill
  passes as a real SHA-256 round-trip (or the gate is honestly reduced — no phantom
  assertions remain).
H-AC5. WebAPI + SDK carry real version stamps; `CHANGELOG.md` exists at root; SDK
  package ships a `LICENSE`.
H-AC6. `.github/workflows/` runs build + unit + SDK vitest on push/PR, green.
H-AC7. Every Bucket-D value-path failure is root-caused with written evidence
  (fixed, or accepted-with-reason); the integration tail count is re-stated.
H-AC8. No doc claims `.NET 8` or SurrealDB `1.5.4`; GO-TO-PROD + RESIDUAL-RISK-RUNBOOK
  carry deprecation banners → NODE-HOST; the sagas-default + G1-live corrections are
  recorded.
H-AC9. Single final sweep: `dotnet build` 0 errors / no new warnings vs the
  28-warning baseline ([[build-warning-baseline-2026-06-16]]); full unit +
  integration suites green (integration tail only the H7-accounted failures).
H-AC10. **Terminal state (unchanged from AC11):** the only remaining launch action
  is `railway up` + provision secrets/guardian sets — zero code.

## §H-followups — explicitly deferred (fine for alpha, blocks v1)

KMS/HSM custody · distributed (multi-instance) rate limiting · auth-endpoint
brute-force limits · low-balance alerting hook · `ConnectWalletAsync` signature
verification (`WalletManager.cs:406-411` currently trusts caller-supplied address —
**verify** it cannot grant value-bearing capability outside the real WalletAuth
challenge flow; the *fix* is deferred, the *verification* is H7-adjacent and should
be noted) · convert-or-delete the dormant success-shaped Metaplex methods
(`SolanaProvider.CreateMetadataAccountAsync`/`UpdateMetadataAsync` — no caller today)
· god-object splits (`QuestManager.cs` ~98 KB, `CrossChainBridgeService.cs` ~59 KB)
· real Solana SPL / Wormhole sequence / ETH secp256k1 value routes (stay
`RealValueEnabled=false`, fail-closed).
