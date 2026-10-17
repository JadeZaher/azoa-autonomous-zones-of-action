---
type: lane-checkpoint
lane: A
domain: Wormhole Token Transfer (WTT) bridge, Solana native output, shared Solana durable signing
status: source-complete-pending-review
resumed: 2026-10-16
baseline: 3d08ace
---

# Round 3 lane A resume checklist

Codex rollout `rollout-2026-10-15T07-00-24-01a13fa6` stopped at 23:04Z. State reconstructed from
`.omc/research/wtt-lane-checkpoint.md` (all sections through "Recipient native output"), the
last 30 lane messages, and the checkpointed source. The three "remaining" items named in the
final lane messages (22:49–22:59) were superseded by later checkpoint lines 187–198: their
provider slices and fixtures are on disk. What is genuinely missing is the WTT domain journal
and everything above it. No gate has run; everything below is source-only.

## Done at checkpoint (provider layer, source-authored, unrun)

- Contract `round3-wtt-contract.md`, codecs, stage-key vault/protector/readiness, guardian
  verifier, finality policy, proof binding, preparation/body-claim/stage-key/evidence-progress
  stores with fixtures.
- Ethereum source + target quote/prepare/submit/observe; Solana source + target
  (Verify/Post/Register/Redeem) + residual cleanup + recipient native output; Algorand source +
  target + recipient participant, paged program history, group receipts, target effects.
- Rust guard `contracts/solana/wtt-guard` opcodes 1–3 (WTT v1/v2), C's opcode 4/5, opcode 6
  Rent-only; C# builders and local-validator protocol fixture for opcode 6.

## In flight at cutoff (verify and close)

- [x] I1 `WttSolanaSourceQuoteTests.NativeOutput.cs` final-minute edit — check rig members and
      provider seams it calls.
- [x] I2 Solana cleanup slice (quote/prepare/submit/observe + fixtures) — static coherence check;
      the outer attempt fence moves to R3/R4.
- [x] I3 Algorand target registration/delivery proof from finalized execution
      (`WttAlgorandTargetEffects`, `AlgorandProvider.WttTargetObservation`) — confirm present.
- [x] I4 `EthereumProvider.WttObservation.cs` after C's `FinalizedReceipt`/`TokenEvents`
      extraction — confirm the thin wrappers compile-coherently.

## Remaining round-3 delivery (finite)

- [x] R1 Capability declaration: `IWttSourceModule`/`IWttTargetModule` on all three providers,
      `IWttParticipantModule` on Algorand, `IWttCleanupModule` + `IWttNativeOutputModule` on
      Solana, plus a provider-owned publication-rule read (consistency level / nonce policy)
      so the domain never guesses per-chain protocol bytes. Existing `Enabled` config still
      gates every network.
- [x] R2 Guardian transport: configured HTTPS endpoints only, exact emitter/sequence path,
      bounded body/time, 404/5xx = awaiting proof, envelope persisted before verification.
- [x] R3 WTT operation journal: `wtt_offer`, `wtt_operation`, `wtt_operation_stage` POCOs,
      `IWttOperationStore` + Surreal store. Atomic offer consumption + authority fence on
      admission; stage claim; fill-once candidate; participant exposure before disclosure;
      first-send authorization CAS; cancel-before-first-send with preparation seal; proof
      record; body-claim link; settlement; due/claim recovery. `IWttPreparationStore` seal.
- [x] R4 `WttLifecycleService`: quote (inspect both chains, normalized intent, source quote,
      persisted offer), start (Idempotency-Key), get/list, continue (prepare next stage),
      authorize first send, participant expose/accept, cancel, refresh, bounded continuation
      quotes for later stages, proof fetch → verify → body claim → target stages, native
      output and cleanup commands with fresh consent, return transfer linkage.
- [x] R5 `WttRecoveryService` + `WttRecoveryWorker` (observe/replay only authorized saved wire).
- [x] R6 `/api/bridge/wtt` controller (authenticated; credential capture; no private wire in views).
- [x] R7 DI registrations in `Program.cs` (additive WTT region).
- [x] R8 SDK `@azoa/sdk` WTT module (types + client methods + exports).
- [x] R9 Frontend WTT journey (quote, start, progress, participant signature, cancel).
- [x] R10 Fixtures: journal store against real SurrealDB; lifecycle flows (lost response,
      cancel-before-send, participant exposure, proof pending, body conflict, settlement);
      guardian transport; SDK client.
- [x] R11 Directory docs (`Services/Wormhole/AGENTS.md`, provider AGENTS) for the journal.

## Round-4 carry-forward candidates (to be confirmed at the end)

- Six-route local protocol fixtures over real pinned Wormhole deployments (EVM/Solana/Algorand
  with a local guardian set).
