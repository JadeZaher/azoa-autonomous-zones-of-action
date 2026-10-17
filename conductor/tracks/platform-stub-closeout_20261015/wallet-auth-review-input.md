---
type: Review Input
title: Wallet authentication evidence for the next full-goal assessment
status: Implementation pending
---

`Services/Signing/Ed25519SignatureVerifier.cs` still accepts only Algorand and throws for every other chain. Adding Ethereum and Solana dispatch alone will not close this package. `WalletAuthManager.CreateChallengeAsync` currently trims addresses without chain-specific canonicalization, while `SurrealUserStore.GetByAuthWalletAsync` compares the stored wallet address exactly. Ethereum checksum/lowercase spellings must identify one wallet throughout challenge, login and link; Solana addresses must preserve case.

The challenge store currently uses a private duplicate POCO, raw single-statement CRUD and catch-all exception wrappers. The generated public `Persistence/SurrealDb/Models/WalletAuthChallenge.cs` already defines a unique nonce index. Migrate the touched store to this typed surface and preserve atomic, unexpired, single-use consume semantics. Do not reinterpret transport errors as valid proof or empty authoritative state.

The reviewed implementation plan should include real signatures over server-issued bytes, altered-message/wrong-chain/address cases, malformed encodings, Ethereum equivalent-address lookup/link, Solana case distinction, concurrent nonce consumption, and concurrent wallet binding. Inspect browser/SDK signature encoding and the wallet-binding uniqueness constraint before choosing an API contract. Existing comments claiming other chains are intentionally out of scope are stale relative to this closeout goal.

This is preparation for the next assessment and plan review. No implementation or runtime acceptance is claimed here, and no external dependency justifies deferral.

Cross-network assessment input: `EthereumProvider.ConfiguredChainId()` currently reads one chain-level metadata `ChainId` for every network. Inspect whether enabled Mainnet/Testnet/Devnet endpoints can each pin their own identity without either contradictory pins or unpinned RPC trust. This is a separate configuration-surface review input; the durable preparation protocol identity checks do not by themselves establish the operator's intended chain on first admission.
