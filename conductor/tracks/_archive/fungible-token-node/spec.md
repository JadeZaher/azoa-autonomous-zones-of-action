# Track: fungible-token-node

## Overview

**Keystone for asset fractionalization.** Expose the already-implemented Algorand
ASA creation (`IAlgorandASAModule.CreateASAAsync`, full `total`/`decimals`
control — [Providers/Blockchain/Algorand/AlgorandProvider.cs](../../../Providers/Blockchain/Algorand/AlgorandProvider.cs):514)
through a quest node + manager seam, so a quest (and therefore the visual builder,
DappSeries, and STAR-ODK) can **launch a fungible token** — not just a supply-1 NFT.

Today the provider can create a fungible ASA but **nothing above it can reach it**:
no `QuestNodeType`, no manager, no controller. This track adds the thinnest
correct seam: a `FungibleTokenManager` wrapping the provider call + a Tier-2
`FungibleTokenCreate` quest node.

This is the single change that turns asset fractionalization from "impossible
through the AZOA surface" into "composable in a quest." Once shipped, the visual
builder's palette surfaces it automatically (the catalog mirrors the enum).

## Relationship to other tracks

- **Enables** [[project-asset-fractionalization]] — the project token is a
  fungible token created by this node.
- **Consumed by** [[star-odk-ecosystem-tree]] via quest composition.
- **Independent of** the bridge — creating a token moves no value cross-chain, so
  this track has **no bridge dependency** and can ship before bridge hardening.

## Goals

1. A `FungibleTokenCreate` `QuestNodeType` (Tier-2, `RequiresChainCapability=true`).
2. A `FungibleTokenManager` seam wrapping `IAlgorandASAModule.CreateASAAsync`,
   mirroring the KYC-gate + idempotency discipline of `AllocationManager`.
3. The node handler links the created token back to a Holon (opt-in `HolonId`),
   reusing the D10 Holon↔asset pattern from `GrantNodeHandler`.
4. Catalog entry in the frontend builder (`node-catalog.ts`) — `requiresChain`.
5. **No rewrite of AllocationManager's internal mint** — that supply-1 path stays;
   this is a parallel fungible path. (Consolidating the two is a follow-up.)

## Design

### New node config (`Models/Quest/NodeConfigs.cs`)
```csharp
/// <summary>FungibleTokenCreate config: launch a fungible token (ASA) backed/linked
/// to a holon. Supply + decimals are tenant-supplied and authoritative; AZOA
/// derives no economic meaning (peg/valuation is tenant-side, e.g. ArdaNova).</summary>
public class FungibleTokenCreateNodeConfig
{
    public string ChainType { get; set; } = "Algorand";
    public string Name { get; set; } = string.Empty;
    public string UnitName { get; set; } = string.Empty;
    public ulong Total { get; set; }      // total supply (base units)
    public int Decimals { get; set; }     // 0..19
    /// <summary>Optional holon to link to the created token (token_id/chain_id).</summary>
    public Guid? HolonId { get; set; }
}
```

### New manager (`Managers/FungibleTokenManager.cs` + `Interfaces/Managers/IFungibleTokenManager.cs`)
```csharp
Task<AZOAResult<FungibleTokenResult>> CreateAsync(
    Guid avatarId, FungibleTokenCreateRequest request,
    Guid callerAvatarId, string? clientIdempotencyKey, string apiKeyId);
```
- KYC gate (`RequireVerifiedAsync`) — fail-closed, same as AllocationManager.
- Idempotency claim BEFORE the create (deterministic content key over
  `(apiKeyId, avatar, name, unit, total, decimals, chain)`).
- Provision-if-absent wallet (reuse `EnsureWalletAsync` logic or extract shared).
- Route to `IAlgorandASAModule.CreateASAAsync` with the avatar's custodial address
  as manager/reserve/freeze/clawback (mechanism only; roles tenant-configurable
  follow-up).

### New handler (`Services/Quest/Handlers/FungibleTokenCreateNodeHandler.cs`)
- `NodeType => QuestNodeType.FungibleTokenCreate`, `RequiresChainCapability => true`.
- Actor avatar ALWAYS from run context (never the config body) — Grant precedent.
- On success, opt-in Holon↔asset link (copy assetId/chainId onto the holon).
- Auto-registered by the Program.cs assembly scan (no DI edit needed).

### Enum + gate
- Add `FungibleTokenCreate` to `Models/Quest/QuestEnums.cs::QuestNodeType`.
- `ChainCapabilityGate` already refuses Tier-2 nodes pre-execution at both dispatch
  seams when no wallet is bound — the new node inherits this by `RequiresChainCapability`.

## Acceptance Criteria — Shipped 2026-06-21

- [x] AC1 — `FungibleTokenCreate` enum value + `FungibleTokenCreateNodeConfig` DTO.
- [x] AC2 — `IFungibleTokenManager`/`FungibleTokenManager` wrapping CreateASAAsync,
      KYC-gated + idempotent, registered in Program.cs DI (scoped, by AllocationManager).
- [x] AC3 — Handler routes through the manager, actor from run ctx, opt-in Holon link.
- [x] AC4 — Tier-2 `RequiresChainCapability=true`; inherits the ChainCapabilityGate
      refusal at both dispatch seams.
- [x] AC5 — Frontend `node-catalog.ts` entry (`requiresChain: true`, Economic).
- [x] AC6 — 3 `FungibleTokenManager` xUnit tests (happy-path, KYC-block,
      idempotency-replay) green; `dotnet build` 0 errors, zero new warnings vs baseline.
- [x] AC7 — AllocationManager's supply-1 mint path untouched.

### As-built deviations (justified)
- `request.Total` is `ulong` (spec) but provider `CreateASAAsync` takes `int total`;
  reconciled with a pre-broadcast `Total > int.MaxValue` rejection (fails the
  idempotency key, no effect) + `checked((int))` cast — no silent truncation.
- Handler idempotency seed: Tier-2 handlers carry no apiKey in quest context
  (SwapNodeHandler precedent), so `clientIdempotencyKey = "{runId}:{nodeId}"`,
  `apiKeyId = runId` as the sentinel partition.
- Holon link reads `assetId` directly from the bare-string `CreateASAAsync` return
  (`chainId = cfg.ChainType`), not the `IBlockchainOperation` Parameters bag.

## Out of scope / follow-ups

- Configurable ASA roles (manager/reserve/freeze/clawback) — defaults to custodial.
- Solana SPL fungible mint (Algorand-only v1).
- Consolidating AllocationManager mint + this fungible path into one seam.
- Decimals validation beyond 0..19.
