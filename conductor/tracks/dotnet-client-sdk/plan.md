# .NET Client SDK — Plan

Build order: **csproj + result/error + auth handler (transport foundation) →
hand-mirrored DTOs → per-facade endpoint methods → the workflow run driver →
tests → pack + README.** Every phase keeps `dotnet build` at **zero warnings**
(Nullable enabled). Tests run **ONCE at the end** (`dotnet build` + `dotnet test`
sweep, per the test-once policy). The whole track is a **new, additive** C#
project — it touches no existing source and does NOT reference
`AZOA.WebAPI.csproj` (standalone client, NFR-1).

The SDK mirrors the TS SDK's **95-row parity matrix across 13 facades**
(spec.md "TS → C# parity matrix"). Where a wire shape is read from the shipped
TS SDK, the file:line is cited; the WebAPI routes are already live (the TS SDK
calls them today).

## Decisions (DP1–DP7 — recommendation + rationale)

| # | Decision | Choice (recommended) | Rationale / evidence |
|---|----------|----------------------|----------------------|
| **DP1** | Package identity + layout. | **`PackageId = Azoa.Sdk`**, project at **`sdk/Azoa.Sdk/`** (mirrors `sdk/azoa-wallet/` being the TS SDK home — `sdk/` is the SDK directory; `packages/` is infra libs). `<Version>0.1.0</Version>`, `<Authors>AZOA</Authors>`, `<Company>AZOA</Company>`, `IsPackable=true`, `IsPublishable=false` (publish deferred). | Keeps both SDKs under `sdk/` — discoverable as siblings. `packages/` is the SurrealDb-infra home (`packages/Azoa.SurrealDb.Client/`), a different concern. Metadata block copies `Azoa.SurrealDb.Client.csproj:19-32` verbatim in shape. `IsPublishable=false` matches the repo's "internal-feed, publish-decision-deferred" convention (`Azoa.SurrealDb.Client.csproj:16-18,31`). |
| **DP2** | Error model. | **A no-throw `AzoaResult<T>` in the SDK** mirroring the TS `Result<T, SdkError>` discipline (`core/result.ts:1-19`, `errors.ts:40-121`), NOT idiomatic throwing and NOT the WebAPI's `AZOAResult<T>` type-by-reference. The SDK hand-mirrors a `AzoaError` (code + message with `method + path` + status + optional server `detail`). | (a) Referencing the WebAPI's `AZOAResult<T>` violates NFR-1 (no `AZOA.WebAPI.csproj` dependency). (b) Throwing breaks parity with the TS no-throw contract and forces consumers into try/catch for routine API errors. (c) A struct-based `AzoaResult<T>` (`IsOk` / `Value` / `Error`) gives the same ergonomics the TS SDK proved (`result.ts`). The wire envelope `AZOAResult<T>` is still unwrapped at the transport boundary (success ⇒ `result`, `isError` ⇒ error — `client.ts:1134-1144`). **Contracts sub-decision: DEFER** a shared `Azoa.Contracts` package; v1 hand-mirrors types (see DP3). |
| **DP3** | DTO source. | **Hand-write DTOs as `record` types for v1**; record OpenAPI-as-single-source as an explicit follow-up. The single biggest lever on maintenance cost — flagged, not solved here. | The WebAPI has Swagger wired (`Program.cs` `AddSwaggerGen`), so NSwag/openapi-gen is *possible*, but (a) generated clients impose their own transport/result shape, fighting the no-throw `AzoaResult<T>` + auth-handler design (DP2/DP4); (b) the TS SDK hand-mirrors its DTOs and that is the proven, reviewed shape this track copies field-for-field (`api/client.ts` DTOs `:382-533`, `holon-query.ts:10-35`, `workflow/types.ts`). Hand-writing keeps v1 self-contained and lets the result/auth design lead. **The clean long-term answer is a generated/shared contract** (`OpenAPI-as-contract` or `Azoa.Contracts`) — recorded as a follow-up, scoped OUT of this track. |
| **DP4** | Auth threading. | **`HttpClient` + a `DelegatingHandler` (`AzoaAuthHandler`)**: attaches `Authorization: Bearer` when a token is present else `X-Api-Key`; on 401 does a **single-flight** token refresh (`SemaphoreSlim`-guarded, the analog of `_refreshInFlight`) + one retry; honors per-call `Idempotency-Key` and per-call `Authorization` overrides via `HttpRequestMessage` headers/`Options`. `IHttpClientFactory`-friendly (consumers can inject their own `HttpClient`). | Mirrors the TS auth path 1:1: Bearer-wins-else-X-Api-Key (`client.ts:1265-1269`), deduped refresh (`client.ts:1291-1306`), `extraHeaders` override (`client.ts:1270-1274`). A `DelegatingHandler` is the idiomatic C# seam for cross-cutting auth and keeps the per-facade methods transport-free. Per-call overrides ride `HttpRequestMessage.Options` (net8.0) / a header the handler reads, the analog of the TS `extraHeaders` bag. |
| **DP5** | Workflow run-driver ergonomics. | **An explicit `await`-per-call async handle**, NOT a forced thenable chain. `Workflow.Quest(id)` returns a `WorkflowRunHandle`; the consumer writes `await h.StartAsync(...); await h.StepAsync(b); await h.StepAsync(c);`. `ForActor` / `OnSuspend` are non-async configurators returning the handle (mild fluent), but each wire op is its own awaited call. | C# fluent-chaining across `await`s is awkward — the TS thenable trick (`run.ts:233-252`, a queued `then`) has no clean C# equivalent and would mean either blocking joins or a custom awaiter, both surprising. Explicit `await …Async` is the **more idiomatic** C# shape, is trivially ordered (sequential awaits), maps 1:1 onto the endpoints, and preserves the `AzoaResult<T>` no-throw return on each call. The handle still carries run state (`runId`, `lastStatus`, cached child token) exactly like `WorkflowRunHandle` (`run.ts:54-85`). **Surfaced for the user:** if a fluent `Start().Step().Step()` *look* is wanted, a `Task`-returning fluent wrapper can be added later; default is explicit await. |
| **DP6** | Client-side signing scope. | **Scope signing OUT of v1.** "Full parity" = **HTTP-API + auth + workflow** parity, NOT browser-signing parity. The TS `Signer` / `ChainProvider` / `DexAdapter` / `algorand` / `solana` client surface (`core/types.ts:7,68,179`) is NOT ported. A .NET consumer calls the API; signing, if needed, goes through AZOA's existing **server-side** signing core (project memory `ardanova-provider-port`). | Porting client-side chain providers + DEX adapters + tx-building to C# is large AND the **wrong layer** for a server SDK — a .NET backend consumer does not hold browser wallet keys; it calls the API and lets AZOA sign server-side. Recorded as an explicit non-goal (spec Out of Scope). **Surfaced as a user decision** because "full parity" was stated — the recommendation is to redefine parity as the HTTP/auth/workflow surface and treat signing as server-side. |
| **DP7** | Cross-platform conventions. | **`<TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>`**, `<Nullable>enable</Nullable>`, `<LangVersion>latest</LangVersion>`, `System.Text.Json` only (no Newtonsoft), **amounts as strings** on the wire. `System.Text.Json` 8.0.5 + `Microsoft.Bcl.AsyncInterfaces` 8.0.0 referenced only for the `netstandard2.0` target. | Maximises reach (Unity, .NET Framework, older tooling) the same way the SurrealDb packages do (`Azoa.SurrealDb.Client.csproj:7,34-39`). The C# analog of the TS no-Buffer rule is "no platform-specific API + netstandard2.0 reach". Amounts-as-strings carries the TS arbitrary-precision rule forward (`core/types.ts:166`; project memory). The recommendation is `netstandard2.0;net8.0` over net8.0-only because ArdaNova/Unity reach is cheap to keep and matches house convention. |

