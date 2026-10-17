---
type: Track Spec
title: "Action Graph Bundle API — Specification"
tags: [action-graph-bundle-api, archived]
---

# Action Graph Bundle API — Specification

## Goal
Expose the Action Graph Bundle dapp-generator API. An action graph bundle is more than a key manager: it scaffolds, configures, and orchestrates dapps that operate on the AZOA holon universe. Generated dapps can query holons, trigger cross-chain interactions, and manage assets on behalf of a user.

## Endpoints

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/actiongraphbundle/{id}` | Get Action Graph Bundle dapp config / bundle by id |
| GET | `/api/actiongraphbundle` | List all Action Graph Bundle dapps / bundle records |
| POST | `/api/actiongraphbundle` | Create / update an Action Graph Bundle dapp or bundle record |
| DELETE | `/api/actiongraphbundle/{id}` | Delete an Action Graph Bundle dapp / bundle record |
| POST | `/api/actiongraphbundle/{id}/generate` | Scaffold a new dapp from this Action Graph Bundle template |
| POST | `/api/actiongraphbundle/{id}/deploy` | Deploy the generated dapp to target chain/provider |

## Authentication
- All endpoints require JWT Bearer auth

## Models
- `ActionGraphBundleDapp` / `ActionGraphBundle` implements `IActionGraphBundle`
- `ActionGraphBundleCreateModel` / `ActionGraphBundleDappCreateModel` for POST body
- `ActionGraphBundleDappGenerationRequest` — params for dapp scaffolding (target chain, holons to bind, etc.)

## Provider Overrides
- Same pattern as User API: optional `AZOARequest` switches provider via `ProviderContext`

## Dapp Generation
- Action Graph Bundle templates define holon-interaction graphs (which holons to query, which operations to expose)
- Generated dapps receive a JWT-like session bound to the user's identity
- Deployment targets a specific chain/provider via `ProviderContext`

## Acceptance Criteria
- [ ] All 6 endpoints return `AZOAResult<T>` or `AZOAResponse`
- [ ] Only authenticated users can access
- [ ] Provider switching works via `AZOARequest` in body
- [ ] Dapp generation endpoint produces a runnable/configured dapp artifact
- [ ] Deploy endpoint can push dapp config to at least one chain provider
