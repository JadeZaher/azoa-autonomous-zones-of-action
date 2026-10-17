---
type: Track Spec
title: "Action Core — Specification"
tags: [action-core, archived]
---

# Action Core — Specification

## Goal
Define the core domain models and abstractions for the **Action DAG system** — a control-flow orchestration layer on top of the holonic graph and Action Graph Bundle dapp-generator. Each action is a DAG with entry/terminal nodes, optional dependencies on completed actions, and reusable node templates (meta-nodes). A series of Actions composes into a dApp contract.

## Architecture Overview

### Layering
```
┌─────────────────────────────────────────────────────────────┐
│  dApp (Action Series) → Contract Generation                 │  (dapp-composition track)
├─────────────────────────────────────────────────────────────┤
│  Action API — REST, Manager, Execution                      │  (action-api track)
├─────────────────────────────────────────────────────────────┤
│  Action Core — Models, DAG, Templates                       │  ← THIS TRACK
├─────────────────────────────────────────────────────────────┤
│  Existing AZOA Manager Layer                                │
│  IHolonManager | INftManager | IWalletManager               │
│  IActionGraphBundleManager | ISearchManager | IUserNFTService          │
│  IBlockchainOperationManager                                 │
├─────────────────────────────────────────────────────────────┤
│  Holon Graph (nested links, peer IDs, parent/children)       │  (data flow, not control)
└─────────────────────────────────────────────────────────────┘
```

### Design Principle
The Action DAG is a **control abstraction** — it encodes execution ordering (what runs before what). The underlying holonic graph (parent/children/peers/nested links) handles data flow and dynamic reconfiguration. The DAG does **not** replace the holon graph; it orchestrates it.

> **No `DataHint` edges** — the holonic graph already handles data flow. The Action DAG is purely control flow.

## Domain Models

### ActionDefinition
A single executable DAG representing a workflow unit.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Unique identifier |
| `Name` | `string` | Human-readable action name |
| `Description` | `string?` | Action description |
| `UserId` | `Guid` | Owner user |
| `Status` | `ActionStatus` | Draft / Active / Completed / Failed / Archived |
| `Nodes` | `List<ActionNode>` | Nodes in this action's DAG |
| `Edges` | `List<ActionEdge>` | Directed edges (control dependencies) |
| `Dependencies` | `List<ActionDependency>` | Links to prerequisite actions |
| `TemplateId` | `Guid?` | If instantiated from an ActionTemplate |
| `DappSeriesId` | `Guid?` | Parent dApp series (if part of one) |
| `Metadata` | `Dictionary<string, string>` | Custom tags, version, etc. |
| `CreatedDate` | `DateTime` | Creation timestamp (matches codebase: `DateTime.UtcNow`) |
| `CompletedDate` | `DateTime?` | Completion timestamp |

### ActionStatus (enum)
`Draft` → `Active` → `Completed` | `Failed` → `Archived`

### ActionNode
A single task/step within an action DAG. Wraps a call to an existing AZOA manager method.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Unique node instance ID |
| `ActionId` | `Guid` | Parent action |
| `NodeTemplateId` | `Guid?` | Reference to a reusable ActionNodeTemplate (meta-node) |
| `NodeType` | `ActionNodeType` | Type of operation — maps to a specific manager method |
| `Name` | `string` | Node label |
| `Config` | `string` (JSON) | Node-specific config (deserialized to the matching request model at execution time) |
| `State` | `ActionNodeState` | Pending / Running / Succeeded / Failed / Skipped |
| `Output` | `string?` (JSON) | Serialized `AZOAResult<T>` from the manager call |
| `Error` | `string?` | Error message if failed |
| `IsEntry` | `bool` | Entry point node (no incoming control edges) |
| `IsTerminal` | `bool` | Terminal node (no outgoing control edges) |
| `ExecutionOrder` | `int` | Topological position (computed) |

### ActionNodeType (enum)
Each value maps to a specific existing manager method:

