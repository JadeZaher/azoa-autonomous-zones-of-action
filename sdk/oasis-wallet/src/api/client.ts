import type { Result } from "../core/result.js";
import { ok, err } from "../core/result.js";
import { SdkError, SdkErrorCode } from "../core/errors.js";

export interface OasisApiConfig {
  baseUrl: string;
  token?: string;
  onTokenRefresh?: () => Promise<string>;
  timeoutMs?: number;
}

// Mirrors the .NET OASISResult<T> shape returned by most controllers
interface OASISResponse<T> {
  isError: boolean;
  message: string;
  result?: T;
}

// ─── Response types matching .NET DTOs ───

export interface AvatarResponse {
  id: string;
  username: string;
  email: string;
  title?: string;
  firstName?: string;
  lastName?: string;
  isActive: boolean;
}

export interface NftResult {
  id: string;
  name: string;
  description: string;
  ownerAvatarId?: string;
  chainId: string;
  tokenId?: string;
  metadata?: NftMetadata;
  createdDate?: string;
  modifiedDate?: string;
  isActive: boolean;
}

export interface NftMetadata {
  name: string;
  description?: string;
  image?: string;
  externalUrl?: string;
  animationUrl?: string;
}

// Matches .NET BridgeTransactionResult exactly
export interface BridgeTransactionResult {
  id: string;
  avatarId: string;
  sourceChain: string;
  targetChain: string;
  sourceTokenId: string;
  targetTokenId?: string;
  sourceAddress: string;
  targetAddress: string;
  amount: number;
  /** BridgeStatus enum: "Initiated"|"Locked"|"AwaitingVAA"|"VAAReady"|"Redeeming"|"Minted"|"Completed"|"Failed"|"Refunded" */
  status: string;
  /** BridgeMode: "Trusted"|"Wormhole" */
  mode: string;
  lockTxHash?: string;
  mintTxHash?: string;
  proofData?: string;
  errorMessage?: string;
  createdAt: string;
  completedAt?: string;
  // Wormhole-specific
  wormholeEmitterChainId?: number;
  wormholeEmitterAddress?: string;
  wormholeSequence?: number;
  vaaBytes?: string;
  vaaSignatureCount?: number;
  redemptionTxHash?: string;
}

// Matches .NET BridgeRouteInfo exactly
export interface BridgeRouteInfo {
  sourceChain: string;
  targetChain: string;
  isEnabled: boolean;
  estimatedTime?: string;
  supportedAssetTypes: string[];
  minAmount?: string;
  feeInfo?: string;
  availableModes: string[];
  wormholeSupported: boolean;
  wormholeSourceChainId?: number;
  wormholeTargetChainId?: number;
}

export interface SearchResult {
  items: unknown[];
  totalCount: number;
  page: number;
  pageSize: number;
}

// ─── Request types matching .NET DTOs ───

export interface NftMintParams {
  walletId: string;
  name: string;
  description: string;
  chainId: string;
  tokenId?: string;
  imageUri?: string;
  externalUri?: string;
  metadata?: Record<string, string>;
}

export interface NftTransferParams {
  targetAvatarId: string;
  walletId: string;
  memo?: string;
}

export interface NftBurnParams {
  walletId: string;
}

export interface BridgeInitiateParams {
  sourceChain: string;
  targetChain: string;
  tokenId: string;
  recipientAddress: string;
  amount?: number;
  mode?: "Trusted" | "Wormhole";
}

export interface SearchParams {
  query: string;
  entityTypes?: number;
  chainId?: string;
  assetType?: string;
  avatarId?: string;
  createdAfter?: string;
  createdBefore?: string;
  sortBy?: string;
  sortDescending?: boolean;
  page?: number;
  pageSize?: number;
}

export class OasisApiClient {
  private config: OasisApiConfig;

  constructor(config: OasisApiConfig) {
    this.config = config;
  }

  // ─── Avatar ───

  async login(email: string, password: string): Promise<Result<string, SdkError>> {
    // .NET returns OASISResult<string> where Result is the JWT token
    return this.request("POST", "/api/avatar/login", { email, password });
  }

  async register(params: {
    email: string;
    password: string;
    username: string;
    title?: string;
    firstName?: string;
    lastName?: string;
  }): Promise<Result<AvatarResponse, SdkError>> {
    // .NET returns OASISResult<IAvatar>
    return this.request("POST", "/api/avatar/register", params);
  }

  async getAvatar(avatarId: string): Promise<Result<AvatarResponse, SdkError>> {
    return this.request("GET", `/api/avatar/${avatarId}`);
  }

  async getAllAvatars(): Promise<Result<AvatarResponse[], SdkError>> {
    return this.request("GET", "/api/avatar");
  }

  async updateAvatar(
    avatarId: string,
    params: { username?: string; email?: string; title?: string; firstName?: string; lastName?: string; isActive?: boolean }
  ): Promise<Result<AvatarResponse, SdkError>> {
    return this.request("PUT", `/api/avatar/${avatarId}`, params);
  }

  async deleteAvatar(avatarId: string): Promise<Result<{ message: string }, SdkError>> {
    return this.request("DELETE", `/api/avatar/${avatarId}`);
  }

  // ─── NFT ───
  // Matches NftController routes exactly

  async getNft(nftId: string): Promise<Result<NftResult, SdkError>> {
    return this.request("GET", `/api/nft/${nftId}`);
  }

  async mintNft(params: NftMintParams): Promise<Result<unknown, SdkError>> {
    // POST /api/nft/mint with NftMintRequest body
    return this.request("POST", "/api/nft/mint", params);
  }

