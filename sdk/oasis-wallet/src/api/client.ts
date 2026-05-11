import type { Result } from "../core/result.js";
import { ok, err } from "../core/result.js";
import { SdkError, SdkErrorCode } from "../core/errors.js";

export interface OasisApiConfig {
  baseUrl: string;
  token?: string;
  apiKey?: string;
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

// ─── Wallet types matching .NET DTOs ───

export interface WalletResult {
  id: string;
  avatarId: string;
  chainType: string;
  address: string;
  publicKey?: string;
  label?: string;
  isDefault: boolean;
  createdDate?: string;
}

export interface WalletCreateParams {
  chainType: string;
  address: string;
  publicKey?: string;
  label?: string;
  isDefault?: boolean;
}

export interface WalletUpdateParams {
  label?: string;
  isDefault?: boolean;
}

export interface WalletQueryParams {
  avatarId?: string;
  chainType?: string;
  isDefault?: boolean;
}

export interface PortfolioResult {
  walletId: string;
  chainType: string;
  address: string;
  balance: number;
  symbol: string;
  nfts: NftHolding[];
  computedAt: string;
}

export interface NftHolding {
  holonId: string;
  name: string;
  tokenId?: string;
  imageUri?: string;
}

// ─── BlockchainOperation types matching .NET DTOs ───

export interface BlockchainOperationResult {
  id: string;
  avatarId?: string;
  walletId?: string;
  operationType: string;
  status: string;
  parameters: Record<string, string>;
  createdDate: string;
  completedDate?: string;
  tokenUri?: string;
  amount?: number;
  assetType?: string;
  sourceHolonId?: string;
  targetHolonId?: string;
  exchangeRate?: string;
  recipientAddress?: string;
}

// ─── STARODK types matching .NET DTOs ───

export interface STARODKResult {
  id: string;
  name: string;
  description: string;
  publicKey?: string;
  avatarId?: string;
  boundHolonIds: string[];
  targetChain?: string;
  generatedCode?: string;
  deploymentConfig?: string;
  createdDate: string;
  modifiedDate?: string;
  isActive: boolean;
}

export interface STARODKCreateParams {
  name: string;
  description: string;
  publicKey?: string;
  avatarId?: string;
}

export interface STARDappGenerationParams {
  targetChain: string;
  boundHolonIds: string[];
  config?: Record<string, string>;
}

// ─── AvatarNFT types matching .NET DTOs ───

export interface AvatarNFTResult {
  id: string;
  avatarId: string;
  nftContractAddress?: string;
  tokenId?: string;
  chainType: string;
  tokenStandard?: string;
  metadataURI?: string;
  imageURI?: string;
  name: string;
  description?: string;
  attributes?: Record<string, string>;
  royaltyPercentage?: number;
  royaltyRecipient?: string;
  isSoulbound: boolean;
  isTransferable: boolean;
  mintedDate?: string;
  lastTransferDate?: string;
  currentOwner?: string;
  isActive: boolean;
  holonBindings?: HolonNFTBindingResult[];
  walletBindings?: WalletNFTBindingResult[];
}

export interface AvatarNFTMintParams {
  chainType: string;
  contractAddress?: string;
  tokenStandard?: string;
  metadataURI?: string;
  name: string;
  description?: string;
  attributes?: Record<string, string>;
  royaltyPercentage?: number;
  isSoulbound?: boolean;
  isTransferable?: boolean;
  holonBindings?: { holonId: string; role: string; permissionLevel: string; permissions?: Record<string, string> }[];
  walletBindings?: { walletId: string; bindingType: string; accessLevel: string; accessPermissions?: Record<string, string> }[];
}

export interface HolonNFTBindingResult {
  id: string;
  holonId: string;
  avatarNFTId: string;
  role: string;
  permissionLevel: string;
  permissions: Record<string, string>;
  createdDate: string;
  lastUpdatedDate?: string;
  isActive: boolean;
}

export interface WalletNFTBindingResult {
  id: string;
  walletId: string;
  avatarNFTId: string;
  bindingType: string;
  accessLevel?: string;
  accessPermissions: Record<string, string>;
  createdDate: string;
  lastUpdatedDate?: string;
  isActive: boolean;
}

// ─── Quest types matching .NET DTOs ───

/** Quest status lifecycle */
export type QuestStatus = "Draft" | "Active" | "Completed" | "Failed" | "Archived";

/** Node execution state */
export type QuestNodeState = "Pending" | "Running" | "Succeeded" | "Failed" | "Skipped";

/** Edge types for DAG flow control */
export type QuestEdgeType = "Control" | "Conditional";

/** All supported quest node operation types */
export type QuestNodeType =
  | "HolonCreate" | "HolonUpdate" | "HolonDelete" | "HolonQuery"
  | "NftMint" | "NftTransfer" | "NftBurn"
  | "WalletCreate" | "WalletTransfer" | "WalletBalance"
  | "StarCreate" | "StarGenerate" | "StarDeploy"
  | "SearchQuery"
  | "AvatarNftMint" | "AvatarNftTransfer" | "AvatarNftVerify"
  | "BlockchainExecute"
  | "Gate" | "Delay" | "Webhook" | "Script"
  | string; // extensible

export interface QuestNodeResult {
  id: string;
  questId: string;
  name: string;
  nodeType: string;
  state: QuestNodeState;
  config: string;
  output?: string;
  error?: string;
  isEntry: boolean;
  isTerminal: boolean;
  nodeTemplateId?: string;
  startedAt?: string;
  completedAt?: string;
}

export interface QuestEdgeResult {
  id: string;
  questId: string;
  sourceNodeId: string;
  targetNodeId: string;
  condition?: string;
  edgeType: QuestEdgeType;
}

export interface QuestDependencyResult {
  id: string;
  questId: string;
  dependsOnQuestId: string;
  dependencyType: string;
}

export interface QuestResult {
  id: string;
  name: string;
  description?: string;
  avatarId: string;
  status: QuestStatus;
  nodes: QuestNodeResult[];
  edges: QuestEdgeResult[];
  dependencies: QuestDependencyResult[];
  templateId?: string;
  dappSeriesId?: string;
  metadata: Record<string, string>;
  createdDate: string;
  completedDate?: string;
}

export interface QuestNodeCreateParams {
  name: string;
  nodeType: QuestNodeType;
  config?: string;
  isEntry?: boolean;
  isTerminal?: boolean;
  nodeTemplateId?: string;
}

export interface QuestEdgeCreateParams {
  sourceNodeId: number;
  targetNodeId: number;
  condition?: string;
  edgeType?: QuestEdgeType;
}

export interface QuestCreateParams {
  name: string;
  description?: string;
  nodes: QuestNodeCreateParams[];
  edges: QuestEdgeCreateParams[];
}

export interface QuestUpdateParams {
  name?: string;
  description?: string;
  status?: QuestStatus;
}

export interface QuestTemplateResult {
  id: string;
  name: string;
  description?: string;
  authorAvatarId: string;
  parameters: string;
  version: string;
  isPublic: boolean;
  tags: string[];
  nodes: QuestNodeResult[];
  edges: QuestEdgeResult[];
  createdDate: string;
}

export interface QuestTemplateCreateParams {
  name: string;
  description?: string;
  nodes: QuestNodeCreateParams[];
  edges: QuestEdgeCreateParams[];
  parameters?: string;
  version?: string;
  isPublic?: boolean;
  tags?: string[];
}

export interface QuestNodeTemplateResult {
  id: string;
  name: string;
  nodeType: string;
  description?: string;
  defaultConfig: string;
  configSchema: string;
  inputSchema: string;
  outputSchema: string;
  version: string;
  isPublic: boolean;
  tags: string[];
  createdDate: string;
}

export interface QuestNodeTemplateCreateParams {
  name: string;
  nodeType: QuestNodeType;
  description?: string;
  defaultConfig?: string;
  configSchema?: string;
  inputSchema?: string;
  outputSchema?: string;
  version?: string;
  isPublic?: boolean;
  tags?: string[];
}

// ─── ApiKey types matching .NET DTOs ───

export interface ApiKeyCreateParams {
  name: string;
  expiresInDays?: number;
  scopes?: string;
}

export interface ApiKeyCreateResult {
  id: string;
  name: string;
  /** The raw API key — shown only once at creation time. Store securely. */
  key: string;
  keyPrefix: string;
  expiresAt?: string;
  scopes?: string;
  createdDate: string;
}

export interface ApiKeyInfo {
  id: string;
  name: string;
  keyPrefix: string;
  createdDate: string;
  expiresAt?: string;
  lastUsedAt?: string;
  revokedAt?: string;
  isActive: boolean;
  scopes?: string;
}

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Validates that an ID is a UUID before URL interpolation. Prevents path traversal. */
function assertUuid(id: string, paramName: string): void {
  if (!UUID_RE.test(id)) {
    throw new SdkError(
      SdkErrorCode.INVALID_INPUT,
      `Invalid ${paramName}: expected UUID format (received "${id.length > 50 ? id.slice(0, 50) + "…" : id}")`
    );
  }
}

export class OasisApiClient {
  private config: OasisApiConfig;
  private _refreshInFlight: Promise<string> | null = null;

