# Value-Path Wiring — Specification

> Track 1 of the **workflow-engine** initiative (AZOA as a durable,
> consumer-driven workflow engine for ArdaNova via SDK). Source gap analysis:
> [`conductor/REVIEW-economic-substrate-2026-06-16.md`](../../REVIEW-economic-substrate-2026-06-16.md)
> Part A. Read that first — it carries the full file:line evidence this spec
> condenses.

## Goal

Make the AZOA **value path correct end-to-end** so that a Swap / Transfer /
Grant primitive actually **broadcasts, signs with the right key, and settles
exactly once**. Today the allocation seam reports success without putting
anything on-chain, and every signed Algorand op is signed with the **platform**
key regardless of whose wallet is moving — so no higher-level economic node
(the sibling `economic-primitive-nodes` track) can be made real until these
substrate defects are closed.

This track fixes the **Critical** and **High** value-path gaps from the review
(C1, C2, H1, H3, H4) plus the **Medium** confirm-timeout defect (M1). It is the
value-flow correctness keystone the same way `signing-core-keystone` was the
signing keystone: it gates real value.

It does **not** build the workflow engine, the generic economic node handlers,
or the SDK — those are sibling tracks that **depend on** this one (see
§Dependents).

## Background

The shipped tracks are structurally sound (idempotency claim-ordering,
broadcast/confirm retry split, custody zeroing, tenant isolation all verified
correct in the review). But two Critical gaps share a root cause: **the
allocation path never routes through the real broadcast + reconciliation
machinery, and the custody resolver is never called.** Five smaller gaps
cluster around the same seam.

### C1 — Custody service is unwired; every signed op uses the platform key

`AlgorandProvider.BuildSignSubmitCoreAsync` resolves its signing key via
`ResolveInterimKeyMaterial(signerAddress)`
(`Providers/Blockchain/Algorand/AlgorandProvider.cs:654`, body at `:785-800`).
That resolver **ignores `signerAddress`** and unconditionally loads
`AZOA:Algorand:PlatformMnemonic`:

```csharp
// AlgorandProvider.cs:785-800
private SigningKeyMaterial? ResolveInterimKeyMaterial(string signerAddress)
{
    if (_keyService is null) return null;
    // The signer address is currently informational — per-address resolution is
    // the custody track's job.
    var mnemonic = _config.GetValue<string>("AZOA:Algorand:PlatformMnemonic");
    ...
    var account = new AlgoAccount(mnemonic.Trim());
    return new SigningKeyMaterial(account.KeyPair.ClearTextPrivateKey);
}
```

Meanwhile `KeyCustodyService.WithSigningKeyAsync(walletId, avatarId, sign)`
(`Managers/KeyCustodyService.cs:73-115`) is the **real** per-user resolver: it
loads the wallet, enforces the IDOR guard *before* any decrypt
(`wallet.AvatarId != avatarId` ⇒ error, `:91-96`), rejects non-platform wallets
(`:99-104`), and hands a zeroable `byte[]` to the signer. Its platform sibling
`WithPlatformSigningKeyAsync(true, sign)` (`:118+`) is the only legitimate door
to the platform key. **Both are called only by their own tests.**

Consequence: a per-user `TransferAsync(from = userWallet)` is signed with the
platform key → sender mismatch → the chain rejects it, or it moves the
platform's asset. The custody track's entire security property is bypassed at
runtime.

The blocker is structural: the provider call surface has **no avatar param**.
`IBlockchainProvider.TransferAsync/MintAsync/BurnAsync`
(`Interfaces/IBlockchainProvider.cs:23-41`) take only `(tokenId, addresses,
amount)` — there is no way to tell the provider *whose* wallet to resolve. So
C1 requires threading `avatarId` (and the owning `walletId`) into the call
surface. That threading is an explicit design decision (see plan.md D1).

**Interim safety:** until the per-user path is wired, a per-user custodial
transfer/mint must be **BLOCKED with a clear error**, never silently
mis-signed with the platform key.

### C2 — Allocation marks idempotency Completed though nothing goes on-chain

