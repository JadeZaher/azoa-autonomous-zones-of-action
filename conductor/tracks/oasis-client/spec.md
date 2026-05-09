# Track: oasis-client

## Overview

Build a higher-level OasisClient in the SDK that wraps OasisApiClient + OasisWallet to provide holon querying, avatar-based OAuth integration for external apps, session management, and cross-chain portfolio aggregation.

## Background

The current SDK has two separate clients:
- `OasisApiClient` — typed HTTP client for all .NET API endpoints
- `OasisWallet` — wallet-of-wallets facade for client-side chain operations

External app developers need a unified client that:
1. Manages authentication sessions (JWT lifecycle)
2. Provides a fluent API for querying holons (the primary data structure)
3. Exposes the OASIS avatar system as an OAuth-compatible identity provider
4. Aggregates wallet balances across chains for portfolio views

## Requirements

### FR-1: OasisClient Facade
```typescript
const oasis = new OasisClient({
  apiUrl: "https://api.oasis.example",
  wallet: { algorand: { ... }, solana: { ... } },
  session: { storage: localStorage }, // or AsyncStorage for RN
});
```
- Composes OasisApiClient + OasisWallet
- Single entry point for all OASIS operations

### FR-2: Session Manager
- Login/register via avatar API
- JWT storage via pluggable adapter (localStorage, AsyncStorage, SecureStore)
- Auto-refresh before expiry
- `onSessionChange` callback for UI frameworks
- `oasis.session.isAuthenticated` / `oasis.session.avatarId`

### FR-3: Holon Query Builder
```typescript
const holons = await oasis.holons
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
const authProvider = oasis.createAuthProvider({
  clientId: "my-app",
  redirectUri: "https://myapp.com/callback",
  scopes: ["profile", "wallets", "holons"],
});

// Redirect to OASIS login
const loginUrl = authProvider.getLoginUrl();
// Exchange code for session
const session = await authProvider.handleCallback(code);
```
- OAuth 2.0 compatible flow (authorization code or implicit)
- Scope-based access to avatar profile, wallets, holons
- Works as an identity provider for third-party apps

### FR-5: Portfolio Aggregator
```typescript
const portfolio = await oasis.portfolio.getAll();
// Returns: { total: "$1,234", chains: [{ chain: "algorand", balance: "100 ALGO", ... }] }
```
- Queries all wallets for the authenticated avatar
- Calls wallet.getBalance for each chain
- Aggregates with fiat conversion (optional price feed)

## Technical Design

### File Structure
```
sdk/oasis-wallet/src/
  client/
    oasis-client.ts      — Main OasisClient class
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
- OasisClient can login, query holons, check balances in < 10 lines of code
- Session auto-refreshes JWT without consumer intervention
- HolonQueryBuilder produces correct API calls for all filter combinations
- OAuth adapter can be integrated into a Next.js/React app as an auth provider
- Portfolio aggregates balances across all registered chains
- Works in browser, React Native, and Lynx
