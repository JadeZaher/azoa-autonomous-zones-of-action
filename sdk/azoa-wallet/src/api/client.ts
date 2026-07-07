import type { Result } from "../core/result.js";
import { ok, err } from "../core/result.js";
import { SdkError, SdkErrorCode } from "../core/errors.js";
import type { SdkErrorDetail } from "../core/errors.js";
import { API_PATHS } from "./api-version.js";

export interface AzoaApiConfig {
  baseUrl: string;
  token?: string;
  apiKey?: string;
  onTokenRefresh?: () => Promise<string>;
  timeoutMs?: number;
  /**
   * Enable verbose diagnostics: every request/response/error is logged via
   * `debugLogger` and parsed server-side exception detail (when the backend
   * runs with `AZOA:DebugErrors`) is attached to the resulting `SdkError`.
   */
  debug?: boolean;
  /** Sink for debug output. Defaults to the global `console`. */
  debugLogger?: Pick<Console, "debug" | "error">;
}

// Mirrors the .NET AZOAResult<T> shape returned by most controllers.
// `error`/`detail` appear on error responses (and the unhandled-exception
// middleware payload); `detail` is only present in backend debug mode.
interface AZOAResponse<T> {
  isError: boolean;
  message?: string;
  error?: string;
  result?: T;
  detail?: SdkErrorDetail;
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
  /** Asset type — defaults to "NFT" on the backend. */
  assetType?: string;
  metadata?: NftMetadata;
  createdDate?: string;
  modifiedDate?: string;
  isActive: boolean;
}

