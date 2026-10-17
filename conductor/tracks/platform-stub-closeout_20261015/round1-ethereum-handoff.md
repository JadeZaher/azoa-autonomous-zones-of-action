---
type: Handoff
title: Round 1 Ethereum prepared transactions and NFT authority
status: Source authored; independent review and integrated runtime gates required
---

The Ethereum provider now implements the shared NFT and prepared value-transfer modules. The shared preparation store reserves a unique protocol/signer/nonce, persists immutable call identity and signed bytes, and admits egress in a separate CAS transition. The legacy Ethereum transaction pipeline uses the same allocator. Prepared NFT/value candidates are never submitted by the recovery worker until their owning service explicitly submits them.

The two new POCOs are `EthereumPreparation` and `EthereumSignerCheckpoint`; generated schema and schema goldens remain assigned to the root coordinator. Ordinary persistence uses typed Surreal builders. The idempotency receipt POCO also gained `PreparationGeneration`, with generation-fenced replacement of proven no-effect value preparations.

Recovery is exposed through authenticated owner and recent-node-operator controllers and matching SDK methods. Operators can cancel platform preparations; they cannot impersonate user signers. Same-nonce cancellation wire is persisted before submission and reused within the explicitly authorized gas cap. Pending cancellation remains a non-egress pending observation. Finalized cancellation or revert provides an explicit no-effect result; no elapsed-time lease releases a possibly submitted nonce. Expected submission-versus-cancellation races produce no additional egress.

Observation verifies full transaction semantics, chain identity, canonical receipt block and finalized coverage. ERC20 restitution additionally requires the exact transfer event. NFT observation requires the contract lifecycle/stage/token event with the exact resulting owner and fee. The provider checks configured runtime code hash and immutable credential authority before NFT preparation/submission.

`contracts/ethereum` contains the pinned Solidity source, lockfile, deterministic compiler script, adversarial contract tests and executable isolated deployment script. The contract requires actor/owner/issuer authorization plus a separate platform policy permit. It prevents standard ERC721 mutation bypass, enforces immutable credential provenance and recipient acceptance, forbids reminting burned token IDs, and settles the original actor's native platform fee atomically with the final logical effect. Treasury callback and receiver failures revert the effect. Replay domains include chain, contract, lifecycle, stage and generation.

Local setup creates a fresh loopback chain and refuses occupied managed API/RPC ports. It verifies compiled deployment identity before writing an ignored Development-only overlay with ephemeral local credentials. Explicit signer activation requires a configured deployment epoch and equal latest/pending/finalized nonce. Public-network activation still requires deployment-wide quiescence; an API cannot prove that an unrelated old process holding a signer key has stopped. No public-network transaction or contract deployment occurred during authoring.

Authored verification includes:

- `EthereumPreparedRecoveryTests`: immutable restart reuse, prepared-only no-egress, ambiguous submission replay, exact/canonical observation, missing checkpoint, owner restrictions, cancellation pending/final behavior, and NFT tuple/dual-permit persistence.
- `SurrealEthereumPreparationStoreTests`: actual unique nonce claims, CAS protection of signed bytes, network-scoped replay, nonce ordering and create-only activation checkpoints.
- `EthereumPreparationRecoveryWorkerTests`: both enabled networks replay, including when the first fails; disabled networks are not resolved and each outage is logged once. The worker enumerates configured networks rather than the factory's single preferred-network convenience API.
- `contracts/ethereum/test/nft-authority.test.mjs`: authority/replay/fee/recipient acceptance/burn-remint/reentrancy/rollback cases.
- Existing pipeline fixtures were upgraded by the root reviewer and independently reviewed by this author. The strict fake store, two-provider concurrency and canonical-finalized RPC fixture passed static review after the checksum-case assertion correction.

The parent separately reviewed the contract and provider source; review records are adjacent to this file. Source authoring is not runtime acceptance. No build, tests, lint, SDK typecheck, contract compilation/deployment or schema generation ran in this lane. The final integrated sweep must execute these gates and resolve resulting defects. Only package-lock resolution ran, with package scripts disabled. `git diff --check` passed (line-ending notices only).

The prior Fiat/Algorand handoff remains in `round1-fiat-handoff.md`. Existing general bridge, DEX and other platform inventory work belongs to the next reviewed assessment round; this handoff does not classify those implementation gaps as external deferrals.
