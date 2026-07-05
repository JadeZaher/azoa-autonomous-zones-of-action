# Custody Key Management — Specification

## Goal

Define the **custody policy/lifecycle layer** that sits on top of the real
signing primitive: how a per-Avatar wallet's encrypted private key is resolved,
decrypted just-in-time to sign, and zeroed afterward; how ownership is enforced
on every resolve; and how the signer obtains keys through a single abstraction
that is agnostic to whether the key belongs to a user or to the platform.

This is **not** the keygen fix. Producing real, spendable keypairs (BIP39
mnemonics, real Ed25519 / secp256k1 derivation) is the sibling
`signing-core-keystone` track. This track is the **management seam around**
that primitive — it must land *after* real keys exist, because resolving and
zeroing a placeholder key has no security value.

## Background

AZOA already has a working **encryption-at-rest** primitive. `WalletKeyService`
(`Core/WalletKeyService.cs`) performs AES-256-GCM encrypt/decrypt of private
keys and seed phrases:

- `EncryptPrivateKey` / `DecryptPrivateKey` (`Core/WalletKeyService.cs:39-48`)
- `EncryptSeedPhrase` / `DecryptSeedPhrase` (`Core/WalletKeyService.cs:51-60`)
- `AesGcmEncrypt` / `AesGcmDecrypt` (`Core/WalletKeyService.cs:178-213`) — packs
  `nonce(12) + tag(16) + ciphertext`, 96-bit nonce, 128-bit tag.

The data-key is derived as `SHA-256(config "AZOA:WalletEncryptionKey")`
(`Core/WalletKeyService.cs:15-20`). The service is registered as a singleton
(`Program.cs:370`). **This encryption layer is real and good and is NOT in
scope to replace** — only to wrap.

Storage is SurrealDB only. The wallet record carries the encrypted material as
`[JsonIgnore]` fields on the domain model (`Models/Wallet.cs:16-19`) and as
`option<string>` columns on the persisted POCO
(`Persistence/SurrealDb/Models/Wallet.cs:76-82`). Wallets are typed
`External` (no platform-held keys) or `Platform` (platform-held encrypted keys)
via the `WalletType` enum (`Core/WalletType.cs:6-13`).

### Where keys flow today

- **Generation** writes encrypted material: `WalletManager.GenerateWalletAsync`
  (`Managers/WalletManager.cs:218-255`) calls `GenerateKeypair`, then
  `EncryptPrivateKey` / `EncryptSeedPhrase` and stores `WalletType.Platform`
  (`:241-243`).
- **Connect** stores no keys: `ConnectWalletAsync`
  (`Managers/WalletManager.cs:259-301`) creates a `WalletType.External` row.
- **Export** is the only existing decrypt-to-cleartext path:
  `ExportWalletAsync` (`Managers/WalletManager.cs:305-347`). It is already
  IDOR-guarded — ownership is checked by `wallet.AvatarId != avatarId`
  (`:313-314`), restricted to `WalletType.Platform` (`:316-317`), and returns
  cleartext exactly once with a "Handle with extreme care" message (`:340`).

### The gap this track closes

There is currently **no signer-facing path** to obtain a decrypted signing key.
The only decrypt path is `ExportWalletAsync`, which is a user-initiated *export*
to cleartext — wrong semantics for an internal signer (it returns the key to the
caller, has no zeroing contract, and is a public manager method). When
`signing-core-keystone` lands real keys, the provider that signs a transaction
needs a **narrow, ownership-checked, decrypt-then-zero** custody resolver — not
direct store access and not `ExportWalletAsync`.

### Why this is custody-correctness, not a feature

The placeholder keygen in `signing-core-keystone`'s scope produces **unspendable
addresses** today: `DeriveEd25519PublicKey` uses HMAC-SHA512
(`Core/WalletKeyService.cs:116-129`), `DeriveSecp256k1PublicKey` uses HMAC-SHA256
(`Core/WalletKeyService.cs:133-142`), and `GenerateMnemonic` word-indexes a
list instead of computing a BIP39 checksum (`Core/WalletKeyService.cs:478-489`).
Once those become real, a wallet's private key is **spendable value**, and the
discipline for handing it to a signer — decrypt JIT, never log, zero after use,
verify ownership first — is itself a pre-value-flow correctness requirement.

## Custody model (formal definition)

This track formalizes **per-user custody as the default**:

1. **Each `Platform` wallet has its own keypair.** No shared platform key signs
   on behalf of users. The private key lives only as AES-256-GCM ciphertext at
   rest (`encrypted_private_key`, `Persistence/SurrealDb/Models/Wallet.cs:76-78`).

2. **Decrypt is just-in-time.** The cleartext key exists only inside the signing
   call stack, never persisted, never returned to an HTTP response, never logged.

3. **Zero after use.** The decrypted key bytes are overwritten before the
   `using`/`finally` scope exits. The lifetime is **decrypt → sign → zero**,
   bounded to a single signing operation.