| Value | Dispatches To | Description |
|-------|--------------|-------------|
| `HolonCreate` | `IHolonManager.CreateAsync` | Create a new holon |
| `HolonUpdate` | `IHolonManager.UpdateAsync` | Update an existing holon |
| `HolonDelete` | `IHolonManager.DeleteAsync` | Delete a holon |
| `HolonGet` | `IHolonManager.GetAsync` | Get a holon by id |
| `HolonQuery` | `IHolonManager.QueryAsync` | Query/search holons across providers |
| `HolonInteract` | `IHolonManager.InteractAsync` | Custom interaction with a holon |
| `HolonGetChildren` | `IHolonManager.GetChildrenAsync` | Get child holons |
| `HolonGetPeers` | `IHolonManager.GetPeersAsync` | Get peer holons |
| `HolonGetAncestors` | `IHolonManager.GetAncestorsAsync` | Get ancestor holons |
| `HolonGetDescendants` | `IHolonManager.GetDescendantsAsync` | Get descendant holons |
| `HolonPropagate` | `IHolonManager.PropagateAsync` | Propagate across the holarchy |
| `HolonCompose` | `IHolonManager.ComposeAsync` | Compose holon subgraph |
| `HolonClone` | `IHolonManager.CloneAsync` | Clone a holon |
| `HolonMoveSubtree` | `IHolonManager.MoveSubtreeAsync` | Move subtree to new parent |
| `NftMint` | `INftManager.MintAsync` | Mint an NFT |
| `NftTransfer` | `INftManager.TransferAsync` | Transfer an NFT |
| `NftBurn` | `INftManager.BurnAsync` | Burn an NFT |
| `NftGet` | `INftManager.GetAsync` | Get an NFT |
| `NftQuery` | `INftManager.QueryAsync` | Query NFTs |
| `NftGetMetadata` | `INftManager.GetMetadataAsync` | Get NFT metadata |
| `WalletCreate` | `IWalletManager.CreateAsync` | Create a wallet |
| `WalletUpdate` | `IWalletManager.UpdateAsync` | Update a wallet |
| `WalletDelete` | `IWalletManager.DeleteAsync` | Delete a wallet |
| `WalletGet` | `IWalletManager.GetAsync` | Get a wallet |
| `WalletQuery` | `IWalletManager.QueryAsync` | Query wallets |
| `WalletSetDefault` | `IWalletManager.SetDefaultAsync` | Set default wallet for user |
| `WalletGetPortfolio` | `IWalletManager.GetPortfolioAsync` | Get portfolio with live balances |
| `ActionGraphBundleGenerate` | `IActionGraphBundleManager.GenerateAsync` | Generate dapp from Action Graph Bundle template |
| `ActionGraphBundleDeploy` | `IActionGraphBundleManager.DeployAsync` | Deploy an Action Graph Bundle dapp to target chain |
| `Search` | `ISearchManager.SearchAsync` | Cross-entity search |
| `UserNFTGetComposite` | `IUserNFTService.GetCompositeAsync` | Get user+NFT composite view |
| `BlockchainExecute` | `IBlockchainOperationManager.ExecuteAsync` | Execute raw blockchain operation |
| `Condition` | *(internal)* | Conditional gate — evaluates against accumulated outputs |
| `ComposeOutputs` | *(internal)* | Merge outputs from multiple upstream nodes |

### ActionNodeState (enum)
`Pending` → `Running` → `Succeeded` | `Failed` | `Skipped`

### ActionEdge
A directed control-flow dependency between two nodes in the same action.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Unique edge ID |
| `ActionId` | `Guid` | Parent action |
| `SourceNodeId` | `Guid` | Predecessor node (must complete first) |
| `TargetNodeId` | `Guid` | Successor node (depends on source) |
| `Condition` | `string?` (JSON) | Optional condition for edge activation (used only with `Conditional` type) |
| `EdgeType` | `ActionEdgeType` | Control / Conditional |

### ActionEdgeType (enum)
| Value | Description |
|-------|-------------|
| `Control` | Hard dependency — source must succeed before target runs |
| `Conditional` | Only activates if condition expression evaluates true |

### ActionDependency
A cross-action dependency — this action depends on the completion (or specific node output) of a prior action.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Unique dependency ID |
| `ActionId` | `Guid` | The action that has this dependency |
| `DependsOnActionId` | `Guid` | The prerequisite action |
| `DependsOnNodeId` | `Guid?` | Optional: specific node output to depend on |
| `DependencyType` | `ActionDependencyType` | Required / Optional |

### ActionDependencyType (enum)
| Value | Description |
|-------|-------------|
| `Required` | Must be satisfied before action can activate |
| `Optional` | If available, use it; action can proceed without |

### ActionNodeTemplate (Meta-Node)
A reusable node definition that can be instantiated across multiple actions. "Node 2 in iter1 becomes node 1 in iter2."

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Template ID (canonical across instantiations) |
| `Name` | `string` | Template name |
| `NodeType` | `ActionNodeType` | Operation type |
| `Description` | `string?` | What this template does |
| `DefaultConfig` | `string` (JSON) | Default configuration payload |
| `ConfigSchema` | `string` (JSON Schema) | JSON Schema for config validation |
| `InputSchema` | `string` (JSON Schema) | Expected inputs from upstream nodes |
| `OutputSchema` | `string` (JSON Schema) | Produced outputs |
| `Version` | `string` | Semantic version (e.g. "1.0.0") |
| `AuthorUserId` | `Guid` | Template author |
| `IsPublic` | `bool` | Available to all users or private |
| `Tags` | `List<string>` | Categorization tags |

