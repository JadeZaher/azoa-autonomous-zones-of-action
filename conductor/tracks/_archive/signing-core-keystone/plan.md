# Signing Core Keystone — Plan

Source spec: [spec.md](spec.md)
Initiative: ardanova-provider-port (AZOA as custodial blockchain provider).

## Decisions to record before starting

| # | Decision | Resolution |
|---|----------|------------|
| D1 | **Crypto source for keygen + signing**: BouncyCastle (`BCCrypto2`) primitives directly, OR `Algorand2`'s `Account`/`Mnemonic` high-level API. | **Use `Algorand2`'s high-level API where it exists** (`Account`, mnemonic, transaction builders + canonical msgpack) — it is purpose-built, already referenced, and guarantees address/checksum/msgpack correctness. Fall back to `BCCrypto2` Ed25519 only for the raw point-derivation if `Algorand2` doesn't expose it cleanly. Rationale: minimize homebake curve math; `Algorand2` already encodes the canonical rules. |
| D2 | **Signer key input type**: `string` hex vs `byte[]`. | **`byte[]`** in `SigningKeyMaterial`. .NET `string` is immutable and cannot be reliably zeroed; the custody layer must hand the signer a `byte[]` it can wipe after use. This also forces a `byte[]`-returning decrypt path in the custody track (flagged there as a real constraint). |
| D3 | **Where the signer is selected**: extend `BlockchainProviderFactory`, or a parallel `ITransactionSignerFactory`. | **Parallel `ITransactionSignerFactory`** keyed by `ChainType`, mirroring `BlockchainProviderFactory` (`Core/BlockchainProviderFactory.cs`). Keeps the signer seam independent of provider construction so the `db-only-null-provider` (no signer) and future chains compose cleanly. |
| D4 | **Soulbound revoke/clawback** in this track or deferred. | **Mint path in-scope; clawback-revoke deferred** to keep the keystone tight — but the manager/freeze/clawback addresses are set to platform at mint so revoke is a pure follow-up. Deferred clawback goes on the deploy-stub list. |

## Phase 0 — Spike: prove the crypto before wiring  ✅ DONE 2026-06-15

- [x] Confirmed `Algorand2 2.0.0.2024051911` API by live reflection + round-trip:
      - `new Account()` / `new Account(string mnemonic)` / `new Account(byte[] seed)`;
        `.Address` (Algorand.Address), `.ToMnemonic()` (real 25-word), `.KeyPair`
        (`ClearTextPrivateKey`/`ClearTextPublicKey` as `byte[]`).
      - `Address.EncodeAsString()`, static `Address.IsValid(string)` (real checksum).
      - `Transaction.Sign(Account) → SignedTransaction`, `.TxID()`;
        `Algorand.Utils.Encoder.EncodeToMsgPackOrdered(signedTxn) → byte[]` = submittable.
      - Typed asset txns: `AssetCreateTransaction`(.AssetParams),
        `AssetTransferTransaction`, `AssetDestroyTransaction`,
        `AssetClawbackTransaction`, `AssetAcceptTransaction` (opt-in). `AssetParams`
        has Name/UnitName/Total/Decimals/DefaultFrozen/Manager/Reserve/Freeze/Clawback/Url.
- [x] **Live round-trip PASSED**: `new Account()` → mnemonic → `new Account(mnemonic)`
      reproduced the SAME 58-char address; `Address.IsValid` = true. Crypto risk retired.
- [x] **No new dependency and no hand-rolled msgpack/curve math needed** — `Algorand2`
      owns the canonical rules. BouncyCastle `BCCrypto2` is NOT required for the
      Algorand path (Account/KeyPair cover it); keep the alias note only if BC 2.x
      types are referenced directly elsewhere. D1 resolves firmly to the `Algorand2`
      high-level API.
- Full confirmed API surface recorded in memory `ardanova-provider-port`.

## Phase 1 — Real Algorand keygen

- [ ] Replace `Core/WalletKeyService.GenerateAlgorandKeypair` (`:64-86`) to derive
      the real Ed25519 public key, correct Algorand address, and valid 25-word
      mnemonic via the D1 path.
- [ ] Remove the placeholder `DeriveEd25519PublicKey` (`:116-129`); leave the
      secp256k1/Solana placeholders but add an explicit `// DEPLOY-STUB:` comment
      referencing `conductor/DEPLOY-STEPS-TODO.md`.
- [ ] Replace/repair `GenerateMnemonic` (`:478-489`) for Algorand to a valid
      mnemonic (or route Algorand through `Algorand2`'s mnemonic, leaving the
      generic helper for the still-stubbed chains).
