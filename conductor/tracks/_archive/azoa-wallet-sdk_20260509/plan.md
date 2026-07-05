# Implementation Plan: @azoa/wallet-sdk

## Overview

Six phases building from project scaffolding through chain-specific builders, the wallet facade, API client, DEX adapters, and cross-platform verification. Each phase ends with a verification checkpoint. All tasks follow TDD (Red -> Green -> Refactor).

**Estimated total effort:** 8-12 days

---

## Phase 1: Project Scaffolding and Core Types

**Goal:** Set up the `sdk/azoa-wallet/` package with build tooling, linting, test infrastructure, and shared type definitions.

### Tasks

- [ ] Task 1.1: Initialize package structure
  Create `sdk/azoa-wallet/` with `package.json` (name: `@azoa/wallet-sdk`, private for now), `tsconfig.json` (strict, ESNext target, module NodeNext), `.gitignore`. Add dev dependencies: `typescript`, `tsup`, `vitest`, `@types/node`. No TDD — pure scaffolding.

- [ ] Task 1.2: Configure tsup build
  Create `tsup.config.ts` with multiple entry points: `src/index.ts`, `src/algorand/index.ts`, `src/solana/index.ts`, `src/dex/index.ts`, `src/api/index.ts`. Output ESM + CJS. External: `algosdk`, `@solana/web3.js`, `@solana/spl-token`. Verify `pnpm build` (or `npm run build`) produces `dist/` with correct outputs.

- [ ] Task 1.3: Configure vitest
  Create `vitest.config.ts`. Confirm `pnpm test` runs and reports zero tests. Add coverage config (v8 provider, thresholds at 80%).

- [ ] Task 1.4: Define core result types (TDD)
  Write tests for `Result<T, E>` discriminated union: `Result.ok(value)`, `Result.err(error)`, `result.isOk()`, `result.unwrap()`, `result.map()`, `result.mapErr()`. Implement in `src/core/result.ts`.

- [ ] Task 1.5: Define SdkError type (TDD)
  Write tests for `SdkError` class: `code` (enum: `NETWORK_ERROR`, `SIGNING_ERROR`, `INVALID_ADDRESS`, `INSUFFICIENT_FUNDS`, `DEX_ERROR`, `API_ERROR`, `UNKNOWN`), `message`, `chain?`, `cause?`. Implement in `src/core/errors.ts`.

- [ ] Task 1.6: Define Signer interface and types (TDD)
  Write tests for `Signer` interface compliance check utility. Define `Signer { sign(message: Uint8Array): Promise<Uint8Array>; publicKey: Uint8Array }`. Define `ChainNetwork` enum (`devnet`, `testnet`, `mainnet`). Define `ChainId` type (`"algorand" | "solana"`). Define `TransactionResult { txHash: string; chain: ChainId; status: "submitted" | "confirmed" | "failed" }`. Implement in `src/core/types.ts`.

- [ ] Task 1.7: Create Uint8Array utility helpers (TDD)
  Write tests for `toHex`, `fromHex`, `concatBytes`, `equalsBytes` — no Node `Buffer` dependency. Implement in `src/core/bytes.ts`.

- [ ] Verification: `pnpm build` succeeds, `pnpm test` passes, dist output contains ESM + CJS bundles with correct entry points. [checkpoint marker]

---

## Phase 2: Algorand Transaction Builder

**Goal:** Implement Algorand transaction construction and signing using `algosdk`.

### Tasks

- [ ] Task 2.1: Define AlgorandBuilder interface (TDD)
  Write interface tests (type-level) for `IAlgorandBuilder` with methods: `buildPayment`, `buildAsaOptIn`, `buildAsaTransfer`, `buildAppCall`. Each returns `Promise<Result<Uint8Array, SdkError>>`. Define in `src/algorand/types.ts`.

- [ ] Task 2.2: Implement payment transaction builder (TDD)
  Write test: given sender, receiver, amount, note, and a mock `algosdk.makePaymentTxnWithSuggestedParamsFromObject`, assert correct params are passed and signed bytes returned. Implement `AlgorandBuilder.buildPayment()` in `src/algorand/builder.ts`.