### ActionTemplate
A reusable action definition — a full DAG template that can be instantiated with parameters.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Template ID |
| `Name` | `string` | Template name |
| `Description` | `string?` | Description |
| `AuthorUserId` | `Guid` | Template author |
| `Nodes` | `List<ActionTemplateNode>` | Template nodes with param placeholders |
| `Edges` | `List<ActionTemplateEdge>` | Template edges |
| `Parameters` | `string` (JSON Schema) | Parameters required for instantiation |
| `Version` | `string` | Semantic version |
| `IsPublic` | `bool` | Public or private |
| `Tags` | `List<string>` | Tags |

### ActionTemplateNode
A node within an ActionTemplate (parameterized, not yet instantiated).

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Unique ID |
| `TemplateId` | `Guid` | Parent template |
| `SlotId` | `string` | Logical slot identifier (e.g. "step_1", "boss_fight") |
| `NodeTemplateId` | `Guid` | Reference to ActionNodeTemplate |
| `ParamOverrides` | `string` (JSON) | Template-level param overrides |
| `IsEntry` | `bool` | Entry point in template |
| `IsTerminal` | `bool` | Terminal point in template |

### ActionTemplateEdge
An edge within an ActionTemplate.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Unique ID |
| `TemplateId` | `Guid` | Parent template |
| `SourceSlotId` | `string` | Source slot |
| `TargetSlotId` | `string` | Target slot |
| `EdgeType` | `ActionEdgeType` | Edge type |

## DAG Invariants
1. **Acyclicity**: No cycles within a single action's DAG (topological sort must succeed).
2. **Single Entry**: At least one entry node (`IsEntry = true`, no incoming control edges).
3. **Single Terminal**: At least one terminal node (`IsTerminal = true`, no outgoing control edges).
4. **No Orphan Nodes**: Every node must be reachable from an entry node.
5. **Cross-Action Dependencies**: Tracked separately via `ActionDependency`, not as DAG edges.
6. **Node Template Compatibility**: When instantiating from an ActionNodeTemplate, the config must validate against the template's `ConfigSchema`.

## Interfaces

### `IActionRepository`
Persistence abstraction wrapping `AZOADbContext`. Uses the same EF patterns as existing models (JSON converters for `Dictionary<string, string>` and `List<Guid>`).

```csharp
Task<ActionDefinition?> GetByIdAsync(Guid id);
Task<IEnumerable<ActionDefinition>> GetByUserIdAsync(Guid userId);
Task<IEnumerable<ActionDefinition>> GetByDappSeriesIdAsync(Guid dappSeriesId);
Task<ActionDefinition> CreateAsync(ActionDefinition action);
Task<ActionDefinition> UpdateAsync(ActionDefinition action);
Task<bool> DeleteAsync(Guid id);
Task<IEnumerable<ActionNodeTemplate>> GetNodeTemplatesAsync(bool? publicOnly = null);
Task<ActionNodeTemplate?> GetNodeTemplateByIdAsync(Guid id);
Task<ActionNodeTemplate> CreateNodeTemplateAsync(ActionNodeTemplate template);
Task<IEnumerable<ActionTemplate>> GetActionTemplatesAsync(bool? publicOnly = null);
Task<ActionTemplate?> GetActionTemplateByIdAsync(Guid id);
Task<ActionTemplate> CreateActionTemplateAsync(ActionTemplate template);
```

### `IActionDagValidator`
Validates DAG structure and invariants.

```csharp
DagValidationResult Validate(ActionDefinition action);
// Returns: IsValid, Errors[], TopologicalOrder[]
```

### `IActionInstantiator`
Instantiates an ActionDefinition from an ActionTemplate with parameters.

```csharp
Task<ActionDefinition> InstantiateAsync(Guid templateId, string parametersJson, Guid userId);
```

## Acceptance Criteria
- [ ] All domain models defined with proper relationships
- [ ] `DateTime` used (not `DateTimeOffset`) — consistent with existing models (`User.CreatedDate`, `Wallet.CreatedDate`, `Holon.CreatedDate`)
- [ ] `ActionNodeType` values map 1:1 to existing manager methods (see table above)
- [ ] `IActionRepository` interface with full CRUD
- [ ] `IActionDagValidator` enforces acyclicity, entry/terminal, orphan checks
- [ ] `IActionInstantiator` validates template params and produces valid ActionDefinition
- [ ] EF configurations use same patterns as `AZOADbContext` (dictConverter, listGuidConverter)
- [ ] Unit tests for DAG validation (cycle detection, topological sort)
- [ ] Unit tests for template instantiation with parameter substitution
- [ ] Builds cleanly with `dotnet build`
