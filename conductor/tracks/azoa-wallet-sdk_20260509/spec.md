# Track: azoa-wallet-sdk — Specification

## Overview

Cross-platform TypeScript SDK (`@azoa/wallet-sdk`) providing client-side transaction signing, AZOA WebAPI integration, and DEX adapters for Tinyman (Algorand) and Jupiter (Solana). The SDK lives at `sdk/azoa-wallet/`, ships via tsup, and targets browser, React Native, and Lynx runtimes.

## Background

The AZOA WebAPI provides server-side blockchain provider orchestration (Algorand, Solana) with mint/burn/transfer/swap/exchange and cross-chain bridge operations. However, there is no client-side counterpart. Wallets hold private keys on the client. Transactions must be signed client-side, then submitted (or relayed through the API). A Node/browser SDK is required to:

1. Build unsigned transactions for Algorand and Solana
2. Sign them locally with user-held keys or wallet adapters
3. Submit signed transactions to the AZOA API or directly to chain RPCs
4. Provide a unified "wallet-of-wallets" facade across chains
5. Integrate with DEX REST APIs (Tinyman, Jupiter) for token swaps

## Functional Requirements

### FR-1: Algorand Transaction Builder
**Description:** Build, sign, and serialize Algorand transactions (payment, ASA opt-in, ASA transfer, application call).
**Acceptance Criteria:**
- Can construct a payment transaction with amount, sender, receiver, note
- Can construct an ASA opt-in transaction for a given asset ID
- Can construct an ASA transfer transaction
- Can construct an application call transaction with app args
- Returns a serializable `Uint8Array` of the signed transaction
- Works with `algosdk` for transaction construction and signing
**Priority:** P0

### FR-2: Solana Transaction Builder
**Description:** Build, sign, and serialize Solana transactions (system transfer, SPL token transfer, versioned transactions).
**Acceptance Criteria:**
- Can construct a system SOL transfer transaction
- Can construct an SPL token transfer (with associated token account derivation)
- Supports both legacy and versioned (v0) transactions
- Returns a serializable `Buffer`/`Uint8Array` of the signed transaction
- Works with `@solana/web3.js` and `@solana/spl-token`
**Priority:** P0

### FR-3: Wallet-of-Wallets Facade
**Description:** Unified abstraction over multiple chain wallets, exposing a single API surface for balance queries, transaction building, signing, and submission regardless of underlying chain.
**Acceptance Criteria:**
- `AzoaWallet.create({ algorand: algoConfig, solana: solConfig })` initializes multi-chain wallet
- `wallet.getBalance(chain, address)` returns normalized balance info
- `wallet.buildTransaction(chain, txParams)` delegates to chain-specific builder
- `wallet.signTransaction(chain, unsignedTx, signer)` signs with provided signer
- `wallet.submitTransaction(chain, signedTx)` submits to chain RPC or AZOA API
- `wallet.getAssets(chain, address)` returns token/NFT holdings
- Chain-specific methods accessible via `wallet.algorand.*` and `wallet.solana.*`
**Priority:** P0

### FR-4: AZOA API Client
**Description:** Typed HTTP client for the AZOA WebAPI, covering Avatar, Holon, NFT, Wallet, and Bridge endpoints.
**Acceptance Criteria:**
- `AzoaApiClient` accepts `baseUrl` and optional JWT token
- Covers avatar CRUD (register, login, get, update, delete)
- Covers wallet endpoints (list, create, set-default, portfolio)
- Covers NFT endpoints (mint, transfer, burn, metadata)
- Covers bridge endpoints (initiate, complete, history, routes)
- Covers holon endpoints (CRUD, query, search)
- All methods return typed `AZOAResult<T>` wrappers
- Supports token refresh / re-auth callback
- Uses `fetch` (browser-compatible, with polyfill path for React Native)
**Priority:** P0

### FR-5: DEX Adapter — Tinyman (Algorand)
**Description:** Adapter for Tinyman DEX providing swap quotes and swap transaction construction via Tinyman REST API.
**Acceptance Criteria:**
- `tinyman.getQuote(assetIn, assetOut, amountIn)` returns quote with expected output, price impact, and fees
- `tinyman.buildSwapTransaction(quote, senderAddress)` returns unsigned Algorand transaction group
- Supports Tinyman V2 REST API
- Handles slippage tolerance parameter
- Returns pool information (liquidity, TVL) when available
**Priority:** P1

### FR-6: DEX Adapter — Jupiter (Solana)
**Description:** Adapter for Jupiter aggregator providing swap quotes and swap transaction construction via Jupiter REST API.
**Acceptance Criteria:**
- `jupiter.getQuote(inputMint, outputMint, amount)` returns quote with routes, price impact, fees
- `jupiter.buildSwapTransaction(quote, userPublicKey)` returns unsigned versioned transaction
- Supports Jupiter V6 REST API
- Handles slippage tolerance, dynamic slippage, and priority fees
- Returns route information with intermediate swaps
**Priority:** P1

### FR-7: Unified Swap Interface
**Description:** Chain-agnostic swap interface that delegates to the appropriate DEX adapter.
**Acceptance Criteria:**
- `wallet.swap({ chain, tokenIn, tokenOut, amountIn, slippage })` routes to Tinyman or Jupiter
- Returns a normalized `SwapResult` with: `expectedOutput`, `priceImpact`, `fee`, `unsignedTx`
- `wallet.getSwapQuote(...)` returns quote without building tx
- Lists available trading pairs per chain
**Priority:** P1

