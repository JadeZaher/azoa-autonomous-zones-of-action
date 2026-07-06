# Hand-off — ardanova-provider-port initiative

> **Purpose.** Pick up this initiative in a fresh session with zero context loss.
> Planning is COMPLETE; no implementation code has been written yet. This doc is
> the single source of truth for what was decided, what was verified, and the exact
> order to build in. Read it top to bottom, then follow "How to start" at the end.
>
> Authored 2026-06-15. Repo: `c:\Users\atooz\Programming\Projects\azoa`
> (branch `api-safety-hardening`).

---

## 1. What this initiative is

Make **AZOA** the custodial blockchain provider — an "avatar wallet manager" — for
**ArdaNova** (a gamified-ICO / worker-co-op platform) and future apps. AZOA assigns
and manages each user's Algorand wallet, abstracting the chain away entirely (brand
requirement: the user never sees a key, fee, or signing prompt).

- **Algorand first**, generic/cross-chain where feasible.
- **Economic/token domain stays in ArdaNova** (dual-gate one-token→many-token model,
  asset-creation/minting orchestration, treasury, allocation). AZOA exposes
  blockchain **primitives** + wallet provisioning only.
- **Fold in** ArdaNova's KYC (provider-agnostic) and Stripe/fiat (ArdaNova gift).
- **No brand leak**: nothing ArdaNova-branded in AZOA code.
- Follow existing AZOA patterns/standards (Controllers→Managers→Providers,
  `AZOAResult<T>`, SurrealDB-only POCOs, zero-warning nullable build).

ArdaNova repo (read-only reference for porting):
`C:\Users\atooz\Documents\Escherbridge\ardanova\ardanova-backend-api-mcp\api-server`

---

## 2. THE finding that reshaped the plan (do not lose this)

The original premise was "port ArdaNova's working signing code." **It is not working.**
ArdaNova's `AlgorandService.SignTransaction` (`...\Infrastructure\Algorand\AlgorandService.cs:777-797`)
just wraps txn bytes in a `"TX"` prefix and its build methods emit **JSON, not msgpack**
(`:656-770`). **Both repos stub signing today.**

What IS portable from ArdaNova = the *orchestration structure* (params→build→sign→
submit→confirm-poll→extract-asset-id) and the KYC/Stripe/escrow code — never its crypto.

**AZOA is better positioned**: it already references `Algorand2 2.0.0.2024051911`
(`AZOA.WebAPI.csproj:35`) + `BouncyCastle.Cryptography 2.6.2` aliased `BCCrypto2`
(`:34`). The real signing keystone needs **no new dependency**.

---

## 3. Phase-0 spike result — crypto is PROVEN (zero new deps)

Verified by live reflection + round-trip on 2026-06-15:

- `new Account()` → `.Address` (Algorand.Address), `.ToMnemonic()` (real **25-word**).
- Restore via `new Account(string mnemonic)` or `new Account(byte[] seed)` → **same address** (confirmed).
- `Account.KeyPair.ClearTextPrivateKey` / `.ClearTextPublicKey` = `byte[]`.
- `Address.EncodeAsString()`, static `Address.IsValid(string)` (real checksum — closes a known gap).
- Sign: `Transaction.Sign(Account) → SignedTransaction`; `Transaction.TxID()` = hash;
  submittable bytes = `Algorand.Utils.Encoder.EncodeToMsgPackOrdered(signedTxn)`.
  **Do NOT hand-roll msgpack or curve math — Algorand2 owns the canonical rules.**
- Typed asset txns: `AssetCreateTransaction` (`.AssetParams`), `AssetTransferTransaction`
  (`.XferAsset/.AssetReceiver/.AssetAmount`), `AssetDestroyTransaction` (`.AssetIndex`),
  `AssetClawbackTransaction` (`.AssetSender/.AssetReceiver/.XferAsset/.AssetAmount`),
  `AssetAcceptTransaction` (opt-in). All derive `Transaction` with
  `Sender/Fee/FirstValid/LastValid/GenesisHash(Digest)/GenesisId`.
- `AssetParams`: `Name/UnitName/Total(ulong?)/Decimals(ulong)/DefaultFrozen(bool?)/
  Manager/Reserve/Freeze/Clawback(Address)/Url/MetadataHash(byte[])`.
  Soulbound = Total=1, Decimals=0, DefaultFrozen=true, all 4 admin addrs = platform.

(Also captured in memory `ardanova-provider-port`.)

---

## 4. Locked decisions (from the user, 2026-06-15)

1. **Custody default = per-user keys**, AES-GCM via existing `Core/WalletKeyService.cs`
   (decrypt-just-in-time-to-sign). KMS/HSM owed before mainnet (deploy-stub B3).
2. **Generic `ITransactionSigner`** abstraction from day one (Algorand impl first).
3. **DB-only mode = simulated `NullBlockchainProvider`** (persists to SurrealDB,
   deterministic `sim:`-prefixed fake tx hashes + simulated confirmations), via the
   existing chain factory.
4. **Several focused tracks** (not one umbrella).
5. **Economic/token domain stays in ArdaNova**; AZOA = rails only.
6. **Fold in** KYC + Stripe as AZOA modules; **no brand leak**.

