---
type: plan
track: chain-value-routes
created: 2026-07-06
status: pending
---

# Plan: chain-value-routes

> **Written retroactively 2026-09-07.** The track had a spec but no plan, so
> nothing recorded that two of its seven items had already been closed by other
> work. Each box below is ticked (or left open) against code read at commit
> `b2576f4`. Item numbering matches the spec's Scope list.

## Value routes

1. [ ] **Real Solana value pipeline.** The plain SPL transfer/mint/burn surface is
   real; the bridge lock/burn/settle lifecycle on top of it is not, so the route
   stays fail-closed behind `Blockchain:Bridge:RealValueEnabled`
   (`Services/Bridge/BridgeOptions.cs:13`, enforced at
   `Services/CrossChainBridgeService.cs:160,301,719`).
2. [ ] **Real Wormhole sequence parsing.** Still the fail-closed stub —
   `Services/CrossChainBridgeService.cs:169` refuses with "sequence derivation and
   launch-safe provider settlement are not implemented." Per-signature secp256k1
   VAA verification is already unconditional (`Core/Blockchain/Wormhole/WormholeConfig.cs:89`);
   what is missing is deriving the emitter sequence from the real core-bridge log
   and validating against a provisioned guardian set.
3. [x] **Real ETH secp256k1 keygen — SHIPPED.**
   `Services/Signing/WalletKeyService.cs:158-170` draws a real 32-byte scalar from
   `RandomNumberGenerator`, retries an out-of-range draw up to
   `MaxSecp256k1ScalarDraws`, and derives the address *from that scalar*. Its own
   comment cites this track ("chain-value-routes H1") and names the bug it closed:
   the prior placeholder hashed the key with HMAC-SHA256 and SHA-256, so
   `wallet.Address` was not the address the key controlled. The persisted bytes are
   exactly what `Providers/Blockchain/Ethereum/EthereumTransactionSigner.cs`
   reconstructs from. **This item was still listed as pending in `tracks.md` and in
   the spec long after it landed.**

## Custody and signature residuals

4. [ ] **`ConnectWalletAsync` signature verification.** Unchanged and still the
   documented alpha-safe posture: `Managers/WalletManager.cs:466-470` reads
   `SignedMessage`/`OriginalMessage` and then does nothing with them — the comment
   says "For now, trust the address". Address uniqueness is enforced, so the residual
   risk is address-squatting, not custody escape.
5. [ ] **`RewrapAsync` cold-path `byte[]`.** `Services/Custody/KeyCustodyService.cs:358`
   still routes a hex `string` intermediate; only the decoded `byte[]` is zeroed. The
   caveat comment at `:376-380` names this track as the owner of the fix.
6. [x] **Soulbound clawback-revoke — the operation LANDED, but ownership of the
   remaining defect MOVED.** `Managers/CredentialManager.cs:174` performs a real
   `ClawbackAssetAsync` with idempotent replay and confirmed-only bookkeeping. It is
   nonetheless **inert in practice**: the D19 authorization check compares issuer to
   caller while the only mint path stamps issuer = caller = holder, and no path writes
   `current_owner`. That defect is now owned by
   `credential-third-party-issuance_20260906`, not by this track. Do not re-open it
   here.
7. [ ] **Metaplex dormant methods — converted, not deleted.**
   `Providers/Blockchain/Solana/SolanaProvider.cs:661` and `:672` no longer return a
   synthetic id for an account that was never created; both now fail closed with an
   explicit "not implemented" message. That closes the *dangerous* half of
   convert-or-delete. The methods still have no caller, so the remaining decision —
   implement the Token Metadata instruction building, or drop the seam — is open.

## Acceptance (unchanged from the spec)

- Each route flips `RealValueEnabled=true` only behind its own devnet/testnet
  round-trip drill, mirroring the Algorand G-gate evidence pattern.
- The mainnet gate (`NODE-HOST §8.3`) continues to require a real value route for
  the target chain. No route ships enabled-by-default.
