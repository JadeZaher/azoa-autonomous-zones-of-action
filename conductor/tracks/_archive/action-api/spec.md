---
type: Track Spec
title: "Action API — Specification"
tags: [action-api, archived]
---

# Action API — Specification

## Goal
Expose the Action DAG system via REST API. Provides CRUD for actions, templates, and node templates, plus action execution orchestration. Builds on action-core domain models and dispatches node execution to existing AZOA managers.

## Architecture
```
Controllers → ActionManager → IActionRepository → AZOADbContext
                         ↓
                    IHolonManager        (holon CRUD, query, interact, propagate, compose, clone, move-subtree)
                    INftManager          (mint, transfer, burn)
                    IWalletManager       (wallet CRUD, portfolio)
                    IActionGraphBundleManager         (generate, deploy)
                    ISearchManager       (cross-entity search)
                    IUserNFTService    (composite views, bindings)
                    IBlockchainOperationManager
```

## Endpoints

### Action CRUD

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/actions` | List actions for authenticated user |
| `GET` | `/api/actions/{id}` | Get action detail (nodes, edges, dependencies) |
| `POST` | `/api/actions` | Create action (Draft status) |
| `PUT` | `/api/actions/{id}` | Update action metadata (Draft only) |
| `DELETE` | `/api/actions/{id}` | Delete action (Draft only) |
| `POST` | `/api/actions/from-template` | Instantiate from ActionTemplate |

### Action Nodes

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/actions/{actionId}/nodes` | List nodes |
| `POST` | `/api/actions/{actionId}/nodes` | Add node (Draft only) |
| `PUT` | `/api/actions/{actionId}/nodes/{nodeId}` | Update node config (Draft only) |
| `DELETE` | `/api/actions/{actionId}/nodes/{nodeId}` | Remove node (Draft only) |

### Action Edges

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/actions/{actionId}/edges` | Add edge (triggers DAG validation) |
| `DELETE` | `/api/actions/{actionId}/edges/{edgeId}` | Remove edge |
| `GET` | `/api/actions/{actionId}/topological-order` | Get topological sort |

### Action Dependencies

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/actions/{actionId}/dependencies` | Add cross-action dependency |
| `DELETE` | `/api/actions/{actionId}/dependencies/{depId}` | Remove dependency |
| `GET` | `/api/actions/{actionId}/dependency-status` | Check if all dependencies satisfied |

### Action Execution

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/actions/{id}/activate` | Draft → Active (validates DAG + dependencies) |
| `POST` | `/api/actions/{id}/execute-next` | Execute next ready node(s) |
| `POST` | `/api/actions/{id}/execute-node/{nodeId}` | Execute specific ready node |
| `POST` | `/api/actions/{id}/complete` | Mark action completed |
| `POST` | `/api/actions/{id}/fail` | Mark action failed |
| `GET` | `/api/actions/{id}/execution-state` | Current execution state + progress |

### Templates

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/actions/node-templates` | List available node templates |
| `POST` | `/api/actions/node-templates` | Create node template |
| `GET` | `/api/actions/templates` | List available action templates |
| `POST` | `/api/actions/templates` | Create action template from completed action |

## Authentication
- `[Authorize]` on all controllers
- `userId` extracted via `GetUserIdFromClaims()` — uses `User.FindFirst(ClaimTypes.NameIdentifier)` or `User.FindFirst("sub")`
- User-scoped: users only see their own actions (public templates excluded)

## Node Execution Dispatch

When a node executes, the ActionManager deserializes the Config JSON to the matching request model and dispatches to the correct manager:

| ActionNodeType | Config Deserializes To | Dispatch Call |
|---|---|---|
| `HolonCreate` | `HolonCreateModel` | `_holonManager.CreateAsync(model, userId, azoaNull)` |
| `HolonUpdate` | `HolonUpdateModel` | `_holonManager.UpdateAsync(holonId, model, azoaNull)` |
| `HolonDelete` | `{ holonId }` | `_holonManager.DeleteAsync(holonId, azoaNull)` |
| `HolonGet` | `{ holonId }` | `_holonManager.GetAsync(holonId, azoaNull)` |
| `HolonQuery` | `HolonQueryRequest` | `_holonManager.QueryAsync(query, azoaNull)` |
| `HolonInteract` | `HolonInteractionRequest` | `_holonManager.InteractAsync(holonId, request, azoaNull)` |
| `HolonPropagate` | `HolonPropagateRequest` | `_holonManager.PropagateAsync(holonId, request, azoaNull)` |
| `HolonCompose` | `{ holonId }` | `_holonManager.ComposeAsync(holonId, azoaNull)` |
| `HolonClone` | `HolonCloneRequest` | `_holonManager.CloneAsync(holonId, request, userId, azoaNull)` |
| `HolonMoveSubtree` | `MoveSubtreeRequest` | `_holonManager.MoveSubtreeAsync(holonId, newParentId, azoaNull)` |
| `NftMint` | `NftMintRequest` | `_nftManager.MintAsync(request, userId, azoaNull)` |
| `NftTransfer` | `NftTransferRequest` | `_nftManager.TransferAsync(nftId, request, userId, azoaNull)` |
| `NftBurn` | `NftBurnRequest` | `_nftManager.BurnAsync(nftId, walletId, userId, azoaNull)` |
| `NftGet` | `{ nftId }` | `_nftManager.GetAsync(nftId, azoaNull)` |
| `NftQuery` | `{ chainId?, owner?, ... }` | `_nftManager.QueryAsync(query, azoaNull)` |
| `NftGetMetadata` | `{ nftId }` | `_nftManager.GetMetadataAsync(nftId, azoaNull)` |
| `WalletCreate` | `WalletCreateModel` | `_walletManager.CreateAsync(model, userId, azoaNull)` |
| `WalletUpdate` | `WalletUpdateModel` | `_walletManager.UpdateAsync(walletId, model, azoaNull)` |
| `WalletDelete` | `{ walletId }` | `_walletManager.DeleteAsync(walletId, azoaNull)` |
| `WalletGet` | `{ walletId }` | `_walletManager.GetAsync(walletId, azoaNull)` |
| `WalletQuery` | `WalletQueryRequest` | `_walletManager.QueryAsync(query, azoaNull)` |
| `WalletSetDefault` | `{ walletId }` | `_walletManager.SetDefaultAsync(userId, walletId, azoaNull)` |
| `WalletGetPortfolio` | `{ walletId }` | `_walletManager.GetPortfolioAsync(walletId, azoaNull)` |
| `ActionGraphBundleGenerate` | `{ actionGraphBundleId, ActionGraphBundleDappGenerationRequest }` | `_bundleManager.GenerateAsync(actionGraphBundleId, request, azoaNull)` |
| `ActionGraphBundleDeploy` | `{ actionGraphBundleId }` | `_bundleManager.DeployAsync(actionGraphBundleId, azoaNull)` |
| `Search` | `SearchRequest` | `_searchManager.SearchAsync(query, userId, azoaNull)` |
| `UserNFTGetComposite` | `{ userId }` | `_userNFTService.GetUserNFTCompositeAsync(userId)` |
| `BlockchainExecute` | `BlockchainOperation` (built from config) | `_blockchainManager.ExecuteAsync(operation, azoaNull)` |
| `Condition` | `{ expression, inputs }` | Evaluate expression against accumulated node outputs |
| `ComposeOutputs` | `{ outputFields }` | Merge upstream outputs into single JSON |

## Manager: `IActionManager`