- [ ] Task 2.3: Implement ASA opt-in transaction (TDD)
  Write test: given sender and assetId, assert `algosdk.makeAssetTransferTxnWithSuggestedParamsFromObject` is called with sender === receiver, amount 0. Implement `AlgorandBuilder.buildAsaOptIn()`.

- [ ] Task 2.4: Implement ASA transfer transaction (TDD)
  Write test: given sender, receiver, assetId, amount, assert correct asset transfer tx is built. Implement `AlgorandBuilder.buildAsaTransfer()`.

- [ ] Task 2.5: Implement application call transaction (TDD)
  Write test: given appId, sender, appArgs, assert `algosdk.makeApplicationCallTxnFromObject` called correctly. Implement `AlgorandBuilder.buildAppCall()`.

- [ ] Task 2.6: Implement signing integration (TDD)
  Write test: given an unsigned tx and a `Signer`, assert `signer.sign()` is called with the tx bytes and the result is a properly formatted signed tx. Implement `AlgorandBuilder.sign()`.

- [ ] Task 2.7: Implement suggested params fetcher (TDD)
  Write test: mock Algorand algod client, assert `getSuggestedParams()` returns params and they are cached for a configurable TTL. Implement in `src/algorand/client.ts`.

- [ ] Task 2.8: Implement Algorand balance and asset queries (TDD)
  Write test: mock algod/indexer responses, assert `getBalance(address)` and `getAssets(address)` return normalized results. Implement in `src/algorand/queries.ts`.

- [ ] Verification: All Algorand builder tests pass. Build still produces correct bundles. `src/algorand/index.ts` exports all public API. [checkpoint marker]

---

## Phase 3: Solana Transaction Builder

**Goal:** Implement Solana transaction construction and signing using `@solana/web3.js`.

### Tasks

- [ ] Task 3.1: Define SolanaBuilder interface (TDD)
  Write interface tests for `ISolanaBuilder` with methods: `buildTransfer`, `buildSplTransfer`, `buildVersionedTransaction`. Define in `src/solana/types.ts`.

- [ ] Task 3.2: Implement SOL transfer transaction (TDD)
  Write test: given sender, receiver, lamports, assert `SystemProgram.transfer` instruction is created correctly. Implement `SolanaBuilder.buildTransfer()` in `src/solana/builder.ts`.

- [ ] Task 3.3: Implement SPL token transfer (TDD)
  Write test: given mint, sender, receiver, amount, decimals, assert associated token account derivation and `createTransferInstruction` are used. Handle case where ATA does not exist (include create ATA instruction). Implement `SolanaBuilder.buildSplTransfer()`.

- [ ] Task 3.4: Implement versioned transaction support (TDD)
  Write test: given instructions and address lookup tables, assert a `VersionedTransaction` (v0) is built with correct `MessageV0`. Implement `SolanaBuilder.buildVersionedTransaction()`.

- [ ] Task 3.5: Implement signing integration (TDD)
  Write test: given a `Transaction` or `VersionedTransaction` and a `Signer`, assert signing produces valid serialized bytes. Implement `SolanaBuilder.sign()`.

- [ ] Task 3.6: Implement recent blockhash fetcher (TDD)
  Write test: mock `Connection.getLatestBlockhash()`, assert caching with TTL. Implement in `src/solana/client.ts`.

- [ ] Task 3.7: Implement Solana balance and token queries (TDD)
  Write test: mock RPC responses for `getBalance`, `getTokenAccountsByOwner`. Implement in `src/solana/queries.ts`.

- [ ] Verification: All Solana builder tests pass. Build produces correct bundles. `src/solana/index.ts` exports all public API. [checkpoint marker]

---

## Phase 4: AZOA API Client and Wallet Facade

**Goal:** Build the typed AZOA API HTTP client and the wallet-of-wallets facade that unifies chain builders.

### Tasks

- [ ] Task 4.1: Define AZOA API response types (TDD)
  Write tests for `AZOAResult<T>` type parsing from JSON. Mirror the server-side `AZOAResult<T>` / `AZOAResponse` shape. Define request/response types for avatar, wallet, NFT, bridge, holon endpoints. Implement in `src/api/types.ts`.

