---
type: Track Spec
title: "Dapp Composition — Specification"
tags: [dapp-composition, archived]
---

# Dapp Composition — Specification

## Goal
Compose a **series of Actions** into a **dApp contract** — a deployable application unit. A dApp bundles ordered actions (each a DAG) with shared context, cross-action data flow, and deployment targets. The complete series produces an `ActionGraphBundleDappGenerationRequest` that delegates to `IActionGraphBundleManager.GenerateAsync` and `IActionGraphBundleManager.DeployAsync` for actual generation and deployment.

## Architecture

### Action Series → dApp Pipeline
```
┌──────────────┐    ┌──────────────┐    ┌──────────────┐
│ Action DAG 1 │───▶│ Action DAG 2 │───▶│ Action DAG N │
│ (Entry/init) │    │ (depends on 1)│   │ (Terminal)   │
└──────────────┘    └──────────────┘    └──────────────┘
         │                   │                   │
         ▼                   ▼                   ▼
    ┌─────────────────────────────────────────────────┐
    │              DappCompositionManager              │
    │  1. Compose: validate series, build manifest     │
    │  2. Generate: create ActionGraphBundle, call IActionGraphBundleManager  │
    │  3. Deploy: call IActionGraphBundleManager.DeployAsync        │
    └─────────────────────────────────────────────────┘
```

### Key Insight
A dApp is **not** a single monolithic DAG. It's a *linked series of action DAGs* where:
- Each action is independently valid (acyclic, entry + terminal)
- Actions declare dependencies on prior actions in the series
- Outputs from one action's terminal nodes flow into the next action's entry nodes
- The complete series produces an `ActionGraphBundleDappGenerationRequest` for Action Graph Bundle generation

## Domain Models

### DappSeries
A collection of actions that together form a dApp.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Unique series ID |
| `Name` | `string` | dApp name |
| `Description` | `string?` | dApp description |
| `UserId` | `Guid` | Owner user |
| `Status` | `DappSeriesStatus` | Draft / Building / Ready / Deployed / Archived |
| `Actions` | `List<DappSeriesAction>` | Ordered action entries |
| `SharedConfig` | `Dictionary<string, string>` | Shared config across all actions (chain, provider settings) |
| `ActionGraphBundleId` | `Guid?` | Linked ActionGraphBundle.Id (populated after generation) |
| `TargetChain` | `string?` | Deployment target (e.g., "algorand-mainnet") |
| `Manifest` | `string?` (JSON) | Generated manifest (post-composition) |
| `CreatedDate` | `DateTime` | Creation timestamp |
| `DeployedDate` | `DateTime?` | Deployment timestamp |

### DappSeriesStatus (enum)
`Draft` → `Building` → `Ready` → `Deployed` → `Archived`

### DappSeriesAction
An entry linking an action to a dApp series, with ordering and context flow.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Unique entry ID |
| `DappSeriesId` | `Guid` | Parent series |
| `ActionId` | `Guid` | The action |
| `Order` | `int` | Execution order (1-indexed) |
| `InputMappings` | `string?` (JSON) | How outputs from prior actions map to this action's entry node inputs |

### InputMapping
Defines how data flows between actions in a series. Stored as JSON array:

```json
[
  {
    "sourceActionId": "guid-of-action-1",
    "sourceNodeId": "guid-of-terminal-node-in-action-1",
    "targetActionId": "guid-of-action-2",
    "targetNodeId": "guid-of-entry-node-in-action-2",
    "fieldMap": { "holonId": "boundHolonId", "nftContract": "targetContract" }
  }
]
```

### DappManifest
The composed artifact used to build an `ActionGraphBundleDappGenerationRequest`.

