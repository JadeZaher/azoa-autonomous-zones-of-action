# Wormhole proof boundary

WTT framing follows Wormhole commit `bb5957f321b0a9d168f52e906699ee77baa82a28`, `ethereum/contracts/Messages.sol` and `bridge/Bridge.sol`; captures and normative contract are recorded in the platform closeout track. VAA header signatures do not participate in message identity: `BodyDigest` is double Keccak-256 over the exact body. Alternate valid signer subsets therefore identify the same redemption. `EnvelopeHex` remains evidence, not a replay key. Identity is additionally partitioned by trusted protocol deployment/environment and emitter tuple in the ledger.

`WttVaaCodec` parses structure only. Its output is never a trusted proof or settlement verdict. Guardian membership, authoritative rotation/expiration, quorum, exact expected publication and target on-chain completion are separate mandatory gates. Bytes, addresses and metadata fields remain lossless; protocol uint64 sequences and uint256 quantities cross public JSON boundaries as decimal strings. WTT payload 3 is not accepted as an ordinary transfer.

Normalization truncates source decimals above eight exactly as the bridge does, discloses source dust and normalized fee, and rejects zero after truncation. Target providers must additionally enforce their native amount range and expected origin/representation decimals before source egress. The legacy `WormholeAdapter` initiation is retired (round 4): `InitiateTransferAsync` refuses before any source lock because only the WTT journal reads the emitter sequence from the source core-bridge message; the `string.GetHashCode` sequence fabricator is deleted. `IWormholeAdapter` stays registered for `WormholeProtocol` (guardian transport + verifier). `/api/bridge/wtt` and the `wtt` action domain (§Workflow authority) are the WTT paths; the six-route protocol fixtures are L6's.

## Protected protocol stage keys

`WttStageKeyVault` enters the existing consent-aware custody boundary for both claim and use. The binding includes actor, tenant, wallet, operation, chain/protocol identity, role, generation and exact source/target signing scope. A provider supplies its vetted seed-to-public-key derivation; generic custody does not invent a chain key format. Claim commits an encrypted CSPRNG seed before any protocol signature and always returns the database winner. Retry after wallet rotation decrypts that original seed and checks its public key; it never derives a replacement from the new wallet key.

`WttStageKeyProtector` uses AES-256-GCM with canonical identity/public-key/key-version associated data. Seed buffers are zeroed on success and failure. The versioned `Wtt:StageKeys` configuration is independent of wallet encryption: `ActiveKeyId` names one key in `Keys`, whose values are base64 32-byte secrets supplied outside source control. Old decrypt versions remain configured until every retained envelope is CAS-rewrapped. `WttStageKeyReadiness` rejects any unavailable referenced version before WTT activation; retention is conservative even for completed operations. A config file edit is not permission to discard an old encryption secret. Multi-host rollout must retain old versions on every host until all old writers stop and the readiness/rewrap audit succeeds.

The protector itself grants no signing authority. It is an internal encryption primitive, never a controller/SDK secret-export surface. Only the custody-bound vault is used for provider key creation/signing; rewrap is an operator maintenance path and does not authorize protocol egress.

**Readiness degrades, never blocks startup (owner default, round 4).** An absent or invalid `Wtt:StageKeys` keyring no longer throws from the singleton constructor: `IsConfigured` is false, every protect/use refuses, and the vault returns a degraded error instead of a 500. `WttStageKeyReadinessWorker` runs `WttStageKeyReadiness.CheckAsync` at startup and every five minutes and publishes the verdict to the singleton `WttStageKeyReadinessState`; until the first verdict the state is unknown and treated as degraded. While degraded, `WttLifecycleService` refuses new quotes and admissions (`DependencyUnavailable`) on any route whose source or target module implements the `IWttStageKeyModule` marker (Solana today); other routes and every existing operation's observation/proof/settlement continue. `Degraded` means unconfigured, or a retained envelope references a version missing from this host's keyring (`UnreadableEnvelopeId`).

Rotation runbook: add the new key and make it `ActiveKeyId` on every host while keeping the old one → `POST /api/operator/wtt/stage-keys/rewrap` (one bounded batch of 100 exact-envelope CAS rewraps onto the active key; repeat until `rewrapPending` is false) → `GET /api/operator/wtt/stage-keys/readiness` → only then remove the old key everywhere. `EnsureReadyAsync` remains the retirement precondition.