`AllocationManager.MintAsync` (`Managers/AllocationManager.cs:166-179`) and
`TransferAsync` (`:181-195`) delegate to `NftManager.MintAsync` /
`NftManager.TransferAsync` (`Managers/NftManager.cs:53-93`, `:95-134`). Those
methods **only upsert a Holon + a `Pending` `BlockchainOperation`** — they never
call `IBlockchainOperationManager.ExecuteAsync`, never reach a provider, never
record a `TxHash`:

```csharp
// NftManager.cs:78-92 — builds a Pending op and returns it. No broadcast.
var operation = new BlockchainOperation
{
    ...
    Status = OperationStatus.Pending,
    Parameters = new Dictionary<string, string> { ["holonId"] = ..., ... }
};
return await _blockchainOperationStore.UpsertAsync(operation, default);
```

Yet `AllocationManager` then writes `CompleteAsync` on the idempotency key
(`AllocationManager.cs:124-125`) as if the value moved. For a fiat bridge
("money already cleared"), this is a **silent zero-mint that reports success**,
and a redelivered webhook dedupes against a record of an effect that never
happened.

The real broadcast machinery already exists and is correct:
`BlockchainOperationManager.ExecuteAsync`
(`Managers/BlockchainOperationManager.cs:28-120`) claims its own idempotency
key, calls the provider via the typed `Execute*Async` dispatch
(`:66-94`, e.g. `ExecuteMintAsync` `:311-318`), and records the `TxHash` from
the chain result (`ApplyChainResult`, `:381-407`). Allocation must drive through
**this** path so the op carries a real `TxHash` and only then `Complete` the
allocation idempotency key.

### H1 — Allocation idempotency key is unrecoverable by reconciliation

A crash between `TryClaimAsync` (`AllocationManager.cs:72`) and
`Complete`/`Fail` (`:124-133`) leaves a **permanently poisoned claim** —
`ReplayFromRecord` returns "already in progress; retry" forever
(`AllocationManager.cs:261-267`). Reconciliation can release such an orphan
**only if the key is persisted on the op row**.
`ReconciliationService.SettleOperationIdempotencyAsync` (`:542-568`) explicitly
refuses to fabricate a key:

```csharp
// ReconciliationService.cs:552-563
if (!op.Parameters.TryGetValue("IdempotencyKey", out var key) || string.IsNullOrWhiteSpace(key))
{
    _logger.LogWarning("... idempotency key is not persisted/resolvable ... NOT fabricating a key ...");
    return;
}
```

The **bridge** path already avoids this by persisting its key on the row
(`ReconciliationService.cs:519-540` reads `tx.IdempotencyKey`). The allocation
path must do the same: write `op.Parameters["IdempotencyKey"]` so the orphan can
be settled from chain truth.

### H2 — Allocation ops are invisible to reconciliation (no TxHash)

**Resolved transitively by C2** — once allocations broadcast and record a
`TxHash`, the existing reconciliation lane handles them
(`ReconciliationService.cs:380-389` currently dead-ends on a blank `TxHash`). No
separate work item; tracked here only so the link is explicit.

### H3 — NFT mint bypasses the KYC gate (the P5 hole, confirmed)

`IKycGateService.RequireVerifiedAsync` has exactly **one** caller —
`AllocationManager.cs:82`. The raw door `NftController.Mint`
(`Controllers/NftController.cs:42-52`) → `NftManager.MintAsync`
(`NftManager.cs:53-93`) has **no** KYC check, so a tenant can mint via
`POST /api/nft/mint` and sidestep the gate the allocation seam enforces. The
fix moves the gate into `NftManager.MintAsync` — the single choke point both
doors pass through — mirroring the custody/decrypt single-choke-point
philosophy (gate-before-side-effect, one place). See plan.md D3.

### H4 — `int amount` truncates large allocations at the provider boundary

`AllocationRequest.Amount` is a `string` (arbitrary precision) but the provider
value surface is `int`:
`AlgorandProvider.MintAsync(... int amount ...)` (`AlgorandProvider.cs:168`),
`TransferAsync(... int amount ...)` (`:203`), with the `(ulong)amount` cast at
`:224`. `int` caps at ~2.1e9 base units — large allocations silently truncate.
Algorand ASA amounts are `ulong` native. The provider value surface widens to
`ulong` (interface `IBlockchainProvider.cs:23-41` + `AlgorandProvider`), and the
string `AllocationRequest.Amount` is parsed into it with **range validation**.
Latent until C2 wires the amount through; fixed as part of the C2 wiring.