| Field | Type | Description |
|-------|------|-------------|
| `DappSeriesId` | `Guid` | Parent series |
| `Version` | `string` | Manifest version |
| `BoundHolonIds` | `List<Guid>` | All holons referenced across actions (deduplicated) |
| `ActionGraph` | `string` (JSON) | Full dependency graph of all actions |
| `TargetChain` | `string` | Deployment target chain |
| `Config` | `Dictionary<string, string>` | Combined shared config + per-action overrides |
| `GeneratedDate` | `DateTime` | Generation timestamp |

## Manager: `IDappCompositionManager`

```csharp
// Series CRUD
Task<AZOAResult<DappSeries>> CreateAsync(Guid userId, string name, string? description = null);
Task<AZOAResult<DappSeries>> GetAsync(Guid seriesId, Guid userId);
Task<AZOAResult<IEnumerable<DappSeries>>> ListAsync(Guid userId, DappSeriesStatus? status = null);
Task<AZOAResult<DappSeries>> UpdateAsync(Guid seriesId, Guid userId, DappSeriesUpdateModel model);
Task<AZOAResult<bool>> DeleteAsync(Guid seriesId, Guid userId);

// Action Management within Series
Task<AZOAResult<DappSeriesAction>> AddActionAsync(Guid seriesId, Guid userId, Guid actionId, int order, string? inputMappings = null);
Task<AZOAResult<DappSeriesAction>> RemoveActionAsync(Guid seriesId, Guid userId, Guid actionId);
Task<AZOAResult<DappSeriesAction>> ReorderActionAsync(Guid seriesId, Guid userId, Guid actionId, int newOrder);
Task<AZOAResult<IEnumerable<DappSeriesAction>>> ListActionsAsync(Guid seriesId, Guid userId);

// Composition
Task<AZOAResult<DappManifest>> ComposeAsync(Guid seriesId, Guid userId);
// Validates: all actions in series are Completed, dependency chain intact, holon bindings resolved
// Produces: DappManifest with BoundHolonIds, ActionGraph, TargetChain, Config

// Generation & Deployment (delegates to IActionGraphBundleManager)
Task<AZOAResult<IActionGraphBundle>> GenerateAsync(Guid seriesId, Guid userId);
// 1. Calls ComposeAsync to get DappManifest
// 2. Creates an ActionGraphBundle record with BoundHolonIds, TargetChain
// 3. Builds ActionGraphBundleDappGenerationRequest { TargetChain, BoundHolonIds, Config }
// 4. Calls _bundleManager.GenerateAsync(actionGraphBundle.Id, request)
// 5. Stores ActionGraphBundleId on DappSeries, status → Ready

Task<AZOAResult<IActionGraphBundle>> DeployAsync(Guid seriesId, Guid userId, string? targetOverride = null);
// 1. Verifies series is Ready and has an ActionGraphBundleId
// 2. Calls _bundleManager.DeployAsync(actionGraphBundleId, azoaRequest)
// 3. Updates status → Deployed, sets DeployedDate
```

## Endpoints

### Dapp Series CRUD

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/dapp-series` | List series for authenticated user |
| `GET` | `/api/dapp-series/{id}` | Get series detail (with action list) |
| `POST` | `/api/dapp-series` | Create series (Draft) |
| `PUT` | `/api/dapp-series/{id}` | Update metadata |
| `DELETE` | `/api/dapp-series/{id}` | Delete (Draft only) |

### Series Action Management

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/dapp-series/{seriesId}/actions` | List actions (ordered) |
| `POST` | `/api/dapp-series/{seriesId}/actions` | Add action to series |
| `DELETE` | `/api/dapp-series/{seriesId}/actions/{actionId}` | Remove action |
| `PUT` | `/api/dapp-series/{seriesId}/actions/{actionId}/order` | Reorder action |
| `PUT` | `/api/dapp-series/{seriesId}/actions/{actionId}/mappings` | Update input mappings |