## Authoritative guardian state

`WttProofVerifier` reparses raw VAA bytes and requires the exact finalized source body, a registered foreign emitter and a fresh pinned target-core observation. Quorum derives from that actual guardian set. Noncurrent sets must remain unexpired at the target's observed chain time. Network/core/token-bridge identities must equal the frozen deployment; configured static guardian lists cannot substitute. Providers own state account codecs only, never proof-verification verdicts.

The existing `Secp256k1VaaSignatureVerifier` now supplies both its legacy configured-address interface and an explicit authoritative-address interface over the same SEC1 4.1.6 recovery implementation. All curve operations use the aliased vetted Bouncy Castle 2.x assembly; the shared Keccak helper hashes the recovered public key. Expected malformed points/hex fail validation, while unexpected faults/cancellation reach the host boundary. The guardian quorum/verifier path remains centralized here. WTT source publication verification and on-chain redemption remain independent of this local cryptographic verdict.

Finalized-head freshness is separate from HTTP fetch freshness. `Wtt:Finality:Networks` contains explicit `{ProtocolIdentity, MaximumLagSeconds}` entries for exact genesis-bound identities; missing, duplicate or nonpositive policies fail closed. Operators set each network allowance to include its normal finalization latency (Ethereum cannot use the short Solana allowance). Both finalized chain time and fetch time are compared with the trusted host clock, with five seconds of future skew. Retained guardian sets must also remain unexpired at host time; an earlier finalized block cannot extend their signing lifetime. No request-supplied lag overrides are accepted.

Physical redemption partitioning uses provider-converted canonical protocol-chain/genesis/core/token bytes (`WttDeploymentIdentity`), not display addresses or RPC profile aliases. Hex case, EVM checksum spelling, signature subsets and implementation upgrades cannot produce another physical redemption partition. Original protocol strings remain evidence and exact-owner replay checks still reject changed lineage.

Retired guardian expiry is exclusive at the later of trusted host time and
observed chain time. This preserves Algorand core's strict `timestamp < expiry`
rule and conservatively rejects the deadline second for all adapters.

Residual stage-fund cleanup is a fresh direct original-owner action with `wtt:cleanup:sign`, not an extension of an expired or cancelled workflow. The cleanup vault entry point requires the original actor/wallet, direct Solana custody, and the existing original stage-key binding; its tenant/stage/generation encryption identity never changes and no new key is claimed. Providers must first prove all original attempts resolved, use the closed exact return message to the original wallet, and preserve the separate total-return versus original-deposit accounting. The journal excludes delayed original submission/replacement before cleanup admission because a cleanup stage can only be claimed while every original attempt is terminal (§Operation journal).

## Operation journal

`WttLifecycleService` is the only path from an authenticated request to WTT egress; `WttRecoveryService`
and `WttRecoveryWorker` only observe, replay already-authorized saved wire, fetch/verify proof and settle.
Neither ever signs, quotes a new economic effect or creates a generation.

- **Offer → admission.** A quote freezes the full `WttOperationIntent` (both provider-inspected
  deployments, normalized amount, per-chain budgets, provider-read consistency/nonce rule) and the
  provider source quote in an immutable `wtt_offer`. The request key fixes the operation id before
  quoting, because providers bind preparation identity to `intent.OperationId`. Start must present the
  same key as `Idempotency-Key`; `AdmitAsync` atomically consumes the unexpired offer under the current
  source signing fence. Replay resolves the original operation before any current-authority check.
  Cancellation by request key writes a tombstone that a delayed admission returns instead of
  consuming the offer. Relayer fees are refused (the platform redeems its own transfers, so a fee
  would only pay a front-runner). Under a tenant credential the recipient must be one of the user's
  own wallets on the target chain (§Recipient rule): custody held per `IKeyCustodyService.CanSignAsync`
  (owned, `Platform`, ciphertext held) rather than the wallet type — `POST /api/wallet` always
  registers an `External` wallet and only custody generation or tenant bootstrap creates `Platform`
  rows — or an `External` wallet with a verified ownership signature. Paying someone else stays under
  `transfer:sign`. Direct user credentials may name any recipient. Reads (get/lookup/list/refresh
  and read-only offers) need a WTT scope.