### M1 — Confirm-timeout discards the txId → false-Failed + double-submit risk

`AlgorandProvider.WaitForConfirmationAsync` (`AlgorandProvider.cs:713-750`)
returns an error after `maxPolls = 10` rounds (`:716`, `:747-749`). The op is
then recorded `Failed` **without** the `TxHash`, so a slow-but-valid tx becomes
permanently false-Failed and a retry could double-submit. The fix: on timeout
return a "submitted, pending confirmation" result **carrying the `txId`**;
record the op `Pending` with `Parameters["TxHash"] = txId` so reconciliation
settles it from chain truth (the `txHash`-present branch at
`ReconciliationService.cs:380-389` then engages).

### What's already solid (verified — do not re-litigate)

Idempotency claim-before-side-effect ordering; `KeyCustodyService` internals
(IDOR-before-decrypt, zero-on-throw); broadcast-vs-confirm retry separation
(`RetrySafety.Broadcast` honored at `AlgorandProvider.cs:673-685`); the
reconciliation "only act on positive chain signal" stance. This track wires the
existing-and-correct machinery together; it does not rebuild it.

## Scope

### In scope

- [ ] **C1 — Wire `IKeyCustodyService` into `AlgorandProvider` signing.** Replace
  `ResolveInterimKeyMaterial(signerAddress)` (`AlgorandProvider.cs:654, 785-800`)
  so signing routes through the custody resolver:
  - user-wallet ops → `KeyCustodyService.WithSigningKeyAsync(walletId, avatarId,
    sign)` (`KeyCustodyService.cs:73-115`), inheriting its IDOR guard;
  - platform / ASA-admin ops (create, destroy/burn, clawback, opt-in of the
    platform account) → `WithPlatformSigningKeyAsync(true, sign)`
    (`KeyCustodyService.cs:118+`).
- [ ] **C1 — Thread the signing context (`avatarId` + owning `walletId`) into the
  provider call surface.** `IBlockchainProvider.TransferAsync/MintAsync/BurnAsync`
  (`IBlockchainProvider.cs:23-41`) gain the avatar/wallet context (mechanism is
  plan.md D1). `BlockchainOperationManager.Execute*Async` (`:311-357`) populate
  it from the op row.
- [ ] **C1 — Interim safety gate.** Until the per-user path is fully wired, a
  per-user custodial transfer/mint returns a clear, non-silent error rather than
  signing with the platform key. (This becomes a *real* per-user signing path by
  end of track; the gate is the fail-closed fallback for any path not yet wired,
  e.g. another chain.)
- [ ] **C2 — Drive allocation mints/transfers through the real broadcast path.**
  `AllocationManager.MintAsync`/`TransferAsync` (`AllocationManager.cs:166-195`)
  route through `IBlockchainOperationManager.ExecuteAsync`
  (`BlockchainOperationManager.cs:28-120`) — which calls the provider and records
  a `TxHash` — instead of `NftManager`'s upsert-only path
  (`NftManager.cs:53-93`). The allocation idempotency key is `Complete`d
  (`AllocationManager.cs:124`) **only with the real `TxHash`**; the allocation
  stays InProgress (not Completed) until the op confirms.
- [ ] **H1 — Persist the allocation idempotency key on the op row.** Write the
  `alloc:{apiKeyId}:…` key (`AllocationManager.cs:216-223`) into
  `op.Parameters["IdempotencyKey"]` so
  `ReconciliationService.SettleOperationIdempotencyAsync` (`:542-568`) can
  release an orphaned claim on crash, exactly as the bridge path does (`:519-540`).
- [ ] **H3 — Move the KYC gate into `NftManager.MintAsync`.** Add the
  `RequireVerifiedAsync(avatarId)` gate (today only at `AllocationManager.cs:82`)
  at the single choke point `NftManager.MintAsync` (`NftManager.cs:53`) so both
  the allocation door and the raw `POST /api/nft/mint` door
  (`NftController.cs:42-52`) inherit it, failing closed before any
  Holon/op side effect. (Manager-level vs controller-level is plan.md D3 —
  recommend manager.)
