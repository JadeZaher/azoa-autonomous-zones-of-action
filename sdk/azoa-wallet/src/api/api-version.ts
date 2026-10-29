/**
 * API version configuration for the AZOA SDK.
 *
 * Supports routing to different API versions and base paths.
 * When the .NET backend introduces versioned endpoints (e.g., /api/v2/user),
 * consumers can configure the version here to route automatically.
 */

export interface ApiVersionConfig {
  /** API version string, e.g. "v1", "v2". Defaults to unversioned. */
  version?: string;
  /** Base path prefix. Defaults to "/api". */
  basePath?: string;
  /** Per-controller path overrides for migration. */
  overrides?: Partial<Record<ApiController, string>>;
}

/** All .NET controllers that the SDK maps to. */
export type ApiController =
  | "user"
  | "holon"
  | "wallet"
  | "nft"
  | "usernft"
  | "bridge"
  | "search"
  | "blockchainoperation"
  | "actiongraphbundle"
  | "action"
  | "apikey"
  | "dappseries"
  | "tenant"
  | "holontypes"
  | "sagaoperator"
  | "keyrotation"
  | "credential"
  | "analytics";

/**
 * Resolves the API path for a given controller and sub-path.
 *
 * With default config: resolveApiPath("user", "/login") → "/api/user/login"
 * With version: resolveApiPath("user", "/login") → "/api/v2/user/login"
 * With override: resolveApiPath("user", "/login") → "/custom/auth/login"
 */
export function resolveApiPath(
  controller: ApiController,
  subPath: string,
  config?: ApiVersionConfig
): string {
  // Check for per-controller override first
  if (config?.overrides?.[controller]) {
    return `${config.overrides[controller]}${subPath}`;
  }

  const base = config?.basePath ?? "/api";
  const version = config?.version ? `/${config.version}` : "";

  return `${base}${version}/${controller}${subPath}`;
}

/**
 * Default (unversioned) API paths — matches current .NET route structure.
 * Update this map when the backend adds versioned routes.
 */