- **Participant consent model.** Custody signs only stage-payer wire under that payer's scope; a
  participant step (target asset opt-in, native output) is always the recipient's own client
  signature, and custody never signs as another wallet. A quote is therefore refused when the
  recipient is one of the user's custodial wallets and the route needs such a step: any native
  output, or an opt-in for a recipient other than the target payer. Another user's custodial
  recipient is refused the same way through `IWttRecipientDirectory` (§Recipient rule).
- **Cancellation fence.** The store refuses cancellation once any sent or disclosed source
  `Transfer` attempt is not provably without effect (`FailedOnChain`/`ExpiredNoEffect`), while a
  cleanup stage is current, or after the source publication is recorded. Confirmed prerequisites
  (allowance, opt-in, attestation) stay cancellable with their effects retained. A confirmed
  Transfer records its attributed publication and moves to `AwaitingProof` in the same transaction
  as the confirming observation, so no `Ready` window exists. Durable-nonce stages: see
  §Nonce reservations.
- **Stages.** `wtt_operation_stage` is a linked list (`PreviousStageId`); a new stage is claimed only
  when the current one is terminal, so at most one attempt is ever unresolved. Claim → fill-once
  candidate → first-send authorization are separate CAS epochs; authorization re-runs the phase's
  signing fence (source wallet + `wtt:source:sign`, target payer + `wtt:target:sign`, consent
  `wtt:participant:sign` for participant/native-output messages, fresh direct owner +
  `wtt:cleanup:sign` for residual return). Only authorized stages are observed or replayed.
- **Generations.** A stage retries as generation+1 only after terminal evidence (`FailedOnChain`,
  `ExpiredNoEffect`) or retirement of a never-signed, never-disclosed claim. Retirement and
  cancellation seal the provider preparation row so a late signature cannot persist or send;
  sealing is idempotent and repeated on the next Continue or cancellation replay, which repairs a
  crash between the journal transition and the seal. EVM stages are never
  retired or cancelled after signing: the signed nonce must be sent or resolved by the Ethereum
  recovery layer.
- **Nonce reservations.** Chains whose custody allocates account nonces (Ethereum) implement
  `IWttNonceReservationModule`, which reads the allocator row at the same deterministic
  `(chain, payer, operation/phase/stage/generation)` id the preparation path uses. Before any
  reservation the service moves the stage `Planned|Preparing → Preparing` at a new stage and
  operation version (`BeginPreparationAsync`); the reservation and every later write are bound to
  that epoch. Cancellation refuses any `Preparing` or `Prepared` source stage, probes every
  `Planned`/`Retired` one, and commits only at the operation version it probed, so a concurrent
  re-preparation and a cancel serialize on the version: one of them loses its CAS. A crash after
  reservation leaves the stage `Preparing`; Continue re-enters it and the deterministic key yields
  the same nonce. A failed preparation frees an unsigned, never-disclosed reservation (the
  allocator may then refill that nonce) and retires the stage at its own epoch; a signed or
  unknown reservation is never released and the stage stays `Preparing` for re-entry. There is no
  time window anywhere in this rule.
- **Continuation quotes.** Provider quotes live 30 s. The admitted quote is used while fresh;
  afterwards each new stage uses a fresh provider quote, accepted only if every potentially spent
  attempt reservation plus every unconfirmed continuation reservation fits the intent's
  independent per-chain caps. Exceeding the frozen budget is refused (a new consent requires a
  new operation; the intent hash cannot change).
- **Participants.** Exposure is recorded before the unsigned message is disclosed and makes the
  stage uncancellable; disclosure replays the same saved message after response loss. Only the
  participant's exact signature is accepted, and server first send still needs authorization.
