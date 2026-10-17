# Wormhole proof boundary

WTT framing follows Wormhole commit `bb5957f321b0a9d168f52e906699ee77baa82a28`, `ethereum/contracts/Messages.sol` and `bridge/Bridge.sol`; captures and normative contract are recorded in the platform closeout track. VAA header signatures do not participate in message identity: `BodyDigest` is double Keccak-256 over the exact body. Alternate valid signer subsets therefore identify the same redemption. `EnvelopeHex` remains evidence, not a replay key. Identity is additionally partitioned by trusted protocol deployment/environment and emitter tuple in the ledger.

`WttVaaCodec` parses structure only. Its output is never a trusted proof or settlement verdict. Guardian membership, authoritative rotation/expiration, quorum, exact expected publication and target on-chain completion are separate mandatory gates. Bytes, addresses and metadata fields remain lossless; protocol uint64 sequences and uint256 quantities cross public JSON boundaries as decimal strings. WTT payload 3 is not accepted as an ordinary transfer.

Normalization truncates source decimals above eight exactly as the bridge does, discloses source dust and normalized fee, and rejects zero after truncation. Target providers must additionally enforce their native amount range and expected origin/representation decimals before source egress. The legacy `WormholeAdapter`/`CrossChainBridgeService` Wormhole mode is not yet retired; `/api/bridge/wtt` is the WTT path and the legacy cutover plus six-route protocol fixtures are carried forward.

## Protected protocol stage keys

`WttStageKeyVault` enters the existing consent-aware custody boundary for both claim and use. The binding includes actor, tenant, wallet, operation, chain/protocol identity, role, generation and exact source/target signing scope. A provider supplies its vetted seed-to-public-key derivation; generic custody does not invent a chain key format. Claim commits an encrypted CSPRNG seed before any protocol signature and always returns the database winner. Retry after wallet rotation decrypts that original seed and checks its public key; it never derives a replacement from the new wallet key.

`WttStageKeyProtector` uses AES-256-GCM with canonical identity/public-key/key-version associated data. Seed buffers are zeroed on success and failure. The versioned `Wtt:StageKeys` configuration is independent of wallet encryption: `ActiveKeyId` names one key in `Keys`, whose values are base64 32-byte secrets supplied outside source control. Old decrypt versions remain configured until every retained envelope is CAS-rewrapped. `WttStageKeyReadiness` rejects any unavailable referenced version before WTT activation; retention is conservative even for completed operations. A config file edit is not permission to discard an old encryption secret. Multi-host rollout must retain old versions on every host until all old writers stop and the readiness/rewrap audit succeeds.

The protector itself grants no signing authority. It is an internal encryption primitive, never a controller/SDK secret-export surface. Only the custody-bound vault is used for provider key creation/signing; rewrap is an operator maintenance path and does not authorize protocol egress. DI registration is in `Program.cs`; schema generation, a startup `WttStageKeyReadiness` gate and rewrap maintenance orchestration remain open.

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
  own key-custodied wallets on the target chain, proven by `IKeyCustodyService.CanSignAsync` (owned,
  `Platform`, ciphertext held) rather than the wallet type: `POST /api/wallet` always registers an
  `External` wallet, and only custody generation or tenant bootstrap creates `Platform` rows.
  Connected external wallets carry no ownership proof, and paying someone else stays under
  `transfer:sign`. Direct user credentials may name any recipient. Reads (get/lookup/list/refresh
  and read-only offers) need a WTT scope.
- **Participant consent model.** Custody signs only stage-payer wire under that payer's scope; a
  participant step (target asset opt-in, native output) is always the recipient's own client
  signature, and custody never signs as another wallet. A quote is therefore refused when the
  recipient is one of the user's custodial wallets and the route needs such a step: any native
  output, or an opt-in for a recipient other than the target payer. (A different user's custodial
  recipient cannot be detected without an address index; that route still waits on a signature
  only its owner's custody could give, and the VAA stays redeemable.)
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

## Guardian transport

`WttGuardianTransport` contacts only `Wtt:Guardians:Endpoints` (HTTPS, or HTTP to a numeric loopback
address for local protocol fixtures), requests the exact `/v1/signed_vaa/{chain}/{emitter}/{sequence}`,
bounds time and body size, and returns an envelope only when its parsed emitter tuple matches the
request. 404, 5xx, timeouts and malformed bodies are "awaiting proof", never source failure or refund
eligibility. Guardian authenticity comes solely from `WttProofVerifier`.
