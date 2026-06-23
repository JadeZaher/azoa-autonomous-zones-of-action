# Signing Core Keystone — Specification

## Goal

Give AZOA a **real, server-side transaction-signing primitive** for Algorand,
exposed behind a **chain-agnostic `ITransactionSigner` abstraction**, and replace
the placeholder keypair generation so platform-custodied wallets are actually
spendable. This is the keystone that unblocks every other track in the
ardanova-provider-port initiative: until AZOA can custody a key and sign with
it, AZOA is a metadata store, not a "blockchain provider," and the brand promise
("abstract away the chain — user never sees a key or a signing prompt") cannot be
honored.

Algorand is the first and only chain implemented in this track. The abstraction
is generic so Solana/Ethereum slot in behind the same seam later (one new signer
implementation + one factory registration), per the project's
extend-by-one-DI-line ethos.

## Background

Two independent placeholders make every value-moving Algorand op non-functional
today. **Both must be fixed together** — fixing one without the other still
yields an unspendable wallet or an unsubmittable transaction.

### Defect 1 — keypair generation is a placeholder (unspendable addresses)

`Core/WalletKeyService.cs:116-129`:

```csharp
private byte[] DeriveEd25519PublicKey(byte[] seed)
{
    // In production, use a proper Ed25519 implementation like libsodium or BouncyCastle.
    // ... placeholder that matches the key format ...
    using var hmac = new HMACSHA512(seed);
    var hash = hmac.ComputeHash("ed25519 seed"u8.ToArray());
    var publicKey = new byte[32];
    Array.Copy(hash, 32, publicKey, 0, 32);
    return publicKey;
}
```

The "public key" is HMAC output, **not** the Ed25519 point derived from the seed.
The Algorand address computed from it (`AlgorandAddressFromPublicKey`) therefore
corresponds to no private key anyone can sign with. `GenerateMnemonic`
(`Core/WalletKeyService.cs:478-489`) word-indexes a list rather than producing a
BIP39/Algorand-25-word mnemonic, so the seed phrase is non-restorable in any
standard wallet. `DeriveSecp256k1PublicKey` (`:133-142`) has the same HMAC
placeholder shape for Ethereum (out of scope here, but noted).

### Defect 2 — the Algorand provider does not sign or submit

`Providers/Blockchain/Algorand/AlgorandProvider.cs`:

- `TransferAsync` (`:156-164`) returns a hard error: *"Transfers require signing
  with the sender's private key. In production, implement client-side signing or
  use a KMS service."*
- `BurnAsync` (`:148-154`) returns a hard error referring the caller to
  `LockForBridgeAsync`.
- `MintAsync` (`:135-146`) delegates to `CreateASAAsync`, which (per the project
  exploration) only **records** the request for client-side signing — it does not
  build a canonical transaction, sign it, or submit it.

The ASA capability module (`OptInAsync`, `CreateASAAsync`) follows the same
"recorded for client-side signing" pattern.

### What is already in place (and is good)

- **The SDKs are already referenced.** `AZOA.WebAPI.csproj:35`
  `Algorand2 2.0.0.2024051911` (real `Algorand.Account(mnemonic)` + canonical
  msgpack), and `:34` `BouncyCastle.Cryptography 2.6.2` aliased `BCCrypto2`. The
  hard part — resolving the legacy BouncyCastle 1.8.8 collision the Algorand2/Solana
  SDKs transitively bundle — is **already solved** via the `extern alias` precedent
  established for `Services/Wormhole/Secp256k1VaaSignatureVerifier.cs`
  (`AZOA.WebAPI.csproj:25-34`). No new dependency is required.
- **AES-256-GCM key encryption at rest is real** — `Core/WalletKeyService.cs`
  `EncryptPrivateKey`/`DecryptPrivateKey`/`EncryptSeedPhrase`/`DecryptSeedPhrase`,
  keyed by `SHA-256(config "AZOA:WalletEncryptionKey")`. This track does not touch
  the encryption envelope; it fixes what gets *put inside* it.
