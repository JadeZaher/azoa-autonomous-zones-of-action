# Value-Path Wiring — Plan

Source spec: [spec.md](spec.md)
Initiative: **workflow-engine** (OASIS as a durable, consumer-driven workflow
engine for ArdaNova via SDK) — Track 1.
Gap analysis: [`conductor/REVIEW-economic-substrate-2026-06-16.md`](../../REVIEW-economic-substrate-2026-06-16.md)
Part A (C1, C2, H1–H4, M1).

Remediation order (from the review's recommendation, `REVIEW…:103-108`): **C2
first** (it makes allocations real and unblocks H1/H2/H4), **then C1** (custody
wiring + the larger avatar-threading change), **then H3** (one-line choke-point
gate), **then M1**. H4 rides along with C2's amount wiring; H1 rides along with
C2's op-row write.

## Decisions to record before starting

| # | Decision required | Recommendation |
|---|-------------------|----------------|
| D1 — **How `avatarId` + owning `walletId` reach the provider** (C1). The provider call surface `IBlockchainProvider.TransferAsync/MintAsync/BurnAsync` (`Interfaces/IBlockchainProvider.cs:23-41`) has no avatar param. Options: **(a)** add `Guid avatarId, Guid walletId` params to each method; **(b)** add a single `SigningContext` value object (`{ AvatarId, WalletId, IsPlatform }`) param; **(c)** keep the signature and resolve `walletAddress → wallet → avatar` *inside* the provider. | **(b) A `SigningContext` object.** One additive param per value-moving method keeps the surface small and future-proof (a `IsPlatform` flag selects `WithPlatformSigningKeyAsync` for ASA-admin ops without a magic address). (a) explodes every signature and every call site; (c) hides an IDOR-relevant lookup inside the provider and couples it to a store it shouldn't own. The context is built once in `BlockchainOperationManager.Execute*Async` (`Managers/BlockchainOperationManager.cs:311-357`) from the op row's `AvatarId`/`WalletId`, which are already populated by `NftManager` (`NftManager.cs:62, 80-81`). `[ decision ]` |
| D2 — **Where the broadcast happens for allocation** (C2). Options: **(a)** `AllocationManager` builds the op and calls `IBlockchainOperationManager.ExecuteAsync` directly (`BlockchainOperationManager.cs:28`), bypassing `NftManager`'s upsert-only path; **(b)** make `NftManager.MintAsync/TransferAsync` themselves broadcast (call `ExecuteAsync` internally) so every NFT caller broadcasts. | **(a) `AllocationManager` calls `ExecuteAsync`.** `NftManager` is also the raw `POST /api/nft/mint` surface and some callers legitimately want the "record intent, sign client-side" path (`AwaitingSignature`, settled correctly at `BlockchainOperationManager.cs:265-272`). Forcing every NFT mint to broadcast server-side (b) would change that contract. `AllocationManager` is the *value-bearing* seam — it builds an `IMintOperation`/`ITransferOperation` (so the typed `Execute*Async` dispatch at `:311-357` fires) and calls `ExecuteAsync`, which records the `TxHash`. `NftManager` keeps owning the Holon upsert + KYC gate (D3). `[ decision ]` |
| D3 — **KYC gate placement** (H3). Options: **(a)** gate inside `NftManager.MintAsync` (`Managers/NftManager.cs:53`); **(b)** gate in `NftController.Mint` (`Controllers/NftController.cs:42-52`). | **(a) Gate in the manager.** Single choke point: both the allocation door and the raw controller door pass through `NftManager.MintAsync`, so the gate is inherited by construction — mirrors the custody/decrypt single-choke-point philosophy. A controller gate (b) would have to be duplicated on every future mint entry point and is easy to forget. `NftManager` gains an `IKycGateService` dependency (already DI-registered, `Managers/KycGateService.cs`). `[ decision ]` |
| D4 — **`AllocationManager.MintAsync` double-gate after H3.** Once the gate moves into `NftManager` (D3), `AllocationManager.cs:82`'s `RequireVerifiedAsync` becomes redundant. Options: **(a)** keep it (defence-in-depth, fail-fast before provisioning); **(b)** remove it. | **(a) Keep it.** It fails closed *before* wallet provisioning (`AllocationManager.cs:89-95`), so a rejected avatar produces zero side effect under a won claim — strictly better than discovering the rejection one layer down after a wallet is generated. Two idempotent gate calls are cheap. `[ decision ]` |
| D5 — **`ulong` vs `string`/`BigInteger` for the provider amount** (H4). | **`ulong`.** Algorand ASA `AssetAmount` is `ulong` native (the cast already exists at `AlgorandProvider.cs:224`); widening the interface to `ulong` removes the truncation with zero new types. `AllocationRequest.Amount` (string) is parsed via `ulong.TryParse` with an explicit overflow/negative rejection. A `BigInteger` surface would over-engineer beyond what any current chain accepts. `[ decision ]` |

## Phase 0 — Confirm seams (no code)

- [ ] Confirm `IKeyCustodyService` is DI-registered and injectable into a
  provider (`Managers/KeyCustodyService.cs`; check `Program.cs` registration).
  Record the exact interface symbol + method signatures
  (`WithSigningKeyAsync(Guid walletId, Guid avatarId, Func<byte[], Task<T>>)`
  `:73`, `WithPlatformSigningKeyAsync(bool, Func<byte[], Task<T>>)` `:118`).
- [ ] Confirm `BlockchainOperation` carries `AvatarId` + `WalletId` (it is set in
  `NftManager.cs:62, 80-81`) so the `SigningContext` (D1) can be built in
  `BlockchainOperationManager.Execute*Async` without a new lookup.
- [ ] Confirm `IMintOperation` / `ITransferOperation` exist and what
  `BlockchainOperationManager` reads off them (`TokenUri`, `Amount`, `AssetType`
  at `:140-142`; `RecipientAddress`, `SourceHolonId` at `:168-169`) so
  `AllocationManager` can build a typed op for `ExecuteAsync` (D2).
- [ ] Confirm the `RetrySafety.Broadcast` contract path
  (`AlgorandProvider.cs:673-685`) is untouched by the signer swap (we change *who
  signs*, not *when we retry*).
- [ ] Re-read the regression-gate tests to know their current expectations before
  editing the value path: `AllocationManagerTests.cs`,
  `ReconciliationServiceTests.cs`, `G2_IdempotencyTocTouTest.cs`,
  `G7_ReconciliationDrillTest.cs`, `AlgorandFaucetIdempotencyTests.cs`.

## Phase 1 — C2: make allocation mints/transfers actually broadcast

- [ ] In `AllocationManager.MintAsync` (`Managers/AllocationManager.cs:166-179`)
  and `TransferAsync` (`:181-195`): instead of calling `_nftManager.MintAsync`
  (upsert-only, `NftManager.cs:53-93`), build a typed `IMintOperation` /
  `ITransferOperation` and call `IBlockchainOperationManager.ExecuteAsync`
  (`BlockchainOperationManager.cs:28-120`) so the provider is called and a
  `TxHash` is recorded (`ApplyChainResult`, `:381-407`). Keep the Holon upsert
  (it stays in `NftManager`, invoked for the metadata object) — only the
  **broadcast** moves to `ExecuteAsync`.
- [ ] In `AllocateAsync` (`AllocationManager.cs:97-127`): `CompleteAsync` the
  allocation idempotency key (`:124`) **only** when the op carries a real
  `TxHash` (confirmed or pending-with-hash per M1). If the op is still
  unconfirmed without a hash, leave the allocation idempotency record
  **InProgress** (do not `Complete`), so a redelivered webhook replays
  "in progress" (`ReplayFromRecord`, `:261-267`) rather than a false success.
- [ ] Carry the already-decided amount into the typed op so it reaches the chain
  call (today `MergeAmount`, `:201-207`, only stuffs it into metadata — that is
  the C2/H4 latency the review notes).

## Phase 2 — H1 + H4: persist the key, widen the amount

- [ ] **H1.** When `AllocationManager` builds the op for `ExecuteAsync` (Phase 1),
  write the `alloc:{apiKeyId}:…` key (`AllocationManager.cs:216-223`) into
  `op.Parameters["IdempotencyKey"]` so
  `ReconciliationService.SettleOperationIdempotencyAsync` (`:542-568`) can release
  an orphaned claim — identical to the bridge precedent
  (`ReconciliationService.cs:519-540`).
- [ ] **H4.** Widen the value-amount surface to `ulong`:
  - `IBlockchainProvider.MintAsync/BurnAsync/TransferAsync`
    (`Interfaces/IBlockchainProvider.cs:23-41`) `int amount → ulong amount`;
  - `AlgorandProvider.MintAsync` (`AlgorandProvider.cs:168`) and `TransferAsync`
    (`:203`) likewise; the `(ulong)amount` cast at `:224` becomes a no-op and is
    removed; `amount <= 0` guards (`:172, 213`) become `amount == 0`;
  - `BlockchainOperationManager.ExecuteBurnAsync` (`:323`,
    `int.Parse(... "Amount" ...)`) and any other `int` amount reads widen to
    `ulong.Parse`/`TryParse`;
  - parse `AllocationRequest.Amount` (string) into `ulong` with explicit
    overflow/negative/non-numeric rejection **before** broadcast (clear error,
    no claim leak — fail the idempotency key like the other validation failures
    at `AllocationManager.cs:105-110`).
- [ ] Update every other `IBlockchainProvider` implementation's signature to the
  `ulong` surface (null/db-only provider, any test doubles) so the interface
  change compiles solution-wide with zero warnings.

## Phase 3 — C1: route signing through custody + thread the context

- [ ] **D1 surface.** Add a `SigningContext` value object
  (`{ Guid AvatarId, Guid WalletId, bool IsPlatform }`) and thread it into the
  value-moving `IBlockchainProvider` methods (`IBlockchainProvider.cs:23-41`).
  Build it once in `BlockchainOperationManager.Execute*Async`
  (`Managers/BlockchainOperationManager.cs:311-357`) from `operation.AvatarId` /
  `operation.WalletId` (set at `NftManager.cs:62, 80-81`); set
  `IsPlatform = true` for ASA-admin ops (create, destroy/burn, clawback, opt-in
  of the platform account).
- [ ] **Inject `IKeyCustodyService` into `AlgorandProvider`** and replace
  `ResolveInterimKeyMaterial(signerAddress)` (`AlgorandProvider.cs:654, 785-800`)
  with custody-routed signing in `BuildSignSubmitCoreAsync`
  (`:610-695`, sign step at `:649-666`):
  - user-wallet op → `WithSigningKeyAsync(ctx.WalletId, ctx.AvatarId, sign)`
    (`KeyCustodyService.cs:73-115`) — its IDOR guard (`:91-96`) and
    platform-wallet-type guard (`:99-104`) now run on the real value path;
  - `ctx.IsPlatform` op → `WithPlatformSigningKeyAsync(true, sign)`
    (`KeyCustodyService.cs:118+`).
  Delete the unconditional `OASIS:Algorand:PlatformMnemonic` load for user ops
  (`:792-799`).
- [ ] **Interim safety.** Any value-moving path that does **not** yet carry a
  resolvable `SigningContext` (e.g. a non-Algorand provider, or a user op with no
  `WalletId`) returns a clear error — **never** falls back to platform signing.
  This replaces the silent mis-sign the review flagged.
- [ ] Keep `RetrySafety.Broadcast` on the submit (`AlgorandProvider.cs:673-685`)
  — only the **signer resolution** changes, not the broadcast/no-retry contract.

## Phase 4 — H3: KYC gate at the single choke point

- [ ] Inject `IKycGateService` into `NftManager` and add
  `await _kycGate.RequireVerifiedAsync(avatarId)` at the top of
  `NftManager.MintAsync` (`Managers/NftManager.cs:53`), **before** the Holon
  upsert (`:73`) and op build (`:78`). On a `KYC_FORBIDDEN:`-prefixed error,
  return the error so `NftController.Mint` (`NftController.cs:50`) surfaces it
  (translate to 403 if the controller maps it — confirm the existing
  `IsError → BadRequest` mapping at `NftController.cs:50` and decide 403 vs 400
  consistent with `kyc-module` convention).
- [ ] Keep the redundant pre-provision gate in `AllocationManager.cs:82` (D4) —
  fail-fast before wallet generation.

## Phase 5 — M1: preserve txId on confirm-timeout

- [ ] `WaitForConfirmationAsync` (`AlgorandProvider.cs:713-750`): on reaching
  `maxPolls` (`:716, :747-749`), return a **success** result that carries the
  `txId` and signals "submitted, pending confirmation" (e.g. a `ConfirmedTxn`
  with `ConfirmedRound = 0` + a pending marker, or a distinct result shape the
  caller can read) instead of an error.
- [ ] In `BlockchainOperationManager` (`ApplyChainResult`, `:381-407` and the
  per-op executors `:311-357`): on a pending-confirmation result, set the op
  `Pending` (not `Failed`) **and** record `Parameters["TxHash"] = txId`, so
  `SettleIdempotencyAsync` (`:253-288`) leaves the key recoverable and
  `ReconciliationService` (`:380-389`, the `txHash`-present branch) settles it
  from chain truth.
- [ ] Verify no double-submit: a pending op with a recorded `TxHash` must replay
  through `ReplayFromRecord` (`BlockchainOperationManager.cs:227-238`) as
  "in progress", never re-broadcast.

## Phase 6 — Tests (authored alongside the phases, run in the final sweep)

- [ ] **C1 — per-user signing.** Drive a per-user custodial `TransferAsync` and
  assert the signature came from the **user's** key (custody resolver invoked
  with the user's `walletId`/`avatarId`), not the platform key. Assert a
  non-owning `avatarId` is rejected by the IDOR guard
  (`KeyCustodyService.cs:91-96`) with no signing side effect. (Mock Algod HTTP +
  a custody/signer test double per `signing-core-keystone`'s mock pattern.)
- [ ] **C2 + H1 — crash-replay exactly-once.** Simulate a crash between broadcast
  and complete: op has a `TxHash`, allocation idempotency key still InProgress.
  A duplicate `AllocateAsync` with the same `(apiKeyId, key)` does **not** re-mint
  (provider mint invoked **exactly once**); reconciliation settles the orphan from
  the persisted `op.Parameters["IdempotencyKey"]` (H1) and the second call replays
  the original `AllocationResult`.
- [ ] **H3 — KYC at the raw door.** `NftManager.MintAsync` (and through it
  `POST /api/nft/mint`) for an unverified avatar is rejected with **no** Holon
  upsert and **no** `BlockchainOperation` created (Moq: `_holonStore.UpsertAsync`
  / `_blockchainOperationStore.UpsertAsync` never invoked).
- [ ] **H4 — no truncation.** An `AllocationRequest.Amount` above `int.MaxValue`
  reaches the provider call as the correct `ulong`; a non-numeric / overflowing
  amount is rejected before broadcast with the idempotency key failed (not
  leaked InProgress).
- [ ] **M1 — timeout preserves txId.** A simulated confirm-timeout records the op
  `Pending` with `Parameters["TxHash"] = txId` (asserted not `Failed`), and a
  follow-up reconciliation/replay does not re-broadcast.
- [ ] **Regression gate (must stay green, no behavioural edit unless reviewed):**
  `AllocationManagerTests.cs`, `ReconciliationServiceTests.cs`,
  `G2_IdempotencyTocTouTest.cs`, `G7_ReconciliationDrillTest.cs`,
  `AlgorandFaucetIdempotencyTests.cs`.

  (xUnit + FluentAssertions + Moq + Builder pattern per
  `tests/.../IntegrationTests/Builders/TestDataBuilders.cs`.)

## Phase 7 — Single end-of-track verification sweep

Per the working-rhythm rule, run the full sweep **once** at the end — not after
each fix.

- [ ] `dotnet build` — 0 errors, **0 warnings** (nullable enabled).
- [ ] `dotnet test` — green: all new C1/C2/H1/H3/H4/M1 tests **and** the five
  regression-gate suites above.
- [ ] Grep the OASIS solution for the tenant brand name — **zero** hits (code,
  config, comments, docs).
- [ ] Grep for any new EF/Postgres/InMemory storage path — **zero**; SurrealDB
  stays the sole engine.
- [ ] Grep `AlgorandProvider.cs` for `PlatformMnemonic` — only the
  `WithPlatformSigningKeyAsync` (platform/ASA-admin) path remains; no
  unconditional user-op load.
- [ ] Independent review pass (separate lane / `code-reviewer`) — not
  self-approved in the authoring context.

## Phase 8 — Close out

- [ ] Update `conductor/DEPLOY-STEPS-TODO.md`:
  - **B4** (`:76-96`) — append: allocation now broadcasts through
    `BlockchainOperationManager.ExecuteAsync` and records a real `TxHash`; the
    `alloc:` key is persisted on the op row so reconciliation releases orphaned
    claims (closes the C2 + H1 + H2 remainder; B4's remaining holes closed).
  - **P5** (`:188-202`) — mark the mint-path KYC enforcement **done**: gate moved
    into `NftManager.MintAsync` (single choke point), so both the allocation door
    and `POST /api/nft/mint` inherit it. (Wallet-generate gating, if still owed,
    stays noted under P5.)
  - **Custody-wiring note** — record that C1 wired `IKeyCustodyService` into
    `AlgorandProvider` (per-user `WithSigningKeyAsync`, platform
    `WithPlatformSigningKeyAsync`); **B3** (KMS/HSM key store) and **B6**
    (mainnet) remain open and untouched.
- [ ] Move the `conductor/tracks.md` row for `value-path-wiring` to `[x]` Shipped.
- [ ] Record as-built notes (final `SigningContext` shape; the typed-op path
  `AllocationManager` builds for `ExecuteAsync`) for the dependent tracks
  (`durable-workflow-engine`, `economic-primitive-nodes`, `workflow-sdk`).

## Commit strategy

House convention: `[value-path-wiring] <imperative verb> <subject>`. One commit
per phase boundary minimum, so a bisect isolates a C2 broadcast regression from
a C1 custody regression:

- `[value-path-wiring] route allocation mints through real broadcast path`
- `[value-path-wiring] persist allocation idempotency key + widen amount to ulong`
- `[value-path-wiring] wire KeyCustodyService into Algorand signing via SigningContext`
- `[value-path-wiring] gate KYC at the NftManager mint choke point`
- `[value-path-wiring] preserve txId on confirm-timeout for reconciliation`
- `[value-path-wiring] add value-path correctness tests`
- `[value-path-wiring] update deploy-steps + tracks; close out`

## Known follow-ups (filed, not scoped here)

- **M2 — multi-step allocation compensation / saga.** A mint failure after wallet
  provisioning strands the wallet; route multi-effect allocations through the
  saga layer. Belongs to **`durable-workflow-engine`** (same suspendable engine
  Part C #3 wants). Not this track.
- **M3 — child Bearer credential → allocation endpoint.** The child JWT lacks the
  `ApiKeyId` claim `AllocationController` requires; decide whether to derive the
  idempotency partition from `OwnerTenantId`. Belongs to `tenant-onboarding`.
- **B3 — KMS/HSM custody key store.** C1 calls the existing config-secret-backed
  `KeyCustodyService`; replacing its key store with KMS stays a DEPLOY-STEPS-TODO
  blocker.
- **B6 — mainnet enablement gate.** Keep the hard mainnet guards until the value
  path is production-grade end-to-end.
- **Solana / Ethereum value paths.** Non-Algorand providers stay under the C1
  interim fail-closed block until separately wired.
- **`DecryptPrivateKeyBytes` byte[] overload** (P1 residual) — orthogonal to this
  track; `KeyCustodyService` already zeroes the derived bytes.
