# Track: azoa-client

## Overview

Build a higher-level AzoaClient in the SDK that wraps AzoaApiClient + AzoaWallet to provide holon querying, avatar-based OAuth integration for external apps, session management, and cross-chain portfolio aggregation.

## Background

The current SDK has two separate clients:
- `AzoaApiClient` — typed HTTP client for all .NET API endpoints
- `AzoaWallet` — wallet-of-wallets facade for client-side chain operations

External app developers need a unified client that:
1. Manages authentication sessions (JWT lifecycle)
2. Provides a fluent API for querying holons (the primary data structure)
3. Exposes the AZOA avatar system as an OAuth-compatible identity provider
4. Aggregates wallet balances across chains for portfolio views

## Requirements

### FR-1: AzoaClient Facade
```typescript
const azoa = new AzoaClient({
  apiUrl: "https://api.azoa.example",
  wallet: { algorand: { ... }, solana: { ... } },
  session: { storage: localStorage }, // or AsyncStorage for RN
});
```
- Composes AzoaApiClient + AzoaWallet
- Single entry point for all AZOA operations

### FR-2: Session Manager
- Login/register via avatar API
- JWT storage via pluggable adapter (localStorage, AsyncStorage, SecureStore)
- Auto-refresh before expiry
- `onSessionChange` callback for UI frameworks
- `azoa.session.isAuthenticated` / `azoa.session.avatarId`

### FR-3: Holon Query Builder
```typescript
const holons = await azoa.holons
  .where({ assetType: "NFT", chainId: "algorand" })
  .ownedBy(avatarId)
  .sortBy("createdDate", "desc")
  .page(1, 20)
  .execute();
```
- Fluent filter/sort/paginate API
- Maps to HolonQueryRequest on the backend
- Supports tree traversal: `.children()`, `.ancestors()`, `.descendants()`

### FR-4: Avatar OAuth Adapter
```typescript
// In an external app's auth flow:
const authProvider = azoa.createAuthProvider({
  clientId: "my-app",
  redirectUri: "https://myapp.com/callback",
  scopes: ["profile", "wallets", "holons"],
});

// Redirect to AZOA login
const loginUrl = authProvider.getLoginUrl();
// Exchange code for session
const session = await authProvider.handleCallback(code);
```
- OAuth 2.0 compatible flow (authorization code or implicit)
- Scope-based access to avatar profile, wallets, holons
- Works as an identity provider for third-party apps

### FR-5: Portfolio Aggregator
```typescript
const portfolio = await azoa.portfolio.getAll();
// Returns: { total: "$1,234", chains: [{ chain: "algorand", balance: "100 ALGO", ... }] }
```
- Queries all wallets for the authenticated avatar
- Calls wallet.getBalance for each chain
- Aggregates with fiat conversion (optional price feed)

## Technical Design

### File Structure
```
sdk/azoa-wallet/src/
  client/
    azoa-client.ts      — Main AzoaClient class
    session.ts           — SessionManager with storage adapters
    holon-query.ts       — HolonQueryBuilder fluent API
    auth-provider.ts     — OAuth adapter for external apps
    portfolio.ts         — Cross-chain portfolio aggregator
    index.ts             — Barrel exports
```

### Package Exports
```json
"./client": {
  "types": "./dist/client/index.d.ts",
  "import": "./dist/client/index.js",
  "require": "./dist/client/index.cjs"
}
```

## Acceptance Criteria
- AzoaClient can login, query holons, check balances in < 10 lines of code
- Session auto-refreshes JWT without consumer intervention
- HolonQueryBuilder produces correct API calls for all filter combinations
- OAuth adapter can be integrated into a Next.js/React app as an auth provider
- Portfolio aggregates balances across all registered chains
- Works in browser, React Native, and Lynx