## Phase 1 — Transport foundation (csproj + result/error + auth handler)
1. `[ ]` **`sdk/Azoa.Sdk/Azoa.Sdk.csproj`** (DP1, DP7): `TargetFrameworks
   netstandard2.0;net8.0`, `Nullable enable`, `LangVersion latest`,
   `GenerateDocumentationFile`, package metadata (`PackageId Azoa.Sdk`, `Version
   0.1.0`, `Authors/Company AZOA`, `IsPackable=true`, `IsPublishable=false`),
   conditional `System.Text.Json 8.0.5` + `Microsoft.Bcl.AsyncInterfaces 8.0.0`
   for netstandard2.0 (copy the shape of
   `packages/Azoa.SurrealDb.Client/Azoa.SurrealDb.Client.csproj:1-41`). **No**
   `ProjectReference` to `AZOA.WebAPI.csproj` (NFR-1).
   **Acceptance:** `dotnet build` clean, both TFMs restore.
2. `[ ]` **`AzoaResult<T>` + `AzoaError` + `AzoaErrorCode`** (DP2) mirroring
   `core/result.ts:1-19` + `errors.ts:1-121`: a readonly struct `AzoaResult<T>`
   (`IsOk`, `Value`, `Error`, `Ok(...)`, `Fail(...)`), an `AzoaError` (Code,
   Message with `method + path`, Status, optional server `Detail` chain). The only
   throw anywhere in the SDK is the synchronous input guard.
   **Acceptance:** unit-constructible; `Fail` carries method + path.
