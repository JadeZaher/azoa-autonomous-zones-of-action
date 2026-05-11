# Quest API — Specification

## Goal
Expose the Quest DAG system via REST API. Provides CRUD for quests, templates, and node templates, plus quest execution orchestration. Builds on quest-core domain models and dispatches node execution to existing OASIS managers.

## Architecture
```
Controllers → QuestManager → IQuestRepository → OASISDbContext
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
| `HolonCreate` | `HolonCreateModel` | `_holonManager.CreateAsync(model, avatarId, oasisNull)` |
| `HolonUpdate` | `HolonUpdateModel` | `_holonManager.UpdateAsync(holonId, model, oasisNull)` |
| `HolonDelete` | `{ holonId }` | `_holonManager.DeleteAsync(holonId, oasisNull)` |
| `HolonGet` | `{ holonId }` | `_holonManager.GetAsync(holonId, oasisNull)` |
| `HolonQuery` | `HolonQueryRequest` | `_holonManager.QueryAsync(query, oasisNull)` |
| `HolonInteract` | `HolonInteractionRequest` | `_holonManager.InteractAsync(holonId, request, oasisNull)` |
| `HolonPropagate` | `HolonPropagateRequest` | `_holonManager.PropagateAsync(holonId, request, oasisNull)` |
| `HolonCompose` | `{ holonId }` | `_holonManager.ComposeAsync(holonId, oasisNull)` |
| `HolonClone` | `HolonCloneRequest` | `_holonManager.CloneAsync(holonId, request, avatarId, oasisNull)` |
| `HolonMoveSubtree` | `MoveSubtreeRequest` | `_holonManager.MoveSubtreeAsync(holonId, newParentId, oasisNull)` |
| `NftMint` | `NftMintRequest` | `_nftManager.MintAsync(request, avatarId, oasisNull)` |
| `NftTransfer` | `NftTransferRequest` | `_nftManager.TransferAsync(nftId, request, avatarId, oasisNull)` |
| `NftBurn` | `NftBurnRequest` | `_nftManager.BurnAsync(nftId, walletId, avatarId, oasisNull)` |
| `NftGet` | `{ nftId }` | `_nftManager.GetAsync(nftId, oasisNull)` |
| `NftQuery` | `{ chainId?, owner?, ... }` | `_nftManager.QueryAsync(query, oasisNull)` |
| `NftGetMetadata` | `{ nftId }` | `_nftManager.GetMetadataAsync(nftId, oasisNull)` |
| `WalletCreate` | `WalletCreateModel` | `_walletManager.CreateAsync(model, avatarId, oasisNull)` |
| `WalletUpdate` | `WalletUpdateModel` | `_walletManager.UpdateAsync(walletId, model, oasisNull)` |
| `WalletDelete` | `{ walletId }` | `_walletManager.DeleteAsync(walletId, oasisNull)` |
| `WalletGet` | `{ walletId }` | `_walletManager.GetAsync(walletId, oasisNull)` |
| `WalletQuery` | `WalletQueryRequest` | `_walletManager.QueryAsync(query, oasisNull)` |
| `WalletSetDefault` | `{ walletId }` | `_walletManager.SetDefaultAsync(avatarId, walletId, oasisNull)` |
| `WalletGetPortfolio` | `{ walletId }` | `_walletManager.GetPortfolioAsync(walletId, oasisNull)` |
| `StarGenerate` | `{ starId, STARDappGenerationRequest }` | `_starManager.GenerateAsync(starId, request, oasisNull)` |
| `StarDeploy` | `{ starId }` | `_starManager.DeployAsync(starId, oasisNull)` |
| `Search` | `SearchRequest` | `_searchManager.SearchAsync(query, avatarId, oasisNull)` |
| `AvatarNFTGetComposite` | `{ avatarId }` | `_avatarNFTService.GetAvatarNFTCompositeAsync(avatarId)` |
| `BlockchainExecute` | `BlockchainOperation` (built from config) | `_blockchainManager.ExecuteAsync(operation, oasisNull)` |
| `Condition` | `{ expression, inputs }` | Evaluate expression against accumulated node outputs |
| `ComposeOutputs` | `{ outputFields }` | Merge upstream outputs into single JSON |

## Manager: `IQuestManager`

```csharp
// CRUD
Task<OASISResult<Quest>> CreateAsync(Guid avatarId, QuestCreateModel model);
Task<OASISResult<Quest>> GetAsync(Guid questId, Guid avatarId);
Task<OASISResult<IEnumerable<Quest>>> ListAsync(Guid avatarId, QuestStatus? status = null, Guid? dappSeriesId = null);
Task<OASISResult<Quest>> UpdateAsync(Guid questId, Guid avatarId, QuestUpdateModel model);
Task<OASISResult<bool>> DeleteAsync(Guid questId, Guid avatarId);