  async transferNft(nftId: string, params: NftTransferParams): Promise<Result<unknown, SdkError>> {
    // POST /api/nft/{id}/transfer with NftTransferRequest body
    return this.request("POST", `/api/nft/${nftId}/transfer`, params);
  }

  async burnNft(nftId: string, params: NftBurnParams): Promise<Result<unknown, SdkError>> {
    // POST /api/nft/{id}/burn with NftBurnRequest body
    return this.request("POST", `/api/nft/${nftId}/burn`, params);
  }

  async getNftMetadata(nftId: string): Promise<Result<NftMetadata, SdkError>> {
    return this.request("GET", `/api/nft/${nftId}/metadata`);
  }

  // ─── Bridge ───
  // BridgeController returns bare objects, not OASISResult<T>

  async getBridgeRoutes(): Promise<Result<BridgeRouteInfo[], SdkError>> {
    // GET /api/bridge/routes — returns bare array, no OASISResult wrapper
    return this.requestBare("GET", "/api/bridge/routes");
  }

  async initiateBridge(params: BridgeInitiateParams): Promise<Result<BridgeTransactionResult, SdkError>> {
    return this.requestBare("POST", "/api/bridge/initiate", params);
  }

  async getBridgeStatus(bridgeId: string): Promise<Result<BridgeTransactionResult, SdkError>> {
    return this.requestBare("GET", `/api/bridge/${bridgeId}`);
  }

  async completeBridge(bridgeId: string): Promise<Result<BridgeTransactionResult, SdkError>> {
    return this.requestBare("POST", `/api/bridge/${bridgeId}/complete`);
  }

  async fetchVAA(bridgeId: string): Promise<Result<BridgeTransactionResult, SdkError>> {
    return this.requestBare("POST", `/api/bridge/${bridgeId}/fetch-vaa`);
  }

  async redeemBridge(bridgeId: string): Promise<Result<BridgeTransactionResult, SdkError>> {
    return this.requestBare("POST", `/api/bridge/${bridgeId}/redeem`);
  }

  async reverseBridge(bridgeId: string, sourceRecipientAddress: string): Promise<Result<BridgeTransactionResult, SdkError>> {
    return this.requestBare("POST", `/api/bridge/${bridgeId}/reverse`, { sourceRecipientAddress });
  }

  async getBridgeHistory(): Promise<Result<BridgeTransactionResult[], SdkError>> {
    return this.requestBare("GET", "/api/bridge/history");
  }

  // ─── Search ───
  // SearchController uses POST with SearchRequest body

  async search(params: SearchParams): Promise<Result<SearchResult, SdkError>> {
    // POST /api/search with SearchRequest body
    return this.request("POST", "/api/search", params);
  }

  async getSearchFacets(): Promise<Result<unknown[], SdkError>> {
    return this.request("GET", "/api/search/facets");
  }

  // ─── Generic HTTP ───

  /** Send a request to an OASISResult<T>-wrapped endpoint. Public for use by query builders. */
  async request<T>(method: string, path: string, body?: unknown, _retried = false): Promise<Result<T, SdkError>> {
    try {
      const resp = await this.fetchWithAuth(method, path, body);

      if (resp.status === 401 && this.config.onTokenRefresh && !_retried) {
        try {
          this.config.token = await this.config.onTokenRefresh();
        } catch (refreshErr) {
          return this.handleFetchError(refreshErr);
        }
        return this.request(method, path, body, true);
      }

      const data = (await resp.json()) as OASISResponse<T>;

      if (data.isError) {
        return err(new SdkError(SdkErrorCode.API_ERROR, data.message));
      }

      return ok(data.result as T);
    } catch (e) {
      return this.handleFetchError(e);
    }
  }

  /** For endpoints that return bare objects (BridgeController pattern) */
  private async requestBare<T>(method: string, path: string, body?: unknown, _retried = false): Promise<Result<T, SdkError>> {
    try {
      const resp = await this.fetchWithAuth(method, path, body);

      if (resp.status === 401 && this.config.onTokenRefresh && !_retried) {
        try {
          this.config.token = await this.config.onTokenRefresh();
        } catch (refreshErr) {
          return this.handleFetchError(refreshErr);
        }
        return this.requestBare(method, path, body, true);
      }

      if (!resp.ok) {
        const errorData = (await resp.json()) as { error?: string };
        return err(new SdkError(SdkErrorCode.API_ERROR, errorData.error ?? `HTTP ${resp.status}`));
      }

      const data = (await resp.json()) as T;
      return ok(data);
    } catch (e) {
      return this.handleFetchError(e);
    }
  }

  private async fetchWithAuth(method: string, path: string, body?: unknown): Promise<Response> {
    let token = this.config.token;
    if (!token && this.config.onTokenRefresh) {
      token = await this.config.onTokenRefresh();
      this.config.token = token;
    }

    const headers: Record<string, string> = {};
    if (body) headers["Content-Type"] = "application/json";
    if (token) headers["Authorization"] = `Bearer ${token}`;

    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), this.config.timeoutMs ?? 30000);

    try {
      return await fetch(`${this.config.baseUrl}${path}`, {
        method,
        headers,
        body: body ? JSON.stringify(body) : undefined,
        signal: controller.signal,
      });
    } finally {
      clearTimeout(timeout);
    }
  }

  private handleFetchError<T>(e: unknown): Result<T, SdkError> {
    if (e instanceof DOMException && e.name === "AbortError") {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, "Request timed out"));
    }
    return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `API request failed: ${e}`, { cause: e as Error }));
  }
}
