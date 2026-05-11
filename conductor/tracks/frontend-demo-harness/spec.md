# Track: frontend-demo-harness

## Overview

Rebuild the Next.js frontend with shadcn/ui as a full-featured demo harness that exercises every capability of the OASIS ecosystem — the .NET API, the @oasis/wallet-sdk, and the OasisClient. The primary goal is functional testing: every API endpoint, every wallet operation, and every integration path should be demonstrable and verifiable from the UI.

## Goals

1. **Functional test coverage** — every completed backend endpoint and SDK feature has a corresponding UI panel that exercises it
2. **Developer experience** — a single app where a developer can verify the entire stack works end-to-end
3. **Demo-ready** — presentable to stakeholders showing the full OASIS capability set
4. **Regression surface** — if a backend change breaks an SDK contract, the demo harness surfaces it immediately

## Tech Stack

- Next.js 14 (App Router, existing setup)
- shadcn/ui (Radix primitives + Tailwind)
- @oasis/wallet-sdk (local link, already configured)
- OasisClient, OasisAuthProvider, hooks from `frontend/src/lib/oasis-*`

## Architecture

```
frontend/src/
  app/
    layout.tsx              -- Root layout with OasisAuthProvider + ThemeProvider
    page.tsx                -- Dashboard landing (auth gate)
    (auth)/
      login/page.tsx        -- Login form
      register/page.tsx     -- Registration form
    (dashboard)/
      layout.tsx            -- Sidebar nav + header
      overview/page.tsx     -- System overview (chain info, provider status)
      avatars/page.tsx      -- Avatar CRUD
      holons/page.tsx       -- Holon explorer (tree view, CRUD, query builder)
      wallets/page.tsx      -- Wallet management + portfolio
      nfts/page.tsx         -- NFT lifecycle (mint, transfer, burn, metadata)
      avatar-nfts/page.tsx  -- AvatarNFT bindings, composites, verification
      blockchain/page.tsx   -- Direct chain operations (balance, tx status, token metadata)
      swap/page.tsx         -- DEX swap interface (Tinyman + Jupiter)
      bridge/page.tsx       -- Cross-chain bridge (initiate, VAA, redeem, history)
      search/page.tsx       -- Unified search with facets
      star-odk/page.tsx     -- STAR dApp generator (create, generate, deploy)
      settings/page.tsx     -- Provider selection, network config, session info
  components/
    ui/                     -- shadcn/ui components (button, card, dialog, table, etc.)
    layout/
      sidebar.tsx           -- Navigation sidebar
      header.tsx            -- Top bar with auth status + chain selector
    shared/
      result-display.tsx    -- Generic OASISResult<T> renderer
      json-viewer.tsx       -- Collapsible JSON tree for raw responses
      chain-badge.tsx       -- Chain indicator (Algorand/Solana/etc.)
      loading-skeleton.tsx  -- Consistent loading states
      error-banner.tsx      -- Error display with retry
  lib/
    oasis.ts                -- SDK singleton (existing)
    oasis-auth.tsx          -- Auth context (existing)
    oasis-hooks.ts          -- React hooks (existing)
```

## Phases

### Phase 1: Foundation (shadcn/ui + layout + auth)

**Goal:** App shell with auth flow, sidebar nav, and shadcn/ui components.

- [ ] 1.1: Install shadcn/ui (`npx shadcn@latest init`), configure Tailwind theme
- [ ] 1.2: Install core components: button, card, input, label, dialog, table, tabs, badge, toast, separator, skeleton, dropdown-menu, sheet, command, avatar
- [ ] 1.3: Build sidebar layout with nav links for all pages
- [ ] 1.4: Build header with auth status, chain selector dropdown, theme toggle
- [ ] 1.5: Build login page with real OasisAuthProvider
- [ ] 1.6: Build register page
- [ ] 1.7: Add auth gate (redirect to login if not authenticated)
- [ ] 1.8: Build ResultDisplay component (renders OASISResult with success/error states)
- [ ] 1.9: Build JsonViewer component (collapsible raw response viewer)

### Phase 2: Core Entity Pages (Avatar, Holon, Wallet)

**Goal:** Full CRUD UI for the three core entities.

