# Auth-Chain Audit — W3-C1 Deliverable

**Date**: 2026-06-08  
**Branch**: `api-safety-hardening`  
**Purpose**: Input document for W3-E2 JSONL edit pass. Tables 1–4 are the source of truth E2 must use.

---

## Table 1 — Controller Actions and Auth Posture

> Legend: `[Authorize]` = class-level unless noted on the action; `[AllowAnonymous]` = overrides class `[Authorize]`; `Inherited` = action inherits class attribute; `-` = no class-level `[Authorize]`, action has its own.

| Controller | Action | HTTP Method + Route | AuthAttribute | Scheme |
|---|---|---|---|---|
| **AvatarController** | `Register` | `POST /api/avatar/register` | `[AllowAnonymous]` (action) | None |
| AvatarController | `Login` | `POST /api/avatar/login` | `[AllowAnonymous]` (action) | None |
| AvatarController | `Get` | `GET /api/avatar/{id:guid}` | `[Authorize]` (action) | MultiScheme |
| AvatarController | `GetAll` | `GET /api/avatar` | `[Authorize]` (action) | MultiScheme |
| AvatarController | `Update` | `PUT /api/avatar/{id:guid}` | `[Authorize]` (action) | MultiScheme |
| AvatarController | `Delete` | `DELETE /api/avatar/{id:guid}` | `[Authorize]` (action) | MultiScheme |
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
| BlockchainOperationController | `GetByAvatar` | `GET /api/blockchainoperation/avatar/{avatarId:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| **STARODKController** | `Get` | `GET /api/starodk/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| STARODKController | `GetAll` | `GET /api/starodk` | Inherited `[Authorize]` (class) | MultiScheme |
| STARODKController | `CreateOrUpdate` | `POST /api/starodk` | Inherited `[Authorize]` (class) | MultiScheme |
| STARODKController | `Delete` | `DELETE /api/starodk/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| STARODKController | `Generate` | `POST /api/starodk/{id:guid}/generate` | Inherited `[Authorize]` (class) | MultiScheme |
| STARODKController | `Deploy` | `POST /api/starodk/{id:guid}/deploy` | Inherited `[Authorize]` (class) | MultiScheme |
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
| **AvatarNFTController** | `MintAvatarNFT` | `POST /api/avatarnft/mint` | Inherited `[Authorize]` (class) | MultiScheme |
| AvatarNFTController | `GetAvatarNFT` | `GET /api/avatarnft/{id}` | Inherited `[Authorize]` (class) | MultiScheme |
| AvatarNFTController | `GetAvatarNFTByTokenId` | `GET /api/avatarnft/by-token/{chainType}/{contractAddress}/{tokenId}` | Inherited `[Authorize]` (class) | MultiScheme |
| AvatarNFTController | `GetAvatarNFTsByAvatar` | `GET /api/avatarnft/avatar/{avatarId}` | Inherited `[Authorize]` (class) | MultiScheme |
| AvatarNFTController | `TransferAvatarNFT` | `POST /api/avatarnft/{id}/transfer` | Inherited `[Authorize]` (class) | MultiScheme |
| AvatarNFTController | `BurnAvatarNFT` | `DELETE /api/avatarnft/{id}` | Inherited `[Authorize]` (class) | MultiScheme |
| AvatarNFTController | `BindHolonToAvatarNFT` | `POST /api/avatarnft/{avatarNFTId}/holons/{holonId}/bind` | Inherited `[Authorize]` (class) | MultiScheme |
| AvatarNFTController | `GetHolonBindings` | `GET /api/avatarnft/{avatarNFTId}/holons` | Inherited `[Authorize]` (class) | MultiScheme |
| AvatarNFTController | `UpdateHolonBinding` | `PUT /api/avatarnft/holons/{bindingId}` | Inherited `[Authorize]` (class) | MultiScheme |
| AvatarNFTController | `RemoveHolonBinding` | `DELETE /api/avatarnft/holons/{bindingId}` | Inherited `[Authorize]` (class) | MultiScheme |
| AvatarNFTController | `BindWalletToAvatarNFT` | `POST /api/avatarnft/{avatarNFTId}/wallets/{walletId}/bind` | Inherited `[Authorize]` (class) | MultiScheme |
| AvatarNFTController | `GetWalletBindings` | `GET /api/avatarnft/{avatarNFTId}/wallets` | Inherited `[Authorize]` (class) | MultiScheme |
| AvatarNFTController | `UpdateWalletBinding` | `PUT /api/avatarnft/wallets/{bindingId}` | Inherited `[Authorize]` (class) | MultiScheme |
| AvatarNFTController | `RemoveWalletBinding` | `DELETE /api/avatarnft/wallets/{bindingId}` | Inherited `[Authorize]` (class) | MultiScheme |
| AvatarNFTController | `GetAvatarNFTComposite` | `GET /api/avatarnft/{avatarNFTId}/composite` | Inherited `[Authorize]` (class) | MultiScheme |
| AvatarNFTController | `GetAvatarNFTCompositesByAvatar` | `GET /api/avatarnft/avatar/{avatarId}/composite` | Inherited `[Authorize]` (class) | MultiScheme |
| AvatarNFTController | `VerifyAvatarNFTOwnership` | `POST /api/avatarnft/verify-ownership` | Inherited `[Authorize]` (class) | MultiScheme |
| AvatarNFTController | `VerifyHolonAccess` | `POST /api/avatarnft/verify-holon-access` | Inherited `[Authorize]` (class) | MultiScheme |
| AvatarNFTController | `VerifyWalletAccess` | `POST /api/avatarnft/verify-wallet-access` | Inherited `[Authorize]` (class) | MultiScheme |
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
| BridgeController | `CompleteBridge` | `POST /api/bridge/{id}/complete` | Inherited `[Authorize]` (class) | MultiScheme |
| BridgeController | `ReverseBridge` | `POST /api/bridge/{id}/reverse` + `[EnableRateLimiting("financial")]` | Inherited `[Authorize]` (class) | MultiScheme |
| BridgeController | `GetBridgeStatus` | `GET /api/bridge/{id}` | Inherited `[Authorize]` (class) | MultiScheme |
| BridgeController | `GetHistory` | `GET /api/bridge/history` | Inherited `[Authorize]` (class) | MultiScheme |
| **QuestController** | `Create` | `POST /api/quest` | Inherited `[Authorize]` (class) | MultiScheme |
| QuestController | `Get` | `GET /api/quest/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| QuestController | `GetByAvatar` | `GET /api/quest/avatar/{avatarId:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| QuestController | `Update` | `PUT /api/quest/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| QuestController | `Delete` | `DELETE /api/quest/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| QuestController | `Validate` | `POST /api/quest/{id:guid}/validate` | Inherited `[Authorize]` (class) | MultiScheme |
| QuestController | `Execute` | `POST /api/quest/{id:guid}/execute` | Inherited `[Authorize]` (class) | MultiScheme |
| QuestController | `ExecuteNode` | `POST /api/quest/{id:guid}/nodes/{nodeId:guid}/execute` | Inherited `[Authorize]` (class) | MultiScheme |
| QuestController | `Fork` | `POST /api/quest/runs/{runId:guid}/fork` | Inherited `[Authorize]` (class) | MultiScheme |
| QuestController | `MarkRunFailed` | `POST /api/quest/runs/{runId:guid}/mark-failed` | Inherited `[Authorize]` (class) | MultiScheme |
| QuestController | `CreateTemplate` | `POST /api/quest/templates` | Inherited `[Authorize]` (class) | MultiScheme |
| QuestController | `GetTemplate` | `GET /api/quest/templates/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| QuestController | `ListTemplates` | `GET /api/quest/templates` | Inherited `[Authorize]` (class) | MultiScheme |
| QuestController | `InstantiateTemplate` | `POST /api/quest/templates/{id:guid}/instantiate` | Inherited `[Authorize]` (class) | MultiScheme |
| QuestController | `CreateNodeTemplate` | `POST /api/quest/node-templates` | Inherited `[Authorize]` (class) | MultiScheme |
| QuestController | `ListNodeTemplates` | `GET /api/quest/node-templates` | Inherited `[Authorize]` (class) | MultiScheme |
| **DappSeriesController** | `List` | `GET /api/dapp-series` | Inherited `[Authorize]` (class) | MultiScheme |
| DappSeriesController | `Get` | `GET /api/dapp-series/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| DappSeriesController | `Create` | `POST /api/dapp-series` | Inherited `[Authorize]` (class) | MultiScheme |
| DappSeriesController | `Update` | `PUT /api/dapp-series/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| DappSeriesController | `Delete` | `DELETE /api/dapp-series/{id:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| DappSeriesController | `ListQuests` | `GET /api/dapp-series/{seriesId:guid}/quests` | Inherited `[Authorize]` (class) | MultiScheme |
| DappSeriesController | `AddQuest` | `POST /api/dapp-series/{seriesId:guid}/quests` | Inherited `[Authorize]` (class) | MultiScheme |
| DappSeriesController | `RemoveQuest` | `DELETE /api/dapp-series/{seriesId:guid}/quests/{questId:guid}` | Inherited `[Authorize]` (class) | MultiScheme |
| DappSeriesController | `ReorderQuest` | `PUT /api/dapp-series/{seriesId:guid}/quests/{questId:guid}/order` | Inherited `[Authorize]` (class) | MultiScheme |
| DappSeriesController | `UpdateMappings` | `PUT /api/dapp-series/{seriesId:guid}/quests/{questId:guid}/mappings` | Inherited `[Authorize]` (class) | MultiScheme |
| **DappCompositionController** | `Compose` | `POST /api/dapp-series/{id:guid}/compose` | Inherited `[Authorize]` (class) | MultiScheme |
| DappCompositionController | `Validate` | `GET /api/dapp-series/{id:guid}/validate` | Inherited `[Authorize]` (class) | MultiScheme |
| DappCompositionController | `GetManifest` | `GET /api/dapp-series/{id:guid}/manifest` | Inherited `[Authorize]` (class) | MultiScheme |
| DappCompositionController | `Generate` | `POST /api/dapp-series/{id:guid}/generate` | Inherited `[Authorize]` (class) | MultiScheme |
| DappCompositionController | `Deploy` | `POST /api/dapp-series/{id:guid}/deploy` | Inherited `[Authorize]` (class) | MultiScheme |
| DappCompositionController | `GetStatus` | `GET /api/dapp-series/{id:guid}/status` | Inherited `[Authorize]` (class) | MultiScheme |

**AllowAnonymous summary (3 total):**
- `AvatarController.Register` — POST /api/avatar/register
- `AvatarController.Login` — POST /api/avatar/login  
- `NftController.GetMetadata` — GET /api/nft/{id}/metadata
- `NetworkController` (class-level `[AllowAnonymous]`) — GET /api/network

> Note: `POST /api/avatar/{id}/wallets` and `DELETE /api/avatar/{id}/wallets/{walletId}` are referenced in many JSONL suites but **do not exist as routes in any controller**. This is a harness misconfiguration — these calls will 404. E2 cannot fix this by adding auth headers; the API itself is missing these endpoints. They are flagged in Table 3.

---

## Table 2 — JSONL Case Map

> Abbreviations: Auth header present = `Y` / `N`; `expectedStatus` = HTTP status or range; `→ controller` = matched action.
> Suites marked `[NOT EDITABLE BY E2]` must not be modified.

### AvatarController.jsonl (11 cases)

| CaseId | Method | Path (resolved) | Matched Action | Auth Header? | expectedStatus |
|---|---|---|---|---|---|
| register_avatar | POST | /api/avatar/register | Avatar.Register | N | 200 |
| register_duplicate | POST | /api/avatar/register | Avatar.Register | N | 400 |
| login_avatar | POST | /api/avatar/login | Avatar.Login | N | 200 |
| get_avatar | GET | /api/avatar/{{avatar1.avatarId}} | Avatar.Get | Y | 200 |
| get_all_avatars | GET | /api/avatar | Avatar.GetAll | Y | 200 |
| update_avatar | PUT | /api/avatar/{{avatar1.avatarId}} | Avatar.Update | Y | 200 |
| add_wallet | POST | /api/avatar/{{avatar1.avatarId}}/wallets | **MISSING ROUTE** | Y | 200 |
| get_wallets | GET | /api/avatar/{{avatar1.avatarId}}/wallets | **MISSING ROUTE** | Y | 200 |
| remove_wallet | DELETE | /api/avatar/{{avatar1.avatarId}}/wallets/{{wallet1.walletId}} | **MISSING ROUTE** | Y | 200 |
| delete_avatar | DELETE | /api/avatar/{{avatar1.avatarId}} | Avatar.Delete | Y | 200 |
| get_deleted_avatar | GET | /api/avatar/{{avatar1.avatarId}} | Avatar.Get | Y | 404 |

### AvatarController_QA.jsonl (33 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| register_minimal | POST | /api/avatar/register | Avatar.Register | N | 200 |
| register_full_profile | POST | /api/avatar/register | Avatar.Register | N | 200 |
| register_unicode_username | POST | /api/avatar/register | Avatar.Register | N | 200 |
| register_special_chars_email | POST | /api/avatar/register | Avatar.Register | N | 200 |
| register_long_username | POST | /api/avatar/register | Avatar.Register | N | 200 |
| register_duplicate_username | POST | /api/avatar/register | Avatar.Register | N | 200 |
| register_duplicate_email | POST | /api/avatar/register | Avatar.Register | N | 400 |
| login_minimal | POST | /api/avatar/login | Avatar.Login | N | 200 |
| login_full | POST | /api/avatar/login | Avatar.Login | N | 200 |
| login_unicode | POST | /api/avatar/login | Avatar.Login | N | 200 |
| login_wrong_password | POST | /api/avatar/login | Avatar.Login | N | 401 |
| login_nonexistent | POST | /api/avatar/login | Avatar.Login | N | 401 |
| login_empty_body | POST | /api/avatar/login | Avatar.Login | N | 400 |
| get_minimal_avatar | GET | /api/avatar/{{minimalAvatar.id}} | Avatar.Get | Y | 200 |
| get_full_avatar | GET | /api/avatar/{{fullAvatar.id}} | Avatar.Get | Y | 200 |
| get_nonexistent_avatar | GET | /api/avatar/00000000-…-000 | Avatar.Get | Y | 404 |
| get_all_avatars | GET | /api/avatar | Avatar.GetAll | Y | 200 |
| update_title | PUT | /api/avatar/{{minimalAvatar.id}} | Avatar.Update | Y | 200 |
| update_email | PUT | /api/avatar/{{minimalAvatar.id}} | Avatar.Update | Y | 200 |
| update_multiple_fields | PUT | /api/avatar/{{fullAvatar.id}} | Avatar.Update | Y | 200 |
| update_nonexistent | PUT | /api/avatar/00000000-…-000 | Avatar.Update | Y | 404 |
| add_algorand_wallet | POST | /api/avatar/{{minimalAvatar.id}}/wallets | **MISSING ROUTE** | Y | 200 |
| add_solana_wallet | POST | /api/avatar/{{minimalAvatar.id}}/wallets | **MISSING ROUTE** | Y | 200 |
| add_second_algorand_wallet | POST | /api/avatar/{{minimalAvatar.id}}/wallets | **MISSING ROUTE** | Y | 200 |
| get_wallets | GET | /api/avatar/{{minimalAvatar.id}}/wallets | **MISSING ROUTE** | Y | 200 |
| remove_solana_wallet | DELETE | /api/avatar/{{minimalAvatar.id}}/wallets/{{solWallet.walletId}} | **MISSING ROUTE** | Y | 200 |
| remove_nonexistent_wallet | DELETE | /api/avatar/{{minimalAvatar.id}}/wallets/00000000-…-000 | **MISSING ROUTE** | Y | 404 |
| add_wallet_to_other_avatar | POST | /api/avatar/{{fullAvatar.id}}/wallets | **MISSING ROUTE** | Y | 200 |
| get_without_auth | GET | /api/avatar/{{minimalAvatar.id}} | Avatar.Get | N | 401 |
| get_with_malformed_auth | GET | /api/avatar/{{minimalAvatar.id}} | Avatar.Get | Y (invalid) | 401 |
| delete_minimal_avatar | DELETE | /api/avatar/{{minimalAvatar.id}} | Avatar.Delete | Y | 200 |
| delete_full_avatar | DELETE | /api/avatar/{{fullAvatar.id}} | Avatar.Delete | Y | 200 |
| delete_unicode_avatar | DELETE | /api/avatar/{{unicodeAvatar.id}} | Avatar.Delete | Y | 200 |
| verify_deleted | GET | /api/avatar/{{minimalAvatar.id}} | Avatar.Get | Y | 404 |
| delete_nonexistent | DELETE | /api/avatar/00000000-…-000 | Avatar.Delete | Y | 404 |

### AvatarController_Malicious.jsonl (46 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| sqli_username | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| sqli_email | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| sqli_password | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| sqli_login_email | POST | /api/avatar/login | Avatar.Login | N | 401 |
| sqli_login_password | POST | /api/avatar/login | Avatar.Login | N | 401 |
| sqli_blind_union | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| xss_username | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| xss_email | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| xss_title | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| xss_firstname | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| xss_encoded | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| oversized_username_10k | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| oversized_email_1k | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| oversized_title | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| null_username | POST | /api/avatar/register | Avatar.Register | N | 4xx |
| numeric_username | POST | /api/avatar/register | Avatar.Register | N | 4xx |
| array_email | POST | /api/avatar/register | Avatar.Register | N | 4xx |
| boolean_password | POST | /api/avatar/register | Avatar.Register | N | 4xx |
| object_instead_of_string | POST | /api/avatar/register | Avatar.Register | N | 4xx |
| empty_json | POST | /api/avatar/register | Avatar.Register | N | 4xx |
| missing_required | POST | /api/avatar/register | Avatar.Register | N | 4xx |
| path_traversal_get | GET | /api/avatar/../../../etc/passwd | (404 routing) | Y (invalid) | 404 |
| null_byte_injection | GET | /api/avatar/550e8400…%00 | (404 routing) | Y (invalid) | 404 |
| double_encoding | GET | /api/avatar/%25550e8400… | (404 routing) | Y (invalid) | 404 |
| rtl_override | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| zero_width_chars | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| emoji_username | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| control_chars | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| bidi_attack | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| deeply_nested_body | POST | /api/avatar/register | Avatar.Register | N | 4xx |
| massive_array | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| header_injection_content_type | POST | /api/avatar/register | Avatar.Register | N | 4xx |
| header_injection_auth | GET | /api/avatar/00000000-…-000 | Avatar.Get | Y (injected) | 401 |
| rapid_register_1–5 (×5) | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| negative_karma_if_exposed | POST | /api/avatar/register | Avatar.Register | N | 2xx |
| login_negative_avatar | POST | /api/avatar/login | Avatar.Login | N | 200 |
| cleanup_negative | DELETE | /api/avatar/{{negAvatar.id}} | Avatar.Delete | Y | 200 |

### HolonController.jsonl (9 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| seed_avatar | POST | /api/avatar/register | Avatar.Register | N | 200 |
| login_seed | POST | /api/avatar/login | Avatar.Login | N | 200 |
| create_holon | POST | /api/holon | Holon.Create | Y | 200 |
| get_holon | GET | /api/holon/{{holon1.holonId}} | Holon.Get | Y | 200 |
| query_holons | GET | /api/holon?name=LiveHolon | Holon.Query | Y | 200 |
| update_holon | PUT | /api/holon/{{holon1.holonId}} | Holon.Update | Y | 200 |
| interact_holon | POST | /api/holon/{{holon1.holonId}}/interact | Holon.Interact | Y | 200 |
| delete_holon | DELETE | /api/holon/{{holon1.holonId}} | Holon.Delete | Y | 200 |
| cleanup_avatar | DELETE | /api/avatar/{{havatar.avatarId}} | Avatar.Delete | Y | 200 |

### HolonController_QA.jsonl (38 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| seed_avatar_a | POST | /api/avatar/register | Avatar.Register | N | 200 |
| login_avatar_a | POST | /api/avatar/login | Avatar.Login | N | 200 |
| seed_avatar_b | POST | /api/avatar/register | Avatar.Register | N | 200 |
| login_avatar_b | POST | /api/avatar/login | Avatar.Login | N | 200 |
| create_root_holon | POST | /api/holon | Holon.Create | Y | 200 |
| create_child_holon | POST | /api/holon | Holon.Create | Y | 200 |
| create_peer_holon | POST | /api/holon | Holon.Create | Y | 200 |
| create_chain_holon | POST | /api/holon | Holon.Create | Y | 200 |
| get_root_holon | GET | /api/holon/{{rootHolon.id}} | Holon.Get | Y | 200 |
| verify_avatar_id_set | GET | /api/holon/{{rootHolon.id}} | Holon.Get | Y | 200 |
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
| isolation_avatar_b_get_a_holon | GET | /api/holon/{{rootHolon.id}} | Holon.Get | Y (authB) | 200 |
| isolation_avatar_b_update_a_holon | PUT | /api/holon/{{rootHolon.id}} | Holon.Update | Y (authB) | 200 |
| isolation_avatar_b_delete_a_holon | DELETE | /api/holon/{{rootHolon.id}} | Holon.Delete | Y (authB) | 200 |
| isolation_unauth_create | POST | /api/holon | Holon.Create | N | 401 |
| create_b_holon | POST | /api/holon | Holon.Create | Y | 200 |
| delete_child_holon | DELETE | /api/holon/{{childHolon.id}} | Holon.Delete | Y | 200 |
| delete_peer_holon | DELETE | /api/holon/{{peerHolon.id}} | Holon.Delete | Y | 200 |
| delete_chain_holon | DELETE | /api/holon/{{chainHolon.id}} | Holon.Delete | Y | 200 |
| delete_root_holon | DELETE | /api/holon/{{rootHolon.id}} | Holon.Delete | Y | 200 |
| delete_b_holon | DELETE | /api/holon/{{bHolon.id}} | Holon.Delete | Y | 200 |
| verify_deleted_holon | GET | /api/holon/{{rootHolon.id}} | Holon.Get | Y | 404 |
| delete_nonexistent_holon | DELETE | /api/holon/00000000-…-000 | Holon.Delete | Y | 404 |
| cleanup_avatar_a | DELETE | /api/avatar/{{avatarA.id}} | Avatar.Delete | Y | 200 |
| cleanup_avatar_b | DELETE | /api/avatar/{{avatarB.id}} | Avatar.Delete | Y | 200 |
| *(3 additional — rapid_register etc.)* | | | | | |

### HolonController_Malicious.jsonl (35 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| seed_target | POST | /api/avatar/register | Avatar.Register | N | 200 |
| login_target | POST | /api/avatar/login | Avatar.Login | N | 200 |
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
| cleanup_avatar | DELETE | /api/avatar/{{malAvatar.id}} | Avatar.Delete | Y | 200 |

### BlockchainOperationController.jsonl (6 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| seed_avatar | POST | /api/avatar/register | Avatar.Register | N | 200 |
| login_seed | POST | /api/avatar/login | Avatar.Login | N | 200 |
| seed_wallet | POST | /api/avatar/{{bavatar.avatarId}}/wallets | **MISSING ROUTE** | Y | 200 |
| get_operations_by_avatar | GET | /api/blockchainoperation/avatar/{{bavatar.avatarId}} | BlockchainOp.GetByAvatar | Y | 200 |
| cleanup_wallet | DELETE | /api/avatar/{{bavatar.avatarId}}/wallets/{{bwallet.walletId}} | **MISSING ROUTE** | Y | 200 |
| cleanup_avatar | DELETE | /api/avatar/{{bavatar.avatarId}} | Avatar.Delete | Y | 200 |

### BlockchainOperationController_QA.jsonl (9 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| seed_avatar | POST | /api/avatar/register | Avatar.Register | N | 200 |
| login_avatar | POST | /api/avatar/login | Avatar.Login | N | 200 |
| add_wallet | POST | /api/avatar/{{bcAvatar.id}}/wallets | **MISSING ROUTE** | Y | 200 |
| get_ops_by_avatar_empty | GET | /api/blockchainoperation/avatar/{{bcAvatar.id}} | BlockchainOp.GetByAvatar | Y | 200 |
| get_op_by_id_nonexistent | GET | /api/blockchainoperation/00000000-…-000 | BlockchainOp.Get | Y | 404 |
| get_op_unauthorized | GET | /api/blockchainoperation/00000000-…-000 | BlockchainOp.Get | N | 401 |
| get_ops_by_avatar_unauthorized | GET | /api/blockchainoperation/avatar/{{bcAvatar.id}} | BlockchainOp.GetByAvatar | N | 401 |
| get_ops_wrong_avatar_id | GET | /api/blockchainoperation/avatar/not-a-guid | (404) | Y | 404 |
| cleanup_wallet | DELETE | /api/avatar/{{bcAvatar.id}}/wallets/{{bcWallet.walletId}} | **MISSING ROUTE** | Y | 200 |
| cleanup_avatar | DELETE | /api/avatar/{{bcAvatar.id}} | Avatar.Delete | Y | 200 |

### BlockchainOperationController_Malicious.jsonl (21 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| seed_avatar | POST | /api/avatar/register | Avatar.Register | N | 200 |
| login_avatar | POST | /api/avatar/login | Avatar.Login | N | 200 |
| get_op_sqli_guid | GET | /api/blockchainoperation/' OR '1'='1 | (404) | Y | 404 |
| get_op_path_traversal | GET | /api/blockchainoperation/../… | (404) | Y | 404 |
| get_op_null_byte | GET | /api/blockchainoperation/550e8400…%00 | (404) | Y | 404 |
| get_op_negative_numbers | GET | /api/blockchainoperation/-50e8400… | (404) | Y | 404 |
| get_by_avatar_sqli | GET | /api/blockchainoperation/avatar/' UNION… | (404) | Y | 404 |
| get_by_avatar_path_traversal | GET | /api/blockchainoperation/avatar/../../… | (404) | Y | 404 |
| get_by_avatar_xss | GET | /api/blockchainoperation/avatar/<script>… | (404) | Y | 404 |
| get_by_avatar_very_long | GET | /api/blockchainoperation/avatar/AAA… | (404) | Y | 404 |
| get_op_no_auth | GET | /api/blockchainoperation/00000000-…-000 | BlockchainOp.Get | N | 401 |
| get_by_avatar_no_auth | GET | /api/blockchainoperation/avatar/{{bcMalAvatar.id}} | BlockchainOp.GetByAvatar | N | 401 |
| get_op_empty_auth | GET | /api/blockchainoperation/00000000-…-000 | BlockchainOp.Get | Y (empty) | 401 |
| get_op_bearer_only | GET | /api/blockchainoperation/00000000-…-000 | BlockchainOp.Get | Y (Bearer only) | 401 |
| get_op_tampered_token | GET | /api/blockchainoperation/avatar/{{bcMalAvatar.id}} | BlockchainOp.GetByAvatar | Y (tampered) | 401 |
| header_injection_op | GET | /api/blockchainoperation/avatar/{{bcMalAvatar.id}} | BlockchainOp.GetByAvatar | Y (injected) | 401 |
| get_op_sqli_provider_type | GET | /api/blockchainoperation/avatar/{{bcMalAvatar.id}}?providerType=… | BlockchainOp.GetByAvatar | Y | 2xx |
| get_op_xss_provider_type | GET | /api/blockchainoperation/avatar/{{bcMalAvatar.id}}?providerType=… | BlockchainOp.GetByAvatar | Y | 2xx |
| get_op_very_long_query | GET | /api/blockchainoperation/avatar/{{bcMalAvatar.id}}?customProviderKeys=… | BlockchainOp.GetByAvatar | Y | 2xx |
| cleanup_avatar | DELETE | /api/avatar/{{bcMalAvatar.id}} | Avatar.Delete | Y | 200 |

### STARODKController.jsonl (8 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| seed_avatar | POST | /api/avatar/register | Avatar.Register | N | 200 |
| login_seed | POST | /api/avatar/login | Avatar.Login | N | 200 |
| create_odk | POST | /api/starodk | STARODK.CreateOrUpdate | Y | 200 |
| get_odk | GET | /api/starodk/{{odk1.odkId}} | STARODK.Get | Y | 200 |
| get_all_odks | GET | /api/starodk | STARODK.GetAll | Y | 200 |
| generate_odk | POST | /api/starodk/{{odk1.odkId}}/generate | STARODK.Generate | Y | 200 |
| delete_odk | DELETE | /api/starodk/{{odk1.odkId}} | STARODK.Delete | Y | 200 |
| cleanup_avatar | DELETE | /api/avatar/{{savatar.avatarId}} | Avatar.Delete | Y | 200 |

### STARODKController_QA.jsonl (26 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| seed_avatar | POST | /api/avatar/register | Avatar.Register | N | 200 |
| login_avatar | POST | /api/avatar/login | Avatar.Login | N | 200 |
| create_odk_basic | POST | /api/starodk | STARODK.CreateOrUpdate | Y | 200 |
| create_odk_advanced | POST | /api/starodk | STARODK.CreateOrUpdate | Y | 200 |
| create_odk_unicode | POST | /api/starodk | STARODK.CreateOrUpdate | Y | 200 |
| get_odk_by_id | GET | /api/starodk/{{odkBasic.id}} | STARODK.Get | Y | 200 |
| get_nonexistent_odk | GET | /api/starodk/00000000-…-000 | STARODK.Get | Y | 404 |
| get_all_odks | GET | /api/starodk | STARODK.GetAll | Y | 200 |
| generate_odk_algorand | POST | /api/starodk/{{odkBasic.id}}/generate | STARODK.Generate | Y | 200 |
| generate_odk_solana | POST | /api/starodk/{{odkAdvanced.id}}/generate | STARODK.Generate | Y | 200 |
| deploy_odk_basic | POST | /api/starodk/{{odkBasic.id}}/deploy | STARODK.Deploy | Y | 200 |
| deploy_odk_advanced | POST | /api/starodk/{{odkAdvanced.id}}/deploy | STARODK.Deploy | Y | 200 |
| update_odk_via_upsert | POST | /api/starodk | STARODK.CreateOrUpdate | Y | 200 |
| get_odk_unauthorized | GET | /api/starodk/{{odkBasic.id}} | STARODK.Get | N | 401 |
| create_odk_unauthorized | POST | /api/starodk | STARODK.CreateOrUpdate | N | 401 |
| delete_odk_unauthorized | DELETE | /api/starodk/{{odkBasic.id}} | STARODK.Delete | N | 401 |
| generate_odk_unauthorized | POST | /api/starodk/{{odkBasic.id}}/generate | STARODK.Generate | N | 401 |
| create_fresh_odk | POST | /api/starodk | STARODK.CreateOrUpdate | Y | 200 |
| deploy_before_generate | POST | /api/starodk/{{odkFresh.id}}/deploy | STARODK.Deploy | Y | 400 |
| delete_odk_basic–verify_deleted_odk (×5) | DELETE/GET | /api/starodk/{id} | STARODK.Delete/Get | Y | 200/404 |
| cleanup_avatar | DELETE | /api/avatar/{{starAvatar.id}} | Avatar.Delete | Y | 200 |

### STARODKController_Malicious.jsonl (38 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| seed_avatar | POST | /api/avatar/register | Avatar.Register | N | 200 |
| login_avatar | POST | /api/avatar/login | Avatar.Login | N | 200 |
| seed_odk | POST | /api/starodk | STARODK.CreateOrUpdate | Y | 200 |
| sqli_odk_name–sqli_generate_config (×4) | POST | /api/starodk or /generate | STARODK.CreateOrUpdate/Generate | Y | 2xx or 200 |
| xss_odk_name–xss_generate_config (×3) | POST | /api/starodk or /generate | STARODK.CreateOrUpdate/Generate | Y | 2xx or 200 |
| get_odk_path_traversal–delete_invalid_odk (×6) | GET/POST/DELETE | /api/starodk/{bad-id} | (404) | Y | 404 |
| oversized_odk_name–oversized_generation_config (×3) | POST | /api/starodk or /generate | STARODK.CreateOrUpdate/Generate | Y | 2xx or 200 |
| null_odk_name–empty_body (×6) | POST | /api/starodk | STARODK.CreateOrUpdate | Y | 4xx |
| generate_invalid_chain–deploy_before_generate (×6) | POST | /api/starodk/{id}/generate or /deploy | STARODK.Generate/Deploy | Y | 200 or 4xx or 400 |
| get_odk_unauth–delete_odk_unauth (×5) | GET/POST/DELETE | /api/starodk/{id} | STARODK.* | N | 401 |
| unicode_odk_name–zwc_odk_name (×3) | POST | /api/starodk | STARODK.CreateOrUpdate | Y | 2xx |
| cleanup_target_odk | DELETE | /api/starodk/{{targetODK.id}} | STARODK.Delete | Y | 200 |
| cleanup_avatar | DELETE | /api/avatar/{{starMalAvatar.id}} | Avatar.Delete | Y | 200 |

### E2E-Flows.jsonl (96 cases across Flows 1–10)

> All authenticated cases use `Authorization: Bearer {{e2eNAuth.token}}` extracted earlier in the same flow.

| Flow | Cases | Key paths | Auth? | expectedStatus |
|---|---|---|---|---|
| Flow 1 (e2e1_*) | 14 | /api/avatar/register+login, /api/holon (CRUD), /api/avatar/*/wallets (MISSING ROUTE) | Mix Y/N | 200 |
| Flow 2 (e2e2_*) | 10 | /api/avatar/register+login, /api/holon, /api/starodk (create+gen+delete) | Mix Y/N | 200 |
| Flow 3 (e2e3_*) | 11 | /api/avatar/register+login (×2), /api/holon (cross-avatar) | Mix Y/N | 200 or 404 |
| Flow 4 (e2e4_*) | 9 | /api/avatar/*/wallets (MISSING ROUTE), /api/holon/mint, /api/blockchainoperation | Mix Y/N | 200 |
| Flow 5 (e2e5_*) | 12 | /api/avatar/register+login, GET/PUT /api/avatar/{id} (×8) | Mix Y/N | 200 |
| Flow 6 (e2e6_*) | 10 | /api/holon (create+interact×5+delete) | Mix Y/N | 200 |
| Flow 7 (e2e7_*) | 11 | Token reuse across /api/avatar, /api/holon, /api/starodk, /api/blockchainoperation | Mix Y/N | 200 |
| Flow 8 (e2e8_*) | 9 | /api/starodk (full deploy lifecycle) | Mix Y/N | 200 |
| Flow 9 (e2e9_*) | 12 | /api/holon/mint+exchange, /api/blockchainoperation (get ops) | Mix Y/N | 200 |
| Flow 10 (e2e10_*) | 8 | /api/avatar/*/wallets (MISSING ROUTE ×3), multi-wallet management | Mix Y/N | 200 |

### CrossController_E2E.jsonl (57 cases across 3 flows)

> All authenticated. Flows follow register→login→operations→cleanup pattern.

| Flow | Cases | Key paths | Auth? | expectedStatus |
|---|---|---|---|---|
| Flow 1 (e2e1_*) | 19 | /api/avatar, /api/avatar/*/wallets (MISSING ROUTE), /api/holon/mint+exchange, /api/blockchainoperation | Mix Y/N | 200 |
| Flow 2 (e2e2_*) | 14 | /api/holon ×2, /api/starodk (gen+deploy) | Mix Y/N | 200 |
| Flow 3 (e2e3_*) | 14 | /api/holon (×2 avatars, isolation test — 200 on get/update/delete, no ownership enforcement) | Mix Y/N | 200 |

### Blockchain_Devnet.jsonl (30 cases)

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| algo_seed_avatar | POST | /api/avatar/register | Avatar.Register | N | 200 |
| algo_login | POST | /api/avatar/login | Avatar.Login | N | 200 |
| algo_add_wallet | POST | /api/avatar/{{algoAvatar.id}}/wallets | **MISSING ROUTE** | Y | 200 |
| sol_seed_avatar | POST | /api/avatar/register | Avatar.Register | N | 200 |
| sol_login | POST | /api/avatar/login | Avatar.Login | N | 200 |
| sol_add_wallet | POST | /api/avatar/{{solAvatar.id}}/wallets | **MISSING ROUTE** | Y | 200 |
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
| algo_get_ops_by_avatar | GET | /api/blockchainoperation/avatar/{{algoAvatar.id}} | BlockchainOp.GetByAvatar | Y | 200 |
| sol_get_op_by_id | GET | /api/blockchainoperation/{{solMintOp.opId}} | BlockchainOp.Get | Y | 200 |
| sol_get_ops_by_avatar | GET | /api/blockchainoperation/avatar/{{solAvatar.id}} | BlockchainOp.GetByAvatar | Y | 200 |
| algo_verify_mint_status | GET | /api/blockchainoperation/{{algoMintOp.opId}} | BlockchainOp.Get | Y | 200 |
| sol_verify_mint_status | GET | /api/blockchainoperation/{{solMintOp.opId}} | BlockchainOp.Get | Y | 200 |
| sol_get_algo_op | GET | /api/blockchainoperation/{{algoMintOp.opId}} | BlockchainOp.Get | Y | 200 |
| algo_cleanup_* (×2) | DELETE | /api/holon/… | Holon.Delete | Y | 200 |
| sol_cleanup_holon | DELETE | /api/holon/… | Holon.Delete | Y | 200 |
| algo_cleanup_wallet | DELETE | /api/avatar/{{algoAvatar.id}}/wallets/… | **MISSING ROUTE** | Y | 200 |
| sol_cleanup_wallet | DELETE | /api/avatar/{{solAvatar.id}}/wallets/… | **MISSING ROUTE** | Y | 200 |
| algo_cleanup_avatar | DELETE | /api/avatar/{{algoAvatar.id}} | Avatar.Delete | Y | 200 |
| sol_cleanup_avatar | DELETE | /api/avatar/{{solAvatar.id}} | Avatar.Delete | Y | 200 |

### MaliciousPayloads.jsonl (77 cases)

> All cases use avatar seed+login as first two cases. Key auth test cases within:

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| mal_avatar_seed | POST | /api/avatar/register | Avatar.Register | N | 200 |
| mal_avatar_login | POST | /api/avatar/login | Avatar.Login | N | 200 |
| mal_sql_* (×4 incl. 2 login) | POST | /api/avatar/register or /login | Register/Login | N | 400 or 401 |
| mal_xss_* (×3) | POST | /api/avatar/register | Avatar.Register | N | 400 |
| mal_oversized_* (×2) | POST | /api/avatar/register | Avatar.Register | N | 400 |
| mal_special_chars_* (×4) | POST | /api/avatar/register | Avatar.Register | N | 400 |
| mal_type_* (×3) | POST | /api/avatar/register | Avatar.Register | N | 400 |
| mal_null_* (×3) | POST | /api/avatar/register | Avatar.Register | N | 400 |
| mal_auth_no_bearer | GET | /api/avatar/{{malAvatar.avatarId}} | Avatar.Get | Y (no Bearer prefix) | 401 |
| mal_auth_expired_format | GET | /api/avatar/{{malAvatar.avatarId}} | Avatar.Get | Y (bad JWT) | 401 |
| mal_auth_empty_token | GET | /api/avatar/{{malAvatar.avatarId}} | Avatar.Get | Y (empty) | 401 |
| mal_path_traversal | GET | /api/avatar/../avatar/… | (404) | Y | 404 |
| mal_guid_zero | GET | /api/avatar/00000000-…-000 | Avatar.Get | Y | 404 |
| mal_update_xss | PUT | /api/avatar/{{malAvatar.avatarId}} | Avatar.Update | Y | 400 |
| mal_update_sqli | PUT | /api/avatar/{{malAvatar.avatarId}} | Avatar.Update | Y | 400 |
| mal_wallet_xss_address | POST | /api/avatar/{{malAvatar.avatarId}}/wallets | **MISSING ROUTE** | Y | 400 |
| mal_wallet_sqli_label | POST | /api/avatar/{{malAvatar.avatarId}}/wallets | **MISSING ROUTE** | Y | 400 |
| mal_avatar_cleanup | DELETE | /api/avatar/{{malAvatar.avatarId}} | Avatar.Delete | Y | 200 |
| mal_holon_seed–mal_holon_cleanup_avatar (×24) | POST/DELETE | /api/avatar, /api/holon | Avatar/Holon | Mix Y/N | 200/400/404 |
| mal_star_seed–mal_star_cleanup_avatar (×10) | POST/DELETE | /api/avatar, /api/starodk | Avatar/STARODK | Mix Y/N | 200/400/404 |
| mal_bc_seed–mal_bc_cleanup_avatar (×21) | POST/GET/DELETE | /api/avatar, /api/blockchainoperation | Avatar/BlockchainOp | Mix Y/N | 200/400/401/404 |
| mal_nosql_* (×3) | POST | /api/avatar/register or /login | Register/Login | N | 400 |
| *(all remaining: cmd, ldap, mass-assign, deep-nest, ssrf, crlf, format-string, log-inject, proto-pollute, unicode, path, template, xml — ×20+)* | POST | /api/avatar/register or /api/holon | Register/Holon | Mix N/Y | 400 or 404 |

### QA-EdgeCases.jsonl (86 cases)

> All auth-requiring cases have valid `Authorization: Bearer {{qaAuth.token}}` / `{{qaHAuth.token}}` / `{{qaSAuth.token}}` / `{{qaBAuth.token}}`.

| Group | Cases | Paths | Auth? | expectedStatus |
|---|---|---|---|---|
| Avatar seed/login | 2 | /api/avatar/register, /login | N | 200 |
| Avatar boundary (missing fields/bad email) | 6 | /api/avatar/register, /login | N | 400 or 401 |
| Avatar GET/UPDATE (auth OK) | 8 | /api/avatar/{id} | Y | 200 or 404 |
| Auth boundary tests | 2 | /api/avatar/{id} | N or Y (invalid) | 401 |
| Wallet edge cases | 5 | /api/avatar/{{qaAvatar.avatarId}}/wallets | Y | 200 or 400 or 404 |
| Holon seed/login | 2 | /api/avatar/register, /login | N | 200 |
| Holon edge cases (create/get/update/interact/delete) | 14 | /api/holon | Y | 200 or 400 or 404 |
| STAR seed/login | 2 | /api/avatar/register, /login | N | 200 |
| STAR ODK edge cases | 10 | /api/starodk | Y | 200 or 400 or 404 |
| Blockchain seed/login | 2 | /api/avatar/register, /login | N | 200 |
| Blockchain edge cases | 4 | /api/blockchainoperation | Y | 200 or 404 |
| Extended holon (seed/login + rich tests) | 17 | /api/holon (activate, peers, query combos, interact) | Y | 200 |
| Extended STAR (seed/login + update/gen) | 8 | /api/starodk | Y | 200 |
| Extended blockchain (seed/wallet/holon/mint/exchange edge) | 10 | /api/avatar/*/wallets (MISSING ROUTE), /api/holon, /api/blockchainoperation | Y | 200 or 400 |
| Misc boundary (dup username, email case, IsActive, empty string, long name, wallet default) | 8 | /api/avatar/{id}, /api/avatar/*/wallets | Y | 200 or 400 or 404 |
| Cleanup (×5) | 5 | /api/avatar/{id}, /api/holon/{id}, etc | Y | 200 |

### Frontend.jsonl (9 cases) — NOT EDITABLE BY E2

| CaseId | Method | Path | Matched Action | Auth? | expectedStatus |
|---|---|---|---|---|---|
| fe_root | GET | http://localhost:3000/ | Frontend (Next.js) | N | 2xx |
| fe_login_page | GET | http://localhost:3000/login | Frontend | N | 2xx |
| fe_register_page | GET | http://localhost:3000/register | Frontend | N | 2xx |
| fe_overview | GET | http://localhost:3000/overview | Frontend | N | 2xx |
| fe_avatars | GET | http://localhost:3000/avatars | Frontend | N | 2xx |
| fe_wallets | GET | http://localhost:3000/wallets | Frontend | N | 2xx |
| fe_holons | GET | http://localhost:3000/holons | Frontend | N | 2xx |
| fe_api_keys | GET | http://localhost:3000/api-keys | Frontend | N | 2xx |
| fe_404 | GET | http://localhost:3000/__does-not-exist__ | Frontend | N | 404 |

> **Note**: All Frontend.jsonl paths target `http://localhost:3000` (Next.js), NOT the API BaseUrl. Regression gate (FR-8 / IR-4). E2 MUST NOT touch this file.

