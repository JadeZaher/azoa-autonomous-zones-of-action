# Self Audit One Fix — Specification

## Goal

Close 9 small code bugs from the 2026-06-10 audit as a single Tier 0.5 track.
Not a refactor — one targeted fix per finding. The fixes are independent of each
other and require no architectural decisions beyond what is noted per item.

## Background

The 2026-06-10 audit (`w2-sdk-frontend.md`, summarised in
`.omc/audit/AUDIT-SUMMARY.md`) produced 9 actionable SDK/frontend findings that
do not warrant their own tracks but must not be left as scattered TODOs. This
track bundles them into a single closable unit.

Item 10 (AuthWrapper cluster) is included here as a boundary note — it was
identified by Lane C but not executed because `AuthWrapper.tsx` is outside Lane
C's owned scope. It is the natural home for a follow-up deletion.

## Confirmed defects (file:line evidence)

### 1. Jupiter adapter `Buffer.from()` cross-platform violation

`sdk/azoa-wallet/src/dex/jupiter.ts:122`

```ts
bytes: Buffer.from(data.swapTransaction, "base64"),
```

`Buffer` is a Node.js global absent in browsers and React Native without a
polyfill. Every other provider in the SDK uses the pure-JS helpers in
`sdk/azoa-wallet/src/core/encoding.ts`. The `base64Decode` export from that
file is the correct replacement.

At line 124, `requiresSigning: true` is appended to the returned
`UnsignedTransaction` object. This field is not present on the `UnsignedTransaction`
interface; TypeScript's structural-typing excess-property check does not fire on
return values, so this passes silently. Any consumer checking `tx.requiresSigning`
will receive `undefined` on a properly-typed variable.

Fix: replace `Buffer.from(data.swapTransaction, "base64")` with
`base64Decode(data.swapTransaction)` imported from `../core/encoding.js`.
Remove `requiresSigning: true` from the returned object.

### 2. Swap page broken — 404 endpoint and hardcoded mock build

`frontend/src/app/(dashboard)/swap/page.tsx:67`

```ts
const backendResp = await azoa.api.request('GET', `/api/swap/quote?...`);
```

`/api/swap/quote` does not exist in `API_PATHS` (`sdk/azoa-wallet/src/api/api-version.ts`)
or in any backend controller. This call always produces a 404.

`frontend/src/app/(dashboard)/swap/page.tsx:90-110` — `handleBuildSwap` is a
hardcoded mock that fabricates an `UnsignedTransaction`-shaped object and never
calls the SDK or the backend.

Fix (decision required — record chosen path in `plan.md`): either wire the page
to the SDK DEX adapter path (`azoa.wallet.getSwapQuote()` /
`azoa.wallet.buildSwap()`), or add a `SwapController` + SDK method pair on the
backend. Do not leave the 404 call or the mock in place.

### 3. Tinyman decimal hardcode

`sdk/azoa-wallet/src/dex/tinyman.ts:168`

```ts
const decimals = { assetIn: 6, assetOut: 6 };
```

Comment reads: `// Assume 6 decimals for ALGO/USDC testnet pair`. This produces
a wrong `expectedAmountOut` for any ASA that is not 6-decimal (most non-ALGO
tokens). Decimal metadata is available from the Algorand Indexer ASA endpoint.

At `sdk/azoa-wallet/src/dex/tinyman.ts:232`:

```ts
// const slippage = 0.005; // unused
```

Dead commented-out variable. Slippage is hardcoded at the call site (line 277)
rather than derived from `quote.slippageBps`.

Fix: look up `decimals.assetIn` / `decimals.assetOut` from indexer ASA metadata
using the existing Algorand provider's indexer path, or accept them as optional
parameters. Remove the dead `slippage` comment at line 232.

### 4. Settings page chains type mismatch and private-field cast

`frontend/src/app/(dashboard)/settings/page.tsx:43`

```ts
const chains = azoa.wallet?.chains ?? {}
```

`AzoaWallet.chains` is `string[]`, not `Record<string, …>`. The `?? {}` fallback
is the wrong type (object instead of array) and `azoa.wallet` is always defined
on an authenticated `AzoaClient` so the optional chain `?.` is unnecessary.