- [ ] **H4 — Widen the provider value-amount surface to `ulong`.** Change
  `IBlockchainProvider.MintAsync/BurnAsync/TransferAsync` (`:23-41`) and
  `AlgorandProvider.MintAsync/TransferAsync` (`AlgorandProvider.cs:168, 203`,
  cast at `:224`) from `int amount` to `ulong amount`; parse
  `AllocationRequest.Amount` (string) into `ulong` with range validation,
  rejecting overflow/negative with a clear error before any broadcast.
- [ ] **M1 — Preserve the txId on confirm-timeout.**
  `WaitForConfirmationAsync` (`AlgorandProvider.cs:713-750`) on timeout returns a
  "submitted, pending confirmation" success carrying the `txId`;
  `BlockchainOperationManager` records the op `Pending` with
  `Parameters["TxHash"] = txId` (not `Failed`), so reconciliation settles it from
  chain truth (`ReconciliationService.cs:380-389`).

### Out of scope (sibling / dependent tracks — referenced, not built here)

- **Durable / suspendable workflow engine** — `durable-workflow-engine` track.
  This track makes value movement correct; the engine that *composes* movements
  into multi-step DAGs is separate.
- **Generic economic node handlers** (`GateCheck`, `Allocate`, `Vest`, emit) —
  `economic-primitive-nodes` track. They consume this track's now-real
  mint/transfer; they do not change it.
- **The SDK** (consumer-driven workflow client) — `workflow-sdk` track.
- **M2 — multi-step allocation compensation / saga.** A mint failure after
  wallet provisioning stranding the wallet (review M2) belongs to the saga layer
  in `durable-workflow-engine`. **Handoff note:** this track keeps the existing
  single-scope idempotency behaviour (a failed allocation marks the key `Failed`,
  `AllocationManager.cs:129-134`); splitting provisioning into its own retryable
  scope is the saga track's job. Not scoped here.
- **KMS/HSM-backed custody (B3)** and **mainnet enablement (B6)** — stay on
  `conductor/DEPLOY-STEPS-TODO.md`, untouched. C1 wires the *existing*
  config-secret custody resolver (`KeyCustodyService`); replacing that resolver's
  key store with KMS remains B3.
- **Solana / Ethereum value paths** — Algorand-first, like every prior track.
  Non-Algorand providers fall under the C1 interim block (fail-closed) until
  separately wired.

### Dependents (tracks that need this one first)

`durable-workflow-engine`, `economic-primitive-nodes`, `workflow-sdk` all build
on a value path that actually broadcasts, signs per-user, and settles
exactly-once. None can be made real until this track ships.

## Acceptance criteria

- [ ] **Per-user signing proof.** A test drives a per-user custodial
  `TransferAsync(from = userWallet)` and asserts it is signed with the **user's**
  key (resolved via `WithSigningKeyAsync(walletId, avatarId, …)`), **not** the
  platform key — and that a caller whose `avatarId` does not own the wallet is
  rejected by the IDOR guard (`KeyCustodyService.cs:91-96`) with **no** signing
  side effect.
- [ ] **Crash-replay proof (exactly-once).** A test simulates a crash *between
  broadcast and complete* (the op has a `TxHash`; the allocation idempotency key
  is still InProgress). A duplicate allocation with the same key does **not**
  re-mint; reconciliation settles the orphaned claim from the persisted
  `op.Parameters["IdempotencyKey"]` (H1) and the second call replays the original
  result.
- [ ] **KYC-gate-at-choke-point proof.** `POST /api/nft/mint`
  (`NftController.cs:42-52`) for an avatar whose KYC is not approved is rejected
  with **no** Holon and **no** `BlockchainOperation` side effect (gate in
  `NftManager.MintAsync`).
- [ ] **C2 wiring.** An allocation mint produces a `BlockchainOperation` with a
  real `TxHash` recorded via `BlockchainOperationManager.ExecuteAsync`; the
  allocation idempotency key is `Complete`d **only** with that `TxHash`, and
  stays InProgress until the op confirms (no premature `CompleteAsync` at the
  old `AllocationManager.cs:124`).