  constructor(config: OasisApiConfig) {
    try {
      const parsed = new URL(config.baseUrl);
      if (!["http:", "https:"].includes(parsed.protocol)) {
        throw new SdkError(SdkErrorCode.INVALID_INPUT, "baseUrl must use HTTP or HTTPS");
      }
    } catch (e) {
      if (e instanceof SdkError) throw e;
      throw new SdkError(SdkErrorCode.INVALID_INPUT, `Invalid baseUrl: ${config.baseUrl}`);
    }
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
    assertUuid(avatarId, "avatarId");
    return this.request("GET", `/api/avatar/${avatarId}`);
  }

  async getAllAvatars(): Promise<Result<AvatarResponse[], SdkError>> {
    return this.request("GET", "/api/avatar");
  }

  async updateAvatar(
    avatarId: string,
    params: { username?: string; email?: string; title?: string; firstName?: string; lastName?: string; isActive?: boolean }
  ): Promise<Result<AvatarResponse, SdkError>> {
    assertUuid(avatarId, "avatarId");
    return this.request("PUT", `/api/avatar/${avatarId}`, params);
  }

  async deleteAvatar(avatarId: string): Promise<Result<{ message: string }, SdkError>> {
    assertUuid(avatarId, "avatarId");
    return this.request("DELETE", `/api/avatar/${avatarId}`);
  }

