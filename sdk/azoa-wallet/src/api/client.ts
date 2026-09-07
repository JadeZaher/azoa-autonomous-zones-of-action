import type { Result } from "../core/result.js";
import { ok, err } from "../core/result.js";
import { SdkError, SdkErrorCode } from "../core/errors.js";
import type { SdkErrorDetail } from "../core/errors.js";
import { API_PATHS } from "./api-version.js";
import type { WorkflowRunStatus } from "../workflow/types.js";

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

export interface UserResponse {
  id: string;
  username: string;
  email: string;
  title?: string;
  firstName?: string;
  lastName?: string;
  isActive: boolean;
}

export type TenantKycStatus = "Unknown" | "Pending" | "Approved" | "Rejected";

/** Secret-free tenant account projection returned to platforms such as ArdaNova. */
export interface TenantCustodialAccountStatus {
  tenantId: string;
  externalSubject: string;
  /** Compatibility alias emitted for the initial ArdaNova integration. */
  ardanovaUserId: string;
  userId: string | null;
  walletId: string | null;
  walletAddress: string | null;
  kycStatus: TenantKycStatus;
  identityReady: boolean;
  kycReady: boolean;
  walletReady: boolean;
  ready: boolean;
  unavailableReason: string | null;
}

export interface TenantCustodialCapabilities {
  enabled: boolean;
  walletChain: string;
  custodyMode: string;
  custodyAvailable: boolean;
  blockchainProviderAvailable: boolean;
  kycProvider: string;
  kycAvailable: boolean;
  hostedVerification: boolean;
  acceptsDocumentReferences: boolean;
  developmentSimulation: boolean;
  identityReady: boolean;
  kycReady: boolean;
  walletProvisioningReady: boolean;
  ready: boolean;
  unavailableReason: string | null;
}

export interface TenantKycSession {
  provider: string;
  hostedVerification: boolean;
  acceptsDocumentReferences: boolean;
  developmentSimulation: boolean;
  verificationUrl: string | null;
  expiresAt: string | null;
  instructions: string | null;
}

export interface TenantKycDocumentReference {
  type: "GOVERNMENT_ID" | "PASSPORT" | "DRIVERS_LICENSE" | "SELFIE" | "PROOF_OF_ADDRESS";
  referenceUrl: string;
  fileName: string;
  mimeType?: string;
  fileSizeBytes?: number;
}

export interface TenantKycSubmission {
  submissionId: string;
  status: TenantKycStatus;
  submittedAt: string;
  expiresAt: string | null;
}

/**
 * Assignable DApp role — the closed allowlist the `PUT /api/user/{id}/dapp-role`
 * endpoint accepts (mirrors `Core/AzoaDappRoles.cs`). `operator:admin` is
 * deliberately absent: operator authority is a JWT-only scope, never a DApp role,
 * so no value assignable here can yield operator privileges.
 */
export type DappRole = "dapp:user" | "dapp:developer" | "dapp:manager";

/** Runtime allowlist backing {@link DappRole}; kept in lock-step with the backend. */
export const DAPP_ROLES: readonly DappRole[] = ["dapp:user", "dapp:developer", "dapp:manager"] as const;

export interface NftResult {
  id: string;
  name: string;
  description: string;
  ownerUserId?: string;
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
  ownerUserId?: string;
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

// Public bridge response. Server-only idempotency metadata is deliberately omitted.
export interface BridgeTransactionResult {
  id: string;
  userId: string;
  sourceChain: string;
  targetChain: string;
  sourceTokenId: string;
  targetTokenId?: string;
  sourceAddress: string;
  targetAddress: string;
  /** Unsigned base-unit amount encoded as a decimal string. */
  amount: string;
  /** BridgeStatus enum returned by the API. */
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
  targetUserId: string;
  walletId: string;
  memo?: string;
}

/**
 * Mirrors .NET FungibleMintRequest — the body for `POST /api/nft/fungible-mint`
 * (the one-shot, no-DAG fungible token launch). Field shape matches the
 * `FungibleTokenCreate` action node config; total + decimals are tenant-authoritative.
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
  /** The user the token was launched for. */
  userId: string;
  /** The custodial wallet the token was launched from. */
  walletId: string;
  /** The on-chain address of the custodial wallet. */
  walletAddress: string;
  /** True when this call provisioned a brand-new wallet. */
  walletProvisioned: boolean;
  /** The chain-native asset id of the created fungible token (ASA id). */
  assetId: string;
  /** True when this response replays a prior launch — no second token was created. */
  replayed: boolean;
}

export interface NftBurnParams {
  walletId: string;
}

// ─── Credential types matching .NET DTOs (CredentialController, D19) ───

/** Mirrors .NET CredentialRevokeRequest — the body for `POST /api/credential/{assetId}/revoke`. */
export interface CredentialRevokeParams {
  /** Chain the credential lives on. Algorand-only (ASA clawback) in v1. */
  chainType: string;
  /** Optional free-text reason recorded on the operation row. */
  reason?: string;
}

/** The OperationStatus constants a revocation can carry; only "Completed" is a confirmed clawback. */
export type CredentialRevokeStatus = "Completed" | "PendingConfirmation" | "Failed";

/**
 * Mirrors .NET CredentialRevokeResult. `status` is the ONLY authority on what happened —
 * branch on it (or `isConfirmed`), never on "the call returned a result". The SDK never
 * returns a "PendingConfirmation" outcome as `ok`; see {@link isCredentialRevocationPending}.
 */
export interface CredentialRevokeResult {
  /** Chain-native asset id (ASA id) of the revoked credential. */
  assetId: string;
  /** AZOA's ownership-row id for the credential. */
  credentialId: string;
  /** The issuer that authorized this revocation — never the holder. */
  issuerUserId: string;
  /** The user the credential was taken from. */
  holderUserId: string;
  /** On-chain address the credential was clawed back FROM. */
  holderAddress: string;
  /** On-chain address the credential was clawed back TO (the issuer's wallet). */
  recipientAddress: string;
  chainType: string;
  status: CredentialRevokeStatus;
  /** True only when `status` is "Completed". */
  isConfirmed: boolean;
  /** Present whenever a transaction reached the network — including unconfirmed / failed-after-broadcast. */
  txHash?: string;
  /** The `CredentialRevoke` operation row recording this move (D16 analytics). */
  operationId: string;
  /** True when this response replays a prior revocation under the same idempotency key. */
  replayed: boolean;
}

/**
 * True when a revoke came back HTTP 202: broadcast but NOT confirmed. The credential may
 * or may not have moved — reconcile against chain truth; do NOT re-submit.
 */
export function isCredentialRevocationPending(error: SdkError): boolean {
  return error.code === SdkErrorCode.API_ERROR && error.status === 202;
}

// ─── Analytics types matching .NET DTOs (AnalyticsController, D16) ───