`frontend/src/app/(dashboard)/settings/page.tsx:46`

```ts
const apiBaseUrl = (azoa as unknown as { config?: { apiUrl?: string } }).config?.apiUrl ?? ...
```

This casts through `unknown` to read a private `config` field on `AzoaApiClient`.
The cast silently returns `undefined` in practice because `config` is `private`.

Fix: treat `azoa.wallet.chains` as `string[]` directly; remove `?? {}` and the
unnecessary `?.`. Introduce a public `getApiUrl()` accessor on `AzoaClient` (or
expose via the session adapter) and replace the cast.

### 5. Algorand `encodeTransaction` msgpack gap

`sdk/azoa-wallet/src/algorand/provider.ts:416-417`

```ts
signature = ed25519.sign(signingBytes, privateKey);
encoded = undefined; // Full msgpack envelope needs algosdk.encodeObj()
```

`sdk/azoa-wallet/src/algorand/provider.ts:492-504` — `encodeAlgorandTransaction()`
prepends the `"TX"` bytes prefix to raw JSON bytes, not to msgpack-encoded
transaction fields. The doc-comment on the method explicitly notes this is "NOT
a valid msgpack-encoded transaction object" and calls it a future enhancement.

The native Ed25519 signing path is therefore structurally incomplete: it signs
something, sets `encoded = undefined`, and returns a result with a missing
encoded payload.

Fix (decision required — record chosen path in `plan.md`): implement canonical
msgpack encoding (import a pure-JS msgpack library; no `Buffer` / algosdk
dependency), OR document the method as "wallet-adapter only" and remove the
dead native Ed25519 signing helper along with `encodeAlgorandTransaction`. Do
not leave the half-implementation that sets `encoded = undefined`.

### 6. SDK `listNfts()` missing despite `NFT_LIST` path declared

`sdk/azoa-wallet/src/api/api-version.ts:92`

```ts
NFT_LIST: "/api/nft",
```

Note: per Lane C's blockers report (`.omc/autopilot/lane-c-blockers.md`),
`api-version.ts` is NOT dead — `resolveApiPath` and `API_PATHS` are re-exported
from `sdk/azoa-wallet/src/api/index.ts:55-56`. The W2 audit's dead-code claim
was incorrect.

`NFT_LIST` is declared but `AzoaApiClient` (`sdk/azoa-wallet/src/api/client.ts`)
has no `listNfts()` method. The NFTs page fetches individual NFTs by ID only.

Fix: confirm whether `GET /api/nft` in `AZOA.WebAPI/Controllers/NftController.cs`
returns a list. If yes, add `listNfts(params?: { avatarId?: string }): Promise<…>`
to `AzoaApiClient`. If the endpoint does not exist, document the gap in a
`// TODO:` with a link to this track's closure.

### 7. `updateSTARODK` missing — no `PUT /api/starodk/:id`

The backend has `createSTARODK` and `deleteSTARODK` but no update endpoint.
`AzoaApiClient` has no `updateSTARODK()` method.

Fix (decision required — record in `plan.md`): either add `PUT /api/starodk/:id`
on the backend and a corresponding `updateSTARODK(id, model)` SDK method, or
document that STARODK records are immutable after creation and add a code comment
to that effect in `AzoaApiClient`.

### 8. `HolonQueryBuilder.getComposite()` path mismatch

`sdk/azoa-wallet/src/client/holon-query.ts:149-150`

```ts
async getComposite(id: string): Promise<Result<unknown, SdkError>> {
  return this.api.request<unknown>("GET", `/api/holon/${id}/compose`);
```

`API_PATHS` has no `compose` sub-path for holons; `api-version.ts` defines only
`HOLON_GET: "/api/holon/:id"`. If the backend route is `/api/holon/:id` (no
sub-path), every call to `getComposite()` is a 404.

Fix: confirm the backend controller route in `HolonController.cs`. Either update
the SDK path to match the actual controller action, or add a `/compose` endpoint
to `HolonController` and update `API_PATHS`.

### 9. `useWallets` bypasses `azoa.api.listWallets()`

`frontend/src/lib/azoa-hooks.ts:132`

