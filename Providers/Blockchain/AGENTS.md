# Providers/Blockchain — agent notes

Directory-level rationale for the blockchain providers. Terse one-line doc-comments
live in the code; the "why" and cross-cutting seams live here.

## Â§atomic-group-observation

`IAtomicTransferGroupObservationModule` is an evidence-only capability. Its
Algorand implementation uses Indexer records as the authority for a confirmed
two-leg group and validates every immutable field: transaction id, group id,
sender, ASA, recipient, amount, and the absence of close-to, clawback, and rekey
fields. Before any HTTP request, the Algorand adapter requires canonical
uppercase base32 transaction digests, canonical 32-byte base64 group digests,
and a positive invariant-culture ASA id. Both legs must have the same positive
confirmed round. If Indexer has
not indexed a leg, Algod can only classify it as pending, unseen, or pool
rejected; it can never upgrade that leg to confirmed. Observation performs no
persistence, signing, broadcast, settlement transition, or ownership mutation.

## Â§network-instance-isolation

`IBlockchainProvider.Initialize` mutates a provider's network client and active
network. Therefore a registered provider is never a singleton instance shared by
network cache keys. `BlockchainProviderFactory` receives
`BlockchainProviderRegistration` creators, creates one fresh provider for each
canonical `(chainType, network)` pair, and initializes it exactly once through a
`Lazy` cache. Production DI registers the concrete providers as transient and
hands creators to the singleton factory; consumers resolve only through
`IBlockchainProviderFactory` and must not call `Initialize` themselves.

All shipped providers inherit `BaseBlockchainProvider`: constructors allocate no
network clients, factory binding constructs those resources exactly once, and a
factory-bound instance rejects later public `Initialize` calls. A standalone
provider may initialize itself lazily for a direct unit test or local tool, but a
factory registration must return it still unbound. Third-party implementations
that bypass the base class remain responsible for honoring the interface contract.
The factory rejects undefined `ChainNetwork` values and disabled live networks
before invoking a registration creator; simulated mode is the intentional
network-free exception.

This is a settlement precondition: network-specific treasury routing and any
future fee settlement may rely on a returned provider remaining bound to the
requested chain/network. The factory tests exercise concurrent real Solana
resolution and assert that resolving Testnet cannot retarget the cached Devnet
provider.

## §bridge — cross-chain value primitives (final-hardening-cutover B2)

### Verification path (there is exactly one)
Cross-chain proof / VAA verification is **not** a provider method. `IBlockchainProvider`
deliberately has **no `VerifyBridgeProofAsync`** — it was removed in B2 because the
provider overrides returned `true` unconditionally (an always-true verifier that lies).
The single hardened verification path is:

    Services/WormholeAdapter.cs → Services/Wormhole/Secp256k1VaaSignatureVerifier.cs

which does a fail-closed guardian-quorum secp256k1 check over the canonical VAA digest.
Do **not** reintroduce a provider-level verifier. No always-true boolean may exist on a
verification path anywhere in the tree.

Call-site check performed at removal time: `grep '\.VerifyBridgeProofAsync('` returned
**zero invocations** — the method was dead on the live path (only the interface decl,
the two overrides, the base virtual, and the G7 test stub referenced it). All removed.

### Lock / burn (real broadcast vs fail-closed)

The bridge orchestrator (`Services/CrossChainBridgeService.cs`, trusted flow) calls:
- `sourceProvider.LockForBridgeAsync(tokenId, vaultAddress, amount, targetChain, targetRecipient)`
- `targetProvider.MintWrappedAsync(...)`
- `targetProvider.BurnWrappedAsync(...)` (reverse flow)

These MUST either broadcast a real transaction and return the real tx id, or fail loud.
A fabricated-success `Ok(op-id)` on a value path is forbidden (it makes the bridge record
value that never moved).

**Algorand — REAL.** Uses the existing `BuildSignSubmitAsync` pipeline
(build typed txn → canonical msgpack encode → sign via custody choke point → broadcast →
poll to confirmation). Mirrors the mint/transfer/ASA-create precedents.
- `LockForBridgeAsync` = platform-signed `AssetTransferTransaction` moving `amount` of the
  ASA into the bridge `vaultAddress` (custodial-trusted model: the platform custodies the
  locked value; the vault must have opted into the ASA out-of-band).