/** Mirrors .NET AnalyticsQueryRequest (sent as querystring). ISO-8601 window bounds; skip/take page. */
export interface AnalyticsQueryParams {
  from?: string;
  to?: string;
  skip?: number;
  take?: number;
}

/** Mirrors .NET PricingStatus. "NeverPriced" is a fact, not a zero — never default `value` to 0. */
export type PricingStatus = "NeverPriced" | "Priced";

/** Mirrors .NET QuantityStatus. "Unknown" is a fact, not a zero — never read `amount ?? "0"`. */
export type QuantityStatus = "Unknown" | "Known";

/** Mirrors .NET ValueStampResponse — every component a string. */
export interface ValueStampResponse {
  /** ISO-4217 code of the stamped currency. */
  currencyCode: string;
  /** Decimal string in the currency's minor unit. */
  minorUnits: string;
  /** The rate applied; "0" is a real quote of zero, not an absent one. */
  rate: string;
  /** When the applied rate was observed — not when it was applied. */
  rateAsOf: string;
  rateSource: string;
}

/** Mirrors .NET TransactionHistoryEntry. Money fields are base-unit decimal strings, never numbers. */
export interface TransactionHistoryEntry {
  operationId: string;
  timestamp: string;
  settledAt?: string;
  operationType: string;
  /** An OperationStatus constant. */
  status: string;
  assetType?: string;
  assetId?: string;
  chainType?: string;
  /** Base-unit decimal string; absent iff `amountStatus` is "Unknown". */
  amount?: string;
  amountStatus: QuantityStatus;
  counterpartyAddress?: string;
  txHash?: string;
  /** Base-unit decimal string. */
  fee?: string;
  /** False for Swap/Exchange/Deploy/Call and non-broadcast rows — not a pricing gap. */
  isStampable: boolean;
  pricing: PricingStatus;
  /** Present iff `pricing` is "Priced". */
  value?: ValueStampResponse;
}

/** Mirrors .NET TransactionHistoryPage — one page, newest first. */
export interface TransactionHistoryPage {
  items: TransactionHistoryEntry[];
  skip: number;
  take: number;
  hasMore: boolean;
  /** Rows in this page carrying no value stamp. */
  unpricedEntryCount: number;
  /** Rows in this page whose quantity the log does not record. */
  unknownAmountEntryCount: number;
}

/** Mirrors .NET FiatValueResponse. Present only when every contributing entry was really priced. */
export interface FiatValueResponse {
  currencyCode: string;
  /** Signed decimal string in the currency's minor unit. */
  minorUnits: string;
}

/** Mirrors .NET PositionSummary. A fiat total is either complete or absent — never a partial sum. */
export interface PositionSummary {
  chainType?: string;
  /** Absent means the chain's own native asset. */
  assetId?: string;
  /** Signed base-unit decimal string of settled, direction-classified movement. */
  quantity: string;
  entryCount: number;
  unsettledEntryCount: number;
  unclassifiedEntryCount: number;
  unknownAmountEntryCount: number;
  isQuantityComplete: boolean;
  pricedEntryCount: number;
  unpricedEntryCount: number;
  /** Subset of `unpricedEntryCount` that can NEVER be stamped by design (e.g. Swap). */
  unstampableEntryCount: number;
  isValueComplete: boolean;
  /** Absent unless the position is value- and quantity-complete in one currency. */
  realizedValue?: FiatValueResponse;
  /** Always absent today: no current-rate source exists. */
  unrealizedValue?: FiatValueResponse;
}

/** Mirrors .NET PositionsResponse. `isComplete: false` = truncated scan, never a final total. */
export interface PositionsResponse {
  items: PositionSummary[];
  scannedEntryCount: number;
  isComplete: boolean;
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
  /** Unsigned base-unit amount in the inclusive range 1..2^64-1. */
  amount: string;
  mode?: "Trusted" | "Wormhole";
}

export interface SearchParams {
  query: string;
  entityTypes?: number;
  chainId?: string;
  assetType?: string;
  userId?: string;
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
  userId: string;
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
  userId?: string;
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
  userId: string | null;
  walletId: string | null;
  operationType: string;
  status: string;
  createdDate: string;
  completedDate: string | null;
  /** Safe public chain identifier, when the operation recorded one. */
  chainType: string | null;
  /** Safe public network identifier, when the operation recorded one. */
  chainNetwork: string | null;
  /** Public transaction reference, when the provider recorded one. */
  transactionReference: string | null;
}

// ─── STARODK types matching .NET DTOs ───

export interface STARODKResult {
  id: string;
  name: string;
  description: string;
  publicKey?: string;
  userId?: string;
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
  userId?: string;
}

export interface STARDappGenerationParams {
  targetChain: string;
  boundHolonIds: string[];
  config?: Record<string, string>;
}

// ─── UserNFT types matching .NET DTOs ───

