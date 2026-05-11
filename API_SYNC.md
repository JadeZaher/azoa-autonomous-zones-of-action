# API Sync Guide — .NET Controllers ↔ SDK Client

This document maps every .NET controller endpoint to its SDK client method. When modifying either side, update the other to stay in sync.

## Sync Checklist

When you change a .NET controller:
1. Find the controller in the table below
2. Update the corresponding SDK method in `sdk/oasis-wallet/src/api/client.ts`
3. Update the TypeScript response/request types to match the .NET DTOs
4. Update the path constant in `sdk/oasis-wallet/src/api/api-version.ts`
5. Update or add tests in `sdk/oasis-wallet/tests/api/client.test.ts`
6. Run `npm run build && npm test` in `sdk/oasis-wallet/`

When you change an SDK client method:
1. Verify the .NET controller still accepts the request shape
2. Run the .NET integration tests: `dotnet test OASIS.WebAPI.IntegrationTests/`

## Controller → SDK Method Map

### AvatarController → `OasisApiClient` + `OasisAuthProvider`

| .NET Route | Method | SDK Method | Request DTO | Response DTO |
|------------|--------|------------|-------------|--------------|
| `POST api/avatar/register` | Register | `api.register(params)` / `auth.register(params)` | `AvatarRegisterModel` | `OASISResult<IAvatar>` → `AvatarResponse` |
| `POST api/avatar/login` | Login | `api.login(email, pw)` / `auth.login(email, pw)` | `AvatarLoginModel` | `OASISResult<string>` → JWT string |
| `GET api/avatar/{id}` | Get | `api.getAvatar(id)` / `auth.getProfile()` | — | `OASISResult<IAvatar>` → `AvatarResponse` |
| `GET api/avatar` | GetAll | `api.getAllAvatars()` | — | `OASISResult<IEnumerable<IAvatar>>` |
| `PUT api/avatar/{id}` | Update | `api.updateAvatar(id, params)` | `AvatarUpdateModel` | `OASISResult<IAvatar>` |
| `DELETE api/avatar/{id}` | Delete | `api.deleteAvatar(id)` | — | `OASISResponse` |

### HolonController → `HolonQueryBuilder`

| .NET Route | Method | SDK Method | Notes |
|------------|--------|------------|-------|
| `GET api/holon/{id}` | Get | `holons.get(id)` | |
| `GET api/holon` | Query | `holons.where({...}).execute()` | Query params match `HolonQueryRequest` |
| `POST api/holon` | Create | `holons.create(params)` | `HolonCreateModel` |
| `PUT api/holon/{id}` | Update | `holons.update(id, params)` | `HolonUpdateModel` |
| `DELETE api/holon/{id}` | Delete | `holons.delete(id)` | |
| `GET api/holon/{id}/children` | GetChildren | `holons.getChildren(id)` | |
| `GET api/holon/{id}/peers` | GetPeers | `holons.getPeers(id)` | |
| `GET api/holon/{id}/ancestors` | GetAncestors | `holons.getAncestors(id)` | |
| `GET api/holon/{id}/descendants` | GetDescendants | `holons.getDescendants(id)` | |
| `POST api/holon/{id}/mint` | Mint | `api.request("POST", path, body)` | Uses BlockchainOperationManager |
| `POST api/holon/{id}/exchange` | Exchange | `api.request("POST", path, body)` | Uses BlockchainOperationManager |
| `GET api/holon/{id}/compose` | Compose | `holons.getComposite(id)` | |

### WalletController → `OasisApiClient`

| .NET Route | Method | SDK Method | Request DTO |
|------------|--------|------------|-------------|
| `GET api/wallet/{id}` | Get | `api.request("GET", path)` | — |
| `GET api/wallet` | Query | `api.request("GET", path + qs)` | `WalletQueryRequest` as query params |
| `POST api/wallet` | Create | `api.request("POST", path, body)` | `WalletCreateModel` |
| `PUT api/wallet/{id}` | Update | `api.request("PUT", path, body)` | `WalletUpdateModel` |
| `DELETE api/wallet/{id}` | Delete | `api.request("DELETE", path)` | — |
| `POST api/wallet/{id}/set-default` | SetDefault | `api.request("POST", path)` | — |
| `GET api/wallet/{id}/portfolio` | Portfolio | `api.request("GET", path)` / `portfolio.getAll()` | — |

### NftController → `OasisApiClient`

| .NET Route | Method | SDK Method | Request DTO | Sync Notes |
|------------|--------|------------|-------------|------------|
| `GET api/nft/{id}` | Get | `api.getNft(id)` | — | Response: `NftResult` |
| `GET api/nft` | Query | `api.request("GET", path)` | `NftQueryRequest` | |
| `POST api/nft/mint` | Mint | `api.mintNft(params)` | `NftMintRequest` | **Fields: walletId (GUID), name, description, chainId** |
| `POST api/nft/{id}/transfer` | Transfer | `api.transferNft(id, params)` | `NftTransferRequest` | **Fields: targetAvatarId (GUID), walletId (GUID)** |
| `POST api/nft/{id}/burn` | Burn | `api.burnNft(id, params)` | `NftBurnRequest` | **Fields: walletId (GUID)** |
| `GET api/nft/{id}/metadata` | Metadata | `api.getNftMetadata(id)` | — | Public endpoint (no auth) |