  // ─── NFT ───
  // Matches NftController routes exactly

  async getNft(nftId: string): Promise<Result<NftResult, SdkError>> {
    assertUuid(nftId, "nftId");
    return this.request("GET", `/api/nft/${nftId}`);
  }

  async mintNft(params: NftMintParams): Promise<Result<unknown, SdkError>> {
    // POST /api/nft/mint with NftMintRequest body
    return this.request("POST", "/api/nft/mint", params);
  }

  async transferNft(nftId: string, params: NftTransferParams): Promise<Result<unknown, SdkError>> {
    assertUuid(nftId, "nftId");
    // POST /api/nft/{id}/transfer with NftTransferRequest body
    return this.request("POST", `/api/nft/${nftId}/transfer`, params);
  }

  async burnNft(nftId: string, params: NftBurnParams): Promise<Result<unknown, SdkError>> {
    assertUuid(nftId, "nftId");
    // POST /api/nft/{id}/burn with NftBurnRequest body
    return this.request("POST", `/api/nft/${nftId}/burn`, params);
  }

  async getNftMetadata(nftId: string): Promise<Result<NftMetadata, SdkError>> {
    assertUuid(nftId, "nftId");
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
    assertUuid(bridgeId, "bridgeId");
    return this.requestBare("GET", `/api/bridge/${bridgeId}`);
  }

