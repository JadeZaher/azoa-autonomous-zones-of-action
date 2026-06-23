# .NET Client SDK — Specification

## Status
Decision record + track. **Tier 1 — a consumer surface.** A first-class .NET
(C#) client SDK for the AZOA WebAPI, shipped as a NuGet package, at **full
parity with the existing TypeScript SDK** `@azoa/sdk` (the package at
`sdk/azoa-wallet/`, npm name `@azoa/wallet-sdk`). USER-DECIDED, USER-SCOPED.
Nothing here is implemented yet — there is **no** .NET client SDK in the repo
today.

## Goal
Ship a **standalone, hand-written .NET client** for the AZOA WebAPI that a C#
consumer references as a NuGet package and uses to call AZOA over HTTP, mirroring
the public surface of the TS SDK: **auth/session, wallet, holons, portfolio, NFT
(+ AvatarNFT bindings), swap, blockchain-operation/bridge/search reads, STAR ODK,
quest CRUD, and the workflow template-authoring + fluent run-driver surface.** The
two SDKs are **separate hand-written clients that both speak HTTP** to the same
WebAPI; they share no runtime.

The driving consumer is **ArdaNova** (project memory `ardanova-provider-port`):
AZOA-as-a-service called from C#. ArdaNova needs a typed, idiomatic .NET client
the same way the browser/RN/Lynx world has the TS SDK. The SDK ships **no
ArdaNova types and no economic semantics** — it is a generic AZOA client, the
same NO-brand-leak rule the TS SDK and the workflow tracks follow.

The headline of the workflow surface — the TS thenable
`quest(id).start().step(b).step(c)` (`sdk/azoa-wallet/src/workflow/run.ts:54-340`)
— is mirrored as an idiomatic C# **async run handle** (see plan DP5).

## Background — the surface this track mirrors (file:line evidence)

### What exists in the repo today
- The **only** TS SDK is at `sdk/azoa-wallet/` — a cross-platform (browser / RN
  / Lynx) typed HTTP client + fluent facades. Its public entry points are
  re-exported from `sdk/azoa-wallet/src/index.ts:1-116`.
- The TS facade `AzoaClient` composes `api` + `wallet` + `session` + `auth` +
  `holons` + `portfolio` + `workflow`
  (`sdk/azoa-wallet/src/client/azoa-client.ts:85-156`).
- The **only** C# libraries in the repo are the WebAPI host
  (`AZOA.WebAPI.csproj`) and the SurrealDB infra packages under `packages/`
  (`packages/Azoa.SurrealDb.Client/`, `packages/Azoa.SurrealDb.Schema/`). There
  is **no** C# client SDK — confirmed by the absence of any `Azoa.Sdk` /
  `Azoa.Client` project and by `tracks.md` listing only the TS
  `azoa-wallet-sdk` SDK track (`conductor/tracks.md:94`).

### The wire contract both SDKs bind to
The WebAPI returns `AZOAResult<T>` on the wire — `{ isError, message, result,
detail }` — which the TS `request<T>` unwraps (success ⇒ `result`, `isError` ⇒
error) (`sdk/azoa-wallet/src/api/client.ts:1134-1144`). A handful of endpoints
(BridgeController) return **bare** objects, handled by `requestBare<T>`
(`sdk/azoa-wallet/src/api/client.ts:1150-1180`). Both shapes must be mirrored in
C# (DP2). Auth is **JWT-or-X-Api-Key**: a Bearer token wins, else `X-Api-Key` is
sent (`sdk/azoa-wallet/src/api/client.ts:1265-1269`); token refresh is
deduplicated via `_refreshInFlight` (`sdk/azoa-wallet/src/api/client.ts:1291-1306`);
an `Idempotency-Key` rides `extraHeaders` (`:879-893`, `:1270-1274`); a per-call
`Authorization` override threads a child JWT for the workflow `forActor` path
(`sdk/azoa-wallet/src/workflow/client.ts:201-209`). The WebAPI has Swagger wired
(`Program.cs` `AddSwaggerGen`), which DP3 evaluates as a DTO source.

### The TS → C# parity matrix (the core of this spec)

Enumerated by reading `sdk/azoa-wallet/src/api/client.ts` (the typed HTTP client,
**77** public methods), the `WorkflowClient` + `quest()` driver
(`sdk/azoa-wallet/src/workflow/client.ts`, `.../run.ts`), and the facade
sub-clients (`auth-provider.ts`, `session.ts`, `portfolio.ts`, `holon-query.ts`).
C# names use PascalCase + the `…Async` suffix (idiomatic). Each row is a TS public
method → the proposed C# method on the corresponding facade. Grouped by facade.

#### Auth / session facade → `azoa.Auth` (+ `azoa.Session`)
| TS method (file:line) | C# method |
|---|---|
| `api.login` (`client.ts:622`) / `auth.login` (`auth-provider.ts:60`) | `Auth.LoginAsync(email, password)` |
| `api.register` (`client.ts:627`) / `auth.register` (`auth-provider.ts:73`) | `Auth.RegisterAsync(RegisterParams)` |
| `auth.getProfile` (`auth-provider.ts:92`) | `Auth.GetProfileAsync()` |
| `auth.getAccessToken` (`auth-provider.ts:119`) | `Auth.AccessToken` (property) |
| `auth.logout` (`auth-provider.ts:124`) | `Auth.LogoutAsync()` |
| `session.restore` (`session.ts:65`) | `Session.RestoreAsync()` |
| `session.createRefreshCallback` (`session.ts:120`) | internal to the auth `DelegatingHandler` (DP4) |

#### Avatar reads/writes → `azoa.Avatars`
| TS method | C# method |
|---|---|
| `getAvatar` (`client.ts:639`) | `Avatars.GetAsync(avatarId)` |
| `getAllAvatars` (`client.ts:644`) | `Avatars.ListAsync()` |
| `updateAvatar` (`client.ts:648`) | `Avatars.UpdateAsync(avatarId, model)` |
| `deleteAvatar` (`client.ts:656`) | `Avatars.DeleteAsync(avatarId)` |

#### Wallet facade → `azoa.Wallets`
| TS method | C# method |
|---|---|
| `getWallet` (`client.ts:758`) | `Wallets.GetAsync(walletId)` |
| `listWallets` (`client.ts:763`) | `Wallets.ListAsync(query?)` |
| `createWallet` (`client.ts:774`) | `Wallets.CreateAsync(params)` |
| `updateWallet` (`client.ts:778`) | `Wallets.UpdateAsync(walletId, params)` |
| `deleteWallet` (`client.ts:783`) | `Wallets.DeleteAsync(walletId)` |
| `setDefaultWallet` (`client.ts:788`) | `Wallets.SetDefaultAsync(walletId)` |
| `getWalletPortfolio` (`client.ts:793`) | `Wallets.GetPortfolioAsync(walletId)` |

#### Portfolio aggregation → `azoa.Portfolio`
| TS method | C# method |
|---|---|
| `portfolio.getAll` (`portfolio.ts:44`) | `Portfolio.GetAllAsync(avatarId)` |
| `portfolio.getChainBalance` (`portfolio.ts:93`) | `Portfolio.GetChainBalanceAsync(...)` — server-portfolio variant only (DP6: no client chain provider) |

#### Holon query builder → `azoa.Holons`
| TS method (`holon-query.ts`) | C# method |
|---|---|
| `where` / `named` / `ownedBy` / `onChain` / `ofType` / `onProvider` / `childrenOf` / `active` / `inactive` (`:58-109`) | fluent `Holons.Where(...).OwnedBy(...).Active()` builder (returns `this`) |
| `execute` (`:112`) | `.ExecuteAsync()` |
| `get` (`:125`) | `Holons.GetAsync(id)` |
| `getChildren` / `getAncestors` / `getDescendants` / `getPeers` (`:130-147`) | `Holons.GetChildrenAsync` / `…AncestorsAsync` / `…DescendantsAsync` / `…PeersAsync` |
| `getComposite` (`:150`) | `Holons.GetCompositeAsync(id)` |
| `create` / `update` / `delete` (`:155-184`) | `Holons.CreateAsync` / `UpdateAsync` / `DeleteAsync` |

#### NFT (holon-asset) → `azoa.Nfts`
| TS method | C# method |
|---|---|
| `getNft` (`client.ts:664`) | `Nfts.GetAsync(nftId)` |
| `listNfts` (`client.ts:673`) | `Nfts.ListAsync(query?)` |
| `mintNft` (`client.ts:684`) | `Nfts.MintAsync(params)` |
| `transferNft` (`client.ts:689`) | `Nfts.TransferAsync(nftId, params)` |
| `burnNft` (`client.ts:695`) | `Nfts.BurnAsync(nftId, params)` |
| `getNftMetadata` (`client.ts:701`) | `Nfts.GetMetadataAsync(nftId)` |

#### AvatarNFT + bindings → `azoa.AvatarNfts`
| TS method | C# method |
|---|---|
| `mintAvatarNFT` (`client.ts:897`) | `AvatarNfts.MintAsync(params)` |
| `getAvatarNFT` (`client.ts:901`) | `AvatarNfts.GetAsync(id)` |
| `getAvatarNFTByToken` (`client.ts:906`) | `AvatarNfts.GetByTokenAsync(chainType, contract, tokenId)` |
| `listAvatarNFTs` (`client.ts:910`) | `AvatarNfts.ListAsync(avatarId)` |
| `transferAvatarNFT` (`client.ts:915`) | `AvatarNfts.TransferAsync(id, recipient)` |
| `burnAvatarNFT` (`client.ts:920`) | `AvatarNfts.BurnAsync(id)` |
| `bindHolonToNFT` (`client.ts:925`) | `AvatarNfts.BindHolonAsync(...)` |
| `listNFTHolonBindings` (`client.ts:931`) | `AvatarNfts.ListHolonBindingsAsync(id)` |
| `updateHolonBinding` (`client.ts:936`) | `AvatarNfts.UpdateHolonBindingAsync(...)` |
| `removeHolonBinding` (`client.ts:941`) | `AvatarNfts.RemoveHolonBindingAsync(id)` |
| `bindWalletToNFT` (`client.ts:946`) | `AvatarNfts.BindWalletAsync(...)` |
| `listNFTWalletBindings` (`client.ts:952`) | `AvatarNfts.ListWalletBindingsAsync(id)` |
| `updateWalletBinding` (`client.ts:957`) | `AvatarNfts.UpdateWalletBindingAsync(...)` |
| `removeWalletBinding` (`client.ts:962`) | `AvatarNfts.RemoveWalletBindingAsync(id)` |
| `getAvatarNFTComposite` (`client.ts:967`) | `AvatarNfts.GetCompositeAsync(id)` |
| `listAvatarNFTComposites` (`client.ts:972`) | `AvatarNfts.ListCompositesAsync(avatarId)` |
| `verifyNFTOwnership` (`client.ts:977`) | `AvatarNfts.VerifyOwnershipAsync(...)` |
| `verifyHolonAccess` (`client.ts:981`) | `AvatarNfts.VerifyHolonAccessAsync(...)` |
| `verifyWalletAccess` (`client.ts:985`) | `AvatarNfts.VerifyWalletAccessAsync(...)` |

#### Swap → `azoa.Swap`
| TS method | C# method |
|---|---|
| `getSwapQuote` (`client.ts:863`) | `Swap.GetQuoteAsync(params)` |
| `executeSwap` (`client.ts:879`, idempotency-key header) | `Swap.ExecuteAsync(params, idempotencyKey?)` |

#### Bridge (bare-object responses) → `azoa.Bridge`
| TS method | C# method |
|---|---|
| `getBridgeRoutes` (`client.ts:709`) | `Bridge.GetRoutesAsync()` |
| `initiateBridge` (`client.ts:714`) | `Bridge.InitiateAsync(params)` |
| `getBridgeStatus` (`client.ts:718`) | `Bridge.GetStatusAsync(bridgeId)` |
| `completeBridge` (`client.ts:723`) | `Bridge.CompleteAsync(bridgeId)` |
| `fetchVAA` (`client.ts:728`) | `Bridge.FetchVaaAsync(bridgeId)` |
| `redeemBridge` (`client.ts:732`) | `Bridge.RedeemAsync(bridgeId)` |
| `reverseBridge` (`client.ts:736`) | `Bridge.ReverseAsync(bridgeId, recipient)` |
| `getBridgeHistory` (`client.ts:740`) | `Bridge.GetHistoryAsync()` |

#### Search + blockchain-operation reads → `azoa.Search` / `azoa.Operations`
| TS method | C# method |
|---|---|
| `search` (`client.ts:747`) | `Search.QueryAsync(params)` |
| `getSearchFacets` (`client.ts:752`) | `Search.GetFacetsAsync()` |
| `getBlockchainOperation` (`client.ts:800`) | `Operations.GetAsync(operationId)` |
| `getBlockchainOperationsByAvatar` (`client.ts:805`) | `Operations.ListByAvatarAsync(avatarId)` |

#### STAR ODK → `azoa.Starodk`
| TS method | C# method |
|---|---|
| `getSTARODK` (`client.ts:812`) | `Starodk.GetAsync(id)` |
| `listSTARODK` (`client.ts:817`) | `Starodk.ListAsync()` |
| `createSTARODK` (`client.ts:828`) | `Starodk.CreateAsync(params)` |
| `updateSTARODK` (`client.ts:837`, IDOR-safe upsert) | `Starodk.UpdateAsync(id, model)` |
| `deleteSTARODK` (`client.ts:842`) | `Starodk.DeleteAsync(id)` |
| `generateSTARDapp` (`client.ts:847`) | `Starodk.GenerateDappAsync(id, params)` |
| `deploySTARODK` (`client.ts:852`) | `Starodk.DeployAsync(id)` |

#### Quest CRUD + execution → `azoa.Quests`
| TS method | C# method |
|---|---|
| `createQuest` (`client.ts:992`) | `Quests.CreateAsync(params)` |
| `getQuest` (`client.ts:997`) | `Quests.GetAsync(questId)` |
| `listQuestsByAvatar` (`client.ts:1003`) | `Quests.ListByAvatarAsync(avatarId)` |
| `updateQuest` (`client.ts:1009`) | `Quests.UpdateAsync(questId, params)` |
| `deleteQuest` (`client.ts:1015`) | `Quests.DeleteAsync(questId)` |
| `validateQuestDag` (`client.ts:1021`) | `Quests.ValidateDagAsync(questId)` |
| `executeQuest` (`client.ts:1027`) | `Quests.ExecuteAsync(questId)` |
| `executeQuestNode` (`client.ts:1033`) | `Quests.ExecuteNodeAsync(questId, nodeId)` |
| `createQuestNodeTemplate` (`client.ts:1066`) | `Quests.CreateNodeTemplateAsync(params)` |
| `listQuestNodeTemplates` (`client.ts:1071`) | `Quests.ListNodeTemplatesAsync()` |

#### Workflow — template authoring + durable run reads/writes → `azoa.Workflow`
| TS method (`workflow/client.ts`) | C# method |
|---|---|
| `createTemplate` (`:51`) / `createQuestTemplate` (`client.ts:1042`) | `Workflow.CreateTemplateAsync(params)` |
| `getTemplate` (`:58`) / `getQuestTemplate` (`client.ts:1047`) | `Workflow.GetTemplateAsync(templateId)` |
| `listTemplates` (`:64`) / `listQuestTemplates` (`client.ts:1053`) | `Workflow.ListTemplatesAsync()` |
| `instantiate` (`:73`) / `instantiateQuestTemplate` (`client.ts:1058`) | `Workflow.InstantiateAsync(templateId, params)` |
| `getRunStatus` (`:88`) | `Workflow.GetRunStatusAsync(runId)` |
| `getExecutionState` (`:100`) | `Workflow.GetExecutionStateAsync(runId)` |
| `startWorkflow` (`:117`) | `Workflow.StartWorkflowAsync(questId)` |
| `advance` (`:131`) | `Workflow.AdvanceAsync(runId, fromNodeId, opts?)` |
| `signal` (`:153`) | `Workflow.SignalAsync(runId, gateId, payload?, opts?)` |
| `issueChildCredential` (`:182`) | `Workflow.IssueChildCredentialAsync(childAvatarId, scopes?)` |

#### Workflow — fluent run driver (the headline) → `azoa.Workflow.Quest(...)`
| TS (`workflow/run.ts`) | C# (DP5 — idiomatic async handle) |
|---|---|
| `quest(questId)` (`:361`) | `Workflow.Quest(questId)` → `WorkflowRunHandle` |
| `quest.fromTemplate(id)` (`:366`) | `Workflow.QuestFromTemplate(templateId)` |
| `quest.run(runId)` (`:371`) | `Workflow.QuestRun(runId)` |
| `.forActor(childAvatarId)` (`:94`) | `handle.ForActor(childAvatarId)` (returns handle) |
| `.start({actor, params})` (`:117`) | `await handle.StartAsync(StartRunParams)` |
| `.step(nodeId, {idempotencyKey})` (`:150`) | `await handle.StepAsync(nodeId, opts?)` |
| `.signal(gateId, payload, {idempotencyKey})` (`:168`) | `await handle.SignalAsync(gateId, payload?, opts?)` |
| `.status()` (`:191`) | `await handle.StatusAsync()` |
| `.onSuspend(cb)` (`:101`) | `handle.OnSuspend(Action<WorkflowRunResult>)` |
| `then` thenable (`:233`) | replaced by explicit `await …Async` per call (DP5) |

#### API-key management → `azoa.ApiKeys`
| TS method | C# method |
|---|---|
| `createApiKey` (`client.ts:1078`) | `ApiKeys.CreateAsync(params)` |
| `listApiKeys` (`client.ts:1083`) | `ApiKeys.ListAsync()` |
| `revokeApiKey` (`client.ts:1088`) | `ApiKeys.RevokeAsync(keyId)` |
| `deleteApiKey` (`client.ts:1094`) | `ApiKeys.DeleteAsync(keyId)` |

**Parity matrix size: 95 rows** mapping TS public methods/entrypoints to proposed
C# methods (77 `api/client.ts` methods + 10 `WorkflowClient` + auth/session +
holon-builder + portfolio facade methods + the run-driver entrypoints), spanning
**13 facades**. Excluded from "HTTP parity" by DP6 (see Out of Scope): the
client-side `Signer` / `ChainProvider` / `DexAdapter` / `algorand` / `solana`
browser-signing surface (`sdk/azoa-wallet/src/core/types.ts:7,68,179`), which is
the **wrong layer** for a server-side .NET client.

