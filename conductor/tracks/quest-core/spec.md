# Quest Core — Specification

## Goal
Define the core domain models and abstractions for the **Quest DAG system** — a control-flow orchestration layer on top of the holonic graph and STAR dapp-generator. Each Quest is a DAG with entry/terminal nodes, optional dependencies on completed quests, and reusable node templates (meta-nodes). A series of Quests composes into a dApp contract.

## Architecture Overview

### Layering
```
┌─────────────────────────────────────────────────────────────┐
│  dApp (Quest Series) → Contract Generation                   │  (dapp-composition track)
├─────────────────────────────────────────────────────────────┤
│  Quest API — REST, Manager, Execution                        │  (quest-api track)
├─────────────────────────────────────────────────────────────┤
│  Quest Core — Models, DAG, Templates                         │  ← THIS TRACK
├─────────────────────────────────────────────────────────────┤
│  Existing OASIS Manager Layer                                │
│  IHolonManager | INftManager | IWalletManager               │
│  ISTARManager | ISearchManager | IAvatarNFTService          │
│  IBlockchainOperationManager                                 │
├─────────────────────────────────────────────────────────────┤
│  Holon Graph (nested links, peer IDs, parent/children)       │  (data flow, not control)
└─────────────────────────────────────────────────────────────┘
```

### Design Principle
The Quest DAG is a **control abstraction** — it encodes execution ordering (what runs before what). The underlying holonic graph (parent/children/peers/nested links) handles data flow and dynamic reconfiguration. The DAG does **not** replace the holon graph; it orchestrates it.

> **No `DataHint` edges** — the holonic graph already handles data flow. The Quest DAG is purely control flow.

## Domain Models

### Quest
A single executable DAG representing a workflow unit.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Unique identifier |
| `Name` | `string` | Human-readable quest name |
| `Description` | `string?` | Quest description |
| `AvatarId` | `Guid` | Owner avatar |
| `Status` | `QuestStatus` | Draft / Active / Completed / Failed / Archived |
| `Nodes` | `List<QuestNode>` | Nodes in this quest's DAG |
| `Edges` | `List<QuestEdge>` | Directed edges (control dependencies) |
| `Dependencies` | `List<QuestDependency>` | Links to prerequisite quests |
| `TemplateId` | `Guid?` | If instantiated from a QuestTemplate |
| `DappSeriesId` | `Guid?` | Parent dApp series (if part of one) |
| `Metadata` | `Dictionary<string, string>` | Custom tags, version, etc. |
| `CreatedDate` | `DateTime` | Creation timestamp (matches codebase: `DateTime.UtcNow`) |
| `CompletedDate` | `DateTime?` | Completion timestamp |

### QuestStatus (enum)
`Draft` → `Active` → `Completed` | `Failed` → `Archived`

### QuestNode
A single task/step within a quest DAG. Wraps a call to an existing OASIS manager method.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Unique node instance ID |
| `QuestId` | `Guid` | Parent quest |
| `NodeTemplateId` | `Guid?` | Reference to a reusable QuestNodeTemplate (meta-node) |
| `NodeType` | `QuestNodeType` | Type of operation — maps to a specific manager method |
| `Name` | `string` | Node label |
| `Config` | `string` (JSON) | Node-specific config (deserialized to the matching request model at execution time) |
| `State` | `QuestNodeState` | Pending / Running / Succeeded / Failed / Skipped |
| `Output` | `string?` (JSON) | Serialized `OASISResult<T>` from the manager call |
| `Error` | `string?` | Error message if failed |
| `IsEntry` | `bool` | Entry point node (no incoming control edges) |
| `IsTerminal` | `bool` | Terminal node (no outgoing control edges) |
| `ExecutionOrder` | `int` | Topological position (computed) |

