---
type: Review
title: Round 1 independent Solana durable provider review
status: Bounded source findings resolved; runtime verification pending
---

The NFT/core author independently reviewed the Solana author's durable provider, partial signer, metadata encoding, value-transfer module, and new unit/local-validator test sources. No build, test, validator, or deployment was executed during this review.

## Finding and correction reread

`SolanaProvider.Nft.cs`, `NftSigningContexts`, assigns contexts by public-key dictionary indexer. A required user and the platform authority can share the same public key but carry different custody authority. For soulbound Accept, setting the platform context overwrites the recipient's user context when their addresses coincide. Required signer keys also deduplicate, so the subsequent exact signer-count check accepts a platform signature without requesting the recipient's grant. Deliver can similarly replace the logical actor's context. Reject incompatible contexts for one public key before custody/signing; permit repeated roles only where their effective signing contexts agree. Add a regression that proves platform custody cannot replace required user authorization. This finding was sent to the author and root coordinator.

The author replaced dictionary overwrites with role collection and normalized-context equality checks. In `SolanaProvider.Nft.cs`, both preparation (before RPC account reads or custody) and persisted-envelope validation now reject conflicting contexts for a public key. I reread the correction and `PlatformAddressCollision_CannotReplaceUserCustodyConsent` in `tests/AZOA.WebAPI.Tests/Signing/SolanaDurableLifecycleTests.cs`; its create/accept cases assert failure and zero custody calls. The concrete source finding is resolved. The author also rewrote `Providers/Blockchain/Solana/AGENTS.md`, removing the obsolete Metaplex-unimplemented claim. Neither correction has been executed.

The author separately corrected explicit TokenProgram IDs for Freeze/Thaw/Close API calls and replaced the SDK random ignored-key SetAuthority encoding with deterministic official SPL instruction bytes for removing mint authority. I inspected those changed instruction lines; runtime protocol validation remains required.

## Inspected safety boundaries

- Preparation does not broadcast. Reconstruction compares the complete expected message, verifies signatures, requires canonical serialized bytes, and checks the original transaction signature before submission.
- Signed memos bind the immutable intent digest, including preparation generation and logical issuer/fee terms. Provider-owned network genesis is checked before submission.
- Finalized confirmation requires archival transaction bytes matching the persisted envelope. Expired unknown history remains operator recovery; the provider does not invent an absence proof or replacement generation.
- Native fees are instructions within the same transaction. Frozen credential creation removes mint authority; acceptance approves the issuer delegate then freezes the recipient account. Delivery thaws/transfers/refreezes atomically, while revocation uses the issuer delegate plus platform freeze authority.
- A recreated destination ATA lacks the frozen delegate evidence and must be accepted again. The authored local-validator test exercises the prepare-to-submit account-recreation race and unauthorized holder operations.
- Ordinary NFTs use the Metaplex master-edition path and distinct token-standard projection. Credential metadata identifies the verified issuer, and ownership reads verify mint supply/decimals, metadata ownership, authority configuration, token account state, and delegate evidence.

These are bounded source observations, not runtime approval. The integrated sweep still needs to execute canonical wire/signature tests, actual Borsh/Metaplex instructions against the pinned validator fixture, custody context tests, frozen account operations, and configured native-fee settlement. Broader background convergence and action workflow parking remain separate implementation packages in the main plan.

## Source-review verdict

No unresolved blocking defect remains in the bounded source paths inspected in `SolanaTransactionSigner.cs`, `SolanaProvider.Durable.cs`, `SolanaProvider.ValueTransfer.cs`, `SolanaProvider.Nft.cs`, `SolanaProvider.NftInstructions.cs`, and `SolanaNftMetadata.cs`. This closes the independent source pass only. It does not close the integrated runtime gate or the full-platform objective.
