# Quest API — Specification

## Goal
Expose the Quest DAG system via REST API. Provides CRUD for quests, templates, and node templates, plus quest execution orchestration. Builds on quest-core domain models and dispatches node execution to existing AZOA managers.

## Architecture
```
Controllers → QuestManager → IQuestRepository → AZOADbContext
                         ↓
                    IHolonManager        (holon CRUD, query, interact, propagate, compose, clone, move-subtree)
                    INftManager          (mint, transfer, burn)
                    IWalletManager       (wallet CRUD, portfolio)
                    ISTARManager         (generate, deploy)
                    ISearchManager       (cross-entity search)
                    IAvatarNFTService    (composite views, bindings)
                    IBlockchainOperationManager
```

## Endpoints

### Quest CRUD

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/quests` | List quests for authenticated avatar |
| `GET` | `/api/quests/{id}` | Get quest detail (nodes, edges, dependencies) |
| `POST` | `/api/quests` | Create quest (Draft status) |
| `PUT` | `/api/quests/{id}` | Update quest metadata (Draft only) |
| `DELETE` | `/api/quests/{id}` | Delete quest (Draft only) |
| `POST` | `/api/quests/from-template` | Instantiate from QuestTemplate |

### Quest Nodes

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/quests/{questId}/nodes` | List nodes |
| `POST` | `/api/quests/{questId}/nodes` | Add node (Draft only) |
| `PUT` | `/api/quests/{questId}/nodes/{nodeId}` | Update node config (Draft only) |
| `DELETE` | `/api/quests/{questId}/nodes/{nodeId}` | Remove node (Draft only) |

### Quest Edges

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/quests/{questId}/edges` | Add edge (triggers DAG validation) |
| `DELETE` | `/api/quests/{questId}/edges/{edgeId}` | Remove edge |
| `GET` | `/api/quests/{questId}/topological-order` | Get topological sort |

### Quest Dependencies

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/quests/{questId}/dependencies` | Add cross-quest dependency |
| `DELETE` | `/api/quests/{questId}/dependencies/{depId}` | Remove dependency |
| `GET` | `/api/quests/{questId}/dependency-status` | Check if all dependencies satisfied |

### Quest Execution

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/quests/{id}/activate` | Draft → Active (validates DAG + dependencies) |
| `POST` | `/api/quests/{id}/execute-next` | Execute next ready node(s) |
| `POST` | `/api/quests/{id}/execute-node/{nodeId}` | Execute specific ready node |
| `POST` | `/api/quests/{id}/complete` | Mark quest completed |
| `POST` | `/api/quests/{id}/fail` | Mark quest failed |
| `GET` | `/api/quests/{id}/execution-state` | Current execution state + progress |

### Templates

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/quests/node-templates` | List available node templates |
| `POST` | `/api/quests/node-templates` | Create node template |
| `GET` | `/api/quests/templates` | List available quest templates |
| `POST` | `/api/quests/templates` | Create quest template from completed quest |

## Authentication
- `[Authorize]` on all controllers
- `avatarId` extracted via `GetAvatarIdFromClaims()` — uses `User.FindFirst(ClaimTypes.NameIdentifier)` or `User.FindFirst("sub")`
- Avatar-scoped: users only see their own quests (public templates excluded)

## Node Execution Dispatch

When a node executes, the QuestManager deserializes the Config JSON to the matching request model and dispatches to the correct manager:

| QuestNodeType | Config Deserializes To | Dispatch Call |
|---|---|---|
| `HolonCreate` | `HolonCreateModel` | `_holonManager.CreateAsync(model, avatarId, azoaNull)` |
| `HolonUpdate` | `HolonUpdateModel` | `_holonManager.UpdateAsync(holonId, model, azoaNull)` |
| `HolonDelete` | `{ holonId }` | `_holonManager.DeleteAsync(holonId, azoaNull)` |
| `HolonGet` | `{ holonId }` | `_holonManager.GetAsync(holonId, azoaNull)` |
| `HolonQuery` | `HolonQueryRequest` | `_holonManager.QueryAsync(query, azoaNull)` |
| `HolonInteract` | `HolonInteractionRequest` | `_holonManager.InteractAsync(holonId, request, azoaNull)` |
| `HolonPropagate` | `HolonPropagateRequest` | `_holonManager.PropagateAsync(holonId, request, azoaNull)` |
| `HolonCompose` | `{ holonId }` | `_holonManager.ComposeAsync(holonId, azoaNull)` |
| `HolonClone` | `HolonCloneRequest` | `_holonManager.CloneAsync(holonId, request, avatarId, azoaNull)` |
| `HolonMoveSubtree` | `MoveSubtreeRequest` | `_holonManager.MoveSubtreeAsync(holonId, newParentId, azoaNull)` |
| `NftMint` | `NftMintRequest` | `_nftManager.MintAsync(request, avatarId, azoaNull)` |
| `NftTransfer` | `NftTransferRequest` | `_nftManager.TransferAsync(nftId, request, avatarId, azoaNull)` |
| `NftBurn` | `NftBurnRequest` | `_nftManager.BurnAsync(nftId, walletId, avatarId, azoaNull)` |
| `NftGet` | `{ nftId }` | `_nftManager.GetAsync(nftId, azoaNull)` |
| `NftQuery` | `{ chainId?, owner?, ... }` | `_nftManager.QueryAsync(query, azoaNull)` |
| `NftGetMetadata` | `{ nftId }` | `_nftManager.GetMetadataAsync(nftId, azoaNull)` |
| `WalletCreate` | `WalletCreateModel` | `_walletManager.CreateAsync(model, avatarId, azoaNull)` |
| `WalletUpdate` | `WalletUpdateModel` | `_walletManager.UpdateAsync(walletId, model, azoaNull)` |
| `WalletDelete` | `{ walletId }` | `_walletManager.DeleteAsync(walletId, azoaNull)` |
| `WalletGet` | `{ walletId }` | `_walletManager.GetAsync(walletId, azoaNull)` |
| `WalletQuery` | `WalletQueryRequest` | `_walletManager.QueryAsync(query, azoaNull)` |
| `WalletSetDefault` | `{ walletId }` | `_walletManager.SetDefaultAsync(avatarId, walletId, azoaNull)` |
| `WalletGetPortfolio` | `{ walletId }` | `_walletManager.GetPortfolioAsync(walletId, azoaNull)` |
| `StarGenerate` | `{ starId, STARDappGenerationRequest }` | `_starManager.GenerateAsync(starId, request, azoaNull)` |
| `StarDeploy` | `{ starId }` | `_starManager.DeployAsync(starId, azoaNull)` |
| `Search` | `SearchRequest` | `_searchManager.SearchAsync(query, avatarId, azoaNull)` |
| `AvatarNFTGetComposite` | `{ avatarId }` | `_avatarNFTService.GetAvatarNFTCompositeAsync(avatarId)` |
| `BlockchainExecute` | `BlockchainOperation` (built from config) | `_blockchainManager.ExecuteAsync(operation, azoaNull)` |
| `Condition` | `{ expression, inputs }` | Evaluate expression against accumulated node outputs |
| `ComposeOutputs` | `{ outputFields }` | Merge upstream outputs into single JSON |

## Manager: `IQuestManager`

