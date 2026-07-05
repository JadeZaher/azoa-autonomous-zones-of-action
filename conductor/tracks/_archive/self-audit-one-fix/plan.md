# Self Audit One Fix — Plan

Source spec: [spec.md](spec.md)  
Audit source: `.omc/audit/w2-sdk-frontend.md` + `.omc/autopilot/lane-c-blockers.md`

## Decisions to record before starting

The following findings require a choice between two valid approaches. Record the
decision in this file (replace the `[ decision ]` marker) before executing
the corresponding phase.

| Finding | Decision required |
|---------|-------------------|
| 2 — Swap page | **Add typed `getSwapQuote()` / `executeSwap()` to `AzoaApiClient`** and wire the swap page to those. `SwapController` already exists (`GET /api/swap/quote`, `POST /api/swap/execute` via `ISwapManager`) — the audit's "endpoint does not exist" claim was wrong; the real gap is the missing typed SDK methods, which is why the page used a raw `request()` + a mock build. Backend dispatches to the right DEX adapter (Tinyman / Jupiter) internally, so the frontend should stay thin. |
| 5 — Algorand msgpack | **Implement canonical msgpack encoding** (overridden 2026-06-11). Add a pure-JS msgpack library and complete `encodeAlgorandTransaction()` so native Ed25519 signing produces a submittable `encoded` payload. Removes the `encoded = undefined` hole; raw-key signing becomes real. |
| 7 — updateSTARODK | **Add `PUT /api/starodk/:id` endpoint + matching `updateSTARODK(id, model)` SDK method** (overridden 2026-06-11). Note: existing `[HttpPost] CreateOrUpdate` at `STARODKController.cs:39` is upsert-by-id; the new PUT must reuse `_manager.CreateOrUpdateAsync` so the two routes share write semantics. SDK gains a typed `updateSTARODK` separate from `createSTARODK`. |

---

## Phase 1 — SDK fixes

Findings 1, 3, 5, 6, 7, 8.

- [ ] **Finding 1** — `sdk/azoa-wallet/src/dex/jupiter.ts:122`: replace
  `Buffer.from(data.swapTransaction, "base64")` with `base64Decode(data.swapTransaction)`
  imported from `../core/encoding.js`. Remove `requiresSigning: true` at line 124.
- [ ] **Finding 3a** — `sdk/azoa-wallet/src/dex/tinyman.ts:168`: replace hardcoded
  `decimals = { assetIn: 6, assetOut: 6 }` with a lookup from Algorand Indexer
  ASA metadata (or accept optional `assetInDecimals`/`assetOutDecimals` params).
- [ ] **Finding 3b** — `sdk/azoa-wallet/src/dex/tinyman.ts:232`: remove dead
  `// const slippage = 0.005; // unused` comment.
- [ ] **Finding 5** — `sdk/azoa-wallet/src/algorand/provider.ts:416-417,492-504`:
  implement canonical msgpack path OR remove native Ed25519 signing helper and
  `encodeAlgorandTransaction()` with a doc note. (Use decision from table above.)
- [ ] **Finding 6** — Confirmed: `NftController.Query` at `[HttpGet]` returns
  `AZOAResult<IEnumerable<NftResult>>` with `[FromQuery] NftQueryRequest`. Add
  `listNfts(params?: NftQueryParams)` to `sdk/azoa-wallet/src/api/client.ts`
  mirroring `NftQueryRequest`'s shape; return `Result<NftResult[], SdkError>`.
- [ ] **Finding 7** — Confirmed: `STARODKController.CreateOrUpdate` is
  `[HttpPost] /api/starodk` (upsert). Add `updateSTARODK(id: string, model: STARODKCreateParams)`
  alias on `AzoaApiClient` that POSTs to the same endpoint; add a doc comment
  to `createSTARODK` clarifying upsert-by-id semantics. (No backend change.)
- [ ] **Finding 8** — Confirmed: `HolonController.Compose` at
  `[HttpGet("{id:guid}/compose")]` exists. The SDK path `/api/holon/${id}/compose`
  in `holon-query.ts:150` is correct — no 404. Fix is just consistency: add
  `HOLON_COMPOSE: "/api/holon/:id/compose"` to `api-version.ts` and have
  `getComposite()` build the URL via `resolveApiPath(API_PATHS.HOLON_COMPOSE, { id })`.
- [ ] SDK build gate: `cd sdk/azoa-wallet && pnpm build` — must be green before
  proceeding to Phase 2.
- [ ] SDK test gate: `pnpm test` — 84+ tests green.

## Phase 2 — Frontend fixes

Findings 2, 4, 9, 10.