// Nodes
Task<OASISResult<QuestNode>> AddNodeAsync(Guid questId, Guid avatarId, QuestNodeCreateModel model);
Task<OASISResult<QuestNode>> UpdateNodeAsync(Guid questId, Guid nodeId, Guid avatarId, QuestNodeUpdateModel model);
Task<OASISResult<bool>> DeleteNodeAsync(Guid questId, Guid nodeId, Guid avatarId);

// Edges
Task<OASISResult<QuestEdge>> AddEdgeAsync(Guid questId, Guid avatarId, QuestEdgeCreateModel model);
Task<OASISResult<bool>> RemoveEdgeAsync(Guid questId, Guid edgeId, Guid avatarId);
Task<OASISResult<int[]>> GetTopologicalOrderAsync(Guid questId, Guid avatarId);

// Dependencies
Task<OASISResult<QuestDependency>> AddDependencyAsync(Guid questId, Guid avatarId, QuestDependencyCreateModel model);
Task<OASISResult<bool>> RemoveDependencyAsync(Guid questId, Guid depId, Guid avatarId);
Task<OASISResult<DependencyCheckResult>> CheckDependenciesAsync(Guid questId, Guid avatarId);

// Execution
Task<OASISResult<Quest>> ActivateAsync(Guid questId, Guid avatarId);
Task<OASISResult<Quest>> CompleteAsync(Guid questId, Guid avatarId, string? output = null);
Task<OASISResult<Quest>> FailAsync(Guid questId, Guid avatarId, string error);
Task<OASISResult<QuestExecutionState>> GetExecutionStateAsync(Guid questId, Guid avatarId);
Task<OASISResult<Quest>> ExecuteNextAsync(Guid questId, Guid avatarId);
Task<OASISResult<Quest>> ExecuteNodeAsync(Guid questId, Guid nodeId, Guid avatarId);

// Templates
Task<OASISResult<Quest>> InstantiateFromTemplateAsync(Guid avatarId, QuestInstantiateModel model);
Task<OASISResult<QuestNodeTemplate>> CreateNodeTemplateAsync(Guid avatarId, QuestNodeTemplateCreateModel model);
Task<OASISResult<IEnumerable<QuestNodeTemplate>>> ListNodeTemplatesAsync(bool publicOnly = true);
Task<OASISResult<QuestTemplate>> CreateQuestTemplateAsync(Guid avatarId, QuestTemplateCreateModel model);
Task<OASISResult<IEnumerable<QuestTemplate>>> ListQuestTemplatesAsync(bool publicOnly = true);
```

All methods return `OASISResult<T>`, matching the existing manager pattern.

## Controller Pattern

```csharp
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class QuestController : ControllerBase
{
    private readonly IQuestManager _manager;

    [HttpGet]
    public async Task<ActionResult<OASISResult<IEnumerable<Quest>>>> List(
        [FromQuery] QuestStatus? status = null, [FromQuery] Guid? dappSeriesId = null)
    {
        var avatarId = GetAvatarIdFromClaims();
        if (avatarId == null)
            return Unauthorized(new OASISResult<IEnumerable<Quest>> { IsError = true, Message = "Invalid token." });

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
- [ ] All endpoints return `OASISResult<T>` or `OASISResponse`
- [ ] `[Authorize]` on all controllers; `GetAvatarIdFromClaims()` using `ClaimTypes.NameIdentifier` / `"sub"`
- [ ] Avatar-scoped access control
- [ ] DAG validation on edge add, quest activate
- [ ] Dependency check on quest activate
- [ ] Node dispatch table maps every `QuestNodeType` to correct existing manager method
- [ ] Config JSON validated against expected request model type per node type
- [ ] Topological order cached, invalidated on DAG mutation
- [ ] Swagger UI lists all quest endpoints
- [ ] Builds cleanly with `dotnet build`
