---
type: Review
title: Round 1 NFT core independent source review
status: Bounded source findings resolved; runtime verification pending
---

The root reviewer independently inspected the author's lifecycle advancement, participant discovery, cancellation, confirmed ownership projection, and Surreal store transaction guards. This is source review, not executed verification or a full-platform approval.

Previously raised findings now have concrete source fixes: terminal cancellation returns before provider resolution; no-effect evidence must match persisted transaction, protocol and contract identity; archived generations retain verdict and observation time; cancellation depends on zero confirmed stages; partial completion details no longer advertise cancellation. The store atomically competes cancellation against admission/submission and releases only the matching asset reservation. Projection preserves the original issuance timestamp.

The prepared-to-submitted compare-and-swap precedes egress. Concurrent losers reload persisted state, and the provider receives the recorded signed transaction. Confirmation requires the same asset identity before the authority and ownership projection commit together. Participant acceptance is checked against the required wallet owner and value-access gate before preparing signatures. The inspected raw Surreal statements coordinate lifecycle and authority records and are documented atomic exceptions.

Still required: the author's historical-network revoke correction, independent provider reviews, generated schema refresh, and integrated tests. Action workflow parking and submitted-operation background reconciliation remain implementation packages in the next reviewed wave; their absence is not accepted as an external deferral.

Additional cross-provider finding sent to the author: current-owner checks and wallet resolution compare addresses as case-sensitive text. Ethereum ABI ownership reads may return lowercase while the recorded wallet uses checksum spelling. Use chain-aware equality consistently, preserve Solana case, and require the returned owner identity in movement admission rather than only `Exists`. This finding remains open until the correction is independently reread.

Correction reread: NFT access, movement admission, wallet resolution and refunds use shared `ChainAddressIdentity`, whose Ethereum branch delegates to the existing EIP-55-aware parser and whose other branches preserve case. Movement/refund evidence must match the expected owner. The historical-network revoke replay correction and explicit initial network selector were also inspected. These bounded source findings are resolved; authored regressions remain unexecuted.