Per-track decision tables (in each `plan.md`) still have a few `[ decision ]` markers
to lock before that track executes — notably tenant model **A (Tenant entity) vs B
(scope + `OwnerTenantId` FK, recommended)**, and Null-vs-Simulated provider naming.

---

## 5. The six tracks + BUILD ORDER

All under `conductor/tracks/<name>/` with `spec.md` + `plan.md`. Index row + summary
in `conductor/tracks.md` ("In flight → Initiative: ardanova-provider-port").

**Hard dependency order — the keystone blocks everything:**

```
1. signing-core-keystone   (Tier 0, BLOCKER)  ← build & verify FIRST, solo
        │
        ├── 2. custody-key-management  (Tier 0)   ┐
        ├── 3. db-only-null-provider   (Tier 1)   │ parallelizable AFTER keystone
        └── 4. tenant-onboarding       (Tier 0.5) ┘
                    │
                    ├── 5. kyc-module          (Tier 1)
                    └── 6. fiat-stripe-bridge  (Tier 1; dep: signing + kyc + tenant)
```

| # | Track | One-line scope |
|---|-------|----------------|
| 1 | signing-core-keystone | Real Ed25519 keygen (replace HMAC placeholder `WalletKeyService.cs:116-129`) + real Algorand signing behind generic `ITransactionSigner`; wire `AlgorandProvider` Transfer/Burn/Mint/CreateASA/OptIn + soulbound (replace hard-error stubs `AlgorandProvider.cs:148-164`). |
| 2 | custody-key-management | Per-user decrypt-to-sign resolver (`IKeyCustodyService`) + IDOR guard + key rotation + platform-signer seam. `byte[]` key zeroing constraint. |
| 3 | db-only-null-provider | Simulated provider via chain factory; deterministic `sim:` addresses/hashes + sim balance ledger; `AZOA:BlockchainMode` config flag. |
| 4 | tenant-onboarding | Multi-tenant: tenant Avatar owns fleet of user Avatars (`OwnerTenantId` FK + `tenant:provision` scope on existing API-key infra); `ExternalUserId` mapping; cross-tenant isolation = security crux. |
| 5 | kyc-module | Port ArdaNova provider-agnostic KYC (Manual + Veriff stub) as SurrealDB POCOs keyed to AvatarId; `KycManager`→`AZOAResult<T>`; reusable gate for wallet/mint. |
| 6 | fiat-stripe-bridge | Thin AZOA seam: idempotent, KYC-gated, tenant-callable wallet-provision + asset-allocation primitive ArdaNova calls post-Stripe-settle. Heavy Stripe stays in ArdaNova. |

---

## 6. Production stub registry

`conductor/DEPLOY-STEPS-TODO.md` — 6 BLOCKERS (B1–B6), 6 PRE-PROD (P1–P6), 5 HARDENING
(H1–H5), each with owner + file:line + status board. Update it as tracks close. Mainnet
stays gated (B6) until B1–B5 are all done.

---

## 7. House rules (from CLAUDE.md / workflow.md — honor these)

- **Test policy: run the full build/test/lint sweep ONCE at the very end** of a
  multi-fix pass — not after each change. (Exception: a change to the test harness
  itself may be re-run inline once.)
- Commit convention: `[<track>] <imperative verb> <subject>`.
- Quality gates: `dotnet build` 0 warnings (nullable on); `dotnet test` green; Swagger
  lists expected endpoints; no Karma symbols.
- **Keep authoring and review separate** — do NOT self-approve crypto in the same
  context; use a separate `code-reviewer`/`verifier` lane.
- SurrealDB is the SOLE storage engine; new entities = decorated POCOs in
  `Persistence/SurrealDb/Models/` (pattern: `Holon.cs`). Frontend typecheck: SKIP
  (pre-existing noise) — run SDK `tsc` + `dotnet build` only.
- IDOR pattern: owned-resource lookups scoped by route id + authenticated AvatarId;
  caller-supplied body AvatarId ignored (STARODK precedent).

---

## 8. How to start (fresh session)

1. Open the repo; confirm branch `api-safety-hardening` (or branch off it).
2. Read this file, then `conductor/tracks/signing-core-keystone/spec.md` + `plan.md`.
   (Recall memory `ardanova-provider-port` has the verified Algorand2 API surface.)
3. Lock any remaining `[ decision ]` markers in the keystone plan (Phase 0 already
   resolved D1 → use `Algorand2` high-level API; no BouncyCastle needed for Algorand).
4. Implement signing-core-keystone in plan order (Phase 1 keygen → Phase 2 signer →
   Phase 3 provider wiring → Phase 4 brand-scrub/docs). Write tests alongside but run
   ONE build+test sweep at the END (house rule).
5. Then a SEPARATE review lane verifies the crypto before marking the track shipped
   and moving its `tracks.md` row to `[x]`.
6. Only after the keystone is green: fan out tracks 2–4 in parallel (e.g. `/ultrapilot`
   with file-partitioned lanes), then 5–6.
7. As each track closes, tick its `DEPLOY-STEPS-TODO.md` items.

**Commit the planning artifacts first** (they're currently uncommitted — see the
companion `kickoff-ardanova-provider-port.sh`).
