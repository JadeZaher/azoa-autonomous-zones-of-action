# Dapp Composition — Specification

## Goal
Compose a **series of Quests** into a **dApp contract** — a deployable application unit. A dApp bundles ordered quests (each a DAG) with shared context, cross-quest data flow, and deployment targets. The complete series produces a `STARDappGenerationRequest` that delegates to `ISTARManager.GenerateAsync` and `ISTARManager.DeployAsync` for actual generation and deployment.

## Architecture

### Quest Series → dApp Pipeline
```
┌──────────────┐    ┌──────────────┐    ┌──────────────┐
│ Quest DAG 1  │───▶│ Quest DAG 2  │───▶│ Quest DAG N  │
│ (Entry/init) │    │ (depends on 1)│   │ (Terminal)   │
└──────────────┘    └──────────────┘    └──────────────┘
         │                   │                   │
         ▼                   ▼                   ▼
    ┌─────────────────────────────────────────────────┐
    │              DappCompositionManager              │
    │  1. Compose: validate series, build manifest     │
    │  2. Generate: create STARODK, call ISTARManager  │
    │  3. Deploy: call ISTARManager.DeployAsync        │
    └─────────────────────────────────────────────────┘
```

### Key Insight
A dApp is **not** a single monolithic DAG. It's a *linked series of quest DAGs* where:
- Each quest is independently valid (acyclic, entry + terminal)
- Quests declare dependencies on prior quests in the series
- Outputs from one quest's terminal nodes flow into the next quest's entry nodes
- The complete series produces an `STARDappGenerationRequest` for STAR generation

## Domain Models

### DappSeries
A collection of quests that together form a dApp.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Unique series ID |
| `Name` | `string` | dApp name |
| `Description` | `string?` | dApp description |
| `AvatarId` | `Guid` | Owner avatar |
| `Status` | `DappSeriesStatus` | Draft / Building / Ready / Deployed / Archived |
| `Quests` | `List<DappSeriesQuest>` | Ordered quest entries |
| `SharedConfig` | `Dictionary<string, string>` | Shared config across all quests (chain, provider settings) |
| `StarOdkId` | `Guid?` | Linked STARODK.Id (populated after generation) |
| `TargetChain` | `string?` | Deployment target (e.g., "algorand-mainnet") |
| `Manifest` | `string?` (JSON) | Generated manifest (post-composition) |
| `CreatedDate` | `DateTime` | Creation timestamp |
| `DeployedDate` | `DateTime?` | Deployment timestamp |

### DappSeriesStatus (enum)
`Draft` → `Building` → `Ready` → `Deployed` → `Archived`

### DappSeriesQuest
An entry linking a quest to a dApp series, with ordering and context flow.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Unique entry ID |
| `DappSeriesId` | `Guid` | Parent series |
| `QuestId` | `Guid` | The quest |
| `Order` | `int` | Execution order (1-indexed) |
| `InputMappings` | `string?` (JSON) | How outputs from prior quests map to this quest's entry node inputs |

### InputMapping
Defines how data flows between quests in a series. Stored as JSON array:

```json
[
  {
    "sourceQuestId": "guid-of-quest-1",
    "sourceNodeId": "guid-of-terminal-node-in-quest-1",
    "targetQuestId": "guid-of-quest-2",
    "targetNodeId": "guid-of-entry-node-in-quest-2",
    "fieldMap": { "holonId": "boundHolonId", "nftContract": "targetContract" }
  }
]
```

### DappManifest
The composed artifact used to build an `STARDappGenerationRequest`.

| Field | Type | Description |
|-------|------|-------------|
| `DappSeriesId` | `Guid` | Parent series |
| `Version` | `string` | Manifest version |
| `BoundHolonIds` | `List<Guid>` | All holons referenced across quests (deduplicated) |
| `QuestGraph` | `string` (JSON) | Full dependency graph of all quests |
| `TargetChain` | `string` | Deployment target chain |
| `Config` | `Dictionary<string, string>` | Combined shared config + per-quest overrides |
| `GeneratedDate` | `DateTime` | Generation timestamp |

## Manager: `IDappCompositionManager`