### BridgeController → `OasisApiClient` (bare response format)

| .NET Route | Method | SDK Method | Response Format |
|------------|--------|------------|-----------------|
| `GET api/bridge/routes` | GetRoutes | `api.getBridgeRoutes()` | **BARE** `BridgeRouteInfo[]` |
| `POST api/bridge/initiate` | Initiate | `api.initiateBridge(params)` | **BARE** `BridgeTransactionResult` |
| `GET api/bridge/{id}` | Status | `api.getBridgeStatus(id)` | **BARE** |
| `POST api/bridge/{id}/fetch-vaa` | FetchVAA | `api.fetchVAA(id)` | **BARE** |
| `POST api/bridge/{id}/redeem` | Redeem | `api.redeemBridge(id)` | **BARE** |
| `POST api/bridge/{id}/complete` | Complete | `api.completeBridge(id)` | **BARE** |
| `POST api/bridge/{id}/reverse` | Reverse | `api.reverseBridge(id, addr)` | **BARE** |
| `GET api/bridge/history` | History | `api.getBridgeHistory()` | **BARE** `BridgeTransactionResult[]` |

**BARE = controller returns raw objects, not wrapped in OASISResult<T>. SDK uses `requestBare()` method.**

### SearchController → `OasisApiClient`

| .NET Route | Method | SDK Method | Request DTO |
|------------|--------|------------|-------------|
| `POST api/search` | Search | `api.search(params)` | `SearchRequest` (POST body, not GET) |
| `GET api/search/facets` | Facets | `api.getSearchFacets()` | — |

## Response Format Rules

| Controller | Wraps in OASISResult<T>? | SDK parse method |
|-----------|--------------------------|------------------|
| AvatarController | Yes | `request()` |
| HolonController | Yes | `request()` |
| WalletController | Yes | `request()` |
| NftController | Yes | `request()` |
| AvatarNFTController | Yes | `request()` |
| SearchController | Yes | `request()` |
| BlockchainOperationController | Yes | `request()` |
| STARODKController | Yes | `request()` |
| **BridgeController** | **No — bare objects** | **`requestBare()`** |

## Type Sync Reference

When a .NET DTO changes, update the matching TypeScript interface:

| .NET DTO | SDK Type | File |
|----------|----------|------|
| `AvatarRegisterModel` | `register()` params | `api/client.ts` |
| `AvatarLoginModel` | `login()` params | `api/client.ts` |
| `IAvatar` / `Avatar` | `AvatarResponse` | `api/client.ts` |
| `NftMintRequest` | `NftMintParams` | `api/client.ts` |
| `NftTransferRequest` | `NftTransferParams` | `api/client.ts` |
| `NftBurnRequest` | `NftBurnParams` | `api/client.ts` |
| `NftResult` | `NftResult` | `api/client.ts` |
| `NftMetadata` | `NftMetadata` | `api/client.ts` |
| `BridgeTransactionResult` | `BridgeTransactionResult` | `api/client.ts` |
| `BridgeRouteInfo` | `BridgeRouteInfo` | `api/client.ts` |
| `BridgeInitiateRequest` | `BridgeInitiateParams` | `api/client.ts` |
| `SearchRequest` | `SearchParams` | `api/client.ts` |
| `SearchResult` | `SearchResult` | `api/client.ts` |
| `HolonQueryRequest` | `HolonQueryParams` | `client/holon-query.ts` |
| `WalletQueryRequest` | inline query params | `client/portfolio.ts` |
| `OASISResult<T>` | `OASISResponse<T>` (internal) | `api/client.ts` |

## API Versioning

The SDK supports versioned API routes via `ApiVersionConfig`:

```typescript
import { OasisClient } from "@oasis/wallet-sdk";

// Default: unversioned (/api/avatar, /api/holon, etc.)
const oasis = new OasisClient({ apiUrl: "https://api.example.com" });

// Future: versioned routes
const oasisV2 = new OasisClient({
  apiUrl: "https://api.example.com",
  // When the backend adds /api/v2/ routes:
  // apiVersion: { version: "v2" },
});
```

To add versioning to the .NET backend:
1. Add `Asp.Versioning.Mvc` NuGet package
2. Configure in Program.cs: `builder.Services.AddApiVersioning()`
3. Add `[ApiVersion("2.0")]` to controllers
4. Update SDK `api-version.ts` path constants
5. Update this sync guide

## Regression Test Checklist

When modifying any controller, verify:

- [ ] SDK `client.test.ts` tests still pass
- [ ] Request body shape matches .NET DTO exactly (field names are camelCase in JSON)
- [ ] Response type matches .NET return type
- [ ] HTTP method is correct (GET/POST/PUT/DELETE)
- [ ] Auth requirement matches ([Authorize] vs [AllowAnonymous])
- [ ] Path parameters match route template (`{id:guid}` vs string)
- [ ] Query parameters match .NET model binding
- [ ] Error response format matches (OASISResult vs bare `{ error }`)