4. **Ownership is checked on every resolve.** The resolver replicates the
   `ExportWalletAsync` IDOR guard (`Managers/WalletManager.cs:313-314`): a key
   is only released for `walletId` if the wallet's `AvatarId` equals the
   authenticated `avatarId`. The caller-supplied avatar identity, not a body
   field, is the authority (STARODK IDOR precedent).

5. **The signer never touches storage.** All key access goes through one
   abstraction (`IKeyCustodyService`), so there is a single audited choke point
   for decrypt/zero/ownership.

### Decrypt → sign → zero lifetime (the contract the signer calls)

```
IKeyCustodyService.WithSigningKeyAsync(walletId, avatarId, sign):
  1. load wallet by walletId            (store read)
  2. assert wallet.AvatarId == avatarId (IDOR guard — else fail, no decrypt)
  3. assert wallet.WalletType == Platform OR is the platform pseudo-wallet
  4. cleartext = DecryptPrivateKey(wallet.EncryptedPrivateKey)   [JIT]
  5. try:    result = sign(cleartext-key-material)
     finally: zero(cleartext bytes)     [always, even on signer throw]
  6. return result   (signature/tx — NEVER the key)
```

The signer (`signing-core-keystone`) passes a `sign` delegate and receives only
the signature. It never sees a store handle, never sees `EncryptedPrivateKey`,
and cannot retain the cleartext past the callback. This is a *higher-order*
custody contract — the key never escapes the resolver's `finally`.

## Key abstraction (`IKeyCustodyService`)

A new interface the signer depends on instead of `IWalletStore` +
`WalletKeyService` directly:

- `Task<AZOAResult<T>> WithSigningKeyAsync<T>(Guid walletId, Guid avatarId, Func<byte[], Task<T>> sign)`
  — the decrypt→sign→zero higher-order path above. Returns the signer's result,
  never the key.
- `Task<AZOAResult<bool>> CanSignAsync(Guid walletId, Guid avatarId)` — the
  ownership/eligibility predicate (no decrypt), so callers can pre-flight without
  touching key material.

Concrete `KeyCustodyService` composes the existing primitives — `IWalletStore`
for the record and `WalletKeyService.DecryptPrivateKey`
(`Core/WalletKeyService.cs:45-48`) for the cleartext. It is the **only** type
outside `WalletManager.ExportWalletAsync` permitted to call `DecryptPrivateKey`.

## Platform-signer seam (platform pseudo-wallet)

Per-user is the default, but some operations are **platform-owned** — e.g. a
soulbound mint where the platform is manager/clawback authority. These must
resolve through the **same** `IKeyCustodyService` so the signer stays agnostic.

The platform key is modeled as a **platform pseudo-wallet**: a reserved
`walletId` (and/or a `CanSignAsync` branch) that resolves to the platform
mnemonic instead of an Avatar's `encrypted_private_key`. The precedent for a
config-sourced platform mnemonic already exists — the Algorand faucet reads
`Blockchain:Faucet:Algorand:Mnemonic` (`Core/AlgorandFaucet.cs:45`). The
platform custody branch resolves a platform signing key from config the same
way, encrypts/decrypts it through the same `WalletKeyService`, and yields it via
the same decrypt→sign→zero contract. **The signer cannot tell** whether it
signed with a user key or the platform key — only `IKeyCustodyService` knows.

The IDOR guard for the platform pseudo-wallet is a **role check**, not an
`AvatarId` equality check: only a caller with platform/manager authority may
resolve it. This is documented here and stubbed; enforcement detail is recorded
in `plan.md`.

## Key rotation / re-encryption

Rotating `AZOA:WalletEncryptionKey` (the source secret at
`Core/WalletKeyService.cs:15-20`) must **re-wrap** every stored ciphertext under
the new data-key without ever exposing cleartext beyond a transient in-process
buffer. The operation is:

```
rewrap(oldKeyService, newKeyService, wallet):
  cleartextPk   = oldKeyService.DecryptPrivateKey(wallet.EncryptedPrivateKey)
  wallet.EncryptedPrivateKey = newKeyService.EncryptPrivateKey(cleartextPk)
  (same for EncryptedSeedPhrase, if present)        zero cleartext buffers
  persist wallet
```

This track delivers a **documented design + a method stub**
(`IKeyCustodyService.RewrapAsync` or a dedicated `KeyRotationService`), not a
live admin endpoint — rotation is a real operational need but the orchestration
(dual-key window, batch re-wrap, rollback) is its own follow-up. The stub must
have a unit test proving a value encrypted under key A decrypts after re-wrap
under key B.

## KMS / HSM — explicit deploy-stub (mainnet blocker)

The current data-key is `SHA-256(config secret)` (`Core/WalletKeyService.cs:20`),
with the secret in `appsettings` / env. **This is not production-grade custody.**
A config-string-derived symmetric key has no hardware boundary, no audited
access, no per-operation authorization, and is exfiltrable with the config.

