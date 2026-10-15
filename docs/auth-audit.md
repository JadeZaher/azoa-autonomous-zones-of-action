# Auth-Chain Audit — W3-C1 Deliverable

**Date**: 2026-06-08  
**Branch**: `api-safety-hardening`  
**Purpose**: Input document for W3-E2 JSONL edit pass. Tables 1–4 are the source of truth E2 must use.

---

## Table 1 — Controller Actions and Auth Posture

> Legend: `[Authorize]` = class-level unless noted on the action; `[AllowAnonymous]` = overrides class `[Authorize]`; `Inherited` = action inherits class attribute; `-` = no class-level `[Authorize]`, action has its own.

| Controller | Action | HTTP Method + Route | AuthAttribute | Scheme |
|---|---|---|---|---|
| **UserController** | `Register` | `POST /api/user/register` | `[AllowAnonymous]` (action) | None |
| UserController | `Login` | `POST /api/user/login` | `[AllowAnonymous]` (action) | None |
| UserController | `Get` | `GET /api/user/{id:guid}` | `[Authorize]` (action) | MultiScheme |
| UserController | `GetAll` | `GET /api/user` | `[Authorize]` (action) | MultiScheme |
| UserController | `Update` | `PUT /api/user/{id:guid}` | `[Authorize]` (action) | MultiScheme |
| UserController | `Delete` | `DELETE /api/user/{id:guid}` | `[Authorize]` (action) | MultiScheme |
| **HolonController** | `Get` | `GET /api/holon/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| HolonController | `Query` | `GET /api/holon` | Inherited `[Authorize]` (class) | MultiScheme |
| HolonController | `Create` | `POST /api/holon` | Inherited `[Authorize]` (class) | MultiScheme |
| HolonController | `Update` | `PUT /api/holon/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| HolonController | `Delete` | `DELETE /api/holon/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| HolonController | `Interact` | `POST /api/holon/{id:guid}/interact` | Inherited `[Authorize]` (class) | MultiScheme |
| HolonController | `Mint` | `POST /api/holon/{id:guid}/mint` | Inherited `[Authorize]` (class) | MultiScheme |
| HolonController | `Exchange` | `POST /api/holon/{id:guid}/exchange` | Inherited `[Authorize]` (class) | MultiScheme |
| HolonController | `GetChildren` | `GET /api/holon/{id:guid}/children` | Inherited `[Authorize]` (class) | MultiScheme |
| HolonController | `GetPeers` | `GET /api/holon/{id:guid}/peers` | Inherited `[Authorize]` (class) | MultiScheme |
| HolonController | `GetAncestors` | `GET /api/holon/{id:guid}/ancestors` | Inherited `[Authorize]` (class) | MultiScheme |
| HolonController | `GetDescendants` | `GET /api/holon/{id:guid}/descendants` | Inherited `[Authorize]` (class) | MultiScheme |
| HolonController | `Propagate` | `POST /api/holon/{id:guid}/propagate` | Inherited `[Authorize]` (class) | MultiScheme |
| HolonController | `Compose` | `GET /api/holon/{id:guid}/compose` | Inherited `[Authorize]` (class) | MultiScheme |
| HolonController | `Clone` | `POST /api/holon/{id:guid}/clone` | Inherited `[Authorize]` (class) | MultiScheme |
| HolonController | `MoveSubtree` | `POST /api/holon/{id:guid}/move` | Inherited `[Authorize]` (class) | MultiScheme |
| **BlockchainOperationController** | `Get` | `GET /api/blockchainoperation/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| BlockchainOperationController | `GetByUser` | `GET /api/blockchainoperation/user/{userId:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| **ActionGraphBundleController** | `Get` | `GET /api/actiongraphbundle/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionGraphBundleController | `GetAll` | `GET /api/actiongraphbundle` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionGraphBundleController | `CreateOrUpdate` | `POST /api/actiongraphbundle` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionGraphBundleController | `Update` | `PUT /api/actiongraphbundle/{id}` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionGraphBundleController | `Delete` | `DELETE /api/actiongraphbundle/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionGraphBundleController | `Generate` | `POST /api/actiongraphbundle/{id:guid}/generate` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionGraphBundleController | `Deploy` | `POST /api/actiongraphbundle/{id:guid}/deploy` | Inherited `[Authorize]` (class) | MultiScheme |
| **WalletController** | `Get` | `GET /api/wallet/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| WalletController | `Query` | `GET /api/wallet` | Inherited `[Authorize]` (class) | MultiScheme |
| WalletController | `Create` | `POST /api/wallet` | Inherited `[Authorize]` (class) | MultiScheme |
| WalletController | `Update` | `PUT /api/wallet/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| WalletController | `Delete` | `DELETE /api/wallet/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| WalletController | `SetDefault` | `POST /api/wallet/{id:guid}/set-default` | Inherited `[Authorize]` (class) | MultiScheme |
| WalletController | `GetPortfolio` | `GET /api/wallet/{id:guid}/portfolio` | Inherited `[Authorize]` (class) | MultiScheme |
| WalletController | `Generate` | `POST /api/wallet/generate` | Inherited `[Authorize]` (class) | MultiScheme |
| WalletController | `Connect` | `POST /api/wallet/connect` | Inherited `[Authorize]` (class) | MultiScheme |
| WalletController | `Export` | `POST /api/wallet/{id:guid}/export` | Inherited `[Authorize]` (class) | MultiScheme |
| WalletController | `TopUp` | `POST /api/wallet/{id:guid}/topup` + `[EnableRateLimiting("financial")]` | Inherited `[Authorize]` (class) | MultiScheme |
| WalletController | `GetByType` | `GET /api/wallet/types` | Inherited `[Authorize]` (class) | MultiScheme |
| **NftController** | `Get` | `GET /api/nft/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| NftController | `Query` | `GET /api/nft` | Inherited `[Authorize]` (class) | MultiScheme |
| NftController | `Mint` | `POST /api/nft/mint` | Inherited `[Authorize]` (class) | MultiScheme |
| NftController | `Transfer` | `POST /api/nft/{id:guid}/transfer` | Inherited `[Authorize]` (class) | MultiScheme |
| NftController | `Burn` | `POST /api/nft/{id:guid}/burn` | Inherited `[Authorize]` (class) | MultiScheme |
| NftController | `GetMetadata` | `GET /api/nft/{id:guid}/metadata` | `[AllowAnonymous]` (action, overrides class) | None |
| **UserNFTController** | `MintUserNFT` | `POST /api/usernft/mint` | Inherited `[Authorize]` (class) | MultiScheme |
| UserNFTController | `GetUserNFT` | `GET /api/usernft/{id}` | Inherited `[Authorize]` (class) | MultiScheme |
| UserNFTController | `GetUserNFTByTokenId` | `GET /api/usernft/by-token/{chainType}/{contractAddress}/{tokenId}` | Inherited `[Authorize]` (class) | MultiScheme |
| UserNFTController | `GetUserNFTsByUser` | `GET /api/usernft/user/{userId}` | Inherited `[Authorize]` (class) | MultiScheme |
| UserNFTController | `TransferUserNFT` | `POST /api/usernft/{id}/transfer` | Inherited `[Authorize]` (class) | MultiScheme |
| UserNFTController | `BurnUserNFT` | `DELETE /api/usernft/{id}` | Inherited `[Authorize]` (class) | MultiScheme |
| UserNFTController | `BindHolonToUserNFT` | `POST /api/usernft/{userNFTId}/holons/{holonId}/bind` | Inherited `[Authorize]` (class) | MultiScheme |
| UserNFTController | `GetHolonBindings` | `GET /api/usernft/{userNFTId}/holons` | Inherited `[Authorize]` (class) | MultiScheme |
| UserNFTController | `UpdateHolonBinding` | `PUT /api/usernft/holons/{bindingId}` | Inherited `[Authorize]` (class) | MultiScheme |
| UserNFTController | `RemoveHolonBinding` | `DELETE /api/usernft/holons/{bindingId}` | Inherited `[Authorize]` (class) | MultiScheme |
| UserNFTController | `BindWalletToUserNFT` | `POST /api/usernft/{userNFTId}/wallets/{walletId}/bind` | Inherited `[Authorize]` (class) | MultiScheme |
| UserNFTController | `GetWalletBindings` | `GET /api/usernft/{userNFTId}/wallets` | Inherited `[Authorize]` (class) | MultiScheme |
| UserNFTController | `UpdateWalletBinding` | `PUT /api/usernft/wallets/{bindingId}` | Inherited `[Authorize]` (class) | MultiScheme |
| UserNFTController | `RemoveWalletBinding` | `DELETE /api/usernft/wallets/{bindingId}` | Inherited `[Authorize]` (class) | MultiScheme |
| UserNFTController | `GetUserNFTComposite` | `GET /api/usernft/{userNFTId}/composite` | Inherited `[Authorize]` (class) | MultiScheme |
| UserNFTController | `GetUserNFTCompositesByUser` | `GET /api/usernft/user/{userId}/composite` | Inherited `[Authorize]` (class) | MultiScheme |
| UserNFTController | `VerifyUserNFTOwnership` | `POST /api/usernft/verify-ownership` | Inherited `[Authorize]` (class) | MultiScheme |
| UserNFTController | `VerifyHolonAccess` | `POST /api/usernft/verify-holon-access` | Inherited `[Authorize]` (class) | MultiScheme |
| UserNFTController | `VerifyWalletAccess` | `POST /api/usernft/verify-wallet-access` | Inherited `[Authorize]` (class) | MultiScheme |
| **ApiKeyController** | `Create` | `POST /api/apikey` | Inherited `[Authorize]` (class) | MultiScheme |
| ApiKeyController | `List` | `GET /api/apikey` | Inherited `[Authorize]` (class) | MultiScheme |
| ApiKeyController | `Revoke` | `POST /api/apikey/{id:guid}/revoke` | Inherited `[Authorize]` (class) | MultiScheme |
| ApiKeyController | `Delete` | `DELETE /api/apikey/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| **SearchController** | `Search` | `POST /api/search` | Inherited `[Authorize]` (class) | MultiScheme |
| SearchController | `GetFacets` | `GET /api/search/facets` | Inherited `[Authorize]` (class) | MultiScheme |
| **NetworkController** | `Get` | `GET /api/network` | `[AllowAnonymous]` (class) | None |
| **SwapController** | `GetQuote` | `GET /api/swap/quote` | Inherited `[Authorize]` (class) | MultiScheme |
| SwapController | `ExecuteSwap` | `POST /api/swap/execute` + `[EnableRateLimiting("financial")]` | Inherited `[Authorize]` (class) | MultiScheme |
| **BridgeController** | `GetRoutes` | `GET /api/bridge/routes` | Inherited `[Authorize]` (class) | MultiScheme |
| BridgeController | `InitiateBridge` | `POST /api/bridge/initiate` + `[EnableRateLimiting("financial")]` | Inherited `[Authorize]` (class) | MultiScheme |
| BridgeController | `FetchVAA` | `POST /api/bridge/{id}/fetch-vaa` | Inherited `[Authorize]` (class) | MultiScheme |
| BridgeController | `RedeemWithVAA` | `POST /api/bridge/{id}/redeem` + `[EnableRateLimiting("financial")]` | Inherited `[Authorize]` (class) | MultiScheme |
| BridgeController | `ReverseBridge` | `POST /api/bridge/{id}/reverse` + `[EnableRateLimiting("financial")]` | Inherited `[Authorize]` (class) | MultiScheme |
| BridgeController | `GetBridgeStatus` | `GET /api/bridge/{id}` | Inherited `[Authorize]` (class) | MultiScheme |
| BridgeController | `GetHistory` | `GET /api/bridge/history` | Inherited `[Authorize]` (class) | MultiScheme |
| **ActionController** | `Create` | `POST /api/action` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionController | `Get` | `GET /api/action/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionController | `GetByUser` | `GET /api/action/user/{userId:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionController | `Update` | `PUT /api/action/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionController | `Delete` | `DELETE /api/action/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionController | `Validate` | `POST /api/action/{id:guid}/validate` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionController | `Execute` | `POST /api/action/{id:guid}/execute` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionController | `ExecuteNode` | `POST /api/action/{id:guid}/nodes/{nodeId:guid}/execute` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionController | `Fork` | `POST /api/action/runs/{runId:guid}/fork` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionController | `MarkRunFailed` | `POST /api/action/runs/{runId:guid}/mark-failed` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionController | `CreateTemplate` | `POST /api/action/templates` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionController | `GetTemplate` | `GET /api/action/templates/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionController | `ListTemplates` | `GET /api/action/templates` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionController | `InstantiateTemplate` | `POST /api/action/templates/{id:guid}/instantiate` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionController | `CreateNodeTemplate` | `POST /api/action/node-templates` | Inherited `[Authorize]` (class) | MultiScheme |
| ActionController | `ListNodeTemplates` | `GET /api/action/node-templates` | Inherited `[Authorize]` (class) | MultiScheme |
| **DappSeriesController** | `List` | `GET /api/dapp-series` | Inherited `[Authorize]` (class) | MultiScheme |
| DappSeriesController | `Get` | `GET /api/dapp-series/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| DappSeriesController | `Create` | `POST /api/dapp-series` | Inherited `[Authorize]` (class) | MultiScheme |
| DappSeriesController | `Update` | `PUT /api/dapp-series/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| DappSeriesController | `Delete` | `DELETE /api/dapp-series/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| DappSeriesController | `ListActions` | `GET /api/dapp-series/{seriesId:guid}/actions` | Inherited `[Authorize]` (class) | MultiScheme |
| DappSeriesController | `AddAction` | `POST /api/dapp-series/{seriesId:guid}/actions` | Inherited `[Authorize]` (class) | MultiScheme |
| DappSeriesController | `RemoveAction` | `DELETE /api/dapp-series/{seriesId:guid}/actions/{actionId:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| DappSeriesController | `ReorderAction` | `PUT /api/dapp-series/{seriesId:guid}/actions/{actionId:guid}/order` | Inherited `[Authorize]` (class) | MultiScheme |
| DappSeriesController | `UpdateMappings` | `PUT /api/dapp-series/{seriesId:guid}/actions/{actionId:guid}/mappings` | Inherited `[Authorize]` (class) | MultiScheme |
| **DappCompositionController** | `Compose` | `POST /api/dapp-series/{id:guid}/compose` | Inherited `[Authorize]` (class) | MultiScheme |
| DappCompositionController | `Validate` | `GET /api/dapp-series/{id:guid}/validate` | Inherited `[Authorize]` (class) | MultiScheme |
| DappCompositionController | `GetManifest` | `GET /api/dapp-series/{id:guid}/manifest` | Inherited `[Authorize]` (class) | MultiScheme |
| DappCompositionController | `Generate` | `POST /api/dapp-series/{id:guid}/generate` | Inherited `[Authorize]` (class) | MultiScheme |
| DappCompositionController | `Deploy` | `POST /api/dapp-series/{id:guid}/deploy` | Inherited `[Authorize]` (class) | MultiScheme |
| DappCompositionController | `GetStatus` | `GET /api/dapp-series/{id:guid}/status` | Inherited `[Authorize]` (class) | MultiScheme |

**AllowAnonymous summary (3 total):**
- `UserController.Register` — POST /api/user/register
- `UserController.Login` — POST /api/user/login  
- `NftController.GetMetadata` — GET /api/nft/{id}/metadata
- `NetworkController` (class-level `[AllowAnonymous]`) — GET /api/network

> Note: `POST /api/user/{id}/wallets` and `DELETE /api/user/{id}/wallets/{walletId}` are referenced in many JSONL suites but **do not exist as routes in any controller**. This is a harness misconfiguration — these calls will 404. E2 cannot fix this by adding auth headers; the API itself is missing these endpoints. They are flagged in Table 3.

---

## Table 2 — JSONL Case Map

> Abbreviations: Auth header present = `Y` / `N`; `expectedStatus` = HTTP status or range; `→ controller` = matched action.
> Suites marked `[NOT EDITABLE BY E2]` must not be modified.

### UserController.jsonl (11 cases)

| CaseId | Method | Path (resolved) | Matched Action | Auth Header? | expectedStatus |
|---|---|---|---|---|---|
| register_user | POST | /api/user/register | User.Register | N | 200 |
| register_duplicate | POST | /api/user/register | User.Register | N | 400 |
| login_user | POST | /api/user/login | User.Login | N | 200 |
| get_user | GET | /api/user/{{user1.userId}} | User.Get | Y | 200 |
| get_all_users | GET | /api/user | User.GetAll | Y | 200 |
| update_user | PUT | /api/user/{{user1.userId}} | User.Update | Y | 200 |
| add_wallet | POST | /api/user/{{user1.userId}}/wallets | **MISSING ROUTE** | Y | 200 |
| get_wallets | GET | /api/user/{{user1.userId}}/wallets | **MISSING ROUTE** | Y | 200 |
| remove_wallet | DELETE | /api/user/{{user1.userId}}/wallets/{{wallet1.walletId}} | **MISSING ROUTE** | Y | 200 |
| delete_user | DELETE | /api/user/{{user1.userId}} | User.Delete | Y | 200 |
| get_deleted_user | GET | /api/user/{{user1.userId}} | User.Get | Y | 404 |

### UserController_QA.jsonl (33 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| register_minimal | POST | /api/user/register | User.Register | N | 200 |
| register_full_profile | POST | /api/user/register | User.Register | N | 200 |
| register_unicode_username | POST | /api/user/register | User.Register | N | 200 |
| register_special_chars_email | POST | /api/user/register | User.Register | N | 200 |
| register_long_username | POST | /api/user/register | User.Register | N | 200 |
| register_duplicate_username | POST | /api/user/register | User.Register | N | 200 |
| register_duplicate_email | POST | /api/user/register | User.Register | N | 400 |
| login_minimal | POST | /api/user/login | User.Login | N | 200 |
| login_full | POST | /api/user/login | User.Login | N | 200 |
| login_unicode | POST | /api/user/login | User.Login | N | 200 |
| login_wrong_password | POST | /api/user/login | User.Login | N | 401 |
| login_nonexistent | POST | /api/user/login | User.Login | N | 401 |
| login_empty_body | POST | /api/user/login | User.Login | N | 400 |
| get_minimal_user | GET | /api/user/{{minimalUser.id}} | User.Get | Y | 200 |
| get_full_user | GET | /api/user/{{fullUser.id}} | User.Get | Y | 200 |
| get_nonexistent_user | GET | /api/user/00000000-…-000 | User.Get | Y | 404 |
| get_all_users | GET | /api/user | User.GetAll | Y | 200 |
| update_title | PUT | /api/user/{{minimalUser.id}} | User.Update | Y | 200 |
| update_email | PUT | /api/user/{{minimalUser.id}} | User.Update | Y | 200 |
| update_multiple_fields | PUT | /api/user/{{fullUser.id}} | User.Update | Y | 200 |
| update_nonexistent | PUT | /api/user/00000000-…-000 | User.Update | Y | 404 |
| add_algorand_wallet | POST | /api/user/{{minimalUser.id}}/wallets | **MISSING ROUTE** | Y | 200 |
| add_solana_wallet | POST | /api/user/{{minimalUser.id}}/wallets | **MISSING ROUTE** | Y | 200 |
| add_second_algorand_wallet | POST | /api/user/{{minimalUser.id}}/wallets | **MISSING ROUTE** | Y | 200 |
| get_wallets | GET | /api/user/{{minimalUser.id}}/wallets | **MISSING ROUTE** | Y | 200 |
| remove_solana_wallet | DELETE | /api/user/{{minimalUser.id}}/wallets/{{solWallet.walletId}} | **MISSING ROUTE** | Y | 200 |
| remove_nonexistent_wallet | DELETE | /api/user/{{minimalUser.id}}/wallets/00000000-…-000 | **MISSING ROUTE** | Y | 404 |
| add_wallet_to_other_user | POST | /api/user/{{fullUser.id}}/wallets | **MISSING ROUTE** | Y | 200 |
| get_without_auth | GET | /api/user/{{minimalUser.id}} | User.Get | N | 401 |
| get_with_malformed_auth | GET | /api/user/{{minimalUser.id}} | User.Get | Y (invalid) | 401 |
| delete_minimal_user | DELETE | /api/user/{{minimalUser.id}} | User.Delete | Y | 200 |
| delete_full_user | DELETE | /api/user/{{fullUser.id}} | User.Delete | Y | 200 |
| delete_unicode_user | DELETE | /api/user/{{unicodeUser.id}} | User.Delete | Y | 200 |
| verify_deleted | GET | /api/user/{{minimalUser.id}} | User.Get | Y | 404 |
| delete_nonexistent | DELETE | /api/user/00000000-…-000 | User.Delete | Y | 404 |

### UserController_Malicious.jsonl (46 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| sqli_username | POST | /api/user/register | User.Register | N | 2xx |
| sqli_email | POST | /api/user/register | User.Register | N | 2xx |
| sqli_password | POST | /api/user/register | User.Register | N | 2xx |
| sqli_login_email | POST | /api/user/login | User.Login | N | 401 |
| sqli_login_password | POST | /api/user/login | User.Login | N | 401 |
| sqli_blind_union | POST | /api/user/register | User.Register | N | 2xx |
| xss_username | POST | /api/user/register | User.Register | N | 2xx |
| xss_email | POST | /api/user/register | User.Register | N | 2xx |
| xss_title | POST | /api/user/register | User.Register | N | 2xx |
| xss_firstname | POST | /api/user/register | User.Register | N | 2xx |
| xss_encoded | POST | /api/user/register | User.Register | N | 2xx |
| oversized_username_10k | POST | /api/user/register | User.Register | N | 2xx |
| oversized_email_1k | POST | /api/user/register | User.Register | N | 2xx |
| oversized_title | POST | /api/user/register | User.Register | N | 2xx |
| null_username | POST | /api/user/register | User.Register | N | 4xx |
| numeric_username | POST | /api/user/register | User.Register | N | 4xx |
| array_email | POST | /api/user/register | User.Register | N | 4xx |
| boolean_password | POST | /api/user/register | User.Register | N | 4xx |
| object_instead_of_string | POST | /api/user/register | User.Register | N | 4xx |
| empty_json | POST | /api/user/register | User.Register | N | 4xx |
| missing_required | POST | /api/user/register | User.Register | N | 4xx |
| path_traversal_get | GET | /api/user/../../../etc/passwd | (404 routing) | Y (invalid) | 404 |
| null_byte_injection | GET | /api/user/550e8400…%00 | (404 routing) | Y (invalid) | 404 |
| double_encoding | GET | /api/user/%25550e8400… | (404 routing) | Y (invalid) | 404 |
| rtl_override | POST | /api/user/register | User.Register | N | 2xx |
| zero_width_chars | POST | /api/user/register | User.Register | N | 2xx |
| emoji_username | POST | /api/user/register | User.Register | N | 2xx |
| control_chars | POST | /api/user/register | User.Register | N | 2xx |
| bidi_attack | POST | /api/user/register | User.Register | N | 2xx |
| deeply_nested_body | POST | /api/user/register | User.Register | N | 4xx |
| massive_array | POST | /api/user/register | User.Register | N | 2xx |
| header_injection_content_type | POST | /api/user/register | User.Register | N | 4xx |
| header_injection_auth | GET | /api/user/00000000-…-000 | User.Get | Y (injected) | 401 |
| rapid_register_1–5 (×5) | POST | /api/user/register | User.Register | N | 2xx |
| negative_karma_if_exposed | POST | /api/user/register | User.Register | N | 2xx |
| login_negative_user | POST | /api/user/login | User.Login | N | 200 |
| cleanup_negative | DELETE | /api/user/{{negUser.id}} | User.Delete | Y | 200 |

### HolonController.jsonl (9 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| seed_user | POST | /api/user/register | User.Register | N | 200 |
| login_seed | POST | /api/user/login | User.Login | N | 200 |
| create_holon | POST | /api/holon | Holon.Create | Y | 200 |
| get_holon | GET | /api/holon/{{holon1.holonId}} | Holon.Get | Y | 200 |
| query_holons | GET | /api/holon?name=LiveHolon | Holon.Query | Y | 200 |
| update_holon | PUT | /api/holon/{{holon1.holonId}} | Holon.Update | Y | 200 |
| interact_holon | POST | /api/holon/{{holon1.holonId}}/interact | Holon.Interact | Y | 200 |
| delete_holon | DELETE | /api/holon/{{holon1.holonId}} | Holon.Delete | Y | 200 |
| cleanup_user | DELETE | /api/user/{{huser.userId}} | User.Delete | Y | 200 |

### HolonController_QA.jsonl (38 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| seed_user_a | POST | /api/user/register | User.Register | N | 200 |
| login_user_a | POST | /api/user/login | User.Login | N | 200 |
| seed_user_b | POST | /api/user/register | User.Register | N | 200 |
| login_user_b | POST | /api/user/login | User.Login | N | 200 |
| create_root_holon | POST | /api/holon | Holon.Create | Y | 200 |
| create_child_holon | POST | /api/holon | Holon.Create | Y | 200 |
| create_peer_holon | POST | /api/holon | Holon.Create | Y | 200 |
| create_chain_holon | POST | /api/holon | Holon.Create | Y | 200 |
| get_root_holon | GET | /api/holon/{{rootHolon.id}} | Holon.Get | Y | 200 |
| verify_user_id_set | GET | /api/holon/{{rootHolon.id}} | Holon.Get | Y | 200 |
| query_by_name | GET | /api/holon?name=RootHolon | Holon.Query | Y | 200 |
| query_no_filter | GET | /api/holon | Holon.Query | Y | 200 |
| query_nonexistent | GET | /api/holon?name=NonExistentHolonXYZ | Holon.Query | Y | 200 |
| update_name | PUT | /api/holon/{{rootHolon.id}} | Holon.Update | Y | 200 |
| update_metadata | PUT | /api/holon/{{rootHolon.id}} | Holon.Update | Y | 200 |
| update_chain | PUT | /api/holon/{{chainHolon.id}} | Holon.Update | Y | 200 |
| update_nonexistent | PUT | /api/holon/00000000-…-000 | Holon.Update | Y | 404 |
| interact_add_peers | POST | /api/holon/{{rootHolon.id}}/interact | Holon.Interact | Y | 200 |
| interact_change_parent | POST | /api/holon/{{childHolon.id}}/interact | Holon.Interact | Y | 200 |
| interact_remove_metadata | POST | /api/holon/{{rootHolon.id}}/interact | Holon.Interact | Y | 200 |
| interact_remove_peers | POST | /api/holon/{{rootHolon.id}}/interact | Holon.Interact | Y | 200 |
| isolation_user_b_get_a_holon | GET | /api/holon/{{rootHolon.id}} | Holon.Get | Y (authB) | 200 |
| isolation_user_b_update_a_holon | PUT | /api/holon/{{rootHolon.id}} | Holon.Update | Y (authB) | 200 |
| isolation_user_b_delete_a_holon | DELETE | /api/holon/{{rootHolon.id}} | Holon.Delete | Y (authB) | 200 |
| isolation_unauth_create | POST | /api/holon | Holon.Create | N | 401 |
| create_b_holon | POST | /api/holon | Holon.Create | Y | 200 |
| delete_child_holon | DELETE | /api/holon/{{childHolon.id}} | Holon.Delete | Y | 200 |
| delete_peer_holon | DELETE | /api/holon/{{peerHolon.id}} | Holon.Delete | Y | 200 |
| delete_chain_holon | DELETE | /api/holon/{{chainHolon.id}} | Holon.Delete | Y | 200 |
| delete_root_holon | DELETE | /api/holon/{{rootHolon.id}} | Holon.Delete | Y | 200 |
| delete_b_holon | DELETE | /api/holon/{{bHolon.id}} | Holon.Delete | Y | 200 |
| verify_deleted_holon | GET | /api/holon/{{rootHolon.id}} | Holon.Get | Y | 404 |
| delete_nonexistent_holon | DELETE | /api/holon/00000000-…-000 | Holon.Delete | Y | 404 |
| cleanup_user_a | DELETE | /api/user/{{userA.id}} | User.Delete | Y | 200 |
| cleanup_user_b | DELETE | /api/user/{{userB.id}} | User.Delete | Y | 200 |
| *(3 additional — rapid_register etc.)* | | | | | |

### HolonController_Malicious.jsonl (35 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| seed_target | POST | /api/user/register | User.Register | N | 200 |
| login_target | POST | /api/user/login | User.Login | N | 200 |
| seed_holon | POST | /api/holon | Holon.Create | Y | 200 |
| sqli_holon_name–sqli_chain_id (×6) | POST | /api/holon | Holon.Create | Y | 2xx |
| xss_holon_name–xss_interact (×4) | POST /GET | /api/holon or /interact | Holon.Create/Interact | Y | 2xx or 200 |
| path_traversal_holon_get | GET | /api/holon/../../../etc/passwd | (404) | Y | 404 |
| invalid_guid_holon | GET | /api/holon/not-a-valid-guid | (404) | Y | 404 |
| empty_guid_holon | GET | /api/holon/ | (404) | Y | 404 |
| guid_with_null | GET | /api/holon/550e8400…%00 | (404) | Y | 404 |
| negative_guid | GET | /api/holon/-50e8400… | (404) | Y | 404 |
| oversized_holon_name | POST | /api/holon | Holon.Create | Y | 2xx |
| oversized_metadata | POST | /api/holon | Holon.Create | Y | 2xx |
| null_holon_name–missing_provider (×6) | POST | /api/holon | Holon.Create | Y | 4xx |
| sqli_interact_metadata–interact_null_parent (×5) | POST | /api/holon/{id}/interact | Holon.Interact | Y | 200 or 4xx |
| mint_negative_amount–exchange_sqli_rate (×6) | POST | /api/holon/{id}/mint or /exchange | Holon.Mint/Exchange | Y | 4xx |
| cleanup_target_holon | DELETE | /api/holon/{{targetHolon.id}} | Holon.Delete | Y | 200 |
| cleanup_user | DELETE | /api/user/{{malUser.id}} | User.Delete | Y | 200 |

### BlockchainOperationController.jsonl (6 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| seed_user | POST | /api/user/register | User.Register | N | 200 |
| login_seed | POST | /api/user/login | User.Login | N | 200 |
| seed_wallet | POST | /api/user/{{buser.userId}}/wallets | **MISSING ROUTE** | Y | 200 |
| get_operations_by_user | GET | /api/blockchainoperation/user/{{buser.userId}} | BlockchainOp.GetByUser | Y | 200 |
| cleanup_wallet | DELETE | /api/user/{{buser.userId}}/wallets/{{bwallet.walletId}} | **MISSING ROUTE** | Y | 200 |
| cleanup_user | DELETE | /api/user/{{buser.userId}} | User.Delete | Y | 200 |

### BlockchainOperationController_QA.jsonl (9 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| seed_user | POST | /api/user/register | User.Register | N | 200 |
| login_user | POST | /api/user/login | User.Login | N | 200 |
| add_wallet | POST | /api/user/{{bcUser.id}}/wallets | **MISSING ROUTE** | Y | 200 |
| get_ops_by_user_empty | GET | /api/blockchainoperation/user/{{bcUser.id}} | BlockchainOp.GetByUser | Y | 200 |
| get_op_by_id_nonexistent | GET | /api/blockchainoperation/00000000-…-000 | BlockchainOp.Get | Y | 404 |
| get_op_unauthorized | GET | /api/blockchainoperation/00000000-…-000 | BlockchainOp.Get | N | 401 |
| get_ops_by_user_unauthorized | GET | /api/blockchainoperation/user/{{bcUser.id}} | BlockchainOp.GetByUser | N | 401 |
| get_ops_wrong_user_id | GET | /api/blockchainoperation/user/not-a-guid | (404) | Y | 404 |
| cleanup_wallet | DELETE | /api/user/{{bcUser.id}}/wallets/{{bcWallet.walletId}} | **MISSING ROUTE** | Y | 200 |
| cleanup_user | DELETE | /api/user/{{bcUser.id}} | User.Delete | Y | 200 |

### BlockchainOperationController_Malicious.jsonl (21 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| seed_user | POST | /api/user/register | User.Register | N | 200 |
| login_user | POST | /api/user/login | User.Login | N | 200 |
| get_op_sqli_guid | GET | /api/blockchainoperation/' OR '1'='1 | (404) | Y | 404 |
| get_op_path_traversal | GET | /api/blockchainoperation/../… | (404) | Y | 404 |
| get_op_null_byte | GET | /api/blockchainoperation/550e8400…%00 | (404) | Y | 404 |
| get_op_negative_numbers | GET | /api/blockchainoperation/-50e8400… | (404) | Y | 404 |
| get_by_user_sqli | GET | /api/blockchainoperation/user/' UNION… | (404) | Y | 404 |
| get_by_user_path_traversal | GET | /api/blockchainoperation/user/../../… | (404) | Y | 404 |
| get_by_user_xss | GET | /api/blockchainoperation/user/<script>… | (404) | Y | 404 |
| get_by_user_very_long | GET | /api/blockchainoperation/user/AAA… | (404) | Y | 404 |
| get_op_no_auth | GET | /api/blockchainoperation/00000000-…-000 | BlockchainOp.Get | N | 401 |
| get_by_user_no_auth | GET | /api/blockchainoperation/user/{{bcMalUser.id}} | BlockchainOp.GetByUser | N | 401 |
| get_op_empty_auth | GET | /api/blockchainoperation/00000000-…-000 | BlockchainOp.Get | Y (empty) | 401 |
| get_op_bearer_only | GET | /api/blockchainoperation/00000000-…-000 | BlockchainOp.Get | Y (Bearer only) | 401 |
| get_op_tampered_token | GET | /api/blockchainoperation/user/{{bcMalUser.id}} | BlockchainOp.GetByUser | Y (tampered) | 401 |
| header_injection_op | GET | /api/blockchainoperation/user/{{bcMalUser.id}} | BlockchainOp.GetByUser | Y (injected) | 401 |
| get_op_sqli_provider_type | GET | /api/blockchainoperation/user/{{bcMalUser.id}}?providerType=… | BlockchainOp.GetByUser | Y | 2xx |
| get_op_xss_provider_type | GET | /api/blockchainoperation/user/{{bcMalUser.id}}?providerType=… | BlockchainOp.GetByUser | Y | 2xx |
| get_op_very_long_query | GET | /api/blockchainoperation/user/{{bcMalUser.id}}?customProviderKeys=… | BlockchainOp.GetByUser | Y | 2xx |
| cleanup_user | DELETE | /api/user/{{bcMalUser.id}} | User.Delete | Y | 200 |

### ActionGraphBundleController.jsonl (8 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| seed_user | POST | /api/user/register | User.Register | N | 200 |
| login_seed | POST | /api/user/login | User.Login | N | 200 |
| create_odk | POST | /api/actiongraphbundle | ActionGraphBundle.CreateOrUpdate | Y | 200 |
| get_odk | GET | /api/actiongraphbundle/{{odk1.odkId}} | ActionGraphBundle.Get | Y | 200 |
| get_all_odks | GET | /api/actiongraphbundle | ActionGraphBundle.GetAll | Y | 200 |
| generate_odk | POST | /api/actiongraphbundle/{{odk1.odkId}}/generate | ActionGraphBundle.Generate | Y | 200 |
| delete_odk | DELETE | /api/actiongraphbundle/{{odk1.odkId}} | ActionGraphBundle.Delete | Y | 200 |
| cleanup_user | DELETE | /api/user/{{suser.userId}} | User.Delete | Y | 200 |

### ActionGraphBundleController_QA.jsonl (26 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| seed_user | POST | /api/user/register | User.Register | N | 200 |
| login_user | POST | /api/user/login | User.Login | N | 200 |
| create_odk_basic | POST | /api/actiongraphbundle | ActionGraphBundle.CreateOrUpdate | Y | 200 |
| create_odk_advanced | POST | /api/actiongraphbundle | ActionGraphBundle.CreateOrUpdate | Y | 200 |
| create_odk_unicode | POST | /api/actiongraphbundle | ActionGraphBundle.CreateOrUpdate | Y | 200 |
| get_odk_by_id | GET | /api/actiongraphbundle/{{odkBasic.id}} | ActionGraphBundle.Get | Y | 200 |
| get_nonexistent_odk | GET | /api/actiongraphbundle/00000000-…-000 | ActionGraphBundle.Get | Y | 404 |
| get_all_odks | GET | /api/actiongraphbundle | ActionGraphBundle.GetAll | Y | 200 |
| generate_odk_algorand | POST | /api/actiongraphbundle/{{odkBasic.id}}/generate | ActionGraphBundle.Generate | Y | 200 |
| generate_odk_solana | POST | /api/actiongraphbundle/{{odkAdvanced.id}}/generate | ActionGraphBundle.Generate | Y | 200 |
| deploy_odk_basic | POST | /api/actiongraphbundle/{{odkBasic.id}}/deploy | ActionGraphBundle.Deploy | Y | 200 |
| deploy_odk_advanced | POST | /api/actiongraphbundle/{{odkAdvanced.id}}/deploy | ActionGraphBundle.Deploy | Y | 200 |
| update_odk_via_upsert | POST | /api/actiongraphbundle | ActionGraphBundle.CreateOrUpdate | Y | 200 |
| get_odk_unauthorized | GET | /api/actiongraphbundle/{{odkBasic.id}} | ActionGraphBundle.Get | N | 401 |
| create_odk_unauthorized | POST | /api/actiongraphbundle | ActionGraphBundle.CreateOrUpdate | N | 401 |
| delete_odk_unauthorized | DELETE | /api/actiongraphbundle/{{odkBasic.id}} | ActionGraphBundle.Delete | N | 401 |
| generate_odk_unauthorized | POST | /api/actiongraphbundle/{{odkBasic.id}}/generate | ActionGraphBundle.Generate | N | 401 |
| create_fresh_odk | POST | /api/actiongraphbundle | ActionGraphBundle.CreateOrUpdate | Y | 200 |
| deploy_before_generate | POST | /api/actiongraphbundle/{{odkFresh.id}}/deploy | ActionGraphBundle.Deploy | Y | 400 |
| delete_odk_basic–verify_deleted_odk (×5) | DELETE/GET | /api/actiongraphbundle/{id} | ActionGraphBundle.Delete/Get | Y | 200/404 |
| cleanup_user | DELETE | /api/user/{{starUser.id}} | User.Delete | Y | 200 |

### ActionGraphBundleController_Malicious.jsonl (38 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| seed_user | POST | /api/user/register | User.Register | N | 200 |
| login_user | POST | /api/user/login | User.Login | N | 200 |
| seed_odk | POST | /api/actiongraphbundle | ActionGraphBundle.CreateOrUpdate | Y | 200 |
| sqli_odk_name–sqli_generate_config (×4) | POST | /api/actiongraphbundle or /generate | ActionGraphBundle.CreateOrUpdate/Generate | Y | 2xx or 200 |
| xss_odk_name–xss_generate_config (×3) | POST | /api/actiongraphbundle or /generate | ActionGraphBundle.CreateOrUpdate/Generate | Y | 2xx or 200 |
| get_odk_path_traversal–delete_invalid_odk (×6) | GET/POST/DELETE | /api/actiongraphbundle/{bad-id} | (404) | Y | 404 |
| oversized_odk_name–oversized_generation_config (×3) | POST | /api/actiongraphbundle or /generate | ActionGraphBundle.CreateOrUpdate/Generate | Y | 2xx or 200 |
| null_odk_name–empty_body (×6) | POST | /api/actiongraphbundle | ActionGraphBundle.CreateOrUpdate | Y | 4xx |
| generate_invalid_chain–deploy_before_generate (×6) | POST | /api/actiongraphbundle/{id}/generate or /deploy | ActionGraphBundle.Generate/Deploy | Y | 200 or 4xx or 400 |
| get_odk_unauth–delete_odk_unauth (×5) | GET/POST/DELETE | /api/actiongraphbundle/{id} | ActionGraphBundle.* | N | 401 |
| unicode_odk_name–zwc_odk_name (×3) | POST | /api/actiongraphbundle | ActionGraphBundle.CreateOrUpdate | Y | 2xx |
| cleanup_target_odk | DELETE | /api/actiongraphbundle/{{targetODK.id}} | ActionGraphBundle.Delete | Y | 200 |
| cleanup_user | DELETE | /api/user/{{starMalUser.id}} | User.Delete | Y | 200 |

### E2E-Flows.jsonl (96 cases across Flows 1–10)

> All authenticated cases use `Authorization: Bearer {{e2eNAuth.token}}` extracted earlier in the same flow.

| Flow | Cases | Key paths | Auth? | expectedStatus |
|---|---|---|---|---|
| Flow 1 (e2e1_*) | 14 | /api/user/register+login, /api/holon (CRUD), /api/user/*/wallets (MISSING ROUTE) | Mix Y/N | 200 |
| Flow 2 (e2e2_*) | 10 | /api/user/register+login, /api/holon, /api/actiongraphbundle (create+gen+delete) | Mix Y/N | 200 |
| Flow 3 (e2e3_*) | 11 | /api/user/register+login (×2), /api/holon (cross-user) | Mix Y/N | 200 or 404 |
| Flow 4 (e2e4_*) | 9 | /api/user/*/wallets (MISSING ROUTE), /api/holon/mint, /api/blockchainoperation | Mix Y/N | 200 |
| Flow 5 (e2e5_*) | 12 | /api/user/register+login, GET/PUT /api/user/{id} (×8) | Mix Y/N | 200 |
| Flow 6 (e2e6_*) | 10 | /api/holon (create+interact×5+delete) | Mix Y/N | 200 |
| Flow 7 (e2e7_*) | 11 | Token reuse across /api/user, /api/holon, /api/actiongraphbundle, /api/blockchainoperation | Mix Y/N | 200 |
| Flow 8 (e2e8_*) | 9 | /api/actiongraphbundle (full deploy lifecycle) | Mix Y/N | 200 |
| Flow 9 (e2e9_*) | 12 | /api/holon/mint+exchange, /api/blockchainoperation (get ops) | Mix Y/N | 200 |
| Flow 10 (e2e10_*) | 8 | /api/user/*/wallets (MISSING ROUTE ×3), multi-wallet management | Mix Y/N | 200 |

### CrossController_E2E.jsonl (57 cases across 3 flows)

> All authenticated. Flows follow register→login→operations→cleanup pattern.

| Flow | Cases | Key paths | Auth? | expectedStatus |
|---|---|---|---|---|
| Flow 1 (e2e1_*) | 19 | /api/user, /api/user/*/wallets (MISSING ROUTE), /api/holon/mint+exchange, /api/blockchainoperation | Mix Y/N | 200 |
| Flow 2 (e2e2_*) | 14 | /api/holon ×2, /api/actiongraphbundle (gen+deploy) | Mix Y/N | 200 |
| Flow 3 (e2e3_*) | 14 | /api/holon (×2 users, isolation test — 200 on get/update/delete, no ownership enforcement) | Mix Y/N | 200 |

### Blockchain_Devnet.jsonl (30 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| algo_seed_user | POST | /api/user/register | User.Register | N | 200 |
| algo_login | POST | /api/user/login | User.Login | N | 200 |
| algo_add_wallet | POST | /api/user/{{algoUser.id}}/wallets | **MISSING ROUTE** | Y | 200 |
| sol_seed_user | POST | /api/user/register | User.Register | N | 200 |
| sol_login | POST | /api/user/login | User.Login | N | 200 |
| sol_add_wallet | POST | /api/user/{{solUser.id}}/wallets | **MISSING ROUTE** | Y | 200 |
| algo_create_holon | POST | /api/holon | Holon.Create | Y | 200 |
| sol_create_holon | POST | /api/holon | Holon.Create | Y | 200 |
| algo_create_peer_holon | POST | /api/holon | Holon.Create | Y | 200 |
| algo_mint_asa | POST | /api/holon/{{algoHolon.id}}/mint | Holon.Mint | Y | 200 |
| algo_mint_small | POST | /api/holon/{{algoHolon.id}}/mint | Holon.Mint | Y | 200 |
| algo_mint_large | POST | /api/holon/{{algoHolon.id}}/mint | Holon.Mint | Y | 200 |
| sol_mint_spl | POST | /api/holon/{{solHolon.id}}/mint | Holon.Mint | Y | 200 |
| sol_mint_nft | POST | /api/holon/{{solHolon.id}}/mint | Holon.Mint | Y | 200 |
| algo_exchange | POST | /api/holon/{{algoHolon.id}}/exchange | Holon.Exchange | Y | 200 |
| algo_exchange_reverse | POST | /api/holon/{{algoPeerHolon.id}}/exchange | Holon.Exchange | Y | 200 |
| algo_get_op_by_id | GET | /api/blockchainoperation/{{algoMintOp.opId}} | BlockchainOp.Get | Y | 200 |
| algo_get_ops_by_user | GET | /api/blockchainoperation/user/{{algoUser.id}} | BlockchainOp.GetByUser | Y | 200 |
| sol_get_op_by_id | GET | /api/blockchainoperation/{{solMintOp.opId}} | BlockchainOp.Get | Y | 200 |
| sol_get_ops_by_user | GET | /api/blockchainoperation/user/{{solUser.id}} | BlockchainOp.GetByUser | Y | 200 |
| algo_verify_mint_status | GET | /api/blockchainoperation/{{algoMintOp.opId}} | BlockchainOp.Get | Y | 200 |
| sol_verify_mint_status | GET | /api/blockchainoperation/{{solMintOp.opId}} | BlockchainOp.Get | Y | 200 |
| sol_get_algo_op | GET | /api/blockchainoperation/{{algoMintOp.opId}} | BlockchainOp.Get | Y | 200 |
| algo_cleanup_* (×2) | DELETE | /api/holon/… | Holon.Delete | Y | 200 |
| sol_cleanup_holon | DELETE | /api/holon/… | Holon.Delete | Y | 200 |
| algo_cleanup_wallet | DELETE | /api/user/{{algoUser.id}}/wallets/… | **MISSING ROUTE** | Y | 200 |
| sol_cleanup_wallet | DELETE | /api/user/{{solUser.id}}/wallets/… | **MISSING ROUTE** | Y | 200 |
| algo_cleanup_user | DELETE | /api/user/{{algoUser.id}} | User.Delete | Y | 200 |
| sol_cleanup_user | DELETE | /api/user/{{solUser.id}} | User.Delete | Y | 200 |

### MaliciousPayloads.jsonl (77 cases)

> All cases use user seed+login as first two cases. Key auth test cases within:

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| mal_user_seed | POST | /api/user/register | User.Register | N | 200 |
| mal_user_login | POST | /api/user/login | User.Login | N | 200 |
| mal_sql_* (×4 incl. 2 login) | POST | /api/user/register or /login | Register/Login | N | 400 or 401 |
| mal_xss_* (×3) | POST | /api/user/register | User.Register | N | 400 |
| mal_oversized_* (×2) | POST | /api/user/register | User.Register | N | 400 |
| mal_special_chars_* (×4) | POST | /api/user/register | User.Register | N | 400 |
| mal_type_* (×3) | POST | /api/user/register | User.Register | N | 400 |
| mal_null_* (×3) | POST | /api/user/register | User.Register | N | 400 |
| mal_auth_no_bearer | GET | /api/user/{{malUser.userId}} | User.Get | Y (no Bearer prefix) | 401 |
| mal_auth_expired_format | GET | /api/user/{{malUser.userId}} | User.Get | Y (bad JWT) | 401 |
| mal_auth_empty_token | GET | /api/user/{{malUser.userId}} | User.Get | Y (empty) | 401 |
| mal_path_traversal | GET | /api/user/../user/… | (404) | Y | 404 |
| mal_guid_zero | GET | /api/user/00000000-…-000 | User.Get | Y | 404 |
| mal_update_xss | PUT | /api/user/{{malUser.userId}} | User.Update | Y | 400 |
| mal_update_sqli | PUT | /api/user/{{malUser.userId}} | User.Update | Y | 400 |
| mal_wallet_xss_address | POST | /api/user/{{malUser.userId}}/wallets | **MISSING ROUTE** | Y | 400 |
| mal_wallet_sqli_label | POST | /api/user/{{malUser.userId}}/wallets | **MISSING ROUTE** | Y | 400 |
| mal_user_cleanup | DELETE | /api/user/{{malUser.userId}} | User.Delete | Y | 200 |
| mal_holon_seed–mal_holon_cleanup_user (×24) | POST/DELETE | /api/user, /api/holon | User/Holon | Mix Y/N | 200/400/404 |
| mal_bundle_seed–mal_bundle_cleanup_user (×10) | POST/DELETE | /api/user, /api/actiongraphbundle | User/ActionGraphBundle | Mix Y/N | 200/400/404 |
| mal_bc_seed–mal_bc_cleanup_user (×21) | POST/GET/DELETE | /api/user, /api/blockchainoperation | User/BlockchainOp | Mix Y/N | 200/400/401/404 |
| mal_nosql_* (×3) | POST | /api/user/register or /login | Register/Login | N | 400 |
| *(all remaining: cmd, ldap, mass-assign, deep-nest, ssrf, crlf, format-string, log-inject, proto-pollute, unicode, path, template, xml — ×20+)* | POST | /api/user/register or /api/holon | Register/Holon | Mix N/Y | 400 or 404 |

### QA-EdgeCases.jsonl (86 cases)

> All auth-requiring cases have valid `Authorization: Bearer {{qaAuth.token}}` / `{{qaHAuth.token}}` / `{{qaSAuth.token}}` / `{{qaBAuth.token}}`.

| Group | Cases | Paths | Auth? | expectedStatus |
|---|---|---|---|---|
| User seed/login | 2 | /api/user/register, /login | N | 200 |
| User boundary (missing fields/bad email) | 6 | /api/user/register, /login | N | 400 or 401 |
| User GET/UPDATE (auth OK) | 8 | /api/user/{id} | Y | 200 or 404 |
| Auth boundary tests | 2 | /api/user/{id} | N or Y (invalid) | 401 |
| Wallet edge cases | 5 | /api/user/{{qaUser.userId}}/wallets | Y | 200 or 400 or 404 |
| Holon seed/login | 2 | /api/user/register, /login | N | 200 |
| Holon edge cases (create/get/update/interact/delete) | 14 | /api/holon | Y | 200 or 400 or 404 |
| Bundle seed/login | 2 | /api/user/register, /login | N | 200 |
| Action graph bundle edge cases | 10 | /api/actiongraphbundle | Y | 200 or 400 or 404 |
| Blockchain seed/login | 2 | /api/user/register, /login | N | 200 |
| Blockchain edge cases | 4 | /api/blockchainoperation | Y | 200 or 404 |
| Extended holon (seed/login + rich tests) | 17 | /api/holon (activate, peers, query combos, interact) | Y | 200 |
| Extended bundle (seed/login + update/gen) | 8 | /api/actiongraphbundle | Y | 200 |
| Extended blockchain (seed/wallet/holon/mint/exchange edge) | 10 | /api/user/*/wallets (MISSING ROUTE), /api/holon, /api/blockchainoperation | Y | 200 or 400 |
| Misc boundary (dup username, email case, IsActive, empty string, long name, wallet default) | 8 | /api/user/{id}, /api/user/*/wallets | Y | 200 or 400 or 404 |
| Cleanup (×5) | 5 | /api/user/{id}, /api/holon/{id}, etc | Y | 200 |

### Frontend.jsonl (9 cases) — NOT EDITABLE BY E2

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| fe_root | GET | http://localhost:3000/ | Frontend (Next.js) | N | 2xx |
| fe_login_page | GET | http://localhost:3000/login | Frontend | N | 2xx |
| fe_register_page | GET | http://localhost:3000/register | Frontend | N | 2xx |
| fe_overview | GET | http://localhost:3000/overview | Frontend | N | 2xx |
| fe_users | GET | http://localhost:3000/users | Frontend | N | 2xx |
| fe_wallets | GET | http://localhost:3000/wallets | Frontend | N | 2xx |
| fe_holons | GET | http://localhost:3000/holons | Frontend | N | 2xx |
| fe_api_keys | GET | http://localhost:3000/api-keys | Frontend | N | 2xx |
| fe_404 | GET | http://localhost:3000/__does-not-exist__ | Frontend | N | 404 |

> **Note**: All Frontend.jsonl paths target `http://localhost:3000` (Next.js), NOT the API BaseUrl. Regression gate (FR-8 / IR-4). E2 MUST NOT touch this file.

### Stress_RapidOperations.jsonl (1024 cases) — NOT EDITABLE BY E2

> 1024 JSON cases: seed_user (register+login), 10 rapid holon creates (`stress_holon_1–10`), then ~1000 rapid holon PUT updates (`stress_update_1–N`) on the same holons. All use `Authorization: Bearer {{stressAuth.token}}`. All `expectedStatus: 200`. W1-B1 owns this file exclusively.

---

## Table 3 — Mismatch Table (auth required, no Authorization header in happy-path 2xx cases)

> These are cases where: (a) `expectedStatus` is `200` or `2xx`, AND (b) the target action requires `[Authorize]`, AND (c) the case either has no `Authorization` header or has an unresolved `{{...}}` token because the upstream login extraction failed due to missing `_suiteVars` + suite prefix collision.

| Suite | CaseId | Path | Required Scheme | Fix Prescription |
|---|---|---|---|---|
| UserController.jsonl | `add_wallet` | /api/user/{{user1.userId}}/wallets | **MISSING ROUTE** (structural, not auth gap) | E2 cannot fix — route does not exist in any controller |
| UserController.jsonl | `get_wallets` | /api/user/{{user1.userId}}/wallets | **MISSING ROUTE** | Same — API-level fix required |
| UserController.jsonl | `remove_wallet` | /api/user/{{user1.userId}}/wallets/{{wallet1.walletId}} | **MISSING ROUTE** | Same |
| UserController_QA.jsonl | `add_algorand_wallet` | /api/user/{{minimalUser.id}}/wallets | **MISSING ROUTE** | Same |
| UserController_QA.jsonl | `add_solana_wallet` | /api/user/{{minimalUser.id}}/wallets | **MISSING ROUTE** | Same |
| UserController_QA.jsonl | `add_second_algorand_wallet` | /api/user/{{minimalUser.id}}/wallets | **MISSING ROUTE** | Same |
| UserController_QA.jsonl | `get_wallets` | /api/user/{{minimalUser.id}}/wallets | **MISSING ROUTE** | Same |
| UserController_QA.jsonl | `remove_solana_wallet` | /api/user/{{minimalUser.id}}/wallets/{walletId} | **MISSING ROUTE** | Same |
| UserController_QA.jsonl | `remove_nonexistent_wallet` | /api/user/{{minimalUser.id}}/wallets/00000000-…-000 | **MISSING ROUTE** | Same |
| UserController_QA.jsonl | `add_wallet_to_other_user` | /api/user/{{fullUser.id}}/wallets | **MISSING ROUTE** | Same |
| BlockchainOperationController.jsonl | `seed_wallet` | /api/user/{{buser.userId}}/wallets | **MISSING ROUTE** | Same |
| BlockchainOperationController.jsonl | `cleanup_wallet` | /api/user/{{buser.userId}}/wallets/{{bwallet.walletId}} | **MISSING ROUTE** | Same |
| BlockchainOperationController_QA.jsonl | `add_wallet` | /api/user/{{bcUser.id}}/wallets | **MISSING ROUTE** | Same |
| BlockchainOperationController_QA.jsonl | `cleanup_wallet` | /api/user/{{bcUser.id}}/wallets/{{bcWallet.walletId}} | **MISSING ROUTE** | Same |
| Blockchain_Devnet.jsonl | `algo_add_wallet` | /api/user/{{algoUser.id}}/wallets | **MISSING ROUTE** | Same |
| Blockchain_Devnet.jsonl | `sol_add_wallet` | /api/user/{{solUser.id}}/wallets | **MISSING ROUTE** | Same |
| Blockchain_Devnet.jsonl | `algo_cleanup_wallet` | /api/user/{{algoUser.id}}/wallets/{walletId} | **MISSING ROUTE** | Same |
| Blockchain_Devnet.jsonl | `sol_cleanup_wallet` | /api/user/{{solUser.id}}/wallets/{walletId} | **MISSING ROUTE** | Same |
| E2E-Flows.jsonl | `e2e1_add_wallet` | /api/user/{{e2e1User.userId}}/wallets | **MISSING ROUTE** | Same |
| E2E-Flows.jsonl | `e2e1_remove_wallet` | /api/user/{{e2e1User.userId}}/wallets/{walletId} | **MISSING ROUTE** | Same |
| E2E-Flows.jsonl | `e2e4_add_wallet` | /api/user/{{e2e4User.userId}}/wallets | **MISSING ROUTE** | Same |
| E2E-Flows.jsonl | `e2e4_remove_wallet` | /api/user/{{e2e4User.userId}}/wallets/{walletId} | **MISSING ROUTE** | Same |
| E2E-Flows.jsonl | `e2e10_wallet_1–3` (×3) | /api/user/{{e2e10User.userId}}/wallets | **MISSING ROUTE** | Same |
| CrossController_E2E.jsonl | `e2e1_add_algo_wallet` | /api/user/{{e2eUser.id}}/wallets | **MISSING ROUTE** | Same |
| CrossController_E2E.jsonl | `e2e1_cleanup_wallet` | /api/user/{{e2eUser.id}}/wallets/{walletId} | **MISSING ROUTE** | Same |
| MaliciousPayloads.jsonl | `mal_wallet_xss_address` | /api/user/{{malUser.userId}}/wallets | **MISSING ROUTE** | Same |
| MaliciousPayloads.jsonl | `mal_wallet_sqli_label` | /api/user/{{malUser.userId}}/wallets | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_wallet_add_missing_chain` | /api/user/{{qaUser.userId}}/wallets | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_wallet_add_missing_address` | /api/user/{{qaUser.userId}}/wallets | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_wallet_add_valid` | /api/user/{{qaUser.userId}}/wallets | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_wallet_remove_random` | /api/user/{{qaUser.userId}}/wallets/22222222-…-222 | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_wallet_remove_valid` | /api/user/{{qaUser.userId}}/wallets/{{qaWallet.walletId}} | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_wallet_default_true` | /api/user/{{qaUser.userId}}/wallets | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_wallet_default_false` | /api/user/{{qaUser.userId}}/wallets | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_wallet_cleanup_default` | /api/user/{{qaUser.userId}}/wallets/{{qaWallet2.walletId}} | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_bc2_add_wallet` | /api/user/{{qaB2User.userId}}/wallets | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_bc2_cleanup_wallet` | /api/user/{{qaB2User.userId}}/wallets/{{qaB2Wallet.walletId}} | **MISSING ROUTE** | Same |

**Critical structural finding**: `POST /api/user/{id}/wallets` and `DELETE /api/user/{id}/wallets/{walletId}` are called in **at least 37 cases across 9 suites** but no such route exists in UserController or any other controller. The wallet CRUD is routed through `WalletController` at `/api/wallet` (no user-scoped sub-path). This is an API routing gap, not an auth-header gap — E2 **cannot fix these by adding auth headers**. Fix prescription for W4-F1: implement the user-scoped wallet endpoints in UserController (or route via WalletController using user claims).

**True auth-header-only mismatches** (cases that hit real routes but lack `Authorization`):

| Suite | CaseId | Path | Required Scheme | Fix Prescription |
|---|---|---|---|---|
| HolonController_QA.jsonl | `isolation_unauth_create` | POST /api/holon | MultiScheme | INTENTIONAL — expects 401; do NOT add auth |
| MaliciousPayloads.jsonl | `mal_mass_holon` | POST /api/holon | MultiScheme | Uses `{{malHAuth.token}}` which IS present; no fix needed if upstream login succeeded |

> **Auth-not-set-up root cause** (spec's "401 auth-not-set-up ~155 cases"): The primary driver is cross-suite email collision. When `register_user` in one suite registers `live@test.azoa` and a second suite also registers `live@test.azoa`, the second gets 400 and the login token is never extracted — all downstream `{{auth1.token}}` substitutions remain as literal `{{...}}` strings. The per-suite `_suiteVars` + `{{suitePrefix}}` mechanism (W2-E1 + W3-E2) is the fix. Once each suite uses `{{suitePrefix}}_live@test.azoa`, collision is eliminated and all downstream auth headers resolve correctly.

---

## Table 4 — Intentional 401 Cases (DO NOT EDIT — IR-1 + R-3)

These cases exist to verify rejection. E2 MUST NOT add `Authorization` headers to these cases.

| Suite | CaseId | What it tests | Why intentional |
|---|---|---|---|
| UserController_QA.jsonl | `get_without_auth` | GET /api/user/{id} with no auth header | Verifies [Authorize] blocks unauthenticated GET |
| UserController_QA.jsonl | `get_with_malformed_auth` | GET /api/user/{id} with `Bearer invalid.token.here` | Verifies token signature validation rejects bad JWTs |
| UserController_Malicious.jsonl | `sqli_login_email` | POST /api/user/login with SQLi email → 401 | Verifies login rejects SQLi without granting access |
| UserController_Malicious.jsonl | `sqli_login_password` | POST /api/user/login with SQLi password → 401 | Verifies login rejects SQLi password without granting access |
| UserController_Malicious.jsonl | `header_injection_auth` | GET /api/user/… with `Bearer token\r\nX-Injected: evil` → 401 | Verifies CRLF-injected auth is rejected |
| HolonController_QA.jsonl | `isolation_unauth_create` | POST /api/holon with no auth → 401 | Verifies class-level [Authorize] blocks unauthenticated Holon.Create |
| BlockchainOperationController_QA.jsonl | `get_op_unauthorized` | GET /api/blockchainoperation/{id} with no auth → 401 | Verifies [Authorize] on BlockchainOp.Get |
| BlockchainOperationController_QA.jsonl | `get_ops_by_user_unauthorized` | GET /api/blockchainoperation/user/{id} with no auth → 401 | Verifies [Authorize] on BlockchainOp.GetByUser |
| BlockchainOperationController_Malicious.jsonl | `get_op_no_auth` | GET /api/blockchainoperation/{id} with no auth → 401 | Auth bypass probe — rejects correctly |
| BlockchainOperationController_Malicious.jsonl | `get_by_user_no_auth` | GET /api/blockchainoperation/user/{id} with no auth → 401 | Auth bypass probe — rejects correctly |
| BlockchainOperationController_Malicious.jsonl | `get_op_empty_auth` | GET /api/blockchainoperation/{id} with `Authorization: ""` → 401 | Verifies empty auth header rejected |
| BlockchainOperationController_Malicious.jsonl | `get_op_bearer_only` | GET /api/blockchainoperation/{id} with `Bearer ` only → 401 | Verifies empty token after `Bearer ` rejected |
| BlockchainOperationController_Malicious.jsonl | `get_op_tampered_token` | GET /api/blockchainoperation/user/{id} with tampered JWT → 401 | Verifies signature validation rejects forged JWT |
| BlockchainOperationController_Malicious.jsonl | `header_injection_op` | GET /api/blockchainoperation/user/{id} with CRLF-injected auth → 401 | Verifies CRLF injection in auth header rejected |
| ActionGraphBundleController_QA.jsonl | `get_odk_unauthorized` | GET /api/actiongraphbundle/{id} with no auth → 401 | Verifies [Authorize] on ActionGraphBundle.Get |
| ActionGraphBundleController_QA.jsonl | `create_odk_unauthorized` | POST /api/actiongraphbundle with no auth → 401 | Verifies [Authorize] on ActionGraphBundle.CreateOrUpdate |
| ActionGraphBundleController_QA.jsonl | `delete_odk_unauthorized` | DELETE /api/actiongraphbundle/{id} with no auth → 401 | Verifies [Authorize] on ActionGraphBundle.Delete |
| ActionGraphBundleController_QA.jsonl | `generate_odk_unauthorized` | POST /api/actiongraphbundle/{id}/generate with no auth → 401 | Verifies [Authorize] on ActionGraphBundle.Generate |
| ActionGraphBundleController_Malicious.jsonl | `get_odk_unauth` | GET /api/actiongraphbundle/{id} with no auth → 401 | Auth bypass probe — attack surface verification |
| ActionGraphBundleController_Malicious.jsonl | `create_odk_unauth` | POST /api/actiongraphbundle with no auth → 401 | Auth bypass probe |
| ActionGraphBundleController_Malicious.jsonl | `generate_odk_unauth` | POST /api/actiongraphbundle/{id}/generate with no auth → 401 | Auth bypass probe |
| ActionGraphBundleController_Malicious.jsonl | `deploy_odk_unauth` | POST /api/actiongraphbundle/{id}/deploy with no auth → 401 | Auth bypass probe |
| ActionGraphBundleController_Malicious.jsonl | `delete_odk_unauth` | DELETE /api/actiongraphbundle/{id} with no auth → 401 | Auth bypass probe |
| MaliciousPayloads.jsonl | `mal_auth_no_bearer` | GET /api/user/{id} with `Authorization: {{malAuth.token}}` (no Bearer prefix) → 401 | Verifies scheme prefix is required |
| MaliciousPayloads.jsonl | `mal_auth_expired_format` | GET /api/user/{id} with valid-shaped-but-unsigned JWT → 401 | Verifies forged-signature rejection |
| MaliciousPayloads.jsonl | `mal_auth_empty_token` | GET /api/user/{id} with `Bearer ` → 401 | Verifies empty token rejected |
| MaliciousPayloads.jsonl | `mal_bc_by_user_sqli`–`mal_bc_get_sqli` | GET with SQLi in path → 404 (via routing) | Path routing + auth: no auth + SQLi in path |
| QA-EdgeCases.jsonl | `qa_get_no_auth` | GET /api/user/{id} with `headers:{}` → 401 | Standard auth gate verification |
| QA-EdgeCases.jsonl | `qa_get_bad_auth` | GET /api/user/{id} with `Bearer invalid.token.here` → 401 | Malformed JWT rejected |
| QA-EdgeCases.jsonl | `qa_login_wrong_pass` | POST /api/user/login with wrong password → 401 | Business logic: wrong credential rejected |
| QA-EdgeCases.jsonl | `qa_login_missing_email` | POST /api/user/login with nonexistent email → 401 | Business logic: unknown user rejected |

---

## Cross-cutting Observations

1. **`UserController.Login` is the unique auth-chain entry point**: every suite that calls any `[Authorize]` endpoint depends on `POST /api/user/login` returning `AZOAResult<string>` with the JWT in `result` (not `result.token`). The extract `{"token":"result"}` is correct. Any upstream failure (e.g., duplicate email collision from missing `suitePrefix`) cascades to all downstream `{{...token}}` cases as Inconclusive/401.

2. **Missing route `/api/user/{id}/wallets`**: Approximately 37+ cases across 9 suites reference this route, which does not exist. This is the single largest structural issue beyond auth-header gaps. W4-F1 needs to add this route (either in UserController as nested methods delegating to WalletManager, or via a redirect to WalletController). E2 cannot address this.

3. **`AllowAnonymous` endpoints (4 total)**: `User.Register`, `User.Login`, `Nft.GetMetadata`, `Network.Get`. No suites attempt to add auth headers to these; correctly omitted.

4. **NetworkController is class-`[AllowAnonymous]`**: Its single action also has `[AllowAnonymous]` — redundant but correct. No suite tests this controller directly.

5. **Suite identity collision** (spec R-2): Every suite that registers a user uses hardcoded emails like `live@test.azoa`, `holon@test.azoa`, etc. Without `_suiteVars` + `{{suitePrefix}}` (W2-E1 + W3-E2), parallel runs collide. The `suitePrefix` fix in E2 is the correct mitigation.

6. **QA/Malicious 401 case count**: Counting Table 4 entries across the 9 editable `_QA` and `_Malicious` suites gives ~30 explicit 401 cases. Together with ~155 spec-predicted auth-not-set-up cases (from suite collision), the total resolved 401 exposure is large. Post-E2, the 30 intentional 401s should remain; the ~155 collision-driven 401s should resolve to 200.

7. **DappCompositionController / DappSeriesController / ActionController / WalletController / BridgeController / SwapController / SearchController / UserNFTController / NftController**: None of these controllers appear in any current JSONL suite. Their auth posture is documented in Table 1 for completeness and for future suite expansion.