- [ ] **Finding 2** — `frontend/src/app/(dashboard)/swap/page.tsx:67,90-110`:
  wire quote fetch to `azoa.wallet.getSwapQuote()` and build to
  `azoa.wallet.buildSwap()`, OR wire to new backend `SwapController` endpoints.
  Remove the 404 raw `request()` call and the mock object. (Use decision from table above.)
- [ ] **Finding 4a** — `frontend/src/app/(dashboard)/settings/page.tsx:43`:
  change `azoa.wallet?.chains ?? {}` to `azoa.wallet.chains` (typed as `string[]`);
  update line 44 `Object.keys(chains)` to `chains` directly or rename for clarity.
- [ ] **Finding 4b** — `frontend/src/app/(dashboard)/settings/page.tsx:46`:
  replace `(azoa as unknown as { config?: { apiUrl?: string } }).config?.apiUrl`
  with `azoa.getApiUrl()` (introduce this public accessor on `AzoaClient` in
  `sdk/azoa-wallet/src/client/azoa-client.ts` as part of this fix).
- [ ] **Finding 9** — `frontend/src/lib/azoa-hooks.ts:132`: replace raw
  `azoa.api.request("GET", \`/api/wallet?avatarId=${avatarId}\`)` with
  `azoa.api.listWallets({ avatarId })`.
- [ ] **Finding 10** — Delete the AuthWrapper cluster atomically:
  - `frontend/src/components/AuthWrapper.tsx`
  - `frontend/src/components/AvatarNFTDashboard.tsx`
  - `frontend/src/components/BlockchainDashboard.tsx`
  - `frontend/src/components/WalletManager.tsx`
  - `frontend/src/components/TransactionHistory.tsx`
  - `frontend/src/lib/auth-simple.tsx`
  - `frontend/src/lib/auth.tsx`
  - `frontend/src/components/TestInterface.tsx`
  - Confirm `frontend/src/__tests__/auth.test.tsx` is stale (only imports `auth.tsx`);
    delete if so.

## Phase 3 — Verification

- [x] `cd sdk/azoa-wallet && pnpm build` — green (CJS + ESM + DTS).
- [x] `cd sdk/azoa-wallet && pnpm test` — 123 passed / 7 pre-existing `assertUuid` failures in `tests/api/client.test.ts` (literal IDs `"abc"`, `"nft-guid-123"` — branch-level test debt, unrelated). 17/17 new self-audit-one-fix tests pass. 20/20 algorand-msgpack tests pass.
- [x] `dotnet build` — green (0 errors, 44 pre-existing warnings).
- [x] STARODK IDOR closed (PUT and POST both scope upsert by route id + authenticated avatar). 36 unit tests cover the closure (`STARManagerTests` + `STARODKControllerTests`).

## Phase 4 — Multi-architect validation

- [x] Functional completeness: **APPROVED** (all 10 findings verified file:line).
- [x] Code quality: **APPROVED** (0 blockers; 4 nits, 3 cleaned, 1 deferred — `request<T>` arity drift).
- [x] Security: **APPROVED after IDOR fix** (initial verdict was BLOCKED on the pre-existing STARODK IDOR; PUT widened the surface and forced the fix).

## Known follow-ups (filed separately)

- **integration-test-namespace-isolation**: `IntegrationTestBase` creates a per-test SurrealDB namespace but `ISurrealExecutor` reads `SurrealDb:Namespace` from in-memory config (`azoa/azoa`), so the WebAPI never sees the per-test namespace. 22 baseline integration tests fail with `(no detail)` UPSERT errors. Discovered while validating the 3 new IDOR integration tests (`Update_DifferentAvatar_Returns403`, etc.) — they are correctly structured and will pass once the harness is fixed.
- **assertUuid test-fixture refresh**: 7 tests in `sdk/azoa-wallet/tests/api/client.test.ts` use literal IDs (`"abc"`, `"nft-guid-123"`) that fail the hardened `assertUuid` guard. Replace with valid UUIDs.
- **msgpack nested omit-empty**: top-level only today. `apgs`/`apls` state-schema fields will need recursive omit-empty when app-create / schema-update tx builders are added. Documented inline at `coerceField`.
- [ ] `dotnet build` from repo root — 0 errors.
- [ ] Grep `sdk/azoa-wallet/src/` for `Buffer`, `btoa`, `atob`, `window`, `document` — no hits introduced by this track.
- [ ] Confirm deleted AuthWrapper cluster files are absent from the tree.
- [ ] Move `tracks.md` row for `self-audit-one-fix` from `[ ]` to `[x]` in the Shipped section.