- [ ] Task 4.2: Implement base HTTP client (TDD)
  Write tests: mock `fetch`, assert correct headers (`Authorization: Bearer ...`, `Content-Type`), error handling (network error -> `Result.err`), timeout, retry with backoff. Implement `HttpClient` in `src/api/http.ts`.

- [ ] Task 4.3: Implement Avatar API methods (TDD)
  Write tests for `register`, `login`, `get`, `update`, `delete`. Assert correct URL paths, HTTP methods, request bodies. Implement in `src/api/avatar.ts`.

- [ ] Task 4.4: Implement Wallet API methods (TDD)
  Write tests for `listWallets`, `createWallet`, `setDefault`, `getPortfolio`. Implement in `src/api/wallet.ts`.

- [ ] Task 4.5: Implement NFT API methods (TDD)
  Write tests for `mint`, `transfer`, `burn`, `getMetadata`. Implement in `src/api/nft.ts`.

- [ ] Task 4.6: Implement Bridge API methods (TDD)
  Write tests for `initiate`, `complete`, `getHistory`, `getRoutes`. Implement in `src/api/bridge.ts`.

- [ ] Task 4.7: Compose AzoaApiClient (TDD)
  Write tests: `new AzoaApiClient({ baseUrl, token })` exposes `.avatar`, `.wallet`, `.nft`, `.bridge`, `.holon` namespaces. Test token refresh callback. Implement in `src/api/client.ts`.

- [ ] Task 4.8: Implement AzoaWallet facade — initialization (TDD)
  Write test: `AzoaWallet.create({ algorand: {...}, solana: {...} })` returns wallet instance with `.algorand` and `.solana` accessors. Only configured chains are available. Implement in `src/wallet.ts`.

- [ ] Task 4.9: Implement AzoaWallet facade — unified methods (TDD)
  Write tests for `wallet.getBalance(chain, address)`, `wallet.buildTransaction(chain, params)`, `wallet.signTransaction(chain, tx, signer)`, `wallet.submitTransaction(chain, signedTx)`. Assert delegation to correct chain builder. Implement in `src/wallet.ts`.

- [ ] Task 4.10: Implement AzoaWallet facade — getAssets (TDD)
  Write test: `wallet.getAssets(chain, address)` returns normalized asset list. Implement.

- [ ] Verification: API client tests pass with mocked fetch. Wallet facade delegates correctly to chain builders. Full build succeeds. [checkpoint marker]

---

## Phase 5: DEX Adapters

**Goal:** Implement Tinyman (Algorand) and Jupiter (Solana) DEX adapters and the unified swap interface.

### Tasks

- [ ] Task 5.1: Define DEX adapter interface (TDD)
  Write type tests for `IDexAdapter`: `getQuote(params)`, `buildSwapTransaction(quote, sender)`, `getSupportedPairs()`. Define `SwapQuote`, `SwapResult` types. Implement in `src/dex/types.ts`.

- [ ] Task 5.2: Implement Tinyman adapter — getQuote (TDD)
  Write test with recorded HTTP fixture: call Tinyman V2 REST API `/v2/quotes`, assert correct query params (asset_in, asset_out, amount, swap_type), parse response into `SwapQuote`. Implement in `src/dex/tinyman.ts`.

- [ ] Task 5.3: Implement Tinyman adapter — buildSwapTransaction (TDD)
  Write test: given a `SwapQuote`, call Tinyman `/v2/swap` endpoint to get transaction group, return unsigned Algorand transaction bytes. Implement in `src/dex/tinyman.ts`.

- [ ] Task 5.4: Implement Tinyman adapter — pool info (TDD)
  Write test: fetch pool info (liquidity, TVL) from Tinyman API. Implement.

- [ ] Task 5.5: Implement Jupiter adapter — getQuote (TDD)
  Write test with recorded HTTP fixture: call Jupiter V6 `/quote` endpoint, assert correct params (inputMint, outputMint, amount, slippageBps), parse response into `SwapQuote`. Implement in `src/dex/jupiter.ts`.

