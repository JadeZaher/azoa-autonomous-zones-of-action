import { AzoaApiClient } from "../api/client.js";
import { AzoaWallet } from "../wallet.js";
import type { ChainProviderRegistration, ChainNetwork } from "../core/types.js";
import { SessionManager } from "./session.js";
import type { SessionStorage, SessionState } from "./session.js";
import { HolonQueryBuilder } from "./holon-query.js";
import { AzoaAuthProvider } from "./auth-provider.js";
import type { AuthProviderConfig } from "./auth-provider.js";
import { PortfolioAggregator } from "./portfolio.js";
import { WorkflowClient } from "../workflow/index.js";

export interface AzoaClientConfig {
  /** AZOA API base URL */
  apiUrl: string;
  /** JWT token (if already authenticated) */
  token?: string;
  /** API key for server-to-server auth (sent as X-Api-Key header) */
  apiKey?: string;
  /** API request timeout in ms */
  timeoutMs?: number;
  /**
   * Enable verbose SDK diagnostics: requests, responses and errors are logged,
   * and server-side exception detail (when the backend runs with
   * `AZOA:DebugErrors`) is attached to every `SdkError`.
   */
  debug?: boolean;
  /** Sink for debug output. Defaults to the global `console`. */
  debugLogger?: Pick<Console, "debug" | "error">;
  /** Chain provider registrations for wallet operations */
  chains?: Record<string, ChainProviderRegistration>;
  /** The active blockchain network. Defaults to "testnet". */
  network?: ChainNetwork;
  /**
   * Factory that builds the chain registrations for a given network. When
   * provided, `setNetwork()` rebuilds the wallet's providers from this so a
   * single client can repoint all operations between devnet/testnet/mainnet
   * at runtime — only the active network's providers are ever registered.
   */
  chainsForNetwork?: (network: ChainNetwork) => Record<string, ChainProviderRegistration>;
  /** Callback fired after the active network changes. */
  onNetworkChange?: (network: ChainNetwork) => void;
  /** Session storage adapter (defaults to in-memory) */
  sessionStorage?: SessionStorage;
  /** Callback when session state changes */
  onSessionChange?: (state: SessionState) => void;
}

/**
 * Unified AZOA client — the single entry point for all AZOA operations.
 *
 * Composes:
 * - `api` — Typed HTTP client for all .NET API endpoints
 * - `wallet` — Multi-chain wallet with client-side signing
 * - `session` — JWT lifecycle management with pluggable storage
 * - `auth` — OAuth-compatible auth provider using AZOA avatars
 * - `holons` — Fluent query builder for holon data
 * - `portfolio` — Cross-chain balance aggregation
 *
 * ```ts
 * const azoa = new AzoaClient({
 *   apiUrl: "https://api.azoa.example",
 *   chains: {
 *     algorand: { provider: new AlgorandProvider(cfg) },
 *     solana: { provider: new SolanaProvider(cfg), dex: new JupiterAdapter() },
 *   },
 *   sessionStorage: localStorageAdapter,
 * });
 *
 * // Restore previous session
 * await azoa.session.restore();
 *
 * // Or login fresh
 * await azoa.auth.login("user@example.com", "password");
 *
 * // Query holons
 * const nfts = await azoa.holons.where({ assetType: "NFT" }).active().execute();
 *
 * // Check portfolio
 * const portfolio = await azoa.portfolio.getAll(azoa.auth.avatarId!);
 *
 * // Build and sign a transaction
 * const tx = await azoa.wallet.buildTransfer("algorand", { from, to, amount: "1.0" });
 * ```
 */
export class AzoaClient {
  /** Typed HTTP client for all AZOA API endpoints. */
  readonly api: AzoaApiClient;

  /** Multi-chain wallet with client-side signing and DEX adapters. */
  readonly wallet: AzoaWallet;

  /** JWT session lifecycle manager. */
  readonly session: SessionManager;

  /** OAuth-compatible auth provider. */
  readonly auth: AzoaAuthProvider;

  /** Fluent holon query builder. */
  readonly holons: HolonQueryBuilder;