### QuestNodeType (enum)
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
| `WalletSetDefault` | `IWalletManager.SetDefaultAsync` | Set default wallet for avatar |
| `WalletGetPortfolio` | `IWalletManager.GetPortfolioAsync` | Get portfolio with live balances |
| `StarGenerate` | `ISTARManager.GenerateAsync` | Generate dapp from STAR template |
| `StarDeploy` | `ISTARManager.DeployAsync` | Deploy a STAR dapp to target chain |
| `Search` | `ISearchManager.SearchAsync` | Cross-entity search |
| `AvatarNFTGetComposite` | `IAvatarNFTService.GetCompositeAsync` | Get avatar+NFT composite view |
| `BlockchainExecute` | `IBlockchainOperationManager.ExecuteAsync` | Execute raw blockchain operation |
| `Condition` | *(internal)* | Conditional gate — evaluates against accumulated outputs |
| `ComposeOutputs` | *(internal)* | Merge outputs from multiple upstream nodes |

### QuestNodeState (enum)
`Pending` → `Running` → `Succeeded` | `Failed` | `Skipped`

### QuestEdge
A directed control-flow dependency between two nodes in the same quest.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Unique edge ID |
| `QuestId` | `Guid` | Parent quest |
| `SourceNodeId` | `Guid` | Predecessor node (must complete first) |
| `TargetNodeId` | `Guid` | Successor node (depends on source) |
| `Condition` | `string?` (JSON) | Optional condition for edge activation (used only with `Conditional` type) |
| `EdgeType` | `QuestEdgeType` | Control / Conditional |

### QuestEdgeType (enum)
| Value | Description |
|-------|-------------|
| `Control` | Hard dependency — source must succeed before target runs |
| `Conditional` | Only activates if condition expression evaluates true |

### QuestDependency
A cross-quest dependency — this quest depends on the completion (or specific node output) of a prior quest.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Unique dependency ID |
| `QuestId` | `Guid` | The quest that has this dependency |
| `DependsOnQuestId` | `Guid` | The prerequisite quest |
| `DependsOnNodeId` | `Guid?` | Optional: specific node output to depend on |
| `DependencyType` | `QuestDependencyType` | Required / Optional |

### QuestDependencyType (enum)
| Value | Description |
|-------|-------------|
| `Required` | Must be satisfied before quest can activate |
| `Optional` | If available, use it; quest can proceed without |

### QuestNodeTemplate (Meta-Node)
A reusable node definition that can be instantiated across multiple quests. "Node 2 in iter1 becomes node 1 in iter2."

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Template ID (canonical across instantiations) |
| `Name` | `string` | Template name |
| `NodeType` | `QuestNodeType` | Operation type |
| `Description` | `string?` | What this template does |
| `DefaultConfig` | `string` (JSON) | Default configuration payload |
| `ConfigSchema` | `string` (JSON Schema) | JSON Schema for config validation |
| `InputSchema` | `string` (JSON Schema) | Expected inputs from upstream nodes |
| `OutputSchema` | `string` (JSON Schema) | Produced outputs |
| `Version` | `string` | Semantic version (e.g. "1.0.0") |
| `AuthorAvatarId` | `Guid` | Template author |
| `IsPublic` | `bool` | Available to all avatars or private |
| `Tags` | `List<string>` | Categorization tags |

### QuestTemplate
A reusable quest definition — a full DAG template that can be instantiated with parameters.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Template ID |
| `Name` | `string` | Template name |
| `Description` | `string?` | Description |
| `AuthorAvatarId` | `Guid` | Template author |
| `Nodes` | `List<QuestTemplateNode>` | Template nodes with param placeholders |
| `Edges` | `List<QuestTemplateEdge>` | Template edges |
| `Parameters` | `string` (JSON Schema) | Parameters required for instantiation |
| `Version` | `string` | Semantic version |
| `IsPublic` | `bool` | Public or private |
| `Tags` | `List<string>` | Tags |