- **Proof and settlement.** The guardian envelope must carry the exact published body and pass
  `WttProofVerifier` against fresh target guardian state before it is persisted, together with the
  proof, body claim and phase change; an unverifiable response only updates `Detail` and is
  re-fetched later, so it can never wedge the set-once evidence fields. An envelope persisted
  without a proof (older journals) is tried once, then replaced by a fresh verified one in the same
  update that first records the proof; that is the only exception to set-once envelopes. If the recorded signer set
  later becomes unusable before any target stage was sent or disclosed, the audited operator
  `refetch-proof` replaces the proof with a freshly verified one for the identical body digest and
  clears the admitted target quote. The body claim must be owned by this operation or the
  operation parks for an operator. Settlement is written only from finalized target `Redeem` evidence (plus finalized
  native output when the intent requested it); submission never settles.
- **Cleanup.** A residual return may start from `Ready`, `AwaitingProof` or `Settled`; the journal
  records `ResumeState` and restores it when the cleanup stage resolves, so a settled transfer stays
  settled.

## Activation and the value gate

`Wtt:Enabled` (X-6) is the production switch: absent, blank or malformed means off (base `appsettings.json` ships
`false`; Development and IntegrationTest set `true`; a deployed Railway host also needs the module-activation
acknowledgement, see `Providers/Blockchain/AGENTS.md` §module activation). It gates only new work: direct quote and
start, and workflow admission in `WttActionResolver` (a disabled switch fails the node before any quote). Admitted
operations keep continuing and reconciling. It flips on per round-4 plan §8 gate 5.

The value gate runs at admission (`StartCoreAsync`) and at the first-send authorization of a value-sending stage
(`AuthorizeCurrentAsync` -> `StageValueRefusalAsync`, the single chokepoint every continue/submit/participant path
reaches), not on every continue (X-2, narrowed by RV-X R-2):

- **Who.** A direct caller passes the risk-hold-wrapped `IRealValueKycGate` (KYC + reversal pause; tenant overload under a
  tenant credential). A workflow node passes only the reversal pause (`ValueRailCapabilityGate.AccountStandingAsync`, the
  same predicate as `AdmitValueNodeAsync`), so identity verification never becomes a workflow precondition.
- **Which stages.** Source stages (`Approve`/`Attest`/`Transfer`) are gated: nothing has left the source wallet before the
  `Transfer` is authorized. `Cleanup:*` source stages and `Cleanup` phase residual returns are never gated. Target,
  participant and native-output stages are exempt when the recipient is the actor's own wallet (custodial or
  signature-proven, `OwnRecipientAsync`); once the source leg may have left, refusing them would only strand the value in
  bridge custody. A delivery to anybody else stays gated, because it is a value move out.
- Claim and preparation of an unsent source stage stay open: they move nothing, and cancel releases the reservation.
  Cancellation stays open too.

## Recipient rule

