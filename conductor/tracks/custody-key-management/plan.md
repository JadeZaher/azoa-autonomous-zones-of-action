# Custody Key Management — Plan

Source spec: [spec.md](spec.md)
Depends on: `signing-core-keystone` (real keys) — **must land first**.
Risk context: `conductor/tracks/api-safety-hardening/spec.md`,
`bridge-unsafe-pre-launch` memory.

## Decisions to record before starting

The following require a choice between valid approaches. Record the decision in
this file (replace the `[ decision ]` marker) before executing the corresponding
phase.

| Topic | Decision required |
|-------|-------------------|
| Resolver shape | **DECIDED: higher-order `WithSigningKeyAsync<T>(walletId, avatarId, Func<byte[],Task<T>>)`** — the key never leaves the resolver's `finally`, so "zero after use" is structurally enforced. This matches the spec's decrypt→sign→zero contract; the signer (`signing-core-keystone`) passes a `sign` delegate and receives only its result. The disposable-lease alternative was rejected because it relies on the caller disposing. |
| Platform pseudo-wallet identity | **DECIDED: reserved sentinel `walletId` (fixed `Guid` constant `KeyCustodyService.PlatformWalletId`) special-cased in `KeyCustodyService`.** Keeps the platform key out of the per-user wallet table; resolved from config (`AZOA:Algorand:PlatformMnemonic`), not from a persisted row. A persisted system-Avatar row was rejected as heavier and as putting a value-bearing platform key into the per-user store. |
| Platform-authority guard | **DECIDED: (c) restrict platform resolution to internal manager callers; never expose on a controller.** Modeled as a separate `WithPlatformSigningKeyAsync<T>(bool isPlatformContext, ...)` method that takes an explicit caller-asserted platform-authority flag. Only platform-authority managers may call it; a `false` (non-platform) assertion returns an error and performs no decrypt. There is deliberately no controller surface for platform-key resolution — smallest attack surface. |
| Rotation delivery | **DECIDED: `RewrapAsync` method stub on `IKeyCustodyService`.** Spec requires *design + stub + unit test* only. The stub accepts an explicit old/new `WalletKeyService` pair so the unit test can prove a value encrypted under key A decrypts after re-wrap under key B. Dual-key window / batch re-wrap / rollback orchestration is documented as the follow-up (not shipped). |

---

## Phase 0 — Gate on dependency

- [ ] **BLOCKER:** confirm `signing-core-keystone` has shipped and
  `WalletKeyService.GenerateKeypair` (`Core/WalletKeyService.cs:27-36`) produces
  real, spendable keys — i.e. `DeriveEd25519PublicKey`
  (`Core/WalletKeyService.cs:116-129`), `DeriveSecp256k1PublicKey` (`:133-142`),
  and `GenerateMnemonic` (`:478-489`) are no longer placeholders. **Do not start
  Phase 1 until this is true** — resolving/zeroing a placeholder key has no
  security value.

## Phase 1 — Custody abstraction

- [ ] Define `IKeyCustodyService` (new file under `Interfaces/` or
  `Interfaces/Services/`, matching repo convention):
  - `Task<AZOAResult<T>> WithSigningKeyAsync<T>(Guid walletId, Guid avatarId, Func<byte[], Task<T>> sign)`
  - `Task<AZOAResult<bool>> CanSignAsync(Guid walletId, Guid avatarId)`
  - (rotation stub added in Phase 4 — see decisions table)
- [ ] Implement `KeyCustodyService` composing `IWalletStore` +
  `WalletKeyService`. Resolve flow (mirror `ExportWalletAsync`,
  `Managers/WalletManager.cs:305-347`):
  1. `_walletStore.GetByIdAsync(walletId)` → not-found ⇒ error.
  2. **IDOR guard:** `wallet.AvatarId != avatarId` ⇒ error, **return before any
     decrypt** (mirror `Managers/WalletManager.cs:313-314`).
  3. **Type guard:** `wallet.WalletType != WalletType.Platform` ⇒ error (mirror
     `Managers/WalletManager.cs:316-317`); `External` is never signable.
  4. Empty `EncryptedPrivateKey` ⇒ error (mirror `:319-320`).
  5. `var key = DecryptPrivateKey(wallet.EncryptedPrivateKey)`
     (`Core/WalletKeyService.cs:45-48`) — JIT, into a `byte[]`.
  6. `try { return await sign(key); } finally { CryptographicOperations.ZeroMemory(key); }`
- [ ] `KeyCustodyService` is the **only** caller of `DecryptPrivateKey` besides
  `ExportWalletAsync`. (Grep-verify in Phase 6.)
- [ ] Register in DI next to the existing key service
  (`Program.cs:370` registers `WalletKeyService` as a singleton —
  `KeyCustodyService` registered alongside; lifetime per repo convention for
  store-dependent services).

## Phase 2 — Platform-signer seam

- [ ] Add the platform pseudo-wallet branch to `KeyCustodyService` per the
  decisions table (sentinel `walletId` vs persisted system-Avatar row).
- [ ] Platform key source: resolve a platform mnemonic from config the same way
  the faucet does — `Blockchain:Faucet:Algorand:Mnemonic`
  (`Core/AlgorandFaucet.cs:45`) is the precedent; choose/record the platform
  custody key's config path. Encrypt/decrypt it through the **same**
  `WalletKeyService` and yield it via the **same** decrypt→sign→zero contract.
