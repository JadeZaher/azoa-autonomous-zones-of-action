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
| Ethereum | hex of 32-byte secp256k1 scalar | placeholder (H1 — not real) | placeholder | (no signer yet) |

Solana keygen (`GenerateSolanaKeypair`) uses `Solnet.Wallet` end to end — real
Ed25519 keypair from a restorable BIP39 mnemonic; no hand-rolled curve math,
base58, or wordlist. The prior HMAC-SHA512 Ed25519 placeholder and the Solana-only
`Base58Encode` were deleted (final-hardening B1). Ethereum keygen remains an
explicit placeholder (deploy-stub H1) — not wired to any signer.

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
