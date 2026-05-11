/**
 * API version configuration for the OASIS SDK.
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
  | "apikey";

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
  AVATAR_GET: (id: string) => `/api/avatar/${id}`,
  AVATAR_LIST: "/api/avatar",
  AVATAR_UPDATE: (id: string) => `/api/avatar/${id}`,
  AVATAR_DELETE: (id: string) => `/api/avatar/${id}`,

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
} as const;
