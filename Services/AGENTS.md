# Services — directory notes

Rationale / cross-cutting notes for `Services/`. Code carries terse one-line
doc-comments; the "why" lives here.

## §inert-control — the defect class this tree keeps re-shipping

**A claim in prose is not a control in code.** This has now recurred FIVE times. Each
time the control was real and the place that had to consult it did not — or consulted
it and ignored the answer — while the surrounding documents described the protection
as if it held:

1. **The AML decision was inert.** `ScreenAsync` / `RegisterMonitorAsync` had zero
   production callers and `KycScreeningResult.Decision` was read by nothing, so a
   sanctioned person who passed identity verification got full value access with a
   `HIT` row sitting unread in the database. See `Services/Kyc/AGENTS.md`
   §aml-screening.
2. **`IKycIdentityStore.GetByGlobalPersonKeyAsync` had zero callers.** The
   one-human-many-users lookup D3/D23 specified was implemented and never asked.
   `IPersonUniquenessGate` is what finally consults it (`Services/Kyc/AGENTS.md`
   §person-uniqueness).
3. **Governance's tally read raw holons.** The uniqueness gate protected the WRITE path
   in `GovernanceVoteNodeHandler` while the tally counted every child holon it could
   read — so a ballot created by any other route counted without ever taking a claim.
   The gate was real; the consumer of its result checked nothing.
4. **"Not enabled" was asserted in three documents while every handler was registered
   and dispatchable.** The runbook, both design track specs and the builder's palette
   badge said governance and credential revocation were not enabled; `Program.cs`
   assembly-scans every handler and both dispatch seams would have run them. See
   `Services/Actions/AGENTS.md` §not-enabled-node-types — that section exists because
   this class recurred.
5. **The integration contract asserted a CI grep that did not exist.** A boundary rule
   in `conductor/ARDANOVA-AZOA-INTEGRATION-CONTRACT.md` named an enforcement no
   workflow ran, and contradicted the convention actually in force and actually tested.

**The rule.** If you write — in a doc-comment, an `AGENTS.md`, a track spec, or a UI
string — that something is *gated*, *enforced*, *disabled*, *refused* or *not enabled*,
the SAME change must:

- (a) add the enforcement at **every** path that reaches the effect, and
- (b) add a test that **fails when the enforcement is removed**.

Before writing such a sentence, grep for the enforcement. Two facts make (a) easy to get
wrong here: an action node has **two dispatch seams** (the in-process `ActionManager`
executor and the durable `ActionNodeStepHandler` saga step), so a gate on one of them is
a gate on neither; and a publish-time check does not bind actions that were published
before it landed.

For (b), prefer a test that reads the enforcing seam's SOURCE when the effect is hard to
drive end to end — `NotEnabledNodeTypeGateTests.BothDispatchSeams_ConsultTheGate` and
`Publish_RefusesABlockedNodeType` do exactly that. The failure being guarded is "somebody
deleted the check", which a behavioural test over a handler cannot see.

## Phase 5 primitives — where each one's notes live

| Primitive | Status | Notes live in |
| --- | --- | --- |
| D3/D23 person-uniqueness gate + `person_operation_claim` | live | `Services/Kyc/AGENTS.md` §person-uniqueness; `Persistence/SurrealDb/Models/AGENTS.md` §person-operation-claim |
| D16 analytics read surface | live | `Services/Analytics/AGENTS.md` |
| D21 principal-only loan tracker | live | `Services/Lending/AGENTS.md`; node rules in `Services/Actions/AGENTS.md` §loan-tracker |
| D20 consumer governance (proposal / vote / tally) | implemented, **gated** | `Services/Governance/AGENTS.md`; node rules in `Services/Actions/AGENTS.md` §governance |
| D19 credential rail (soulbound revocation) | implemented, **gated** | `Services/Actions/AGENTS.md` §credential-revoke; manager notes in `Managers/AGENTS.md` §credential-revocation |

