---
type: lane-checkpoint
lane: C
domain: DEX (Tinyman / Jupiter / Ethereum Uniswap V3 executor)
status: source-complete-pending-review
---

# Round 3 lane C (DEX) resume checklist

Source of truth: `round3-dex-contract.md` plus amendments (cleanup, Solana exposure,
Jupiter cleanup, Ethereum allocation, finalized winner, Ethereum allowance cleanup) and
`round3-dex-source-handoff.md` (new section "Round 3 resume"). Codex lane C stopped
23:04Z mid-slice; Claude Code resumed 2026-10-16. **No build, test, lint, typecheck,
schema generation, hardhat/cargo or git was run.** Every item below is source work with
authored, unexecuted fixtures; runtime acceptance is the integrated sweep after round 4.

## Done before cutoff (independently source-accepted per handoff)

- [x] D1 Contract and six amendments (the allowance-cleanup amendment was complete).
- [x] D2–D8 Pure Tinyman/Jupiter arithmetic and codecs; quote/operation/stage/rent stores;
  Tinyman and Jupiter full module source; Ethereum executor, unsigned reservation, permit,
  quote, allowance store, preparation, signing; coordinator, `/api/dex`, recovery worker,
  SDK `api/dex.ts`; exact `DependencyUnavailable` → `Unavailable: true` mapping.

## Closed this session

- [x] C1 Ethereum submission — `Providers/Blockchain/Ethereum/EthereumProvider.DexSubmission.cs`.
  Exact saved wire only; refuses while the allocator row is canceling/terminal, the finalized
  nonce is consumed or the swap deadline elapsed; never marks the shared row
  `SubmissionAuthorized`; tolerated replay responses are no-send results.
- [x] C2 Ethereum finalized settlement — `EthereumProvider.DexSettlement.cs`,
  `EthereumDexReceipt.cs` (closed executor/pool/token/WETH log grammar), `AllowanceAfter` and
  `AllowanceRelease` evidence; revert is `FailedOnChain` with its fee; finalized
  alternate-signature winner settles as `FinalizedCandidate` with original hashes retained.
- [x] C3 Ethereum hashless exposure and nonce winner — `EthereumProvider.DexObservation.cs`:
  no signature-independent IDs, persisted plan-bound binary search over finalized blocks,
  exposed-candidate adoption, different-winner `ExpiredAbsent`, pruned history stays Pending.
- [x] C4 Ethereum lineage and capability — `EthereumProvider.DexStages.cs` (consented
  `ResetAllowance` → `ApproveExact` → `Swap`, live first-stage choice, retained allowance).
- [x] C5 Allowance cleanup consent — `Helpers/Dex/SwapCleanupContract.cs`,
  `Models/Dex/DexContracts.cs` / `DexRequests.cs`, `Services/Dex/SwapOperationService.Cleanup.cs`,
  `EthereumProvider.DexCleanup.cs`, `EthereumProvider.DexPreparation.cs` (cleanup plan read),
  Tinyman/Jupiter planners require `CloseCurrentZeroHolding`; cleanup signing ignores the swap deadline.
- [x] C6 Resource release and worker bypass — `Interfaces/IDexRetainedResourceModule.cs`,
  `Services/Dex/SwapStageCoordinator.Resources.cs`, `SwapRecoveryService.cs`,
  `SurrealEthereumDexAllowanceStore.cs` (contention self-heal), `EthereumProvider.Recovery.cs` (skip).
- [x] C7 Jupiter cleanup fixture — consistent; both responses now keyed on `cleanupTx`
  (`tests/.../JupiterProviderCleanupTests.cs`).
- [x] C8 Solana cleanup fixtures — close/reopen in the same block and missing history present
  (same theory). Rust opcodes 4/5 unchanged; no `lib.rs` edit was needed.
- [x] R1 `DexCapability` + `DescribeDexNetworkAsync` on Algorand, Solana, Ethereum (`*.DexStages.cs`).
- [x] R2 `GET /api/dex/capabilities` (`Controllers/DexController.cs`, `Services/Dex/DexModuleResolver.cs`), SDK `getDexCapabilities`.
- [x] R3 Action cutover — `Services/Actions/Handlers/SwapNodeHandler.cs`, `Services/Dex/SwapActionResolver.cs`,
  `Models/Actions/NodeConfigs.cs` (`SwapNodeConfig.Quote`), `ActionNodeOutputSchema.cs` (flat `SwapActionOutput`).