3. `[ ]` **`Guards.AssertUuid(value, name)`** (the `assertUuid` analog,
   `client.ts:569-576`): `Guid.TryParse` → throw `ArgumentException` on failure;
   `AssertNonEmptyString(value, name)` for `gateId` (free-string contract,
   `workflow/types.ts:147-161`).
   **Acceptance:** non-UUID throws before any HTTP; empty `gateId` throws.
4. `[ ]` **`AzoaAuthHandler : DelegatingHandler`** (DP4): Bearer-else-`X-Api-Key`
   (`client.ts:1265-1269`); 401 → single-flight `SemaphoreSlim`-guarded refresh +
   one retry (`client.ts:1291-1306`); honor per-call `Idempotency-Key` +
   `Authorization` override read from `HttpRequestMessage`
   (`client.ts:1270-1274`). A `ISessionStore` (the `SessionStorage` analog,
   `session.ts:14-26`) + token holder feed the handler.
   **Acceptance:** Bearer wins; one 401 ⇒ one refresh+retry; concurrent calls
   share one refresh; per-call override beats the default header.
5. `[ ]` **`AzoaHttpTransport`**: the `request`/`requestBare` analog
   (`client.ts:1105-1180`) — builds `HttpRequestMessage`, sends via the
   `HttpClient` (auth handler in the pipeline), unwraps `AZOAResult<T>` (success
   ⇒ `result`, `isError` ⇒ `AzoaError` with method+path+status+detail,
   `client.ts:1134-1144`, `:1202-1217`) OR the bare-object path
   (`client.ts:1150-1180`). One shared `JsonSerializerOptions` (camelCase,
   ignore-null).
   **Acceptance:** AZOAResult success + error + bare-object paths covered.

## Phase 2 — DTOs (DP3, hand-written records)
6. `[ ]` **`Models/` DTOs as `record` types**, decorated for `System.Text.Json`,
   one file per facade group, field-for-field from the TS shapes: avatar
   (`AvatarResponse`), wallet, holon (`holon-query.ts:10-35`), nft / avatarnft +
   bindings, swap (`SwapQuoteParams` / `SwapQuoteResponse` / execute params,
   `client.ts:863-893`), bridge (bare), search, blockchain-operation, starodk,
   quest CRUD + templates, api-key, and the workflow DTOs
   (`workflow/types.ts:24-209`: `WorkflowRunStatus` enum, `WorkflowRunResult`,
   `WorkflowNodeExecution`, `WorkflowExecutionState`, `AdvanceParams`,
   `SignalParams`, `StartRunParams`, `ChildCredentialResult`, `AdvanceOptions`).
   **Amounts are `string`** (NFR-4). Status enums as string-serialized.
   **Acceptance:** each DTO round-trips the corresponding `result` payload;
   amounts are strings.
7. `[ ]` **`ApiPaths` static class** mirroring
   `sdk/azoa-wallet/src/api/api-version.ts:60-130` (avatar/holon/wallet/nft/
   bridge/search/swap/quest + `QUEST_START_WORKFLOW` / `QUEST_RUN_ADVANCE` /
   `QUEST_RUN_SIGNAL` / `QUEST_RUN_STATUS` / `QUEST_RUN_EXECUTION_STATE` /
   `TENANT_CHILD_CREDENTIAL`). Path builders take ids the facades have already
   guarded.
   **Acceptance:** every matrix path present and matches the TS constant.