### Composition & Generation

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/dapp-series/{id}/compose` | Compose series into manifest |
| `GET` | `/api/dapp-series/{id}/manifest` | Get current manifest |
| `POST` | `/api/dapp-series/{id}/generate` | Generate via Action Graph Bundle manager |
| `POST` | `/api/dapp-series/{id}/deploy` | Deploy via Action Graph Bundle manager |
| `GET` | `/api/dapp-series/{id}/status` | Get deployment status |

## Composition Validation Rules
1. **All Actions Completed**: Every action in the series must have `Status = Completed`.
2. **Chain Completeness**: No isolated actions — each action (after the first) must have at least one dependency on a prior action in the series.
3. **Input Mapping Consistency**: Entry nodes in action N (N > 1) should have mapped inputs from terminal nodes in prior actions.
4. **No Circular Dependencies**: Action A cannot depend on Action B if Action B appears later in the series order.
5. **Holon Bindings Resolved**: All holon IDs referenced in action node configs must exist.

## Action Graph Bundle Integration Flow

`GenerateAsync` builds an `ActionGraphBundleDappGenerationRequest` from the composed manifest:

```csharp
var request = new ActionGraphBundleDappGenerationRequest
{
    TargetChain = manifest.TargetChain,
    BoundHolonIds = manifest.BoundHolonIds,
    Config = manifest.Config
};

var bundleResult = await _bundleManager.CreateOrUpdateAsync(new ActionGraphBundleCreateModel
{
    Name = series.Name,
    Description = series.Description,
    UserId = userId,
    TargetChain = manifest.TargetChain,
    BoundHolonIds = manifest.BoundHolonIds
});

var generated = await _bundleManager.GenerateAsync(bundleResult.Result.Id, request);
series.ActionGraphBundleId = generated.Result.Id;
series.Status = DappSeriesStatus.Ready;
```

`DeployAsync` delegates to `IActionGraphBundleManager.DeployAsync`:

```csharp
var deployed = await _bundleManager.DeployAsync(series.ActionGraphBundleId.Value, azoaRequest);
series.Status = DappSeriesStatus.Deployed;
series.DeployedDate = DateTime.UtcNow;
```

## Acceptance Criteria
- [x] All endpoints return `AZOAResult<T>` or `AZOAResponse`
- [x] `[Authorize]` on all controllers; user-scoped access
- [x] Composition validates all rules before producing manifest
- [x] `GenerateAsync` correctly builds `ActionGraphBundleDappGenerationRequest` and delegates to `IActionGraphBundleManager`
- [x] `DeployAsync` correctly delegates to `IActionGraphBundleManager.DeployAsync`
- [x] Input mappings enable data flow between actions
- [x] Series status machine enforced (Draft → Building → Ready → Deployed)
- [x] `DateTime` used throughout (not `DateTimeOffset`)
- [x] Swagger UI documents all dApp composition endpoints — verified by `SwaggerJson_ShouldListAllDappCompositionEndpoints` integration test (asserts all 12 route paths present in `/swagger/v1/swagger.json`)
- [x] Builds cleanly with `dotnet build` — 0 warnings / 0 errors on both `AZOA.WebAPI.csproj` and the integration test project

## Phase-G verification (closeout)
- Unit tests: **567 / 567 passing** (`AZOA.WebAPI.Tests`, includes 12 manager tests in `DappCompositionManagerTests`).
- Integration tests: **18 / 18 passing** (`DappSeriesControllerIntegrationTests` — series CRUD, action management, compose/validate/manifest/generate/deploy/status, auth probes, Swagger smoke).
- Two test-harness fixes applied during closeout (pre-existing baseline issues, not dapp-composition bugs):
  1. `AZOATestWebApplicationFactory` + `McpAuthScopingIntegrationTests`: env name changed from `"Testing"` → `"IntegrationTest"` to match the boot-probe skip gate in `Program.cs:549`.
  2. `Program.cs:527`: Swagger middleware now mounts in `IntegrationTest` env too (not just `Development`), so the smoke test can hit `/swagger/v1/swagger.json` against the test host.