export const API_PATHS = {
  // User
  USER_REGISTER: "/api/user/register",
  USER_LOGIN: "/api/user/login",
  // Server-side "logout everywhere": bumps AuthNotBefore, invalidating all
  // outstanding JWTs for the user (not just the local session).
  USER_LOGOUT: "/api/user/logout",
  USER_GET: (id: string) => `/api/user/${id}`,
  USER_LIST: "/api/user",
  USER_UPDATE: (id: string) => `/api/user/${id}`,
  USER_DELETE: (id: string) => `/api/user/${id}`,
  // user-dapp-rbac: assign a user's DApp role. Operator (JWT operator:admin)
  // may assign any role incl. manager; a dapp:manager may assign only
  // developer/user. operator:admin is never assignable via this route.
  USER_DAPP_ROLE: (id: string) => `/api/user/${id}/dapp-role`,

  // Holon
  HOLON_GET: (id: string) => `/api/holon/${id}`,
  HOLON_LIST: "/api/holon",
  HOLON_CREATE: "/api/holon",
  HOLON_UPDATE: (id: string) => `/api/holon/${id}`,
  HOLON_DELETE: (id: string) => `/api/holon/${id}`,
  HOLON_CHILDREN: (id: string) => `/api/holon/${id}/children`,
  HOLON_PEERS: (id: string) => `/api/holon/${id}/peers`,
  HOLON_ANCESTORS: (id: string) => `/api/holon/${id}/ancestors`,
  HOLON_DESCENDANTS: (id: string) => `/api/holon/${id}/descendants`,
  HOLON_MINT: (id: string) => `/api/holon/${id}/mint`,
  HOLON_EXCHANGE: (id: string) => `/api/holon/${id}/exchange`,
  HOLON_COMPOSE: (id: string) => `/api/holon/${id}/compose`,
  HOLON_CLONE: (id: string) => `/api/holon/${id}/clone`,

  // Wallet
  WALLET_GET: (id: string) => `/api/wallet/${id}`,
  WALLET_LIST: "/api/wallet",
  WALLET_CREATE: "/api/wallet",
  WALLET_UPDATE: (id: string) => `/api/wallet/${id}`,
  WALLET_DELETE: (id: string) => `/api/wallet/${id}`,
  WALLET_SET_DEFAULT: (id: string) => `/api/wallet/${id}/set-default`,
  WALLET_PORTFOLIO: (id: string) => `/api/wallet/${id}/portfolio`,

  // NFT
  NFT_GET: (id: string) => `/api/nft/${id}`,
  NFT_LIST: "/api/nft",
  NFT_MINT: "/api/nft/mint",
  // fungible-mint-and-render-model (§11.3): one-shot fungible token (ASA) launch,
  // the direct (no-DAG) parallel to the FungibleTokenCreate action node.
  NFT_FUNGIBLE_MINT: "/api/nft/fungible-mint",
  NFT_TRANSFER: (id: string) => `/api/nft/${id}/transfer`,
  NFT_BURN: (id: string) => `/api/nft/${id}/burn`,
  NFT_METADATA: (id: string) => `/api/nft/${id}/metadata`,

  // Bridge
  BRIDGE_ROUTES: "/api/bridge/routes",
  BRIDGE_INITIATE: "/api/bridge/initiate",
  BRIDGE_STATUS: (id: string) => `/api/bridge/${id}`,
  BRIDGE_FETCH_VAA: (id: string) => `/api/bridge/${id}/fetch-vaa`,
  BRIDGE_REDEEM: (id: string) => `/api/bridge/${id}/redeem`,
  BRIDGE_REVERSE: (id: string) => `/api/bridge/${id}/reverse`,
  BRIDGE_HISTORY: "/api/bridge/history",

  // Search
  SEARCH: "/api/search",
  SEARCH_FACETS: "/api/search/facets",

  // Swap (SwapController)

  // Marketplace browse: public + Active actions any authenticated user may fork/start.
  ACTION_PUBLIC: "/api/action/public",

  // Action definition lifecycle (action-dag-semantic-hardening FR-2 / FR-8).
  ACTION_PUBLISH: (actionId: string) => `/api/action/${actionId}/publish`,
  ACTION_UNPUBLISH: (actionId: string) => `/api/action/${actionId}/unpublish`,

  // Durable workflow engine (durable-workflow-engine) — run-driver surface on
  // ActionController. `start-workflow` starts a durable run on an existing action;
  // `advance` is the `.step(nodeId)` primitive; `signal` un-parks a gate.
  ACTION_START_WORKFLOW: (actionId: string) => `/api/action/${actionId}/start-workflow`,
  ACTION_RUN_ADVANCE: (runId: string) => `/api/action/runs/${runId}/advance`,
  ACTION_RUN_SIGNAL: (runId: string) => `/api/action/runs/${runId}/signal`,
  ACTION_RUN_STATUS: (runId: string) => `/api/action/runs/${runId}`,
  ACTION_RUN_EXECUTION_STATE: (runId: string) => `/api/action/runs/${runId}/execution-state`,
  // All runs of an action definition (`ListRunsByAction`); user-scoped read.
  ACTION_RUNS_BY_ACTION: (actionId: string) => `/api/action/${actionId}/runs`,
  // Per-run reconciliation re-probe (reconcile-before-retry §7). Un-parks an
  // `AwaitingReconciliation` run against chain truth; NEVER re-broadcasts.
  ACTION_RUN_RECONCILE: (runId: string) => `/api/action/runs/${runId}/reconcile`,
  // Operator sweep over every parked run → { scanned, results, errors, hardStuck }.
  ACTION_RUN_RECONCILE_SWEEP: "/api/action/runs/reconcile-sweep",

  // Invite-gated actions + request/approval flow (action-invitations-approval).
  // Run-access is orthogonal to `IsPublic` discoverability: an Open action runs
  // for any viewer; an InviteOnly action runs only for owner + invited users.
  // Owner-scoped: run-access mode, direct invite/revoke, approval queue.
  ACTION_RUN_ACCESS: (actionId: string) => `/api/action/${actionId}/run-access`,
  ACTION_INVITE: (actionId: string) => `/api/action/${actionId}/invite`,
  ACTION_INVITE_REVOKE: (actionId: string, userId: string) => `/api/action/${actionId}/invite/${userId}`,
  ACTION_ACCESS_REQUESTS: (actionId: string) => `/api/action/${actionId}/access-requests`,
  // Requester-scoped: decide (owner), withdraw (requester), my outbound requests.
  ACTION_ACCESS_REQUEST_DECISION: (requestId: string) => `/api/action/access-requests/${requestId}/decision`,
  ACTION_ACCESS_REQUEST_WITHDRAW: (requestId: string) => `/api/action/access-requests/${requestId}/withdraw`,
  ACTION_ACCESS_REQUESTS_MINE: "/api/action/access-requests/mine",

  // Tenant onboarding (tenant-onboarding) — child credential issuance the
  // `forActor` actor abstraction threads (tenant acts FOR a child user).
  TENANT_CHILD_CREDENTIAL: (userId: string) => `/api/tenant/users/${userId}/credential`,
  TENANT_CUSTODIAL_CAPABILITIES: "/api/tenant/custodial-accounts/capabilities",
  TENANT_CUSTODIAL_ACCOUNT: (externalSubject: string) =>
    `/api/tenant/custodial-accounts/${encodeURIComponent(externalSubject)}`,
  TENANT_CUSTODIAL_KYC_SESSION: (externalSubject: string) =>
    `/api/tenant/custodial-accounts/${encodeURIComponent(externalSubject)}/kyc/session`,
  TENANT_CUSTODIAL_KYC_SUBMISSIONS: (externalSubject: string) =>
    `/api/tenant/custodial-accounts/${encodeURIComponent(externalSubject)}/kyc/submissions`,

  // Opt-in Holon AssetType registry (final-hardening-cutover F5). Reads are open
  // to any authenticated caller; register/deactivate/delete are Operator-scoped.
  HOLON_TYPES_LIST: "/api/holon-types",
  HOLON_TYPES_GET: (assetType: string) => `/api/holon-types/${encodeURIComponent(assetType)}`,
  HOLON_TYPES_REGISTER: "/api/holon-types",
  HOLON_TYPES_DEACTIVATE: (assetType: string) => `/api/holon-types/${encodeURIComponent(assetType)}/deactivate`,
  HOLON_TYPES_DELETE: (assetType: string) => `/api/holon-types/${encodeURIComponent(assetType)}`,

  // Saga operator dead-letter surface (SagaOperatorController, final-hardening
  // Phase-F). Operator-scoped; list/requeue/cancel the generic saga-step outbox.
  SAGA_DEAD_LETTERS: "/api/admin/sagas/dead-letters",
  SAGA_REQUEUE: (id: string) => `/api/admin/sagas/${id}/requeue`,
  SAGA_CANCEL: (id: string) => `/api/admin/sagas/${id}/cancel`,

  // Wallet wrapping-key rotation (KeyRotationController, final-hardening B5). Operator-scoped.
  KEY_ROTATION_ROTATE: "/api/admin/key-rotation/rotate",

  // Fiat on-ramp checkout (FiatCheckoutController). The subject is always the
  // route userId — no body field can redirect a payment to a different account.
  FIAT_CHECKOUT_CAPABILITIES: "/api/fiat/checkout/capabilities",
  FIAT_CHECKOUT_START: (userId: string) => `/api/fiat/checkout/${userId}`,
  FIAT_CHECKOUT_GET: (userId: string, intentId: string) => `/api/fiat/checkout/${userId}/${intentId}`,
  // The raw request key is one path segment; the node refuses a key containing '/' (400).
  FIAT_CHECKOUT_VOID_REQUEST: (userId: string, requestKey: string) =>
    `/api/fiat/checkout/${userId}/requests/${encodeURIComponent(requestKey)}/void`,

  // Fiat off-ramp / cash-out (FiatPayoutController). The subject is always the
  // route userId — no body field can redirect a cash-out to a different account.
  FIAT_PAYOUT_AVAILABILITY: (userId: string) => `/api/fiat/payouts/${userId}/availability`,
  FIAT_PAYOUT_ACCOUNT: (userId: string) => `/api/fiat/payouts/${userId}/account`,
  // Shared by POST (start a cash-out) and GET (this person's history).
  FIAT_PAYOUT_BASE: (userId: string) => `/api/fiat/payouts/${userId}`,

  // Sponsorship exchange (ExchangeController, W5.1/D50). The person the
  // exchange is for is always the route userId, never a body field.
  EXCHANGE_RUN: (userId: string) => `/api/exchange/${userId}`,
  EXCHANGE_RECEIPT: (idempotencyKey: string) =>
    `/api/exchange/receipt/${encodeURIComponent(idempotencyKey)}`,

  // Platform token reserve/genesis read (PlatformTokenController, W3.2/D49). Operator-scoped;
  // the genesis mutation is deliberately NOT exposed here (needs a fresh node-operator login).
  PLATFORM_TOKEN_GET: "/api/admin/platform-token",

  // Per-project custodial treasury reads (ProjectTreasuryController). Operator-scoped.
  PROJECT_TREASURY_GET: (chain: string, tenantId: string, projectReference: string, assetId?: string) => {
    const qs = new URLSearchParams({ chain, tenantId, projectReference });
    if (assetId) qs.set("assetId", assetId);
    return `/api/admin/project-treasuries?${qs}`;
  },
  PROJECT_TREASURY_LIST_BY_TENANT: (tenantId: string) => `/api/admin/project-treasuries/tenant/${tenantId}`,

  // Soulbound credential revocation (CredentialController, D19). Issuer-only ASA
  // clawback; NOT enabled server-side yet (credential-third-party-issuance_20260906).
  CREDENTIAL_REVOKE: (assetId: string) => `/api/credential/${encodeURIComponent(assetId)}/revoke`,

  // Analytics read surface (AnalyticsController, D16): operation history + positions.
  // Unpriced / unknown-quantity rows are flagged by enum, never rendered as zero.
  ANALYTICS_TRANSACTIONS: "/api/analytics/transactions",
  ANALYTICS_POSITIONS: "/api/analytics/positions",

  // API-key issuance discovery + rotation (dapp-developer-experience audit).
  // `scopes` lists the self-issuable scope vocabulary; `rotate` mints a successor key.
  APIKEY_SCOPES: "/api/apikey/scopes",
  APIKEY_ROTATE: (id: string) => `/api/apikey/${id}/rotate`,

  // DappSeries CRUD + action management. Reads are authenticated-user reads;
  // authoring writes require dapp:develop, lifecycle writes require dapp:manage.
  DAPP_SERIES_LIST: "/api/dapp-series",
  DAPP_SERIES_GET: (id: string) => `/api/dapp-series/${id}`,
  DAPP_SERIES_CREATE: "/api/dapp-series",
  DAPP_SERIES_UPDATE: (id: string) => `/api/dapp-series/${id}`,
  DAPP_SERIES_DELETE: (id: string) => `/api/dapp-series/${id}`,
  DAPP_SERIES_ACTIONS: (seriesId: string) => `/api/dapp-series/${seriesId}/actions`,
  DAPP_SERIES_ACTION_REMOVE: (seriesId: string, actionId: string) => `/api/dapp-series/${seriesId}/actions/${actionId}`,
  DAPP_SERIES_ACTION_ORDER: (seriesId: string, actionId: string) => `/api/dapp-series/${seriesId}/actions/${actionId}/order`,
  DAPP_SERIES_ACTION_MAPPINGS: (seriesId: string, actionId: string) => `/api/dapp-series/${seriesId}/actions/${actionId}/mappings`,

  // Public node transparency reads (NodeTransparencyController). Anonymous,
  // cacheable; no token or API key is ever sent with these — enforced by
  // AzoaApiClient.requestAnonymous (client.ts), not merely asserted (OI-70).
  NODE_TRANSPARENCY_CURRENT: "/api/node-transparency/current",
  NODE_TRANSPARENCY_GOVERNANCE_AUDIT: "/api/node-transparency/audit/governance",
  NODE_TRANSPARENCY_FEE_AUDIT: "/api/node-transparency/audit/fees",
  NODE_TRANSPARENCY_TREASURY_AUDIT: "/api/node-transparency/audit/treasury",
  NODE_TRANSPARENCY_HISTORY_CHECKPOINT: "/api/node-transparency/audit/checkpoint",

  // First-party KYC (KycController): begin/resume the hosted session, read and re-fetch status.
  KYC_SESSION: "/api/kyc/session",
  KYC_STATUS: "/api/kyc/status",
  KYC_REFRESH: "/api/kyc/refresh",
  HOSTED_SESSION_REFRESH: (sessionId: string) => `/api/operator/hosted-sessions/${sessionId}/refresh`,

  // External wallet ownership proof (WalletController, audit-1 M2).
  WALLET_OWNERSHIP_CHALLENGE: (walletId: string) => `/api/wallet/${walletId}/ownership-challenge`,
  WALLET_OWNERSHIP_PROOF: (walletId: string) => `/api/wallet/${walletId}/ownership-proof`,

  // D-3 client-signed operations (BlockchainOperationController).
  BLOCKCHAIN_OPERATION_SIGNING_PLAN: (operationId: string) => `/api/blockchain/operations/${operationId}/signing-plan`,
  BLOCKCHAIN_OPERATION_SUBMIT_SIGNED: (operationId: string) => `/api/blockchain/operations/${operationId}/submit-signed`,
  OPERATOR_STUCK_OPERATIONS: "/api/operator/blockchain-operations/stuck",
  OPERATOR_OPERATION_RESOLVE: (operationId: string) => `/api/operator/blockchain-operations/${operationId}/resolve`,

  // Deployment operator exit (DeploymentOperatorController, F-H12).
  OPERATOR_DEPLOYMENTS: "/api/operator/deployments",
  OPERATOR_DEPLOYMENT_RESOLVE: (operationId: string) => `/api/operator/deployments/${operationId}/resolve`,

  // Fiat operator resolutions (Operator policy).
  FIAT_PAYOUT_RESOLVE: (payoutId: string) => `/api/admin/fiat-payouts/${encodeURIComponent(payoutId)}/resolve`,
  FIAT_REVERSAL_RESOLVE: (reversalId: string) => `/api/admin/fiat-reversals/${encodeURIComponent(reversalId)}/resolve`,
  // RV-L1-01: operator exit for a payout-account setup stuck past its Stripe replay window.
  FIAT_PAYOUT_ACCOUNT_SETUP_RESOLVE: (userId: string) => `/api/admin/fiat-payouts/accounts/${encodeURIComponent(userId)}/setup/resolve`,

  // D-8 tenant payout float.
  TENANT_PAYOUT_BALANCE: "/api/fiat/tenant-payout-balance",
  TENANT_PAYOUT_TOP_UP: "/api/fiat/tenant-payout-balance/top-up",
  TENANT_PAYOUT_FLOAT: (tenantId: string) => `/api/admin/tenant-payout-floats/${tenantId}`,

  // Direct hold admission reads (HoldCommandsController).
  HOLD_LIABILITIES: "/api/hold-commands/liabilities",
  HOLD_LIABILITY: (holdRecordId: string) => `/api/hold-commands/liabilities/${holdRecordId}`,
  HOLD_RESERVE_ASSETS: "/api/hold-commands/reserve-assets",

  // Retired exchange rows (ExchangeController, Operator policy).
  EXCHANGE_RETIRED_ROWS: "/api/exchange/operator/retired-rows",
  EXCHANGE_RETIRED_ROW_RESOLVE: (operationId: string) => `/api/exchange/operator/retired-rows/${operationId}/resolve`,

  // Operator console surfaces.
  OPERATOR_WALLET_IDENTITIES: "/api/operator/wallet-identities",
  OPERATOR_WALLET_IDENTITY_CLEAR: "/api/operator/wallet-identities/clear-conflicting-binding",
  OPERATOR_DEX_SETTLE_REVIEW: (operationId: string) => `/api/operator/dex/operations/${operationId}/settle-review`,
  OPERATOR_WTT_OPERATION: (operationId: string) => `/api/operator/wtt/operations/${operationId}`,
  OPERATOR_WTT_ACTION: (operationId: string, action: "reconcile" | "refetch-proof" | "resume-proof") =>
    `/api/operator/wtt/operations/${operationId}/${action}`,
  OPERATOR_WTT_STAGE_KEY_READINESS: "/api/operator/wtt/stage-keys/readiness",
  OPERATOR_WTT_STAGE_KEY_REWRAP: "/api/operator/wtt/stage-keys/rewrap",
} as const;