- [ ] 2.1: **Avatar page** — profile view, edit form, delete with confirmation. Tests: GET/PUT/DELETE avatar endpoints
- [ ] 2.2: **Holon explorer** — tree visualization of parent/child/peer relationships, create/edit/delete dialogs, query builder with all HolonQueryRequest filters. Tests: all 15 holon endpoints
- [ ] 2.3: **Wallet page** — list wallets, create wallet form (chain selector + address), set default, delete. Portfolio card with live balance from usePortfolio hook. Tests: all 7 wallet endpoints
- [ ] 2.4: **Overview page** — chain info cards (Algorand + Solana status), provider health, session info, system stats

### Phase 3: Blockchain Operations (NFT, Swap, Bridge)

**Goal:** Exercise all blockchain-connected features.

- [ ] 3.1: **NFT page** — mint form (name, description, chainId, walletId), transfer dialog, burn confirmation, metadata viewer. Query NFTs with filters. Tests: all 6 NFT endpoints
- [ ] 3.2: **AvatarNFT page** — mint avatar NFT, bind holons/wallets, composite view, ownership verification, access verification. Tests: all 19 AvatarNFT endpoints
- [ ] 3.3: **Swap page** — token pair selector, amount input, slippage control, quote preview (price impact, fee, route), execute swap. Chain tabs for Algorand (Tinyman) and Solana (Jupiter). Tests: getQuote + buildSwapTransaction for both DEX adapters
- [ ] 3.4: **Bridge page** — initiate bridge form (source/target chain, token, recipient), bridge status tracker with step visualization (Initiated -> Locked -> AwaitingVAA -> Completed), Wormhole VAA fetch + redeem flow, bridge history table, reverse bridge. Tests: all 8 bridge endpoints
- [ ] 3.5: **Direct blockchain panel** — raw balance check, address validation, transaction status lookup, token metadata viewer. Tests wallet.getBalance, wallet.validateAddress, wallet.getTransactionStatus, wallet.getTokenMetadata, wallet.getChainInfo for both chains

### Phase 4: Search, STAR, and Advanced Features

**Goal:** Complete feature coverage.

- [ ] 4.1: **Search page** — search input with entity type filters (checkboxes for Avatars, Holons, Wallets, etc.), faceted results, pagination. Tests: POST /api/search with all SearchRequest fields
- [ ] 4.2: **STAR ODK page** — list ODKs, create form, generate dApp (shows generated code), deploy stub. Tests: all 6 STARODK endpoints
- [ ] 4.3: **Settings page** — current session info (JWT claims, expiry), provider selection (OASISRequest.providerName), network config display, SDK version info

### Phase 5: Functional Test Dashboard

**Goal:** A dedicated test runner page that systematically exercises every endpoint.

- [ ] 5.1: **Test runner page** — automated test suite that calls every API endpoint and SDK method, displays pass/fail for each
- [ ] 5.2: **Test categories:**
  - Auth flow (register -> login -> get profile -> update -> delete)
  - Holon CRUD (create -> query -> get -> update -> children/peers/ancestors -> clone -> move -> delete)
  - Wallet lifecycle (create -> set-default -> portfolio -> delete)
  - NFT lifecycle (mint -> get -> transfer -> burn)
  - AvatarNFT lifecycle (mint -> bind holon -> bind wallet -> composite -> verify ownership -> verify access -> unbind -> burn)
  - Blockchain queries (balance, validate address, tx status, token metadata, chain info — for each chain)
  - DEX (get quote for Algorand/Tinyman, get quote for Solana/Jupiter)
  - Bridge (get routes, initiate trusted bridge, get status, complete, reverse)
  - Search (search with filters, get facets)
  - STAR ODK (create, generate, deploy, delete)
- [ ] 5.3: **Test result persistence** — save results to localStorage, show history
- [ ] 5.4: **Regression indicators** — compare current run with previous, highlight regressions

### Phase 6: Polish and Documentation

- [ ] 6.1: Responsive design (mobile sidebar collapse, responsive tables)
- [ ] 6.2: Loading states and error boundaries on all pages
- [ ] 6.3: Toast notifications for mutations (created/updated/deleted)
- [ ] 6.4: Keyboard shortcuts (Ctrl+K for search, etc.)
- [ ] 6.5: README with setup instructions and demo walkthrough

## Functional Test Matrix

Every row must pass for the harness to be "green":

