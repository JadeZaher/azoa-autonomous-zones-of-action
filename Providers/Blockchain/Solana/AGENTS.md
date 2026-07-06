# Providers/Blockchain/Solana

## §signer — SolanaTransactionSigner (final-hardening B1)

Real Ed25519 Solana signing behind the chain-agnostic `ITransactionSigner` seam.
Crypto comes entirely from `Solnet.Wallet` / `Solnet.Rpc` (already referenced) — no
hand-rolled curve math, base58, or wire framing.

### Seam contract — the exact byte format (B2 MUST match this)

This is the contract the not-yet-real transaction-building provider path (B2) has to
produce so `Sign` accepts its bytes:

- **Input (`canonicalTxn`)** = the serialized Solana **Message**, i.e. the output of
  `Solnet.Rpc.Builders.TransactionBuilder.CompileMessage()` (equivalently
  `Solnet.Rpc.Models.Transaction.CompileMessage()`). These are the exact bytes that
  are Ed25519-signed. `Message.AccountKeys[0]` MUST be the fee-payer / first required
  signer — the signer verifies the resolved key matches it and fails closed
  otherwise. NOTE: the blockhash is inside the message, so B2 must set a recent
  blockhash before compiling; the signer does not (and cannot) add one.
- **Output** = the submittable **wire transaction**: `[compact-u16 signature count]
  [64-byte Ed25519 signatures...][message bytes]`, produced by
  `Transaction.Populate(message, [signature]).Serialize()`. This byte-matches
  Solnet's own `TransactionBuilder.Build(signer)` for identical inputs (pinned by
  the round-trip test). It is ready for the JSON-RPC `sendTransaction` (base64/base58
  encode at the RPC boundary — the signer returns raw bytes).

### Key representation

The signer reconstructs the signing account from the raw 64-byte Solana secret
(`new Account(secret64, secret64[32..64])`) — the same representation
`WalletKeyService.GenerateSolanaKeypair` persists (Solnet `PrivateKey.KeyBytes`).
Fail-closed guards: a key that is not exactly 64 bytes, a message whose first
account key is not the signer's public key, a non-64-byte signature, or a final
transaction that fails `VerifySignatures()` all return an error result — never a
partially-signed or wrong-key transaction. The error message never contains key
bytes.

### Current state of the provider (why B2 is still owed)

`SolanaProvider` Mint/Burn/Transfer AND the bridge quartet
(LockForBridge/MintWrapped/BurnWrapped) are now **fail-closed** — they return an
explicit `Error`, not a synthetic-op-id `Ok`. This closes the fund-path hazard where a
durable quest chain-action node dispatched to Solana would record success with a
non-chain op-id and mislead reconcile-before-retry. The generic value ops were
hardened in G2 (the chain-capability gate only checks wallet-bound, not per-chain
pipeline readiness, so it does NOT block Solana value ops — hence fail-close here).
`VerifyBridgeProofAsync` was removed entirely (the always-true verifier is gone; VAA
verification is the WormholeAdapter / Secp256k1VaaSignatureVerifier path only). The
signer is ready the moment the provider compiles a real `Message` and routes it through
the custody chokepoint → this signer; at that point the fail-closed guards flip to real
build→sign→submit. NOTE: the signer returns the fully-assembled submittable wire
transaction (not a bare signature) — the provider submits it as-is.
