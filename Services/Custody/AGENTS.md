# Services/Custody — key resolution + wrapping-key rotation

## KeyCustodyService — the decrypt→sign→zero chokepoint

The single audited place a decrypted signing key is resolved, handed to a signer,
and zeroed. Invariants (enforced, not documented-only): ownership IDOR check before
any decrypt; only `WalletType.Platform` wallets are signable; live consent gate
consulted before any tenant-driven decrypt; cleartext key exists only as a local
`byte[]` wiped in a `finally` (even on signer throw); the key is never returned,
never logged.

final-hardening B5 changed the hot path to `WalletKeyService.DecryptPrivateKeyBytes`
— the cleartext key no longer materializes as an immutable hex `string`. The raw
key bytes (`DecodeHexAsciiToRaw` output) reach the signer directly and are zeroed
after use. Algorand yields the 32-byte seed; Solana the 64-byte secret.

`RewrapAsync` (the per-wallet rotation primitive) still uses the string
`Decrypt/Encrypt` pair because re-encryption requires a hex string input; only the
byte buffer it decodes is zeroed, the transient hex strings live until GC. This is
acceptable for the rotation path (cold, operator-triggered) but NOT for the signing
hot path — hence the split.

## §rotation — KeyRotationService (final-hardening B5)

Live `AZOA:WalletEncryptionKey` rotation orchestration on top of
`IKeyCustodyService.RewrapAsync`. Endpoint: `POST /api/admin/key-rotation/rotate`
(`[Authorize(Policy="Operator")]`), body `{ newEncryptionKey }`. Returns counts
only (`KeyRotationReport`) — never key material.