**Mainnet value flow is BLOCKED until a KMS/HSM-backed key store replaces the
`SHA-256(config-secret)` derivation.** `IKeyCustodyService` is the seam that makes
that swap localized: a future `KmsKeyCustodyService` implements the same interface
and the signer is unchanged. This aligns with the repo's existing pre-launch risk
posture — see the `bridge-unsafe-pre-launch` memory ("no idempotency/replay/
atomicity; Tier 0 before any value flows") and the `api-safety-hardening` track
(`conductor/tracks/api-safety-hardening/spec.md:1-16`, "safe to move real
cross-chain value **before launch** … Tier 0"). This item is added to the
deploy-stub list and must be recorded in `conductor/DEPLOY-STEPS-TODO.md`
(create it if absent) as a hard mainnet gate.

## Acceptance criteria

- [ ] Custody model documented in-repo: per-user-default, decrypt→sign→zero
      lifetime, ownership-on-resolve, single choke point. (This spec is the
      authoritative source; link it from `tracks.md`.)
- [ ] `IKeyCustodyService` defined with `WithSigningKeyAsync<T>` (higher-order,
      key never returned) and `CanSignAsync`.
- [ ] `KeyCustodyService` implemented composing `IWalletStore` +
      `WalletKeyService.DecryptPrivateKey`; it is the only caller of
      `DecryptPrivateKey` besides `ExportWalletAsync`.
- [ ] IDOR guard on resolve replicates `ExportWalletAsync`
      (`Managers/WalletManager.cs:313-314`): different-avatar resolve returns an
      error and performs **no decrypt**.
- [ ] `Platform`-only enforcement on resolve (mirror
      `Managers/WalletManager.cs:316-317`); `External` wallets are never
      resolvable for signing.
- [ ] Decrypted key bytes are zeroed in a `finally`, even when the `sign`
      delegate throws. Unit test asserts the buffer is cleared and that a
      throwing signer still triggers zeroing.
- [ ] Platform pseudo-wallet seam documented and stubbed: a platform-authority
      caller resolves a config-sourced platform key through the **same**
      interface (precedent `Core/AlgorandFaucet.cs:45`); a non-platform caller
      cannot.
- [ ] Key-rotation re-wrap design documented + method stub present, with a unit
      test: value encrypted under key A is recoverable after re-wrap under key B.
- [ ] **No cleartext key, seed phrase, or decrypted byte buffer is ever logged.**
      Grep the new code for log calls touching key material; none permitted.
      The resolver returns signatures/tx only, never key bytes.
- [ ] KMS/HSM mainnet gate recorded in `conductor/DEPLOY-STEPS-TODO.md` and
      cross-referenced from this spec.
- [ ] `dotnet build` — zero warnings (nullable enabled, per
      `conductor/workflow.md:18`).
- [ ] `dotnet test` — green; new unit tests cover the custody resolver, the
      IDOR guard, the zeroing-on-throw contract, and the rotation re-wrap.
- [ ] `tracks.md` row for this track moves to `[x]` Shipped.

## Out of scope

- **Real keypair generation** (BIP39, real Ed25519/secp256k1). That is
  `signing-core-keystone` — this track *depends* on it and resolves whatever
  keys it produces.
- **A live KMS/HSM integration.** Documented as a deploy-stub + mainnet gate
  only; the production key store is its own track.
- **A live key-rotation admin endpoint / batch orchestration.** Design + stub +
  unit test only.
- **Changing the AES-256-GCM encryption primitive**
  (`Core/WalletKeyService.cs:178-213`) — it is correct and reused as-is.
- **The existing `ExportWalletAsync` user-export path** — unchanged; the
  custody resolver is a separate signer-facing path.
- **Frontend changes** (no UI surface for internal signing custody).

## Tier

**Tier 0** — custody correctness is a pre-value-flow blocker. Handing a real,
spendable key to a signer without an ownership-checked, zero-after-use,
never-logged contract is a value-flow hazard on the same footing as the
`api-safety-hardening` bridge defects.

## Dependencies

- **`signing-core-keystone` (must land first).** Until keygen produces real,
  spendable keys (replacing the placeholders at
  `Core/WalletKeyService.cs:116-129`, `:133-142`, `:478-489`), there is nothing
  spendable to custody and this track has no security value.
- Reuses (no change): `WalletKeyService` AES-GCM primitive
  (`Core/WalletKeyService.cs:39-60,178-213`), the `Platform` wallet type
  (`Core/WalletType.cs:6-13`), and the encrypted-material columns
  (`Persistence/SurrealDb/Models/Wallet.cs:76-82`).
- Risk-context siblings (referenced, not blocking): `api-safety-hardening`
  (`conductor/tracks/api-safety-hardening/spec.md`) and the
  `bridge-unsafe-pre-launch` memory establish the "Tier 0 before value flows"
  posture this track extends to key custody.