### Stress_RapidOperations.jsonl (1024 cases) — NOT EDITABLE BY E2

> 1024 JSON cases: seed_avatar (register+login), 10 rapid holon creates (`stress_holon_1–10`), then ~1000 rapid holon PUT updates (`stress_update_1–N`) on the same holons. All use `Authorization: Bearer {{stressAuth.token}}`. All `expectedStatus: 200`. W1-B1 owns this file exclusively.

---

## Table 3 — Mismatch Table (auth required, no Authorization header in happy-path 2xx cases)

> These are cases where: (a) `expectedStatus` is `200` or `2xx`, AND (b) the target action requires `[Authorize]`, AND (c) the case either has no `Authorization` header or has an unresolved `{{...}}` token because the upstream login extraction failed due to missing `_suiteVars` + suite prefix collision.

| Suite | CaseId | Path | Required Scheme | Fix Prescription |
|---|---|---|---|---|
| AvatarController.jsonl | `add_wallet` | /api/avatar/{{avatar1.avatarId}}/wallets | **MISSING ROUTE** (structural, not auth gap) | E2 cannot fix — route does not exist in any controller |
| AvatarController.jsonl | `get_wallets` | /api/avatar/{{avatar1.avatarId}}/wallets | **MISSING ROUTE** | Same — API-level fix required |
| AvatarController.jsonl | `remove_wallet` | /api/avatar/{{avatar1.avatarId}}/wallets/{{wallet1.walletId}} | **MISSING ROUTE** | Same |
| AvatarController_QA.jsonl | `add_algorand_wallet` | /api/avatar/{{minimalAvatar.id}}/wallets | **MISSING ROUTE** | Same |
| AvatarController_QA.jsonl | `add_solana_wallet` | /api/avatar/{{minimalAvatar.id}}/wallets | **MISSING ROUTE** | Same |
| AvatarController_QA.jsonl | `add_second_algorand_wallet` | /api/avatar/{{minimalAvatar.id}}/wallets | **MISSING ROUTE** | Same |
| AvatarController_QA.jsonl | `get_wallets` | /api/avatar/{{minimalAvatar.id}}/wallets | **MISSING ROUTE** | Same |
| AvatarController_QA.jsonl | `remove_solana_wallet` | /api/avatar/{{minimalAvatar.id}}/wallets/{walletId} | **MISSING ROUTE** | Same |
| AvatarController_QA.jsonl | `remove_nonexistent_wallet` | /api/avatar/{{minimalAvatar.id}}/wallets/00000000-…-000 | **MISSING ROUTE** | Same |
| AvatarController_QA.jsonl | `add_wallet_to_other_avatar` | /api/avatar/{{fullAvatar.id}}/wallets | **MISSING ROUTE** | Same |
| BlockchainOperationController.jsonl | `seed_wallet` | /api/avatar/{{bavatar.avatarId}}/wallets | **MISSING ROUTE** | Same |
| BlockchainOperationController.jsonl | `cleanup_wallet` | /api/avatar/{{bavatar.avatarId}}/wallets/{{bwallet.walletId}} | **MISSING ROUTE** | Same |
| BlockchainOperationController_QA.jsonl | `add_wallet` | /api/avatar/{{bcAvatar.id}}/wallets | **MISSING ROUTE** | Same |
| BlockchainOperationController_QA.jsonl | `cleanup_wallet` | /api/avatar/{{bcAvatar.id}}/wallets/{{bcWallet.walletId}} | **MISSING ROUTE** | Same |
| Blockchain_Devnet.jsonl | `algo_add_wallet` | /api/avatar/{{algoAvatar.id}}/wallets | **MISSING ROUTE** | Same |
| Blockchain_Devnet.jsonl | `sol_add_wallet` | /api/avatar/{{solAvatar.id}}/wallets | **MISSING ROUTE** | Same |
| Blockchain_Devnet.jsonl | `algo_cleanup_wallet` | /api/avatar/{{algoAvatar.id}}/wallets/{walletId} | **MISSING ROUTE** | Same |
| Blockchain_Devnet.jsonl | `sol_cleanup_wallet` | /api/avatar/{{solAvatar.id}}/wallets/{walletId} | **MISSING ROUTE** | Same |
| E2E-Flows.jsonl | `e2e1_add_wallet` | /api/avatar/{{e2e1Avatar.avatarId}}/wallets | **MISSING ROUTE** | Same |
| E2E-Flows.jsonl | `e2e1_remove_wallet` | /api/avatar/{{e2e1Avatar.avatarId}}/wallets/{walletId} | **MISSING ROUTE** | Same |
| E2E-Flows.jsonl | `e2e4_add_wallet` | /api/avatar/{{e2e4Avatar.avatarId}}/wallets | **MISSING ROUTE** | Same |
| E2E-Flows.jsonl | `e2e4_remove_wallet` | /api/avatar/{{e2e4Avatar.avatarId}}/wallets/{walletId} | **MISSING ROUTE** | Same |
| E2E-Flows.jsonl | `e2e10_wallet_1–3` (×3) | /api/avatar/{{e2e10Avatar.avatarId}}/wallets | **MISSING ROUTE** | Same |
| CrossController_E2E.jsonl | `e2e1_add_algo_wallet` | /api/avatar/{{e2eAvatar.id}}/wallets | **MISSING ROUTE** | Same |
| CrossController_E2E.jsonl | `e2e1_cleanup_wallet` | /api/avatar/{{e2eAvatar.id}}/wallets/{walletId} | **MISSING ROUTE** | Same |
| MaliciousPayloads.jsonl | `mal_wallet_xss_address` | /api/avatar/{{malAvatar.avatarId}}/wallets | **MISSING ROUTE** | Same |
| MaliciousPayloads.jsonl | `mal_wallet_sqli_label` | /api/avatar/{{malAvatar.avatarId}}/wallets | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_wallet_add_missing_chain` | /api/avatar/{{qaAvatar.avatarId}}/wallets | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_wallet_add_missing_address` | /api/avatar/{{qaAvatar.avatarId}}/wallets | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_wallet_add_valid` | /api/avatar/{{qaAvatar.avatarId}}/wallets | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_wallet_remove_random` | /api/avatar/{{qaAvatar.avatarId}}/wallets/22222222-…-222 | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_wallet_remove_valid` | /api/avatar/{{qaAvatar.avatarId}}/wallets/{{qaWallet.walletId}} | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_wallet_default_true` | /api/avatar/{{qaAvatar.avatarId}}/wallets | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_wallet_default_false` | /api/avatar/{{qaAvatar.avatarId}}/wallets | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_wallet_cleanup_default` | /api/avatar/{{qaAvatar.avatarId}}/wallets/{{qaWallet2.walletId}} | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_bc2_add_wallet` | /api/avatar/{{qaB2Avatar.avatarId}}/wallets | **MISSING ROUTE** | Same |
| QA-EdgeCases.jsonl | `qa_bc2_cleanup_wallet` | /api/avatar/{{qaB2Avatar.avatarId}}/wallets/{{qaB2Wallet.walletId}} | **MISSING ROUTE** | Same |

**Critical structural finding**: `POST /api/avatar/{id}/wallets` and `DELETE /api/avatar/{id}/wallets/{walletId}` are called in **at least 37 cases across 9 suites** but no such route exists in AvatarController or any other controller. The wallet CRUD is routed through `WalletController` at `/api/wallet` (no avatar-scoped sub-path). This is an API routing gap, not an auth-header gap — E2 **cannot fix these by adding auth headers**. Fix prescription for W4-F1: implement the avatar-scoped wallet endpoints in AvatarController (or route via WalletController using avatar claims).

**True auth-header-only mismatches** (cases that hit real routes but lack `Authorization`):

| Suite | CaseId | Path | Required Scheme | Fix Prescription |
|---|---|---|---|---|
| HolonController_QA.jsonl | `isolation_unauth_create` | POST /api/holon | MultiScheme | INTENTIONAL — expects 401; do NOT add auth |
| MaliciousPayloads.jsonl | `mal_mass_holon` | POST /api/holon | MultiScheme | Uses `{{malHAuth.token}}` which IS present; no fix needed if upstream login succeeded |

> **Auth-not-set-up root cause** (spec's "401 auth-not-set-up ~155 cases"): The primary driver is cross-suite email collision. When `register_avatar` in one suite registers `live@test.oasis` and a second suite also registers `live@test.oasis`, the second gets 400 and the login token is never extracted — all downstream `{{auth1.token}}` substitutions remain as literal `{{...}}` strings. The per-suite `_suiteVars` + `{{suitePrefix}}` mechanism (W2-E1 + W3-E2) is the fix. Once each suite uses `{{suitePrefix}}_live@test.oasis`, collision is eliminated and all downstream auth headers resolve correctly.

---

## Table 4 — Intentional 401 Cases (DO NOT EDIT — IR-1 + R-3)

These cases exist to verify rejection. E2 MUST NOT add `Authorization` headers to these cases.

| Suite | CaseId | What it tests | Why intentional |
|---|---|---|---|
| AvatarController_QA.jsonl | `get_without_auth` | GET /api/avatar/{id} with no auth header | Verifies [Authorize] blocks unauthenticated GET |
| AvatarController_QA.jsonl | `get_with_malformed_auth` | GET /api/avatar/{id} with `Bearer invalid.token.here` | Verifies token signature validation rejects bad JWTs |
| AvatarController_Malicious.jsonl | `sqli_login_email` | POST /api/avatar/login with SQLi email → 401 | Verifies login rejects SQLi without granting access |
| AvatarController_Malicious.jsonl | `sqli_login_password` | POST /api/avatar/login with SQLi password → 401 | Verifies login rejects SQLi password without granting access |
| AvatarController_Malicious.jsonl | `header_injection_auth` | GET /api/avatar/… with `Bearer token\r\nX-Injected: evil` → 401 | Verifies CRLF-injected auth is rejected |
| HolonController_QA.jsonl | `isolation_unauth_create` | POST /api/holon with no auth → 401 | Verifies class-level [Authorize] blocks unauthenticated Holon.Create |
| BlockchainOperationController_QA.jsonl | `get_op_unauthorized` | GET /api/blockchainoperation/{id} with no auth → 401 | Verifies [Authorize] on BlockchainOp.Get |
| BlockchainOperationController_QA.jsonl | `get_ops_by_avatar_unauthorized` | GET /api/blockchainoperation/avatar/{id} with no auth → 401 | Verifies [Authorize] on BlockchainOp.GetByAvatar |
| BlockchainOperationController_Malicious.jsonl | `get_op_no_auth` | GET /api/blockchainoperation/{id} with no auth → 401 | Auth bypass probe — rejects correctly |
| BlockchainOperationController_Malicious.jsonl | `get_by_avatar_no_auth` | GET /api/blockchainoperation/avatar/{id} with no auth → 401 | Auth bypass probe — rejects correctly |
| BlockchainOperationController_Malicious.jsonl | `get_op_empty_auth` | GET /api/blockchainoperation/{id} with `Authorization: ""` → 401 | Verifies empty auth header rejected |
| BlockchainOperationController_Malicious.jsonl | `get_op_bearer_only` | GET /api/blockchainoperation/{id} with `Bearer ` only → 401 | Verifies empty token after `Bearer ` rejected |
| BlockchainOperationController_Malicious.jsonl | `get_op_tampered_token` | GET /api/blockchainoperation/avatar/{id} with tampered JWT → 401 | Verifies signature validation rejects forged JWT |
| BlockchainOperationController_Malicious.jsonl | `header_injection_op` | GET /api/blockchainoperation/avatar/{id} with CRLF-injected auth → 401 | Verifies CRLF injection in auth header rejected |
| STARODKController_QA.jsonl | `get_odk_unauthorized` | GET /api/starodk/{id} with no auth → 401 | Verifies [Authorize] on STARODK.Get |
| STARODKController_QA.jsonl | `create_odk_unauthorized` | POST /api/starodk with no auth → 401 | Verifies [Authorize] on STARODK.CreateOrUpdate |
| STARODKController_QA.jsonl | `delete_odk_unauthorized` | DELETE /api/starodk/{id} with no auth → 401 | Verifies [Authorize] on STARODK.Delete |
| STARODKController_QA.jsonl | `generate_odk_unauthorized` | POST /api/starodk/{id}/generate with no auth → 401 | Verifies [Authorize] on STARODK.Generate |
| STARODKController_Malicious.jsonl | `get_odk_unauth` | GET /api/starodk/{id} with no auth → 401 | Auth bypass probe — attack surface verification |
| STARODKController_Malicious.jsonl | `create_odk_unauth` | POST /api/starodk with no auth → 401 | Auth bypass probe |
| STARODKController_Malicious.jsonl | `generate_odk_unauth` | POST /api/starodk/{id}/generate with no auth → 401 | Auth bypass probe |
| STARODKController_Malicious.jsonl | `deploy_odk_unauth` | POST /api/starodk/{id}/deploy with no auth → 401 | Auth bypass probe |
| STARODKController_Malicious.jsonl | `delete_odk_unauth` | DELETE /api/starodk/{id} with no auth → 401 | Auth bypass probe |
| MaliciousPayloads.jsonl | `mal_auth_no_bearer` | GET /api/avatar/{id} with `Authorization: {{malAuth.token}}` (no Bearer prefix) → 401 | Verifies scheme prefix is required |
| MaliciousPayloads.jsonl | `mal_auth_expired_format` | GET /api/avatar/{id} with valid-shaped-but-unsigned JWT → 401 | Verifies forged-signature rejection |
| MaliciousPayloads.jsonl | `mal_auth_empty_token` | GET /api/avatar/{id} with `Bearer ` → 401 | Verifies empty token rejected |
| MaliciousPayloads.jsonl | `mal_bc_by_avatar_sqli`–`mal_bc_get_sqli` | GET with SQLi in path → 404 (via routing) | Path routing + auth: no auth + SQLi in path |
| QA-EdgeCases.jsonl | `qa_get_no_auth` | GET /api/avatar/{id} with `headers:{}` → 401 | Standard auth gate verification |
| QA-EdgeCases.jsonl | `qa_get_bad_auth` | GET /api/avatar/{id} with `Bearer invalid.token.here` → 401 | Malformed JWT rejected |
| QA-EdgeCases.jsonl | `qa_login_wrong_pass` | POST /api/avatar/login with wrong password → 401 | Business logic: wrong credential rejected |
| QA-EdgeCases.jsonl | `qa_login_missing_email` | POST /api/avatar/login with nonexistent email → 401 | Business logic: unknown user rejected |

---

## Cross-cutting Observations

1. **`AvatarController.Login` is the unique auth-chain entry point**: every suite that calls any `[Authorize]` endpoint depends on `POST /api/avatar/login` returning `OASISResult<string>` with the JWT in `result` (not `result.token`). The extract `{"token":"result"}` is correct. Any upstream failure (e.g., duplicate email collision from missing `suitePrefix`) cascades to all downstream `{{...token}}` cases as Inconclusive/401.

2. **Missing route `/api/avatar/{id}/wallets`**: Approximately 37+ cases across 9 suites reference this route, which does not exist. This is the single largest structural issue beyond auth-header gaps. W4-F1 needs to add this route (either in AvatarController as nested methods delegating to WalletManager, or via a redirect to WalletController). E2 cannot address this.

3. **`AllowAnonymous` endpoints (4 total)**: `Avatar.Register`, `Avatar.Login`, `Nft.GetMetadata`, `Network.Get`. No suites attempt to add auth headers to these; correctly omitted.

4. **NetworkController is class-`[AllowAnonymous]`**: Its single action also has `[AllowAnonymous]` — redundant but correct. No suite tests this controller directly.

5. **Suite identity collision** (spec R-2): Every suite that registers an avatar uses hardcoded emails like `live@test.oasis`, `holon@test.oasis`, etc. Without `_suiteVars` + `{{suitePrefix}}` (W2-E1 + W3-E2), parallel runs collide. The `suitePrefix` fix in E2 is the correct mitigation.

6. **QA/Malicious 401 case count**: Counting Table 4 entries across the 9 editable `_QA` and `_Malicious` suites gives ~30 explicit 401 cases. Together with ~155 spec-predicted auth-not-set-up cases (from suite collision), the total resolved 401 exposure is large. Post-E2, the 30 intentional 401s should remain; the ~155 collision-driven 401s should resolve to 200.

7. **DappCompositionController / DappSeriesController / QuestController / WalletController / BridgeController / SwapController / SearchController / AvatarNFTController / NftController**: None of these controllers appear in any current JSONL suite. Their auth posture is documented in Table 1 for completeness and for future suite expansion.