| Feature | .NET Endpoint | SDK Method | UI Page | Status |
|---------|--------------|------------|---------|--------|
| Register | POST /api/avatar/register | auth.register() | /register | [ ] |
| Login | POST /api/avatar/login | auth.login() | /login | [ ] |
| Get Avatar | GET /api/avatar/{id} | auth.getProfile() | /avatars | [ ] |
| Update Avatar | PUT /api/avatar/{id} | api.updateAvatar() | /avatars | [ ] |
| Delete Avatar | DELETE /api/avatar/{id} | api.deleteAvatar() | /avatars | [ ] |
| Create Holon | POST /api/holon | holons.create() | /holons | [ ] |
| Query Holons | GET /api/holon | holons.where().execute() | /holons | [ ] |
| Get Holon | GET /api/holon/{id} | holons.get() | /holons | [ ] |
| Holon Children | GET /api/holon/{id}/children | holons.getChildren() | /holons | [ ] |
| Holon Ancestors | GET /api/holon/{id}/ancestors | holons.getAncestors() | /holons | [ ] |
| Update Holon | PUT /api/holon/{id} | holons.update() | /holons | [ ] |
| Delete Holon | DELETE /api/holon/{id} | holons.delete() | /holons | [ ] |
| Create Wallet | POST /api/wallet | api.request() | /wallets | [ ] |
| List Wallets | GET /api/wallet | useWallets() | /wallets | [ ] |
| Set Default | POST /api/wallet/{id}/set-default | api.request() | /wallets | [ ] |
| Portfolio | GET /api/wallet/{id}/portfolio | usePortfolio() | /wallets | [ ] |
| Delete Wallet | DELETE /api/wallet/{id} | api.request() | /wallets | [ ] |
| Mint NFT | POST /api/nft/mint | api.mintNft() | /nfts | [ ] |
| Get NFT | GET /api/nft/{id} | api.getNft() | /nfts | [ ] |
| Transfer NFT | POST /api/nft/{id}/transfer | api.transferNft() | /nfts | [ ] |
| Burn NFT | POST /api/nft/{id}/burn | api.burnNft() | /nfts | [ ] |
| NFT Metadata | GET /api/nft/{id}/metadata | api.getNftMetadata() | /nfts | [ ] |
| Mint AvatarNFT | POST /api/avatarnft/mint | api.request() | /avatar-nfts | [ ] |
| Bind Holon | POST /api/avatarnft/{id}/holons/{hid}/bind | api.request() | /avatar-nfts | [ ] |
| Composite View | GET /api/avatarnft/{id}/composite | api.request() | /avatar-nfts | [ ] |
| Verify Ownership | POST /api/avatarnft/verify-ownership | api.request() | /avatar-nfts | [ ] |
| Algo Balance | — | wallet.getBalance("algorand") | /blockchain | [ ] |
| Sol Balance | — | wallet.getBalance("solana") | /blockchain | [ ] |
| Algo Chain Info | — | wallet.getChainInfo("algorand") | /overview | [ ] |
| Sol Chain Info | — | wallet.getChainInfo("solana") | /overview | [ ] |
| Tinyman Quote | — | wallet.getSwapQuote("algorand") | /swap | [ ] |
| Jupiter Quote | — | wallet.getSwapQuote("solana") | /swap | [ ] |
| Bridge Routes | GET /api/bridge/routes | api.getBridgeRoutes() | /bridge | [ ] |
| Bridge Initiate | POST /api/bridge/initiate | api.initiateBridge() | /bridge | [ ] |
| Bridge Status | GET /api/bridge/{id} | api.getBridgeStatus() | /bridge | [ ] |
| Bridge History | GET /api/bridge/history | api.getBridgeHistory() | /bridge | [ ] |
| Search | POST /api/search | api.search() | /search | [ ] |
| Search Facets | GET /api/search/facets | api.getSearchFacets() | /search | [ ] |
| Create STARODK | POST /api/starodk | api.request() | /star-odk | [ ] |
| Generate dApp | POST /api/starodk/{id}/generate | api.request() | /star-odk | [ ] |

## Acceptance Criteria

- All shadcn/ui components render correctly
- Auth flow works end-to-end with real JWT tokens
- Every page exercises its corresponding API endpoints
- Test runner page can execute all 38+ test cases and report pass/fail
- UI correctly handles: loading states, error responses, empty states, network failures
- Responsive down to 768px
- No console errors in production build

## Dependencies

- Requires .NET backend running (`dotnet run` or Docker)
- Requires SDK built (`cd sdk/oasis-wallet && npm run build`)
- Requires blockchain nodes accessible (testnet/devnet RPCs)
- For bridge testing: Wormhole Guardian network access (testnet)