"Gated" means the code is written, reviewed and tested, and the server **refuses to run
it** — `Services/Actions/NotEnabledActionNodeTypes` blocks those four node types at
publish and at both dispatch seams. Do not describe them as working and do not describe
them as absent.

## Credential-free public endpoints

`CredentialFreePublicEndpoint` marks anonymous evidence routes that ignore all
supplied credentials. The policy selector routes them to a no-op authentication
scheme, and `ApiKeyAuthenticationHandler` also returns no result before opening
a store scope as defense in depth. Arbitrary bearer/API-key values therefore
cannot turn a free public read into an unmetered pre-rate-limit database lookup.
Pair the marker with an explicit credential-free CORS policy and an
IP-partitioned limiter.

## §bridge — CrossChainBridgeService + WormholeAdapter + Reconciliation

The cross-chain bridge is FUND-LOSS-CRITICAL. Its safety rests on an
**exactly-once** invariant composed from five independent mechanisms; a change
to any one must preserve the whole:

1. **Kill switch** (`BridgeOptions.RealValueEnabled`). Every real-value entry
   point — `InitiateBridgeAsync`, `RedeemWithVAAAsync`, `ReverseBridgeAsync` —
   refuses to move value when the flag is off UNLESS both chains resolve to the
   Simulated provider (`IsSimulatedRoute`, fail-closed on unknown chains).
2. **User-scoped, request-bound idempotency keys.** A client-supplied
   `Idempotency-Key` is trimmed, hashed, and namespaced by the authenticated
   user. Its ledger `OperationType` includes an operation-specific request
   fingerprint, so reusing one key for another bridge action or payload rejects
   before any chain effect. Absent a client key, a deterministic content key is
   derived (never random) so duplicates collapse.
3. **VAA replay ledger** (`ConsumedVaas`, UNIQUE-on-digest). The canonical
   digest is `SHA-256` over the base64-DECODED VAA bytes
   (`WormholeAdapter.ComputeVaaDigest`); malformed base64 is rejected BEFORE any
   claim/transition/on-chain call. Insert-before-redeem: a duplicate digest ⇒
   the VAA was already consumed ⇒ reject, never mint.
4. **Atomic status guards.** Every phase change is a conditional
   `TryTransitionBridgeStatusAsync(id, expected, next, …)` whose `WHERE
   Status==expected` predicate elects a single exclusive owner. The forward
   redeem lifecycle is `VAAReady → Redeeming → Completed/Failed`; the reversal
   lifecycle is the EXPLICIT `Completed → Reversing → Refunded/Failed` (reversal
   provenance is a state, never inferred from a `CompletedAt` stamp).
5. **Reconciliation** re-derives truth from chain confirmations. It only ever
   ADVANCES on a positive on-chain signal, FAILS on an explicit negative signal,
   and otherwise leaves the row untouched (flagging MANUAL INTERVENTION past the
   hard-stuck threshold). It NEVER auto-reverses funds and NEVER re-broadcasts.

### Real-value KYC authorization

Every non-simulated initiate, redeem, and reverse resolves the canonical
transaction owner and calls `IRealValueKycGate` before an idempotency claim,
status CAS, or chain effect. The gate reads Azoa's authoritative submission
ledger and requires the latest entry to be `APPROVED` with an explicit future
expiry plus provenance matching the active provider, operator policy version,
and assurance level. Missing/invalid provenance, a retired provider or policy,
manual approval outside Development, revocation/rejection, expiry, and storage
failure all deny. Fully simulated routes deliberately skip this real-value gate.

### G2 doc/canonicalization notes (final-hardening-cutover)