export interface UserNFTResult {
  id: string;
  userId: string;
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

export interface UserNFTMintParams {
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
  userNFTId: string;
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
  userNFTId: string;
  bindingType: string;
  accessLevel?: string;
  accessPermissions: Record<string, string>;
  createdDate: string;
  lastUpdatedDate?: string;
  isActive: boolean;
}

// ─── Action types matching .NET DTOs ───

/** Action status lifecycle */
export type ActionStatus = "Draft" | "Active" | "Completed" | "Failed" | "Archived";

/** Node execution state */
export type ActionNodeState = "Pending" | "Running" | "Succeeded" | "Failed" | "Skipped";

/** Edge types for DAG flow control */
export type ActionEdgeType = "Control" | "Conditional";

/** All supported action node operation types */
export type ActionNodeType =
  | "HolonCreate" | "HolonUpdate" | "HolonDelete" | "HolonQuery"
  | "NftMint" | "NftTransfer" | "NftBurn"
  | "WalletCreate" | "WalletTransfer" | "WalletBalance"
  | "StarCreate" | "StarGenerate" | "StarDeploy"
  | "SearchQuery"
  | "UserNftMint" | "UserNftTransfer" | "UserNftVerify"
  | "BlockchainExecute"
  | "Gate" | "Delay" | "Webhook" | "Script"
  // Value-rail primitives (mirrors backend ActionNodeType). Governance + CredentialRevoke are
  // NOT enabled server-side (governance-vote-of-record_20260906, credential-third-party-issuance_20260906).
  | "CredentialRevoke"
  | "GovernanceProposalCreate" | "GovernanceVote" | "GovernanceTally"
  | "LoanOpen" | "LoanRepaymentRecord" | "LoanBalance"
  | string; // extensible

export interface ActionNodeResult {
  id: string;
  actionId: string;
  name: string;
  nodeType: string;
  state: ActionNodeState;
  config: string;
  output?: string;
  error?: string;
  isEntry: boolean;
  isTerminal: boolean;
  nodeTemplateId?: string;
  startedAt?: string;
  completedAt?: string;
}

export interface ActionEdgeResult {
  id: string;
  actionId: string;
  sourceNodeId: string;
  targetNodeId: string;
  condition?: string;
  edgeType: ActionEdgeType;
}

export interface ActionDependencyResult {
  id: string;
  actionId: string;
  dependsOnActionId: string;
  dependencyType: string;
}

export interface ActionResult {
  id: string;
  name: string;
  description?: string;
  userId: string;
  status: ActionStatus;
  nodes: ActionNodeResult[];
  edges: ActionEdgeResult[];
  dependencies: ActionDependencyResult[];
  templateId?: string;
  dappSeriesId?: string;
  metadata: Record<string, string>;
  createdDate: string;
  completedDate?: string;
  /**
   * Optimistic-concurrency version (final-hardening F6). Bumped on every
   * publish/unpublish transition; a stale read racing a concurrent transition
   * surfaces as a "conflict" {@link SdkError} from {@link AzoaApiClient.publishAction} /
   * {@link AzoaApiClient.unpublishAction} / workflow run-start — reload and retry.
   */
  version: number;
  /**
   * Marketplace visibility. When true and the action is Active, a non-owner
   * may start it (see {@link AzoaApiClient.executeAction}) — the run is
   * created under the caller's own user, linked back to the origin action.
   */
  isPublic: boolean;
  /**
   * Origin/creator user when this action is a marketplace copy instantiated
   * from a template (set to the template author's user id). Undefined for
   * first-party actions created directly via {@link AzoaApiClient.createAction}.
   */
  originUserId?: string;
}

/**
 * Lifecycle status of a single {@link ActionRunResult} (one execution attempt of an
 * action). Single source of truth: this is the {@link WorkflowRunStatus} union from
 * `workflow/types.ts` (both mirror C# `Models/Actions/ActionRunStatus.cs` 1:1) — the
 * alias keeps the two public surfaces from drifting apart.
 */
export type ActionRunStatus = WorkflowRunStatus;

/**
 * One execution attempt of a {@link ActionResult} definition (mirrors .NET
 * `ActionRun`). Returned by {@link AzoaApiClient.executeAction}. Per-node
 * runtime state lives on {@link ActionNodeResult}, queryable separately by run id.
 */
export interface ActionRunResult {
  id: string;
  actionId: string;
  userId: string;
  actingTenantId?: string;
  status: ActionRunStatus;
  startedAt: string;
  endedAt?: string;
  parentRunId?: string;
  forkedAtNodeId?: string;
  forkReason?: string;
  failReason?: string;
  /**
   * Marketplace provenance: the origin action this run was started from when
   * the runner is not the action owner. Undefined for owner runs.
   */
  sourceActionId?: string;
  /**
   * Marketplace provenance: the origin action's owner/creator user when the
   * runner is not the owner. Undefined for owner runs.
   */
  originUserId?: string;
}

// ─── Action invitations + request/approval (action-invitations-approval) ───

/**
 * Run-authorization mode for an action, orthogonal to `isPublic` discoverability
 * (mirrors C# `Models/Actions/ActionRunAccess`). `Open` = anyone who can view may
 * run/fork (today's default); `InviteOnly` = only owner + invited users may
 * run/fork, though the action stays viewable.
 */
export type ActionRunAccess = "Open" | "InviteOnly";

/**
 * Lifecycle status of a {@link ActionAccessRequest} (mirrors C#
 * `Models/Actions/ActionAccessRequestStatus`). Terminal states
 * (`Approved | Rejected | Withdrawn`) are immutable; a re-request after a
 * terminal state opens a fresh `Pending`.
 */
export type ActionAccessRequestStatus = "Pending" | "Approved" | "Rejected" | "Withdrawn";

/**
 * A self-service request by a non-invited user to run an `InviteOnly` action
 * (mirrors .NET `ActionAccessRequest`). Owner approval appends the requester to
 * the action's invited set; reject/withdraw closes it.
 */
export interface ActionAccessRequest {
  id: string;
  actionId: string;
  requesterUserId: string;
  status: ActionAccessRequestStatus;
  message?: string;
  decisionReason?: string;
  createdAt: string;
  decidedAt?: string;
  decidedByUserId?: string;
}

/** Request body for {@link AzoaApiClient.setActionRunAccess}. */
export interface SetRunAccessRequest {
  runAccess: ActionRunAccess;
  /** Optionally seed the invite list when switching to `InviteOnly`. */
  invitedUserIds?: string[];
}

/** Request body for {@link AzoaApiClient.inviteUser}. */
export interface InviteUserRequest {
  userId: string;
}

/** Request body for {@link AzoaApiClient.requestActionAccess}. */
export interface RequestAccessBody {
  /** Optional note from the requester to the owner. */
  message?: string;
}

/** Request body for {@link AzoaApiClient.decideAccessRequest}. */
export interface DecideAccessRequestBody {
  approve: boolean;
  /** Optional reason recorded on the decision (approve or reject). */
  reason?: string;
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

export interface ActionNodeCreateParams {
  name: string;
  nodeType: ActionNodeType;
  config?: string;
  isEntry?: boolean;
  isTerminal?: boolean;
  nodeTemplateId?: string;
}

export interface ActionEdgeCreateParams {
  sourceNodeId: number;
  targetNodeId: number;
  condition?: string;
  edgeType?: ActionEdgeType;
}

export interface ActionCreateParams {
  name: string;
  description?: string;
  nodes: ActionNodeCreateParams[];
  edges: ActionEdgeCreateParams[];
  /** Marketplace visibility. Defaults to false (private, owner-only) server-side. */
  isPublic?: boolean;
}

export interface ActionUpdateParams {
  name?: string;
  description?: string;
  status?: ActionStatus;
  /** Marketplace visibility toggle. Omit to leave unchanged. */
  isPublic?: boolean;
}

export interface ActionTemplateResult {
  id: string;
  name: string;
  description?: string;
  authorUserId: string;
  parameters: string;
  version: string;
  isPublic: boolean;
  tags: string[];
  nodes: ActionNodeResult[];
  edges: ActionEdgeResult[];
  createdDate: string;
}

export interface ActionTemplateCreateParams {
  name: string;
  description?: string;
  nodes: ActionNodeCreateParams[];
  edges: ActionEdgeCreateParams[];
  parameters?: string;
  version?: string;
  isPublic?: boolean;
  tags?: string[];
}

export interface ActionNodeTemplateResult {
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

export interface ActionNodeTemplateCreateParams {
  name: string;
  nodeType: ActionNodeType;
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

/**
 * The self-issuable scopes an ordinary user may attach to its own new API key
 * (mirrors .NET AzoaScopes.SelfIssuableScopes). An empty CSV still means legacy
 * "full access"; these are the explicit opt-in scopes.
 */
export type SelfIssuableApiKeyScope =
  | "dapp:develop"
  | "dapp:manage"
  | "wallet:manage"
  | "nft:mint"
  | "action:execute"
  | "swap:sign"
  | "transfer:sign"
  | "grant:sign"
  | "token:create:sign";

/** One self-issuable API-key scope with its human description. */
export interface SelfIssuableApiKeyScopeInfo {
  scope: string;
  description: string;
  isSelfIssuable: boolean;
}

/** Backwards-compatible alias for existing SDK consumers. */
export type ApiKeyScope = SelfIssuableApiKeyScope;

/** Backwards-compatible alias for existing SDK consumers. */
export type ApiKeyScopeInfo = SelfIssuableApiKeyScopeInfo;

// ─── Allocation types (Fiat Stripe Bridge) ───

export interface AllocationRequest {
  kind: "Mint" | "Transfer";
  chainType: string;
  amount: string;
  name?: string;
  description?: string;
  assetId?: string;
  assetRecordId?: string;
  metadata?: Record<string, string>;
  memo?: string;
}

export interface AllocationResult {
  userId: string;
  walletId: string;
  walletAddress: string;
  walletProvisioned: boolean;
  operationId: string;
  replayed: boolean;
  grossAmount: string;
  nodeFeeAmount: string;
  netAmount: string;
  nodeFeeScheduleVersion: number;
}

// ─── DappSeries types matching .NET DTOs (DappSeriesController) ───

/** DappSeries lifecycle status (mirrors .NET DappSeries.StatusKind). */
export type DappSeriesStatus = "Draft" | "Building" | "Ready" | "Deployed" | "Archived";

/** A linked series of action DAGs that compose into a deployable dApp (mirrors .NET DappSeries). */
export interface DappSeriesResult {
  id: string;
  name: string;
  description?: string;
  userId: string;
  status: DappSeriesStatus;
  /** Cross-action deployment settings (string→string map), serialized as JSON on the row. */
  sharedConfig?: unknown;
  starOdkId?: string;
  targetChain?: string;
  /** Composed DappManifest as a JSON string (null until ComposeAsync runs). */
  manifest?: string;
  createdDate: string;
  deployedDate?: string;
}

/** Body for {@link AzoaApiClient.createDappSeries} (mirrors .NET DappSeriesCreateModel). */
export interface DappSeriesCreateParams {
  name: string;
  description?: string;
}

/** Body for {@link AzoaApiClient.updateDappSeries} (mirrors .NET DappSeriesUpdateModel). */
export interface DappSeriesUpdateParams {
  name?: string;
  description?: string;
  targetChain?: string;
  sharedConfig?: Record<string, string>;
}

/** One ordered action entry inside a series (mirrors .NET DappSeriesAction). */
export interface DappSeriesActionResult {
  id: string;
  dappSeriesId: string;
  actionId: string;
  /** 1-indexed execution order within the series. */
  order: number;
  /** JSON array of InputMapping entries; null when no cross-action flow. */
  inputMappings?: string;
}

/** Body for {@link AzoaApiClient.addSeriesAction} (mirrors .NET DappSeriesAddActionModel). */
export interface DappSeriesAddActionParams {
  actionId: string;
  order: number;
  /** JSON array of InputMapping entries; omit when no cross-action flow. */
  inputMappings?: string;
}

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const ULONG_MAX = 18_446_744_073_709_551_615n;

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

function assertPositiveUlongDecimal(value: string, paramName: string): void {
  if (typeof value !== "string" || value.length > 20 || !/^[0-9]+$/.test(value)) {
    throw new SdkError(
      SdkErrorCode.INVALID_INPUT,
      `Invalid ${paramName}: expected a positive unsigned 64-bit decimal string`
    );
  }

  const parsed = BigInt(value);
  if (parsed === 0n || parsed > ULONG_MAX) {
    throw new SdkError(
      SdkErrorCode.INVALID_INPUT,
      `Invalid ${paramName}: expected a value between 1 and ${ULONG_MAX}`
    );
  }
}

/** Appends the defined AnalyticsQueryParams as a querystring. */
function withAnalyticsQuery(path: string, query?: AnalyticsQueryParams): string {
  const qs = new URLSearchParams();
  if (query) {
    for (const [key, value] of Object.entries(query)) {
      if (value !== undefined) qs.set(key, String(value));
    }
  }
  const encoded = qs.toString();
  return encoded ? `${path}?${encoded}` : path;
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

  // Tenant custodial onboarding

  async getTenantCustodialCapabilities(): Promise<Result<TenantCustodialCapabilities, SdkError>> {
    return this.request("GET", API_PATHS.TENANT_CUSTODIAL_CAPABILITIES);
  }

  async ensureTenantCustodialAccount(
    externalSubject: string,
    idempotencyKey: string
  ): Promise<Result<TenantCustodialAccountStatus, SdkError>> {
    assertNonEmpty(externalSubject, "externalSubject");
    assertNonEmpty(idempotencyKey, "idempotencyKey");
    return this.request(
      "PUT",
      API_PATHS.TENANT_CUSTODIAL_ACCOUNT(externalSubject),
      undefined,
      false,
      { "Idempotency-Key": idempotencyKey }
    );
  }

  async getTenantCustodialAccount(
    externalSubject: string
  ): Promise<Result<TenantCustodialAccountStatus, SdkError>> {
    assertNonEmpty(externalSubject, "externalSubject");
    return this.request("GET", API_PATHS.TENANT_CUSTODIAL_ACCOUNT(externalSubject));
  }

  /** Ensure/resume the active KYC attempt using one stable per-subject key. */
  async beginTenantKyc(
    externalSubject: string,
    idempotencyKey: string
  ): Promise<Result<TenantKycSession, SdkError>> {
    assertNonEmpty(externalSubject, "externalSubject");
    assertNonEmpty(idempotencyKey, "idempotencyKey");
    return this.request(
      "POST",
      API_PATHS.TENANT_CUSTODIAL_KYC_SESSION(externalSubject),
      undefined,
      false,
      { "Idempotency-Key": idempotencyKey }
    );
  }

  async submitTenantKyc(
    externalSubject: string,
    documents: TenantKycDocumentReference[]
  ): Promise<Result<TenantKycSubmission, SdkError>> {
    assertNonEmpty(externalSubject, "externalSubject");
    return this.request(
      "POST",
      API_PATHS.TENANT_CUSTODIAL_KYC_SUBMISSIONS(externalSubject),
      { documents }
    );
  }

  // ─── User ───

  async login(email: string, password: string): Promise<Result<string, SdkError>> {
    // .NET returns AZOAResult<string> where Result is the JWT token
    return this.request("POST", "/api/user/login", { email, password });
  }

  async register(params: {
    email: string;
    password: string;
    username: string;
    title?: string;
    firstName?: string;
    lastName?: string;
  }): Promise<Result<UserResponse, SdkError>> {
    // .NET returns AZOAResult<IUser>
    return this.request("POST", "/api/user/register", params);
  }

  /**
   * Server-side "logout everywhere": invalidates every live JWT for the
   * authenticated user (bumps AuthNotBefore server-side). The subject is
   * taken from the bearer token, never a URL/body param.
   */
  async logoutEverywhere(): Promise<Result<boolean, SdkError>> {
    return this.request("POST", API_PATHS.USER_LOGOUT);
  }

  async getUser(userId: string): Promise<Result<UserResponse, SdkError>> {
    assertUuid(userId, "userId");
    return this.request("GET", `/api/user/${userId}`);
  }

  async getAllUsers(): Promise<Result<UserResponse[], SdkError>> {
    return this.request("GET", "/api/user");
  }

  async updateUser(
    userId: string,
    params: { username?: string; email?: string; title?: string; firstName?: string; lastName?: string; isActive?: boolean }
  ): Promise<Result<UserResponse, SdkError>> {
    assertUuid(userId, "userId");
    return this.request("PUT", `/api/user/${userId}`, params);
  }

  async deleteUser(userId: string): Promise<Result<{ message: string }, SdkError>> {
    assertUuid(userId, "userId");
    return this.request("DELETE", `/api/user/${userId}`);
  }

  /**
   * user-dapp-rbac: assign the target user's DApp role. The target is the
   * route id (uuid-asserted). `role` is validated against the {@link DAPP_ROLES}
   * allowlist client-side; `operator:admin` is not assignable here. Authority is
   * enforced server-side (operator may grant any role incl. manager; a
   * dapp:manager may grant only developer/user; otherwise 403). Maps to
   * `PUT /api/user/{id}/dapp-role`, returns the updated user.
   */
  async assignDappRole(userId: string, role: DappRole): Promise<Result<UserResponse, SdkError>> {
    assertUuid(userId, "userId");
    const path = API_PATHS.USER_DAPP_ROLE(userId);
    if (!(DAPP_ROLES as readonly string[]).includes(role)) {
      return err(
        new SdkError(
          SdkErrorCode.INVALID_INPUT,
          `assignDappRole PUT ${path}: invalid role "${role}" — expected one of ${DAPP_ROLES.join(", ")}`
        )
      );
    }
    return this.request("PUT", path, { role });
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

  async mintNft(params: NftMintParams): Promise<Result<BlockchainOperationResult, SdkError>> {
    // POST /api/nft/mint with NftMintRequest body
    return this.request<BlockchainOperationResult>("POST", "/api/nft/mint", params);
  }

  /**
   * One-shot fungible token (ASA) mint — the direct (no-DAG) parallel to the
   * `FungibleTokenCreate` action node. Maps to `POST /api/nft/fungible-mint`
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

  async transferNft(nftId: string, params: NftTransferParams): Promise<Result<BlockchainOperationResult, SdkError>> {
    assertUuid(nftId, "nftId");
    // POST /api/nft/{id}/transfer with NftTransferRequest body
    return this.request<BlockchainOperationResult>("POST", `/api/nft/${nftId}/transfer`, params);
  }

  async burnNft(nftId: string, params: NftBurnParams): Promise<Result<BlockchainOperationResult, SdkError>> {
    assertUuid(nftId, "nftId");
    // POST /api/nft/{id}/burn with NftBurnRequest body
    return this.request<BlockchainOperationResult>("POST", `/api/nft/${nftId}/burn`, params);
  }

  async getNftMetadata(nftId: string): Promise<Result<NftMetadata, SdkError>> {
    assertUuid(nftId, "nftId");
    return this.request("GET", `/api/nft/${nftId}/metadata`);
  }

  // ─── Credential (CredentialController, D19) ───

  /**
   * Revoke a soulbound credential (issuer-only ASA clawback). Maps to
   * `POST /api/credential/{assetId}/revoke`. KYC-gated, scope-gated
   * (`credential:revoke:sign`) and idempotent — `options.idempotencyKey` sets the
   * `Idempotency-Key` header; a duplicate call replays the ORIGINAL result with
   * `replayed: true`.
   *
   * NOT yet enabled server-side (credential-third-party-issuance_20260906); this
   * method exists for parity with the backend surface.
   *
   * A PendingConfirmation outcome (HTTP 202, broadcast but unconfirmed) is NEVER
   * returned as `ok` — it surfaces as an `SdkError` with `status: 202`. Detect it
   * with {@link isCredentialRevocationPending} and reconcile rather than re-submit.
   */
  async revokeCredential(
    assetId: string,
    params: CredentialRevokeParams,
    options?: { idempotencyKey?: string }
  ): Promise<Result<CredentialRevokeResult, SdkError>> {
    assertNonEmpty(assetId, "assetId");
    const extraHeaders = options?.idempotencyKey
      ? { "Idempotency-Key": options.idempotencyKey }
      : undefined;
    return this.request<CredentialRevokeResult>(
      "POST",
      API_PATHS.CREDENTIAL_REVOKE(assetId),
      params,
      false,
      extraHeaders
    );
  }

  // ─── Analytics (AnalyticsController, D16) ───

  /** One page of the caller's operation history, newest first. Unpriced / unknown-quantity rows stay flagged — never coerce to 0. */
  async getAnalyticsTransactions(query?: AnalyticsQueryParams): Promise<Result<TransactionHistoryPage, SdkError>> {
    return this.request<TransactionHistoryPage>("GET", withAnalyticsQuery(API_PATHS.ANALYTICS_TRANSACTIONS, query));
  }

  /** Net movement per (chain, asset) over the window. `isComplete: false` means a truncated scan. */
  async getAnalyticsPositions(query?: AnalyticsQueryParams): Promise<Result<PositionsResponse, SdkError>> {
    return this.request<PositionsResponse>("GET", withAnalyticsQuery(API_PATHS.ANALYTICS_POSITIONS, query));
  }

  // ─── Bridge ───
  // BridgeController returns bare objects, not AZOAResult<T>

  async getBridgeRoutes(): Promise<Result<BridgeRouteInfo[], SdkError>> {
    // GET /api/bridge/routes — returns bare array, no AZOAResult wrapper
    return this.requestBare("GET", "/api/bridge/routes");
  }

  async initiateBridge(params: BridgeInitiateParams): Promise<Result<BridgeTransactionResult, SdkError>> {
    assertPositiveUlongDecimal(params.amount, "amount");
    return this.requestBare("POST", "/api/bridge/initiate", params);
  }

  async getBridgeStatus(bridgeId: string): Promise<Result<BridgeTransactionResult, SdkError>> {
    assertUuid(bridgeId, "bridgeId");
    return this.requestBare("GET", `/api/bridge/${bridgeId}`);
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

  async getBlockchainOperationsByUser(userId: string): Promise<Result<BlockchainOperationResult[], SdkError>> {
    assertUuid(userId, "userId");
    return this.request("GET", `/api/blockchainoperation/user/${userId}`);
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

  // ─── Allocation (Fiat Stripe Bridge) ───

  /**
   * Executes an idempotent fiat-allocation bridge call (Mint or Transfer).
   *
   * @param userId The target user receiving the asset
   * @param request The allocation details (amount, chain, kind, etc.)
   * @param idempotencyKey The stable per-payment key (e.g. Stripe PaymentIntent ID). If omitted, a deterministic key is generated based on the request content.
   */
  async allocate(
    userId: string,
    request: AllocationRequest,
    idempotencyKey?: string
  ): Promise<Result<AllocationResult, SdkError>> {
    const headers: Record<string, string> = {};
    if (idempotencyKey) {
      headers["Idempotency-Key"] = idempotencyKey;
    }
    return this.request<AllocationResult>(
      "POST",
      `/api/allocation/${userId}`,
      request,
      false,
      headers
    );
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

  // ─── UserNFT ───

  async mintUserNFT(params: UserNFTMintParams): Promise<Result<UserNFTResult, SdkError>> {
    return this.request("POST", "/api/usernft/mint", params);
  }

  async getUserNFT(id: string): Promise<Result<UserNFTResult, SdkError>> {
    assertUuid(id, "userNFTId");
    return this.request("GET", `/api/usernft/${id}`);
  }

  async getUserNFTByToken(chainType: string, contractAddress: string, tokenId: string): Promise<Result<UserNFTResult, SdkError>> {
    return this.request("GET", `/api/usernft/by-token/${encodeURIComponent(chainType)}/${encodeURIComponent(contractAddress)}/${encodeURIComponent(tokenId)}`);
  }

  async listUserNFTs(userId: string): Promise<Result<UserNFTResult[], SdkError>> {
    assertUuid(userId, "userId");
    return this.request("GET", `/api/usernft/user/${userId}`);
  }

  async transferUserNFT(id: string, recipientAddress: string): Promise<Result<UserNFTResult, SdkError>> {
    assertUuid(id, "userNFTId");
    return this.request("POST", `/api/usernft/${id}/transfer`, { recipientAddress });
  }

  async burnUserNFT(id: string): Promise<Result<{ message: string }, SdkError>> {
    assertUuid(id, "userNFTId");
    return this.request("DELETE", `/api/usernft/${id}`);
  }

  async bindHolonToNFT(userNFTId: string, holonId: string, params: { role: string; permissionLevel: string; permissions?: Record<string, string> }): Promise<Result<HolonNFTBindingResult, SdkError>> {
    assertUuid(userNFTId, "userNFTId");
    assertUuid(holonId, "holonId");
    return this.request("POST", `/api/usernft/${userNFTId}/holons/${holonId}/bind`, params);
  }

  async listNFTHolonBindings(userNFTId: string): Promise<Result<HolonNFTBindingResult[], SdkError>> {
    assertUuid(userNFTId, "userNFTId");
    return this.request("GET", `/api/usernft/${userNFTId}/holons`);
  }

  async updateHolonBinding(bindingId: string, params: { role?: string; permissionLevel?: string; permissions?: Record<string, string>; isActive?: boolean }): Promise<Result<HolonNFTBindingResult, SdkError>> {
    assertUuid(bindingId, "bindingId");
    return this.request("PUT", `/api/usernft/holons/${bindingId}`, params);
  }

  async removeHolonBinding(bindingId: string): Promise<Result<{ message: string }, SdkError>> {
    assertUuid(bindingId, "bindingId");
    return this.request("DELETE", `/api/usernft/holons/${bindingId}`);
  }

  async bindWalletToNFT(userNFTId: string, walletId: string, params: { bindingType: string; accessLevel: string; accessPermissions?: Record<string, string> }): Promise<Result<WalletNFTBindingResult, SdkError>> {
    assertUuid(userNFTId, "userNFTId");
    assertUuid(walletId, "walletId");
    return this.request("POST", `/api/usernft/${userNFTId}/wallets/${walletId}/bind`, params);
  }

  async listNFTWalletBindings(userNFTId: string): Promise<Result<WalletNFTBindingResult[], SdkError>> {
    assertUuid(userNFTId, "userNFTId");
    return this.request("GET", `/api/usernft/${userNFTId}/wallets`);
  }

  async updateWalletBinding(bindingId: string, params: { bindingType?: string; accessLevel?: string; accessPermissions?: Record<string, string>; isActive?: boolean }): Promise<Result<WalletNFTBindingResult, SdkError>> {
    assertUuid(bindingId, "bindingId");
    return this.request("PUT", `/api/usernft/wallets/${bindingId}`, params);
  }

  async removeWalletBinding(bindingId: string): Promise<Result<{ message: string }, SdkError>> {
    assertUuid(bindingId, "bindingId");
    return this.request("DELETE", `/api/usernft/wallets/${bindingId}`);
  }

  async getUserNFTComposite(userNFTId: string): Promise<Result<UserNFTResult, SdkError>> {
    assertUuid(userNFTId, "userNFTId");
    return this.request("GET", `/api/usernft/${userNFTId}/composite`);
  }

  async listUserNFTComposites(userId: string): Promise<Result<UserNFTResult[], SdkError>> {
    assertUuid(userId, "userId");
    return this.request("GET", `/api/usernft/user/${userId}/composite`);
  }

  async verifyNFTOwnership(params: { chainType: string; nftContractAddress: string; tokenId: string }): Promise<Result<{ isOwner: boolean }, SdkError>> {
    return this.request("POST", "/api/usernft/verify-ownership", params);
  }

  async verifyHolonAccess(params: { userNFTId: string; holonId: string; requiredPermission?: string }): Promise<Result<{ hasAccess: boolean }, SdkError>> {
    return this.request("POST", "/api/usernft/verify-holon-access", params);
  }

  async verifyWalletAccess(params: { userNFTId: string; walletId: string; requiredAccess?: string }): Promise<Result<{ hasAccess: boolean }, SdkError>> {
    return this.request("POST", "/api/usernft/verify-wallet-access", params);
  }

  // ─── Action DAG Operations ───

  /** Create a new action with a DAG of nodes and edges. */
  async createAction(params: ActionCreateParams): Promise<Result<ActionResult, SdkError>> {
    return this.request("POST", "/api/action", params);
  }

  /** Get an action by ID, including all nodes, edges, and dependencies. */
  async getAction(actionId: string): Promise<Result<ActionResult, SdkError>> {
    assertUuid(actionId, "actionId");
    return this.request("GET", `/api/action/${actionId}`);
  }

  /** List all actions belonging to a user. */
  async listActionsByUser(userId: string): Promise<Result<ActionResult[], SdkError>> {
    assertUuid(userId, "userId");
    return this.request("GET", `/api/action/user/${userId}`);
  }

  /**
   * Marketplace browse: list every public + Active action any authenticated user
   * may fork/start (maps to `GET /api/action/public`, ActionController.ListPublic). A
   * non-owner starts one via {@link executeAction}; the run is owned by the caller and
   * carries provenance back to the origin action ({@link ActionRunResult.originUserId}).
   */
  async listPublicActions(): Promise<Result<ActionResult[], SdkError>> {
    return this.request("GET", API_PATHS.ACTION_PUBLIC);
  }

  /** Update action metadata or status. */
  async updateAction(actionId: string, params: ActionUpdateParams): Promise<Result<ActionResult, SdkError>> {
    assertUuid(actionId, "actionId");
    return this.request("PUT", `/api/action/${actionId}`, params);
  }

  /** Delete an action and all its nodes/edges. */
  async deleteAction(actionId: string): Promise<Result<{ message: string }, SdkError>> {
    assertUuid(actionId, "actionId");
    return this.request("DELETE", `/api/action/${actionId}`);
  }

  /** Validate action DAG structure (checks for cycles, unreachable nodes, etc.). */
  async validateActionDag(actionId: string): Promise<Result<boolean, SdkError>> {
    assertUuid(actionId, "actionId");
    return this.request("POST", `/api/action/${actionId}/validate`);
  }

  /**
   * Run the full validation stack and flip the action from Draft to Active.
   * Returns validation errors on failure (400); action stays Draft. Also fails
   * (400, message containing "Publish conflict") when a concurrent
   * publish/unpublish raced this call and won the optimistic-concurrency check
   * on {@link ActionResult.version} (final-hardening F6) — use {@link isActionConflict}
   * to detect this case and prompt the caller to reload + retry.
   * See Managers/AGENTS.md §publish-lifecycle.
   */
  async publishAction(actionId: string): Promise<Result<ActionResult, SdkError>> {
    assertUuid(actionId, "actionId");
    return this.request("POST", API_PATHS.ACTION_PUBLISH(actionId));
  }

  /**
   * Flip an Active action back to Draft so its definition can be mutated.
   * Returns an error if any in-flight runs exist for this action, or (message
   * containing "Unpublish conflict") if a concurrent transition raced and won
   * the version check (final-hardening F6) — see {@link isActionConflict}.
   */
  async unpublishAction(actionId: string): Promise<Result<ActionResult, SdkError>> {
    assertUuid(actionId, "actionId");
    return this.request("POST", API_PATHS.ACTION_UNPUBLISH(actionId));
  }

  /**
   * Execute all ready nodes in the action DAG. Returns the produced
   * {@link ActionRunResult} (one execution attempt) — NOT the action
   * definition. Fails (400, message containing "Action run conflict") if the
   * action was unpublished or modified concurrently (final-hardening F6) —
   * see {@link isActionConflict}. A non-owner may call this on a
   * `isPublic` action (action-marketplace-provenance); the resulting run is
   * owned by the caller and carries {@link ActionRunResult.sourceActionId} /
   * {@link ActionRunResult.originUserId} back to the action owner.
   */
  async executeAction(actionId: string): Promise<Result<ActionRunResult, SdkError>> {
    assertUuid(actionId, "actionId");
    return this.request("POST", `/api/action/${actionId}/execute`);
  }

  /** Execute a single node within an action. */
  async executeActionNode(actionId: string, nodeId: string): Promise<Result<ActionNodeResult, SdkError>> {
    assertUuid(actionId, "actionId");
    assertUuid(nodeId, "nodeId");
    return this.request("POST", `/api/action/${actionId}/nodes/${nodeId}/execute`);
  }

  // ─── Action Invitations + Request/Approval (action-invitations-approval) ───

  /**
   * Owner-only: set the run-authorization mode (`Open` ↔ `InviteOnly`) and
   * optionally seed the invite list. Orthogonal to marketplace visibility
   * ({@link ActionResult.isPublic}) — an `InviteOnly` action stays viewable but
   * runs/forks only for owner + invited users.
   */
  async setActionRunAccess(actionId: string, body: SetRunAccessRequest): Promise<Result<ActionResult, SdkError>> {
    assertUuid(actionId, "actionId");
    if (body.invitedUserIds) {
      for (const id of body.invitedUserIds) assertUuid(id, "invitedUserId");
    }
    return this.request("PUT", API_PATHS.ACTION_RUN_ACCESS(actionId), body);
  }

  /** Owner-only: directly invite a user to run an `InviteOnly` action (no request needed). */
  async inviteUser(actionId: string, userId: string): Promise<Result<ActionResult, SdkError>> {
    assertUuid(actionId, "actionId");
    assertUuid(userId, "userId");
    return this.request("POST", API_PATHS.ACTION_INVITE(actionId), { userId } as InviteUserRequest);
  }

  /** Owner-only: revoke a user's invitation. In-flight runs are unaffected; new starts are gated. */
  async revokeInvite(actionId: string, userId: string): Promise<Result<ActionResult, SdkError>> {
    assertUuid(actionId, "actionId");
    assertUuid(userId, "userId");
    return this.request("DELETE", API_PATHS.ACTION_INVITE_REVOKE(actionId, userId));
  }

  /**
   * Open (or return the existing) Pending access request for an `InviteOnly`
   * action the caller can view. Idempotent per (action, requester): re-requesting
   * while Pending returns the same request; re-requesting after a terminal state
   * opens a fresh Pending. Rejected server-side for owner / already-invited.
   */
  async requestActionAccess(actionId: string, message?: string): Promise<Result<ActionAccessRequest, SdkError>> {
    assertUuid(actionId, "actionId");
    return this.request("POST", API_PATHS.ACTION_ACCESS_REQUESTS(actionId), { message } as RequestAccessBody);
  }

  /** Owner-only: list the approval queue for an action, optionally filtered by status. */
  async listAccessRequests(actionId: string, status?: ActionAccessRequestStatus): Promise<Result<ActionAccessRequest[], SdkError>> {
    assertUuid(actionId, "actionId");
    const query = status ? `?status=${encodeURIComponent(status)}` : "";
    return this.request("GET", `${API_PATHS.ACTION_ACCESS_REQUESTS(actionId)}${query}`);
  }

  /**
   * Owner-only: approve (append requester to the invited set) or reject a
   * Pending request. Scoped by the request's action owner; terminal requests are
   * immutable.
   */
  async decideAccessRequest(requestId: string, approve: boolean, reason?: string): Promise<Result<ActionAccessRequest, SdkError>> {
    assertUuid(requestId, "requestId");
    return this.request("POST", API_PATHS.ACTION_ACCESS_REQUEST_DECISION(requestId), { approve, reason } as DecideAccessRequestBody);
  }

  /** Requester-only: withdraw the caller's own Pending request. */
  async withdrawAccessRequest(requestId: string): Promise<Result<ActionAccessRequest, SdkError>> {
    assertUuid(requestId, "requestId");
    return this.request("POST", API_PATHS.ACTION_ACCESS_REQUEST_WITHDRAW(requestId));
  }

  /** Requester-only: list the caller's own outbound access requests, optionally filtered by status. */
  async listMyAccessRequests(status?: ActionAccessRequestStatus): Promise<Result<ActionAccessRequest[], SdkError>> {
    const query = status ? `?status=${encodeURIComponent(status)}` : "";
    return this.request("GET", `${API_PATHS.ACTION_ACCESS_REQUESTS_MINE}${query}`);
  }

  // ─── Action Templates ───

  /** Create a reusable action template. */
  async createActionTemplate(params: ActionTemplateCreateParams): Promise<Result<ActionTemplateResult, SdkError>> {
    return this.request("POST", "/api/action/templates", params);
  }

  /** Get an action template by ID. */
  async getActionTemplate(templateId: string): Promise<Result<ActionTemplateResult, SdkError>> {
    assertUuid(templateId, "templateId");
    return this.request("GET", `/api/action/templates/${templateId}`);
  }

  /** List all available action templates. */
  async listActionTemplates(): Promise<Result<ActionTemplateResult[], SdkError>> {
    return this.request("GET", "/api/action/templates");
  }

  /** Instantiate an action from a template with optional parameter overrides. */
  async instantiateActionTemplate(templateId: string, parameters?: Record<string, string>): Promise<Result<ActionResult, SdkError>> {
    assertUuid(templateId, "templateId");
    return this.request("POST", `/api/action/templates/${templateId}/instantiate`, parameters);
  }

  // ─── Action Node Templates ───

  /** Create a reusable node template. */
  async createActionNodeTemplate(params: ActionNodeTemplateCreateParams): Promise<Result<ActionNodeTemplateResult, SdkError>> {
    return this.request("POST", "/api/action/node-templates", params);
  }

  /** List all available node templates. */
  async listActionNodeTemplates(): Promise<Result<ActionNodeTemplateResult[], SdkError>> {
    return this.request("GET", "/api/action/node-templates");
  }

  // ─── API Key Management ───

  /** Create a new API key. The raw key is returned ONCE — store it securely. */
  async createApiKey(params: ApiKeyCreateParams): Promise<Result<ApiKeyCreateResult, SdkError>> {
    return this.request("POST", "/api/apikey", params);
  }

  /** List all API keys for the authenticated user (raw keys are never returned). */
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
   * List the scopes a user may self-issue on a new key, each with a human
   * description (maps to `GET /api/apikey/scopes`). Drives the key-creation scope
   * picker. An empty CSV still means legacy "full access" — these are the opt-in scopes.
   */
  async listSelfIssuableApiKeyScopes(): Promise<Result<SelfIssuableApiKeyScopeInfo[], SdkError>> {
    return this.request("GET", API_PATHS.APIKEY_SCOPES);
  }

  /** Backwards-compatible alias for {@link listSelfIssuableApiKeyScopes}. */
  async listIssuableScopes(): Promise<Result<SelfIssuableApiKeyScopeInfo[], SdkError>> {
    return this.listSelfIssuableApiKeyScopes();
  }

  /**
   * Rotate an API key: mints a NEW key inheriting the old key's name, scopes, and
   * expiry, revokes the old key, and returns the new raw key ONCE (maps to
   * `POST /api/apikey/{id}/rotate`). Scoped to the caller's own key.
   */
  async rotateApiKey(keyId: string): Promise<Result<ApiKeyCreateResult, SdkError>> {
    assertUuid(keyId, "keyId");
    return this.request("POST", API_PATHS.APIKEY_ROTATE(keyId));
  }

  // ─── DappSeries (DappSeriesController) ───
  // Reads are authenticated-user reads; authoring writes require dapp:develop.

  /** List the caller's dApp-series, optionally filtered by status. */
  async listDappSeries(status?: DappSeriesStatus): Promise<Result<DappSeriesResult[], SdkError>> {
    const query = status ? `?status=${encodeURIComponent(status)}` : "";
    return this.request("GET", `${API_PATHS.DAPP_SERIES_LIST}${query}`);
  }

  /** Get one dApp-series by id (owner-scoped). */
  async getDappSeries(id: string): Promise<Result<DappSeriesResult, SdkError>> {
    assertUuid(id, "dappSeriesId");
    return this.request("GET", API_PATHS.DAPP_SERIES_GET(id));
  }

  /** Create a new dApp-series. Requires the `dapp:develop` scope on a scoped key. */
  async createDappSeries(params: DappSeriesCreateParams): Promise<Result<DappSeriesResult, SdkError>> {
    return this.request("POST", API_PATHS.DAPP_SERIES_CREATE, params);
  }

  /** Update dApp-series metadata / shared config. Requires the `dapp:develop` scope. */
  async updateDappSeries(id: string, params: DappSeriesUpdateParams): Promise<Result<DappSeriesResult, SdkError>> {
    assertUuid(id, "dappSeriesId");
    return this.request("PUT", API_PATHS.DAPP_SERIES_UPDATE(id), params);
  }

  /** Delete a dApp-series. Requires the `dapp:develop` scope. */
  async deleteDappSeries(id: string): Promise<Result<{ message: string }, SdkError>> {
    assertUuid(id, "dappSeriesId");
    return this.request("DELETE", API_PATHS.DAPP_SERIES_DELETE(id));
  }

  /** List the ordered action entries in a dApp-series. */
  async listSeriesActions(seriesId: string): Promise<Result<DappSeriesActionResult[], SdkError>> {
    assertUuid(seriesId, "dappSeriesId");
    return this.request("GET", API_PATHS.DAPP_SERIES_ACTIONS(seriesId));
  }

  /** Add an action to a dApp-series at a given order. Requires the `dapp:develop` scope. */
  async addSeriesAction(seriesId: string, params: DappSeriesAddActionParams): Promise<Result<DappSeriesActionResult, SdkError>> {
    assertUuid(seriesId, "dappSeriesId");
    assertUuid(params.actionId, "actionId");
    return this.request("POST", API_PATHS.DAPP_SERIES_ACTIONS(seriesId), params);
  }

  /** Remove an action from a dApp-series. Requires the `dapp:develop` scope. */
  async removeSeriesAction(seriesId: string, actionId: string): Promise<Result<{ message: string }, SdkError>> {
    assertUuid(seriesId, "dappSeriesId");
    assertUuid(actionId, "actionId");
    return this.request("DELETE", API_PATHS.DAPP_SERIES_ACTION_REMOVE(seriesId, actionId));
  }

  /** Change an action's 1-indexed order within a series. Requires the `dapp:develop` scope. */
  async reorderSeriesAction(seriesId: string, actionId: string, newOrder: number): Promise<Result<DappSeriesActionResult, SdkError>> {
    assertUuid(seriesId, "dappSeriesId");
    assertUuid(actionId, "actionId");
    return this.request("PUT", API_PATHS.DAPP_SERIES_ACTION_ORDER(seriesId, actionId), { newOrder });
  }

  /** Update an action's cross-action input mappings (JSON array). Requires the `dapp:develop` scope. */
  async updateSeriesMappings(seriesId: string, actionId: string, inputMappings: string | null): Promise<Result<DappSeriesActionResult, SdkError>> {
    assertUuid(seriesId, "dappSeriesId");
    assertUuid(actionId, "actionId");
    return this.request("PUT", API_PATHS.DAPP_SERIES_ACTION_MAPPINGS(seriesId, actionId), { inputMappings });
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
