# Implementation Plan: frontend-demo-harness

## Overview

6 phases building from shadcn/ui foundation through entity pages, blockchain operations, search/STAR/workflow, a functional test dashboard, and polish. Phases 2-4 can partially overlap. Phase 5 (test dashboard) depends on all prior pages.

---

## Phase 1: Foundation (shadcn/ui + layout + auth)

**Goal:** App shell, navigation, auth flow, shared components.

- [ ] Task 1.1: Initialize shadcn/ui
  Run `npx shadcn@latest init` in frontend/. Configure: New York style, zinc base color, CSS variables enabled. Install components: button, card, input, label, dialog, table, tabs, badge, toast, separator, skeleton, dropdown-menu, sheet, command, avatar, scroll-area, form, select, checkbox, switch, textarea, popover, tooltip.

- [ ] Task 1.2: Build dashboard layout
  Create `app/(dashboard)/layout.tsx` with collapsible sidebar (`components/layout/sidebar.tsx`) and header (`components/layout/header.tsx`). Sidebar nav items map to all pages in the spec. Header shows: auth status (avatar name), chain selector dropdown (Algorand/Solana), theme toggle (dark/light). Nav list: /overview, /avatars, /holons, /wallets, /nfts, /avatar-nfts, /blockchain, /swap, /bridge, /search, /star-odk, /workflow, /settings.

- [ ] Task 1.3: Build auth pages
  Create `app/(auth)/login/page.tsx` and `app/(auth)/register/page.tsx` using shadcn form components. Wire to `useAzoaAuth()`. Redirect to dashboard on success. Show validation errors from API.

- [ ] Task 1.4: Build shared components
  - `components/shared/result-display.tsx` — renders any AZOAResult with success (green card) or error (red card with message)
  - `components/shared/json-viewer.tsx` — collapsible tree view for raw API responses (recursive key/value renderer)
  - `components/shared/chain-badge.tsx` — colored badge showing chain name (blue=Algorand, purple=Solana)
  - `components/shared/loading-skeleton.tsx` — card-sized skeleton matching the page layout
  - `components/shared/error-banner.tsx` — dismissible error with retry button

- [ ] Task 1.5: Auth gate middleware
  Create wrapper in `app/(dashboard)/layout.tsx` that checks `useAzoaAuth().isAuthenticated` and redirects to `/login` if false. Show loading skeleton while session restores.

- [ ] Verification: Can register, login, see dashboard with sidebar. Logout works. Auth persists on refresh.

---

## Phase 2: Core Entity Pages

**Goal:** Avatar, Holon, Wallet CRUD with full API coverage.

- [ ] Task 2.1: Overview page
  Two chain info cards (Algorand + Solana) using `useChainInfo()`. System stats: number of wallets, holons, NFTs (fetched from respective list endpoints). Session info card showing JWT claims.

- [ ] Task 2.2: Avatar page
  Profile card showing current user info from `useAzoaAuth()`. Edit form (username, email, title, firstName, lastName) with save button calling `api.updateAvatar()`. Delete account with confirmation dialog.

- [ ] Task 2.3: Holon explorer
  **This is the most complex page.**
  - Query builder panel: filters for name, chainId, assetType, providerName, isActive. Uses `HolonQueryBuilder`.
  - Results table with columns: name, chainId, assetType, isActive, createdDate. Click row to expand detail.
  - Detail panel: full holon data + JSON viewer for metadata.
  - Tree view tab: visualize parent/child/peer relationships. Click node to navigate.
  - Create dialog: form with all HolonCreateModel fields.
  - Edit dialog: pre-populated with current values.
  - Delete with confirmation.
  - Action buttons: clone, move subtree (target parent selector).

- [ ] Task 2.4: Wallet page
  - Wallet list (table) filtered by avatar. Chain badge per row.
  - Create wallet dialog: chain type selector, address input, label, isDefault checkbox.
  - Set default button per wallet.
  - Portfolio card: uses `usePortfolio()` to show live balance per chain. NFT holdings list.
  - Delete wallet with confirmation.

- [ ] Verification: All Avatar, Holon, and Wallet endpoints exercised from UI. Data persists across page navigation.

---

## Phase 3: Blockchain Operations

**Goal:** NFT lifecycle, DEX swaps, cross-chain bridge.

- [ ] Task 3.1: NFT page
  - Mint form: walletId (dropdown from user wallets), name, description, chainId, imageUri, metadata key/value pairs.
  - NFT gallery: card grid showing minted NFTs with metadata. Click for detail.
  - Transfer dialog: target avatar ID input, wallet selector.
  - Burn confirmation dialog.
  - Metadata tab: rendered NFT metadata with image preview.

- [ ] Task 3.2: AvatarNFT page
  - Mint avatar NFT form.
  - Binding manager: bind/unbind holons and wallets with permission selectors.
  - Composite view: unified card showing the AvatarNFT + all bound holons + all bound wallets.
  - Verification panel: test ownership verification, holon access, wallet access with result display.

