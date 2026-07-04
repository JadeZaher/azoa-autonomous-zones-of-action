---
type: track-plan
track: bridge-safety-hardening
status: pending
created: 2026-07-02
---

# Bridge Safety Hardening — Plan

Phased build order for [spec.md](spec.md). **This track is fully local** — no
external platform credentials, accounts, or API keys are required to build or
verify it (see §External dependencies). It ships with `BridgeRealValueEnabled`
**off**; the deploy-time retrieval checklist below is what's owed *later*, when
the flag is flipped for a real network.

**Spec staleness note (2026-07-02 re-audit):** the spec was written 2026-06-21.
Since then, api-safety-hardening tasks 14/15 landed `ReconciliationHostedService`
+ `ReconciliationService.ReconcileBridgeAsync` (periodic sweep over
`GetNonTerminalBridgeIdsAsync`), and the `ConsumedVaaLedger` POCO + `TryInsertConsumedVaaAsync`
(UNIQUE on canonical digest + emitter/sequence triple) exist in
`SurrealBridgeStore`. So AC2 and AC4 are **partially or fully delivered** —
Phase 0 confirms instead of assuming, and the remaining build work concentrates
on AC1 (atomicity windows), AC5 (kill switch — confirmed absent from code), and
the AC3 test matrix.

## External dependencies — what to retrieve vs. fully local

**To BUILD this track: nothing external.** All work is C# + SurrealDB + tests:

- Unit tests mock `IWormholeAdapter` / `IBlockchainProvider` (existing
  `WormholeAdapterTests` pattern); crash/replay tests exercise the store + service
  seams directly.
- Integration tests use the existing podman SurrealDB (per-class namespace
  isolation, established harness).
- The Guardian REST API (`https://api.wormholescan.io`) is public/keyless and is
  **not** hit by tests (adapter is mocked).

**Deploy-time retrieval checklist** (owed before `BridgeRealValueEnabled=true`
on testnet/mainnet — record here, execute later, NOT in this track):

| Item | Where it goes | How to get it |
|---|---|---|
| Wormhole Guardian set (ordered addresses, set index) | `Blockchain:Wormhole:GuardianSets` in env appsettings | Public — official Wormhole docs / core-contract `getGuardianSet`; mainnet is a 19-guardian set (quorum 13 already matches `MinGuardianSignatures`); testnet is a single guardian. Today only the dev placeholder `"0": ["0xbeFA…FBe"]` is configured. |
| `ExpectedGuardianSetSize` per network | same section | Same source as above (enables BFT quorum math). |
| Bridge vault addresses | `Blockchain:Wormhole:BridgeVaults` (all three are `""` today) | **Provisioned, not retrieved** — these are our own escrow accounts created on each chain (or the Wormhole token-bridge contract addresses if going trustless). Belongs to the real-bridge-primitives follow-up track. |
| Hardened RPC endpoints (optional) | `Blockchain:Chains:*` | Public Solana RPC is rate-limited for prod; a paid RPC (e.g. Helius) is a mainnet-quality upgrade. Algorand AlgoNode/AlgoExplorer free tier is fine. Not needed for this track. |

Out of scope here (separate follow-up tracks, per the 2026-07-02 provider
audit): real `LockForBridge`/`BurnWrapped`/`VerifyBridgeProof` provider
primitives, Solana signing (deploy-stub H1), Wormhole sequence parsing from
on-chain logs. This track makes the **orchestration layer** provably safe so
those can land on solid ground.

## Phase 0 — Re-baseline the ACs against current code (audit, no code changes)

**Goal:** lock the exact remaining gap list; the spec predates the
reconciliation sweep and the consumed-VAA ledger.

1. Trace `RedeemWithVAAAsync` end-to-end and enumerate every crash window:
   fetch → `SaveVaaFetchResultAsync` → `AwaitingVAA→VAAReady` transition →
   idempotency claim → `TryInsertConsumedVaaAsync` → on-chain redeem →
   `Redeeming→Completed`. For each window: what does a replay with the same
   idempotency key do today (resume / error / duplicate)?
