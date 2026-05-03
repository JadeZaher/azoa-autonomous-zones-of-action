# Holon API — Specification

## Goal
Expose a unified Holon API that allows users to create, read, update, delete, and query holons across all registered storage providers. Holons are the primary data units in OASIS; NFTs function as on-chain storage-backed holons.

## Endpoints

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/holon/{id}` | Get holon by id |
| GET | `/api/holon` | Query holons (filter by provider, chain, asset type, owner/avatar) |
| POST | `/api/holon` | Create / update a holon |
| DELETE | `/api/holon/{id}` | Delete a holon |
| POST | `/api/holon/{id}/interact` | Orchestrate an interaction between this holon and others |

## Authentication
- All endpoints require JWT Bearer auth
- Holons are scoped to the authenticated avatar (owner-based access control)

## Models
- `Holon` — core holon model with metadata, provider refs, chain refs, asset type
- `HolonCreateModel` / `HolonUpdateModel` — request DTOs
- `HolonQueryRequest` — filters for cross-universe search
- `HolonInteractionRequest` — payload for orchestrating multi-holon operations

## Provider Overrides
- Same pattern as Avatar API: optional `OASISRequest` switches provider via `ProviderContext`
- Providers must implement `IHolonStorageProvider` (extends `IOASISStorageProvider`)

## NFT-as-Storage
- NFT-backed providers map token metadata + on-chain state to `Holon` instances
- Read operations return cached/off-chain enriched holon data
- Write operations update on-chain state via the provider adapter

## Cross-Universe Query
- `GET /api/holon` supports querying across all active providers simultaneously
- Results are aggregated, deduplicated, and returned as a unified holon list
- Optional `provider` filter restricts to a single provider/chain

## Acceptance Criteria
- [ ] All 5 endpoints return `OASISResult<T>` or `OASISResponse`
- [ ] Only authenticated users can access; holons scoped to avatar
- [ ] Provider switching works via `OASISRequest` in body
- [ ] NFT-as-storage provider can map token data to holon model
- [ ] Cross-provider query aggregates results from all active providers