- [ ] Task 3.3: Swap page
  - Chain tabs (Algorand | Solana).
  - Token pair selector (input mint / output mint).
  - Amount input with max button.
  - Slippage control (0.1%, 0.5%, 1%, custom).
  - Quote panel: expected output, price impact, fee, route visualization.
  - "Get Quote" button → shows quote → "Execute Swap" builds unsigned tx → shows tx descriptor.
  - For Algorand: Tinyman adapter. For Solana: Jupiter Ultra.

- [ ] Task 3.4: Bridge page
  - Route explorer: table of all supported routes from `getBridgeRoutes()`.
  - Initiate bridge form: source chain, target chain, tokenId, recipient address, amount, mode (Trusted/Wormhole).
  - Bridge tracker: step-by-step visualization (Initiated → Locked → AwaitingVAA → VAAReady → Redeeming → Completed).
  - Wormhole controls: "Fetch VAA" and "Redeem" buttons for multi-step flow.
  - History table: all bridge transactions for the authenticated avatar.
  - Reverse bridge button on completed transactions.

- [ ] Task 3.5: Direct blockchain panel
  - Chain selector tabs.
  - Balance checker: address input → getBalance result.
  - Address validator: address input → validateAddress result.
  - Transaction lookup: tx hash input → getTransactionStatus result.
  - Token metadata: tokenId input → getTokenMetadata result.
  - Chain info: auto-loaded per selected chain.

- [ ] Verification: Can mint an NFT, see it in the gallery, transfer it, burn it. Can get a swap quote. Can initiate a bridge and track status. All blockchain queries return data.

---

## Phase 4: Search, STAR, Settings

**Goal:** Complete feature coverage.

- [ ] Task 4.1: Search page
  - Search input with debounced query.
  - Entity type checkboxes (Avatar, Holon, Wallet, BlockchainOperation, STARODK).
  - Chain filter, asset type filter, date range filter.
  - Sort controls (field + direction).
  - Paginated results table with entity type badge per row.
  - Facets sidebar showing available filter values.

- [ ] Task 4.2: STAR ODK page
  - ODK list table.
  - Create ODK form: name, description, publicKey, target chain.
  - Generate dialog: bound holon selector, config options, shows generated code in code block.
  - Deploy button (stub — shows mock tx hash).
  - Delete with confirmation.

- [ ] Task 4.3: Settings page
  - Session info: JWT token (masked), expiry, avatar ID, claims.
  - Provider selection: dropdown to set AZOARequest.providerName for subsequent calls.
  - Network config: display current RPC URLs for each chain.
  - SDK version info.
  - "Clear session" button.

- [ ] Task 4.4: Workflow page
  Create `app/(dashboard)/workflow/page.tsx`. This page exercises the durable workflow engine + economic-primitive-nodes + workflow-sdk end-to-end via `azoa.workflow` (the `WorkflowClient` and `quest()` factory composed on the AzoaClient facade).

  **Template authoring panel**
  - Form to compose a quest template: name, description, and a small node DAG. Use the `nodeConfig` builders from `@azoa/sdk` to construct node config strings:
    - `nodeConfig.gateCheck({ predicate, reads? })` for gate nodes
    - `nodeConfig.emit({ payload })` for emit nodes
    - `nodeConfig.swap/grant/transfer/refund` for chain nodes (optional, Tier-2)
  - SDK calls: `azoa.workflow.createTemplate(params)` → `POST /api/quest/templates`
  - Template list table: `azoa.workflow.listTemplates()` → `GET /api/quest/templates`
  - Template detail viewer: `azoa.workflow.getTemplate(templateId)` → `GET /api/quest/templates/{id}`
  - Instantiate dialog: param key/value inputs → `azoa.workflow.instantiate(templateId, params)` → `POST /api/quest/templates/{id}/instantiate`

  **Phase-by-phase run driver panel**
  - "Start run" button: calls `azoa.workflow.quest(questId).start({ params })` → `POST /api/quest/{questId}/start-workflow`; binds the handle to the returned `runId`.
  - Node advance controls: for each node in the quest DAG, a "Step" button calls `.step(nodeId)` → `POST /api/quest/runs/{runId}/advance { fromNodeId }`. Steps chain on the same `WorkflowRunHandle` in order.
  - Optional idempotency key input (passed as `.step(nodeId, { idempotencyKey })`) — sets `Idempotency-Key` header.

  **Start-and-signal-at-gates panel**
  - Start a run (same `.start()` as above), then show a signal form when the run parks at `AwaitingSignal`: gate id input + payload input → `.signal(gateId, payload)` → `POST /api/quest/runs/{runId}/signal { gateId, payload }`.
  - Demonstrates the hybrid model: `.start()` then `.signal()` is the start-and-signal-at-gates path; `.step()` is the phase-by-phase path; both compose on the same `WorkflowRunHandle`.

  **Live run state visualizer**
  - Run status card: poll `.status()` → `GET /api/quest/runs/{runId}`. Display `WorkflowRunStatus` with color coding:
    - Active: `Pending` (grey), `Running` (blue), `Suspended` (amber), `AwaitingSignal` (amber), `AwaitingTimer` (amber)
    - Terminal: `Succeeded` (green), `Failed` (red), `Cancelled` (muted)
  - Per-node execution table: `azoa.workflow.getExecutionState(runId)` → `GET /api/quest/runs/{runId}/execution-state`. Columns: nodeId, state, startedAt, endedAt, error.
  - `.onSuspend(cb)` callback wired to surface a dismissible banner "Run parked at gate — signal to resume" when the run enters an awaiting state.

  **Demo flows (built-in)**
  - *Pure-metadata E2E* (Tier-1, chain-free): create a `HolonCreate → GateCheck → Emit` template, instantiate it, start the run, step through each node. No wallet required. This is the canonical proof that the durable engine + economic-primitive-nodes + workflow SDK work end-to-end on the chain-free path.
  - *Capability-gate check* (Tier-2, optional): create a template with a `Transfer` node, start a run without any wallet bound, attempt to step through the Transfer node — the run must enter `Failed` state with a capability-gate rejection, proving fail-closed enforcement.

