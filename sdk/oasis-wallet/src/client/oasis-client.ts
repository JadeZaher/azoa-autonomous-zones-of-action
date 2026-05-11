import { OasisApiClient } from "../api/client.js";
import { OasisWallet } from "../wallet.js";
import type { ChainProviderRegistration } from "../core/types.js";
import { SessionManager } from "./session.js";
import type { SessionStorage, SessionState } from "./session.js";
import { HolonQueryBuilder } from "./holon-query.js";
import { OasisAuthProvider } from "./auth-provider.js";
import type { AuthProviderConfig } from "./auth-provider.js";
import { PortfolioAggregator } from "./portfolio.js";

export interface OasisClientConfig {
  /** OASIS API base URL */
  apiUrl: string;
  /** JWT token (if already authenticated) */
  token?: string;
  /** API key for server-to-server auth (sent as X-Api-Key header) */
  apiKey?: string;
  /** API request timeout in ms */
  timeoutMs?: number;
  /** Chain provider registrations for wallet operations */
  chains?: Record<string, ChainProviderRegistration>;
  /** Session storage adapter (defaults to in-memory) */
  sessionStorage?: SessionStorage;
  /** Callback when session state changes */
  onSessionChange?: (state: SessionState) => void;
}

/**
 * Unified OASIS client — the single entry point for all OASIS operations.
 *
 * Composes:
 * - `api` — Typed HTTP client for all .NET API endpoints
 * - `wallet` — Multi-chain wallet with client-side signing
 * - `session` — JWT lifecycle management with pluggable storage
 * - `auth` — OAuth-compatible auth provider using OASIS avatars
 * - `holons` — Fluent query builder for holon data
 * - `portfolio` — Cross-chain balance aggregation
 *
 * ```ts
 * const oasis = new OasisClient({
 *   apiUrl: "https://api.oasis.example",
 *   chains: {
 *     algorand: { provider: new AlgorandProvider(cfg) },
 *     solana: { provider: new SolanaProvider(cfg), dex: new JupiterAdapter() },
 *   },
 *   sessionStorage: localStorageAdapter,
 * });
 *
 * // Restore previous session
 * await oasis.session.restore();
 *
 * // Or login fresh
 * await oasis.auth.login("user@example.com", "password");
 *
 * // Query holons
 * const nfts = await oasis.holons.where({ assetType: "NFT" }).active().execute();
 *
 * // Check portfolio
 * const portfolio = await oasis.portfolio.getAll(oasis.auth.avatarId!);
 *
 * // Build and sign a transaction
 * const tx = await oasis.wallet.buildTransfer("algorand", { from, to, amount: "1.0" });
 * ```
 */
export class OasisClient {
  /** Typed HTTP client for all OASIS API endpoints. */
  readonly api: OasisApiClient;

  /** Multi-chain wallet with client-side signing and DEX adapters. */
  readonly wallet: OasisWallet;

  /** JWT session lifecycle manager. */
  readonly session: SessionManager;

  /** OAuth-compatible auth provider. */
  readonly auth: OasisAuthProvider;

  /** Fluent holon query builder. */
  readonly holons: HolonQueryBuilder;

  /** Cross-chain portfolio aggregator. */
  readonly portfolio: PortfolioAggregator;

  constructor(config: OasisClientConfig) {
    // Session manager
    this.session = new SessionManager({
      storage: config.sessionStorage,
      onSessionChange: config.onSessionChange,
    });

    // API client with session-backed token refresh
    this.api = new OasisApiClient({
      baseUrl: config.apiUrl,
      token: config.token,
      apiKey: config.apiKey,
      timeoutMs: config.timeoutMs,
      onTokenRefresh: this.session.createRefreshCallback(),
    });

    // Wallet
    this.wallet = new OasisWallet();
    if (config.chains) {
      for (const [_key, reg] of Object.entries(config.chains)) {
        this.wallet.register(reg);
      }
    }

    // High-level modules
    this.auth = new OasisAuthProvider(this.api, this.session);
    this.holons = new HolonQueryBuilder(this.api);
    this.portfolio = new PortfolioAggregator(this.api, this.wallet);
  }

  /** Create an auth provider with custom config (e.g., for a specific app). */
  createAuthProvider(config?: AuthProviderConfig): OasisAuthProvider {
    return new OasisAuthProvider(this.api, this.session, config);
  }
}