Safety properties (the reviewer's checklist):

1. **Dual-key read window.** Each wallet is probed under the NEW key first
   (`IsReadableUnder`: try-decrypt, catch `AuthenticationTagMismatch`). The old key
   service stays live for wallets not yet rotated, so a mixed old/new store is fully
   readable while a rotation is in flight. Rotation is derived from wallet state, so
   there is no separate "which key is active" flag to get out of sync.
2. **Idempotent / resumable.** A wallet already readable under the new key was
   rotated by a prior (possibly crashed) run and is skipped. A re-run after a partial
   failure resumes without double-wrapping. No ledger table is needed — the wallet's
   own ciphertext is the source of truth for "rotated or not".
3. **All-or-nothing rollback.** Every wallet's pre-rotation ciphertext is
   snapshotted before it is mutated; on any per-wallet failure the batch restores all
   already-persisted wallets to their snapshot (`RollbackAsync`, on a fresh
   `CancellationToken.None` so rollback is not itself cancelled). The report's
   `RolledBack` flag records the outcome; if a restore itself fails the message flags
   `ROLLBACK INCOMPLETE — manual review required` and logs at Error.
4. **Fail-closed guards.** Empty new key, missing current key, or a new key identical
   to the current key (which would defeat the dual-key probe) all error before any
   wallet is touched. A wallet unreadable under BOTH keys aborts + rolls back rather
   than being silently skipped.

Two `WalletKeyService` instances are constructed in-process from the old (config)
and new (caller-supplied) secrets — the service does not mutate process config, so
the running app keeps decrypting under the old key until the operator flips
`AZOA:WalletEncryptionKey` and restarts. Operator sequence: run rotate → verify
report → set new `AZOA:WalletEncryptionKey` → restart. Documented for the operator
in `docs/NODE-HOST.md`.

### security-review HIGH-1 — key-LOSS prevention (pending marker + readability sweep)

The prior all-or-nothing rollback still had a fatal gap: if a rotation fails mid-batch
and the *rollback itself* also fails (or the process dies between a successful persist
under the NEW key and the rollback), some wallets can be left readable ONLY under the
new key. If the operator then concludes "rotation aborted" and discards the new key
candidate, those wallets are PERMANENTLY UNREADABLE — irrecoverable fund loss. Two
safety nets sit BENEATH the existing rollback:

1. **Durable pending-rotation marker (`IPendingRotationKeyStore`).** Written BEFORE the
   batch mutates any wallet; refusing to start if it cannot be persisted (an unrecorded
   rotation is exactly the hazard). It lives OUTSIDE SurrealDB
   (`FilePendingRotationKeyStore`, path `AZOA:Rotation:PendingKeyFilePath`) so it
   survives a DB outage that itself might have prompted the rotation. It NEVER stores
   the raw new key — only a **verifier token**: the fixed sentinel
   `azoa-rotation-key-verifier-v1` AES-GCM-encrypted under the new key. A recovery
   holder decrypts the token with a candidate key; a round-trip back to the sentinel
   proves the candidate is the exact key this rotation used. So even in the
   discard-new-key scenario the operator can identify/confirm the right key from the
   marker. The marker is cleared ONLY after the readability sweep passes; a failed
   rotation KEEPS it for recovery.
2. **Post-rotation readability assertion sweep.** After the batch, EVERY wallet is
   re-read from the store and asserted readable under the INTENDED final (new) key. Any
   wallet carrying ciphertext that does not decrypt under the new key throws — the
   rotation fails LOUDLY and the pending marker is retained. This catches a wallet that
   was persisted but is unreadable (e.g. a torn write) that the per-wallet path did not
   surface, before the operator is told "success".

Constructor now takes `IPendingRotationKeyStore` (DI: `AddSingleton<...,
FilePendingRotationKeyStore>()`, a thin stateless file wrapper). The verifier sentinel
is a *public* constant — security is in the key, not the plaintext.

### security-review HIGH-2 — hardened `Operator` authorization (the two admin endpoints)

`POST /api/admin/key-rotation/rotate` and `POST /api/admin/backfill/apply` are the most
destructive surfaces in the tree (both rewrite state across every user). The app
supports BOTH JWT and API-key auth (`ApiKeyAuthenticationHandler` emits "the same
claims as JWT"). Finding: an API key can ONLY ever emit `scope` claims from its stored
`ApiKey.Scopes` CSV — it emits NO `role` claim, is in NO role, and emits NO `is_admin`
claim — so it cannot satisfy the legacy admin checks today. But the old `Operator`
policy asserted nothing about the authentication *scheme*, so a single future change
(e.g. an API key that could carry a role) would silently open the door. Hardened on two
independent axes (`Program.cs` `Operator` policy):

1. **Scheme floor.** Reject any principal carrying `AuthMethod=ApiKey`. Operator
   authority originates ONLY from a JWT. An X-Api-Key can never reach these endpoints.
2. **Explicit operator capability.** Beyond the scheme floor, require a real admin
   signal: the dedicated `operator:admin` scope (`AzoaScopes.Operator`, minted only for
   admins), the `Admin` role, `role=Admin`, or `is_admin=true`.

Defense-in-depth at the source: `AzoaScopes.IsApiKeyIssuableScope` marks `operator:admin`
as forbidden, and `ApiKeyAuthenticationHandler` strips it at claim-emit time — a forged
or misconfigured key CSV containing the literal string still yields no operator claim.
Both endpoints additionally carry `[EnableRateLimiting("financial")]` so the
irreversible operations are throttled like the value-moving endpoints.

### security-review test-gap #2 — key-zeroing on signer throw

`DecryptSignZeroAsync` zeroes the key buffer in a `finally` even when the signer throws.
`KeyCustodyServiceTests.DecryptSignZero_throwing_signer_returns_error_and_zeroes_key_without_leaking`
locks all three properties: clean error result, no key bytes in the surfaced message,
and the handed buffer fully zeroed after a signer throw.

## §platform-key-derivation — the chain-aware platform door

The platform signing key is config-sourced, not a wallet row, and it is **per chain**.
Before this section existed the door was hardcoded to Algorand: it read
`AZOA:Algorand:PlatformMnemonic` and ran that mnemonic through Algorand's account
import, so a Solana or Ethereum platform op received a 32-byte Algorand Ed25519 seed —
useless on Solana (wants Solnet's 64-byte secret) and, worse, *plausible* on Ethereum,
where the same 32 bytes read as a valid secp256k1 scalar controlling a completely
different account.

The shape is a small registry keyed by chain, not conditionals at the call site:

- `IPlatformKeyDerivation` — one chain's rule. Declares its `ChainType`, its own
  `PlatformKeyConfigPath`, a `SecretDescription` safe to print, the exact `KeyLength`,
  and a **total, non-throwing** `TryDerive`.
- `PlatformKeyDerivations.cs` — Algorand (`AZOA:Algorand:PlatformMnemonic`, 25-word
  mnemonic → 32-byte `ClearTextPrivateKey`), Solana (`AZOA:Solana:PlatformMnemonic`,
  BIP-39 → Solnet's 64-byte secret), Ethereum (`AZOA:Ethereum:PlatformPrivateKey`,
  0x-prefixed 32-byte secp256k1 scalar, validated by constructing an `EthECKey`).
- `PlatformKeyDerivationRegistry` — case-insensitive chain lookup, duplicate
  registration throws (mirrors `BlockchainProviderFactory`). `Default` is a static,
  immutable, stateless instance: it needs no DI registration, which keeps it out of the
  app-lifetime provider cache and away from any captive-dependency risk against the
  **scoped** `IKeyCustodyService`.

### Why config paths are per-derivation, not `AZOA:{Chain}:PlatformMnemonic`

Ethereum's platform secret is a raw private key, not a mnemonic, because a BIP-39
phrase only becomes an Ethereum key once a BIP-44 account path is chosen. Guessing the
path produces a perfectly valid key for the wrong account — the exact failure mode this
door exists to prevent. Letting each derivation name its own path also kept the
Ethereum provider's already-shipped `AZOA:Ethereum:PlatformPrivateKey` as the single
source (the two doors must accept byte-identical input or they resolve different
senders — hence the strict, matching `0x` requirement).

### Fail-closed ladder (`KeyCustodyService.ResolvePlatformKeyAsync`)

Authority → chain named → derivation registered → secret configured → derives →
length matches → sign → zero. Each rung returns an error result; none falls through to
another chain. Unregistered chain names the chain and lists the chains that do have a
door. Nothing on this ladder puts a secret or a key byte in a message: `TryDerive`
swallows SDK exceptions on purpose because Algorand/NBitcoin echo the offending
mnemonic word, and a swallowed `bool` cannot be captured into `AZOAResult.Exception`
(which `AZOAResultDebug.Enabled` would serialize through `Detail`). This also removed a
real pre-existing defect: the old door called `new Account(mnemonic)` outside any
`try`, so a malformed mnemonic threw an SDK exception carrying mnemonic text straight
out of the chokepoint.

### `LegacyPlatformChainType` is a pin, not a fallback

`WithPlatformSigningKeyAsync(bool, …)` and `(bool, SigningContext, …)` predate per-chain
derivation and carry no chain. They resolve `SigningContext.ChainType` when the caller
stamped one and otherwise pin to `Algorand` — byte-identical to historical behaviour,
which is the point: their only production callers were Algorand paths. A caller that
*names* a chain never reaches the pin; it gets that chain's key or a fail-closed error.
New callers use the chain-explicit overload
`WithPlatformSigningKeyAsync(chainType, isPlatformContext, ctx, sign)` or stamp
`SigningContext.ChainType` / `SigningContext.PlatformFor(chain)`.

Per-USER resolves are untouched and never consult any of this: their key comes from the
wallet record's ciphertext, which is already chain-correct, and a per-user context that
cannot be resolved still errors rather than reaching for a platform key.

### Readiness

`Observability/RealValueReadinessHealthCheck` now validates the platform key for every
chain **enabled** under `Blockchain:Chains` (any of Devnet/Testnet/Mainnet), skipping
`Simulated` and skipping chains disabled on every network. It proves the key by actually
running `TryDerive` and then zeroing the result, so a malformed secret fails readiness
instead of the first value operation. It reads config rather than instantiating
providers: `BlockchainProviderFactory.GuardAgainstUnregisteredEnabledChains` already
makes enabled-implies-registered a boot invariant.

### `CustodyModeResolver` — the one predicate every custody caller now shares (OI-81)

The "still out of scope" claim below is stale as written: a real, production-usable
custody mode exists now. `Services/Custody/CustodyModeResolver.cs` resolves
`CustodialAccounts:CustodyMode` into one `RecognisedCustodyMode` — `Disabled`,
`DevelopmentOnly` (simulated-chain-only, Development host only), `PlatformManaged`
(config values `Platform`/`Node`: node-held custody under the boot-validated
`AZOA:WalletEncryptionKey`, usable on a live, non-Simulated chain), or `KmsHsm` — and is
the single place `TenantCustodialAccountManager`, `WalletManager`, and
`RealValueReadinessHealthCheck` all ask "may this node hold a signing key right now?"
rather than each re-deriving the predicate. An absent or blank configured value resolves
to `Disabled`, and `PlatformManaged` still fails closed if the wallet encryption key is
missing/short or the chain is `Simulated` — every rung returns a caller-safe
`UnavailableReason`, never a silent no.

The shipped default is still `CustodyMode: "Disabled"` (`appsettings.json`), with
`DevelopmentOnly` only in `appsettings.Development.json` — enabling `Platform`/`Node`
custody in a real environment is a deliberate operator config change, not a code change.

### Still out of scope

`RecognisedCustodyMode.KmsHsm` is recognised but never available:
`CustodyModeResolver` always returns `KmsHsmAdapterMissingReason` for it, because no
external KMS/HSM adapter is registered. `IKeyCustodyService` is still the swap seam for
one — the interface node-held custody already satisfies would be implemented by an
adapter the same way. Absent that adapter, every platform path here fails closed on a
missing/malformed config secret or an unrecognised/unavailable mode rather than degrading
to a weaker key source.