```ts
const result = await azoa.api.request<Array<…>>(
  "GET", `/api/wallet?avatarId=${avatarId}`
)
```

`AzoaApiClient` already has a typed `listWallets({ avatarId })` method that
constructs this same request with `assertUuid` validation and proper typing.
The hook duplicates the logic with a raw `request()` call, bypassing the
validation and typed return.

Fix: replace with `azoa.api.listWallets({ avatarId })`.

### 10. AuthWrapper cluster removal (boundary note)

Lane C confirmed (`lane-c-blockers.md §1`) that `frontend/src/components/AuthWrapper.tsx`
is unconsumed — no import of it exists outside its own definition. The following
files form a dead cluster that should be deleted atomically:

- `frontend/src/components/AuthWrapper.tsx`
- `frontend/src/components/AvatarNFTDashboard.tsx` (imported only by AuthWrapper)
- `frontend/src/components/BlockchainDashboard.tsx` (imported only by AuthWrapper)
- `frontend/src/components/WalletManager.tsx` (imported only by AuthWrapper)
- `frontend/src/components/TransactionHistory.tsx` (imported only by AuthWrapper)
- `frontend/src/lib/auth-simple.tsx` (imported only by AuthWrapper)
- `frontend/src/lib/auth.tsx` (imported only by `frontend/src/__tests__/auth.test.tsx`,
  which is itself stale if auth.tsx is dead)
- `frontend/src/components/TestInterface.tsx`

This is a deletion task, not a code fix. It is scoped here so it lands in the
same closable track rather than floating as a note.

## Acceptance criteria

- [ ] Finding 1: `jupiter.ts:122` uses `base64Decode` from `core/encoding.ts`; `requiresSigning: true` field removed from return.
- [ ] Finding 2: Swap page wired to real backend SwapController OR SDK DEX adapter; the 404 call and the mock build are both gone. Decision recorded in `plan.md`.
- [ ] Finding 3: Tinyman decimals derived from indexer ASA metadata (or accepted as parameters); dead `slippage` comment at `:232` removed.
- [ ] Finding 4: `settings/page.tsx` treats `AzoaWallet.chains` as `string[]`; `?? {}` and unnecessary `?.` removed; `getApiUrl()` public accessor introduced on `AzoaClient`; cast through `unknown` removed.
- [ ] Finding 5: Algorand msgpack implemented and `encoded` is never `undefined` on success, OR native signing helper removed with a clear doc note. Decision recorded in `plan.md`.
- [ ] Finding 6: `listNfts()` added to `AzoaApiClient` (if backend endpoint exists), OR gap documented with a `// TODO:` comment referencing the track.
- [ ] Finding 7: `updateSTARODK` added (backend + SDK), OR immutability documented. Decision recorded in `plan.md`.
- [ ] Finding 8: `getComposite()` path reconciled with backend controller route; `API_PATHS` updated if a new route is added.
- [ ] Finding 9: `azoa-hooks.ts:132` uses `azoa.api.listWallets({ avatarId })`.
- [ ] Finding 10: AuthWrapper cluster (8 files) deleted; `auth.test.tsx` deleted if stale.
- [ ] All SDK fixes have a unit test (vitest) where a meaningful test is possible.
- [ ] SDK build green: `cd sdk/azoa-wallet && pnpm build`.
- [ ] SDK test suite green: `pnpm test` (84+ tests).
- [ ] .NET build green: `dotnet build`.
- [ ] No `Buffer`, `btoa`, `atob`, `window`, `document` references introduced into `sdk/azoa-wallet/src/`.
- [ ] `tracks.md` row for this track moves to `[x]` Shipped.

## Out of scope

- Refactors (beyond what is explicitly described per finding).
- New features.
- Coverage gaps from W4 (test-suite audit).
- Frontend typecheck (per project memory `no-frontend-typecheck`).
- Provider-level completions (Algorand `validateAddress` checksum, Solana `buildMint` msgpack) — those are separate scope.

## Tier

Tier 0.5 — pre-launch hygiene. Independent of api-safety-hardening and
surrealdb-migration; can land in any order.

## Dependencies

None. All fixes are self-contained within their respective files.