- [x] R4 Legacy cutover — deleted `SwapController`, `SwapManager`, `ISwapManager`, `IDexAdapter`,
  `JupiterDexAdapter`, `TinymanDexAdapter` (instructions-as-success builder), `JupiterConfig`,
  `SwapQuoteRequest`/`SwapExecuteRequest`/`SwapQuoteResponse`, their validators and mock tests.
- [x] R5 SDK/UI — `sdk/azoa-wallet/src/api/{client,dex,index,api-version}.ts`, `src/index.ts`,
  `src/workflow/node-config.ts`, `frontend/src/app/(dashboard)/swap/page.tsx` (capability-driven
  durable lifecycle), `components/wallet-fund-dialog.tsx`, tests page probe; SDK governed fee-schedule read/update.
- [x] R6 Fixtures (unrun): `EthereumDexSettlementTests.cs` (10 flows), `SwapActionResolverTests.cs`,
  `SwapCleanupContractTests.cs`, `DexModuleResolverTests.cs`, coordinator release theory,
  allowance self-heal (real Surreal), SDK capability/revocation/fee/workflow cases, and the actual
  pinned Uniswap + executor `EthereumDexProtocolTests.cs` with `Fixtures/Ethereum/dex-chain.mjs`.
- [x] R7 Capability matrix (below).

## DEX capability matrix (source state)

| Capability | Route / registry | Provider seam | SDK | UI |
|---|---|---|---|---|
| Discovery | `GET /api/dex/capabilities` | `DescribeDexNetworkAsync` ×3 | `getDexCapabilities` | Swap page list; test probe |
| Exact-input quote | `POST/GET /api/dex/quotes` | Tinyman V2, Jupiter-Whirlpool, Uniswap V3 executor | `quoteDex` | Swap page (unavailable = empty state) |
| Admission/replay | `/api/dex/operations` | — | `startDexSwap`, `getDexSwap` | saved-request retry |
| Custody / browser signing | `/sign`, `/external-signing`, `/external-signature` | Sign/Validate/Exposure | yes | buttons; consent + paste (no wallet adapter) |
| Submit, withdraw, cancel, refresh | lifecycle subroutes + `SwapRecoveryWorker` | Submit/Observe | yes | buttons + accounting |
| Cleanup: close holding | `/cleanup`, `/cleanup/prepare` | Tinyman `CloseOptIn`, Jupiter `CloseAta` | yes | cleanup panel |
| Cleanup: revoke allowance | same | Ethereum `RevokeAllowance` | `acceptCurrentAllowanceRevocation` | cleanup panel |
| Workflow Swap node | `SwapNodeHandler` + resolver domain `swap` | via service | `nodeConfig.swap`, `SwapActionOutput` | action builder config |
| Swap fee denomination | `PUT /api/node-governance/fee-schedule` | — | `updateNodeFeeSchedule` | transparency display; console control → R4 carry |
| Legacy `/api/swap` | retired | retired | retired | retired |

All three provider modules are structurally complete but **undeclared**, so discovery is empty
and quotes return `Unavailable` until activation after review.

## Review fixes (coordinator batch, round3-join-ledger §Lane C findings)

Source-complete, unbuilt (single sweep after round 4).

1. HIGH same-call winner: `EthereumProvider.DexObservation.cs:101` routes a winner with the same
   to/value/input but a different envelope to `DexSameCallWinner` (:198). The result is
   NeedsOperator with `ethereum-nonce-winner-same-call/1` evidence (EffectProven, ActualOutput).
   It is never ExpiredAbsent.
2. HIGH cleanup cancel: `SurrealSwapOperationStore.Stages.cs:112` returns the operation to
   CleanupReady. `.Continuation.cs:71` lets the same cleanup stage re-plan at generation+1.
3. MEDIUM nonce holes:
   - Unsigned, never-exposed reservations are released (`EthereumProvider.Preparation.cs:144`) and
     refilled first (:107). `SurrealEthereumPreparationStore.cs:46` retires and claims in one
     transaction, with transient retry.
   - Signed or exposed reservations surface `preparationKey`/`signedCandidateSaved` in the swap
     view, and the swap page has a same-nonce cancel panel.
   - The rule is documented in Ethereum `AGENTS.md` §Nonce holes.
4. MEDIUM Failed outcomes:
   - `DexResultCodes.Unsettleable` comes from the deadline (`DexPreparation.cs:51`,
     `DexSigning.cs:29`) and from cap exhaustion (`SwapStageCoordinator.cs:47`, which also releases
     the reservation). The resolver (`SwapActionResolver.cs:123`) cancels the unsent swap and
     returns Failed.
   - Admission refuses unoffered semantics (`SwapOperationService.Cleanup.cs:34`).
   - The Cancelled message is reworded (:99).
