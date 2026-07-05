---
type: spec
track: final-hardening-cutover
created: 2026-07-05
status: pending
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

**A2. Warning drift 28 → 53.** The tracked baseline ([[build-warning-baseline-2026-06-16]])
was 28; current build is 53 (0 errors). Triage the +25, fix or explicitly baseline
each, and reset the recorded baseline. Zero NEW warnings after this track.

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
