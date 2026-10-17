---
type: contract-input
status: research and source analysis; requires adversarial plan review
---
# Round 3 bridge implementation contract input

## Supported scope and evidence

The captured official WTT network matrix explicitly lists **Ethereum, Solana and Algorand** with mainnet/testnet/devnet support. Algorand must not be dropped on an unsupported deprecation assumption. Source: [WTT supported networks](https://wormhole.com/docs/products/token-transfers/wrapped-token-transfers/reference/supported-networks/), captured in `.omc/research/wormhole-algorand-scope-2026-10-15.md`, table dated September 28, 2026. The separately captured [network update](https://wormhole.com/blog/updates-to-wormholes-supported-networks) names other deprecated chains, not Algorand. The table also retains some chains that the dated update deprecates; therefore runtime route availability must use a reviewed per-network allowlist and actual configured contracts, not blindly import every matrix row.

For this platform the required WTT route matrix is the six directed pairs among its implemented Ethereum, Solana and Algorand providers, with explicit network/environment and real chain identity. Protocol support does not mean the repository currently implements the pair. Testnet labels across chains must map to the corresponding Wormhole environment, not arbitrary similarly named RPC endpoints. Local fixtures use a distinct pinned local protocol deployment and guardian set.

Official EVM WTT contract reference captured at `.omc/research/round3-wormhole-wtt-contracts-2026-10-15.md` documents `transferTokens(address,uint256,uint16,bytes32,uint256,uint32)` and `completeTransfer(bytes)`, plus payload variants. Those concrete functions support native token bridge lock/burn and mint/release semantics. Asset attestation/wrapped-asset registration is a separate prerequisite and must have its own durable stages when missing. Do not use payload variants merely to smuggle an application key without implementing their recipient authorization semantics.

Exact Solana instruction account metas/PDAs and Algorand application/group schemas are **not specified by the EVM reference**. The implementation owner must capture the pinned official Wormhole SDK/program contracts for those platforms before authoring wire codecs. Existing published chain IDs and contract addresses must come from official per-environment deployment data, not guessed constants or a local developer public-network assumption.

## Current mismatch to repair

- `IBridgeSourceLeg` currently describes a platform vault lock/release, and `IBridgeTargetLeg` a generic wrapped mint/burn. These can model a separately authorized trusted bridge, but do not by themselves publish or redeem WTT messages.
- `WormholeProtocol.ObserveAsync` is an explicit refusal: no actual source emitter/sequence extraction.
- `WormholeAdapter.RedeemTransferAsync` routes through generic mint with VAA bytes as TokenUri and an arbitrary amount of one. Replace this adapter contract with a real protocol redemption capability rather than teaching generic mint to reinterpret a URI.
- `BridgeObservationRequest` lacks immutable amount, network, protocol/contract identity and source-effect binding; `BridgeMessageHandle.Sequence` is signed long while the protocol sequence is uint64. Use exact unsigned decimal representation across SDK/persistence and validate bounds.
- `BridgeSettlement` contains only transaction ID. `CrossChainBridgeService` consequently transitions any nonerror settle result directly to Completed. An accepted or timed-out target broadcast cannot establish settled value.
- `ComputeVaaDigest` currently hashes the full signed envelope. Distinct valid guardian signature subsets/envelope encodings over the same body can produce different ledger keys. Replay protection must identify the canonical signed message body, not its interchangeable signature envelope.

## Proposed provider boundaries (new AZOA contracts, not claims about existing vendor APIs)

Use typed WTT-specific source/target capability modules alongside trusted-bridge modules. A frozen intent contains version, protocol/environment, source and destination chain identities, native and Wormhole asset identities, sender and target recipient bytes, exact base-unit amount/decimals, normalized WTT amount and dust policy, relayer fee, expected core/token bridge contracts or programs, custody contexts, payer, fee/rent caps and stable operation/generation identity.

Source capability: quote prerequisites and costs; prepare exact bounded approval/attestation/transfer stages; persist prepared wire before first authorized submission; submit exact wire; observe finalized source result and extract exactly one expected core publication. The returned message observation binds source transaction, actual core contract/program, token bridge emitter, full uint64 sequence, consistency/finality, canonical body/payload hash and exact intended transfer. Multiple matching-looking events require deterministic exact identity, not first-log selection.

Target capability: verify supported asset attestation/wrapped mapping and completion state; prepare actual WTT redemption with exact VAA body and target bridge identity; persist/submit/observe exact wire; return Pending, Confirmed, FailedNoEffect or Unknown with evidence. A source publication alone never authorizes a platform-minted substitute asset. Existing completed redemption on chain is valid recovery evidence only after binding its body identity, recipient, asset and amount to this operation.

Carry actual caller and tenant custody through every fresh stage, including allowance, asset opt-in, metadata/attestation registration or account rent. Observation and rebroadcast of already authorized wire do not create new consent. New stage preparation requires current authority and action cancellation/fork fences. Reuse current provider preparations/nonce allocation, but give WTT its own immutable intent and domain identity rather than passing arbitrary instructions through a permissive generic signer.

## Canonical proof and replay

Parse wire VAA strictly once and verify its canonical signed body against the configured guardian set with authoritative expiry/rotation rules and required quorum. Do not trust caller-populated Digest, SignatureCount, emitter or sequence fields. Parse the WTT transfer payload and compare original asset chain/address, normalized amount/fee, destination chain/address and source emitter/sequence to the frozen operation. Include explicit decimal normalization and dust handling; a signed unrelated VAA is not authorization for this bridge row.

Use a domain-separated ledger key from environment/core protocol identity plus canonical body digest or canonical body bytes, with a unique message identity index `(environment, emitterChain, emitterAddress, sequence)` as an additional conflict fence. Equivalent valid signature sets for the same body must collide; conflicting bodies under one message identity must quarantine. Preserve raw envelope bytes for evidence, but not as the sole economic uniqueness identity. The existing double-keccak body digest is already parsed in WormholeAdapter and is a useful shared primitive after full structural/authenticity checks.

The claim is an ownership/reservation record before target submission, not terminal consumption success. Keep durable target preparation/submission identity and exact replay after response loss. Never release a possibly sent claim on timeout or operator absence assertion. A definitive failed-no-effect generation may be retired only with protocol proof and an audited next generation; finalized success settles the consumed-proof record and bridge atomically. Existing greenfield raw-envelope ledger fixtures and all replay tests must migrate together, including alternate-signature-set vectors.

## Pending settlement and reverse journey

Expose source preparation/confirmation, awaiting guardian proof, target prerequisites, target authorization, submitted redemption, finalized settlement, and NeedsOperator distinctly. The action domain resumes the saved bridge identity; no successor fires merely because a transaction ID exists. Polling must be fair and must never silently sign fresh stages. SDK/UI must expose recipient/payer participation and cross-device recovery.

A completed WTT return is another actual WTT transfer of the original representation back to its origin; it is not an arbitrary burn followed by release from AZOA's vault. Keep trusted mode separate. Refund after source finality cannot promise automatic compensation while outbound redemption may still occur. Define reverse/failed-target evidence and required holder authorization in the contract before implementing buttons or enabling action Back.

## Acceptance evidence to author before final sweep

- Real local source publication and target settlement through actual provider modules on each required protocol family, with pinned program/contract/guardian artifacts and loopback/genesis checks before funding/signing.
- Exact emitter/sequence extraction, unrelated/multiple log rejection, full payload binding, amount/dust precision and guardian-set validation.
- Same body with different valid signature envelopes consumes once; wrong network/core/recipient/asset/amount cannot settle.
- Crashes before claim, after prepared wire, after send before response, between on-chain completion and DB projection; concurrent retries never issue a fresh economic operation.
- Target Pending remains pending; confirmed target receipt reconciles once; absent history remains Unknown; reverse requires actual holder authority and confirmed return.
- SDK/UI/action parity for frozen keys, participant waits, fee disclosure and operator evidence recovery.

No source implementation or runtime execution was performed for this input. Missing vendor/platform API parameters require concrete interfaces and pinned official protocol research, not an implementation deferral.