## Phase 3 — Per-facade endpoint methods (FR-1..FR-5)
8. `[ ]` **`AzoaClient` facade + config** (`AzoaClientConfig`: `ApiUrl`,
   `Token?`, `ApiKey?`, `TimeoutMs?`, optional injected `HttpClient`), composing
   the facade properties exactly as the TS `AzoaClient` composes `auth` /
   `wallet` / `holons` / `portfolio` / `workflow`
   (`client/azoa-client.ts:85-156`).
9. `[ ]` **Auth/session facade** (FR-1): `Auth.LoginAsync` / `RegisterAsync` /
   `GetProfileAsync` / `LogoutAsync` (`auth-provider.ts:60-127`); `Session` +
   `ISessionStore` (`session.ts`); JWT subject decode for the avatar id
   (`session.ts:135-167`) — hand base64url decode, no extra dep.
10. `[ ]` **Avatar + wallet + portfolio facades** (FR-2): `Avatars.*`
    (`client.ts:639-657`); `Wallets.*` (`client.ts:758-794`); `Portfolio
    .GetAllAsync(avatarId)` using the server wallet-list + per-wallet portfolio
    read (server-side only — DP6 excludes the client chain-provider balance path,
    so `Portfolio` reads `GET /api/wallet/{id}/portfolio` rather than calling a
    local provider as `portfolio.ts:61-79` does).
11. `[ ]` **Fluent `Holons` builder** (FR-2): `Where/Named/OwnedBy/OnChain/OfType
    /OnProvider/ChildrenOf/Active/Inactive` returning `this`; `ExecuteAsync`
    (resets accumulated params after execute, `holon-query.ts:113-114`);
    `GetAsync` / `GetChildrenAsync` / `GetAncestorsAsync` / `GetDescendantsAsync`
    / `GetPeersAsync` / `GetCompositeAsync` / `CreateAsync` / `UpdateAsync` /
    `DeleteAsync` (`holon-query.ts:112-184`).
12. `[ ]` **NFT + AvatarNFT + bindings facades** (FR-3): `Nfts.*`
    (`client.ts:664-701`); `AvatarNfts.*` incl. holon/wallet binding +
    verification (`client.ts:897-985`). `AssertUuid` on every interpolated id.
13. `[ ]` **Swap + bridge + search + operations + starodk + quests facades**
    (FR-4): `Swap.GetQuoteAsync` / `ExecuteAsync(params, idempotencyKey?)`
    (header via the per-call override, `client.ts:879-893`); `Bridge.*` on the
    **bare-object** path (`client.ts:709-740`); `Search.*` / `Operations.*`;
    `Starodk.*` with the IDOR-safe `UpdateAsync` model (`client.ts:837`);
    `Quests.*` CRUD + execute + node-templates (`client.ts:992-1071`);
    `ApiKeys.*` (`client.ts:1078-1095`).
14. `[ ]` **Workflow template + run reads/writes facade** (FR-5):
    `Workflow.CreateTemplateAsync` / `GetTemplateAsync` / `ListTemplatesAsync` /
    `InstantiateAsync` / `GetRunStatusAsync` / `GetExecutionStateAsync` /
    `StartWorkflowAsync` / `AdvanceAsync(runId, fromNodeId, opts?)` /
    `SignalAsync(runId, gateId, payload?, opts?)` /
    `IssueChildCredentialAsync(childAvatarId, scopes?)`
    (`workflow/client.ts:32-193`). Idempotency-key + per-call `Authorization`
    override ride the transport's per-call header path (the `buildExtraHeaders`
    analog, `workflow/client.ts:201-209`).

## Phase 4 — The fluent run driver (FR-6, the headline)
15. `[ ]` **`WorkflowRunHandle`** (DP5) in `Workflow/WorkflowRunHandle.cs`,
    constructed by `Workflow.Quest(questId)` / `QuestFromTemplate(templateId)` /
    `QuestRun(runId)` (the `createQuestFactory` analog, `run.ts:360-377`). Holds
    `RunId?`, `LastStatus?`, `LastRun?`, the actor + cached child token.
    - `ForActor(childAvatarId)` (guard, store actor) — returns the handle.
    - `OnSuspend(Action<WorkflowRunResult>)` — returns the handle.
    - `await StartAsync(StartRunParams?)`: from template ⇒ instantiate then
      `StartWorkflowAsync`; from quest ⇒ `StartWorkflowAsync`; from run ⇒ read
      current status; binds `RunId` (`run.ts:117-144`).
    - `await StepAsync(nodeId, opts?)` → `AdvanceAsync` (`run.ts:150-161`).
    - `await SignalAsync(gateId, payload?, opts?)` → `SignalAsync`
      (`run.ts:168-182`).
    - `await StatusAsync()` → `GetRunStatusAsync`; fires `OnSuspend` when the run
      is awaiting (`run.ts:191-209`, `types.ts:59-67`).
    Each method returns `Task<AzoaResult<WorkflowRunResult>>`; ids guarded.