5. MEDIUM archive: escalates to NeedsOperator after 10 misses (`DexObservation.cs:168`), and
   `ArchiveStateConfirmed` gates `ReadDexDeployment` (`DexState.cs:29`).
6. M1: the chain-id error omits the URL (`EthereumProvider.cs:269`), and `DescribeDexNetworkAsync`
   returns fixed text.
7. LOW:
   - L2: exposure replay re-binds consent (`SwapOperationService.Exposure.cs:38`).
   - L3: `SwapScope` (`SwapOperationService.cs:78,114`; `DexQuoteService.cs:72`).
   - L4: `financial` limiter plus a 30 s bounded cache (`DexController.cs:20`,
     `DexModuleResolver.cs:27`).
   - L5: SDK `assertSwapOperationId`/`assertDexQuoteId` (`client.ts:2068`) on all 12 routes.
8. Allowance race: named `$allowance_released`, then a re-read that accepts the same-stage winner
   (`SurrealEthereumDexAllowanceStore.cs:128-140`).
9. `Providers/Stores/Surreal/AGENTS.md` now states the operation-row serialization invariant
   instead of "Cancelled never gains egress".
10. Fixtures:
    - The allowance Pending row is now a sequential fact that fails if the eager release is
      removed. There is also a concurrent-release fact.
    - Item 1: same-call winner (exposed and custody paths).
    - Item 2: cleanup cancel goes to CleanupReady, then re-plans.
    - Item 3: released nonce is refilled and a signed one is never released.
    - Item 4: coordinator cap theory, resolver Unsettleable→Failed and ordinary→Pending, cleanup
      semantic admission theory.
    - Also: capability cache and SDK id rejection table.

Residual, not fixed: `EthereumProvider.GetChainInfoAsync` still returns `rpcEndpoint`
(BaseAddress). It is a generic chain-info surface shared with the other providers, so the fix
belongs in a cross-provider sweep (round 4).

### Review fixes, cycle 2 (re-review: the 8 cycle-1 items passed; 4 defects introduced by the fixes)

1. MEDIUM, same-call winner pins the lane. Fixed with an operator-only settlement path.
   - Route and service: `DexOperatorController` (`POST /api/operator/dex/operations/{id}/settle-review`,
     recent operator session, `financial`) calls `SwapOperationService.SettleOperatorReviewAsync`.
   - Provider proof: the provider (`IDexOperatorSettlementModule`) re-proves the winner from
     finality (`EthereumProvider.DexOperatorSettlement.cs:16`):
     - the owner's own signature via `EthereumRpcEnvelope.ReadOwnEnvelope`;
     - the full finality proof via `DexFinality.cs:32`;
     - the closed receipt grammar;
     - `AllowanceAfter`/`AllowanceRelease` read from chain state.
   - Audit and write: `SurrealSwapOperationStore.OperatorSettlement.cs` writes the result with an
     immutable audit (operator, reason, time, hash of the replaced review).
   - Downstream acceptance:
     - history: `SwapStageCoordinator.cs:246`, `DexStages.cs:140`;
     - completion: `ProvenConfirmedStage`;
     - continuation and cleanup: `ValidJournalObservation`;
     - allowance release: `SurrealEthereumDexAllowanceStore.cs:112`, `Resources.cs:19`.
   - The worker can never write a settlement (`Observations.cs:24`). The ApproveExact variant
     settles and continues to the swap.
2. LOW, refill erased the key lock. The released row now becomes a `ReleasedRefilled` tombstone
   (`SurrealEthereumPreparationStore.cs:54`). `NonceSlot` is added and the unique index becomes
   `(protocol, sender, nonce, nonce_slot)`. `ReleasedReservation` (`Preparation.cs:130`) covers
   planning, the WTT probe and recovery, and recovery now refuses released rows (`Recovery.cs:80`).
3. LOW, hidden holes.
   - (a) The release re-runs on cancel retries (`Commands.cs:81,96`) and when the resolver observes
     `Cancelled` (`SwapActionResolver.cs:100`).
   - (b) The swap page reads the allocator row (`page.tsx:64,160,163`): `Prepared` gets the
     same-nonce cancel, `Reserved` gets a fee-free release.
4. LOW, a never-sent operation pinned the lane. A cleanup cancel returns to `Cancelled` unless some
   stage had egress (`SurrealSwapOperationStore.Stages.cs:114,143`).