- **Emitter canonicalization at the single seam.** `WormholeAdapter.NormalizeEmitterAddress`
  is the one place that produces the emitter written into the replay ledger. It
  canonicalizes to lowercase 64-hex (`^[0-9a-f]{64}$` — strips `0x`, lowercases,
  left-pads) so the `consumed_vaa_ledger` schema ASSERT never false-rejects a legit
  redeem over mere casing / prefix. `ParseVAA` independently emits lowercase-hex via
  `Encoding.ToLowerHex`. A test pins that `ParseVAA` output always matches the ASSERT.
- **Wormhole mode is NOT launch-ready.** `WormholeAdapter.DeriveSequenceFromTxHash` is a
  dev stub (`string.GetHashCode`, collision-prone) — real sequence parsing from source-chain
  Wormhole message logs is a documented follow-up. Keep `RealValueEnabled` OFF for Wormhole
  routes until real sequence derivation lands; the kill switch (mechanism 1 above) gates
  init/redeem/reverse closed by default (`RealValueEnabled=false`) so this stub cannot move
  real value today.

### B3 coverage-gap resolutions (final-hardening-cutover)

- **Locked no-op loop — closed.** A `Locked` row whose lock tx is chain-confirmed
  must NOT self-transition `Locked→Locked` (the mint/VAA step never ran); the
  sweep flags `StuckFlagged` for operator/foreground re-drive instead of spinning
  as a silent no-op (`ReconciliationService` G1 guard). A `Locked` row with a
  chain-FAILED lock advances `Locked→Failed`.
- **Invisible lock-ok/mint-fail — surfaced.** The trusted flow's "funds locked on
  source, mint failed on target" path stamps `Failed` with an explicit MANUAL
  INTERVENTION message, and `CheckLockedFundsAtRiskAsync` /
  `GetFailedBridgesWithLockedFundsAsync` (Failed AND lock_tx set AND mint_tx
  absent) re-surface these otherwise-terminal-invisible rows every sweep tick.
- **Config-driven network — no hardcoded Devnet in value paths.** Provider
  resolution uses a single config-driven `_network`
  (`Blockchain:DefaultNetwork`, Devnet fallback only). `CrossChainBridgeService`
  and `WormholeAdapter` BOTH resolve lock (source) and redeem (target) providers
  on that network — `WormholeAdapter` previously hardcoded `ChainNetwork.Devnet`
  and was fixed to mirror the service. The only remaining `Devnet` literal is the
  per-row fallback in `ReconciliationService.TryResolveProvider` for rows written
  before the `Network` column existed (greenfield: none in practice).

### Idempotency settlement across crash windows

When a process dies AFTER the on-chain effect but BEFORE
`CompleteAsync`/`FailAsync`, the idempotency record stays `InProgress` and would
poison every retry. Two mechanisms settle it without guessing: the foreground
`SettleIdempotencyToBridgeStateAsync` mirrors the row's ACTUAL terminal state
after a 0-row conditional update; reconciliation's `SettleBridgeIdempotency`
settles the record only once chain truth proves a terminal state, resolving the
key from the row's persisted `IdempotencyKey` column (never fabricated).

Trusted initiation persists a deterministic tracking row before claiming the
idempotency ledger. After a claim, it must win `Initiated → Locking` before
broadcasting the source lock. A crash before the broadcast can therefore be
recovered after the configured stale-claim interval, with the row CAS electing
one retry. A row already in `Locking` is ambiguous and is never re-broadcast;
reconciliation must establish chain truth.

### Ambiguous crash windows fail CLOSED

If a consume-ledger row exists but the bridge is still `Redeeming` (or a reversal
is `Reversing` with no persisted burn hash), the outcome is genuinely ambiguous
(on-chain may or may not have landed). These are parked for reconciliation with a
"manual/operator resolution required" error — NEVER auto-completed or
auto-failed, because either guess could double-mint or strand funds.

### Submitted operation status is durable