## Functional Requirements

### FR-1: Auth / session facade
**Description:** `LoginAsync` / `RegisterAsync` / `GetProfileAsync` /
`LogoutAsync` mirroring `AzoaAuthProvider` (`auth-provider.ts:41-127`), plus a
session token store mirroring `SessionManager` (`session.ts:44-168`) with a
pluggable `ISessionStore` (the C# analog of `SessionStorage`). JWT is decoded for
the avatar id subject the same way (`session.ts:135-167`) — but using
`System.IdentityModel.Tokens.Jwt`-free hand-decoding to keep zero extra deps
(plan task) OR a tiny base64url decode (DP7 — no Buffer-equivalent constraint
needed in C#, but stay dependency-light).
**Acceptance:** `LoginAsync` returns the token + avatar id; the token is stored
and threaded as `Authorization: Bearer` on subsequent calls via the auth handler
(FR-8); `LogoutAsync` clears it.
**Priority:** P0

### FR-2: Wallet, holons, portfolio, avatar facades
**Description:** Mirror `Wallets` (`client.ts:758-794`), the fluent `Holons`
builder (`holon-query.ts:49-185`), `Portfolio.GetAllAsync` (server-side wallet
list + per-wallet portfolio read — NOT the client-chain-provider balance path,
DP6), and `Avatars` (`client.ts:639-657`).
**Acceptance:** every read/write maps to the matrix row's path + method + body;
the fluent holon builder resets its accumulated params after `ExecuteAsync`
(mirror `holon-query.ts:113-114`).
**Priority:** P0

### FR-3: NFT + AvatarNFT + bindings facades
**Description:** Mirror the full NFT surface (`client.ts:664-701`) and the
AvatarNFT + holon/wallet binding + verification surface (`client.ts:897-985`).
**Acceptance:** all 25 matrix rows present; binding/verify bodies match.
**Priority:** P0

### FR-4: Swap + bridge + search + operations + STAR ODK + quest CRUD facades
**Description:** Mirror swap (`client.ts:863-893`, with idempotency-key on
execute), bridge (bare-object responses — DP2's `RequestBareAsync` path,
`client.ts:709-740`), search/operations reads, STAR ODK CRUD (IDOR-safe upsert on
`UpdateAsync`, `client.ts:837`), and quest CRUD + execution
(`client.ts:992-1071`).
**Acceptance:** bare-object endpoints round-trip through the bare path; swap
execute sets `Idempotency-Key`; STAR ODK update sends the IDOR-safe model.
**Priority:** P0

### FR-5: Workflow template authoring + durable run reads/writes
**Description:** Mirror `WorkflowClient` (`workflow/client.ts:32-193`):
`CreateTemplateAsync` / `GetTemplateAsync` / `ListTemplatesAsync` /
`InstantiateAsync`, the run reads `GetRunStatusAsync` / `GetExecutionStateAsync`,
and the run writes `StartWorkflowAsync` / `AdvanceAsync` / `SignalAsync` +
`IssueChildCredentialAsync`. Path constants mirror
`sdk/azoa-wallet/src/api/api-version.ts:60-130`.
**Acceptance:** every path/body matches the matrix; `AdvanceAsync` sends
`{ fromNodeId }`; `SignalAsync` sends `{ gateId, payload }`; idempotency-key +
per-call `Authorization` override both ride an `extraHeaders` analog
(`workflow/client.ts:201-209`).
**Priority:** P0

### FR-6: Fluent workflow run driver (the headline)
**Description:** `Workflow.Quest(questId)` / `QuestFromTemplate(templateId)` /
`QuestRun(runId)` open a `WorkflowRunHandle` whose `StartAsync` / `StepAsync` /
`SignalAsync` / `StatusAsync` map onto the durable-run endpoints, with
`ForActor(childAvatarId)` threading a lazily-acquired child credential. The
ergonomics are the **idiomatic C# explicit-await-per-call** handle (DP5), not the
TS thenable.
**Acceptance:**
- `var h = azoa.Workflow.Quest(questId); await h.StartAsync(...); await
  h.StepAsync(nodeB); await h.StepAsync(nodeC);` issues, in order,
  `POST /api/quest/{questId}/start-workflow`,
  `POST /api/quest/runs/{runId}/advance {fromNodeId:nodeB}`, then `{fromNodeId:nodeC}`.
- The hybrid model: `StartAsync` then `SignalAsync(gateId, payload)` issues
  `start-workflow → signal {gateId, payload}`.
- `ForActor(childAvatarId)` lazily issues `POST
  /api/tenant/avatars/{childAvatarId}/credential` on the first advancement, caches
  the child JWT for the handle, re-acquires near expiry (deduped — the C#
  `SemaphoreSlim` analog of `_refreshInFlight`,
  `sdk/azoa-wallet/src/api/client.ts:1291-1306`), and threads it as a per-call
  `Authorization: Bearer` override.
- `StatusAsync` maps the run-status read; `OnSuspend` fires when a call leaves the
  run in `Suspended` / `AwaitingSignal` / `AwaitingTimer`
  (`sdk/azoa-wallet/src/workflow/types.ts:24-67`).
- Every interpolated id (`runId` / `nodeId` / `templateId` / `questId` /
  `childAvatarId`) is validated by the C# `assertUuid` analog (a `Guid.TryParse`
  guard — house rules); `gateId` is guarded as a non-empty string (contract:
  `gateId` is a free string, `sdk/azoa-wallet/src/workflow/types.ts:147-161`).
**Priority:** P0

### FR-7: Result / error model (DP2)
**Description:** A no-throw `AzoaResult<T>` mirroring the TS `Result<T, SdkError>`
no-throw discipline (`sdk/azoa-wallet/src/core/result.ts:1-19`,
`errors.ts:40-121`), with verbose `method + path` error messages
(`client.ts:1202-1217`). RECOMMENDED over throwing exceptions (DP2). The result
type and DTOs are **hand-mirrored in the SDK** (the SDK does NOT reference
`AZOA.WebAPI.csproj`).
**Acceptance:** every async method returns `Task<AzoaResult<T>>`; the only throw
is the synchronous input guard (`Guid.TryParse` failure → `ArgumentException`,
mirroring the TS `assertUuid` pre-send throw); the `AZOAResult<T>` wire envelope
is unwrapped (success ⇒ `Result`, `isError` ⇒ a typed error carrying status +
method + path); bare-object endpoints use a separate unwrap path.
**Priority:** P0

### FR-8: Auth threading + idempotency (DP4)
**Description:** An `HttpClient` + `IHttpClientFactory`-friendly transport with a
`DelegatingHandler` that (a) attaches `Authorization: Bearer <jwt>` when a token
is present, else `X-Api-Key`; (b) on a 401, single-flight-refreshes the token
(`SemaphoreSlim`-guarded, the analog of `_refreshInFlight`) and retries once; (c)
honors per-call `Idempotency-Key` and per-call `Authorization` overrides (for
`forActor`).
**Acceptance:** Bearer wins over `X-Api-Key`; one 401 triggers exactly one
refresh+retry; concurrent requests during a refresh share one in-flight refresh;
a per-call override beats the handler-default header (mirror
`sdk/azoa-wallet/src/api/client.ts:1265-1274`).
**Priority:** P0

### FR-9: DTOs (DP3)
**Description:** Request/response DTOs (records) for every facade, decorated for
`System.Text.Json`. Amounts are **strings** on the wire (NFR-4). DP3 decides the
DTO source (hand-write vs generate vs shared contracts assembly) — RECOMMENDED:
hand-write v1, record the OpenAPI-as-single-source follow-up.
**Acceptance:** DTOs deserialize the `AZOAResult<T>.result` payloads;
`JsonSerializerOptions` set once (camelCase, ignore-null) and reused.
**Priority:** P0

### FR-10: Tests (mirror the TS vitest harness)
**Description:** xUnit + FluentAssertions + a **mocked `HttpMessageHandler`** (the
C# analog of the TS `vi.stubGlobal("fetch")` harness,
`sdk/azoa-wallet/tests/workflow/run-driver.test.ts:18-19`) asserting ordered
calls + headers + URL/method/body.
**Acceptance:**
- **Ordered-call:** `Quest(a).StartAsync(); StepAsync(b); StepAsync(c)` records,
  in order, `start-workflow`, `advance {fromNodeId:b}`, `advance {fromNodeId:c}`
  (mirror `run-driver.test.ts:83-90`).
- **Hybrid:** `StartAsync → SignalAsync {gateId, payload}`.
- **Idempotency passthrough:** `StepAsync(.., idempotencyKey)` sets the header.
- **UUID guard:** a non-UUID id throws `ArgumentException` before any HTTP send.
- **forActor credential:** `ForActor(childId).StartAsync()` issues the credential
  POST first (tenant `X-Api-Key`), then uses the child JWT as `Authorization:
  Bearer` on advance/signal.
- **Result unwrap:** `AZOAResult{isError:true}` maps to a typed error with
  method + path; bare-object endpoints round-trip.
**Priority:** P0

## Non-Functional Requirements

### NFR-1: Standalone — NO WebAPI dependency
The SDK project MUST NOT reference `AZOA.WebAPI.csproj`. Result types and DTOs
are hand-mirrored in the SDK (DP2/DP3). This keeps the client deployable without
the server assembly and its transitive dependency graph.

### NFR-2: Cross-platform reach (DP1 / DP7)
`<TargetFrameworks>` chosen for reach (DP1 recommends `netstandard2.0;net8.0` to
match the SurrealDb packages and reach Unity / .NET Framework consumers).
`<Nullable>enable</Nullable>`, `<LangVersion>latest</LangVersion>`. No
platform-specific APIs.

### NFR-3: System.Text.Json only
No Newtonsoft. `System.Text.Json` 8.0.5 + `Microsoft.Bcl.AsyncInterfaces` 8.0.0
referenced **only** for the `netstandard2.0` target (mirror
`packages/Azoa.SurrealDb.Client/Azoa.SurrealDb.Client.csproj:34-39`).

### NFR-4: Amounts as strings
Every amount on the wire is a `string` (arbitrary precision — project memory; the
TS rule, e.g. `SwapParams.amountIn`, `core/types.ts:166`). The SDK NEVER models a
wire amount as `decimal` / `long` / `double`.

### NFR-5: No brand leak
**Zero** ArdaNova types, names, or economic concepts in the SDK source. The SDK
is a generic AZOA client. `ForActor` takes a plain `Guid`/string avatar id; the
tenant-credential mechanics are an internal auth detail. (Same rule as
`workflow-sdk` NFR-4.)

### NFR-6: Error model
No-throw `AzoaResult<T>` (FR-7); verbose `method + path` errors; the only throw
is the synchronous input guard.

## Rejected Alternatives

### REJECTED (LOCKED) — Node bindings / cross-language runtime sharing
Shipping a single .NET SDK and consuming it from Node via bindings (edge-js /
WASM / CLR-in-Node) was **explicitly rejected by the user**. Rationale:
1. It would make the portable TS SDK **Node-server-only**, destroying its
   browser / React Native / Lynx reach — the entire reason the TS SDK forbids
   `Buffer`/`btoa`/`atob` and ships pure-JS encoding
   (`sdk/azoa-wallet/src/core/encoding.ts`; project memory cross-platform rule).
2. It would marshal across an FFI boundary merely to wrap **an HTTP client** — the
   SDK is, at its core, `HttpClient` + JSON + auth headers. There is no native
   capability to share; both SDKs just speak HTTP to the same WebAPI.
3. It adds a heavyweight, platform-specific runtime dependency (a CLR host inside
   Node) to solve a problem that does not exist: a .NET consumer references a
   NuGet package natively; a JS consumer references the npm package natively.

**Decision:** the two SDKs are **separate hand-written clients**. The .NET SDK is
consumed natively as a NuGet reference by .NET consumers (ArdaNova). **No binding
layer of any kind is designed or built.**

### REJECTED — Referencing `AZOA.WebAPI.csproj` for its `AZOAResult<T>` + DTOs
Reusing the server's result type / DTOs directly would couple every client to the
full WebAPI assembly + transitive graph (NFR-1). Rejected in favor of
hand-mirrored types (DP2/DP3); a shared `Azoa.Contracts` package is noted as a
deferred follow-up, NOT built in this track.

## Out of Scope
- **Client-side signing / chain providers / DEX adapters (DP6).** The TS
  `Signer` / `ChainProvider` / `DexAdapter` / `algorand` / `solana` browser
  surface (`sdk/azoa-wallet/src/core/types.ts:7,68,179`) is **NOT** ported. A
  server-side .NET consumer calls the API; if it signs, it does so via AZOA's
  existing **server-side** signing core (project memory
  `ardanova-provider-port` / signing keystone), not a client-built transaction.
  "Full parity" for this track therefore means **HTTP-API + auth + workflow
  parity**, NOT browser-signing parity. This is surfaced as DP6 for the user
  because "full parity" was the stated goal.
- **The Node binding / cross-language runtime** (LOCKED rejection above).
- **An OpenAPI-as-contract single-source-of-truth migration** beyond delivering
  this SDK. DP3 recommends hand-written DTOs for v1 and records contract-gen as a
  follow-up; a full migration is its own track.
- **A shared `Azoa.Contracts` extraction** (noted under DP2 as a sub-decision;
  default-deferred — the SDK hand-mirrors types in v1).
- **Frontend / TS SDK changes.** This track adds a C# project only; it does not
  touch `sdk/azoa-wallet/` or `frontend/`.

## Dependencies
- **AZOA WebAPI** ✓ shipped — the HTTP surface the SDK binds to (15 controllers,
  Swagger wired, `AZOAResult<T>` envelope). Specced against the **shipped**
  routes the TS SDK already calls.
- **`azoa-wallet-sdk` (TS)** ✓ shipped — the reference surface this SDK achieves
  parity with (`sdk/azoa-wallet/`).
- **`workflow-sdk` (TS)** ✓ shipped — the reference for the workflow
  template-authoring + run-driver + `forActor` surface
  (`sdk/azoa-wallet/src/workflow/`).
- **`durable-workflow-engine` / `quest-api` / `tenant-onboarding`** ✓ shipped —
  the run-advancement, run-read, and child-credential endpoints the workflow
  facade calls.
- **`packages/Azoa.SurrealDb.Client`** — referenced ONLY as the **csproj
  convention template** (TargetFrameworks / Nullable / System.Text.Json
  multi-target pattern), NOT as a code dependency.

## Tier
**Tier 1 — a consumer surface.** It ships no AZOA server capability; it is the
idiomatic .NET client that lets a C# consumer (ArdaNova first) call AZOA over
HTTP at parity with the TS SDK. It sits alongside the `workflow-sdk` track in the
workflow-engine initiative as the .NET-side consumer.

## House rules (carried into Acceptance)
`dotnet build` **zero warnings** (Nullable enabled). `dotnet test` green. The SDK
project does **NOT** reference `AZOA.WebAPI.csproj`. `dotnet pack` produces a
valid `.nupkg`. **NO brand leak** (no ArdaNova types/economics). Amounts as
**strings**; a `Guid.TryParse` UUID guard on every interpolated id (the
`assertUuid` analog). Tests: xUnit + FluentAssertions + a mocked
`HttpMessageHandler` asserting ordered calls + headers + URL/method/body
(mirroring `sdk/azoa-wallet/tests/workflow/run-driver.test.ts`). One commit per
logical unit, message form **`[dotnet-client-sdk] <verb> <subject>`**. Tests run
**once at the end** (`dotnet build` + `dotnet test` sweep).