- Legacy `CrossChainBridgeService` Wormhole mode and action Bridge/Back cutover onto the WTT
  domain (B owns the action registry/fence; needs the reviewed journal contract first).

## End-of-round record (2026-10-16, source-only, nothing executed)

### Closed

- **I1–I4.** Native-output fixture is complete against its rig (`SourceRpc(native: true)`,
  `FinalizeRedemption`, `FinalizeNativeOutput`, `NativeOutputSnapshot` all present); cleanup and
  Algorand target-effects slices are present and coherent; Ethereum WTT observation compiles
  against C's `FinalizedReceipt`/`TokenEvents` extraction through the `WttTokenEvent` wrapper.
- **R1** Capability declarations: `IWttSourceModule` on Ethereum/Solana/Algorand
  (`*.WttQuote.cs`/`*.WttSourceQuote.cs`), `IWttTargetModule` on all three (`*.WttTargetQuote.cs`),
  `IWttParticipantModule` (Algorand), `IWttCleanupModule` + `IWttNativeOutputModule` (Solana).
  New `ReadWttPublicationRulesAsync`: Solana 32/any nonce, Algorand 0/zero nonce, Ethereum reads
  `finality()` at a finalized snapshot.
- **R2** `Services/Wormhole/WttGuardianTransport.cs` (configured HTTPS or numeric-loopback HTTP only,
  exact emitter tuple, bounded body/time, pending never means failure).
- **R3** `wtt_offer`/`wtt_operation`/`wtt_operation_stage` POCOs, `IWttOperationStore`,
  `SurrealWttOperationStore{,.Stages,.Updates}.cs`, `WttSigningAuthorityFence.cs`;
  `IWttPreparationStore.SealAsync`.
- **R4** `WttLifecycleService{,.Stages,.Followups,.Projection}.cs`: offer, Idempotency-Key start,
  lookup/list, request-key cancel with tombstone, continue/submit, participant expose/accept,
  bounded continuation quotes (cumulative per-chain budget), retire+seal, native output, residual
  cleanup with resume state, return linkage.
- **R5** `WttRecoveryService.cs` (observe-before-replay, proof fetch→persist→verify→body claim,
  settlement only from finalized Redeem / native output, operator proof resume) and
  `WttRecoveryWorker.cs`.
- **R6** `Controllers/WttBridgeController.cs` (`/api/bridge/wtt`) and `WttOperatorController.cs`.
- **R7** `Program.cs` additive WTT block after the evidence-progress registration.
- **R8** `sdk/azoa-wallet/src/api/wtt.ts`, WTT section in `client.ts`, exports in both indexes.
- **R9** `frontend/src/components/bridge/wtt-transfer.tsx`, mounted on the bridge page.
- **R10** `WttLifecycleFlowTests{,.Rig}.cs` (10 flows), `WttGuardianTransportTests.cs`,
  `SurrealWttOperationStoreTests.cs` (real SurrealDB), `client.wtt.test.ts`, `wtt-transfer.test.tsx`.
- **R11** `Services/Wormhole/AGENTS.md` §Operation journal, §Guardian transport; WTT notes in the
  Solana, Algorand and Ethereum provider AGENTS updated.

### Round-4 carry-forward (not round-3 work, with reason)

1. Six-route local protocol fixtures over pinned Wormhole deployments plus a local guardian set:
   they need artifacts built by three toolchains, and they must target the journal contract as
   reviewed after this join. Shared sweep risk with lane C: WTT Ethereum relies on the `finalized`
   block tag (`EthereumProvider.WttState.cs:57`, shared `FinalizedReceipt.cs:22`) and EIP-1898
   `{blockHash, requireCanonical}` (`WttState.cs:63`, `WttObservation.cs:107`,
   `WttTargetObservation.cs:37`). Ganache 7.9.2 support for both is unverified; one check covers
   both lanes.
2. Retire the legacy `CrossChainBridgeService` Wormhole mode and move action Bridge/Back onto the
   `wtt` domain. Lane B owns the action registry and fences, and `WttSigningAuthorityFence` refuses
   workflow authority until an action-bound first-send fence exists.
3. Absence proof for exposed participant messages that are never signed (Algorand opt-in, Solana
   native output). Safety already holds because an exposed stage blocks cancellation and
   replacement. Liveness needs Jupiter's reviewed full-block-ancestry absence observer adapted to
   two more chains, and that adaptation needs its own source review.
4. Startup `WttStageKeyReadiness` gate and the rewrap/retire maintenance workflow. These are
   operator key-rotation paths, not transfer flow, and need a decision on a blocking readiness
   signal for vault use.
5. Generate `.surql` for the three new tables. This happens in the final sweep, per policy.

### Cross-lane edits

