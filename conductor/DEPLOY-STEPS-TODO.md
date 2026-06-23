# Deploy-Steps & Stub-Remediation Registry

**Purpose.** This is the canonical, single-source list of every stub, placeholder,
deferred primitive, and operational pre-requisite that must be remediated before
AZOA can act as a **production custodial blockchain provider** that moves real
value. It exists because the ardanova-provider-port initiative deliberately ships
the *architecture* (signing seam, custody policy, KYC, fiat, multi-tenancy) ahead
of the *production-grade primitives* — and that gap must never be invisible.

**Reading this file.** Items are grouped by severity. A 🔴 BLOCKER must be closed
before that capability touches **mainnet / real value**. A 🟠 PRE-PROD must be
closed before a production launch even on testnet-as-prod. A 🟡 HARDENING is a
real gap that is acceptable for an internal/beta cut. Each item names its owning
track and the file:line evidence where the stub lives.

> Greenfield context: per project memory (`greenfield-prelaunch-no-compat`), there
> are no live customers or data yet. Nothing here is a migration; everything is a
> "before we flip it on" gate.

---

## 🔴 BLOCKERS — must close before mainnet / real value flow

### B1. Real Algorand keypair generation — ✅ DONE (signing-core-keystone Phase 1)
- **Was:** `Core/WalletKeyService.cs` `DeriveEd25519PublicKey` = HMAC-SHA512 (not
  Ed25519) and `GenerateMnemonic` = word-index. Algorand addresses were unspendable.
- **Now:** `GenerateAlgorandKeypair` derives a real Ed25519 keypair via the
  already-referenced `Algorand2` package (`new Account()`): real checksummed address
  (`Address.EncodeAsString()`), real restorable 25-word mnemonic (`ToMnemonic()`),
  private key persisted as Algorand2 `ClearTextPrivateKey` bytes. The Solana/Ethereum
  HMAC placeholders + the word-index mnemonic helper remain ONLY for those still-stub
  chains and are now annotated `// DEPLOY-STUB (H1, ...)` in source.
- **Owner:** `signing-core-keystone` (Phase 1).
- **Done when:** ✅ round-trip tests green — seed/private-key → address matches
  `Algorand2`-derived; mnemonic re-imports to the same address
  (`tests/AZOA.WebAPI.Tests/Signing/AlgorandKeygenAndSignerTests.cs`).
- **Remaining before mainnet:** B3 custody + B6 gate (this only made addresses spendable).

### B2. Real server-side signing for Algorand — ✅ DONE (signing-core-keystone Phases 2–3)
- **Was:** `AlgorandProvider` `Burn`/`Transfer` returned hard errors;
  `Mint`/`CreateASA`/`OptIn` only recorded a "sign client-side" request.
- **Now:** a chain-agnostic signing seam (`ITransactionSigner` +
  `SigningKeyMaterial(byte[])` + `ITransactionSignerFactory`, selected by ChainType)
  with a real `AlgorandTransactionSigner` (canonical msgpack via `Algorand2`,
  Ed25519 sign, submittable `EncodeToMsgPackOrdered` bytes). `AlgorandProvider`
  Transfer/Burn/Mint/CreateASA/OptIn + a soulbound-ASA mint path now run a real
  params→build→sign→submit→confirm flow, honoring `RetrySafety.Broadcast`
  (no post-broadcast retry). Real tx id / asset id is returned for the manager to
  persist on the `BlockchainOperation` record.
- **Owner:** `signing-core-keystone` (Phases 2–3).
- **Done when:** ✅ known-vector sign test byte-matches `Algorand2`; provider
  transact tests (in-process Algod stub) green for create/mint/soulbound/transfer +
  broadcast-no-retry (`tests/AZOA.WebAPI.Tests/Signing/`). Live testnet smoke +
  platform fee-funding (P3) still owed before real value flow.
- **Interim custody caveat:** the provider's signing key is resolved by an INTERIM
  resolver that decrypts `AZOA:Algorand:PlatformMnemonic` from config (no per-user
  ownership check). The real ownership-checked, KMS-backed, byte[]-zeroing resolver
  is B3/P1 (`custody-key-management`).

### B3. KMS/HSM-backed custody (replace config-secret key derivation) — STILL OPEN (mainnet gate)
- **Stub:** `Core/WalletKeyService.cs:15-20` derives the data-encryption key from
  `SHA-256(config "AZOA:WalletEncryptionKey")`. A config secret in appsettings/env is
  **not** production-grade custody for value-bearing keys.