`OperationStatus.PendingConfirmation` is a first-class `operation_log` status,
not an alias for `Unknown`. `BlockchainOperationManager` uses it only when a
provider returns a transaction hash but confirmation is still indeterminate.
The reconciliation sweep includes this state and may advance it only from
positive chain truth or fail it only from an explicit on-chain rejection. This
status must remain mirrored in the decorated POCO enum and generated schema;
otherwise a submitted transaction disappears from the recovery queue.
Provider resolution uses the persisted `ChainType` and `ChainNetwork`; an
invalid network fails closed, while a missing network retains the legacy
Devnet fallback for rows created before the field existed.

### Bridge submission boundaries and launch posture

Trusted flow treats `OperationStatus.PendingConfirmationMarker` as nonterminal.
A submitted source lock is persisted as `Locking` plus `LockTxHash`; target mint
is not called until the lock result is positively confirmed. A submitted target
mint remains `Locked` plus `MintTxHash`, and reconciliation alone may advance it
from positive target-chain truth. Every persistence CAS is checked before the
next chain effect; a zero-row write stops the flow and requires intervention.

`SourceAddress` is the server-selected source custody/vault address, never a
transaction hash and never caller-supplied wallet identity. Reversal persists the
target burn reference before attempting source release, and reaches `Refunded`
only after a confirmed source release. A pending release stays `Reversing` with
its release transaction reference in `ProofData` for reconciliation.

Wormhole initiation is launch-blocked before persistence or chain calls.

### The orchestrator is generic over capability and protocol, never over chain

`CrossChainBridgeService` contains no chain name in any decision. It resolves exactly three
things and then runs ONE lifecycle:

- `IBridgeSourceLeg` on the source chain (lock, release),
- `IBridgeTargetLeg` on the target chain (wrapped mint, wrapped burn),
- `IBridgeProtocol<TProof>` — chosen at composition time, so `TProof` binds statically.

A chain that cannot bridge does not implement the module, so `TryGetModule` returning false
IS the fail-closed refusal: there is no `SupportsBridging` boolean to set true by mistake and
no refusal body left to regress into an `Ok`. The direction asymmetry a single boolean could
not express — Algorand is a proven SOURCE and deliberately not a target — is expressed
exactly by which modules a provider implements, and `GetSupportedRoutesAsync` now advertises
that asymmetry per direction.

The bridge is generic over capability and protocol, **never over provider type**: the chain
pair is runtime data (`GetProvider` returns `IBlockchainProvider`), so an
`IBridgeStrategy<TSource,TTarget>` would need `MakeGenericType` reflection and forfeit the
compile-time safety that motivated it. The protocol, by contrast, is composition-time data,
which is why the generic earns its keep there.

`WormholeProtocol` is the single seam through which VAA verification is reachable; its
`IWormholeAdapter` is the Guardian-API transport and the hardened verifier it wraps
(`Secp256k1VaaSignatureVerifier` stays unconditional and fail-closed). Settlement requires a
resolved `IBridgeTargetLeg`, so a target chain with no reviewed wrapped-asset lifecycle stops
the redeem before any proof is consumed.

`Blockchain:DefaultNetwork` resolves through `ChainNetworkResolution`: absent means local dev
(Devnet), but a PRESENT-and-unparseable value now THROWS. The old "parse failed ⇒ Devnet"
fallback would have pinned a production deployment to devnet on a typo.

### Tests

- `BridgeExactlyOnceReplaySweepTests` — integrated exactly-once proof: duplicate
  key / replayed VAA / crash-resume / kill switch / reconciliation-idempotence
  driven through the real service + sweep over ONE shared fake store.
- `BridgeRedeemRecoveryTests`, `BridgeReverseRecoveryTests` — per-branch
  redeem/reverse resume decision trees.
- `BridgeIdempotencyScopingTests`, `BridgeKillSwitchTests` — key namespacing +
  kill-switch matrix.
- `ReconciliationBridgeHardeningTests` — G1/G5/G6/Window-6 sweep hardening.
- `CrossChainBridgeServiceTests` — service-level happy/edge paths.
