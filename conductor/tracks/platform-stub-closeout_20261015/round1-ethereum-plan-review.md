---
type: review
---

# Independent adversarial Ethereum plan review

Reviewer: root. Status: corrections requested before Ethereum source implementation. Reviewed [design amendment](round1-ethereum-design-amendment.md).

The provider-owned durable preparation boundary and atomic NFT/native-fee contract are appropriate directions. The following gaps must be resolved in the concrete design:

1. An unsigned nonce reservation or permanently underpriced transaction cannot strand the signer queue. Provide a usable recovery/cancellation journey, with a persisted same-nonce cancellation and final canonical evidence. Cancellation requires the real signer's authorization; node operators cannot impersonate user wallets.
2. Coordinate allocator activation with legacy in-flight calls. RPC pending count alone does not account for an older concurrent caller that has not submitted yet. Include quiescence/reconciliation before admitting the new writer surface.
3. Specify one consistent credential authorization model. The proposed platform authorization, logical issuer signature and original fee payer are distinct identities. Extend immutable provider intents with the necessary identities and signing contexts; do not infer the issuer from the shared platform address.
4. Protect the invariants across every ERC721 entry point, including standard and safe transfers, approvals, burns, and the authorized execution function. Soulbound and issuer checks cannot exist only in the custom endpoint.
5. Bind permits to full logical operation, stage and generation. A zero-fee intermediary authorization must never authorize the final fee-bearing effect.
6. Supply executable local contract deployment and readiness verification, pinned build inputs, ABI and reproducible runtime hash. An instruction to configure an unavailable contract does not complete the development integration.

Required contract regressions also include treasury callback/reentrancy, cross-chain and cross-contract replay, and fee payer substitution. This is a plan review, not implementation approval or runtime verification.

## Revised design acceptance

The revised amendment addresses all six findings: one durable allocator, signer-authorized nonce recovery, quiesced activation, distinct issuer/authority/payer identities, complete ERC721 entrypoint restrictions and stage-bound permits, plus executable pinned local deployment. Accepted for implementation. This is design acceptance only; independent source review and final integrated runtime verification remain required. The activation checkpoint must be enforced at all egress paths, and expired coordination leases must never release a possibly submitted nonce.


## Cross-layer submission fence

Subsequent integration review identified a required implementation invariant: a signed provider preparation is not permission for a background worker to submit it. The owning NFT lifecycle can cancel Preparing/Prepared before its Submitted CAS. Provider recovery must retain a distinct durable egress authorization set through the owning lifecycle submission boundary; losing preparers and cancelled outer operations cannot be auto-submitted or resumed through a bypassing provider route. Owned nonce cleanup may use separately signer-authorized zero-value cancellation. The original economic call remains blocked after outer cancellation.

