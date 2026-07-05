# Track: project-asset-fractionalization

## Overview

The end-to-end economic flow ArdaNova consumes through AZOA:

1. **Represent a project as an asset** — a Holon modeling the project + its state.
2. **Notarize** — mint a 1-of-1 deed NFT bound to that Holon (whole ownership).
3. **Mint membership tokens** — issue membership/access tokens for the project
   as needed (fungible, supply-N).
4. **Investor backing** — investors back the project with a **platform token**
   (managed on AZOA).
5. **Bridge** — the platform token bridges to a **project token**.
6. **Pegged project token** — the project token is a fungible token **pegged to
   the asset's value**. The peg math + valuation are **owned by ArdaNova** (tenant);
   AZOA owns the fungible-mint + bridge rails only.

AZOA provides **mechanism**; ArdaNova provides **economic meaning** (consistent
with the economic-primitive-nodes philosophy — [[economic-primitive-nodes-shipped]]).

## What this composes from (build-on vs build-new)

| Step | Existing rail | New work |
|------|---------------|----------|
| Project-as-asset | `HolonCreate` node, `Holon` model | quest-template convention |
| Notarize / deed | `Grant`/`NftMint` + D10 Holon↔asset link (`GrantNodeHandler`) | none |
| Membership tokens | — | **[[fungible-token-node]]** (`FungibleTokenCreate`) |
| Platform-token backing | `AllocationManager.AllocateAsync` (KYC + idempotent, arbitrary `Amount`) | a `PlatformTokenGrant`/`Back` node wrapping allocation |
| Platform→project bridge | `ICrossChainBridgeService.InitiateBridgeAsync`/`RedeemWithVAAAsync` | a `Bridge` quest node + **bridge hardening dep** |
| Pegged project token | `FungibleTokenCreate` (the project token) | peg state is **tenant-side** (`Emit` payload to ArdaNova) |

## Hard dependencies

- **[[fungible-token-node]]** (membership + project token creation) — must ship first.
- **[[bridge-safety-hardening]]** — the platform→project **value-moving** bridge
  step is GATED on this. No real value flows over the bridge until idempotency +
  replay + atomicity are proven. (Decision 2026-06-21.) Until then, the bridge
  node is authorable but the flow is run only on testnet / with the bridge in a
  no-real-value mode.
- Soft: [[star-odk-ecosystem-tree]] (the flow is one dApp in a larger ecosystem).

## Deliverables

1. **New quest nodes** (Tier-2): `Bridge` (wraps `ICrossChainBridgeService`),
   and a `Back`/`PlatformTokenGrant` node wrapping `AllocationManager.AllocateAsync`.
   (`MembershipMint` and `ProjectTokenCreate` are just `FungibleTokenCreate` uses —
   no new node, possibly node-template presets.)
2. **Node-template presets** seeded in the builder palette: "Project Asset",
   "Membership Token", "Investor Backing", "Bridge to Project Token" — each a
   pre-configured `FungibleTokenCreate`/`Back`/`Bridge` node template.
3. **A canonical quest template**: `HolonCreate → Grant(deed,link) →
   FungibleTokenCreate(membership) → Back(platform) → Bridge(platform→project) →
   FungibleTokenCreate(project,pegged) → Emit(peg config to ArdaNova)`.
   Parameterized (`Parameters` JSON Schema) so ArdaNova instantiates it per project.
4. **Peg ownership stays tenant-side**: AZOA holds NO peg/valuation/collateral
   state. The `Emit` node hands the peg config to ArdaNova; redemption/peg
   maintenance is ArdaNova's. (Documented explicitly — no vault/collateral models
   added to AZOA.)

## Acceptance Criteria

- [ ] AC1 — `Bridge` + `Back` Tier-2 nodes (enum + config + handler + gate).
- [ ] AC2 — The 4 node-template presets seed the builder palette.
- [ ] AC3 — The canonical fractionalization quest template instantiates + validates
      (DAG-valid, parameters schema enforced).
- [ ] AC4 — Bridge node refuses to move real value unless bridge-safety-hardening
      is shipped (feature flag / env gate). Testnet path works end-to-end.
- [ ] AC5 — Zero AZOA-side peg/collateral state; peg handed to tenant via `Emit`.
- [ ] AC6 — `dotnet build` + unit tests green, zero new warnings vs baseline.

## Out of scope

- ArdaNova's peg math, valuation oracle, redemption mechanics (tenant-side).
- Secondary market / order book for membership or project tokens.
- Vesting / cliff schedules (tenant-side or a later track).
