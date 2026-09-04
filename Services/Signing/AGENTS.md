# Services/Signing — key generation, encryption, and the byte[] decrypt path

`WalletKeyService` is the single place that generates chain keypairs and
encrypts/decrypts stored key material (AES-256-GCM, key derived from
`AZOA:WalletEncryptionKey` via SHA-256).

## Keygen representations (pin these — the signers reconstruct from them)

Each chain's persisted private key MUST be the exact representation its
`ITransactionSigner` reconstructs from. Round-trip tests pin the invariant.

| Chain | Persisted private key (`privateKeyHex`) | Address | Mnemonic | Signer reconstructs |
| :--- | :--- | :--- | :--- | :--- |
| Algorand | hex of Algorand2 `ClearTextPrivateKey` (32-byte Ed25519 seed) | Algorand2 `Address` (SHA-512/256 + base32) | Algorand2 25-word | `new Account(bytes)` |
| Solana | hex of Solnet `PrivateKey.KeyBytes` (**64-byte** secret = 32-byte seed ++ 32-byte pubkey) | Solnet `PublicKey.Key` (base58) | Solnet BIP39 12-word | `new Account(secret64, secret64[32..64])` |
| Ethereum | hex of the raw **32-byte** secp256k1 scalar | EIP-55 checksummed, derived (see §ethereum-derivation) | none (`null`) | `new EthECKey(bytes, isPrivate: true)` |

Solana keygen (`GenerateSolanaKeypair`) uses `Solnet.Wallet` end to end — real
Ed25519 keypair from a restorable BIP39 mnemonic; no hand-rolled curve math,
base58, or wordlist. The prior HMAC-SHA512 Ed25519 placeholder and the Solana-only
`Base58Encode` were deleted (final-hardening B1).

## §ethereum-derivation — the address is derived from the key, not invented

`DeriveEthereumPublicKeyAndAddress(privateKey)` is the single derivation:
uncompressed public key `0x04 ‖ X ‖ Y` from the scalar, then
`address = keccak-256(uncompressedPublicKey[1..])[12..]`, EIP-55 checksummed. It runs
through the same `Nethereum.Signer.EthECKey` that `EthereumTransactionSigner` signs
with and `EthereumProvider.TryDeriveAddress` derives from, so keygen and the value
path cannot disagree about which account a key controls. Nothing here is hand-rolled:
keccak-256 is Nethereum's (NOT SHA3-256, NOT SHA-256) and the checksum is
`AddressUtil.ConvertToChecksumAddress`, the same routine `EthereumAddress` validates
against — a differently-cased address would be REJECTED by the provider.

This replaced a placeholder (chain-value-routes H1) that ran HMAC-SHA256 over the key
for a "public key" and SHA-256 over that for an "address": the stored
`wallet.Address` was unrelated to the stored key, so a per-user transfer signed with
that key would have moved value from a different account than the one recorded.
`EthereumProvider` catches this class of mismatch at signing time and must keep doing
so — it is the last line of defence, not a redundancy.

Two shape rules the signer depends on:
- The persisted scalar is the CSPRNG's 32 bytes verbatim. Never re-encode it through an
  unsigned big-integer form: that strips leading zero bytes, and roughly one draw in 256
  would then fail the signer's exact-32-byte check and produce an unspendable wallet.
- A draw outside `[1, n-1]` is redrawn, never persisted.

**Ethereum keygen returns no recovery phrase (`null`).** A phrase that restores in a
standard wallet needs BIP-39 + BIP-32/BIP-44 (`m/44'/60'/0'/0/0`) derivation, which no
referenced package provides (`Nethereum.HdWallet` is not referenced). The 12-word string
this used to return was worse than nothing: it restored the wallet in no wallet on earth,
and each word was an index over the key bytes, so possessing the "backup" leaked most of
the private scalar. Wiring a real HD path is the follow-up; until then
`Wallet.EncryptedSeedPhrase` is null for Ethereum and export reports no phrase.

## §decrypt-bytes — zeroable `byte[]` decrypt (final-hardening B5)

`DecryptPrivateKeyBytes(encryptedHex)` returns the RAW key bytes in a zeroable
`byte[]`, so the cleartext key never materializes as an immutable `string` on the
signing hot path (a .NET `string` cannot be reliably zeroed). The caller MUST
`CryptographicOperations.ZeroMemory` the returned buffer after use;
`KeyCustodyService.DecryptSignZeroAsync` does exactly this in a `finally`.

## Tenant bootstrap limitation

`WalletKeyService.GenerateCustodialKeypair` is deliberately Algorand-only and
does not derive or return a mnemonic. `WalletManager.BootstrapWalletAsync`
persists no encrypted seed phrase. This path is gated by tenant capabilities to
Development + simulated mode because its private key still transiently exists as
an immutable hex string before config-key encryption. It is not a production
KMS/HSM boundary.

Internals:
- `AesGcmDecryptBytes` decrypts into an owned plaintext `byte[]` (no plaintext
  string). On a GCM tag/format failure it zeroes any transient buffer and rethrows
  — fail-closed, never a partial-plaintext leak.
- `DecodeHexAsciiToRaw` decodes the ASCII-hex plaintext into raw key bytes,
  mirroring the custody layer's `FromHexOrUtf8` contract (hex → decoded; non-hex,
  e.g. a mnemonic, → verbatim bytes). Neither branch allocates a plaintext string.
- The legacy `DecryptPrivateKey` (string) is retained for `RewrapAsync` and
  `WalletManager.ExportWalletAsync`; it now routes through `AesGcmDecryptBytes` and
  zeroes its transient plaintext buffer before returning the string.

Security: no key is ever logged; wrong-key decrypt throws
`AuthenticationTagMismatchException` (never returns garbage plaintext).