16. `[ ]` **Lazy child-credential acquisition** (`run.ts:305-339`): on the first
    advancement when `ForActor` is set, `IssueChildCredentialAsync` with the
    tenant `X-Api-Key`; cache `{token, expiresAt}` for the handle; re-acquire near
    expiry behind a `SemaphoreSlim` single-flight (the `_credentialInFlight`
    analog); thread the child JWT as the per-call `Authorization: Bearer` override
    on advance/signal only — tenant `X-Api-Key` stays the principal for the
    credential call. **No brand leak**: `ForActor` takes a plain avatar id.

## Phase 5 — Tests (FR-10, mirror the TS vitest harness)
17. `[ ]` **`tests/Azoa.Sdk.Tests/`** (xUnit + FluentAssertions) with a
    **`RecordingHttpMessageHandler`** — the C# analog of
    `vi.stubGlobal("fetch", mockFetch)`
    (`sdk/azoa-wallet/tests/workflow/run-driver.test.ts:18-67`) — that records
    each `HttpRequestMessage` (method, URI, headers, body) and returns queued
    canned `AZOAResult<T>` responses. Mirror the fixture style
    (stable UUIDs, `azoaResponse<T>` helper, `runResult` builder,
    `run-driver.test.ts:23-63`).
18. `[ ]` **Run-driver ordered-call test** (`run-driver.test.ts:83-90`):
    `Quest(a).StartAsync(); StepAsync(b); StepAsync(c)` ⇒ recorded calls are
    `start-workflow`, `advance {fromNodeId:b}`, `advance {fromNodeId:c}` in order.
19. `[ ]` **Hybrid + idempotency + UUID-guard + forActor tests**: `StartAsync →
    SignalAsync {gateId, payload}`; `StepAsync(.., idempotencyKey)` sets the
    `Idempotency-Key` header; a non-UUID id throws `ArgumentException` before any
    send; `ForActor(childId).StartAsync()` issues the credential POST first (tenant
    `X-Api-Key`) then uses the child JWT as `Authorization: Bearer` on
    advance/signal (`run-driver.test.ts` forActor pattern).
20. `[ ]` **Transport tests**: `AZOAResult{isError:true}` ⇒ `AzoaError` with
    method + path + status; bare-object endpoint round-trips; auth handler does
    Bearer-else-`X-Api-Key`, one 401 ⇒ one refresh+retry, concurrent calls share
    one refresh.
21. `[ ]` **A representative per-facade smoke test** for each facade group
    (one read + one write) asserting URL/method/body — enough to lock the matrix
    rows without one-test-per-method bloat.

## Phase 6 — Pack + README + final sweep (house rules)
22. `[ ]` **`sdk/Azoa.Sdk/README.md`**: quick-start (`new AzoaClient(new
    AzoaClientConfig{ ApiUrl=…, ApiKey=… })`), the `Quest(...).StartAsync()
    .StepAsync()` example, the `ForActor` example, and the amounts-as-strings +
    no-throw `AzoaResult<T>` conventions. Mirror the shape of
    `sdk/azoa-wallet/README.md`.
23. `[ ]` **`dotnet pack`** produces a valid `Azoa.Sdk.0.1.0.nupkg`
    (`IsPackable=true`); confirm the `.nupkg` contains both TFM assemblies + the
    XML doc.
24. `[ ]` **Final sweep (ONCE)**: `dotnet build` **zero warnings** + `dotnet test`
    green; grep the SDK source for `ArdaNova` / brand / economic terms → **zero**
    hits (NFR-5); confirm no `ProjectReference` to `AZOA.WebAPI.csproj` (NFR-1);
    confirm no `Newtonsoft` reference (NFR-3); confirm no `decimal`/`long` typed
    wire amount (NFR-4).