  /** Cross-chain portfolio aggregator. */
  readonly portfolio: PortfolioAggregator;

  /**
   * Workflow surface — template authoring + run reads, plus the fluent
   * `azoa.workflow.quest(...)` run driver. The headline ergonomics of the
   * workflow-engine initiative.
   */
  readonly workflow: WorkflowClient;

  private _network: ChainNetwork;
  private readonly _chainsForNetwork?: (
    network: ChainNetwork
  ) => Record<string, ChainProviderRegistration>;
  private readonly _onNetworkChange?: (network: ChainNetwork) => void;

  constructor(config: AzoaClientConfig) {
    this._network = config.network ?? "testnet";
    this._chainsForNetwork = config.chainsForNetwork;
    this._onNetworkChange = config.onNetworkChange;
    // Session manager
    this.session = new SessionManager({
      storage: config.sessionStorage,
      onSessionChange: (state) => {
        // Clear cached API token on any session change (login, logout, restore)
        // This ensures the next request will call onTokenRefresh() and get the latest token.
        this.api.clearToken();
        config.onSessionChange?.(state);
      },
    });

    // API client with session-backed token refresh
    this.api = new AzoaApiClient({
      baseUrl: config.apiUrl,
      token: config.token,
      apiKey: config.apiKey,
      timeoutMs: config.timeoutMs,
      debug: config.debug,
      debugLogger: config.debugLogger,
      onTokenRefresh: this.session.createRefreshCallback(),
    });

    // Wallet
    this.wallet = new AzoaWallet();
    if (config.chains) {
      for (const [_key, reg] of Object.entries(config.chains)) {
        this.wallet.register(reg);
      }
    }

    // High-level modules
    this.auth = new AzoaAuthProvider(this.api, this.session);
    this.holons = new HolonQueryBuilder(this.api);
    this.portfolio = new PortfolioAggregator(this.api, this.wallet);
    this.workflow = new WorkflowClient(this.api);
  }

  /** Create an auth provider with custom config (e.g., for a specific app). */
  createAuthProvider(config?: AuthProviderConfig): AzoaAuthProvider {
    return new AzoaAuthProvider(this.api, this.session, config);
  }

  /**
   * The AZOA API base URL this client is pointed at. Public accessor so UIs
   * (e.g. a settings page) can display the active endpoint without reaching
   * into private fields.
   */
  getApiUrl(): string {
    return this.api.getBaseUrl();
  }

  /**
   * Toggle verbose SDK diagnostics at runtime. When on, the API client logs
   * every request/response/error and renders the backend's server-side
   * exception chain (when it runs with `AZOA:DebugErrors`) in error output.
   * Lets a UI flip debug mode without rebuilding the client.
   */
  setDebug(enabled: boolean): void {
    this.api.setDebug(enabled);
  }

  /** Whether verbose SDK diagnostics are currently enabled. */
  get debug(): boolean {
    return this.api.debug;
  }

  /** The currently active blockchain network. */
  get network(): ChainNetwork {
    return this._network;
  }

  /**
   * Switch the active network and repoint every wallet operation to it.
   *
   * If a `chainsForNetwork` factory was supplied, the wallet's providers are
   * fully rebuilt for `network` (old providers are cleared first), so balance,
   * transfer, swap and bridge calls all resolve against the new network and it
   * is impossible to act on assets from a different network. The user session
   * is left untouched — switching networks does not log the user out.
   */
  setNetwork(network: ChainNetwork): void {
    if (network === this._network) return;
    this._network = network;
    this.refreshChains();
    this._onNetworkChange?.(network);
  }

  /**
   * Rebuild the wallet's providers for the *current* network from the
   * `chainsForNetwork` factory. Call this when the underlying endpoint config
   * changes at runtime (e.g. after fetching RPC URLs from the backend) without
   * changing the active network. No-op if no factory was supplied.
   */
  refreshChains(): void {
    if (!this._chainsForNetwork) return;
    this.wallet.clear();
    for (const reg of Object.values(this._chainsForNetwork(this._network))) {
      this.wallet.register(reg);
    }
  }
}