- [ ] **C1 interim safety.** Any per-user custodial path not yet wired returns a
  clear error (never a platform-key mis-sign); a grep confirms
  `ResolveInterimKeyMaterial`'s unconditional `PlatformMnemonic` load
  (`AlgorandProvider.cs:785-800`) is gone for user-wallet ops.
- [ ] **H4 widening.** `IBlockchainProvider` + `AlgorandProvider` value-amount
  params are `ulong`; an allocation amount above `int.MaxValue` round-trips to the
  chain call without truncation, and a non-numeric / overflowing
  `AllocationRequest.Amount` is rejected before broadcast.
- [ ] **M1 timeout.** A simulated confirm-timeout
  (`WaitForConfirmationAsync`, `AlgorandProvider.cs:713-750`) records the op
  `Pending` with `Parameters["TxHash"] = txId` (not `Failed` without a hash); a
  test asserts no false-Failed and that reconciliation can pick it up.
- [ ] **Regression gate — the api-safety-hardening exactly-once / replay /
  reconciliation tests stay green.** This track edits the exact value path those
  tests guard, so they are the regression gate:
  `tests/AZOA.WebAPI.Tests/Managers/AllocationManagerTests.cs`,
  `tests/AZOA.WebAPI.Tests/Services/Reconciliation/ReconciliationServiceTests.cs`,
  `tests/AZOA.WebAPI.IntegrationTests/Gates/G2_IdempotencyTocTouTest.cs`,
  `tests/AZOA.WebAPI.IntegrationTests/Gates/G7_ReconciliationDrillTest.cs`,
  `tests/AZOA.WebAPI.Tests/Core/AlgorandFaucetIdempotencyTests.cs` — all remain
  green. Any required edit to these is a deliberate, reviewed change, not a
  silent break.
- [ ] **`dotnet build` green, zero warnings** (nullable enabled).
- [ ] **`dotnet test` green**, including the new C1/C2/H1/H3/H4/M1 tests.
- [ ] **No brand leak** — grep the AZOA solution for the tenant brand name: zero
  hits in code, config, comments, docs.
- [ ] **SurrealDB sole engine** — no EF/Postgres/InMemory storage path
  introduced; persistence stays through the existing stores.
- [ ] **Single end-of-track sweep** — build/test/lint run **once** at the end
  (per the working-rhythm rule), not iteratively per fix.
- [ ] **`conductor/DEPLOY-STEPS-TODO.md` updated:** C1 closes the custody-wiring
  gap; C2 + H1 + H2 close B4's remaining holes; H3 closes P5.
- [ ] **`conductor/tracks.md`** row for `value-path-wiring` moved to `[x]`
  Shipped.

## Out of scope (restated for the boundary)

- No durable engine, no economic node handlers, no SDK (sibling tracks).
- No saga/compensation (M2 → `durable-workflow-engine`).
- No KMS/HSM (B3), no mainnet flip (B6) — DEPLOY-STEPS-TODO only.
- No Solana/Ethereum value path (fail-closed until separately wired).
- No new storage engine; no economic logic in AZOA (amounts stay
  tenant-decided, opaque to AZOA).

## Tier

**Tier 0 — value-flow correctness blocker.** Gates real value exactly the way
`signing-core-keystone` did: until allocations broadcast, sign per-user, and
settle exactly-once, no Swap/Transfer/Grant workflow node can be real and no
fiat may point at the allocation endpoint. Must land before any
`economic-primitive-nodes` / `durable-workflow-engine` value node and before
B6 (mainnet) is considered.

## Dependencies

All inbound dependencies are **shipped**:

- **signing-core-keystone** ✓ — provides `ITransactionSigner` /
  `AlgorandTransactionSigner` + real keygen; this track routes the *right key*
  into it.
- **custody-key-management** ✓ — provides `IKeyCustodyService`
  (`WithSigningKeyAsync` / `WithPlatformSigningKeyAsync`,
  `KeyCustodyService.cs:73-115, 118+`); this track makes the provider actually
  call it (closes the "only callers are its tests" gap).
- **kyc-module** ✓ — provides `IKycGateService.RequireVerifiedAsync`
  (`Managers/KycGateService.cs:25`); this track moves the gate to the single
  choke point.
- **fiat-stripe-bridge** ✓ — provides `IAllocationManager` /
  `AllocationController`; this track makes its mints actually broadcast and
  reconcile.