### FR-8: Cross-Platform Compatibility
**Description:** SDK runs in browser, React Native, and Lynx environments.
**Acceptance Criteria:**
- tsup builds ESM and CJS outputs
- No Node.js-only APIs used in core modules (no `fs`, `crypto` node module — use `webcrypto` / polyfills)
- `Buffer` usage abstracted behind `Uint8Array` where possible
- React Native: compatible with `react-native-get-random-values` and `react-native-url-polyfill`
- Lynx: no DOM APIs in core; UI bindings are a separate optional export
- Package exports map: `"."`, `"./algorand"`, `"./solana"`, `"./dex"`, `"./api"`
**Priority:** P0

## Non-Functional Requirements

### NFR-1: Bundle Size
- Core module (wallet facade + API client) under 50 KB gzipped
- Chain-specific modules tree-shakeable; unused chains add 0 bytes
- DEX adapters are separate entry points, not bundled into core

### NFR-2: Performance
- Transaction building completes in under 200ms on mid-range mobile device
- API client requests have configurable timeout (default 30s)
- Connection pooling for RPC endpoints

### NFR-3: Security
- Private keys never leave the client; no key material sent to AZOA API
- Signer abstraction accepts external wallet adapters (Pera, Phantom, etc.)
- No `eval` or dynamic code execution
- Dependencies audited via `npm audit` with zero high/critical vulnerabilities

### NFR-4: Error Handling
- All public methods return `Result<T, SdkError>` (discriminated union, not thrown exceptions)
- `SdkError` includes: `code` (enum), `message`, `chain`, `cause` (original error)
- Network errors are retried with exponential backoff (configurable)

### NFR-5: Testing
- Unit test coverage above 80% (vitest)
- Integration tests against Algorand/Solana devnets (optional, gated by env vars)
- DEX adapter tests use recorded HTTP fixtures (msw or similar)

## User Stories

### US-1: Multi-Chain Balance Check
**As** a dapp developer,
**I want** to query balances across Algorand and Solana from a single wallet instance,
**So that** I can display a unified portfolio view.

**Given** a wallet initialized with Algorand and Solana configs
**When** I call `wallet.getBalance("algorand", algoAddress)` and `wallet.getBalance("solana", solAddress)`
**Then** I receive normalized balance objects with `amount`, `decimals`, `symbol` for each chain.

### US-2: Client-Side NFT Mint
**As** a dapp user,
**I want** to mint an NFT on Algorand by signing the transaction in my browser,
**So that** my private key never leaves my device.

**Given** an Algorand transaction builder and a local signer
**When** I build a mint transaction, sign it, and submit via `wallet.submitTransaction`
**Then** the NFT is created on-chain and the tx hash is returned.

### US-3: Token Swap via Jupiter
**As** a Solana user,
**I want** to swap USDC for SOL through Jupiter,
**So that** I get the best aggregated price.

**Given** a wallet with Jupiter DEX adapter configured
**When** I call `wallet.swap({ chain: "solana", tokenIn: USDC_MINT, tokenOut: SOL_MINT, amountIn: 100, slippage: 0.5 })`
**Then** I receive an unsigned versioned transaction ready for signing, along with expected output and price impact.

### US-4: Cross-Chain Bridge via AZOA API
**As** a dapp developer,
**I want** to initiate a bridge from Algorand to Solana using the SDK,
**So that** the complex multi-step flow is handled by a single method.

**Given** an authenticated AZOA API client
**When** I call `apiClient.bridge.initiate({ sourceChain: "algorand", targetChain: "solana", tokenId, recipientAddress })`
**Then** the bridge transaction is created server-side and I receive the bridge transaction status.

## Technical Considerations

- **Signer Abstraction:** The SDK does not manage keys directly. Instead, a `Signer` interface is provided:
  ```typescript
  interface Signer {
    sign(message: Uint8Array): Promise<Uint8Array>;
    publicKey: Uint8Array;
  }
  ```
  Adapters for Pera Wallet (Algorand) and Phantom (Solana) can wrap this interface.

- **RPC Configuration:** Each chain builder accepts `{ rpcUrl, network }`. Defaults to public devnet endpoints. Production apps should provide their own RPC URLs.

- **AZOA API Versioning:** The API client targets the current WebAPI contract. API version is pinned in the client config; breaking changes require SDK version bump.

- **tsup Build:** Dual ESM/CJS output. External peer dependencies: `algosdk`, `@solana/web3.js`, `@solana/spl-token`. These are not bundled.

- **Monorepo Path:** `sdk/azoa-wallet/` with its own `package.json`, `tsconfig.json`, `tsup.config.ts`, and `vitest.config.ts`.

## Out of Scope

- Wallet UI components (buttons, modals) — separate package
- Hardware wallet integration (Ledger, Trezor)
- On-chain smart contract deployment from the SDK
- Fiat on-ramp / off-ramp
- Support for chains beyond Algorand and Solana
- Trustless bridge logic (that lives server-side in the cross-chain bridge track)
- Key generation or mnemonic management

## Open Questions

1. **Wallet adapter protocol:** Should we adopt Algorand's WalletConnect and Solana's Wallet Adapter Standard, or define our own `Signer` interface and provide adapters? (Recommendation: own `Signer` + adapters)
2. **Tinyman API versioning:** Tinyman V2 REST API — is there a stable base URL and OpenAPI spec we can code-gen from, or do we hand-write the client?
3. **Lynx runtime:** What polyfills does Lynx require? Need to confirm `fetch`, `crypto.getRandomValues`, and `TextEncoder` availability.
4. **AZOA API auth flow:** Should the SDK handle full JWT refresh flow, or just accept a token and call a user-provided refresh callback?
5. **Error reporting:** Should SDK errors include chain-specific error codes (e.g., Algorand "overspend") or normalize everything to SDK error codes?