- **Owner:** `custody-key-management`.
- **Swap seam landed (custody-key-management):** `IKeyCustodyService`
  (`Interfaces/Managers/IKeyCustodyService.cs`) + `KeyCustodyService`
  (`Managers/KeyCustodyService.cs`) are now the single audited decrypt→sign→zero
  choke point (ownership-checked resolve, JIT decrypt, `byte[]` zeroing in `finally`,
  platform pseudo-wallet). A future `KmsKeyCustodyService` implements the SAME
  interface and the signer is unchanged — this B3 unblock is localized to that one
  swap. The remaining gap is purely the KMS/HSM-backed key STORE behind it.
- **Done when:** the data key (or the per-user keys themselves) live in a KMS/HSM;
  no value-bearing private key is recoverable from app config alone. Cross-ref
  `bridge-unsafe-pre-launch` memory + `api-safety-hardening` track risk posture.

### B4. Idempotency / replay / atomicity for fiat-triggered allocation — ✅ DONE (fiat-stripe-bridge + value-path-wiring)

> **value-path-wiring closeout (2026-06-16):** the remaining B4 holes are now
> closed. The allocation primitive **actually broadcasts** through
> `IBlockchainOperationManager.ExecuteAsync` and records a real `TxHash` (C2 —
> previously it upserted a `Pending` op and `Complete`d the key with no on-chain
> effect). The `alloc:{apiKeyId}:…` key is persisted on the op row
> (`Parameters["IdempotencyKey"]`) so `ReconciliationService.SettleOperationIdempotencyAsync`
> releases an orphaned claim after a crash between broadcast and complete (H1).
> The allocation key is `Complete`d **only** when the op carries a real `TxHash`
> (else left InProgress so a replay returns "in progress", not false success).
> Amount widened `int`→`ulong` end-to-end (H4) — the idempotency key keys off the
> true `ulong`, closing a collision where two distinct >int.MaxValue allocations
> clamped to one key (caught by independent review, guarded by
> `AmountIdempotencyKeyTests`). Confirm-timeout now records `PendingConfirmation`
> + `TxHash` (M1) so reconciliation settles from chain truth, never re-broadcasts.


- **Was:** fiat settles on the tenant and triggers an AZOA wallet/asset
  allocation. Without idempotency a webhook replay double-allocates. The bridge
  already shipped this mistake once (`bridge-unsafe-pre-launch`).
- **Now:** `IAllocationManager.AllocateAsync` (`Managers/AllocationManager.cs`)
  fronts `POST /api/allocation/{avatarId}` (`Controllers/AllocationController.cs`,
  `[EnableRateLimiting("financial")]`). It dedupes through the existing
  `IIdempotencyStore` exactly-once ledger, partitioned by the caller's
  `ApiKeyId` claim + the `Idempotency-Key` header. A duplicate key returns the
  cached original `AllocationResult` (`Replayed = true`) and performs **no**
  second mint/transfer; absent header ⇒ deterministic content key over
  (avatarId, kind, chainType, amount, asset) — never a random per-request key.
  KYC is fail-closed (`IKycGateService.RequireVerifiedAsync` before any value
  side effect, per D3). Proven by `AllocationManagerTests` (duplicate-key
  exactly-once mint, KYC fail-closed never-mints, provision-if-absent, IDOR).
- **Deploy-stub (NEVER committed):** the tenant authenticates with its AZOA API
  key. Provision it at deploy time as `AZOA_TENANT_API_KEY` (the SHA-256-hashed
  ApiKey record carries the `nft:mint` / `wallet:manage` scope that authorises
  allocation). AZOA holds **no** Stripe secret — no `Stripe:SecretKey`, no
  `Stripe:WebhookSecret`, no webhook handler. See
  `conductor/tracks/fiat-stripe-bridge/docs/INTEGRATION-CONTRACT.md`.

### B5. Cross-tenant isolation enforcement — ✅ DONE (tenant-onboarding)
- **Was:** no tenant principal concept; no edge isolating one app's avatars from another's.
- **Now:** enforced via the `OwnerTenantId` self-FK guard. Every per-child op
  (`IssueChildCredentialAsync`) loads the child and asserts
  `child.OwnerTenantId == authenticatedTenantId`; a mismatch OR `null` ownership
  returns `TenantAuthorizationError.NotFound` → **404, never 403** (a prober
  cannot distinguish "no such avatar" from "another tenant's avatar"). The tenant
  id is claim-sourced only (the provision/credential request models have no tenant
  id field by construction). Store lookups (`ListByOwnerTenantAsync`,
  `GetByTenantAndExternalUserAsync`) are owner-scoped SELECTs. Scope delegation is
  intersection-only (no escalation; `tenant:provision` is never delegated down).
