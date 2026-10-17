---
type: Track Spec
title: "Track: action-graph-bundle-ecosystem-tree"
tags: [action-graph-bundle-ecosystem-tree, archived]
---

# Track: action-graph-bundle-ecosystem-tree

## Overview

Reframe **Action Graph Bundle as an action *tree*** — a way to model an entire **dApp,
multi-dApp ecosystem, or cross-chain ecosystem**. The mental model (user, 2026-06-21):

- An **action** = a single flow within a dApp.
- A **DappSeries** = one dApp = an ordered chain of actions (already shipped;
  `ActionDefinition.DappSeriesId` back-ref, `InputMappings` data flow between actions).
- An **Action Graph Bundle** = the **ecosystem topology** — a *tree of DappSeries* spanning
  multiple dApps and potentially multiple chains.

Today Action Graph Bundle is the **codegen layer downstream of DappSeries**
([Managers/ActionGraphBundleManager.cs](../../../Managers/ActionGraphBundleManager.cs)): `DappCompositionManager.GenerateAsync`
composes a series and calls `ActionGraphBundleManager.CreateOrUpdateAsync` + `GenerateAsync`,
storing the ActionGraphBundle id back on the series (`DappSeries.ActionGraphBundleId`). ActionGraphBundle itself
has **no action/series awareness** — it only receives bound holons + a target chain.

This track makes Action Graph Bundle **wrap DappSeries** (decision 2026-06-21): the ecosystem
layer references many DappSeries (each a dApp), forming the tree. DappSeries stays
the per-dApp composition unit — **no reinvention**.

## Design

### Model change (`Models/ActionGraphBundle.cs` + SurrealDb POCO)
Add an ecosystem topology to ActionGraphBundle:
```csharp
/// Tree of dApps composing the ecosystem. Each node references a DappSeries
/// (one dApp = one action chain) + optional parent for the tree structure +
/// optional target chain override (cross-chain ecosystems).
public List<EcosystemNode> Ecosystem { get; set; } = new();

public sealed class EcosystemNode
{
    public Guid Id { get; set; }
    public Guid DappSeriesId { get; set; }   // the dApp (action chain)
    public Guid? ParentNodeId { get; set; }  // tree edge; null = root dApp
    public string? TargetChain { get; set; } // per-dApp chain (cross-chain ecosystems)
    public string? Label { get; set; }
}
```
Keep `BoundHolonIds`/`TargetChain`/`GeneratedCode` for back-compat; the ecosystem
tree is additive.

### Manager (`ActionGraphBundleManager`)
- `AddDappSeriesAsync(actionGraphBundleId, dappSeriesId, parentNodeId?, targetChain?)` — graft
  a dApp onto the tree (validates the series exists + ownership).
- `GetEcosystemAsync(actionGraphBundleId)` — return the resolved tree (series → actions → nodes)
  for rendering.
- `GenerateAsync` extended to walk the tree and emit a multi-dApp manifest
  (per-dApp generated code keyed by ecosystem node), rather than one flat blob.

### Frontend
- Action Graph Bundle page renders the ecosystem as a **tree** (reuse the React Flow canvas
  from [[action-visual-builder]] — a DappSeries is a collapsible super-node; expand
  to see its action chain; cross-chain edges styled distinctly).
- "Add dApp" grafts a DappSeries onto a selected parent node.

## Relationship to other tracks

- **Builds on** the shipped dapp-composition (DappSeries) + [[action-visual-builder]]
  (reuse the canvas for tree rendering).
- **Contains** [[project-asset-fractionalization]] flows: a fractionalization action
  is one dApp (DappSeries) in the ecosystem tree; ArdaNova configures the whole
  ecosystem through Action Graph Bundle and consumes it.
- DappSeries is the per-dApp unit; Action Graph Bundle is the cross-dApp/cross-chain topology.

## Acceptance Criteria

- [ ] AC1 — ActionGraphBundle carries an `Ecosystem` tree of `EcosystemNode` (DappSeries refs
      + parent edges + per-node chain). SCHEMAFULL POCO regen.
- [ ] AC2 — `AddDappSeriesAsync` / `GetEcosystemAsync`; ownership + existence validated.
- [ ] AC3 — `GenerateAsync` walks the tree → multi-dApp manifest (per-node codegen).
- [ ] AC4 — Frontend renders the ecosystem tree (React Flow, DappSeries as super-nodes,
      cross-chain edges distinct); "Add dApp" grafts onto a parent.
- [ ] AC5 — Cross-chain ecosystem: nodes on different `TargetChain` render + generate.
- [ ] AC6 — Back-compat: existing flat ActionGraphBundle (no ecosystem) still generates/deploys.
- [ ] AC7 — `dotnet build` + schema tests green; frontend `tsc` clean on new files.

## Out of scope

- Real multi-dApp on-chain deployment orchestration (codegen + manifest only;
  deploy stays the existing pseudo-deploy until a deploy-orchestration track).
- Ecosystem-level rollback/compensation across dApps.
