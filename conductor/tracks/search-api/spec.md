# Search API — Specification

## Goal
Provide a unified, cross-entity search endpoint that queries Avatars, Holons, Wallets, BlockchainOperations, and STARODKs through a single interface with pagination, filtering, and faceted results.

## Motivation
As the OASIS ecosystem grows, consumers need to discover assets across entity boundaries:
- "Find all NFTs (Holons) owned by avatar X"
- "Find wallets on Solana chain for avatar Y"
- "Find blockchain operations of type 'Mint' in the last 24h"
- Full-text search across Names, Descriptions, and Metadata

Rather than exposing N separate query endpoints, a single `SearchManager` provides a holistic discovery layer.

## Architecture
```
SearchController → ISearchManager → ProviderContext
                                 → IOASISStorageProvider (queries each entity set)
                                 → In-memory aggregation, filtering, pagination
```

Because `IOASISStorageProvider` does not expose a native full-text search, the `SearchManager` will:
1. Load relevant entity collections from the active provider
2. Apply in-memory filtering (case-insensitive contains on Name/Description/Metadata)
3. Apply entity-type filters
4. Sort and paginate
5. Return faceted counts

> **Future enhancement**: When a provider (e.g., MongoDB, Elastic-backed provider) is added, `ISearchManager` can delegate to `provider.SearchAsync` if available.

### New / Modified Files
| Layer | File | Action |
|---|---|---|
| Interface | `Interfaces/Managers/ISearchManager.cs` | New |
| Manager | `Managers/SearchManager.cs` | New |
| Controller | `Controllers/SearchController.cs` | New |
| Models | `Models/Requests/SearchRequest.cs` | New |
| Models | `Models/Responses/SearchResult.cs` | New |
| Models | `Models/Responses/SearchFacet.cs` | New |
| Core | `Core/SearchableEntityType.cs` | New (Flags enum) |

### SearchableEntityType
```csharp
[Flags]
public enum SearchableEntityType
{
    None = 0,
    Avatar = 1,
    Holon = 2,
    Wallet = 4,
    BlockchainOperation = 8,
    STARODK = 16,
    All = Avatar | Holon | Wallet | BlockchainOperation | STARODK
}
```

### ISearchManager Contract
```csharp
public interface ISearchManager
{
    Task<OASISResult<SearchResult>> SearchAsync(SearchRequest request, OASISRequest? providerRequest = null);
    Task<OASISResult<List<SearchFacet>>> GetFacetsAsync(OASISRequest? providerRequest = null);
}
```

### SearchController Endpoints
| Method | Route | Auth | Description |
|---|---|---|---|
| POST | `/api/search` | Authorize | Execute cross-entity search |
| GET | `/api/search/facets` | Authorize | Get facet counts per entity type |

### Request Models
**SearchRequest**
```csharp
public class SearchRequest
{
    public string Query { get; set; } = string.Empty;           // Full-text search term
    public SearchableEntityType EntityTypes { get; set; } = SearchableEntityType.All;
    public string? ChainId { get; set; }                        // Filter wallets/Holons by chain
    public string? AssetType { get; set; }                      // Filter Holons by asset type (e.g. "NFT")
    public Guid? AvatarId { get; set; }                         // Scope to a specific avatar
    public DateTime? CreatedAfter { get; set; }
    public DateTime? CreatedBefore { get; set; }
    public string SortBy { get; set; } = "CreatedDate";         // CreatedDate, Name, Relevance
    public bool SortDescending { get; set; } = true;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
```

### Response Models
**SearchResult**
```csharp
public class SearchResult
{
    public string Query { get; set; } = string.Empty;
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / Math.Max(PageSize, 1));
    public List<SearchHit> Hits { get; set; } = new();
    public List<SearchFacet> Facets { get; set; } = new();
}
```

**SearchHit** (polymorphic result item)
```csharp
public class SearchHit
{
    public Guid Id { get; set; }
    public SearchableEntityType EntityType { get; set; }
    public string Title { get; set; } = string.Empty;           // Name / Username / Address / OperationType
    public string Description { get; set; } = string.Empty;
    public string? Highlight { get; set; }                      // Snippet where match occurred
    public Dictionary<string, object> Fields { get; set; } = new(); // Extra entity-specific fields
    public DateTime CreatedDate { get; set; }
}
```

**SearchFacet**
```csharp
public class SearchFacet
{
    public SearchableEntityType EntityType { get; set; }
    public int Count { get; set; }
    public string Label { get; set; } = string.Empty;
}
```

## Search Algorithm (v1 — in-memory)
1. **Activate provider** via `ProviderContext`
2. **Determine entity sets to load** from `EntityTypes` flag
3. **Load data**:
   - Avatars: `LoadAllAvatarsAsync`
   - Holons: `LoadAllHolonsAsync`
   - Wallets: `LoadWalletsByAvatarAsync` (if AvatarId provided) or all wallets (requires provider extension)
   - BlockchainOperations: `LoadBlockchainOperationsByAvatarAsync` (if AvatarId provided) or all ops (requires provider extension)
   - STARODKs: `LoadAllSTARODKsAsync`
4. **Filter by query** (case-insensitive contains on Name, Description, Username, Email, Address, Metadata values)
5. **Apply structured filters**: ChainId, AssetType, AvatarId, date range
6. **Sort** by `SortBy` field
7. **Paginate** using Skip/Take
8. **Build facets** from total counts per entity type before pagination
9. **Map** to `SearchHit` with `Highlight` set to the matching field value

## Provider Extensions
To support search without loading *everything* into memory, add these methods to `IOASISStorageProvider`:
```csharp
Task<OASISResult<IEnumerable<IWallet>>> LoadAllWalletsAsync(CancellationToken ct = default);
Task<OASISResult<IEnumerable<IBlockchainOperation>>> LoadAllBlockchainOperationsAsync(CancellationToken ct = default);
```
> These are optional for v1; if not implemented, the SearchManager can skip those entity types or load via existing avatar-scoped methods when AvatarId is provided.

## Business Rules
1. **Empty query**: Returns all entities matching structured filters (e.g. all Holons with AssetType="NFT").
2. **AvatarId scope**: When provided, only load avatar-scoped entities (Wallets, BlockchainOperations) for that avatar. Cross-avatar search is admin-only (future track).
3. **Pagination limits**: `PageSize` clamped between 1 and 100.
4. **Relevance sorting**: v1 uses `CreatedDate` as proxy for relevance. Future v2 can implement scoring.
5. **Highlight**: v1 returns the first matching field value as Highlight string.

## Acceptance Criteria
- [ ] `ISearchManager` defined and implemented
- [ ] `SearchController` exposes `/api/search` and `/api/search/facets`
- [ ] Search returns hits from multiple entity types in one call
- [ ] Pagination works correctly (TotalCount, TotalPages, Page, PageSize)
- [ ] Facets return accurate counts per entity type
- [ ] Query filtering is case-insensitive and searches Name, Description, Metadata values
- [ ] Structured filters (ChainId, AssetType, AvatarId, date range) work
- [ ] PageSize clamped to [1, 100]
- [ ] Unit + integration tests cover search, filters, pagination, facets
- [ ] Stryker mutation score for new code ≥ 50 %
