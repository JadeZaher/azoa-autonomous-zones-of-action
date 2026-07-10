/**
 * API version configuration for the AZOA SDK.
 *
 * Supports routing to different API versions and base paths.
 * When the .NET backend introduces versioned endpoints (e.g., /api/v2/avatar),
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
  | "avatar"
  | "holon"
  | "wallet"
  | "nft"
  | "avatarnft"
  | "bridge"
  | "search"
  | "blockchainoperation"
  | "starodk"
  | "quest"
  | "apikey"
  | "dappseries"
  | "tenant"
  | "holontypes"
  | "sagaoperator"
  | "keyrotation";

/**
 * Resolves the API path for a given controller and sub-path.
 *
 * With default config: resolveApiPath("avatar", "/login") → "/api/avatar/login"
 * With version: resolveApiPath("avatar", "/login") → "/api/v2/avatar/login"
 * With override: resolveApiPath("avatar", "/login") → "/custom/auth/login"
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
  // Avatar
  AVATAR_REGISTER: "/api/avatar/register",
  AVATAR_LOGIN: "/api/avatar/login",
  // Server-side "logout everywhere": bumps AuthNotBefore, invalidating all
  // outstanding JWTs for the avatar (not just the local session).
  AVATAR_LOGOUT: "/api/avatar/logout",
  AVATAR_GET: (id: string) => `/api/avatar/${id}`,
  AVATAR_LIST: "/api/avatar",
  AVATAR_UPDATE: (id: string) => `/api/avatar/${id}`,
  AVATAR_DELETE: (id: string) => `/api/avatar/${id}`,
  // avatar-dapp-rbac: assign an avatar's DApp role. Operator (JWT operator:admin)
  // may assign any role incl. manager; a dapp:manager may assign only
  // developer/user. operator:admin is never assignable via this route.
  AVATAR_DAPP_ROLE: (id: string) => `/api/avatar/${id}/dapp-role`,

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
  // the direct (no-DAG) parallel to the FungibleTokenCreate quest node.
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
  BRIDGE_COMPLETE: (id: string) => `/api/bridge/${id}/complete`,
  BRIDGE_REVERSE: (id: string) => `/api/bridge/${id}/reverse`,
  BRIDGE_HISTORY: "/api/bridge/history",

  // Search
  SEARCH: "/api/search",
  SEARCH_FACETS: "/api/search/facets",

  // Swap (SwapController)
  SWAP_QUOTE: "/api/swap/quote",
  SWAP_EXECUTE: "/api/swap/execute",

  // Marketplace browse: public + Active quests any authenticated avatar may fork/start.
  QUEST_PUBLIC: "/api/quest/public",

  // Quest definition lifecycle (quest-dag-semantic-hardening FR-2 / FR-8).
  QUEST_PUBLISH: (questId: string) => `/api/quest/${questId}/publish`,
  QUEST_UNPUBLISH: (questId: string) => `/api/quest/${questId}/unpublish`,

  // Durable workflow engine (durable-workflow-engine) — run-driver surface on
  // QuestController. `start-workflow` starts a durable run on an existing quest;
  // `advance` is the `.step(nodeId)` primitive; `signal` un-parks a gate.
  QUEST_START_WORKFLOW: (questId: string) => `/api/quest/${questId}/start-workflow`,
  QUEST_RUN_ADVANCE: (runId: string) => `/api/quest/runs/${runId}/advance`,
  QUEST_RUN_SIGNAL: (runId: string) => `/api/quest/runs/${runId}/signal`,
  QUEST_RUN_STATUS: (runId: string) => `/api/quest/runs/${runId}`,
  QUEST_RUN_EXECUTION_STATE: (runId: string) => `/api/quest/runs/${runId}/execution-state`,
  // All runs of a quest definition (`ListRunsByQuest`); avatar-scoped read.
  QUEST_RUNS_BY_QUEST: (questId: string) => `/api/quest/${questId}/runs`,
  // Per-run reconciliation re-probe (reconcile-before-retry §7). Un-parks an
  // `AwaitingReconciliation` run against chain truth; NEVER re-broadcasts.
  QUEST_RUN_RECONCILE: (runId: string) => `/api/quest/runs/${runId}/reconcile`,

  // Invite-gated quests + request/approval flow (quest-invitations-approval).
  // Run-access is orthogonal to `IsPublic` discoverability: an Open quest runs
  // for any viewer; an InviteOnly quest runs only for owner + invited avatars.
  // Owner-scoped: run-access mode, direct invite/revoke, approval queue.
  QUEST_RUN_ACCESS: (questId: string) => `/api/quest/${questId}/run-access`,
  QUEST_INVITE: (questId: string) => `/api/quest/${questId}/invite`,
  QUEST_INVITE_REVOKE: (questId: string, avatarId: string) => `/api/quest/${questId}/invite/${avatarId}`,
  QUEST_ACCESS_REQUESTS: (questId: string) => `/api/quest/${questId}/access-requests`,
  // Requester-scoped: decide (owner), withdraw (requester), my outbound requests.
  QUEST_ACCESS_REQUEST_DECISION: (requestId: string) => `/api/quest/access-requests/${requestId}/decision`,
  QUEST_ACCESS_REQUEST_WITHDRAW: (requestId: string) => `/api/quest/access-requests/${requestId}/withdraw`,
  QUEST_ACCESS_REQUESTS_MINE: "/api/quest/access-requests/mine",

  // Tenant onboarding (tenant-onboarding) — child credential issuance the
  // `forActor` actor abstraction threads (tenant acts FOR a child avatar).
  TENANT_CHILD_CREDENTIAL: (avatarId: string) => `/api/tenant/avatars/${avatarId}/credential`,

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

  // API-key issuance discovery + rotation (dapp-developer-experience audit).
  // `scopes` lists the self-issuable scope vocabulary; `rotate` mints a successor key.
  APIKEY_SCOPES: "/api/apikey/scopes",
  APIKEY_ROTATE: (id: string) => `/api/apikey/${id}/rotate`,

  // DappSeries CRUD + quest management. Reads are authenticated-avatar reads;
  // authoring writes require dapp:develop, lifecycle writes require dapp:manage.
  DAPP_SERIES_LIST: "/api/dapp-series",
  DAPP_SERIES_GET: (id: string) => `/api/dapp-series/${id}`,
  DAPP_SERIES_CREATE: "/api/dapp-series",
  DAPP_SERIES_UPDATE: (id: string) => `/api/dapp-series/${id}`,
  DAPP_SERIES_DELETE: (id: string) => `/api/dapp-series/${id}`,
  DAPP_SERIES_QUESTS: (seriesId: string) => `/api/dapp-series/${seriesId}/quests`,
  DAPP_SERIES_QUEST_REMOVE: (seriesId: string, questId: string) => `/api/dapp-series/${seriesId}/quests/${questId}`,
  DAPP_SERIES_QUEST_ORDER: (seriesId: string, questId: string) => `/api/dapp-series/${seriesId}/quests/${questId}/order`,
  DAPP_SERIES_QUEST_MAPPINGS: (seriesId: string, questId: string) => `/api/dapp-series/${seriesId}/quests/${questId}/mappings`,
} as const;
