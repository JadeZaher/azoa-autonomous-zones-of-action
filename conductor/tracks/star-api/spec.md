# STAR API — Specification

## Goal
Expose the STAR dapp-generator API. STAR is not just an ODK key manager; it scaffolds, configures, and orchestrates dapps that operate on the OASIS holon universe. Generated dapps can query holons, trigger cross-chain interactions, and manage assets on behalf of an avatar.

## Endpoints

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/star/{id}` | Get STAR dapp config / ODK by id |
| GET | `/api/star` | List all STAR dapps / ODK records |
| POST | `/api/star` | Create / update a STAR dapp or ODK record |
| DELETE | `/api/star/{id}` | Delete a STAR dapp / ODK record |
| POST | `/api/star/{id}/generate` | Scaffold a new dapp from this STAR template |
| POST | `/api/star/{id}/deploy` | Deploy the generated dapp to target chain/provider |

## Authentication
- All endpoints require JWT Bearer auth

## Models
- `STARDapp` / `STARODK` implements `ISTARODK`
- `STARODKCreateModel` / `STARDappCreateModel` for POST body
- `STARDappGenerationRequest` — params for dapp scaffolding (target chain, holons to bind, etc.)

## Provider Overrides
- Same pattern as Avatar API: optional `OASISRequest` switches provider via `ProviderContext`

## Dapp Generation
- STAR templates define holon-interaction graphs (which holons to query, which operations to expose)
- Generated dapps receive a JWT-like session bound to the avatar's identity
- Deployment targets a specific chain/provider via `ProviderContext`

## Acceptance Criteria
- [ ] All 6 endpoints return `OASISResult<T>` or `OASISResponse`
- [ ] Only authenticated users can access
- [ ] Provider switching works via `OASISRequest` in body
- [ ] Dapp generation endpoint produces a runnable/configured dapp artifact
- [ ] Deploy endpoint can push dapp config to at least one chain provider