- [ ] Verification: Template CRUD round-trips correctly. `quest(id).start().step(nodeId)` chain issues ordered HTTP calls and the run reaches `Succeeded`. Signal path parks at `AwaitingSignal` and resumes on `.signal()`. Live status visualizer reflects `WorkflowRunStatus` changes. Pure-metadata E2E completes without a wallet. Capability-gate demo fails closed on Transfer with no wallet. All 13 workflow rows in the Functional Test Matrix pass.

---

## Phase 5: Functional Test Dashboard

**Goal:** Automated test runner that validates the entire stack.

- [ ] Task 5.1: Test runner infrastructure
  Create `app/(dashboard)/tests/page.tsx` with a test runner that:
  - Defines test cases as an array of `{ name, category, fn: () => Promise<TestResult> }`
  - Runs all or selected categories
  - Shows real-time progress (running / passed / failed / skipped)
  - Displays results in a table with expand-to-see-details

- [ ] Task 5.2: Auth test suite
  Tests: register → login → get profile → update profile → delete account (uses temp account).

- [ ] Task 5.3: Entity CRUD test suites
  Tests for Holon, Wallet, NFT, AvatarNFT lifecycles. Each creates → reads → updates → deletes.

- [ ] Task 5.4: Blockchain query test suite
  Tests: getBalance, validateAddress, getTransactionStatus, getTokenMetadata, getChainInfo for both chains.

- [ ] Task 5.5: DEX + Bridge test suite
  Tests: getSwapQuote for both chains, getBridgeRoutes, initiateBridge (trusted mode), getBridgeStatus.

- [ ] Task 5.6: Search + STAR test suite
  Tests: search with various filters, getFacets, STARODK CRUD + generate.

- [ ] Task 5.7: Workflow test suite
  Tests (mirrors the 13 workflow rows in the Functional Test Matrix):
  - Template lifecycle: createTemplate → listTemplates → getTemplate → instantiate
  - Phase-by-phase run: quest(questId).start({ params }).step(nodeId) — verify ordered HTTP calls + run reaches Succeeded
  - Hybrid signal path: quest(questId).start().signal(gateId, payload) — verify run parks at AwaitingSignal then resumes
  - Run status poll: .status() returns WorkflowRunStatus; getExecutionState() returns per-node WorkflowNodeExecution
  - onSuspend fires on AwaitingSignal state
  - Pure-metadata E2E: HolonCreate→GateCheck→Emit completes with status Succeeded, no wallet
  - Capability-gate: Transfer node with no wallet bound → run enters Failed state (fail-closed)

- [ ] Task 5.8: Test persistence + regression
  Save test results to localStorage with timestamp. Show diff against last run. Highlight regressions in red.

- [ ] Verification: Test runner executes all 51+ test cases (including 13 workflow rows). Green/red indicators for each. Regression comparison works.

---

## Phase 6: Polish

- [ ] Task 6.1: Responsive design — sidebar collapses to sheet on mobile, tables scroll horizontally
- [ ] Task 6.2: Loading states — skeleton on every page while data fetches
- [ ] Task 6.3: Toast notifications — success/error toasts for all mutations
- [ ] Task 6.4: Keyboard shortcuts — Ctrl+K opens command palette (search), Escape closes dialogs
- [ ] Task 6.5: Error boundaries — wrap each page in an error boundary with retry
- [ ] Task 6.6: Build verification — `npm run build` produces no errors, no console warnings

---

## Summary

| Phase | Focus | Est. Effort | Parallelizable |
|-------|-------|-------------|----------------|
| 1 | Foundation (shadcn/ui + layout + auth) | 1 day | No (sequential) |
| 2 | Core entity pages (Avatar, Holon, Wallet) | 2 days | Yes (pages independent) |
| 3 | Blockchain ops (NFT, Swap, Bridge) | 2-3 days | Yes (pages independent) |
| 4 | Search, STAR, Settings, Workflow | 2 days | Yes (pages independent) |
| 5 | Functional test dashboard | 1-2 days | Depends on phases 2-4 |
| 6 | Polish | 1 day | Yes |
