---
type: Handoff
status: awaiting-independent-review-runtime-unrun
owner: lane-B
---

# B2 Algorand value preparation prerequisite

The existing value adapter now requires a durable preparation key before new signing. It claims an immutable unsigned NativePreparation plan before custody, commits full intent hash in the note, reuses exact saved signed bytes, and conflicts on changed terms under the same preparation key. Submit validates the saved plan/candidate, exact canonical envelope, single-sender Ed25519 signature and immutable protocol. Historical no-key wire is observation-only. This does not grant first-send authority; the outer domain still owns that decision.

Indexer observation now guards JSON types, rejects unexhausted pagination as uncertainty, checks exact transfer semantics/note/fee/validity and genesis before finality or absence. Network parameters use the existing canonical genesis/nonnegative-round/fee validator, renamed for shared use. Pure typed receipt-field checks were extracted to AlgorandReceiptFields and reused by fungible creation. Existing canonical-framing protection was already present and is preserved.

Authored fixtures migrate the original Algorand value tests to real custody signing plus durable preparation storage, exact server transaction IDs, and indexed intent fields. Additions cover overlapping preparation, saved-byte replay without more signatures, changed terms, missing key, corrupted signature, trailing objects, and partial/malformed absence evidence. These in-process fixtures do not replace real-store/protocol execution.

No build/test/lint/typecheck/schema/protocol execution ran. git diff --check is clean for the bounded files. Hold cost quotation, exact fee-cap consent, prepared recipient acceptance, atomic command/accounting store, funder lifecycle, caller migration and clients remain the next required B2 slices; this prerequisite is not a Hold completion claim.