```csharp
// Series CRUD
Task<OASISResult<DappSeries>> CreateAsync(Guid avatarId, string name, string? description = null);
Task<OASISResult<DappSeries>> GetAsync(Guid seriesId, Guid avatarId);
Task<OASISResult<IEnumerable<DappSeries>>> ListAsync(Guid avatarId, DappSeriesStatus? status = null);
Task<OASISResult<DappSeries>> UpdateAsync(Guid seriesId, Guid avatarId, DappSeriesUpdateModel model);
Task<OASISResult<bool>> DeleteAsync(Guid seriesId, Guid avatarId);

// Quest Management within Series
Task<OASISResult<DappSeriesQuest>> AddQuestAsync(Guid seriesId, Guid avatarId, Guid questId, int order, string? inputMappings = null);
Task<OASISResult<DappSeriesQuest>> RemoveQuestAsync(Guid seriesId, Guid avatarId, Guid questId);
Task<OASISResult<DappSeriesQuest>> ReorderQuestAsync(Guid seriesId, Guid avatarId, Guid questId, int newOrder);
Task<OASISResult<IEnumerable<DappSeriesQuest>>> ListQuestsAsync(Guid seriesId, Guid avatarId);

// Composition
Task<OASISResult<DappManifest>> ComposeAsync(Guid seriesId, Guid avatarId);
// Validates: all quests in series are Completed, dependency chain intact, holon bindings resolved
// Produces: DappManifest with BoundHolonIds, QuestGraph, TargetChain, Config

// Generation & Deployment (delegates to ISTARManager)
Task<OASISResult<ISTARODK>> GenerateAsync(Guid seriesId, Guid avatarId);
// 1. Calls ComposeAsync to get DappManifest
// 2. Creates a STARODK record with BoundHolonIds, TargetChain
// 3. Builds STARDappGenerationRequest { TargetChain, BoundHolonIds, Config }
// 4. Calls _starManager.GenerateAsync(starOdk.Id, request)
// 5. Stores StarOdkId on DappSeries, status → Ready

Task<OASISResult<ISTARODK>> DeployAsync(Guid seriesId, Guid avatarId, string? targetOverride = null);
// 1. Verifies series is Ready and has a StarOdkId
// 2. Calls _starManager.DeployAsync(starOdkId, oasisRequest)
// 3. Updates status → Deployed, sets DeployedDate
```

## Endpoints

### Dapp Series CRUD

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/dapp-series` | List series for authenticated avatar |
| `GET` | `/api/dapp-series/{id}` | Get series detail (with quest list) |
| `POST` | `/api/dapp-series` | Create series (Draft) |
| `PUT` | `/api/dapp-series/{id}` | Update metadata |
| `DELETE` | `/api/dapp-series/{id}` | Delete (Draft only) |

### Series Quest Management

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/dapp-series/{seriesId}/quests` | List quests (ordered) |
| `POST` | `/api/dapp-series/{seriesId}/quests` | Add quest to series |
| `DELETE` | `/api/dapp-series/{seriesId}/quests/{questId}` | Remove quest |
| `PUT` | `/api/dapp-series/{seriesId}/quests/{questId}/order` | Reorder quest |
| `PUT` | `/api/dapp-series/{seriesId}/quests/{questId}/mappings` | Update input mappings |

### Composition & Generation

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/dapp-series/{id}/compose` | Compose series into manifest |
| `GET` | `/api/dapp-series/{id}/manifest` | Get current manifest |
| `POST` | `/api/dapp-series/{id}/generate` | Generate via STAR manager |
| `POST` | `/api/dapp-series/{id}/deploy` | Deploy via STAR manager |
| `GET` | `/api/dapp-series/{id}/status` | Get deployment status |

## Composition Validation Rules
1. **All Quests Completed**: Every quest in the series must have `Status = Completed`.
2. **Chain Completeness**: No isolated quests — each quest (after the first) must have at least one dependency on a prior quest in the series.
3. **Input Mapping Consistency**: Entry nodes in quest N (N > 1) should have mapped inputs from terminal nodes in prior quests.
4. **No Circular Dependencies**: Quest A cannot depend on Quest B if Quest B appears later in the series order.
5. **Holon Bindings Resolved**: All holon IDs referenced in quest node configs must exist.

## STAR Integration Flow

`GenerateAsync` builds an `STARDappGenerationRequest` from the composed manifest:

```csharp
var request = new STARDappGenerationRequest
{
    TargetChain = manifest.TargetChain,
    BoundHolonIds = manifest.BoundHolonIds,
    Config = manifest.Config
};

var starResult = await _starManager.CreateOrUpdateAsync(new STARODKCreateModel
{
    Name = series.Name,
    Description = series.Description,
    AvatarId = avatarId,
    TargetChain = manifest.TargetChain,
    BoundHolonIds = manifest.BoundHolonIds
});

var generated = await _starManager.GenerateAsync(starResult.Result.Id, request);
series.StarOdkId = generated.Result.Id;
series.Status = DappSeriesStatus.Ready;
```

`DeployAsync` delegates to `ISTARManager.DeployAsync`:

```csharp
var deployed = await _starManager.DeployAsync(series.StarOdkId.Value, oasisRequest);
series.Status = DappSeriesStatus.Deployed;
series.DeployedDate = DateTime.UtcNow;
```

## Acceptance Criteria
- [ ] All endpoints return `OASISResult<T>` or `OASISResponse`
- [ ] `[Authorize]` on all controllers; avatar-scoped access
- [ ] Composition validates all rules before producing manifest
- [ ] `GenerateAsync` correctly builds `STARDappGenerationRequest` and delegates to `ISTARManager`
- [ ] `DeployAsync` correctly delegates to `ISTARManager.DeployAsync`
- [ ] Input mappings enable data flow between quests
- [ ] Series status machine enforced (Draft → Building → Ready → Deployed)
- [ ] `DateTime` used throughout (not `DateTimeOffset`)
- [ ] Swagger UI documents all dApp composition endpoints
- [ ] Builds cleanly with `dotnet build`