/** Mirrors the .NET NftQueryRequest fields (sent as querystring). */
export interface NftQueryParams {
  ownerAvatarId?: string;
  chainId?: string;
  tokenId?: string;
  name?: string;
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
  /** Whether real-value bridge movement is permitted (kill switch). False = test-mode only. */
  realValueEnabled?: boolean;
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

/**
 * Mirrors .NET FungibleMintRequest — the body for `POST /api/nft/fungible-mint`
 * (the one-shot, no-DAG fungible token launch). Field shape matches the
 * `FungibleTokenCreate` quest node config; total + decimals are tenant-authoritative.
 */
export interface FungibleMintParams {
  /** Target chain (e.g. "Algorand"). Algorand-only in v1. */
  chainType: string;
  /** Human-readable asset name (ASA name). */
  name: string;
  /** Short unit name / ticker (ASA unit name). */
  unitName: string;
  /** Total supply in base units. Must be > 0. Sent as a number/bigint-safe value. */
  total: number;
  /** Number of decimal places (0..19). */
  decimals: number;
  /** Optional holon to link the created asset to (D10 Holon↔asset link). */
  holonId?: string;
}

/** Mirrors .NET FungibleTokenResult — the outcome of a fungible-token (ASA) launch. */
export interface FungibleTokenResult {
  /** The avatar the token was launched for. */
  avatarId: string;
  /** The custodial wallet the token was launched from. */
  walletId: string;
  /** The on-chain address of the custodial wallet. */
  walletAddress: string;
  /** True when this call provisioned a brand-new wallet. */
  walletProvisioned: boolean;
  /** The chain-native asset id of the created fungible token (ASA id). */
  assetId: string;
  /** The idempotency key the launch was deduped on (diagnostics). */
  idempotencyKey: string;
  /** True when this response replays a prior launch — no second token was created. */
  replayed: boolean;
}

export interface NftBurnParams {
  walletId: string;
}

// ─── Swap types (match SwapController DTOs) ───

/** Mirrors .NET SwapQuoteRequest (sent as querystring). */
export interface SwapQuoteParams {
  /** "algorand" or "solana". */
  chain: string;
  tokenIn: string;
  tokenOut: string;
  amountIn: string;
  /** Defaults to 50 (0.5%). */
  slippageBps?: number;
  /** Public key of the wallet requesting the swap (required for Jupiter v2). */
  walletAddress?: string;
}

/** Mirrors .NET SwapExecuteRequest (sent as JSON body). */
export interface SwapExecuteParams {
  chain: string;
  /** The quoteId returned from getSwapQuote(). */
  quoteId: string;
  walletAddress: string;
}

/** Mirrors .NET SwapQuoteResponse. */
export interface SwapQuoteResponse {
  chain: string;
  tokenIn: string;
  tokenOut: string;
  amountIn: string;
  expectedAmountOut: string;
  priceImpact: number;
  fee: string;
  route?: unknown;
  raw?: unknown;
  /** Unique quote identifier for downstream swap execution (Jupiter v2). */
  quoteId?: string;
  /** Base64-encoded unsigned swap transaction for client-side signing. */
  swapTransaction?: string;
  /** Last valid block height for the swap transaction (Solana). */
  lastValidBlockHeight?: number;
  /** Human-readable status message. */
  message?: string;
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
  /**
   * Render-ready holdings (fungible-mint-and-render-model §11.5). One entry per
   * asset — native coin, fungible token, NFT — carrying everything the UI needs to
   * render in this single call (raw + display amounts precomputed). Prefer this over
   * the legacy scalar `balance`/`nfts` fields when rendering.
   */
  assets: PortfolioAsset[];
  computedAt: string;
}

/** The kind of holding, so the UI can branch rendering without guessing. */
export type PortfolioAssetKind = "Native" | "Fungible" | "Nft";

/** A single render-ready holding from {@link PortfolioResult.assets}. */
export interface PortfolioAsset {
  /** Stable id: chain-native asset id / symbol for tokens, holon id for an NFT. */
  id: string;
  /** Holding kind. Serialized as the .NET enum name ("Native"|"Fungible"|"Nft"). */
  kind: PortfolioAssetKind;
  /** Short symbol / unit name / ticker. */
  symbol: string;
  /** Human-readable display name. */
  name: string;
  /** Decimal places relating rawAmount to displayAmount. */
  decimals: number;
  /** Raw on-chain amount in base units (arbitrary-precision string — chain truth). */
  rawAmount: string;
  /** Decimal-adjusted, human-readable amount (precomputed for the UI). */
  displayAmount: string;
  /** The chain this asset lives on (e.g. "Algorand"). */
  chain: string;
  /** Optional icon / metadata reference (e.g. an image URI). */
  iconRef?: string;
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
  /**
   * Optimistic-concurrency version (final-hardening F6). Bumped on every
   * publish/unpublish transition; a stale read racing a concurrent transition
   * surfaces as a "conflict" {@link SdkError} from {@link AzoaApiClient.publishQuest} /
   * {@link AzoaApiClient.unpublishQuest} / workflow run-start — reload and retry.
   */
  version: number;
  /**
   * Marketplace visibility. When true and the quest is Active, a non-owner
   * may start it (see {@link AzoaApiClient.executeQuest}) — the run is
   * created under the caller's own avatar, linked back to the origin quest.
   */
  isPublic: boolean;
  /**
   * Origin/creator avatar when this quest is a marketplace copy instantiated
   * from a template (set to the template author's avatar id). Undefined for
   * first-party quests created directly via {@link AzoaApiClient.createQuest}.
   */
  originAvatarId?: string;
}

/** Lifecycle status of a single {@link QuestRunResult} (one execution attempt of a Quest). */
export type QuestRunStatus =
  | "Pending"
  | "Running"
  | "Succeeded"
  | "Failed"
  | "Forked"
  | "Cancelled"
  | "Suspended"
  | "AwaitingSignal"
  | "AwaitingTimer"
  | "AwaitingReconciliation";

/**
 * One execution attempt of a {@link QuestResult} definition (mirrors .NET
 * `QuestRun`). Returned by {@link AzoaApiClient.executeQuest}. Per-node
 * runtime state lives on {@link QuestNodeResult}, queryable separately by run id.
 */
export interface QuestRunResult {
  id: string;
  questId: string;
  avatarId: string;
  actingTenantId?: string;
  status: QuestRunStatus;
  startedAt: string;
  endedAt?: string;
  parentRunId?: string;
  forkedAtNodeId?: string;
  forkReason?: string;
  failReason?: string;
  /**
   * Marketplace provenance: the origin quest this run was started from when
   * the runner is not the quest owner. Undefined for owner runs.
   */
  sourceQuestId?: string;
  /**
   * Marketplace provenance: the origin quest's owner/creator avatar when the
   * runner is not the owner. Undefined for owner runs.
   */
  originAvatarId?: string;
}

/** One registered entry in the opt-in Holon AssetType registry (final-hardening F5). */
export interface HolonTypeResult {
  id: string;
  assetType: string;
  description: string;
  requiredMetadataFields?: string[];
  isActive: boolean;
  createdDate: string;
  modifiedAt?: string;
}

/** Operator request body for {@link AzoaApiClient.registerHolonType}. */
export interface HolonTypeRegisterParams {
  assetType: string;
  description?: string;
  requiredMetadataFields?: string[];
  isActive?: boolean;
}

/** Filterable statuses for {@link AzoaApiClient.listSagaDeadLetters} (SagaOperatorController). */
export type SagaStepStatus = "DeadLettered" | "Parked" | "Cancelled";

/** Query options for {@link AzoaApiClient.listSagaDeadLetters}. */
export interface SagaDeadLetterListParams {
  /** Defaults to `["DeadLettered"]` server-side when omitted. */
  status?: SagaStepStatus[];
  /** Defaults to 100 server-side when omitted. */
  limit?: number;
}

/** Operator-facing projection of a saga step (mirrors .NET `SagaStepView`). */
export interface SagaStepView {
  id: string;
  sagaName: string;
  stepName: string;
  correlationKey: string;
  status: string;
  isCompensation: boolean;
  attemptCount: number;
  lastError?: string;
  gateId?: string;
  nextRunAt: string;
  updatedAt: string;
}

/** Result of {@link AzoaApiClient.requeueSagaStep} (bare response). */
export interface SagaStepRequeueResult {
  id: string;
  status: string;
  message: string;
}

/** Result of {@link AzoaApiClient.cancelSagaStep} (bare response). */
export interface SagaStepCancelResult {
  id: string;
  status: string;
  message: string;
}

/** Outcome of a wrapping-key rotation batch (mirrors .NET `KeyRotationReport`). Counts only — never key material. */
export interface KeyRotationReport {
  total: number;
  rewrapped: number;
  alreadyRotated: number;
  skipped: number;
  rolledBack: boolean;
}

/** Request body for {@link AzoaApiClient.rotateWalletKeys}. */
export interface KeyRotationParams {
  newEncryptionKey: string;
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
  /** Marketplace visibility. Defaults to false (private, owner-only) server-side. */
  isPublic?: boolean;
}

export interface QuestUpdateParams {
  name?: string;
  description?: string;
  status?: QuestStatus;
  /** Marketplace visibility toggle. Omit to leave unchanged. */
  isPublic?: boolean;
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

/**
 * Validates a non-UUID, URL-interpolated route segment (e.g. a natural-key
 * string like `assetType`) is a non-empty string before interpolation. The
 * path constant itself `encodeURIComponent`s the value, so this only guards
 * against an accidental empty segment collapsing the route.
 */
function assertNonEmpty(value: string, paramName: string): void {
  if (!value || value.trim().length === 0) {
    throw new SdkError(SdkErrorCode.INVALID_INPUT, `Invalid ${paramName}: must be a non-empty string`);
  }
}

export class AzoaApiClient {
  private config: AzoaApiConfig;
  private _refreshInFlight: Promise<string> | null = null;