Fixtures:
- Provider settlement proves, validates and completes; a different-call review is refused.
- Surreal: same-call holds the lane, then the operator settles (forged audit refused, replay
  idempotent), then a new swap claims the lane.
- Surreal tombstone refill.
- The nonce-reuse unit test extends past the refill.
- Never-sent cleanup cancel ends `Cancelled`.
- Resolver crash-retry release.

Cross-lane:
- `EthereumProvider.WttPreparation.cs:196`: lane A WTT probe; `ReleasedRefilled` reads `None`.
  `ReleaseUnsignedReservationAsync` is unchanged in name, signature and semantics.
- `EthereumProvider.Recovery.cs:80`.
- `Persistence/SurrealDb/Models/EthereumPreparation.cs`: shared allocator POCO and index.
- `EthereumRpcEnvelope.cs`: additive.
- `tests/.../TestSupport/FakeEthereumPreparationStore.cs`.

Carry-forward:
- The schema regeneration owed for `ethereum_preparation` must include the renamed index. Existing
  databases must drop the old `ethereum_preparation_nonce` index; until then, refills fail closed
  as busy.
- No frontend test harness exists for the panel.
- `BeginCleanupAsync` from a never-sent `Cancelled` operation still enters `CleanupReady`. The lane
  stays held until that cleanup is planned and cancelled, or confirmed.

## Round-4 carry-forward

- Activation: add `: IDexTransactionModule` to the three providers — after this round's
  independent adversarial review, per the handover protocol.
- Tinyman LocalNet protocol fixture — compiled pinned validator/pool-template programs are not
  captured (only Tealish source/tests at `3de3a35`); needs a pinned artifact capture.
- Jupiter/Whirlpool local-validator fixture — pinned program binaries are not captured, and a
  locally synthesized Build response is needed; recorded HTTP/RPC fixtures exist.
- Local chain for .NET Ethereum protocol fixtures (DEX here, WTT in lane A) must support EIP-1898
  block-hash params and the `finalized` tag; confirm pinned Ganache 7.9.2 or switch node.
- SDK wallet-local `TinymanAdapter`/`JupiterAdapter` (`sdk/azoa-wallet/src/dex`) predate the
  durable contract; the Jupiter adapter calls the retired `/swap/v2/quote|swap`. Retire vs.
  rebuild as an exposed-stage signing adapter is an owner decision on the AGENTS.md DexAdapter convention.
- Operator console fee-schedule control: no node-governance console or BFF route exists for any
  fee entry (`/api/node-governance` is outside the operator BFF); SDK update shipped this round.
- Legacy unused `swap_state` POCO/table: retire with schema regeneration and the least-privilege hash pin.
- Dependency audit of the Uniswap lock metadata (10 findings) in the final sweep.

## Cross-lane edits

`Program.cs` (DEX block, removed the now-unused `Core.Blockchain` using, two comments),
`Models/Actions/NodeConfigs.cs`, `Services/Actions/ActionNodeOutputSchema.cs`,
`tests/.../Actions/{ActionNodeOutputSchemaGoldenTests,ComposedDagDeterminismTests}.cs`,
`tests/.../Actions/Handlers/{Tier2ChainNodeHandlerTests,ChainCapabilityFlagInvariantTests}.cs`
(the invariant instantiator now passes null for concrete collaborators, which also unblocks
`EmitNodeHandler`/`FungibleTokenCreateNodeHandler`), `tests/.../Validation/ValidationTests.cs`,
SDK `api/client.ts`, `api/index.ts`, `src/index.ts`, `workflow/{node-config,types}.ts`,
`tests/workflow/run-driver.test.ts`, `tests/api/{client,self-audit-one-fix}.test.ts`,
`Providers/Stores/Surreal/AGENTS.md`, `PROVIDERS.md`, `API_SYNC.md`, `docs/auth-audit.md`,
`docs/RESIDUAL-RISK-RUNBOOK.md`. Nothing in A-owned Solana core files or `lib.rs`.

## Requests to other lanes

- B: `Program.cs` exchange-block comment and `Interfaces/IReserveExchangeManager.cs` remarks still
  name `SwapManager`/`ISwapManager`; reword to "the durable Swap node/DEX journal".
- A: EIP-1898/`finalized` support of the local Ethereum chain is shared by WTT reads.
- Reviewer: confirm the action engine maps resolver `Failed` (unenabled network, cleanup
  without settlement) and `Pending` as intended for a recoverable handler.