```csharp
// CRUD
Task<AZOAResult<Quest>> CreateAsync(Guid avatarId, QuestCreateModel model);
Task<AZOAResult<Quest>> GetAsync(Guid questId, Guid avatarId);
Task<AZOAResult<IEnumerable<Quest>>> ListAsync(Guid avatarId, QuestStatus? status = null, Guid? dappSeriesId = null);
Task<AZOAResult<Quest>> UpdateAsync(Guid questId, Guid avatarId, QuestUpdateModel model);
Task<AZOAResult<bool>> DeleteAsync(Guid questId, Guid avatarId);

// Nodes
Task<AZOAResult<QuestNode>> AddNodeAsync(Guid questId, Guid avatarId, QuestNodeCreateModel model);
Task<AZOAResult<QuestNode>> UpdateNodeAsync(Guid questId, Guid nodeId, Guid avatarId, QuestNodeUpdateModel model);
Task<AZOAResult<bool>> DeleteNodeAsync(Guid questId, Guid nodeId, Guid avatarId);

// Edges
Task<AZOAResult<QuestEdge>> AddEdgeAsync(Guid questId, Guid avatarId, QuestEdgeCreateModel model);
Task<AZOAResult<bool>> RemoveEdgeAsync(Guid questId, Guid edgeId, Guid avatarId);
Task<AZOAResult<int[]>> GetTopologicalOrderAsync(Guid questId, Guid avatarId);

// Dependencies
Task<AZOAResult<QuestDependency>> AddDependencyAsync(Guid questId, Guid avatarId, QuestDependencyCreateModel model);
Task<AZOAResult<bool>> RemoveDependencyAsync(Guid questId, Guid depId, Guid avatarId);
Task<AZOAResult<DependencyCheckResult>> CheckDependenciesAsync(Guid questId, Guid avatarId);

// Execution
Task<AZOAResult<Quest>> ActivateAsync(Guid questId, Guid avatarId);
Task<AZOAResult<Quest>> CompleteAsync(Guid questId, Guid avatarId, string? output = null);
Task<AZOAResult<Quest>> FailAsync(Guid questId, Guid avatarId, string error);
Task<AZOAResult<QuestExecutionState>> GetExecutionStateAsync(Guid questId, Guid avatarId);
Task<AZOAResult<Quest>> ExecuteNextAsync(Guid questId, Guid avatarId);
Task<AZOAResult<Quest>> ExecuteNodeAsync(Guid questId, Guid nodeId, Guid avatarId);

// Templates
Task<AZOAResult<Quest>> InstantiateFromTemplateAsync(Guid avatarId, QuestInstantiateModel model);
Task<AZOAResult<QuestNodeTemplate>> CreateNodeTemplateAsync(Guid avatarId, QuestNodeTemplateCreateModel model);
Task<AZOAResult<IEnumerable<QuestNodeTemplate>>> ListNodeTemplatesAsync(bool publicOnly = true);
Task<AZOAResult<QuestTemplate>> CreateQuestTemplateAsync(Guid avatarId, QuestTemplateCreateModel model);
Task<AZOAResult<IEnumerable<QuestTemplate>>> ListQuestTemplatesAsync(bool publicOnly = true);
```

All methods return `AZOAResult<T>`, matching the existing manager pattern.

## Controller Pattern

```csharp
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class QuestController : ControllerBase
{
    private readonly IQuestManager _manager;

    [HttpGet]
    public async Task<ActionResult<AZOAResult<IEnumerable<Quest>>>> List(
        [FromQuery] QuestStatus? status = null, [FromQuery] Guid? dappSeriesId = null)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new AZOAResult<IEnumerable<Quest>> { IsError = true, Message = "Invalid token." });

        var result = await _manager.ListAsync(avatarId.Value, status, dappSeriesId);
        return Ok(result);
    }

    private Guid? GetAvatarIdFromClaims()
    {
        var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return sub != null ? Guid.Parse(sub) : null;
    }
}
```

## Acceptance Criteria
- [ ] All endpoints return `AZOAResult<T>` or `AZOAResponse`
- [ ] `[Authorize]` on all controllers; `GetAvatarIdFromClaims()` using `ClaimTypes.NameIdentifier` / `"sub"`
- [ ] Avatar-scoped access control
- [ ] DAG validation on edge add, quest activate
- [ ] Dependency check on quest activate
- [ ] Node dispatch table maps every `QuestNodeType` to correct existing manager method
- [ ] Config JSON validated against expected request model type per node type
- [ ] Topological order cached, invalidated on DAG mutation
- [ ] Swagger UI lists all quest endpoints
- [ ] Builds cleanly with `dotnet build`