- **Owner:** `tenant-onboarding`.
- **Done when:** ✅ cross-tenant rejection (404, "not found, not forbidden") +
  scope-ceiling proven by 15 green unit tests
  (`tests/AZOA.WebAPI.Tests/Managers/TenantManagerTests.cs` —
  `IssueChildCredential_OtherTenantsChild_ReturnsNotFound_Not403`,
  `_UnownedAvatar_ReturnsNotFound`, `_CannotExceedTenantScopes`). Store-layer
  isolation tests authored in `SurrealAvatarStoreTests` (round-trip + owner-scoped
  list/resolve) — currently Skipped on the 3.x-strict harness (open follow-up
  `integration-test-namespace-isolation`), green once that lands.

### B6. Mainnet enablement gate
- **Risk:** flipping to mainnet before B1–B5 close moves real value over unspendable-
  /unsigned-/unprotected paths.
- **Owner:** ops + `signing-core-keystone` (keep existing hard mainnet guards, e.g.
  the faucet `Mainnet` block, until B1–B5 are all `[x]`).
- **Done when:** a single documented checklist gates the mainnet config flip on
  B1–B5 + a security review sign-off.

---

## 🟠 PRE-PROD — close before any production launch

### P1. Private-key zeroing (string-immutability constraint) — ✅ DONE at the custody boundary; byte[] overload follow-up filed
- **Constraint:** `WalletKeyService.DecryptPrivateKey` returns a hex `string`
  (`Core/WalletKeyService.cs:45-48`); .NET strings can't be reliably zeroed. The
  decrypt→sign→zero contract needs a `byte[]` decrypt overload at the custody
  boundary (this is why `SigningKeyMaterial` uses `byte[]`, signing-core D2).
- **Now:** `KeyCustodyService` (`Managers/KeyCustodyService.cs`) decrypts JIT, then
  `Convert.FromHexString(...)` into a `byte[]` that is wiped via
  `CryptographicOperations.ZeroMemory` in a `finally` — even when the `sign`
  delegate throws (unit-tested: `KeyCustodyServiceTests` zero-on-throw + zero-on-
  happy-path). The decrypted bytes never escape the resolver; only the signer's
  result is returned.
- **CAVEAT (residual):** the intermediate hex `string` returned by
  `DecryptPrivateKey` is immutable and CANNOT be zeroed; it lives until GC. The
  byte[] derived from it IS zeroed. Fully closing this needs a first-class
  `DecryptPrivateKeyBytes` (byte[]-returning) overload on `WalletKeyService` so the
  cleartext never materializes as a string at all.
- **FOLLOW-UP (filed, owned by the WalletKeyService owner / Lane A):** add
  `WalletKeyService.DecryptPrivateKeyBytes` returning zeroable `byte[]`;
  `KeyCustodyService` switches to it and drops the hex-string intermediate.
  `custody-key-management` did NOT edit `WalletKeyService` (cross-lane ownership).
- **Owner:** `custody-key-management` (boundary zeroing, done) + WalletKeyService
  owner (byte[] overload, follow-up).

### P2. Key rotation / re-encryption — ✅ DONE (design + stub + unit test); live orchestration is the follow-up
- **Gap:** no path to re-wrap stored keys under a new `AZOA:WalletEncryptionKey`.
- **Now:** `IKeyCustodyService.RewrapAsync(wallet, oldKeyService, newKeyService)`
  (`Managers/KeyCustodyService.cs`) is the per-wallet re-wrap primitive — decrypt
  `EncryptedPrivateKey` (+ `EncryptedSeedPhrase`) under the OLD data-key, re-encrypt
  under the NEW one, zero the transient buffer. Unit-tested: a value encrypted under
  key A is recoverable after re-wrap under key B (`KeyCustodyServiceTests`).
- **FOLLOW-UP (filed):** the live rotation orchestration — dual-key read window
  (accept both old + new during cutover), batch re-wrap of every wallet, and
  rollback/abort on partial failure — plus an admin endpoint. NOT shipped here;
  the stub is the in-process primitive only.