2. Verify `SaveVaaFetchResultAsync` persists the VAA payload AND the status
   transition in **one** conditional statement, or document the two-step gap
   (the spec's AC1 concern).
3. Audit `ReconciliationService` state coverage: every non-terminal status
   (`Initiated`, `Locked`, `AwaitingVAA`, `VAAReady`, `Redeeming`, `Reversing`)
   has a recovery decision, and recovery consults **chain truth**
   (`GetTransactionStatus`/confirmation) where a tx hash exists — mirrors the
   quest-side `ChainActionRecovery` "verify, then act" discipline. Note which
   states only get stuck-flagging vs. actual driving-to-terminal.
4. Confirm the `consumed_vaa_ledger` UNIQUE index exists in the **generated**
   `.surql` goldens (regen via schema scanner if touched — never hand-edit,
   per [[quest-run-status-inside-constraint-gap]]).
5. Check idempotency-key scoping: `BridgeController` accepts a client
   `Idempotency-Key` header verbatim — confirm claims are scoped so one
   avatar's key cannot collide with / replay another avatar's operation.
   If unscoped, that becomes a Phase B fix.
6. **Write findings into the table below before Phase A.**

### Phase 0 findings (filled 2026-07-02, 3-agent audit)

| # | Concern | Status today | Fix needed? | Phase |
|---|---|---|---|---|
| 1 | fetch/save/transition atomicity | GAP — `SaveVaaFetchResultAsync` (SurrealBridgeStore.cs:322-332) co-writes payload+status in ONE statement but has NO `WHERE status='AwaitingVAA'` predicate (unconditional overwrite; every other transition uses the WHERE guard) | Yes | B |
| 2 | claim→consume→redeem crash replay | GAP — `TryClaimAsync` Won=false/InProgress → hard error, NO resume path (CrossChainBridgeService.cs:171-187). Crash windows: claim→transition (poisoned key), consume→on-chain (replay hits "VAA already consumed" → FailRedeem with NO mint), on-chain→Completed (stuck Redeeming, no tx hash to probe). Consumed-VAA row = reliable point-of-no-return marker (inserted before on-chain call) | Yes | B |
| 3 | reconciliation state coverage | PARTIAL — sweep is read-only (never double-submits ✓); BUT: `Locked` self-transitions `Locked→Locked` no-op forever (SelectBridgeProbe, ReconciliationService.cs:288-289); `TryResolveProvider` hardcodes `ChainNetwork.Devnet` (:718 — network not persisted on BridgeTx); trusted lock-ok/mint-fail lands `Failed` w/ LockTxHash = invisible to sweep (locked funds, no surface); orphaned InProgress idempotency keys never settled; `Reversing`/`AwaitingVAA` stuck-flag-only is BY DESIGN (keep) | Yes | B |
| 4 | ledger UNIQUE in goldens | OK — `consumed_vaa_digest` + `consumed_vaa_emitter_address_sequence` both UNIQUE in golden .surql (:41-49), all fields `ASSERT != NONE`, emitter canonicalized `^[0-9a-f]{64}$`, POCO `[Index]` attrs match exactly | No | — |
| 5 | idempotency-key avatar scoping | GAP — client `Idempotency-Key` used VERBATIM (BridgeController.cs:200-209 → service :164-166, :434-437, :692-694); avatars A+B with same key collide on one claim row. Derived fallback keys ARE avatar-scoped (embed avatarId / tx.Id) | Yes | B |
| 6 | AC5 kill switch | ABSENT — no flag/options/gate anywhere. Coverage needed: exactly `InitiateBridgeAsync`/`RedeemWithVAAAsync`/`ReverseBridgeAsync`; `BridgeController` is the SOLE production consumer of `ICrossChainBridgeService` (no quest/MCP/saga/reconciliation callers; reconciliation goes store-direct) | Yes | A |
| 7 | idempotency claim mechanism | OK — DB UNIQUE on `key` + CREATE INSERT-wins, deterministic SHA-256 record-id, per-statement IsOk check (SurrealIdempotencyLedger.cs:80-160) | No | — |

## Phase A — AC5: `BridgeRealValueEnabled` kill switch

**Goal:** a single, service-seam gate on real-value movement; off by default.

1. New `BridgeOptions` (section `Blockchain:Bridge`) with
   `RealValueEnabled = false` default; bind via `IOptions` in `Program.cs`
   (config-driven per [[config-driven-calls]]).
2. Gate inside `CrossChainBridgeService` (NOT only the controller — quest
   nodes / future saga callers must hit the same wall): initiate (trusted +
   wormhole), redeem, and reverse refuse with an explicit, typed error when
   the resolved provider is non-simulated and the flag is false.
   `SimulatedBlockchainProvider` routes stay allowed so dev/demo flows keep
   working.
3. The refusal must be **pre-persist** (no bridge row created for a refused
   initiate) and pre-on-chain for redeem/reverse of pre-existing rows.
4. Wire the flag into `GET /api/bridge/routes` response so the frontend can
   render "test mode" honestly.
5. appsettings: base file `false`; no env file sets it `true` in this track.

## Phase B — AC1/AC2/AC3 hardening fixes

**Goal:** close every gap the Phase 0 table confirmed. Expected shape
(adjust to findings; apply ALL fixes, defer test runs to Phase D per the
run-once policy):

1. **Atomic VAA save** — fold VAA payload persist + `AwaitingVAA→VAAReady`
   into one conditional `UPDATE … WHERE Status=AwaitingVAA` (single-statement
   atomicity, same pattern as `TryTransitionBridgeStatusAsync`). A
   fetch-succeeds/save-fails crash then leaves clean `AwaitingVAA` and the
   VAA is simply re-fetched (digest is deterministic → same downstream keys).
2. **Recoverable redeem** — on replay where the idempotency claim already
   exists: resume by inspecting bridge status + consumed-ledger presence and
   converge to the single correct outcome (complete, or report
   already-completed) instead of erroring or double-submitting. The consumed
   ledger insert stays BEFORE the on-chain call (fail-closed: prefer a
   stranded-but-reconcilable row over a double-redeem).
3. **Reconciliation completeness** — extend `ReconciliationService` so any
   state Phase 0 found only stuck-flagged is actually driven to terminal
   using chain truth where a tx hash exists; park (flag) only genuinely
   ambiguous cases. Reuse `ChainActionRecovery.Decide` semantics.
4. **Idempotency-key scoping** — if Phase 0 #5 found client keys unscoped,
   namespace claims by authenticated avatar (`{avatarId}:{clientKey}`).
5. **Trusted-mode lock→mint failure** — keep "manual intervention" as the
   terminal message BUT ensure the row lands in a reconcilable state the
   sweep picks up and re-verifies against chain truth (no silent Failed with
   funds locked). Full compensation/unlock is provider-primitive work —
   out of scope; the state must at least be honest and swept.

## Phase C — AC1/AC3/AC4 test matrix (author all; run in Phase D)

xUnit + FluentAssertions + Moq, existing builder patterns. Service-level with
mocked adapter/providers unless noted:

1. Concurrent double-redeem: two parallel `RedeemWithVAAAsync` for the same
   VAA → exactly one on-chain submit; loser gets a deterministic
   already-redeemed outcome. (Integration variant against real SurrealDB for
   the UNIQUE-constraint race.)
2. Crash replay at EACH Phase 0 window: kill after claim, after consume-insert,
   after on-chain submit (before Completed) → replay converges, never a second
   on-chain call.
3. Replayed initiate (same content → same derived key; and same explicit
   client key) → one bridge row, same response.
4. Reverse replay + reverse-after-partial (burn submitted, transition lost).
5. Digest consistency: service-side digest == `WormholeAdapter.ComputeVaaDigest`
   for identical VAA bytes; differing base64 encodings of the same bytes
   produce the same digest (decode-then-hash canonicalization).
6. Cross-avatar key isolation (post Phase B #4): same client key, two avatars
   → two independent claims.
7. Reconciliation: seed one stuck row per non-terminal state with mocked
   confirmations (Confirmed / FailedOnChain / Pending) → sweep drives to the
   documented terminal state or flags; assert no double-submit from the sweep.
8. Kill switch: flag off + live provider → typed refusal, no row, no provider
   call; flag off + simulated → allowed; flag on → allowed.

## Phase D — Single verification sweep + AC6 security review

1. `dotnet build` — zero new warnings vs the 28-warning baseline
   ([[build-warning-baseline-2026-06-16]]).
2. `dotnet test` — full suite once (unit + integration), per the
   run-once-at-the-end policy. Iterate fixes from the full picture, then one
   confirmation sweep.
3. **Security review in a separate lane** (security-reviewer / fresh-context
   pass — no self-approval): scope = malicious/oversized VAA bytes, digest
   malleability, idempotency-key abuse (header injection, cross-user replay),
   status-transition races, kill-switch bypass routes (quest nodes, MCP,
   reconciliation sweep itself), and the financial rate-limit policy actually
   covering all bridge endpoints. Findings triaged: fix-now vs. tracked.
4. Verify Swagger still lists bridge endpoints (quality gate #3).

## Phase E — Close-out

1. Tick spec ACs; mark track shipped in `conductor/tracks.md`.
2. Update `PROVIDERS.md` bridge/safety section (flag, recovery semantics) and
   the [[bridge-unsafe-pre-launch]] memory: service layer proven; residual
   risk = provider primitives (link follow-up tracks).
3. Confirm the deploy-time retrieval checklist above is copied into
   `conductor/DEPLOY-STEPS-TODO.md` (or equivalent) so it isn't lost.

## Risk register

| Risk | Likelihood | Mitigation |
|---|---|---|
| SurrealDB single-statement atomicity assumption wrong for multi-field UPDATE | low | integration test the race directly (Phase C #1/#2 against real DB) |
| Kill switch misses a call path (quest Bridge node lands later) | med | gate at the SERVICE seam + security-review bypass hunt (Phase D #3) |
| Reconciliation sweep double-submits while redeem in flight | med | sweep uses the same conditional-transition + consumed-ledger claims as the foreground path (Phase B #3, test C #7) |
| Idempotency scoping change breaks existing tests' key expectations | low | Phase 0 #5 documents current shape first; adjust builders once |
| Spec/code drift (more has shipped than the spec knows) | confirmed | Phase 0 re-baseline gate before any code |

## Rollback

Every phase is additive or behind the (default-off) flag. The kill switch can
ship alone (Phase A is independently valuable). Atomicity fixes are
store/service-local with no schema change expected except possibly a regen'd
golden; reverting a phase = reverting its commits, no data migration.
