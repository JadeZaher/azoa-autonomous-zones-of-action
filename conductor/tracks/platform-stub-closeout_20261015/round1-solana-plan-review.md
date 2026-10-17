---
type: review
---

# Independent Solana plan review

Reviewer: root. Revised design accepted for implementation; source and runtime review remain required.

The public deterministic seed was clarified as SystemProgram.CreateAccountWithSeed address derivation requiring the protected base-account signature. It is not a keypair/private-key seed; the initial private-key-exposure concern does not apply to that concrete primitive.

The revised plan separates master-edition NFTs from single-supply credential SPL mints with immutable metadata and removed mint authority. Recipient acceptance authorizes delegation and freezes the empty holding account atomically; delivery and issuer-authorized revocation thaw/change/refreeze in one transaction. Implementation must prove the frozen-token delegate restrictions and reject an account closed/recreated between acceptance and delivery. The reported token standard must reflect actual on-chain semantics.

Each partial signature must verify against the exact canonical message and declared required public key. Extra instructions/signers, fee substitution, wrong genesis, and altered recovery metadata must be rejected. Finalized blockhash expiry alone cannot prove historical absence: retained coverage or explicit NeedsOperator is required. Local validator execution and official instruction fixtures are valid development evidence; live devnet evidence is recorded separately.

No source/runtime approval is implied by this plan acceptance.