- `BurnWrappedAsync` = platform-signed `AssetDestroyTransaction` on the platform-managed
  wrapped ASA (the wrapped ASA is created by `MintWrappedAsync` with the platform as
  manager/reserve). Destroy requires the platform to hold the full outstanding supply;
  otherwise the tx is rejected on-chain and the error surfaces (fail-loud).
- Sender address is derived from the **same** platform mnemonic the signer uses
  (`AZOA:Algorand:PlatformMnemonic`, via `ResolvePlatformAddress()`) so Sender == signing
  key. Missing mnemonic ⇒ **fail-closed** config error, never a fabricated lock.
- Idempotency/no-double-broadcast: `BuildSignSubmitCoreAsync` never auto-retries after the
  POST /v2/transactions send (`RetrySafety.Broadcast`); a confirm-timeout returns the tx id
  with `OperationStatus.PendingConfirmationMarker` (success carrying the hash) so the caller
  records Pending + TxHash and reconciliation settles it — a slow-but-valid tx is never
  re-sent and never false-Failed.

**Solana — FAIL-CLOSED.** `LockForBridgeAsync`, `BurnWrappedAsync`, and `MintWrappedAsync`
return explicit errors. The Solana provider has **no** build→sign→submit pipeline: no
signer factory / custody seam is injected, and `SolanaTransactionSigner.Sign` is itself a
fail-loud stub (owned by the B1 signer worker). Rather than fabricate a success, these
refuse. They become real once (a) the Solana signer is real AND (b) a provider-side
SPL transfer/mint/burn pipeline is built to the canonical-byte contract below.

### Solana canonical-byte contract (coordination with the B1 signer worker)

`ITransactionSigner.Sign(byte[] canonicalTxn, SigningKeyMaterial key)` is chain-agnostic:
the **provider** owns producing `canonicalTxn`; the **signer** owns the Ed25519 signature
over exactly those bytes. For Solana the contract this side assumes (and any real Solana
provider pipeline MUST produce) is:

- `canonicalTxn` = the **serialized Solana message** (NOT a full transaction, NOT base64):
  the wire-format message = `[header (3 bytes)][account keys][recent blockhash (32 bytes)]
  [compiled instructions]`, serialized with the standard short-vec (compact-u16) length
  prefixes, exactly the byte sequence a Solana validator hashes and that clients sign.
- The signer returns the **fully-assembled submittable wire transaction** —
  `[compact-u16 signature count][64-byte Ed25519 signatures...][message bytes]`,
  produced by `Transaction.Populate(message, [signature]).Serialize()`. The provider
  does **NOT** reassemble anything: it base64/base58-encodes the returned bytes as-is
  and submits them straight to the RPC `sendTransaction` boundary. (This matches the
  shipped `SolanaTransactionSigner` and `Providers/Blockchain/Solana/AGENTS.md §signer`
  — the signer owns wire framing, not just the raw signature.)
- The signing key is the fee-payer / first required signer; the provider MUST place that
  pubkey first in the message account-keys so signature order matches. The signer
  fails closed if `Message.AccountKeys[0]` is not the signing key's public key.

If the B1 signer worker documents a different canonical shape (e.g. it expects the full
signed-tx skeleton), reconcile HERE first — the provider side must match byte-for-byte, or
signatures will be valid over the wrong bytes and every Solana bridge tx will be rejected.
Until a real Solana pipeline is built to this contract, the Solana bridge primitives stay
fail-closed (above).

### Security invariants (reviewer checklist)
- No fabricated-success return on any lock/burn/mint value path (Algorand broadcasts;
  Solana fails closed).
- No always-true verifier anywhere (provider-level verify removed; Wormhole path is the
  only verifier and is fail-closed).
- Missing signer/config ⇒ explicit error, never a silent fake-Ok.
- Real tx hash (from `BuildSignSubmitAsync`) is returned on success, not a synthetic op-id.
- No double-broadcast: broadcast step is non-retrying; confirm-timeout ⇒ pending marker.