  constructor(config: AzoaApiConfig) {
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

  /** Clear the cached token. Forces the next request to use onTokenRefresh. */
  clearToken(): void {
    this.config.token = undefined;
  }

  /** The AZOA API base URL this client is pointed at. */
  getBaseUrl(): string {
    return this.config.baseUrl;
  }

  /**
   * Toggle verbose diagnostics at runtime. When on, every
   * request/response/error is logged and the backend's server-side exception
   * chain (when it runs with `AZOA:DebugErrors`) is rendered via
   * `SdkError.debugString()` in error logs.
   */
  setDebug(enabled: boolean): void {
    this.config.debug = enabled;
  }

  /** Whether verbose diagnostics are currently enabled. */
  get debug(): boolean {
    return this.config.debug ?? false;
  }

  // ─── Avatar ───

  async login(email: string, password: string): Promise<Result<string, SdkError>> {
    // .NET returns AZOAResult<string> where Result is the JWT token
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
    // .NET returns AZOAResult<IAvatar>
    return this.request("POST", "/api/avatar/register", params);
  }

  /**
   * Server-side "logout everywhere": invalidates every live JWT for the
   * authenticated avatar (bumps AuthNotBefore server-side). The subject is
   * taken from the bearer token, never a URL/body param.
   */
  async logoutEverywhere(): Promise<Result<boolean, SdkError>> {
    return this.request("POST", API_PATHS.AVATAR_LOGOUT);
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

  /**
   * List NFTs matching optional query filters.
   * Maps to `GET /api/nft` (NftController.Query) — fields mirror NftQueryRequest.
   */
  async listNfts(params?: NftQueryParams): Promise<Result<NftResult[], SdkError>> {
    const qs = new URLSearchParams();
    if (params) {
      for (const [key, value] of Object.entries(params)) {
        if (value !== undefined) qs.set(key, String(value));
      }
    }
    const query = qs.toString();
    return this.request<NftResult[]>("GET", `/api/nft${query ? `?${query}` : ""}`);
  }

  async mintNft(params: NftMintParams): Promise<Result<unknown, SdkError>> {
    // POST /api/nft/mint with NftMintRequest body
    return this.request("POST", "/api/nft/mint", params);
  }

  /**
   * One-shot fungible token (ASA) mint — the direct (no-DAG) parallel to the
   * `FungibleTokenCreate` quest node. Maps to `POST /api/nft/fungible-mint`
   * (NftController.FungibleMint). KYC-gated (a fail-closed rejection surfaces as a
   * 403 → `SdkError` with the `KYC_FORBIDDEN:` message) and idempotent.
   *
   * Supply `options.idempotencyKey` to set the `Idempotency-Key` header; the
   * backend dedupes on it (absent ⇒ deterministic content key). A duplicate call
   * under the same key returns the ORIGINAL result with `replayed: true` and
   * creates NO second token.
   */
  async fungibleMint(
    params: FungibleMintParams,
    options?: { idempotencyKey?: string }
  ): Promise<Result<FungibleTokenResult, SdkError>> {
    const extraHeaders = options?.idempotencyKey
      ? { "Idempotency-Key": options.idempotencyKey }
      : undefined;
    return this.request<FungibleTokenResult>(
      "POST",
      "/api/nft/fungible-mint",
      params,
      false,
      extraHeaders
    );
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
  // BridgeController returns bare objects, not AZOAResult<T>

  async getBridgeRoutes(): Promise<Result<BridgeRouteInfo[], SdkError>> {
    // GET /api/bridge/routes — returns bare array, no AZOAResult wrapper
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

  /**
   * Render-model portfolio read (fungible-mint-and-render-model §11.5): the SAME
   * `GET /api/wallet/{id}/portfolio` call, typed to emphasise the render-ready
   * {@link PortfolioResult.assets} list — everything the UI needs to render the
   * wallet's holdings (native coin + fungible tokens + NFTs, with raw + display
   * amounts precomputed) in ONE round-trip. Chain stays source of truth; AZOA
   * stores no balance. Use this when driving a render off `result.value.assets`.
   */
  async getWalletPortfolioRenderModel(
    walletId: string
  ): Promise<Result<PortfolioResult, SdkError>> {
    assertUuid(walletId, "walletId");
    return this.request<PortfolioResult>("GET", `/api/wallet/${walletId}/portfolio`);
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

  /**
   * Create or update a STARODK record.
   *
   * Upsert by id — the backend's `POST /api/starodk` (STARODKController.CreateOrUpdate)
   * is the same endpoint also used to update an existing record. See {@link updateSTARODK}
   * for a semantically-explicit alias when the caller intends an update.
   */
  async createSTARODK(params: STARODKCreateParams): Promise<Result<STARODKResult, SdkError>> {
    return this.request("POST", "/api/starodk", params);
  }

  /**
   * Update an existing STARODK record via PUT. Routes through the same
   * `CreateOrUpdateAsync` upsert path as POST but uses the explicit-id route
   * `PUT /api/starodk/{id}`, making update intent unambiguous.
   */
  async updateSTARODK(id: string, model: STARODKCreateParams): Promise<Result<STARODKResult, SdkError>> {
    assertUuid(id, "starodkId");
    return this.request("PUT", `/api/starodk/${id}`, model);
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

  // ─── Holon AssetType registry (HolonTypeRegistryController, final-hardening F5) ───
  // Reads open to any authenticated caller; register/deactivate/delete are Operator-scoped.

  /** Lists every registered holon type, most recent first. */
  async listHolonTypes(): Promise<Result<HolonTypeResult[], SdkError>> {
    return this.request("GET", API_PATHS.HOLON_TYPES_LIST);
  }

  /** Gets one registered holon type by its AssetType name. */
  async getHolonType(assetType: string): Promise<Result<HolonTypeResult, SdkError>> {
    assertNonEmpty(assetType, "assetType");
    return this.request("GET", API_PATHS.HOLON_TYPES_GET(assetType));
  }

  /** Registers or re-registers a holon type. Operator-scoped. */
  async registerHolonType(params: HolonTypeRegisterParams): Promise<Result<HolonTypeResult, SdkError>> {
    assertNonEmpty(params.assetType, "assetType");
    return this.request("POST", API_PATHS.HOLON_TYPES_REGISTER, params);
  }

  /** Marks a registered type inactive (validation then ignores it). Operator-scoped. */
  async deactivateHolonType(assetType: string): Promise<Result<HolonTypeResult, SdkError>> {
    assertNonEmpty(assetType, "assetType");
    return this.request("POST", API_PATHS.HOLON_TYPES_DEACTIVATE(assetType));
  }

  /** Hard-deletes a holon type registration. Operator-scoped. */
  async deleteHolonType(assetType: string): Promise<Result<{ message: string }, SdkError>> {
    assertNonEmpty(assetType, "assetType");
    return this.request("DELETE", API_PATHS.HOLON_TYPES_DELETE(assetType));
  }

  // ─── Saga operator dead-letter surface (SagaOperatorController, final-hardening Phase-F) ───
  // Operator-scoped; bare response format (not AZOAResult<T>).

  /** Lists saga steps parked in the dead-letter queue (default) or any of {DeadLettered, Parked, Cancelled}, newest-updated first. */
  async listSagaDeadLetters(opts?: SagaDeadLetterListParams): Promise<Result<SagaStepView[], SdkError>> {
    const qs = new URLSearchParams();
    if (opts?.status) {
      for (const s of opts.status) qs.append("status", s);
    }
    if (opts?.limit !== undefined) qs.set("limit", String(opts.limit));
    const query = qs.toString();
    return this.requestBare("GET", `${API_PATHS.SAGA_DEAD_LETTERS}${query ? `?${query}` : ""}`);
  }

  /** Requeues a Parked/DeadLettered saga step back to Pending. Operator-scoped. */
  async requeueSagaStep(id: string): Promise<Result<SagaStepRequeueResult, SdkError>> {
    assertUuid(id, "id");
    return this.requestBare("POST", API_PATHS.SAGA_REQUEUE(id));
  }

  /** Terminally cancels a Pending/Parked/DeadLettered saga step so it never retries. Operator-scoped. */
  async cancelSagaStep(id: string, reason?: string): Promise<Result<SagaStepCancelResult, SdkError>> {
    assertUuid(id, "id");
    return this.requestBare("POST", API_PATHS.SAGA_CANCEL(id), reason ? { reason } : undefined);
  }

  // ─── Wallet wrapping-key rotation (KeyRotationController, final-hardening B5) ───
  // Operator-scoped; bare response format (not AZOAResult<T>).

  /** Re-wraps every wallet's ciphertext under a new encryption key. Idempotent/resumable; rolls back on any failure. */
  async rotateWalletKeys(params: KeyRotationParams): Promise<Result<KeyRotationReport, SdkError>> {
    assertNonEmpty(params.newEncryptionKey, "newEncryptionKey");
    return this.requestBare("POST", API_PATHS.KEY_ROTATION_ROTATE, params);
  }

  // ─── Swap (SwapController) ───

  /**
   * Get a swap quote from the backend's swap manager (dispatches to Tinyman
   * for Algorand and Jupiter for Solana). Maps to `GET /api/swap/quote`.
   */
  async getSwapQuote(params: SwapQuoteParams): Promise<Result<SwapQuoteResponse, SdkError>> {
    const qs = new URLSearchParams();
    for (const [key, value] of Object.entries(params)) {
      if (value !== undefined) qs.set(key, String(value));
    }
    return this.request<SwapQuoteResponse>("GET", `/api/swap/quote?${qs}`);
  }

  /**
   * Execute a previously-fetched swap quote — returns an unsigned swap
   * transaction the caller signs and broadcasts. Maps to `POST /api/swap/execute`.
   *
   * Supply `options.idempotencyKey` to set the `Idempotency-Key` request header;
   * the backend dedupes against this key. When absent the server falls back to
   * a deterministic content key.
   */
  async executeSwap(
    params: SwapExecuteParams,
    options?: { idempotencyKey?: string }
  ): Promise<Result<SwapQuoteResponse, SdkError>> {
    const extraHeaders = options?.idempotencyKey
      ? { "Idempotency-Key": options.idempotencyKey }
      : undefined;
    return this.request<SwapQuoteResponse>(
      "POST",
      "/api/swap/execute",
      params,
      false,
      extraHeaders
    );
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

  /**
   * Run the full validation stack and flip the quest from Draft to Active.
   * Returns validation errors on failure (400); quest stays Draft. Also fails
   * (400, message containing "Publish conflict") when a concurrent
   * publish/unpublish raced this call and won the optimistic-concurrency check
   * on {@link QuestResult.version} (final-hardening F6) — use {@link isQuestConflict}
   * to detect this case and prompt the caller to reload + retry.
   * See Managers/AGENTS.md §publish-lifecycle.
   */
  async publishQuest(questId: string): Promise<Result<QuestResult, SdkError>> {
    assertUuid(questId, "questId");
    return this.request("POST", API_PATHS.QUEST_PUBLISH(questId));
  }

  /**
   * Flip an Active quest back to Draft so its definition can be mutated.
   * Returns an error if any in-flight runs exist for this quest, or (message
   * containing "Unpublish conflict") if a concurrent transition raced and won
   * the version check (final-hardening F6) — see {@link isQuestConflict}.
   */
  async unpublishQuest(questId: string): Promise<Result<QuestResult, SdkError>> {
    assertUuid(questId, "questId");
    return this.request("POST", API_PATHS.QUEST_UNPUBLISH(questId));
  }

  /**
   * Execute all ready nodes in the quest DAG. Returns the produced
   * {@link QuestRunResult} (one execution attempt) — NOT the quest
   * definition. Fails (400, message containing "Quest run conflict") if the
   * quest was unpublished or modified concurrently (final-hardening F6) —
   * see {@link isQuestConflict}. A non-owner may call this on a
   * `isPublic` quest (quest-marketplace-provenance); the resulting run is
   * owned by the caller and carries {@link QuestRunResult.sourceQuestId} /
   * {@link QuestRunResult.originAvatarId} back to the quest owner.
   */
  async executeQuest(questId: string): Promise<Result<QuestRunResult, SdkError>> {
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

  /**
   * Send a request to an AZOAResult<T>-wrapped endpoint. Public for use by
   * query builders. The optional `extraHeaders` argument adds caller-supplied
   * headers (e.g., `Idempotency-Key`) on top of the auth + content-type
   * headers the client builds for every request.
   */
  async request<T>(
    method: string,
    path: string,
    body?: unknown,
    _retried: boolean = false,
    extraHeaders?: Record<string, string>
  ): Promise<Result<T, SdkError>> {
    this.logRequest(method, path, body);
    try {
      const resp = await this.fetchWithAuth(method, path, body, extraHeaders);

      if (resp.status === 401 && this.config.onTokenRefresh && !_retried) {
        try {
          await this.getOrRefreshToken(true);
        } catch (refreshErr) {
          return this.handleFetchError(method, path, refreshErr);
        }
        return this.request(method, path, body, true, extraHeaders);
      }

      if (resp.status === 401 && _retried) {
        return this.fail(new SdkError(SdkErrorCode.AUTH_EXPIRED, `${method} ${path}: session expired. Please log in again.`, { status: 401, method, path }));
      }

      if (!resp.ok) {
        const parsed = await this.parseErrorBody(resp);
        return this.fail(this.apiError(method, path, resp.status, parsed));
      }

      const data = (await resp.json()) as AZOAResponse<T>;

      if (data.isError) {
        return this.fail(this.apiError(method, path, resp.status, {
          message: data.message ?? data.error,
          detail: data.detail,
        }));
      }

      this.logResponse(method, path, resp.status);
      return ok(data.result as T);
    } catch (e) {
      return this.handleFetchError(method, path, e);
    }
  }

  /** For endpoints that return bare objects (BridgeController pattern). */
  async requestBare<T>(method: string, path: string, body?: unknown, _retried = false): Promise<Result<T, SdkError>> {
    this.logRequest(method, path, body);
    try {
      const resp = await this.fetchWithAuth(method, path, body);

      if (resp.status === 401 && this.config.onTokenRefresh && !_retried) {
        try {
          await this.getOrRefreshToken(true);
        } catch (refreshErr) {
          return this.handleFetchError(method, path, refreshErr);
        }
        return this.requestBare(method, path, body, true);
      }

      if (resp.status === 401 && _retried) {
        return this.fail(new SdkError(SdkErrorCode.AUTH_EXPIRED, `${method} ${path}: session expired. Please log in again.`, { status: 401, method, path }));
      }

      if (!resp.ok) {
        const parsed = await this.parseErrorBody(resp);
        return this.fail(this.apiError(method, path, resp.status, parsed));
      }

      const data = (await resp.json()) as T;
      this.logResponse(method, path, resp.status);
      return ok(data);
    } catch (e) {
      return this.handleFetchError(method, path, e);
    }
  }

  /**
   * Parse an error response body, tolerating both the AZOAResult shape
   * (`message`) and the bare-error shape (`error`), plus the verbose
   * `detail` exception chain present when backend debug mode is on.
   */
  private async parseErrorBody(
    resp: Response
  ): Promise<{ message?: string; detail?: SdkErrorDetail }> {
    try {
      const body = (await resp.json()) as {
        message?: string;
        error?: string;
        detail?: SdkErrorDetail;
      };
      return { message: body.message ?? body.error, detail: body.detail };
    } catch {
      return {}; // non-JSON / empty body (e.g. a bare 500 with no body)
    }
  }

  private apiError(
    method: string,
    path: string,
    status: number,
    parsed: { message?: string; detail?: SdkErrorDetail }
  ): SdkError {
    const message = parsed.message
      ? `${method} ${path}: ${parsed.message}`
      : `${method} ${path} failed with HTTP ${status}`;
    return new SdkError(SdkErrorCode.API_ERROR, message, {
      status,
      method,
      path,
      detail: parsed.detail,
    });
  }

  private fail<T>(e: SdkError): Result<T, SdkError> {
    this.logError(e);
    return err(e);
  }

  private get logger(): Pick<Console, "debug" | "error"> {
    return this.config.debugLogger ?? console;
  }

  private logRequest(method: string, path: string, body?: unknown): void {
    if (!this.config.debug) return;
    this.logger.debug(
      `[azoa-sdk] → ${method} ${path}`,
      body !== undefined ? redactSecrets(body) : ""
    );
  }

  private logResponse(method: string, path: string, status: number): void {
    if (!this.config.debug) return;
    this.logger.debug(`[azoa-sdk] ← ${method} ${path} ${status}`);
  }

  private logError(e: SdkError): void {
    if (!this.config.debug) return;
    this.logger.error(`[azoa-sdk] ✗ ${e.debugString()}`);
  }

  private async fetchWithAuth(
    method: string,
    path: string,
    body?: unknown,
    extraHeaders?: Record<string, string>
  ): Promise<Response> {
    let token = this.config.token;
    if (!token && this.config.onTokenRefresh) {
      try {
        token = await this.getOrRefreshToken();
      } catch (e) {
        // Initial token fetch failed — likely no session active.
        // We continue anyway as the endpoint might be anonymous.
        // If it's NOT anonymous, the server will return 401 and we'll handle it in request().
      }
    }

    const headers: Record<string, string> = {};
    if (body) headers["Content-Type"] = "application/json";
    if (token) {
      headers["Authorization"] = `Bearer ${token}`;
    } else if (this.config.apiKey) {
      headers["X-Api-Key"] = this.config.apiKey;
    }
    if (extraHeaders) {
      for (const [k, v] of Object.entries(extraHeaders)) {
        headers[k] = v;
      }
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
  private async getOrRefreshToken(force = false): Promise<string | undefined> {
    if (this.config.token && !force) return this.config.token;
    if (!this.config.onTokenRefresh) return undefined;
    if (!this._refreshInFlight) {
      // If forcing, clear the cached token first so the refresh callback is guaranteed to be used
      if (force) this.config.token = undefined;
      
      this._refreshInFlight = this.config.onTokenRefresh().finally(() => {
        this._refreshInFlight = null;
      });
    }
    const token = await this._refreshInFlight;
    this.config.token = token;
    return token;
  }

  private handleFetchError<T>(method: string, path: string, e: unknown): Result<T, SdkError> {
    if (e instanceof DOMException && e.name === "AbortError") {
      return this.fail(new SdkError(SdkErrorCode.NETWORK_ERROR, `${method} ${path}: request timed out`, { method, path }));
    }
    return this.fail(new SdkError(SdkErrorCode.NETWORK_ERROR, `${method} ${path}: network request failed`, { method, path, cause: e as Error }));
  }
}

/** Shallow-redact obvious credentials before logging a request body. */
function redactSecrets(value: unknown): unknown {
  if (!value || typeof value !== "object") return value;
  const SECRET_RE = /pass(word)?|token|secret|api[-_]?key|mnemonic|privatekey|seed/i;
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
    out[k] = SECRET_RE.test(k) ? "«redacted»" : v;
  }
  return out;
}