- **Owner:** `custody-key-management` (primitive, done) + ops/future track (live
  orchestration).

### P3. Platform account fee-funding & monitoring
- **Gap:** a custodial signer needs ALGO to pay fees. Provisioning the platform
  account, funding it, and alerting on low balance are ops deploy-steps.
- **Owner:** ops (surfaced by `signing-core-keystone`).

### P4. KYC provider secrets + real provider
- **Stub:** the external KYC provider adapter (`Providers/Kyc/VeriffKycProviderService.cs`)
  is a deploy-stub — every method throws `NotImplementedException` with a generic
  message and it is registered ONLY when `Kyc:Provider == "veriff"`. The default
  `ManualKycProviderService` is admin-review only and needs no secrets.
- **Deploy-stub flag (kyc-module shipped):** to enable the external provider, set
  `Kyc:Provider=veriff` and supply `Kyc:VeriffApiKey` + `Kyc:VeriffBaseUrl` (and,
  for a real integration, the webhook signing secret) from the deploy secret store.
  These are EMPTY placeholders in `appsettings.json` (`Kyc` section) and must never
  be committed. The manual provider needs none of these. Real automated KYC
  (session creation, webhook signature verification, status polling) is still owed
  to promote the stub to a real adapter.
- **Owner:** `kyc-module` (seam + stub shipped) + ops/future provider track (secrets
  + real integration).

### P5. KYC gating actually wired on wallet-generate + mint — ✅ MINT DONE (value-path-wiring); wallet-generate still owed
- **Seam shipped (kyc-module):** the reusable gate `IKycGateService`
  (`Interfaces/Managers/IKycGateService.cs`) + `KycGateService`
  (`Managers/KycGateService.cs`) + `KycAuthorizationError` are landed and registered
  in DI. `RequireVerifiedAsync(Guid avatarId)` returns a success result when the
  avatar has an APPROVED submission, else an error whose Message starts with
  `KycAuthorizationError.Forbidden` (`KYC_FORBIDDEN: `).
- **Mint path WIRED (value-path-wiring, 2026-06-16, H3):** the gate moved INTO
  `NftManager.MintAsync` (the single choke point) so BOTH the allocation door
  (`AllocationManager`) AND the raw `POST /api/nft/mint` door inherit it — an
  unverified avatar is rejected with no Holon upsert and no `BlockchainOperation`
  created (proven by `NftManagerTests`). `NftController.Mint` translates the
  `KYC_FORBIDDEN:` prefix to **403**.
- **Still owed:** `WalletManager.GenerateWalletAsync` gating (wallet-provision
  pre-KYC is currently allowed per fiat D3 — confirm whether a zero-balance
  wallet may exist pre-KYC, or gate it too). Owner: wallet owners.

### P6. Tenant onboarding runbook executed for the first tenant — ✅ runbook authored; execution owed
- **Mechanism + runbook:** ✅ done. The provisioning surface (`api/tenant`) ships and
  the step-by-step runbook is authored at
  `conductor/tracks/tenant-onboarding/ONBOARDING.md` (register tenant avatar → mint
  `tenant:provision` key → provision children → issue child credential → resolve by
  external id), verified against the shipped routes.
- **Execution still owed:** the first real tenant (ArdaNova) must actually be
  registered, issued a tenant-scoped API key, and its user→Avatar mapping populated
  — an ops/deploy-time step, not code.
- **Owner:** `tenant-onboarding` (runbook) + ops (execution).

---

## 🟡 HARDENING — real gaps, acceptable for internal/beta

### H1. Solana + Ethereum real keygen & signing
- **Stub:** `Core/WalletKeyService.cs:133-142` (secp256k1 HMAC placeholder) and the
  Solana Ed25519 path; no real Solana/Ethereum signer yet. Algorand-first by design.
- **Owner:** future `signing-core-*` chain tracks.

### H2. Soulbound clawback-revoke primitive — deferred as planned (signing-core D4)
- **Status:** the soulbound-MINT path shipped (`CreateSoulboundAsaAsync`: total=1,
  decimals=0, defaultFrozen=true, platform = manager/freeze/clawback). The
  revoke-by-clawback+destroy primitive is intentionally NOT in this track (D4) — but
  because the platform already holds the clawback role at mint, revoke is a pure
  follow-up (build an `AssetClawbackTransaction` + `AssetDestroyTransaction` through
  the same signer seam).