- [ ] Tests: round-trip (seed→address == Algorand2-derived; mnemonic re-import ==
      same address); the AES-GCM encrypt/decrypt envelope still round-trips the new
      real key unchanged.

## Phase 2 — Signing abstraction + Algorand signer

- [ ] Define `ITransactionSigner` + `SigningKeyMaterial` (`byte[]` key, per D2) and
      `ITransactionSignerFactory` (per D3).
- [ ] Implement `AlgorandTransactionSigner`: canonical msgpack build for acfg-create,
      acfg-destroy, axfer-transfer, axfer-clawback, axfer-opt-in; Ed25519 sign;
      return submittable signed bytes.
- [ ] Register the factory + Algorand signer in `Program.cs` DI (one line, mirroring
      the provider registration at `Program.cs:413-414`).
- [ ] Tests: a known-vector test asserting our signed envelope byte-matches an
      `Algorand2`-signed reference for identical inputs (the canonical correctness
      proof).

## Phase 3 — Wire AlgorandProvider to transact

- [ ] `TransferAsync` (`:156-164`): real params→build→sign→submit→confirm. Remove
      the hard-error return.
- [ ] `BurnAsync` (`:148-154`): real acfg-destroy (manager-signed). Remove the
      hard-error return.
- [ ] `MintAsync`/`CreateASAAsync`: real acfg-create; extract real asset id from
      confirmation; persist tx hash + asset id on the `BlockchainOperation` record
      via `IBlockchainOperationStore`.
- [ ] `OptInAsync`: real opt-in.
- [ ] Add the **soulbound-ASA mint** path (total=1, decimals=0, defaultFrozen=true,
      platform manager/freeze/clawback), fully parameterized — caller supplies
      name/url/metadata; no `ardanova.com`, no `"ArdaNova"` defaults.
- [ ] Honor `RetrySafety.Broadcast` on all signed/submitted calls.
- [ ] Interim custody hookup: obtain `SigningKeyMaterial` via a minimal resolver
      (decrypt the platform/owner key through `WalletKeyService`) **good enough for
      tests** — note inline that the real resolver + IDOR guard + zeroing is the
      `custody-key-management` track's deliverable.
- [ ] Tests: each transact path with mocked Algod/Indexer HTTP (params, submit,
      pending→confirmed); asset-id extraction; broadcast-no-retry.

## Phase 4 — Brand scrub, deploy-stubs, docs

- [ ] Grep AZOA source for `ardanova`/`ArdaNova`/`ardanova.com` — zero hits.
- [ ] Create/append `conductor/DEPLOY-STEPS-TODO.md` with: Solana+Ethereum real
      keygen, KMS/HSM custody handoff (replace `SHA-256(config-secret)` derivation),
      soulbound clawback-revoke path, mainnet enablement gate, platform-mnemonic
      provisioning + funding for fees.
- [ ] Update `PROVIDERS.md` Algorand section: signing is now real server-side;
      remove the "client-side signing required" caveats that this track resolves.

## Phase 5 — Verification gates

- [ ] `dotnet build` — 0 errors, **0 new warnings** (nullable enabled) — `workflow.md:18`.
- [ ] `dotnet test` — green; keygen round-trip, msgpack/sign vector, and provider
      transact tests all pass.
- [ ] Swagger UI launches and lists the wallet/NFT endpoints unchanged — `workflow.md:20`.
- [ ] No new NuGet reference in `AZOA.WebAPI.csproj`.
- [ ] Independent review pass (separate lane / `code-reviewer`) — NOT self-approved
      in the authoring context, per the keep-author-and-review-separate rule.

## Phase 6 — Close out

- [ ] Move `conductor/tracks.md` row for `signing-core-keystone` from `[ ]` to `[x]`.
- [ ] Record as-built notes (final `Algorand2` API surface used; any `BCCrypto2`
      alias usings) for the sibling tracks that build on this seam.

## Commit strategy

Per `conductor/workflow.md`: `[signing-core-keystone] <imperative verb> <subject>`.
One commit per phase boundary minimum; keygen and signer land as separate commits
from the provider wiring so a bisect can isolate a crypto regression from a
transact-flow regression.

## Known follow-ups (filed to DEPLOY-STEPS-TODO.md, not this track)

- Solana + Ethereum real keypair generation (still HMAC placeholders).
- KMS/HSM-backed custody (the `custody-key-management` track owns the abstraction;
  the production key store is a deploy-stub).
- Mainnet enablement gate — keep the existing hard mainnet guards until custody is
  production-grade.
- Soulbound clawback-revoke primitive (deferred per D4).
- Platform fee-funding: a custodial signer needs ALGO for fees; provisioning +
  monitoring the platform account balance is an ops deploy-step.