  async completeBridge(bridgeId: string): Promise<Result<BridgeTransactionResult, SdkError>> {
    assertUuid(bridgeId, "bridgeId");
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

  // ─── Wallet ───

  async getWallet(walletId: string): Promise<Result<WalletResult, SdkError>> {
    assertUuid(walletId, "walletId");
    return this.request("GET", `/api/wallet/${walletId}`);
  }

  async listWallets(params?: WalletQueryParams): Promise<Result<WalletResult[], SdkError>> {
    const qs = new URLSearchParams();
    if (params) {
      for (const [key, value] of Object.entries(params)) {
        if (value !== undefined) qs.set(key, String(value));
      }
    }
    const query = qs.toString();
    return this.request("GET", `/api/wallet${query ? `?${query}` : ""}`);
  }

  async createWallet(params: WalletCreateParams): Promise<Result<WalletResult, SdkError>> {
    return this.request("POST", "/api/wallet", params);
  }

  async updateWallet(walletId: string, params: WalletUpdateParams): Promise<Result<WalletResult, SdkError>> {
    assertUuid(walletId, "walletId");
    return this.request("PUT", `/api/wallet/${walletId}`, params);
  }

  async deleteWallet(walletId: string): Promise<Result<{ message: string }, SdkError>> {
    assertUuid(walletId, "walletId");
    return this.request("DELETE", `/api/wallet/${walletId}`);
  }

  async setDefaultWallet(walletId: string): Promise<Result<WalletResult, SdkError>> {
    assertUuid(walletId, "walletId");
    return this.request("POST", `/api/wallet/${walletId}/set-default`);
  }

  async getWalletPortfolio(walletId: string): Promise<Result<PortfolioResult, SdkError>> {
    assertUuid(walletId, "walletId");
    return this.request("GET", `/api/wallet/${walletId}/portfolio`);
  }

  // ─── BlockchainOperation ───

  async getBlockchainOperation(operationId: string): Promise<Result<BlockchainOperationResult, SdkError>> {
    assertUuid(operationId, "operationId");
    return this.request("GET", `/api/blockchainoperation/${operationId}`);
  }

  async getBlockchainOperationsByAvatar(avatarId: string): Promise<Result<BlockchainOperationResult[], SdkError>> {
    assertUuid(avatarId, "avatarId");
    return this.request("GET", `/api/blockchainoperation/avatar/${avatarId}`);
  }

  // ─── STARODK ───

  async getSTARODK(id: string): Promise<Result<STARODKResult, SdkError>> {
    assertUuid(id, "starodkId");
    return this.request("GET", `/api/starodk/${id}`);
  }

  async listSTARODK(): Promise<Result<STARODKResult[], SdkError>> {
    return this.request("GET", "/api/starodk");
  }

  async createSTARODK(params: STARODKCreateParams): Promise<Result<STARODKResult, SdkError>> {
    return this.request("POST", "/api/starodk", params);
  }

  async deleteSTARODK(id: string): Promise<Result<{ message: string }, SdkError>> {
    assertUuid(id, "starodkId");
    return this.request("DELETE", `/api/starodk/${id}`);
  }

  async generateSTARDapp(starodkId: string, params: STARDappGenerationParams): Promise<Result<STARODKResult, SdkError>> {
    assertUuid(starodkId, "starodkId");
    return this.request("POST", `/api/starodk/${starodkId}/generate`, params);
  }

  async deploySTARODK(starodkId: string): Promise<Result<STARODKResult, SdkError>> {
    assertUuid(starodkId, "starodkId");
    return this.request("POST", `/api/starodk/${starodkId}/deploy`);
  }

  // ─── AvatarNFT ───

  async mintAvatarNFT(params: AvatarNFTMintParams): Promise<Result<AvatarNFTResult, SdkError>> {
    return this.request("POST", "/api/avatarnft/mint", params);
  }

  async getAvatarNFT(id: string): Promise<Result<AvatarNFTResult, SdkError>> {
    assertUuid(id, "avatarNFTId");
    return this.request("GET", `/api/avatarnft/${id}`);
  }

  async getAvatarNFTByToken(chainType: string, contractAddress: string, tokenId: string): Promise<Result<AvatarNFTResult, SdkError>> {
    return this.request("GET", `/api/avatarnft/by-token/${encodeURIComponent(chainType)}/${encodeURIComponent(contractAddress)}/${encodeURIComponent(tokenId)}`);
  }

  async listAvatarNFTs(avatarId: string): Promise<Result<AvatarNFTResult[], SdkError>> {
    assertUuid(avatarId, "avatarId");
    return this.request("GET", `/api/avatarnft/avatar/${avatarId}`);
  }

  async transferAvatarNFT(id: string, recipientAddress: string): Promise<Result<AvatarNFTResult, SdkError>> {
    assertUuid(id, "avatarNFTId");
    return this.request("POST", `/api/avatarnft/${id}/transfer`, { recipientAddress });
  }

  async burnAvatarNFT(id: string): Promise<Result<{ message: string }, SdkError>> {
    assertUuid(id, "avatarNFTId");
    return this.request("DELETE", `/api/avatarnft/${id}`);
  }

  async bindHolonToNFT(avatarNFTId: string, holonId: string, params: { role: string; permissionLevel: string; permissions?: Record<string, string> }): Promise<Result<HolonNFTBindingResult, SdkError>> {
    assertUuid(avatarNFTId, "avatarNFTId");
    assertUuid(holonId, "holonId");
    return this.request("POST", `/api/avatarnft/${avatarNFTId}/holons/${holonId}/bind`, params);
  }

  async listNFTHolonBindings(avatarNFTId: string): Promise<Result<HolonNFTBindingResult[], SdkError>> {
    assertUuid(avatarNFTId, "avatarNFTId");
    return this.request("GET", `/api/avatarnft/${avatarNFTId}/holons`);
  }

  async updateHolonBinding(bindingId: string, params: { role?: string; permissionLevel?: string; permissions?: Record<string, string>; isActive?: boolean }): Promise<Result<HolonNFTBindingResult, SdkError>> {
    assertUuid(bindingId, "bindingId");
    return this.request("PUT", `/api/avatarnft/holons/${bindingId}`, params);
  }

  async removeHolonBinding(bindingId: string): Promise<Result<{ message: string }, SdkError>> {
    assertUuid(bindingId, "bindingId");
    return this.request("DELETE", `/api/avatarnft/holons/${bindingId}`);
  }

  async bindWalletToNFT(avatarNFTId: string, walletId: string, params: { bindingType: string; accessLevel: string; accessPermissions?: Record<string, string> }): Promise<Result<WalletNFTBindingResult, SdkError>> {
    assertUuid(avatarNFTId, "avatarNFTId");
    assertUuid(walletId, "walletId");
    return this.request("POST", `/api/avatarnft/${avatarNFTId}/wallets/${walletId}/bind`, params);
  }

  async listNFTWalletBindings(avatarNFTId: string): Promise<Result<WalletNFTBindingResult[], SdkError>> {
    assertUuid(avatarNFTId, "avatarNFTId");
    return this.request("GET", `/api/avatarnft/${avatarNFTId}/wallets`);
  }

  async updateWalletBinding(bindingId: string, params: { bindingType?: string; accessLevel?: string; accessPermissions?: Record<string, string>; isActive?: boolean }): Promise<Result<WalletNFTBindingResult, SdkError>> {
    assertUuid(bindingId, "bindingId");
    return this.request("PUT", `/api/avatarnft/wallets/${bindingId}`, params);
  }

  async removeWalletBinding(bindingId: string): Promise<Result<{ message: string }, SdkError>> {
    assertUuid(bindingId, "bindingId");
    return this.request("DELETE", `/api/avatarnft/wallets/${bindingId}`);
  }

  async getAvatarNFTComposite(avatarNFTId: string): Promise<Result<AvatarNFTResult, SdkError>> {
    assertUuid(avatarNFTId, "avatarNFTId");
    return this.request("GET", `/api/avatarnft/${avatarNFTId}/composite`);
  }

  async listAvatarNFTComposites(avatarId: string): Promise<Result<AvatarNFTResult[], SdkError>> {
    assertUuid(avatarId, "avatarId");
    return this.request("GET", `/api/avatarnft/avatar/${avatarId}/composite`);
  }

  async verifyNFTOwnership(params: { chainType: string; nftContractAddress: string; tokenId: string }): Promise<Result<{ isOwner: boolean }, SdkError>> {
    return this.request("POST", "/api/avatarnft/verify-ownership", params);
  }

  async verifyHolonAccess(params: { avatarNFTId: string; holonId: string; requiredPermission?: string }): Promise<Result<{ hasAccess: boolean }, SdkError>> {
    return this.request("POST", "/api/avatarnft/verify-holon-access", params);
  }

  async verifyWalletAccess(params: { avatarNFTId: string; walletId: string; requiredAccess?: string }): Promise<Result<{ hasAccess: boolean }, SdkError>> {
    return this.request("POST", "/api/avatarnft/verify-wallet-access", params);
  }

  // ─── Quest DAG Operations ───

  /** Create a new quest with a DAG of nodes and edges. */
  async createQuest(params: QuestCreateParams): Promise<Result<QuestResult, SdkError>> {
    return this.request("POST", "/api/quest", params);
  }

  /** Get a quest by ID, including all nodes, edges, and dependencies. */
  async getQuest(questId: string): Promise<Result<QuestResult, SdkError>> {
    assertUuid(questId, "questId");
    return this.request("GET", `/api/quest/${questId}`);
  }

  /** List all quests belonging to an avatar. */
  async listQuestsByAvatar(avatarId: string): Promise<Result<QuestResult[], SdkError>> {
    assertUuid(avatarId, "avatarId");
    return this.request("GET", `/api/quest/avatar/${avatarId}`);
  }

  /** Update quest metadata or status. */
  async updateQuest(questId: string, params: QuestUpdateParams): Promise<Result<QuestResult, SdkError>> {
    assertUuid(questId, "questId");
    return this.request("PUT", `/api/quest/${questId}`, params);
  }

  /** Delete a quest and all its nodes/edges. */
  async deleteQuest(questId: string): Promise<Result<{ message: string }, SdkError>> {
    assertUuid(questId, "questId");
    return this.request("DELETE", `/api/quest/${questId}`);
  }

  /** Validate quest DAG structure (checks for cycles, unreachable nodes, etc.). */
  async validateQuestDag(questId: string): Promise<Result<boolean, SdkError>> {
    assertUuid(questId, "questId");
    return this.request("POST", `/api/quest/${questId}/validate`);
  }

  /** Execute all ready nodes in the quest DAG. Returns the updated quest. */
  async executeQuest(questId: string): Promise<Result<QuestResult, SdkError>> {
    assertUuid(questId, "questId");
    return this.request("POST", `/api/quest/${questId}/execute`);
  }

  /** Execute a single node within a quest. */
  async executeQuestNode(questId: string, nodeId: string): Promise<Result<QuestNodeResult, SdkError>> {
    assertUuid(questId, "questId");
    assertUuid(nodeId, "nodeId");
    return this.request("POST", `/api/quest/${questId}/nodes/${nodeId}/execute`);
  }

  // ─── Quest Templates ───

  /** Create a reusable quest template. */
  async createQuestTemplate(params: QuestTemplateCreateParams): Promise<Result<QuestTemplateResult, SdkError>> {
    return this.request("POST", "/api/quest/templates", params);
  }

  /** Get a quest template by ID. */
  async getQuestTemplate(templateId: string): Promise<Result<QuestTemplateResult, SdkError>> {
    assertUuid(templateId, "templateId");
    return this.request("GET", `/api/quest/templates/${templateId}`);
  }

  /** List all available quest templates. */
  async listQuestTemplates(): Promise<Result<QuestTemplateResult[], SdkError>> {
    return this.request("GET", "/api/quest/templates");
  }

  /** Instantiate a quest from a template with optional parameter overrides. */
  async instantiateQuestTemplate(templateId: string, parameters?: Record<string, string>): Promise<Result<QuestResult, SdkError>> {
    assertUuid(templateId, "templateId");
    return this.request("POST", `/api/quest/templates/${templateId}/instantiate`, parameters);
  }

  // ─── Quest Node Templates ───

  /** Create a reusable node template. */
  async createQuestNodeTemplate(params: QuestNodeTemplateCreateParams): Promise<Result<QuestNodeTemplateResult, SdkError>> {
    return this.request("POST", "/api/quest/node-templates", params);
  }

  /** List all available node templates. */
  async listQuestNodeTemplates(): Promise<Result<QuestNodeTemplateResult[], SdkError>> {
    return this.request("GET", "/api/quest/node-templates");
  }

  // ─── API Key Management ───

  /** Create a new API key. The raw key is returned ONCE — store it securely. */
  async createApiKey(params: ApiKeyCreateParams): Promise<Result<ApiKeyCreateResult, SdkError>> {
    return this.request("POST", "/api/apikey", params);
  }

  /** List all API keys for the authenticated avatar (raw keys are never returned). */
  async listApiKeys(): Promise<Result<ApiKeyInfo[], SdkError>> {
    return this.request("GET", "/api/apikey");
  }

  /** Revoke an API key (soft-delete — deactivated but record retained). */
  async revokeApiKey(keyId: string): Promise<Result<{ message: string }, SdkError>> {
    assertUuid(keyId, "keyId");
    return this.request("POST", `/api/apikey/${keyId}/revoke`);
  }

  /** Permanently delete an API key record. */
  async deleteApiKey(keyId: string): Promise<Result<{ message: string }, SdkError>> {
    assertUuid(keyId, "keyId");
    return this.request("DELETE", `/api/apikey/${keyId}`);
  }

  // ─── Generic HTTP ───

  /** Send a request to an OASISResult<T>-wrapped endpoint. Public for use by query builders. */
  async request<T>(method: string, path: string, body?: unknown, _retried = false): Promise<Result<T, SdkError>> {
    try {
      const resp = await this.fetchWithAuth(method, path, body);

      if (resp.status === 401 && this.config.onTokenRefresh && !_retried) {
        try {
          await this.getOrRefreshToken();
        } catch (refreshErr) {
          return this.handleFetchError(refreshErr);
        }
        return this.request(method, path, body, true);
      }

      if (resp.status === 401 && _retried) {
        return err(new SdkError(SdkErrorCode.AUTH_EXPIRED, `${method} ${path}: session expired. Please log in again.`));
      }

      if (!resp.ok) {
        let msg = `${method} ${path} failed with HTTP ${resp.status}`;
        try {
          const errBody = (await resp.json()) as Partial<OASISResponse<unknown>>;
          if (errBody.message) msg = `${method} ${path}: ${errBody.message}`;
        } catch { /* non-JSON body */ }
        return err(new SdkError(SdkErrorCode.API_ERROR, msg));
      }

      const data = (await resp.json()) as OASISResponse<T>;

      if (data.isError) {
        return err(new SdkError(SdkErrorCode.API_ERROR, `${method} ${path}: ${data.message}`));
      }

      return ok(data.result as T);
    } catch (e) {
      return this.handleFetchError(e);
    }
  }

  /** For endpoints that return bare objects (BridgeController pattern). */
  async requestBare<T>(method: string, path: string, body?: unknown, _retried = false): Promise<Result<T, SdkError>> {
    try {
      const resp = await this.fetchWithAuth(method, path, body);

      if (resp.status === 401 && this.config.onTokenRefresh && !_retried) {
        try {
          await this.getOrRefreshToken();
        } catch (refreshErr) {
          return this.handleFetchError(refreshErr);
        }
        return this.requestBare(method, path, body, true);
      }

      if (resp.status === 401 && _retried) {
        return err(new SdkError(SdkErrorCode.AUTH_EXPIRED, `${method} ${path}: session expired. Please log in again.`));
      }

      if (!resp.ok) {
        let msg = `${method} ${path} failed with HTTP ${resp.status}`;
        try {
          const errorData = (await resp.json()) as { error?: string };
          if (errorData.error) msg = `${method} ${path}: ${errorData.error}`;
        } catch { /* non-JSON error body */ }
        return err(new SdkError(SdkErrorCode.API_ERROR, msg));
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
      token = await this.getOrRefreshToken();
    }

    const headers: Record<string, string> = {};
    if (body) headers["Content-Type"] = "application/json";
    if (token) {
      headers["Authorization"] = `Bearer ${token}`;
    } else if (this.config.apiKey) {
      headers["X-Api-Key"] = this.config.apiKey;
    }

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

  /** Deduplicated token refresh — prevents concurrent refresh races. */
  private async getOrRefreshToken(): Promise<string | undefined> {
    if (this.config.token) return this.config.token;
    if (!this.config.onTokenRefresh) return undefined;
    if (!this._refreshInFlight) {
      this._refreshInFlight = this.config.onTokenRefresh().finally(() => {
        this._refreshInFlight = null;
      });
    }
    const token = await this._refreshInFlight;
    this.config.token = token;
    return token;
  }

  private handleFetchError<T>(e: unknown): Result<T, SdkError> {
    if (e instanceof DOMException && e.name === "AbortError") {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, "Request timed out"));
    }
    return err(new SdkError(SdkErrorCode.NETWORK_ERROR, "Network request failed", { cause: e as Error }));
  }
}