## Task flow and dependencies
```
Phase 1 (csproj + AzoaResult/AzoaError + Guards + AzoaAuthHandler + transport)
 └─ Phase 2 (DTO records + ApiPaths)
     └─ Phase 3 (facades: auth/session, avatar/wallet/portfolio, holons,
        │         nft/avatarnft, swap/bridge/search/ops/starodk/quests/apikeys,
        │         workflow template+run reads/writes)            ── parallelizable per facade
        └─ Phase 4 (WorkflowRunHandle + lazy child credential)   ── needs Phase 3 workflow facade
            └─ Phase 5 (tests: RecordingHttpMessageHandler, ordered-call,
               │         hybrid/idempotency/guard/forActor, transport, per-facade smoke)
               └─ Phase 6 (README + dotnet pack + final sweep ONCE)
```

## Commit strategy
One commit per logical unit, message form **`[dotnet-client-sdk] <verb>
<subject>`**, e.g.:
- `[dotnet-client-sdk] add Azoa.Sdk csproj (netstandard2.0;net8.0, no WebAPI ref)`
- `[dotnet-client-sdk] add AzoaResult/AzoaError no-throw result model + Guards`
- `[dotnet-client-sdk] add AzoaAuthHandler (Bearer-or-ApiKey + single-flight refresh)`
- `[dotnet-client-sdk] add AZOAResult/bare-object transport + JSON options`
- `[dotnet-client-sdk] add hand-mirrored DTO records + ApiPaths constants`
- `[dotnet-client-sdk] add auth/session + avatar/wallet/portfolio facades`
- `[dotnet-client-sdk] add fluent Holons builder + NFT/AvatarNFT facades`
- `[dotnet-client-sdk] add swap/bridge/search/starodk/quest/apikey facades`
- `[dotnet-client-sdk] add Workflow template + durable run reads/writes facade`
- `[dotnet-client-sdk] add WorkflowRunHandle fluent run driver + lazy forActor credential`
- `[dotnet-client-sdk] add xUnit suite + RecordingHttpMessageHandler harness`
- `[dotnet-client-sdk] add README + dotnet pack metadata`

Each commit leaves `dotnet build` clean; the `dotnet test` sweep runs **once** at
the end of Phase 5/6 (test-once policy).

## tracks.md row (orchestrator adds — do NOT edit here)
Add a **pending** row `[ ]` under the **workflow-engine initiative** table
(`conductor/tracks.md:31-36`, the same section that lists `workflow-sdk`), since
this SDK is the .NET-side consumer serving ArdaNova alongside the TS
`workflow-sdk`:
```
| [dotnet-client-sdk](tracks/dotnet-client-sdk/spec.md) | `[ ]` | **Tier 1 — .NET consumer surface.** First-class C# client SDK (NuGet `Azoa.Sdk`) at parity with the TS `@azoa/sdk` HTTP/auth/workflow surface; standalone (no WebAPI ref), System.Text.Json, no-throw `AzoaResult<T>`, idiomatic async run driver. Signing scoped OUT (server-side). |
```

## Success criteria
- `sdk/Azoa.Sdk/` C# project builds with **zero warnings** (Nullable enabled),
  both TFMs (`netstandard2.0;net8.0`); `dotnet test` green; `dotnet pack` emits a
  valid `Azoa.Sdk.0.1.0.nupkg`.
- The SDK does **NOT** reference `AZOA.WebAPI.csproj` and uses **System.Text.Json
  only** (no Newtonsoft).
- All **95 parity-matrix rows across 13 facades** present, each mapping to the TS
  path + method + body; amounts on the wire are **strings**.
- No-throw `AzoaResult<T>` everywhere; the only throw is the synchronous UUID /
  non-empty-string input guard; errors carry `method + path` + status.
- The fluent run driver (`Quest(...).StartAsync().StepAsync()` explicit-await
  shape, DP5) issues ordered `start-workflow → advance → advance`; the hybrid
  `start → signal` path works; `ForActor` threads a lazily-acquired, single-flight
  child credential as a per-call Bearer override.
- Tests mirror the TS harness (mocked `HttpMessageHandler`, ordered-call +
  hybrid + idempotency + UUID-guard + forActor + transport assertions).
- **No brand leak** (zero ArdaNova/economic terms); signing scoped OUT (DP6);
  Node binding NOT built (LOCKED rejection); contract-gen + `Azoa.Contracts`
  recorded as follow-ups, not built.
- `tracks.md` row added by the orchestrator under the workflow-engine initiative.