A direct (owner) credential may name any recipient. Under a tenant credential (owner decision: the
user's own wallets only, relayer fee `"0"`), and under a workflow run (the graph author chose the
recipient while the runner's value moves: RV-L5-06), the recipient must be one of the actor's own wallets on
the target chain: key custody actually held (`Platform` + `IKeyCustodyService.CanSignAsync`), or an
`External` wallet whose ownership a verified signature challenge proved (`IWalletOwnershipProof`; the
registered `NoWalletOwnershipProof` is fail-closed until L4's persisted proof flag replaces it in place).
A route needs a **participant step** when it unwraps native output or the recipient is not ready and is
not the target payer. Such a route is refused at quote when the recipient is custodial (custody never
signs as a recipient), when the caller is a tenant or a workflow (neither can produce a recipient's own
signature; the fence refuses `Participant`/`NativeOutput` for tenant and action-bound intents too), and
when `IWttRecipientDirectory` reports the address is another user's key-custodied wallet (only that
user's custody could sign; Algorand recipients must opt in before anyone can redeem). The directory
compares exact canonical addresses, which is correct for the only participant-step chains (Algorand,
Solana); a `(chain_type, address)` wallet index is requested from L4 for the lookup.

## Workflow authority

Bridge and Back nodes with a `wtt` config object run on the `wtt` action domain
(`Services/Actions/WttActionResolver.cs`). The node identity fixes the request key
`action:{run}:{node}:wtt` (direct callers cannot quote, start or cancel with an `action:` key), so the
operation id exists before any quote. The resolver quotes and admits under the node's frozen
`WorkflowSigningWitness`, which must have selected exactly `wtt:source:sign` + `wtt:target:sign` (+
`action:execute`). The operation stores `ActionRunId`/`ActionNodeId`/`ActionIntentHash`; from then on
`WttSigningAuthorityFence` accepts only that node's `Workflow` authority for Source/Target, proving
every grant plus `ActionExecutionFence` (run still Running, frozen input unchanged) inside each claim
and first-send transaction, and refuses direct authority. Workflow routes never carry a participant
step and never sign cleanup (residual return stays a fresh direct-owner action). The resolver only
continues with `AuthorizeSubmission`, reconciles finality/proof without signing, and reports
`Succeeded` with a `WttActionOutput` (Back binds its `ReturnOfOperationId` to the Bridge node's
`operationId`), `Cancelled`, `Ambiguous` (NeedsOperator or lost authority) or `Pending`. A disabled or
missing route module fails the node before any quote. A cancelled run leaves an unsent admitted
operation inert (no effect); a sent one keeps reconciling. Graph authors choose the recipient, but the
recipient rule above applies to every workflow run (RV-L5-06).

## Operation exits

- **Exposed participant never signs.** Recovery observes an `Exposed`, never-authorized participant
  stage through `ObserveExposedWttParticipantAsync` / `ObserveExposedWttNativeOutputAsync`.
  `ExpiredNoEffect` carries an absence proof past the exact message's validity (Algorand: txid absent
  from the indexer past `lastValid` with the finalized expiry block; Solana: the Jupiter
  full-finalized-ancestry scan adapted to the exact message bytes, resumable eight blocks per pass with
  its cursor stored in the stage observation) and resolves the stage so Continue issues generation+1.
  `Landed` means the participant broadcast their own exact signature: the provider recovers it, the
  ordinary acceptance path validates it, and the stage becomes `SubmissionAuthorized` with that
  signature as its authority, after which normal observation decides. Cancellation stays refused while
  exposed.
- **Publication mismatch.** A confirmed source Transfer whose publication is not attributed parks
  `NeedsOperator`. If the stage itself is `NeedsOperator`, operator `reconcile` re-observes and records
  the atomic completion once the provider returns an attributed publication. If a legacy journal
  parked with the Transfer already `Confirmed`, operator `resume-proof` re-observes it read-only and
  moves to `AwaitingProof` only with an exactly attributed publication; nonce/consistency drift stays
  refused because proof binding requires the frozen values.
- **Tenant-started operation after revocation.** `POST /api/bridge/wtt/operations/{id}/owner-takeover`
  (direct owner credential, `wtt:target:sign` on the frozen target payer) records `OwnerIntentJson`:
  the admitted intent with the tenant removed and both payer contexts rebound to the owner. Allowed
  only in `Ready`/`Target` with a verified proof and no target stage ever sent or disclosed; it clears
  the admitted target quote. Economics, deployments, recipient and the body claim are unchanged; the
  projection keeps the consented intent. New stages quote and sign under the owner intent's hash (stage
  rows keep the admitted `IntentHash` as journal identity; `IntentFor` replays each stage under the
  intent its quote bound). The tenant is locked out of further signing.
- **Workflow-admitted operation after its run stops (RV-L5-07).** The same takeover applies to an action-bound
  operation (with or without a tenant) under the same `Ready`/`Target` rules, once the admitting run can no longer
  sign: the store write carries `WttSigningAuthorityFence.ActionStopped`, the inverse of `ActionExecutionFence` (throws
  while the run is `Running`/`AwaitingReconciliation` and its node execution is still `Running` on the frozen input),
  in the same transaction as the version CAS. After takeover the store's `Action(operation)` and the service's
  `SigningActionOf` return no binding, so stages fence the owner's direct authority and the workflow node is refused.

## Guardian transport

`WttGuardianTransport` contacts only `Wtt:Guardians:Endpoints` (HTTPS, or HTTP to a numeric loopback
address for local protocol fixtures), requests the exact `/v1/signed_vaa/{chain}/{emitter}/{sequence}`,
bounds time and body size, and returns an envelope only when its parsed emitter tuple matches the
request. 404, 5xx, timeouts and malformed bodies are "awaiting proof", never source failure or refund
eligibility. Guardian authenticity comes solely from `WttProofVerifier`.