- `Program.cs`: additive WTT DI block (lines after `IWttEvidenceProgressStore`).
- SDK `client.ts` (one import line plus WTT section before `// ─── UserNFT ───`), `api/index.ts`,
  `src/index.ts`: one export line each.
- `Providers/Blockchain/Ethereum/AGENTS.md` (last edited by C): one sentence in the WTT paragraph.
- `Providers/Blockchain/Algorand/AGENTS.md`: two WTT sentences.
- `API_SYNC.md`: three WTT rows after the DEX rows in the mapping table.

### Requests to other lanes

- **Root/B (owner of `Core/Surreal/SigningAuthorityFence.cs`)**: `Sql()` (line 172) rewrites
  `$_sa_x` to `$_<prefix>_x`, but every `WithParam` binds `<prefix>_x` (for example line 63),
  without the leading underscore. Under SurrealForge strict validation every fenced transaction
  (DEX, Hold, NFT, WTT) would then fail. Fix by binding `"_" + prefix + ...` or by rewriting to
  `"$" + prefix + "_"`, then verify in the sweep.
- **B**: register a `wtt` action-domain resolver for Bridge/Back after this journal is reviewed.
- **C**: none beyond the shared Ganache RPC-feature check above.

## Review fixes (2026-10-16, from round3-join-ledger §Lane A findings; source-only, nothing executed)

- **HIGH 1, cancel after a confirmed Transfer.** The store fences on the stage set, not only the
  current stage. `SurrealWttOperationStore.cs:155-166` refuses when any Source Transfer was
  authorized or exposed and has not been proven no-effect (`FailedOnChain`/`ExpiredNoEffect`).
  It also refuses when the current stage is not Source, or is an EVM stage that is `Prepared` or
  younger than 5 minutes. In-memory parity is in `InMemoryWttOperationStore.CancelAsync`.
  Atomic completion: `WttRecoveryService.cs:60-66` builds the attributed publication(s). Then
  `SurrealWttOperationStore.Stages.cs:202` records them and moves to `AwaitingProof` in the same
  transaction as the confirming observation, guarded by `TransferPublicationJson == null`. No
  Ready window remains. Legacy Ready journals are still caught by the fence.
- **HIGH 2, relayer fee and tenant recipients.** `WttLifecycleService.cs:37` refuses any fee that
  is not `"0"`. `:51` limits tenant credentials to the user's own Platform (custodial) wallets on
  the target chain. Direct users may deliver anywhere. Scope text is at `AzoaScopes.cs:352`. The
  SDK types the fee as `"0" | null`, and the frontend fee input is gone.
- **MEDIUM 3, guardian envelope.** `WttGuardianTransport.cs:94` binds the envelope to the
  publication's exact `BodyHex`. `WttRecoveryService.cs:131-139` persists envelopes only together
  with a verified proof. A failure leaves a Detail note and is re-fetched on the next pass, and a
  legacy unverified envelope falls back to a fresh fetch (`:206`). Operator `refetch-proof`
  (`WttOperatorController.cs:37`) calls `RefetchProofAsync` (`:184`, same body digests), then
  `ReplaceProofAsync` (`SurrealWttOperationStore.Updates.cs:63`). That only works in the Target
  phase, with the expected ProofsJson and no target stage sent or disclosed.
- **MEDIUM 4, EVM.** `Prepared` is never cancellable, and a fresh `Planned` stage counts as in
  flight (`SurrealWttOperationStore.cs:18,166`). `EthereumProvider.Preparation.cs` is untouched.
- **LOW 5:**
  - Algorand decode and sender errors become `InvalidDataException`, so they return 400
    (`WttAlgorandGroup.cs:43-54`).
  - The SDK checks ids against `^[0-9a-f]{64}$` (`client.ts:3553`).
  - Read scope (`CanReadWtt`) is enforced on get, list, replay, refresh and both quotes.
  - List, Lookup and Get are rate-limited.
  - A cursor beyond `MaxValue` ticks is rejected.
  - The fence lets only Cancel accept the direct owner (`WttSigningAuthorityFence.cs:18,25,48`).
  - Prefixes are `wtt_fence_*`, generated scopes are `wtt_gen_*`.
  - The guardian HttpClient has no redirects (`Program.cs:1267`).
- **LOW 6:**
  - `SealUnsentAsync` (`WttLifecycleService.Stages.cs:336`) runs on every cancel, retire and
    continue, so sealing is idempotent and repairs crashes.
  - In-memory `ClaimStageAsync` checks the cleanup original.
  - Admission, tombstone and commit results are read from named `LET` variables
    (`SurrealGeneratedStatementScope.Assign`) with indices captured at insertion. This also clears
    SRDB0001 in these files.