- [ ] Platform-authority guard per decisions table — a non-platform caller
  resolving the platform pseudo-wallet returns an error and performs no decrypt.
- [ ] Assert by test that the **signer cannot distinguish** a user-key signature
  from a platform-key signature (same `WithSigningKeyAsync` surface).

## Phase 3 — Decrypt→sign→zero hardening

- [ ] Confirm cleartext key material exists only as a local `byte[]` inside
  `WithSigningKeyAsync`; never assigned to a field, return value, or response DTO.
- [ ] `CryptographicOperations.ZeroMemory` (or equivalent) runs in `finally` so
  a throwing `sign` delegate still zeroes.
- [ ] If the key is decrypted as a `string` (DecryptPrivateKey returns hex
  string, `Core/WalletKeyService.cs:45-48`), document that strings are immutable
  and cannot be reliably zeroed — convert to `byte[]` at the resolver boundary
  and zero the bytes; record this caveat inline. (Decide in Phase 1 review
  whether to add a `byte[]`-returning decrypt overload to `WalletKeyService`.)

## Phase 4 — Key rotation re-wrap

- [ ] Add rotation per decisions table (`RewrapAsync` stub on
  `IKeyCustodyService` **or** `KeyRotationService`):
  - signature: re-wrap a wallet's `EncryptedPrivateKey` (+ `EncryptedSeedPhrase`,
    `Persistence/SurrealDb/Models/Wallet.cs:76-82`) from an old data-key to a new
    one, zeroing the transient cleartext buffer.
  - body is a documented stub (XML doc describing dual-key window / batch /
    rollback as the follow-up), wired enough to be unit-testable.
- [ ] Document that the data-key source is `AZOA:WalletEncryptionKey`
  (`Core/WalletKeyService.cs:15-20`) and that rotation = rotate-secret +
  re-wrap-all.

## Phase 5 — KMS/HSM deploy-stub

- [ ] Create/append `conductor/DEPLOY-STEPS-TODO.md` with a **hard mainnet gate**:
  "Custody key store: replace `SHA-256(config secret)` derivation
  (`Core/WalletKeyService.cs:20`) with a KMS/HSM-backed key store before any
  mainnet value flow. `IKeyCustodyService` is the swap seam
  (`KmsKeyCustodyService` implements the same interface)."
- [ ] Cross-reference the `bridge-unsafe-pre-launch` memory and
  `conductor/tracks/api-safety-hardening/spec.md:1-16` as the shared Tier 0
  pre-launch posture.
- [ ] Confirm spec.md's "KMS / HSM" section and `DEPLOY-STEPS-TODO.md` agree.

## Phase 6 — Verification

- [ ] `dotnet build` — **zero warnings** (nullable enabled,
  `conductor/workflow.md:18`).
- [ ] `dotnet test` — green. New unit tests:
  - resolver happy path returns the signer's result for an owned `Platform`
    wallet;
  - **IDOR guard:** different `avatarId` ⇒ error and **DecryptPrivateKey is
    never called** (assert via a spy/mock on `WalletKeyService` or
    `IKeyCustodyService` seam);
  - `External` wallet ⇒ error;
  - **zero-on-throw:** a `sign` delegate that throws still zeroes the buffer;
  - platform pseudo-wallet: platform-authority caller succeeds, non-platform
    caller fails;
  - rotation re-wrap: value encrypted under key A is recoverable after re-wrap
    under key B.
- [ ] **No-cleartext-log audit:** grep new files for any log/console/Debug call
  whose argument touches a key, seed phrase, or decrypted buffer — must be zero
  hits.
- [ ] **Single-choke-point audit:** grep the solution for `DecryptPrivateKey` —
  only `WalletManager.ExportWalletAsync` (`Managers/WalletManager.cs:324`) and
  `KeyCustodyService` may call it.
- [ ] Swagger UI launches and lists existing endpoints (no new public endpoint
  expected from this track unless a decision added one) —
  `conductor/workflow.md:20`.
- [ ] Move `tracks.md` row for `custody-key-management` from `[ ]` to `[x]`
  Shipped.

## Commit strategy

Per `conductor/workflow.md:9-15`, prefix `[custody-key-management]`:

- `[custody-key-management] add IKeyCustodyService + decrypt→sign→zero resolver`
- `[custody-key-management] route platform signing through pseudo-wallet seam`
- `[custody-key-management] add key-rotation re-wrap design + stub`
- `[custody-key-management] record KMS/HSM mainnet gate in DEPLOY-STEPS-TODO`
- `[custody-key-management] cover resolver, IDOR guard, zeroing, rotation in tests`

## Known follow-ups (filed separately)

- **KMS/HSM-backed key store** — the production custody key store replacing
  `SHA-256(config secret)`; the actual mainnet unblock. Recorded as a deploy
  gate here, owned by its own future track.
- **Live key-rotation orchestration** — dual-key read window, batch re-wrap of
  all wallets, rollback/abort. This track ships only the design + stub + unit
  test.
- **`byte[]`-returning decrypt overload on `WalletKeyService`** — if Phase 3
  decides the string-immutability caveat warrants a first-class
  `DecryptPrivateKeyBytes` returning zeroable bytes.
