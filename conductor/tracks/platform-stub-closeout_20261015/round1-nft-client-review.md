---
type: review
---

# Independent NFT client and fee-boundary review

Reviewer: root, separate from the SDK/UI author. Source inspection only; runtime checks remain in the final integrated sweep.

The initial pass found stale fee/account state, late asynchronous response handling, permanently locked requests after a lost first response, and missing exact request correlation for server-discovered operations. The author added context generation guards, state resets, participant-scoped discovery, request-key lookup, and authenticated cancellation that creates a durable server tombstone. Clearing local state now requires cancellation or exact terminal operation correlation; absence alone does not authorize a replacement request.

A subsequent pass found a missing JSX closure and a cancellation control whose busy state survived identity changes; both were corrected. The fee boundary initially authorized only revision and maximum amount, while existing assets could belong to a different network from the default quote. The server now hashes all immutable quote terms, validates that identity on admission, and resolves quote context from the NFT or source transfer. SDK/UI carry that quote identity. This also rejects treasury or currency changes under an accidentally reused revision.

Bounded reread found the requested corrections present in the shared request recovery, cancellation, move, pending discovery, quote policy/identity, and SDK endpoint contracts. This is not blanket approval of every changed frontend file or a passing typecheck/test claim. The new regression tests, remaining mint/page integration, provider behavior and generated contracts still require the planned independent/final verification gates.