### QuestTemplateNode
A node within a QuestTemplate (parameterized, not yet instantiated).

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Unique ID |
| `TemplateId` | `Guid` | Parent template |
| `SlotId` | `string` | Logical slot identifier (e.g. "step_1", "boss_fight") |
| `NodeTemplateId` | `Guid` | Reference to QuestNodeTemplate |
| `ParamOverrides` | `string` (JSON) | Template-level param overrides |
| `IsEntry` | `bool` | Entry point in template |
| `IsTerminal` | `bool` | Terminal point in template |

### QuestTemplateEdge
An edge within a QuestTemplate.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Unique ID |
| `TemplateId` | `Guid` | Parent template |
| `SourceSlotId` | `string` | Source slot |
| `TargetSlotId` | `string` | Target slot |
| `EdgeType` | `QuestEdgeType` | Edge type |

## DAG Invariants
1. **Acyclicity**: No cycles within a single quest's DAG (topological sort must succeed).
2. **Single Entry**: At least one entry node (`IsEntry = true`, no incoming control edges).
3. **Single Terminal**: At least one terminal node (`IsTerminal = true`, no outgoing control edges).
4. **No Orphan Nodes**: Every node must be reachable from an entry node.
5. **Cross-Quest Dependencies**: Tracked separately via `QuestDependency`, not as DAG edges.
6. **Node Template Compatibility**: When instantiating from a QuestNodeTemplate, the config must validate against the template's `ConfigSchema`.

## Interfaces

### `IQuestRepository`
Persistence abstraction wrapping `OASISDbContext`. Uses the same EF patterns as existing models (JSON converters for `Dictionary<string, string>` and `List<Guid>`).

```csharp
Task<Quest?> GetByIdAsync(Guid id);
Task<IEnumerable<Quest>> GetByAvatarIdAsync(Guid avatarId);
Task<IEnumerable<Quest>> GetByDappSeriesIdAsync(Guid dappSeriesId);
Task<Quest> CreateAsync(Quest quest);
Task<Quest> UpdateAsync(Quest quest);
Task<bool> DeleteAsync(Guid id);
Task<IEnumerable<QuestNodeTemplate>> GetNodeTemplatesAsync(bool? publicOnly = null);
Task<QuestNodeTemplate?> GetNodeTemplateByIdAsync(Guid id);
Task<QuestNodeTemplate> CreateNodeTemplateAsync(QuestNodeTemplate template);
Task<IEnumerable<QuestTemplate>> GetQuestTemplatesAsync(bool? publicOnly = null);
Task<QuestTemplate?> GetQuestTemplateByIdAsync(Guid id);
Task<QuestTemplate> CreateQuestTemplateAsync(QuestTemplate template);
```

### `IQuestDagValidator`
Validates DAG structure and invariants.

```csharp
DagValidationResult Validate(Quest quest);
// Returns: IsValid, Errors[], TopologicalOrder[]
```

### `IQuestInstantiator`
Instantiates a Quest from a QuestTemplate with parameters.

```csharp
Task<Quest> InstantiateAsync(Guid templateId, string parametersJson, Guid avatarId);
```

## Acceptance Criteria
- [ ] All domain models defined with proper relationships
- [ ] `DateTime` used (not `DateTimeOffset`) — consistent with existing models (`Avatar.CreatedDate`, `Wallet.CreatedDate`, `Holon.CreatedDate`)
- [ ] `QuestNodeType` values map 1:1 to existing manager methods (see table above)
- [ ] `IQuestRepository` interface with full CRUD
- [ ] `IQuestDagValidator` enforces acyclicity, entry/terminal, orphan checks
- [ ] `IQuestInstantiator` validates template params and produces valid Quest
- [ ] EF configurations use same patterns as `OASISDbContext` (dictConverter, listGuidConverter)
- [ ] Unit tests for DAG validation (cycle detection, topological sort)
- [ ] Unit tests for template instantiation with parameter substitution
- [ ] Builds cleanly with `dotnet build`
