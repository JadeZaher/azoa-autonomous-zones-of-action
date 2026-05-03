# Holon API — Implementation Plan

## Holonic Architecture Design Decisions

The holon primitive is modeled as a **self-similar, autonomous agent**:
- **Self-Similarity**: `Holon` contains `SubHolons` (a holarchy). Each child is itself a complete holon.
- **Autonomy**: Each holon carries its own `Metadata`, `ProviderName`, and `AssetType`, controlling its own state independent of peers.
- **Cooperation**: `PeerHolonIds` and `ParentHolonId` enable holons to form cooperative networks without central orchestration.
- **Physical + Logical**: `ChainId`/`TokenId`/`AssetType` are the physical (on-chain) aspect; `Name`/`Description`/`Metadata` are the logical aspect.
- **Resilience**: Cross-universe query aggregates from all active providers; if one fails, others continue serving.

## Tasks

1. `[x]` Design `IHolon` interface — holon primitive contract
2. `[x]` Create `Holon` model — EF-mapped entity with self-referencing relationship
3. `[x]` Create request/response DTOs (`HolonCreateModel`, `HolonUpdateModel`, `HolonQueryRequest`, `HolonInteractionRequest`)
4. `[x]` Extend `IOASISStorageProvider` with holon CRUD methods
5. `[x]` Implement holon storage in `InMemoryStorageProvider`
6. `[x]` Implement holon storage in `EfStorageProvider` + update `OASISDbContext`
7. `[x]` Create `HolonController` with 5+ endpoints (GET, GET query, POST, DELETE, POST interact, POST mint, POST exchange)
8. `[x]` Cross-universe query aggregation across all active providers
9. `[x]` Owner-based access control (JWT avatar scoping)
10. `[x]` `dotnet build` zero warnings, Swagger lists endpoints