**Fixtures:**
- Flow tests, `WttLifecycleFlowTests.cs`:
  - `:289` atomic completion and legacy-Ready cancel refusal
  - `:324` relayer fee, tenant foreign recipient, direct foreign recipient
  - `:341` tenant isolation and cursor overflow
  - `:357` unverifiable envelope never persisted, plus refetch
- Store tests, `SurrealWttOperationStoreTests.cs`:
  - `:164` Transfer fence and atomic completion
  - `:186` EVM Prepared and in-flight Planned
  - `:200` tenant isolation and direct-owner cancel
- Transport: `WttGuardianTransportTests.cs:19` (other-body).
- SDK: `client.wtt.test.ts:66` (malformed ids).

**Deviations, with reasons:**
- A "verified wallet" means a `WalletType.Platform` wallet. That is the only class whose
  ownership the platform has proved by custody.
- Non-transient guardian failures re-fetch instead of parking. Nothing is persisted, so
  retrying is safe, and parking would only add operator work for a guardian that answers later.
- Planned EVM stages use a 5-minute window rather than a preparation-row probe, because a probe
  would mean editing lane C's Ethereum preparation file.

### Review fixes, cycle 2 (2026-10-16, source-only, nothing executed)

- **Correction to cycle 1.** The legacy-envelope fallback in cycle 1 never reached the journal.
  `SetOnce` rejected the replacement envelope, so the fallback did not unwedge anything. It is
  fixed below (item 3).
- **MEDIUM 1, EVM cancel.** The 5-minute heuristic is removed; no time window remains anywhere.
  - New `IWttNonceReservationModule` (`Interfaces/Wtt/IWttNonceReservationModule.cs`). Ethereum
    implements it in `EthereumProvider.WttPreparation.cs:188,208`. It reads the row id derived
    from `EthereumIdentity(chain, payer, WttPreparationKey(...))`. A released, never-signed row reads
    `None`; `Reserved` and unsigned reads `Unsigned`; anything else, or any doubt, reads `Held`.
  - New journal CAS `BeginPreparationAsync` (`SurrealWttOperationStore.Stages.cs:68`) moves
    `Planned|Preparing → Preparing` before any reservation (`WttLifecycleService.Stages.cs:256`).
  - Cancel refuses `Preparing`/`Prepared`, probes `Planned`/`Retired`, and commits only at the
    probed version (`WttLifecycleService.cs:214`, store `:154`). The store refuses an Ethereum
    cancel with a stage when no probe version is given.
  - A failed preparation releases only an unsigned reservation, through lane C's
    `ReleaseUnsignedReservationAsync`, then retires the stage at its own epoch. A signed one stays
    `Preparing` for idempotent re-entry (`.Stages.cs:347`). Cancel itself never releases anything.
- **MEDIUM 2, Platform proof.** `WalletCreateModel` no longer carries `WalletType`, and
  `WalletManager.cs:101` always writes `External`. `GenerateWalletAsync` and tenant bootstrap are
  unchanged. The recipient check is now `OwnCustodialWalletAsync`, which requires
  `IKeyCustodyService.CanSignAsync` (owned, Platform, ciphertext held) at
  `WttLifecycleService.cs:256`.
- **LOW 3, legacy envelope.** An unproven envelope is replaced only in the patch that first
  records its proof (`SurrealWttOperationStore.Updates.cs:19`, mirrored in the in-memory store).
- **LOW 4, custodial participant.** Refused at quote time (`WttLifecycleService.cs:84`): a custodial
  recipient with native output, or one needing an opt-in while not being the target payer.
  Reason: custody signs only payer wire, and the participant signature is always the recipient's
  own. Documented in `Services/Wormhole/AGENTS.md` §Participant consent model.
- **LOW 5.**
  - Positive tenant case: tenant credentials resolve a real grant through the factory.
  - `TimeProvider` is injected into the service, both stores, and the rig's `FlowClock`. The DB-side
    `time::now()` offer expiry in admission stays on the database clock.

**Fixtures:**
- `WttLifecycleFlowTests.cs`:
  - `:328`: 7-row recipient table
  - `:352`: crash after reserve, cancel refused, re-entry reuses the nonce
  - `:374`: Planned re-prepare vs cancel, in both orders
  - `:404`: failure after reserve vs after sign
  - `:419`: legacy envelope
  - `:440`: expiry on the injected clock
- `SurrealWttOperationStoreTests.cs:186`: epoch, probe version, Preparing and Prepared.
- `WalletControllerIntegrationTests.cs:33`: a Platform claim becomes External.

**Cut:** the cycle-1 store test `SignedEvmStageCannotBeCancelledAndAFreshlyClaimedOneIsTreatedAsInFlight`
(it encoded the removed time window); also the validator enum test, since the field is gone.

**Residual (round 4):** a different user's custodial recipient that needs a participant step cannot
be detected without a wallet address index. The VAA stays redeemable by its owner.