- [ ] Task 5.6: Implement Jupiter adapter — buildSwapTransaction (TDD)
  Write test: given a quote response, call Jupiter `/swap` endpoint with `userPublicKey`, receive serialized versioned transaction. Implement in `src/dex/jupiter.ts`.

- [ ] Task 5.7: Implement Jupiter adapter — route info (TDD)
  Write test: parse Jupiter quote response to extract route plan with intermediate swaps, price impact, fees. Implement.

- [ ] Task 5.8: Implement unified swap interface (TDD)
  Write tests for `wallet.swap({ chain, tokenIn, tokenOut, amountIn, slippage })` routing to Tinyman for `"algorand"` and Jupiter for `"solana"`. Assert `wallet.getSwapQuote()` returns quote without building tx. Implement in `src/wallet.ts` (extend facade).

- [ ] Verification: All DEX adapter tests pass with recorded fixtures. Unified swap interface routes correctly. Build succeeds with `src/dex/index.ts` exports. [checkpoint marker]

---

## Phase 6: Cross-Platform Verification and Polish

**Goal:** Validate the SDK works across browser, React Native, and Lynx. Final documentation, package exports, and CI readiness.

### Tasks

- [ ] Task 6.1: Audit Node.js-only API usage
  Write a test/script that statically checks imports across `src/` for forbidden Node APIs (`fs`, `path`, `crypto` as node module, `Buffer` direct usage). Assert zero violations. Fix any found.

- [ ] Task 6.2: Add platform polyfill guidance (TDD)
  Write test: import `src/core/platform.ts` which exports `getPlatformCrypto()` returning `crypto.getRandomValues`-compatible implementation. Assert it works without Node `crypto`. Implement with `globalThis.crypto` fallback.

- [ ] Task 6.3: Validate package.json exports map
  Configure `"exports"` field: `"."` -> core + wallet, `"./algorand"` -> algorand builder, `"./solana"` -> solana builder, `"./dex"` -> dex adapters, `"./api"` -> api client. Each with `import` (ESM) and `require` (CJS) conditions. Write test that dynamically imports each entry point.

- [ ] Task 6.4: React Native compatibility test
  Write test simulating React Native environment (no `window`, polyfilled `fetch`, `crypto.getRandomValues` via `react-native-get-random-values`). Assert core modules load without error.

- [ ] Task 6.5: Bundle size check
  Write test/script: run tsup build, check gzipped size of core entry point is under 50 KB. Assert chain modules are tree-shakeable (not included in core bundle).

- [ ] Task 6.6: Add JSDoc documentation to all public APIs
  Ensure every exported function, class, interface, and type has JSDoc with `@param`, `@returns`, `@example`. No TDD — documentation pass.

- [ ] Task 6.7: Create SDK usage examples
  Write `sdk/azoa-wallet/examples/basic-usage.ts` demonstrating: wallet creation, balance check, building + signing a transaction, swap quote, API client usage. Verify it compiles with `tsc --noEmit`.

- [ ] Task 6.8: Final test suite run and coverage check
  Run full test suite. Assert coverage >= 80% for all modules. Fix any gaps.

- [ ] Verification: Build produces ESM + CJS. All tests pass. Coverage >= 80%. Bundle size under limit. Examples compile. Package exports resolve correctly. [checkpoint marker]

---

## Summary

| Phase | Focus | Est. Effort |
|-------|-------|-------------|
| 1 | Scaffolding + Core Types | 1 day |
| 2 | Algorand Transaction Builder | 1.5 days |
| 3 | Solana Transaction Builder | 1.5 days |
| 4 | API Client + Wallet Facade | 2 days |
| 5 | DEX Adapters (Tinyman + Jupiter) | 2 days |
| 6 | Cross-Platform + Polish | 1.5 days |

**Dependencies between phases:**
- Phase 2 and 3 can run in parallel after Phase 1
- Phase 4 depends on Phase 2 and 3 (wallet facade wraps builders)
- Phase 5 depends on Phase 2 and 3 (DEX adapters produce chain-specific txs)
- Phase 6 depends on all prior phases