- **A confirmation-polling orchestration shape exists to mirror.** ArdaNova's
  `AlgorandService` (`C:\Users\atooz\Documents\Escherbridge\ardanova\ardanova-backend-api-mcp\api-server\src\ArdaNova.Infrastructure\Algorand\AlgorandService.cs`)
  has a clean params→build→sign→submit→confirm-poll→extract-asset-id flow
  (`:113-168`, `:487-604`). **Caveat — its `SignTransaction` (`:777-797`) is ALSO a
  stub** (wraps bytes in a `"TX"` prefix; build methods emit JSON not msgpack at
  `:656-770`). We port the *orchestration structure*, never its crypto. The real
  crypto comes from `Algorand2` + BouncyCastle.

## Scope

### 1. Generic signing abstraction — `ITransactionSigner`

A chain-agnostic interface in the provider/core layer, e.g.:

```csharp
public interface ITransactionSigner
{
    string ChainType { get; }
    // Sign canonical chain-native transaction bytes with the key material the
    // custody layer hands in; return the submittable signed envelope bytes.
    AZOAResult<byte[]> Sign(byte[] canonicalTxn, SigningKeyMaterial key);
}
```

- `SigningKeyMaterial` carries a **decrypted private key as `byte[]`** (not a
  `string` — see the zeroing constraint below), supplied by the custody layer.
  The signer **never** reaches into wallet storage or `WalletKeyService` itself —
  custody resolution is the sibling `custody-key-management` track's job. This
  track defines the seam and an interim resolution path good enough for tests.
- Registered/selected by `ChainType` so the provider obtains its signer the same
  way the chain factory hands out providers
  (`Core/BlockchainProviderFactory.cs` pattern).

### 2. Real Algorand keypair generation

Replace the placeholders in `Core/WalletKeyService.cs`:

- `GenerateAlgorandKeypair` must derive the **real Ed25519 public key** from the
  seed (via BouncyCastle `Ed25519PrivateKeyParameters.GeneratePublicKey()` under
  the `BCCrypto2` alias, or via `Algorand2`'s `Account`), compute the **correct
  Algorand address** (SHA-512/256 checksum + base32), and produce a **valid
  Algorand 25-word mnemonic** (via `Algorand2`'s mnemonic API).
- A round-trip test must prove: generate → address matches what `Algorand2`
  derives from the same seed/mnemonic → re-import mnemonic yields the same address.
- `DeriveEd25519PublicKey`'s placeholder is removed; the `secp256k1` and Solana
  placeholders are **left as-is but explicitly documented as deploy-stubs**
  (Solana/Ethereum keygen are out of scope for this Algorand-first track and go on
  the deploy-stub list).

### 3. Real Algorand signing implementation — `AlgorandTransactionSigner`

An `ITransactionSigner` for Algorand that uses `Algorand2`/BouncyCastle to:

- build **canonical msgpack** transactions (asset-config/create, asset-transfer,
  asset-destroy, asset-clawback, opt-in) — replacing the JSON-bytes approach
  ArdaNova used,
- sign with the supplied Ed25519 private key,
- return the canonical signed envelope bytes ready for Algod `POST /v2/transactions`.

### 4. Wire the Algorand provider to actually transact

In `Providers/Blockchain/Algorand/AlgorandProvider.cs`, replace the hard-error
stubs with a real params→build→sign→submit→confirm flow (mirroring ArdaNova's
*structure* at `AlgorandService.cs:487-604`, but with real crypto):

- `TransferAsync` (`:156-164`) — real ASA transfer.
- `BurnAsync` (`:148-154`) — real ASA destroy (manager-signed).
- `MintAsync` / `CreateASAAsync` — real ASA create, returns the on-chain asset id
  from the confirmation, persisted via the existing `BlockchainOperation` /
  `IBlockchainOperationStore` path (so the operation record carries the real
  tx hash + asset id, not a placeholder).
- `OptInAsync` — real opt-in.
- A **soulbound-mint** code path (total=1, decimals=0, defaultFrozen=true,
  manager/freeze/clawback = platform) so the NFT-membership-credential use case is
  served by a real on-chain ASA. (The *credential domain semantics* stay in the
  caller/ArdaNova; AZOA provides the soulbound-ASA primitive.)
- Each value-moving call must respect the existing `RetrySafety.Broadcast`
  contract (`Core/Blockchain/Base/BaseBlockchainProvider.cs`) — never retry a
  post-broadcast transaction.

### 5. Brand & genericity guardrails

- No `ArdaNova`-branded strings, URLs, or labels anywhere in the ported code
  (ArdaNova hardcodes `https://ardanova.com/credentials/...` and
  `"ArdaNova Membership"` — these become caller-supplied parameters / generic
  defaults).
- Nothing Algorand-specific leaks into the `ITransactionSigner` contract.

## Acceptance criteria

- [ ] `ITransactionSigner` (chain-agnostic) defined and selected by `ChainType`
      via the factory pattern; `AlgorandProvider` obtains its signer through it and
      never touches `WalletKeyService` directly.
- [ ] `Core/WalletKeyService.GenerateAlgorandKeypair` produces a **real** Ed25519
      keypair, **correct** Algorand address, and a **valid 25-word** mnemonic; the
      HMAC placeholder `DeriveEd25519PublicKey` is removed.
- [ ] Round-trip test: seed → address equals `Algorand2`-derived address;
      mnemonic re-import reproduces the same address.
- [ ] `AlgorandTransactionSigner` builds **canonical msgpack** (not JSON) and signs
      with Ed25519; a known-vector test asserts the signed bytes match an
      `Algorand2`-signed reference for the same inputs.
- [ ] `AlgorandProvider.TransferAsync` / `BurnAsync` / `MintAsync` / `CreateASAAsync`
      / `OptInAsync` perform real build→sign→submit→confirm; the hard-error returns
      at `:148-164` are gone.
- [ ] Soulbound-ASA mint path exists (total=1, decimals=0, defaultFrozen=true,
      platform as manager/freeze/clawback), parameterized (no hardcoded brand).
- [ ] Real tx hash + asset id are persisted on the `BlockchainOperation` record;
      `GetTransactionStatusAsync` reflects the real confirmation.
- [ ] `RetrySafety.Broadcast` honored on every signed/submitted call (no
      post-broadcast retry).
- [ ] No new NuGet dependency added (uses existing `Algorand2` + `BCCrypto2`); the
      `extern alias` collision strategy is followed if BouncyCastle 2.x types are
      referenced directly.
- [ ] `dotnet build` green, **zero warnings** (nullable enabled).
- [ ] `dotnet test` green; new unit tests cover keygen round-trip, msgpack/sign
      vector, and provider transact paths (mock Algod/Indexer HTTP).
- [ ] Grep: no `ArdaNova`/`ardanova.com` strings introduced into AZOA source.
- [ ] `conductor/DEPLOY-STEPS-TODO.md` updated with every stub this track leaves
      (Solana/Ethereum real keygen, KMS custody handoff, soulbound clawback-revoke
      path if deferred, mainnet enablement gate).
- [ ] `conductor/tracks.md` row for `signing-core-keystone` moved to `[x]` Shipped.

## Out of scope

- **Custody policy/lifecycle** (per-user decrypt-to-sign resolver, rotation,
  KMS/HSM) — that is the `custody-key-management` track. This track defines the
  `SigningKeyMaterial` seam and an interim test-grade key supply only.
- **Solana / Ethereum real signing & keygen** — deferred; placeholders documented
  as deploy-stubs.
- **DEX swap/exchange** on Algorand (`ExchangeAsync`/`SwapAsync` at `:166-180`) —
  separate DEX scope.
- **Smart-contract / AVM** deploy + call.
- **The economic/token domain** (dual-gate, allocation, treasury) — stays in
  ArdaNova; AZOA exposes the create/transfer/soulbound primitives only.
- **Mainnet enablement** — real value flow is gated behind custody KMS work; this
  track is correct on testnet/devnet and leaves a mainnet gate on the deploy-stub
  list.

## Tier

**Tier 0 — keystone.** Blocks the value-flow story for the entire initiative.
Must land before `custody-key-management`, `fiat-stripe-bridge`, and any real-mint
use of `kyc-module`.

## Dependencies

None inbound. **Outbound:** `custody-key-management`, `db-only-null-provider`
(aligns to the same signer seam), `fiat-stripe-bridge`, `kyc-module` (real mint)
all build on this.
