---
type: Review
status: Corrected bounded source accepted; runtime pending
---

# Solana metadata read review

A independently inspected Root's GetTokenMetadataAsync rewrite and
SolanaMetadataReadTests without running tests or changing implementation.

Required correction: SolanaTokenAmountInfo.Decimals is a nonnullable integer.
An RPC supply object containing amount but omitting decimals deserializes to zero,
passes the range check and reports invented precision. Preserve field presence
with a required/nullable contract or a strict dedicated response shape, and author
an omitted-decimals regression.

Root changed Decimals to nullable, requires a present in-range value in metadata
reads and GetMintDecimalsAsync, and added the absent-decimals fixture. A separately
reread the correction and accepts this bounded source path. Missing precision can
no longer become a successful zero-decimal result. No tests were executed.

The remaining bounded source path uses finalized supply reads, rejects missing
supply instead of substituting zero, derives the authentic metadata PDA, checks
program ownership/executable role and embedded mint, and uses the existing
Metaplex codec for actual name/URI/update authority/mutability. Missing metadata
remains explicit and does not invent name fields. The fixture's closed RPC switch
would reject an attempted off-chain URI fetch or unrelated request.

No other concrete blocker was identified in this narrow pass. Add explicit
infrastructure-error and request-cancellation fixtures to substantiate the stated
propagation boundary. This review does not approve unrelated Solana changes or
claim runtime evidence.