```csharp
// CRUD
Task<AZOAResult<ActionDefinition>> CreateAsync(Guid userId, ActionCreateModel model);
Task<AZOAResult<ActionDefinition>> GetAsync(Guid actionId, Guid userId);
Task<AZOAResult<IEnumerable<ActionDefinition>>> ListAsync(Guid userId, ActionStatus? status = null, Guid? dappSeriesId = null);
Task<AZOAResult<ActionDefinition>> UpdateAsync(Guid actionId, Guid userId, ActionUpdateModel model);
Task<AZOAResult<bool>> DeleteAsync(Guid actionId, Guid userId);

// Nodes
Task<AZOAResult<ActionNode>> AddNodeAsync(Guid actionId, Guid userId, ActionNodeCreateModel model);
Task<AZOAResult<ActionNode>> UpdateNodeAsync(Guid actionId, Guid nodeId, Guid userId, ActionNodeUpdateModel model);
Task<AZOAResult<bool>> DeleteNodeAsync(Guid actionId, Guid nodeId, Guid userId);

// Edges
Task<AZOAResult<ActionEdge>> AddEdgeAsync(Guid actionId, Guid userId, ActionEdgeCreateModel model);
Task<AZOAResult<bool>> RemoveEdgeAsync(Guid actionId, Guid edgeId, Guid userId);
Task<AZOAResult<int[]>> GetTopologicalOrderAsync(Guid actionId, Guid userId);

// Dependencies
Task<AZOAResult<ActionDependency>> AddDependencyAsync(Guid actionId, Guid userId, ActionDependencyCreateModel model);
Task<AZOAResult<bool>> RemoveDependencyAsync(Guid actionId, Guid depId, Guid userId);
Task<AZOAResult<DependencyCheckResult>> CheckDependenciesAsync(Guid actionId, Guid userId);

// Execution
Task<AZOAResult<ActionDefinition>> ActivateAsync(Guid actionId, Guid userId);
Task<AZOAResult<ActionDefinition>> CompleteAsync(Guid actionId, Guid userId, string? output = null);
Task<AZOAResult<ActionDefinition>> FailAsync(Guid actionId, Guid userId, string error);
Task<AZOAResult<ActionExecutionState>> GetExecutionStateAsync(Guid actionId, Guid userId);
Task<AZOAResult<ActionDefinition>> ExecuteNextAsync(Guid actionId, Guid userId);
Task<AZOAResult<ActionDefinition>> ExecuteNodeAsync(Guid actionId, Guid nodeId, Guid userId);

// Templates
Task<AZOAResult<ActionDefinition>> InstantiateFromTemplateAsync(Guid userId, ActionInstantiateModel model);
Task<AZOAResult<ActionNodeTemplate>> CreateNodeTemplateAsync(Guid userId, ActionNodeTemplateCreateModel model);
Task<AZOAResult<IEnumerable<ActionNodeTemplate>>> ListNodeTemplatesAsync(bool publicOnly = true);
Task<AZOAResult<ActionTemplate>> CreateActionTemplateAsync(Guid userId, ActionTemplateCreateModel model);
Task<AZOAResult<IEnumerable<ActionTemplate>>> ListActionTemplatesAsync(bool publicOnly = true);
```

All methods return `AZOAResult<T>`, matching the existing manager pattern.

## Controller Pattern

```csharp
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ActionController : ControllerBase
{
    private readonly IActionManager _manager;

    [HttpGet]
    public async Task<ActionResult<AZOAResult<IEnumerable<ActionDefinition>>>> List(
        [FromQuery] ActionStatus? status = null, [FromQuery] Guid? dappSeriesId = null)
    {
        var userId = GetUserIdFromClaims();
        if (userId == null)
            return Unauthorized(new AZOAResult<IEnumerable<ActionDefinition>> { IsError = true, Message = "Invalid token." });

        var result = await _manager.ListAsync(userId.Value, status, dappSeriesId);
        return Ok(result);
    }

    private Guid? GetUserIdFromClaims()
    {
        var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return sub != null ? Guid.Parse(sub) : null;
    }
}
```

## Acceptance Criteria
- [ ] All endpoints return `AZOAResult<T>` or `AZOAResponse`
- [ ] `[Authorize]` on all controllers; `GetUserIdFromClaims()` using `ClaimTypes.NameIdentifier` / `"sub"`
- [ ] User-scoped access control
- [ ] DAG validation on edge add, action activate
- [ ] Dependency check on action activate
- [ ] Node dispatch table maps every `ActionNodeType` to correct existing manager method
- [ ] Config JSON validated against expected request model type per node type
- [ ] Topological order cached, invalidated on DAG mutation
- [ ] Swagger UI lists all action endpoints
- [ ] Builds cleanly with `dotnet build`