- **Owner:** follow-up to `signing-core-keystone`.

### H3. Simulated-data distinguishability audit — ✅ DONE (db-only-null-provider)
- **Guardrail (shipped):** `SimulatedBlockchainProvider` stamps the reserved
  `sim:` marker on EVERY synthetic identifier it emits — addresses
  (`sim:<chain>:<digest>`) and tx hashes (`sim:tx:<digest>`), centralized as
  `SimulatedBlockchainProvider.SimPrefix` / `SimTxPrefix`. Because a real
  Algorand address is 58-char base32 and a real Solana address is base58 —
  neither alphabet contains `:` — a `sim:` value can NEVER collide with a settled
  on-chain identifier. A unit test (`SimulatedBlockchainProviderTests`) asserts
  the marker is present and the format cannot equal a 58-char base32 / base58
  address.
- **Audit result (no mis-read path):**
  - `GetTransactionStatusAsync` only reports `Completed`/`confirmed=true` for a
    hash carrying the `sim:tx:` prefix; a real hash is rejected, so a simulated
    confirmation can never be returned for real-chain input and vice-versa.
  - `ValidateAddressAsync` accepts ONLY `sim:`-prefixed addresses and rejects
    real-looking ones (no cross-contamination): a real address is never silently
    treated as a simulated owner.
  - Simulated balances live in the provider's own in-memory ledger; they are
    NEVER written to the real `wallet` aggregate (which stays balance-free).
  - Selection is a single global flag (`Blockchain:Mode`, default `Live`); the
    factory short-circuits to the simulated provider only when `Mode=Simulated`.
    When a tenant toggles Simulated→Live, the marker on already-persisted rows
    keeps simulated history partitionable from real settlement.
- **Owner:** `db-only-null-provider`.

### H4. Algorand address checksum validation — ✅ DONE (signing-core-keystone)
- **Was:** `ValidateAddressAsync`/`ValidateAddressFormat` were length+charset only.
- **Now:** `AlgorandProvider.ValidateAddressFormat` uses `Algorand.Address.IsValid`
  (real SHA-512/256 checksum) — cheap now that `Algorand2` is on the crypto path.
- **Owner:** `signing-core-keystone` (closed as a B1/B2 follow-on).

### H5. Brand-leak guard in CI
- **Guardrail:** every ported track has a "no `ArdaNova` strings" acceptance grep.
  Promote it to a CI check so the brand boundary can't regress.
- **Owner:** any track / CI ops.

---

## Status board

| ID | Severity | Item | Owning track | State |
|----|----------|------|--------------|-------|
| B1 | 🔴 | Real Algorand keygen | signing-core-keystone | ✅ done |
| B2 | 🔴 | Real Algorand signing | signing-core-keystone | ✅ done |
| B3 | 🔴 | KMS/HSM custody | custody-key-management | open (swap seam landed; KMS store owed) |
| B4 | 🔴 | Fiat-allocation idempotency | fiat-stripe-bridge | ✅ done (dedupe proven by test; `AZOA_TENANT_API_KEY` deploy-stub) |
| B5 | 🔴 | Cross-tenant isolation | tenant-onboarding | ✅ done |
| B6 | 🔴 | Mainnet enablement gate | ops + signing-core-keystone | open |
| P1 | 🟠 | Key zeroing (byte[]) | custody-key-management | ✅ boundary done; byte[] overload follow-up |
| P2 | 🟠 | Key rotation | custody-key-management | ✅ stub+test done; live orchestration follow-up |
| P3 | 🟠 | Platform fee-funding | ops | open |
| P4 | 🟠 | KYC provider secrets | kyc-module | open |
| P5 | 🟠 | KYC gating wired | kyc-module + value-path-wiring + wallet owners | ✅ mint done (gate in NftManager choke point); wallet-generate owed |
| P6 | 🟠 | First-tenant onboarding | tenant-onboarding + ops | runbook authored; execution owed |
| H1 | 🟡 | Solana/Ethereum keygen+signing | future chain tracks | open |
| H2 | 🟡 | Soulbound clawback-revoke | signing-core follow-up | deferred (D4); mint shipped |
| H3 | 🟡 | Simulated-data distinguishability | db-only-null-provider | ✅ done |
| H4 | 🟡 | Algorand checksum validation | signing-core-keystone | ✅ done |
| H5 | 🟡 | Brand-leak CI guard | CI ops | open |
