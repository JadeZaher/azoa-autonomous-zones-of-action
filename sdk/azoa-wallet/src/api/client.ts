import type { WebhookRegistrationCommand, WebhookRegistrationStatus, WebhookRegistrationMutationResult, WebhookRegistrationWithdrawal, WebhookEventStatus, WebhookEventRetryCommand, WebhookEventRetryReceipt, ConsentWebhookEventStatus } from "./webhooks.js";
import type { ConsentGrantCommand, ConsentGrantView, ConsentGrantReceipt, ConsentRevocationReceipt, ConsentOffboardReceipt, ConsentCommandWithdrawal } from "./consent.js";
import type { Result } from "../core/result.js";
import type { HoldLiabilityPage, HoldLiabilityView, HoldReserveAssetView, HoldStartCommand, HoldCommandView, HoldCommandPage, HoldConsentCommand, HoldRecoveryCommand, HoldOpeningAcceptance, HoldRequestLookup, HoldRequestResolution, HoldOpeningRecoveryLookup, HoldMandateOfferRequest, HoldMandateOffer, HoldMandateAcceptance, HoldMandateView, HoldMandatePage, HoldMandateResolution, ExchangeReleaseMandateRequest, ExchangeReleaseAcceptance, ExchangeReleaseDisclosure } from "./hold-commands.js";
import type { DexCapabilityView, NodeFeeScheduleUpdateRequest, DexQuoteRequest, DexQuoteAvailability, DexQuoteView, SwapAdmissionRequest, SwapOperationView, SwapVersionCommand, SwapStageCommand, SwapExposureCommand, SwapExternalSigningView, SwapBrowserStageCommand, SwapCleanupRequest, SwapCleanupStageCommand } from "./dex.js";
import type { WttQuoteRequest, WttOfferView, WttStartRequest, WttCancelRequest, WttOperationView, WttOperationPage, WttContinueCommand, WttStageCommand, WttParticipantSigningView, WttParticipantSignatureCommand, WttNativeOutputOffer, WttNativeOutputRequest, WttCleanupOffer, WttCleanupRequest } from "./wtt.js";
import type { KycSessionStart, KycSubmission, HostedSessionRefreshReceipt, WalletOwnershipChallenge, WalletOwnershipProof } from "./identity.js";
import type { FiatPayoutResolveRequest, FiatReversalResolveRequest, TenantPayoutBalance, TenantPayoutTopUpParams, TenantPayoutTopUp, TenantPayoutFloatView } from "./money.js";
import type { ClientSigningPlanView, ClientSignedSubmission, StuckClientSignedOperationView, ClientSigningResolution } from "./operations.js";
import type { DeploymentOperatorResolution, ParkedDeployment, RetiredExchangeRowView, RetiredExchangeResolution, WalletIdentityMigrationView, WalletIdentityConflictRepair, SwapOperatorSettlementRequest, WttStageKeyReadinessReport, WttStageKeyRewrapReport } from "./operator.js";
import { ok, err } from "../core/result.js";
import { SdkError, SdkErrorCode } from "../core/errors.js";
import type { SdkErrorDetail } from "../core/errors.js";
import { API_PATHS } from "./api-version.js";
import type { ContractQueryRequest, DappCompositionManifest, DappCompositionValidation, DeploymentArtifactView, DeploymentEcosystemAttachment, DeploymentEcosystemTree, DeploymentGenerateRequest, DeploymentInvokeRequest, DeploymentPage, DeploymentQuoteRequest, DeploymentReceipt, DeploymentStartRequest, NativeOperationIntent, DeploymentOriginalBudgetRecoveryRequest, DeploymentRecoveryQuoteRequest, DeploymentRecoveryQuote, DeploymentRecoveryStartRequest, DeploymentRecoveryAncestry } from "./deployments.js";
import type { WorkflowRunStatus, WorkflowNodeExecution, WorkflowExecutionState, StartWorkflowOptions, WorkflowReconciliationSweep } from "../workflow/types.js";
import { workflowStartQuery } from "../workflow/start-options.js";

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

export interface ActionRecoveryView {
  executionId: string;
  runId: string;
  nodeId: string;
  version: number;
  state: string;
  recoveryState?: string | null;
  domain?: string | null;
  operationId?: string | null;
  intentHash?: string | null;
  canRetry: boolean;
}

export interface ActionRecoveryRequest {
  requestId: string;
  expectedVersion: number;
  intentHash: string;
  reason: string;
}

export interface ActionRecoveryReceipt {
  auditId: string;
  executionId: string;
  expectedVersion: number;
  requestedAt: string;
}

export interface ActionRecoveryAudit {
  id: string;
  operatorId: string;
  executionId: string;
  runId: string;
  nodeId: string;
  expectedVersion: number;
  intentHash: string;
  reason: string;
  action: string;
  createdAt: string;
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
  code?: string;
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

export interface BusinessVerificationCapabilities {
  providerKey: string;
  flowKind: "kyb";
  available: boolean;
  missingConfigurationKeys: string[];
  unavailableReason?: string | null;
}

export interface BusinessVerificationStatus {
  attemptId: string;
  status: "PENDING" | "APPROVED" | "REJECTED" | "EXPIRED" | "NEEDS_OPERATOR";
  verified: boolean;
  expiresAt: string;
  reviewedAt?: string | null;
  beneficiaryCount: number;
}

export interface OnRampLotView {
  id: string;
  intentId: string;
  status: string;
  statusReason: string | null;
  chargedAndUncredited: boolean;
  needsAPersonToMoveIt: boolean;
  fiatMinorUnits: number;
  currencyCode: string;
  createdAt: string;
  providerRefundId: string | null;
  refundStartedAt: string | null;
  refundAttempt: number;
  providerRefundStatus: string | null;
}

export interface FiatEventView {
  id: string;
  providerEventId: string;
  eventType: string;
  status: string;
  providerWasToldToStopRetrying: boolean;
  attemptCount: number;
  lastError: string | null;
}

export interface HostedSessionRecoveryView {
  sessionId: string;
  flowKind: string;
  providerKey: string;
  subjectUserId: string;
  tenantId?: string;
  status: string;
  createdDate: string;
}

export interface HostedSessionRecoveryRequest {
  reason: string;
  operatorReviewedSubjectAndProvider: true;
  confirmedProviderAbsence: boolean;
  providerSessionId?: string;
  redirectUrl?: string;
}

export type WalletAuthenticationChain = "algorand" | "ethereum" | "solana";
export interface WalletAuthenticationChallenge {
  address: string;
  chainType: WalletAuthenticationChain;
  nonce: string;
  message: string;
  expiresAt: string;
}
export interface WalletAuthenticationProof {
  address: string;
  chainType: WalletAuthenticationChain;
  signature: string;
  message: string;
}
export interface WalletAuthenticationToken { token: string; userId: string }
export type WalletOwnershipClaim = { targetUserId: string; claimToken: string } & (
  { newPassword: string; address?: never; chainType?: never; signature?: never; message?: never }
  | (WalletAuthenticationProof & { newPassword?: never })
);

export interface BusinessVerificationSession {
  verification: BusinessVerificationStatus;
  verificationUrl: string;
  resumed: boolean;
}

/**
 * Mirrors AZOA's `TenantKycStatus`. `UnderReview` means identity verified but an
 * additional compliance check still owes a human decision: neither approved nor
 * rejected, and closed to value until it resolves.
 */
export type TenantKycStatus =
  | "Unknown"
  | "Pending"
  | "Approved"
  | "Rejected"
  | "UnderReview";

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
  /** When an open compliance review is due a decision; null unless `UnderReview`. */
  reviewDueAt: string | null;
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
  protocolCostAuthorization?: NftProtocolCostAuthorization;
  feeAuthorization?: NftFeeAuthorization;
  walletId: string;
  name: string;
  description: string;
  chainId: string;
  metadataUri: string;
  imageUri?: string;
  externalUri?: string;
  metadata?: Record<string, string>;
}

export interface NftTransferParams {
  feeAuthorization?: NftFeeAuthorization;
  targetUserId: string;
  walletId: string;
  memo?: string;
}

/** Immutable descriptor admitted before cost consent and chain submission. */
export interface FungibleMintParams {
  chainType: string;
  network?: string;
  name: string;
  unitName: string;
  /** Canonical uint64 base-unit text; safe integer numbers are accepted for compatibility. */
  total: string | number;
  decimals: number;
  holonId?: string;
}

export interface FungibleCreationQuote {
  quoteId: string;
  intentHash: string;
  protocolIdentity: string;
  networkFeeBaseUnits: string;
  minimumBalanceBaseUnits: string;
  currency: string;
  decimals: number;
  payerAddress: string;
  expiresAt: string;
}

export interface FungibleCreationView {
  operationId: string;
  version: number;
  state: string;
  chainType: string;
  network: string;
  assetId?: string | null;
  transactionId?: string | null;
  detail?: string | null;
  quote?: FungibleCreationQuote | null;
  walletId: string;
  walletAddress: string;
  walletProvisioned: boolean;
  requestedHolonId?: string | null;
  purpose: 'Participant' | 'PlatformGenesis';
}

export interface PlatformGenesisRequest {
  chainType: string; name: string; unitName: string; total: string; decimals: number;
  expectedNetwork: string; confirmIrreversible: true;
}
export interface PlatformGenesisResponse {
  definition: PlatformTokenDefinition; created: boolean; state: string;
  operationsNote?: string | null; operation?: FungibleCreationView | null;
}

export interface FungibleContinuationRequest { requestId: string; version: number }
export interface FungibleAttachmentRequest extends FungibleContinuationRequest { projectionAuthorityId: string }
export interface FungibleAttachmentView {
  operationId: string; version: number; state: string; holonId: string; projectionAuthorityId: string;
  chainType: string; network: string; assetId: string; transactionId: string;
}

/** AssetId remains empty until authoritative issuance; operation is the recovery receipt. */
export interface FungibleTokenResult {
  userId: string;
  walletId: string;
  walletAddress: string;
  walletProvisioned: boolean;
  assetId: string;
  replayed: boolean;
  operation?: FungibleCreationView | null;
}

export interface NftBurnParams {
  feeAuthorization?: NftFeeAuthorization;
  walletId: string;
}

// ─── Credential types matching .NET DTOs (CredentialController, D19) ───

/** Mirrors .NET CredentialRevokeRequest — the body for `POST /api/credential/{assetId}/revoke`. */
export interface CredentialRevokeParams {
  feeAuthorization?: NftFeeAuthorization;
  /** Chain and historical network of the verified issuance. */
  chainType: string;
  network?: string;
  contractAddress?: string;
  /** Optional free-text reason recorded on the operation row. */
  reason?: string;
}

/** The OperationStatus constants a revocation can carry; only "Completed" is a confirmed clawback. */
export type CredentialRevokeStatus = "Completed" | "PendingConfirmation" | "Failed";

/** Credential revocation projection; inspect status/isConfirmed and reconcile lifecycleId while pending. */
export interface CredentialRevokeResult {
  lifecycleId?: string;
  lifecycleState?: NftLifecycleView["state"];
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

// ─── ActionGraphBundle types matching .NET DTOs ───

export interface ActionGraphBundleResult {
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

export interface ActionGraphBundleCreateParams {
  name: string;
  description: string;
  publicKey?: string;
  userId?: string;
}

export interface ActionGraphBundleDappGenerationParams {
  targetChain: string;
  boundHolonIds: string[];
  config?: Record<string, string>;
  generation: DeploymentGenerateRequest;
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
  protocolCostAuthorization?: NftProtocolCostAuthorization;
  feeAuthorization?: NftFeeAuthorization;
  chainType: string;
  walletId?: string;
  nftContractAddress?: string;
  tokenStandard?: string;
  metadataURI?: string;
  imageURI?: string;
  name: string;
  description?: string;
  attributes?: Record<string, string>;
  royaltyPercentage?: number;
  royaltyRecipient?: string;
  isSoulbound?: boolean;
  isTransferable?: boolean;
  holonBindings?: Record<string, string>;
  walletBindings?: Record<string, string>;
}

export type NftOperation = "Mint" | "Transfer" | "Burn" | "Revoke" | "Refund";
export interface NftFeeAuthorization { revision: number; maxNativeBaseUnits: string; quoteId: string }
export interface NftFeeQuote {
  quoteId: string;
  chainType: string; network: string; operation: NftOperation; revision: number;
  currency: string; decimals: number; nativeBaseUnits: string; treasuryAddress: string; payerUserId: string;
}
export interface NftProtocolCostLimits { networkFee: string; retainedRent: string; protocolLevy: string }
export interface NftProtocolCostAuthorization { quoteId: string; quoteHash: string; limits: NftProtocolCostLimits }
export interface NftProtocolCostQuote {
  quoteId: string; actorUserId: string; actingTenantId: string; payerWalletId: string; payerAddress: string;
  chainType: string; network: string; physicalNetworkIdentity: string; lifecycleId: string; draftHash: string;
  stage: string; preparationGeneration: number; tokenStandard: string; policyEvidenceJson: string;
  limits: NftProtocolCostLimits; createdAt: string; expiresAt: string;
  quotedIntentHash?: string | null;
}
export interface NftProtocolCostOffer { quote: NftProtocolCostQuote; authorization: NftProtocolCostAuthorization }
export interface NftWorkflowCostDisclosure { executionId: string; executionVersion: number; offer: NftProtocolCostOffer }
export interface NftWorkflowCostAcceptance {
  requestId: string; executionId: string; expectedExecutionVersion: number; authorization: NftProtocolCostAuthorization;
}
export interface NftWorkflowCostResolution { state: "Applied" | "Obsolete" | "Unresolved"; lifecycle?: NftLifecycleView | null }
export interface NftLifecyclePage { items: NftLifecycleView[]; nextCursor: string | null }
export interface EthereumPreparationView {
  id: string;
  network: string;
  sender: string;
  state: string;
  nonce: string;
  transactionId: string | null;
  preparationKey: string;
  updatedAt: string;
}
export interface EthereumRecoveryRequest { cancel: boolean; maximumFeePerGas: string; reason: string }
export interface NftLifecycleView {
  version: number;
  preparationGeneration: number;
  stageAcceptance?: NftStageAcceptance | null;
  needsStageAcceptance?: boolean;
  needsReplacementCosts?: boolean;
  replacementCostsAccepted?: boolean;
  /** Display hint: the original requester's direct authority lapsed and must be renewed before the next new signature. */
  needsActorContinuation?: boolean;
  /** Display hint: the accepting participant's stage credential lapsed; that participant re-accepts the same stage. */
  needsParticipantRenewal?: boolean;
  renewalParticipantUserId?: string | null;
  operation: NftOperation;
  feeQuote: NftFeeQuote | null;
  actorUserId: string;
  recipientUserId: string;
  operationId: string;
  nftId: string;
  state: "Preparing" | "Prepared" | "Submitted" | "NeedsOperator" | "AwaitingRecipientAcceptance"
    | "AwaitingCreatorDestruction" | "AwaitingRefundAuthorization" | "AwaitingRefundDestinationAcceptance" | "Confirmed" | "Completed" | "Cancelled" | "Failed";
  stage: string;
  assetId: string | null;
  transactionId: string | null;
  detail: string | null;
}

export interface NftStageAcceptance {
  requestId: string;
  expectedVersion: number;
  state: NftLifecycleView["state"];
  stage: string;
  generation: number;
}

export interface NftReplacementCostAcceptance {
  stage: NftStageAcceptance;
  costs: NftProtocolCostAuthorization;
}

export interface NftReplacementCostReconciliation {
  status: "Applied" | "Obsolete" | "Unresolved";
  lifecycle: NftLifecycleView;
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
  | "ActionGraphBundleCreate" | "ActionGraphBundleGenerate" | "ActionGraphBundleDeploy"
  | "SearchQuery"
  | "UserNftMint" | "UserNftTransfer" | "UserNftVerify"
  | "BlockchainExecute"
  | "Gate" | "Delay" | "Webhook" | "Script"
  // Value-rail primitives (mirrors backend ActionNodeType). Governance + CredentialRevoke are
  // NOT enabled server-side (governance-vote-of-record_20260906, credential-third-party-issuance_20260906).
  | "CredentialRevoke"
  | "GovernanceProposalCreate" | "GovernanceVote" | "GovernanceTally"
  | "LoanOpen" | "LoanRepaymentRecord" | "LoanBalance"
  // Hold rails (D14 / W3.4): funds set aside in the platform holding account.
  | "Hold" | "Release" | "HoldRefund" | "ReleaseIntoHold"
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
  | "token:create:sign"
  | "contract:deploy:sign"
  | "wtt:source:sign"
  | "wtt:target:sign"
  | "wtt:participant:sign"
  | "wtt:cleanup:sign"
  | "contract:call:sign"
  | "solana:account:sign";

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

/**
 * Mirrors AZOA's `AllocationKind` (D4/D47). `Mint` is retired for tenants — the
 * manager refuses it for any tenant-driven caller — and stays only so historical
 * receipts still deserialise. `IssueUnits` is the tenant-facing default: it draws
 * already-existing units from a named reserve partition instead of creating supply.
 */
export interface AllocationRequest {
  kind: "Mint" | "Transfer" | "IssueUnits";
  chainType: string;
  amount: string;
  name?: string;
  description?: string;
  assetId?: string;
  assetRecordId?: string;
  /**
   * The named reserve partition an `IssueUnits` draw comes from (e.g.
   * `"contributor"`, `"investor"`). Required on that path — the backend refuses
   * an `IssueUnits` request with no partition rather than defaulting one, so one
   * purpose can never quietly overdraw another's earmark.
   */
  partition?: string;
  /**
   * Why these units are being issued, in the caller's own words (e.g.
   * `"milestone-2 contributor stake"`). Recorded against the durable operation;
   * never interpreted.
   */
  purpose?: string;
  /**
   * The fee schedule version the caller quoted via `previewOnly`. When set and
   * the node's live schedule has moved on, the allocation is refused rather than
   * settling on terms the caller never agreed to.
   */
  expectedFeeScheduleVersion?: number;
  /**
   * Quote only: runs every readiness check and returns the exact gross/fee/net
   * and schedule version, performing NO allocation and claiming NO idempotency
   * key. Use this to show a caller the fee before it locks terms with a person.
   */
  previewOnly?: boolean;
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

// ─── Exchange types (ExchangeController, W5.1/D50) ───
// The sponsorship moment: a consumer that has already decided the amounts and
// the rate calls `exchange()`, the sponsor's funds reach the project on chain,
// and the sponsor's units are either theirs immediately or committed to a hold
// — all in one atomic group. Requires an API-key principal carrying the
// exchange-signing scope, exactly like `allocate()`.

/** Which way units move in a treasury-rate exchange. */
export type ExchangeDirection =
  | "Buy"
  /**
   * A holder gives project units back and receives the platform unit. Refused
   * until a buy-back liquidity policy is registered server-side.
   */
  | "Sell";

/**
 * The rate the CALLER decided, relayed verbatim. AZOA computes no exchange
 * rate of its own: it records the one its consumer declared, with that
 * consumer's own source, version and observation time, or it records nothing.
 * Exactly one of `majorUnitsPerWholeUnit` and `minorUnitsPerBaseUnit` must be
 * supplied, both as decimal strings so a price is never rounded through a
 * binary floating-point conversion.
 */
export interface DeclaredExchangeRate {
  /** ISO-4217 code the rate converts into. USD only today. */
  currencyCode?: string;
  /** Price of ONE WHOLE project unit in the currency's major unit. Needs `assetDecimals`. */
  majorUnitsPerWholeUnit?: string;
  /** Minor units of `currencyCode` per ONE BASE UNIT of the project asset. */
  minorUnitsPerBaseUnit?: string;
  /** The project asset's decimals, needed to convert `majorUnitsPerWholeUnit`. */
  assetDecimals?: number;
  /** Digits in the currency's minor unit — 2 for USD cents. Defaults to 2. */
  currencyMinorUnitDigits?: number;
  /** When the caller observed this rate — not when AZOA read or applied it. */
  declaredAt?: string;
  /** The caller's own identifier for where the rate came from (e.g. `"ardanova:treasury-rate"`). */
  source?: string;
  /** The caller's own version for the rate schedule it belongs to, when it versions one. */
  version?: string;
}

/**
 * One treasury-rate exchange, with every amount already decided by the
 * caller. AZOA executes the move; it does not price it. The person the
 * exchange is for is the `userId` route argument, never a field here.
 */
export interface ExchangeParams {
  /** The rail both legs settle on. They are one atomic group, so there is exactly one. */
  chainType: string;
  /** Which way units move. Defaults to `"Buy"` server-side. */
  direction?: ExchangeDirection;
  /** The caller's own project id. Names the project treasury the funds land in. */
  projectReference: string;
  /** The project's asset — the units the sponsor receives. */
  projectAssetId: string;
  /** Platform-unit amount, in base units, as a whole decimal string. The funds leg. */
  platformAmount: string;
  /** Project-unit amount, in base units, as a whole decimal string. The units leg. */
  projectUnitAmount: string;
  /**
   * Named partition on the reserve ledger the project units are drawn from.
   * Required: without it one purpose could quietly draw down another's earmark.
   */
  reservePartition: string;
  /** The rate this exchange was priced at. Required unless the node's policy says otherwise. */
  declaredRate?: DeclaredExchangeRate;
  /**
   * The hold window. When present the project units are COMMITTED to a hold
   * for the sponsor and become theirs at this instant; when absent they move
   * straight to them.
   */
  releaseNotBefore?: string;
  /** Optional caller-supplied hold id; derived from the exchange when absent. */
  holdReference?: string;
  /** The caller's own reason, carried onto the durable record. */
  purpose?: string;
  /**
   * Runs every gate and validation and then STOPS: no idempotency claim, no
   * partition draw, no movement, and an empty operation id as the
   * machine-checkable proof. Exempt from the consent check other exchanges
   * require, since nothing moves.
   */
  previewOnly?: boolean;
  /** Requested automatic release of a held purchase; a preview returns its `releaseDisclosure`. */
  releaseMandate?: ExchangeReleaseMandateRequest;
  /** Explicit acceptance of the previewed disclosure; required with `releaseMandate`. */
  releaseAcceptance?: ExchangeReleaseAcceptance;
}

/** Externally observable state of a caller-authorized exchange. */
export type ExchangeReceiptState = "AwaitingReconciliation" | "Completed" | "Failed";

/**
 * Secret-free, caller-scoped exchange receipt — what a consumer reconciles
 * against. Deliberately omits raw idempotency keys, caller identity, account
 * keys and provider payloads.
 */
export interface ExchangeReceipt {
  /** Opaque SHA-256 reference for this exchange. Safe to log and to echo back. */
  receiptReference: string;
  /** Opaque durable operation reference once AZOA has persisted one. */
  operationId: string | null;
  state: ExchangeReceiptState;
  isTerminal: boolean;
  requiresReconciliation: boolean;
  /** True when this answer replays an earlier identical request rather than a new exchange. */
  replayed: boolean;
  direction: ExchangeDirection;
  /** The person the exchange was for. */
  userId: string | null;
  /** The caller's own project id, echoed back. */
  projectReference: string | null;
  /** Base units the initiator gave up, and the asset they are counted in. */
  amountSpent: string | null;
  amountSpentAssetId: string | null;
  /** Base units the initiator received, and the asset they are counted in. */
  amountReceived: string | null;
  amountReceivedAssetId: string | null;
  /** Base units credited to the project's own account by this exchange. */
  projectCreditAmount: string | null;
  /** The rail's reference for the whole atomic group, when one settled. */
  settlementReference: string | null;
  /** The rail's per-leg references, in the order the group declared them. */
  settlementLegReferences: string[];
  /** The hold the received units are committed to, when the exchange opened one. */
  holdReference: string | null;
  /** When the held units become the recipient's to move. */
  holdReleasesAt: string | null;
  /** The declared rate that priced this exchange, and where it came from. */
  rate: string | null;
  rateCurrencyCode: string | null;
  rateSource: string | null;
  rateVersion: string | null;
  rateAsOf: string | null;
  /**
   * What the spent leg was worth in the rate currency's minor unit at
   * settlement, when a rate source priced it. Absent means never priced —
   * never zero.
   */
  valueMinorUnits: string | null;
  createdAt: string;
  updatedAt: string;
  /** Bounded terminal error discriminator; never provider detail. */
  failureCode: string | null;
  /** Durable exchange source this receipt reads; retain it for discovery after a lost response. */
  sourceId?: string | null;
  /** Server-authored automatic-release disclosure returned by a preview. */
  releaseDisclosure?: ExchangeReleaseDisclosure | null;
  /** Release permission created with a held purchase, separate from its settlement. */
  releaseMandateId?: string | null;
}

// ─── Hold commands and release mandates live in ./hold-commands.ts ───

// ─── Fiat on-ramp checkout types (FiatCheckoutController) ───
// The "add funds" surface: start a payment for a person's account and find that
// payment again when they come back. This controller never credits anything — a
// payment becomes funds only when the provider's webhook clears server-side.

/**
 * Consumer-facing lifecycle words for one funding intent (mirrors
 * `FiatIntentStatuses`). Never rail vocabulary — a caller never sees a Stripe
 * session state directly.
 */
export type FiatCheckoutStatus =
  | "starting"
  | "awaiting_payment"
  | "processing"
  | "complete"
  | "failed"
  | "expired";

/**
 * How far a funding intent has got, in the vocabulary of the person who paid
 * (mirrors `FiatSettlementStates`). `payment_received` means the card cleared but
 * the funds have not yet landed in the account; only `funds_applied` means the
 * account balance actually moved.
 */
export type FiatSettlementState =
  | "not_started"
  | "payment_received"
  | "funds_applied"
  | "payment_failed";

/** Allowlisted routing value for a checkout (mirrors `FiatCheckoutPurposes`). */
export type FiatCheckoutPurpose = "funds-top-up";

/** Non-secret capability report for a hosted-session provider (KYC or fiat checkout). */
export interface HostedSessionCapabilities {
  providerKey: string;
  flowKind: string;
  available: boolean;
  /** Configuration key NAMES that are missing or invalid. Never values. */
  missingConfigurationKeys: string[];
  unavailableReason: string | null;
}

export interface FiatCheckoutStartParams {
  /**
   * Caller-stable key. The same key resumes the same payment instead of opening
   * a second one, so a double-tapped button cannot charge twice.
   */
  requestKey: string;
  /** Allowlisted routing value; see `Fiat:Checkout:AllowedPurposes` server-side. */
  purpose?: FiatCheckoutPurpose | string;
  /** The amount to pay, in minor units of the node's configured currency. Never a unit count. */
  amountMinorUnits: number;
  /** Where to return the payer afterwards. Must be on an allowlisted origin. */
  successUrl?: string;
  /** Where to return the payer if they back out. Must be on an allowlisted origin. */
  cancelUrl?: string;
}

/** Everything the return page needs to recognise the intent it came back to. */
export interface FiatCheckoutIntent {
  /** The node's own intent id. Safe to show; it is NOT the payment provider's id. */
  intentId: string;
  admissionKey: string;
  status: FiatCheckoutStatus;
  settlementState: FiatSettlementState;
  /** The hosted payment page, while the intent is still open. */
  paymentUrl: string | null;
  /** True when this call resumed an existing intent instead of creating one. */
  resumed: boolean;
  /** The amount that actually cleared, re-read from the provider. Null until it has. */
  amountPaidMinorUnits: number | null;
  currency: string | null;
  expiresAt: string | null;
}

// ─── Fiat off-ramp / cash-out (FiatPayoutController) ───
// The "cash out" surface: set up where a person's money should go, see what
// they can take out, and take it out. A first-party login may only cash out
// for itself; an API-key principal additionally needs the subject's live
// consent, the same gate `startFiatCheckout` applies. Nothing here ever moves
// value inline — a request claims a durable cash-out and hands it to the
// backend's saga, so the read surface reports a status, never a receipt.

/**
 * What a person may cash out right now, in money, plus why the rest may not
 * leave yet.
 */
export interface CashOutQuote {
  /** Whether this node offers cashing out at all. */
  available: boolean;
  /** Whether their payout account can actually receive money. */
  accountReady: boolean;
  /** The most they could ask for today, in minor units of `currencyCode`. */
  cashableMinorUnits: number;
  /** Money-equivalent of what is still inside a reversal window. */
  onHoldMinorUnits: number;
  /** When the oldest held part becomes cashable, when that is knowable. */
  availableFrom: string | null;
  /** The smallest cash-out this node will send, in minor units. */
  minimumAmountMinorUnits: number;
  /** The largest cash-out this node will send in one go, in minor units. */
  maximumAmountMinorUnits: number;
  currencyCode: string;
  /** One sentence a person can act on. */
  statusMessage: string;
}

/** One cash-out as the operator board sees it (mirrors backend `FiatPayoutView`); base-unit quantities are strings. */
export interface FiatPayoutView {
  id: string;
  status: string;
  statusReason?: string | null;
  subjectUserId: string;
  tenantId?: string | null;
  amountMinorUnits: number;
  currencyCode: string;
  quantityBaseUnits: string;
  agedQuantityBaseUnits: string;
  unitsOwedBaseUnits: string;
  reversedMinorUnits: number;
  consumedLotCount: number;
  connectedAccountId?: string | null;
  providerTransferId?: string | null;
  unitsCollectedTransactionId?: string | null;
  unitsReturnedTransactionId?: string | null;
  reversalId?: string | null;
  attemptCount: number;
  requestedAt: string;
  unitsCollectedAt?: string | null;
  paidAt?: string | null;
  reversedAt?: string | null;
  providerPayoutId?: string | null;
}

/** What a person's payout account can do right now, in the words they read on the page. */
export interface PayoutAccountStatus {

  /** Whether this node offers cashing out at all. */
  available: boolean;
  /** Whether a payout account exists for this person yet. */
  accountOpened: boolean;
  /** Whether money may actually be sent to it. */
  readyToReceive: boolean;
  /** Whether they have finished the provider's form. */
  detailsSubmitted: boolean;
  /** How many things the provider is still waiting for. */
  outstandingRequirementCount: number;
  /** The hosted setup page, when there is something left to do. */
  setupUrl: string | null;
  /** When that link stops working. */
  setupUrlExpiresAt: string | null;
  /** One sentence a person can act on. */
  statusMessage: string;
}

/** What a caller posts to cash out. The amount is MONEY; the node works out the units. */
export interface CashOutStartParams {
  /**
   * Caller-stable key. The same key returns the same cash-out instead of
   * starting a second one, so a double-tapped button cannot pay twice.
   */
  requestKey: string;
  /** The amount to send, in minor units of the node's configured currency. */
  amountMinorUnits: number;
}

/** One cash-out as a person sees it. No chain words, no provider ids. */
export interface CashOutView {
  id: string;
  status: string;
  /** Consumer sentence for the current status. The row's own words are never shown raw. */
  statusMessage: string;
  amountMinorUnits: number;
  currencyCode: string;
  requestedAt: string;
  paidAt: string | null;
  reversedAt: string | null;
}

// ─── Platform token reserve/genesis read (PlatformTokenController, W3.2/D49) ───
// Operator-scoped (JWT operator policy; unreachable via API key). Read-only —
// the SDK deliberately does not expose the one-time genesis mutation, which
// additionally requires a fresh, non-API-key node-operator login.

/** The platform unit's on-chain definition. Amounts are base-unit decimal strings. */
export interface PlatformTokenDefinition {
  chainType: string;
  network: string;
  assetId: string;
  name: string;
  unitName: string;
  decimals: number;
  totalSupply: string;
  reserveAddress: string;
  activatedAt: string | null;
}

/** Supply in base units as decimal strings — nothing here is truncated to a JS number. */
export interface PlatformTokenSupply {
  /** The genesis ceiling: everything this node could ever issue. */
  total: string;
  /** Reserve outflow: units that have left the reserve and not come back. */
  circulating: string;
  /** Units committed to holds; promised, but not yet out of the reserve. */
  held: string;
  /** Total minus circulating: what the reserve still accounts for. */
  inReserve: string;
}

/** The platform unit plus its live supply. */
export interface PlatformTokenState {
  definition: PlatformTokenDefinition;
  supply: PlatformTokenSupply;
}

// ─── Project treasury reads (ProjectTreasuryController, Admin/Operator) ───
// Operator-scoped read surface for per-project custodial treasuries — the
// accounts a project's funding actually lands in. No SDK method moves units;
// crediting/spending belong to the value paths that actually move money.

export interface ProjectTreasuryAccount {
  chain: string;
  tenantId: string;
  projectReference: string;
  address: string;
  derivationVersion: string;
  status: string;
}

/** One asset's counters on a treasury, in base units as decimal strings. */
export interface ProjectTreasuryAsset {
  assetId: string;
  credited: string;
  spent: string;
  held: string;
  available: string;
}

export interface ProjectTreasuryLedger {
  chain: string;
  network: string;
  tenantId: string;
  projectReference: string;
  address: string;
  derivationVersion: string;
  status: string;
  closedAt: string | null;
  closeReason: string | null;
  assets: ProjectTreasuryAsset[];
  /** Set (with an empty `assets`) when the assets blob could not be decoded — never read as "no commitments". */
  assetsError: string | null;
  version: number;
  createdAt: string;
  updatedAt: string;
}

/** Combines the account, its ledger, and chain truth for one asset. */
export interface ProjectTreasuryView {
  account: ProjectTreasuryAccount;
  ledger: ProjectTreasuryLedger;
  chainBalance: string | null;
  chainBalanceError: string | null;
}

// ─── Node transparency reads (NodeTransparencyController) ───
// Public, anonymous, ETag/Cache-Control-cacheable reads — never signed and
// never scoped to a caller. What any browser may show about this node's own
// governance policy, fee schedule and treasury destinations, plus the append-
// only audit trail behind each. Every method here needs no `token`/`apiKey`
// on the client config at all.

/** Network a chain destination or fee change applies to (mirrors the backend's `ChainNetwork`). */
export type NodeChainNetwork = "Devnet" | "Testnet" | "Mainnet";

/** One operation's flat-plus-bps fee terms, in base units. */
export interface NodeFeeScheduleEntry {
  flatBaseUnits: string;
  bps: number;
  denomination?: "input-base-units-v1" | null;
}

/** The node's live, publicly-declared fee schedule. */
export interface NodeFeeSchedule {
  mint: NodeFeeScheduleEntry;
  transfer: NodeFeeScheduleEntry;
  swap: NodeFeeScheduleEntry;
  actionComplete: NodeFeeScheduleEntry;
  federationPublish: NodeFeeScheduleEntry;
  version: number;
  updatedAt: string | null;
}

/** The node's live, publicly-declared governance parameters. */
export interface NodeGovernanceParameters {
  allowedChains: string[] | null;
  allowedAssetTypes: string[] | null;
  version: number;
  updatedAt: string | null;
}

/** One chain the node settles a treasury on, and the address it uses. */
export interface NodeTreasuryDestination {
  chain: string;
  network: NodeChainNetwork;
  address: string;
  version: number;
  updatedAt: string | null;
}

/** The current state of everything this node discloses about its own policy. */
export interface NodeTransparencySnapshot {
  governance: NodeGovernanceParameters;
  feeSchedule: NodeFeeSchedule;
  treasuryDestinations: NodeTreasuryDestination[];
  lastUpdatedAt: string | null;
  /** Content hash of this snapshot; doubles as the response ETag. */
  contentSha256: string;
  cryptographicHistoryProofAvailable: boolean;
}

/** One cursor-paginated page of a public audit trail. */
export interface NodeTransparencyPage<T> {
  items: T[];
  /** Pass back as the `cursor` query parameter to read the next page. Null on the last page. */
  nextCursor: string | null;
  /** Content hash of this page; doubles as the response ETag. */
  contentSha256: string;
  cryptographicHistoryProofAvailable: boolean;
}

/** One recorded change to the node's governance parameters. */
export interface NodeGovernanceAuditEntry {
  action: string;
  previousVersion: number;
  newVersion: number;
  previousAllowedChains: string[] | null;
  previousAllowedAssetTypes: string[] | null;
  allowedChains: string[] | null;
  allowedAssetTypes: string[] | null;
  occurredAt: string;
}

/** One recorded change to the node's fee schedule. */
export interface NodeFeeAuditEntry {
  action: string;
  previousVersion: number;
  newVersion: number;
  previousSchedule: NodeFeeSchedule | null;
  schedule: NodeFeeSchedule;
  occurredAt: string;
}

/** One recorded change to a treasury destination. */
export interface NodeTreasuryAuditEntry {
  action: string;
  chain: string;
  network: NodeChainNetwork;
  previousVersion: number;
  newVersion: number;
  previousDestination: NodeTreasuryDestination | null;
  destination: NodeTreasuryDestination;
  occurredAt: string;
}

/** Public signing key in SubjectPublicKeyInfo form. */
export interface NodePublicKey {
  algorithm: string;
  keyId: string;
  subjectPublicKeyInfoBase64: string;
}

/** Proof that the immediately preceding node key authorized the current key. */
export interface NodePreviousKeyContinuity {
  key: NodePublicKey;
  continuitySignatureBase64: string;
}

/** Public identity facts for this sovereign node. */
export interface NodeDescriptor {
  nodeId: string;
  currentKey: NodePublicKey;
  previousKey: NodePreviousKeyContinuity | null;
}

/** A node-identity-signed checkpoint over the ordered public audit history. */
export interface NodeTransparencyHistoryCheckpoint {
  schemaVersion: number;
  descriptor: NodeDescriptor;
  issuedAt: string;
  auditEventCount: number;
  headSha256: string;
  signatureBase64: string;
}

/** One redacted event committed into the public governance-history chain. */
export interface NodeTransparencyHistoryEntry {
  kind: string;
  occurredAt: string;
  payloadJson: string;
  entrySha256: string;
}

/**
 * Bounded public history and the checkpoint that authenticates it — a
 * consumer can verify this document independently of trusting the node's own
 * HTTPS transport.
 */
export interface NodeTransparencyHistoryDocument {
  checkpoint: NodeTransparencyHistoryCheckpoint;
  entries: NodeTransparencyHistoryEntry[];
}

// ─── Fiat reversal types (FiatReversalOperatorController) ───
// TYPES ONLY. This is an Operator-JWT-gated admin board (a refund or dispute
// unwind, and the account-level risk flag it can carry) with no API-key
// surface, so the SDK does not wrap it with request methods — an operator
// console types its own `azoa.api.request(...)` calls against these instead
// of hand-rolling the shape locally.

/** "Refund" (the node issued it) or "Dispute" (the payer raised it). */
export type FiatReversalKind = "Refund" | "Dispute";

/** Where a reversal stands (mirrors the backend's `FiatReversal.Status` allow-list). */
export type FiatReversalStatus =
  | "Open"
  | "Recovering"
  | "Recovered"
  | "PartiallyRecovered"
  | "Absorbed"
  | "Reinstated"
  | "Unmatched"
  | "NeedsOperator";

/**
 * Which rung of the compensation ladder answered (mirrors the backend's
 * `FiatReversal.RecoveryPath` allow-list). `Mixed` means more than one rung
 * contributed; `None` means no rung has run yet.
 */
export type FiatReversalRecoveryPath =
  | "None"
  | "Balance"
  | "Hold"
  | "Purchase"
  | "Reserve"
  | "Mixed";

/**
 * One reversal as an operator sees it. Amounts are exact decimal strings;
 * nothing here is a float, because a rounded figure on a financial board is
 * worse than no figure. Card details never existed on the row and none
 * appear here.
 */
export interface FiatReversalView {
  id: string;
  kind: FiatReversalKind;
  status: FiatReversalStatus;
  statusReason: string | null;
  /** The provider event that last moved this reversal. */
  lastEventType: string;
  chargeId: string;
  subjectUserId: string | null;
  flowPurpose: string | null;
  /** The money taken back, in minor units of `currencyCode`. */
  amountMinorUnits: number;
  /** The original payment, in the same minor units. */
  sourceAmountMinorUnits: number;
  currencyCode: string;
  isPartial: boolean;
  /** False for a dispute nobody has debited yet. */
  fundsWithdrawn: boolean;
  assetId: string | null;
  targetQuantityBaseUnits: string;
  recoveredQuantityBaseUnits: string;
  absorbedQuantityBaseUnits: string;
  reinstatementOwedBaseUnits: string;
  recoveryPath: FiatReversalRecoveryPath;
  accountFlagged: boolean;
  flagReason: string | null;
  flagClearedAt: string | null;
  attemptCount: number;
  openedAt: string;
  resolvedAt: string | null;
  reinstatedAt: string | null;
  /** Attempt-suffixed key of a move whose outcome is ambiguous; set while the reversal is parked on it. */
  pendingMoveKey?: string | null;
}

// ─── DappSeries types matching .NET DTOs (DappSeriesController) ───

/** DappSeries lifecycle status (mirrors .NET DappSeries.StatusKind). */
export type DappSeriesStatus = "Draft" | "Building" | "Ready" | "Deploying" | "Deployed" | "DeploymentFailed" | "Archived";

/** A linked series of action DAGs that compose into a deployable dApp (mirrors .NET DappSeries). */
export interface DappSeriesResult {
  id: string;
  name: string;
  description?: string;
  userId: string;
  status: DappSeriesStatus;
  /** Cross-action deployment settings (string→string map), serialized as JSON on the row. */
  sharedConfig?: unknown;
  actionGraphBundleId?: string;
  deploymentOperationId?: string;
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

const HEX32_ID_RE = /^[0-9a-f]{32}$/;
const HEX64_ID_RE = /^[0-9a-f]{64}$/;

/** Validates a server-issued 32-lowercase-hex id (NFT lifecycle reference, DEX quote) before URL interpolation. */
function assertHex32(id: string, paramName: string, noun = "id"): void {
  if (typeof id !== "string" || !HEX32_ID_RE.test(id)) {
    throw new SdkError(SdkErrorCode.INVALID_INPUT, `Invalid ${paramName}: expected a 32-character lowercase hex ${noun}`);
  }
}

/** Validates a server-issued 64-lowercase-hex id (swap/WTT/hold operation, stage, mandate) before URL interpolation. */
function assertHex64(id: string, paramName: string, noun = "id"): void {
  if (typeof id !== "string" || !HEX64_ID_RE.test(id)) {
    throw new SdkError(SdkErrorCode.INVALID_INPUT, `Invalid ${paramName}: expected a 64-character lowercase hex ${noun}`);
  }
}

/** Clamps a list limit to [1, max] as an integer. */
function clampLimit(limit: number, max: number): number {
  return Math.max(1, Math.min(max, Math.trunc(limit)));
}

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

/** Appends the `limit`/`cursor` pagination pair the node-transparency audit reads share. */
function withAuditQuery(path: string, limit?: number, cursor?: string): string {
  const qs = new URLSearchParams();
  if (limit !== undefined) qs.set("limit", String(limit));
  if (cursor !== undefined) qs.set("cursor", cursor);
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

  async getBusinessVerificationCapabilities(): Promise<Result<BusinessVerificationCapabilities, SdkError>> {
    return this.request("GET", "/api/tenant/business-verification/capabilities");
  }

  async getBusinessVerification(): Promise<Result<BusinessVerificationStatus | null, SdkError>> {
    return this.request("GET", "/api/tenant/business-verification");
  }

  async recoverOnRampPayment(intentId: string, action: "resume" | "refund", reason: string, providerRefundId?: string, retry?: { expectedRefundAttempt: number }): Promise<Result<OnRampLotView, SdkError>> {
    return this.request<OnRampLotView>("POST", `/api/operator/on-ramp-lots/intents/${encodeURIComponent(intentId)}/${action}`, { reason, providerRefundId, ...(retry ? { retryFailed: true, expectedRefundAttempt: retry.expectedRefundAttempt } : {}) });
  }

  async recoverFailedBankPayout(payoutId: string, reason: string): Promise<Result<FiatPayoutView, SdkError>> {
    return this.request<FiatPayoutView>("POST", `/api/admin/fiat-payouts/${encodeURIComponent(payoutId)}/recover`, { reason });
  }

  async listEthereumPreparations(network: "Devnet" | "Testnet" | "Mainnet", operator = false): Promise<Result<EthereumPreparationView[], SdkError>> {
    return this.request("GET", `/api/${operator ? "operator/" : ""}ethereum/recovery/${network}`);
  }

  async activateEthereumSigner(network: "Devnet" | "Testnet" | "Mainnet", walletId: string): Promise<Result<boolean, SdkError>> {
    return this.request("POST", `/api/ethereum/recovery/${network}/activate`, { walletId });
  }

  async activateEthereumPlatformSigner(network: "Devnet" | "Testnet" | "Mainnet", sender: string): Promise<Result<boolean, SdkError>> {
    return this.request("POST", `/api/operator/ethereum/recovery/${network}/activate`, { sender });
  }

  async recoverEthereumPreparation(network: "Devnet" | "Testnet" | "Mainnet", id: string, request: EthereumRecoveryRequest, operator = false): Promise<Result<EthereumPreparationView, SdkError>> {
    return this.request("POST", `/api/${operator ? "operator/" : ""}ethereum/recovery/${network}/${encodeURIComponent(id)}`, request);
  }

  async replayFiatEvent(eventId: string): Promise<Result<FiatEventView, SdkError>> {
    return this.request<FiatEventView>("POST", `/api/operator/fiat-events/${encodeURIComponent(eventId)}/replay`, {});
  }

  async listFiatEvents(limit = 50): Promise<Result<FiatEventView[], SdkError>> {
    return this.request("GET", `/api/operator/fiat-events?limit=${Math.max(1, Math.min(100, Math.trunc(limit)))}`);
  }

  async createWalletAuthenticationChallenge(address: string, chainType: WalletAuthenticationChain): Promise<Result<WalletAuthenticationChallenge, SdkError>> {
    return this.request("POST", "/api/user/auth/challenge", { address, chainType }, false, undefined, true);
  }

  async verifyWalletAuthentication(proof: WalletAuthenticationProof): Promise<Result<WalletAuthenticationToken, SdkError>> {
    return this.request("POST", "/api/user/auth/verify", proof, false, undefined, true);
  }

  async linkAuthenticationWallet(proof: WalletAuthenticationProof): Promise<Result<boolean, SdkError>> {
    return this.request("POST", "/api/user/wallet/link", proof);
  }

  async createWalletOwnershipInvite(childUserId: string): Promise<Result<{ claimToken: string; targetUserId: string; expiresAt: string }, SdkError>> {
    return this.request("POST", "/api/user/claim-invite", { childUserId });
  }

  async claimWalletOwnership(request: WalletOwnershipClaim): Promise<Result<WalletAuthenticationToken, SdkError>> {
    return this.request("POST", "/api/user/claim", request, false, undefined, true);
  }

  async listActionRecovery(limit = 50): Promise<Result<ActionRecoveryView[], SdkError>> {
    return this.request("GET", `/api/operator/action-recovery?limit=${Math.max(1, Math.min(100, Math.trunc(limit)))}`);
  }

  async getActionRecovery(executionId: string): Promise<Result<ActionRecoveryView, SdkError>> {
    return this.request("GET", `/api/operator/action-recovery/${encodeURIComponent(executionId)}`);
  }

  async getActionRecoveryHistory(executionId: string): Promise<Result<ActionRecoveryAudit[], SdkError>> {
    return this.request("GET", `/api/operator/action-recovery/${encodeURIComponent(executionId)}/history`);
  }

  async retryActionRecovery(executionId: string, request: ActionRecoveryRequest): Promise<Result<ActionRecoveryReceipt, SdkError>> {
    return this.request("POST", `/api/operator/action-recovery/${encodeURIComponent(executionId)}/retry`, request);
  }

  async listHostedSessionRecovery(limit = 50): Promise<Result<HostedSessionRecoveryView[], SdkError>> {
    return this.request("GET", `/api/operator/hosted-sessions/reconciliation?limit=${Math.max(1, Math.min(100, Math.trunc(limit)))}`);
  }

  /** Operator: re-reads the provider for one hosted session (Identomat KYC/KYB included); audited server-side. */
  async refreshHostedSession(sessionId: string): Promise<Result<HostedSessionRefreshReceipt, SdkError>> {
    assertUuid(sessionId, "sessionId");
    return this.request("POST", API_PATHS.HOSTED_SESSION_REFRESH(sessionId), {});
  }

  async reconcileHostedSession(sessionId: string, request: HostedSessionRecoveryRequest): Promise<Result<{ sessionId: string; status: string; reconciledAt?: string }, SdkError>> {
    assertUuid(sessionId, "sessionId");
    return this.request("POST", `/api/operator/hosted-sessions/${encodeURIComponent(sessionId)}/reconcile`, request);
  }

  async refreshBusinessVerification(): Promise<Result<BusinessVerificationStatus | null, SdkError>> {
    return this.request("POST", "/api/tenant/business-verification/refresh");
  }

  async beginBusinessVerification(idempotencyKey: string): Promise<Result<BusinessVerificationSession, SdkError>> {
    assertNonEmpty(idempotencyKey, "idempotencyKey");
    return this.request("POST", "/api/tenant/business-verification/session", undefined, false,
      { "Idempotency-Key": idempotencyKey });
  }

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

  async loginByUserId(userId: string, password: string): Promise<Result<string, SdkError>> {
    return this.request("POST", "/api/user/login", { userId, password }, false, undefined, true);
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

  /** Returns costs for explicit consent without admitting or signing the mint. */
  async quoteNftMintCosts(params: NftMintParams, idempotencyKey: string): Promise<Result<NftProtocolCostOffer, SdkError>> {
    assertNonEmpty(idempotencyKey, "idempotencyKey");
    return this.request("POST", "/api/nft/mint-cost-quote", params, false, { "Idempotency-Key": idempotencyKey });
  }

  async mintNft(params: NftMintParams, idempotencyKey: string): Promise<Result<BlockchainOperationResult, SdkError>> {
    // POST /api/nft/mint with NftMintRequest body
    return this.request<BlockchainOperationResult>("POST", "/api/nft/mint", params, false, { "Idempotency-Key": idempotencyKey });
  }

  /** Retain the original key and descriptor until the server reports a terminal operation. */
  async fungibleMint(params: FungibleMintParams, options: { idempotencyKey: string }): Promise<Result<FungibleTokenResult, SdkError>> {
    assertNonEmpty(options.idempotencyKey, "idempotencyKey");
    if (typeof params.total === "number" && (!Number.isSafeInteger(params.total) || params.total <= 0))
      throw new Error("Supply must be a positive safe integer or canonical uint64 text.");
    const total = String(params.total);
    if (!/^[1-9][0-9]*$/.test(total) || BigInt(total) > 18446744073709551615n)
      throw new Error("Supply must be canonical positive uint64 text.");
    return this.request<FungibleTokenResult>("POST", "/api/nft/fungible-mint", { ...params, total }, false,
      { "Idempotency-Key": options.idempotencyKey });
  }

  async listFungibleCreations(offset = 0, limit = 25): Promise<Result<FungibleCreationView[], SdkError>> {
    return this.request("GET", `/api/fungible-creation?offset=${offset}&limit=${limit}`);
  }

  async getFungibleCreation(id: string): Promise<Result<FungibleCreationView, SdkError>> {
    return this.request("GET", `/api/fungible-creation/${encodeURIComponent(id)}`);
  }

  async quoteFungibleCreation(id: string): Promise<Result<FungibleCreationView, SdkError>> {
    return this.request("POST", `/api/fungible-creation/${encodeURIComponent(id)}/quote`);
  }

  async acceptFungibleCreationQuote(id: string, request: { version: number; quoteId: string }): Promise<Result<FungibleCreationView, SdkError>> {
    return this.request("POST", `/api/fungible-creation/${encodeURIComponent(id)}/accept-quote`, request);
  }

  async continueFungibleCreation(id: string, request: FungibleContinuationRequest): Promise<Result<FungibleCreationView, SdkError>> {
    return this.request("POST", `/api/fungible-creation/${encodeURIComponent(id)}/continue`, request);
  }

  async refreshFungibleCreation(id: string): Promise<Result<FungibleCreationView, SdkError>> {
    return this.request("POST", `/api/fungible-creation/${encodeURIComponent(id)}/refresh`);
  }

  async replaceFungibleCreation(id: string, request: FungibleContinuationRequest): Promise<Result<FungibleCreationView, SdkError>> {
    return this.request("POST", `/api/fungible-creation/${encodeURIComponent(id)}/replace`, request);
  }

  async cancelFungibleCreation(id: string, request: FungibleContinuationRequest): Promise<Result<FungibleCreationView, SdkError>> {
    return this.request("POST", `/api/fungible-creation/${encodeURIComponent(id)}/cancel`, request);
  }

  async getFungibleAttachment(id: string): Promise<Result<FungibleAttachmentView, SdkError>> {
    return this.request("GET", `/api/fungible-creation/${encodeURIComponent(id)}/attachment`);
  }

  async attachFungibleCreation(id: string, request: FungibleAttachmentRequest): Promise<Result<FungibleAttachmentView, SdkError>> {
    return this.request("POST", `/api/fungible-creation/${encodeURIComponent(id)}/attachment`, request);
  }

  async transferNft(nftId: string, params: NftTransferParams, idempotencyKey: string): Promise<Result<BlockchainOperationResult, SdkError>> {
    assertUuid(nftId, "nftId");
    // POST /api/nft/{id}/transfer with NftTransferRequest body
    return this.request<BlockchainOperationResult>("POST", `/api/nft/${nftId}/transfer`, params, false, { "Idempotency-Key": idempotencyKey });
  }

  async burnNft(nftId: string, params: NftBurnParams, idempotencyKey: string): Promise<Result<BlockchainOperationResult, SdkError>> {
    assertUuid(nftId, "nftId");
    // POST /api/nft/{id}/burn with NftBurnRequest body
    return this.request<BlockchainOperationResult>("POST", `/api/nft/${nftId}/burn`, params, false, { "Idempotency-Key": idempotencyKey });
  }

  async getNftMetadata(nftId: string): Promise<Result<NftMetadata, SdkError>> {
    assertUuid(nftId, "nftId");
    return this.request("GET", `/api/nft/${nftId}/metadata`);
  }

  // ─── Credential (CredentialController, D19) ───

  /** Starts or resumes issuer-authorized credential revocation; pending outcomes require lifecycle reconciliation. */
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

  // ─── ActionGraphBundle ───

  async getActionGraphBundle(id: string): Promise<Result<ActionGraphBundleResult, SdkError>> {
    assertUuid(id, "actiongraphbundleId");
    return this.request("GET", `/api/actiongraphbundle/${id}`);
  }

  async listActionGraphBundle(): Promise<Result<ActionGraphBundleResult[], SdkError>> {
    return this.request("GET", "/api/actiongraphbundle");
  }

  /**
   * Create or update an action graph bundle record.
   *
   * Upsert by id — the backend's `POST /api/actiongraphbundle` (ActionGraphBundleController.CreateOrUpdate)
   * is the same endpoint also used to update an existing record. See {@link updateActionGraphBundle}
   * for a semantically-explicit alias when the caller intends an update.
   */
  async createActionGraphBundle(params: ActionGraphBundleCreateParams): Promise<Result<ActionGraphBundleResult, SdkError>> {
    return this.request("POST", "/api/actiongraphbundle", params);
  }

  /**
   * Update an existing action graph bundle record via PUT. Routes through the same
   * `CreateOrUpdateAsync` upsert path as POST but uses the explicit-id route
   * `PUT /api/actiongraphbundle/{id}`, making update intent unambiguous.
   */
  async updateActionGraphBundle(id: string, model: ActionGraphBundleCreateParams): Promise<Result<ActionGraphBundleResult, SdkError>> {
    assertUuid(id, "actiongraphbundleId");
    return this.request("PUT", `/api/actiongraphbundle/${id}`, model);
  }

  async deleteActionGraphBundle(id: string): Promise<Result<{ message: string }, SdkError>> {
    assertUuid(id, "actiongraphbundleId");
    return this.request("DELETE", `/api/actiongraphbundle/${id}`);
  }

  async generateActionGraphBundleDapp(actiongraphbundleId: string, params: ActionGraphBundleDappGenerationParams): Promise<Result<ActionGraphBundleResult, SdkError>> {
    assertUuid(actiongraphbundleId, "actiongraphbundleId");
    return this.request("POST", `/api/actiongraphbundle/${actiongraphbundleId}/generate`, params);
  }

  async deployActionGraphBundle(actiongraphbundleId: string, request: DeploymentStartRequest, requestKey: string): Promise<Result<DeploymentReceipt, SdkError>> {
    assertUuid(actiongraphbundleId, "actiongraphbundleId");
    return this.request("POST", `/api/actiongraphbundle/${actiongraphbundleId}/deploy`, request, false, { "Idempotency-Key": requestKey });
  }

  async generateDeployment(bundleId: string, request: DeploymentGenerateRequest): Promise<Result<DeploymentArtifactView, SdkError>> {
    assertUuid(bundleId, "bundleId");
    return this.request("POST", `/api/deployments/bundles/${bundleId}/generate`, request);
  }

  async getDeploymentArtifact(artifactId: string): Promise<Result<DeploymentArtifactView, SdkError>> {
    return this.request("GET", `/api/deployments/artifacts/${encodeURIComponent(artifactId)}`);
  }

  /** Read-only authoritative quote; retain the returned immutable intent for admission. */
  async quoteDeployment(request: DeploymentQuoteRequest): Promise<Result<NativeOperationIntent, SdkError>> {
    return this.request("POST", "/api/deployments/quote", request);
  }

  /** Simulation/read only: this endpoint cannot allocate custody, nonces, or broadcast. */
  async queryNativeContract(request: ContractQueryRequest): Promise<Result<unknown, SdkError>> {
    return this.request("POST", "/api/deployments/contract-query", request);
  }

  /** Reuse both key and request after a lost response. Pending is not confirmation. */
  async startDeployment(request: DeploymentStartRequest, requestKey: string): Promise<Result<DeploymentReceipt, SdkError>> {
    return this.request("POST", "/api/deployments", request, false, { "Idempotency-Key": requestKey });
  }

  async listDeployments(cursor?: string, limit = 25): Promise<Result<DeploymentPage, SdkError>> {
    const query = new URLSearchParams({ limit: String(limit) });
    if (cursor) query.set("cursor", cursor);
    return this.request("GET", `/api/deployments?${query}`);
  }

  async getDeployment(operationId: string): Promise<Result<DeploymentReceipt, SdkError>> {
    return this.request("GET", `/api/deployments/${encodeURIComponent(operationId)}`);
  }

  /** Absence does not prove a suspended request cannot still be admitted. */
  async getDeploymentByRequest(requestKey: string): Promise<Result<DeploymentReceipt, SdkError>> {
    return this.request("GET", `/api/deployments/request?${new URLSearchParams({ requestKey })}`);
  }

  /** Atomically fences delayed admission, or cancels an existing safely cancellable operation. */
  async cancelDeploymentRequest(requestKey: string, request: DeploymentStartRequest): Promise<Result<DeploymentReceipt, SdkError>> {
    return this.request("POST", "/api/deployments/request/cancel", { requestKey, request });
  }

  /** Interactive authorization of the next stage; the server rechecks live custody and caps. */
  async continueDeployment(operationId: string): Promise<Result<DeploymentReceipt, SdkError>> {
    return this.request("POST", `/api/deployments/${encodeURIComponent(operationId)}/continue`);
  }

  async cancelDeployment(operationId: string): Promise<Result<DeploymentReceipt, SdkError>> {
    return this.request("POST", `/api/deployments/${encodeURIComponent(operationId)}/cancel`);
  }

  async recoverDeploymentWithinBudget(operationId: string, request: DeploymentOriginalBudgetRecoveryRequest): Promise<Result<DeploymentReceipt, SdkError>> {
    return this.request("POST", `/api/deployments/${encodeURIComponent(operationId)}/recovery/original-budget`, request);
  }

  async quoteDeploymentRecovery(operationId: string, request: DeploymentRecoveryQuoteRequest): Promise<Result<DeploymentRecoveryQuote, SdkError>> {
    return this.request("POST", `/api/deployments/${encodeURIComponent(operationId)}/recovery/quote`, request);
  }

  /** Persist this exact request before sending; replay it after a lost response. */
  async startDeploymentRecovery(operationId: string, request: DeploymentRecoveryStartRequest): Promise<Result<DeploymentReceipt, SdkError>> {
    return this.request("POST", `/api/deployments/${encodeURIComponent(operationId)}/recovery/start`, request);
  }

  /** Success proves this exact unadmitted quote can no longer authorize recovery. */
  async invalidateDeploymentRecovery(operationId: string, request: DeploymentRecoveryStartRequest): Promise<Result<DeploymentReceipt, SdkError>> {
    return this.request("POST", `/api/deployments/${encodeURIComponent(operationId)}/recovery/invalidate`, request);
  }

  async getWebhookRegistration(): Promise<Result<WebhookRegistrationStatus | null, SdkError>> {
    return this.request("GET", "/api/tenant/webhook-registration");
  }

  /** Persist the command before submission and reuse it unchanged after response loss. */
  async grantConsent(command: ConsentGrantCommand): Promise<Result<ConsentGrantReceipt, SdkError>> {
    return this.request("POST", "/api/user/consent", command);
  }

  async withdrawConsentCommand(command: ConsentGrantCommand): Promise<Result<ConsentCommandWithdrawal, SdkError>> {
    return this.request("POST", "/api/user/consent/withdraw", command);
  }

  async listConsentGrants(): Promise<Result<ConsentGrantView[], SdkError>> {
    return this.request("GET", "/api/user/consent");
  }

  async getConsentGrant(grantId: string): Promise<Result<ConsentGrantView, SdkError>> {
    return this.request("GET", `/api/user/consent/${encodeURIComponent(grantId)}`);
  }

  async revokeConsent(grantId: string): Promise<Result<ConsentRevocationReceipt, SdkError>> {
    return this.request("DELETE", `/api/user/consent/${encodeURIComponent(grantId)}`);
  }

  async listTenantConsentGrants(participationRef?: string): Promise<Result<ConsentGrantView[], SdkError>> {
    return this.request("GET", `/api/tenant/consent${participationRef === undefined ? "" : `?participationRef=${encodeURIComponent(participationRef)}`}`);
  }

  async offboardParticipation(participationRef: string): Promise<Result<ConsentOffboardReceipt, SdkError>> {
    return this.request("DELETE", `/api/tenant/consent/participation/${encodeURIComponent(participationRef)}`);
  }

  async offboardProvisionedUser(externalUserId: string): Promise<Result<ConsentOffboardReceipt, SdkError>> {
    return this.request("DELETE", `/api/tenant/consent/provisioned?externalUserId=${encodeURIComponent(externalUserId)}`);
  }

  async listConsentWebhookEvents(offset = 0, limit = 25): Promise<Result<ConsentWebhookEventStatus[], SdkError>> {
    return this.request("GET", `/api/tenant/consent-webhook-events?offset=${offset}&limit=${limit}`);
  }

  async getConsentWebhookEvent(eventId: string): Promise<Result<ConsentWebhookEventStatus, SdkError>> {
    return this.request("GET", `/api/tenant/consent-webhook-events/${encodeURIComponent(eventId)}`);
  }

  async retryConsentWebhookEvent(eventId: string, command: WebhookEventRetryCommand): Promise<Result<WebhookEventRetryReceipt, SdkError>> {
    return this.request("POST", `/api/tenant/consent-webhook-events/${encodeURIComponent(eventId)}/retry`, command);
  }

  async listOperatorConsentWebhookEvents(tenantId: string, offset = 0, limit = 25): Promise<Result<ConsentWebhookEventStatus[], SdkError>> {
    return this.request("GET", `/api/operator/tenants/${encodeURIComponent(tenantId)}/consent-webhook-events?offset=${offset}&limit=${limit}`);
  }

  async getOperatorConsentWebhookEvent(tenantId: string, eventId: string): Promise<Result<ConsentWebhookEventStatus, SdkError>> {
    return this.request("GET", `/api/operator/tenants/${encodeURIComponent(tenantId)}/consent-webhook-events/${encodeURIComponent(eventId)}`);
  }

  async retryOperatorConsentWebhookEvent(tenantId: string, eventId: string, command: WebhookEventRetryCommand): Promise<Result<WebhookEventRetryReceipt, SdkError>> {
    return this.request("POST", `/api/operator/tenants/${encodeURIComponent(tenantId)}/consent-webhook-events/${encodeURIComponent(eventId)}/retry`, command);
  }

  async listWebhookEvents(offset = 0, limit = 25): Promise<Result<WebhookEventStatus[], SdkError>> {
    return this.request("GET", `/api/tenant/webhook-events?offset=${offset}&limit=${limit}`);
  }

  async getWebhookEvent(eventId: string): Promise<Result<WebhookEventStatus, SdkError>> {
    return this.request("GET", `/api/tenant/webhook-events/${encodeURIComponent(eventId)}`);
  }

  /** Preserve the exact command when retrying after response loss. */
  async retryWebhookEvent(eventId: string, command: WebhookEventRetryCommand): Promise<Result<WebhookEventRetryReceipt, SdkError>> {
    return this.request("POST", `/api/tenant/webhook-events/${encodeURIComponent(eventId)}/retry`, command);
  }

  async listOperatorWebhookEvents(tenantId: string, offset = 0, limit = 25): Promise<Result<WebhookEventStatus[], SdkError>> {
    return this.request("GET", `/api/operator/tenants/${encodeURIComponent(tenantId)}/webhook-events?offset=${offset}&limit=${limit}`);
  }

  async getOperatorWebhookEvent(tenantId: string, eventId: string): Promise<Result<WebhookEventStatus, SdkError>> {
    return this.request("GET", `/api/operator/tenants/${encodeURIComponent(tenantId)}/webhook-events/${encodeURIComponent(eventId)}`);
  }

  async retryOperatorWebhookEvent(tenantId: string, eventId: string, command: WebhookEventRetryCommand): Promise<Result<WebhookEventRetryReceipt, SdkError>> {
    return this.request("POST", `/api/operator/tenants/${encodeURIComponent(tenantId)}/webhook-events/${encodeURIComponent(eventId)}/retry`, command);
  }

  /** Retry the same command after response loss; a replay never rediscloses a secret. */
  async updateWebhookRegistration(command: WebhookRegistrationCommand): Promise<Result<WebhookRegistrationMutationResult, SdkError>> {
    return this.request("PUT", "/api/tenant/webhook-registration", command);
  }

  async withdrawWebhookRegistration(command: WebhookRegistrationCommand): Promise<Result<WebhookRegistrationWithdrawal, SdkError>> {
    return this.request("POST", "/api/tenant/webhook-registration/withdraw", command);
  }

  async withdrawOperatorWebhookRegistration(tenantId: string, command: WebhookRegistrationCommand): Promise<Result<WebhookRegistrationWithdrawal, SdkError>> {
    return this.request("POST", `/api/operator/tenants/${encodeURIComponent(tenantId)}/webhook-registration/withdraw`, command);
  }

  async getOperatorWebhookRegistration(tenantId: string): Promise<Result<WebhookRegistrationStatus | null, SdkError>> {
    return this.request("GET", `/api/operator/tenants/${encodeURIComponent(tenantId)}/webhook-registration`);
  }

  async updateOperatorWebhookRegistration(tenantId: string, command: WebhookRegistrationCommand): Promise<Result<WebhookRegistrationMutationResult, SdkError>> {
    return this.request("PUT", `/api/operator/tenants/${encodeURIComponent(tenantId)}/webhook-registration`, command);
  }

  /** Inspects one durable ancestry page without signing or submitting a transaction. */
  async inspectDeploymentRecoveryAncestry(operationId: string): Promise<Result<DeploymentRecoveryAncestry, SdkError>> {
    return this.request("POST", `/api/deployments/${encodeURIComponent(operationId)}/recovery/ancestry/inspect`);
  }

  /** Admits an actual caller-owned run; observe the returned run through getActionRun. */
  async invokeDeployment(operationId: string, request: DeploymentInvokeRequest, invocationKey: string): Promise<Result<ActionRunResult, SdkError>> {
    return this.request("POST", `/api/deployments/${encodeURIComponent(operationId)}/invoke`, request, false, { "Idempotency-Key": invocationKey });
  }

  async generateDappSeries(seriesId: string, request: DeploymentGenerateRequest): Promise<Result<ActionGraphBundleResult, SdkError>> {
    assertUuid(seriesId, "seriesId");
    return this.request("POST", `/api/dapp-series/${seriesId}/generate`, request);
  }

  async composeDappSeries(seriesId: string): Promise<Result<DappCompositionManifest, SdkError>> {
    assertUuid(seriesId, "seriesId");
    return this.request("POST", `/api/dapp-series/${seriesId}/compose`);
  }

  async validateDappSeries(seriesId: string): Promise<Result<DappCompositionValidation, SdkError>> {
    assertUuid(seriesId, "seriesId");
    return this.request("GET", `/api/dapp-series/${seriesId}/validate`);
  }

  async getDappSeriesManifest(seriesId: string): Promise<Result<DappCompositionManifest, SdkError>> {
    assertUuid(seriesId, "seriesId");
    return this.request("GET", `/api/dapp-series/${seriesId}/manifest`);
  }

  async getDappSeriesDeploymentStatus(seriesId: string): Promise<Result<DappSeriesStatus, SdkError>> {
    assertUuid(seriesId, "seriesId");
    return this.request("GET", `/api/dapp-series/${seriesId}/status`);
  }

  async getDeploymentEcosystem(bundleId: string): Promise<Result<DeploymentEcosystemTree, SdkError>> {
    assertUuid(bundleId, "bundleId");
    return this.request("GET", `/api/actiongraphbundle/${bundleId}/ecosystem`);
  }

  async attachDeploymentEcosystem(bundleId: string, request: DeploymentEcosystemAttachment): Promise<Result<DeploymentEcosystemTree, SdkError>> {
    assertUuid(bundleId, "bundleId");
    return this.request("POST", `/api/actiongraphbundle/${bundleId}/ecosystem/dapp-series`, request);
  }

  async deployDappSeries(seriesId: string, request: DeploymentStartRequest, requestKey: string): Promise<Result<DeploymentReceipt, SdkError>> {
    assertUuid(seriesId, "seriesId");
    return this.request("POST", `/api/dapp-series/${seriesId}/deploy`, request, false, { "Idempotency-Key": requestKey });
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

  // ─── Exchange (ExchangeController, W5.1/D50) ───
  // Value-moving write requires an API-key principal carrying the exchange-
  // signing scope, exactly like allocate(). Unlike allocate(), the
  // idempotency key is REQUIRED — the backend refuses a bare exchange rather
  // than guessing whether a repeat is a retry or a second sponsorship.

  /**
   * Runs one sponsorship exchange for `userId` at the rate the caller
   * declared (AZOA never prices an exchange itself). Idempotent on the
   * `Idempotency-Key` header, partitioned by the caller's own API key, so a
   * redelivered request never sponsors twice.
   *
   * @param userId The person the exchange is for. Route value only.
   * @param params The exchange details — rail, direction, amounts, declared rate.
   * @param idempotencyKey The caller's own stable key for this exchange. Required.
   */
  async exchange(
    userId: string,
    params: ExchangeParams,
    idempotencyKey: string
  ): Promise<Result<ExchangeReceipt, SdkError>> {
    assertUuid(userId, "userId");
    assertNonEmpty(idempotencyKey, "idempotencyKey");
    return this.request<ExchangeReceipt>(
      "POST",
      API_PATHS.EXCHANGE_RUN(userId),
      params,
      false,
      { "Idempotency-Key": idempotencyKey }
    );
  }

  /**
   * The receipt for one exchange — the read surface a consumer reconciles
   * against. Scoped to the caller's own API key and identity.
   */
  async getExchangeReceipt(idempotencyKey: string): Promise<Result<ExchangeReceipt, SdkError>> {
    assertNonEmpty(idempotencyKey, "idempotencyKey");
    return this.request<ExchangeReceipt>("GET", API_PATHS.EXCHANGE_RECEIPT(idempotencyKey));
  }

  // ─── Hold commands (HoldCommandsController) and release mandates ───

  /** Admits the original keyed command and returns its actual custody progress. */
  async startHoldCommand(request: HoldStartCommand): Promise<Result<HoldCommandView, SdkError>> {
    assertNonEmpty(request.intent.requestKey, 'requestKey');
    return this.request('POST', '/api/hold-commands', request);
  }

  async listHoldCommands(offset = 0, limit = 25): Promise<Result<HoldCommandPage, SdkError>> {
    return this.request('GET', `/api/hold-commands?offset=${offset}&limit=${limit}`);
  }

  async getHoldCommand(id: string): Promise<Result<HoldCommandView, SdkError>> {
    assertHex64(id, 'operationId');
    return this.request('GET', `/api/hold-commands/${encodeURIComponent(id)}`);
  }

  /** Observes or rebroadcasts previously authorized bytes without new signing consent. */
  async refreshHoldCommand(id: string): Promise<Result<HoldCommandView, SdkError>> {
    assertHex64(id, 'operationId');
    return this.request('POST', `/api/hold-commands/${encodeURIComponent(id)}/refresh`);
  }

  async continueHoldCommand(id: string, request: HoldConsentCommand): Promise<Result<HoldCommandView, SdkError>> {
    assertHex64(id, 'operationId');
    return this.request('POST', `/api/hold-commands/${encodeURIComponent(id)}/continue`, request);
  }

  async acceptHoldRecipient(id: string, request: HoldConsentCommand): Promise<Result<HoldCommandView, SdkError>> {
    assertHex64(id, 'operationId');
    return this.request('POST', `/api/hold-commands/${encodeURIComponent(id)}/accept`, request);
  }

  async replaceHoldCommand(id: string, request: HoldRecoveryCommand): Promise<Result<HoldCommandView, SdkError>> {
    assertHex64(id, 'operationId');
    return this.request('POST', `/api/hold-commands/${encodeURIComponent(id)}/replace`, request);
  }

  async cancelHoldCommand(id: string, request: HoldRecoveryCommand): Promise<Result<HoldCommandView, SdkError>> {
    assertHex64(id, 'operationId');
    return this.request('POST', `/api/hold-commands/${encodeURIComponent(id)}/cancel`, request);
  }

  async reconcileHoldCommand(id: string, request: HoldRequestLookup): Promise<Result<HoldRequestResolution, SdkError>> {
    assertHex64(id, 'operationId');
    return this.request('POST', `/api/hold-commands/${encodeURIComponent(id)}/reconcile-request`, request);
  }

  async acceptHoldOpening(id: string, request: HoldOpeningAcceptance): Promise<Result<HoldCommandView, SdkError>> {
    assertHex64(id, 'operationId');
    return this.request('POST', `/api/hold-commands/${encodeURIComponent(id)}/accept-opening`, request);
  }

  async reconcileHoldOpening(id: string, request: HoldOpeningAcceptance): Promise<Result<HoldRequestResolution, SdkError>> {
    assertHex64(id, 'operationId');
    return this.request('POST', `/api/hold-commands/${encodeURIComponent(id)}/reconcile-opening`, request);
  }

  async replaceHoldOpening(id: string, request: HoldRecoveryCommand): Promise<Result<HoldCommandView, SdkError>> {
    assertHex64(id, 'operationId');
    return this.request('POST', `/api/hold-commands/${encodeURIComponent(id)}/replace-opening`, request);
  }

  async withdrawHoldOpening(id: string, request: HoldRecoveryCommand): Promise<Result<HoldCommandView, SdkError>> {
    assertHex64(id, 'operationId');
    return this.request('POST', `/api/hold-commands/${encodeURIComponent(id)}/withdraw-opening`, request);
  }

  async reconcileHoldOpeningRecovery(id: string, request: HoldOpeningRecoveryLookup): Promise<Result<HoldRequestResolution, SdkError>> {
    assertHex64(id, 'operationId');
    return this.request('POST', `/api/hold-commands/${encodeURIComponent(id)}/reconcile-opening-recovery`, request);
  }

  async quoteHoldReleaseMandate(request: HoldMandateOfferRequest): Promise<Result<HoldMandateOffer, SdkError>> {
    return this.request('POST', '/api/hold-release-mandates/offers', request);
  }

  async acceptHoldReleaseMandate(request: HoldMandateAcceptance): Promise<Result<HoldMandateView, SdkError>> {
    return this.request('POST', '/api/hold-release-mandates/accept', request);
  }

  async listHoldReleaseMandates(offset = 0, limit = 25): Promise<Result<HoldMandatePage, SdkError>> {
    return this.request('GET', `/api/hold-release-mandates?offset=${offset}&limit=${limit}`);
  }

  async getHoldReleaseMandate(id: string): Promise<Result<HoldMandateView, SdkError>> {
    assertHex64(id, 'mandateId');
    return this.request('GET', `/api/hold-release-mandates/${encodeURIComponent(id)}`);
  }

  async reauthorizeHoldReleaseMandate(id: string, request: HoldRecoveryCommand): Promise<Result<HoldMandateView, SdkError>> {
    assertHex64(id, 'mandateId');
    return this.request('POST', `/api/hold-release-mandates/${encodeURIComponent(id)}/reauthorize`, request);
  }

  async revokeHoldReleaseMandate(id: string, request: HoldRecoveryCommand): Promise<Result<HoldMandateView, SdkError>> {
    assertHex64(id, 'mandateId');
    return this.request('POST', `/api/hold-release-mandates/${encodeURIComponent(id)}/revoke`, request);
  }

  async reconcileHoldReleaseMandate(id: string, request: { kind: 'MandateReauthorize' | 'MandateRevoke'; request: HoldRecoveryCommand }): Promise<Result<HoldMandateResolution, SdkError>> {
    assertHex64(id, 'mandateId');
    return this.request('POST', `/api/hold-release-mandates/${encodeURIComponent(id)}/reconcile-request`, request);
  }


  // ─── Fiat on-ramp checkout (FiatCheckoutController) ───

  /** Whether this node can take a payment right now, and which config keys are missing if not. */
  async getFiatCheckoutCapabilities(): Promise<Result<HostedSessionCapabilities, SdkError>> {
    return this.request<HostedSessionCapabilities>("GET", API_PATHS.FIAT_CHECKOUT_CAPABILITIES);
  }

  /**
   * Starts (or resumes) a payment that adds funds to `userId`'s account. A
   * first-party login may only pay for itself — `userId` must be the caller's own
   * id unless the principal is a tenant API key with the person's live consent.
   */
  async startFiatCheckout(
    userId: string,
    params: FiatCheckoutStartParams
  ): Promise<Result<FiatCheckoutIntent, SdkError>> {
    assertUuid(userId, "userId");
    assertNonEmpty(params.requestKey, "requestKey");
    return this.request<FiatCheckoutIntent>("POST", API_PATHS.FIAT_CHECKOUT_START(userId), params);
  }

  /**
   * Reads one payment for the person it belongs to — what the return page calls
   * with the `intent` query parameter the rail put on the return URL.
   */
  async getFiatCheckoutIntent(
    userId: string,
    intentId: string
  ): Promise<Result<FiatCheckoutIntent, SdkError>> {
    assertUuid(userId, "userId");
    assertUuid(intentId, "intentId");
    return this.request<FiatCheckoutIntent>("GET", API_PATHS.FIAT_CHECKOUT_GET(userId, intentId));
  }

  // ─── Fiat off-ramp / cash-out (FiatPayoutController) ───
  // The subject is always `userId`; a first-party login may only cash out for
  // itself, and an API-key principal additionally needs that person's live
  // consent, resolved server-side exactly like startFiatCheckout().

  /** What `userId` can cash out today, and why the rest is still held. */
  async getCashOutAvailability(userId: string): Promise<Result<CashOutQuote, SdkError>> {
    assertUuid(userId, "userId");
    return this.request<CashOutQuote>("GET", API_PATHS.FIAT_PAYOUT_AVAILABILITY(userId));
  }

  /** Where `userId`'s money goes, and whether it is ready to receive any. */
  async getPayoutAccountStatus(userId: string): Promise<Result<PayoutAccountStatus, SdkError>> {
    assertUuid(userId, "userId");
    return this.request<PayoutAccountStatus>("GET", API_PATHS.FIAT_PAYOUT_ACCOUNT(userId));
  }

  /**
   * Starts (or resumes) payout-account setup and hands back a fresh hosted
   * link. This is the one moment paperwork is expected, so it happens at the
   * first cash-out rather than at sign-up.
   */
  async beginPayoutAccountSetup(userId: string): Promise<Result<PayoutAccountStatus, SdkError>> {
    assertUuid(userId, "userId");
    return this.request<PayoutAccountStatus>("POST", API_PATHS.FIAT_PAYOUT_ACCOUNT(userId));
  }

  /**
   * Claims a cash-out for `userId` and hands it to the backend's saga.
   * Idempotent on `params.requestKey`: a redelivered request resumes the same
   * cash-out and pays nothing twice.
   */
  async startCashOut(
    userId: string,
    params: CashOutStartParams
  ): Promise<Result<CashOutView, SdkError>> {
    assertUuid(userId, "userId");
    assertNonEmpty(params.requestKey, "requestKey");
    return this.request<CashOutView>("POST", API_PATHS.FIAT_PAYOUT_BASE(userId), params);
  }

  /** `userId`'s own cash-out history, newest first. */
  async listCashOutHistory(
    userId: string,
    limit?: number
  ): Promise<Result<CashOutView[], SdkError>> {
    assertUuid(userId, "userId");
    const base = API_PATHS.FIAT_PAYOUT_BASE(userId);
    return this.request<CashOutView[]>("GET", limit ? `${base}?limit=${limit}` : base);
  }

  // ─── Platform token reserve/genesis read (PlatformTokenController). Operator-scoped. ───

  /** Operator-scoped verified platform issuance and reserve supply. */
  async getPlatformTokenState(): Promise<Result<PlatformTokenState, SdkError>> {
    return this.request<PlatformTokenState>("GET", API_PATHS.PLATFORM_TOKEN_GET);
  }

  /** Requires an actual recent node-operator session; admission does not accept costs or sign. */
  async runPlatformTokenGenesis(request: PlatformGenesisRequest): Promise<Result<PlatformGenesisResponse, SdkError>> {
    return this.request("POST", "/api/admin/platform-token/genesis", request);
  }

  async getPlatformGenesisCreation(id: string): Promise<Result<FungibleCreationView, SdkError>> {
    return this.request("GET", `/api/admin/platform-token/creation/${encodeURIComponent(id)}`);
  }

  async refreshPlatformGenesisCreation(id: string): Promise<Result<FungibleCreationView, SdkError>> {
    return this.request("POST", `/api/admin/platform-token/creation/${encodeURIComponent(id)}/refresh`);
  }

  async quotePlatformGenesisCreation(id: string): Promise<Result<FungibleCreationView, SdkError>> {
    return this.request("POST", `/api/admin/platform-token/creation/${encodeURIComponent(id)}/quote`);
  }

  async acceptPlatformGenesisQuote(id: string, request: { version: number; quoteId: string }): Promise<Result<FungibleCreationView, SdkError>> {
    return this.request("POST", `/api/admin/platform-token/creation/${encodeURIComponent(id)}/accept-quote`, request);
  }

  async continuePlatformGenesisCreation(id: string, request: FungibleContinuationRequest): Promise<Result<FungibleCreationView, SdkError>> {
    return this.request("POST", `/api/admin/platform-token/creation/${encodeURIComponent(id)}/continue`, request);
  }

  async replacePlatformGenesisCreation(id: string, request: FungibleContinuationRequest): Promise<Result<FungibleCreationView, SdkError>> {
    return this.request("POST", `/api/admin/platform-token/creation/${encodeURIComponent(id)}/replace`, request);
  }

  // ─── Project treasury reads (ProjectTreasuryController). Operator-scoped. ───

  /**
   * One project's treasury: the account, its ledger counters, and what the chain
   * says the address actually holds. No SDK method moves units through this
   * surface — crediting and spending belong to the value paths that move money.
   */
  async getProjectTreasury(
    chain: string,
    tenantId: string,
    projectReference: string,
    assetId?: string
  ): Promise<Result<ProjectTreasuryView, SdkError>> {
    assertNonEmpty(chain, "chain");
    assertUuid(tenantId, "tenantId");
    assertNonEmpty(projectReference, "projectReference");
    return this.request<ProjectTreasuryView>(
      "GET",
      API_PATHS.PROJECT_TREASURY_GET(chain, tenantId, projectReference, assetId)
    );
  }

  /** Every treasury one tenant owns, newest first. */
  async listProjectTreasuriesByTenant(
    tenantId: string,
    limit?: number
  ): Promise<Result<ProjectTreasuryLedger[], SdkError>> {
    assertUuid(tenantId, "tenantId");
    const base = API_PATHS.PROJECT_TREASURY_LIST_BY_TENANT(tenantId);
    return this.request<ProjectTreasuryLedger[]>("GET", limit ? `${base}?limit=${limit}` : base);
  }

  // ─── Node transparency reads (NodeTransparencyController) ───
  // Public, anonymous, cacheable. Every call below goes through requestAnonymous(),
  // which strips both Authorization and X-Api-Key even when this client instance
  // is configured with one — what this node discloses about its own policy is the
  // same answer for every viewer. See parity-w10-3.test.ts's "token-less contract"
  // suite, which configures a token AND an api key and asserts neither is sent.

  /** The node's current governance parameters, fee schedule and treasury destinations. */
  async getNodeTransparencySnapshot(): Promise<Result<NodeTransparencySnapshot, SdkError>> {
    return this.requestAnonymous<NodeTransparencySnapshot>("GET", API_PATHS.NODE_TRANSPARENCY_CURRENT);
  }

  /** Public audit trail of governance-parameter changes, newest first. */
  async listNodeGovernanceAudit(
    limit?: number,
    cursor?: string
  ): Promise<Result<NodeTransparencyPage<NodeGovernanceAuditEntry>, SdkError>> {
    return this.requestAnonymous<NodeTransparencyPage<NodeGovernanceAuditEntry>>(
      "GET",
      withAuditQuery(API_PATHS.NODE_TRANSPARENCY_GOVERNANCE_AUDIT, limit, cursor)
    );
  }

  /** Public audit trail of fee-schedule changes, newest first. */
  async listNodeFeeAudit(
    limit?: number,
    cursor?: string
  ): Promise<Result<NodeTransparencyPage<NodeFeeAuditEntry>, SdkError>> {
    return this.requestAnonymous<NodeTransparencyPage<NodeFeeAuditEntry>>(
      "GET",
      withAuditQuery(API_PATHS.NODE_TRANSPARENCY_FEE_AUDIT, limit, cursor)
    );
  }

  /** Public audit trail of treasury-destination changes, newest first. */
  async listNodeTreasuryAudit(
    limit?: number,
    cursor?: string
  ): Promise<Result<NodeTransparencyPage<NodeTreasuryAuditEntry>, SdkError>> {
    return this.requestAnonymous<NodeTransparencyPage<NodeTreasuryAuditEntry>>(
      "GET",
      withAuditQuery(API_PATHS.NODE_TRANSPARENCY_TREASURY_AUDIT, limit, cursor)
    );
  }

  /**
   * The node-identity-signed checkpoint over the bounded audit history above
   * — verify this independently of trusting the node's own HTTPS transport.
   */
  async getNodeTransparencyHistoryCheckpoint(): Promise<
    Result<NodeTransparencyHistoryDocument, SdkError>
  > {
    return this.requestAnonymous<NodeTransparencyHistoryDocument>(
      "GET",
      API_PATHS.NODE_TRANSPARENCY_HISTORY_CHECKPOINT
    );
  }

  // ─── Durable DEX (DexController) ───

  /** Lists structurally complete DEX modules per network; grammar never implies liquidity or readiness. */
  async getDexCapabilities(): Promise<Result<DexCapabilityView[], SdkError>> {
    return this.request("GET", "/api/dex/capabilities");
  }

  /** Reads the governed fee schedule, including the swap flat-fee denomination (NodeGovern). */
  async getNodeFeeSchedule(): Promise<Result<NodeFeeSchedule, SdkError>> {
    return this.request("GET", "/api/node-governance/fee-schedule");
  }

  /** Version-checked fee schedule update; a nonzero swap flat fee needs an explicit denomination before DEX quoting. */
  async updateNodeFeeSchedule(request: NodeFeeScheduleUpdateRequest): Promise<Result<NodeFeeSchedule, SdkError>> {
    return this.request("PUT", "/api/node-governance/fee-schedule", request);
  }

  /** Issues an immutable owner-bound quote; unavailable liquidity is an ordinary result. */
  async quoteDex(request: DexQuoteRequest): Promise<Result<DexQuoteAvailability, SdkError>> {
    return this.request("POST", "/api/dex/quotes", request);
  }

  async getDexQuote(id: string): Promise<Result<DexQuoteView, SdkError>> {
    assertHex32(id, "id", "DEX quote id");
    return this.request("GET", `/api/dex/quotes/${encodeURIComponent(id)}`);
  }

  /** Replays the caller's saved admission key and original quote without generating a new command. */
  async startDexSwap(request: SwapAdmissionRequest): Promise<Result<SwapOperationView, SdkError>> {
    return this.request("POST", "/api/dex/operations", request);
  }

  async getDexSwap(id: string): Promise<Result<SwapOperationView, SdkError>> {
    assertHex64(id, "id", "swap operation id");
    return this.request("GET", `/api/dex/operations/${encodeURIComponent(id)}`);
  }

  async prepareDexSwap(id: string, command: SwapVersionCommand): Promise<Result<SwapOperationView, SdkError>> {
    assertHex64(id, "id", "swap operation id");
    return this.request("POST", `/api/dex/operations/${encodeURIComponent(id)}/prepare`, command);
  }

  async signDexSwap(id: string, command: SwapStageCommand): Promise<Result<SwapOperationView, SdkError>> {
    assertHex64(id, "id", "swap operation id");
    return this.request("POST", `/api/dex/operations/${encodeURIComponent(id)}/sign`, command);
  }

  /** Disclosure is potential egress and requires explicit persisted participant consent. */
  async exposeDexSwap(id: string, command: SwapExposureCommand): Promise<Result<SwapExternalSigningView, SdkError>> {
    assertHex64(id, "id", "swap operation id");
    return this.request("POST", `/api/dex/operations/${encodeURIComponent(id)}/external-signing`, command);
  }

  async acceptDexSwapSignature(id: string, command: SwapBrowserStageCommand): Promise<Result<SwapOperationView, SdkError>> {
    assertHex64(id, "id", "swap operation id");
    return this.request("POST", `/api/dex/operations/${encodeURIComponent(id)}/external-signature`, command);
  }

  async submitDexSwap(id: string, command: SwapStageCommand): Promise<Result<SwapOperationView, SdkError>> {
    assertHex64(id, "id", "swap operation id");
    return this.request("POST", `/api/dex/operations/${encodeURIComponent(id)}/submit`, command);
  }

  async refreshDexSwap(id: string): Promise<Result<SwapOperationView, SdkError>> {
    assertHex64(id, "id", "swap operation id");
    return this.request("POST", `/api/dex/operations/${encodeURIComponent(id)}/refresh`);
  }

  async cancelDexSwap(id: string, command: SwapVersionCommand): Promise<Result<SwapOperationView, SdkError>> {
    assertHex64(id, "id", "swap operation id");
    return this.request("POST", `/api/dex/operations/${encodeURIComponent(id)}/cancel`, command);
  }

  /** Stops future server submission without recalling a send or releasing reserved accounting. */
  async withdrawDexSwapSubmission(id: string, command: SwapStageCommand): Promise<Result<SwapOperationView, SdkError>> {
    assertHex64(id, "id", "swap operation id");
    return this.request("POST", `/api/dex/operations/${encodeURIComponent(id)}/withdraw-submission`, command);
  }

  async beginDexSwapCleanup(id: string, command: SwapCleanupRequest): Promise<Result<SwapOperationView, SdkError>> {
    assertHex64(id, "id", "swap operation id");
    return this.request("POST", `/api/dex/operations/${encodeURIComponent(id)}/cleanup`, command);
  }

  async prepareDexSwapCleanup(id: string, command: SwapCleanupStageCommand): Promise<Result<SwapOperationView, SdkError>> {
    assertHex64(id, "id", "swap operation id");
    return this.request("POST", `/api/dex/operations/${encodeURIComponent(id)}/cleanup/prepare`, command);
  }

  // ─── Wormhole Token Transfer (WttBridgeController) ───

  /** Read-only offer; its request key fixes the operation identity and must be reused as the start Idempotency-Key. */
  async quoteWtt(request: WttQuoteRequest): Promise<Result<WttOfferView, SdkError>> {
    return this.request("POST", "/api/bridge/wtt/quotes", request);
  }

  /** Admits the exact offer once; response loss replays the same operation with the same key. */
  async startWtt(request: WttStartRequest, idempotencyKey: string): Promise<Result<WttOperationView, SdkError>> {
    assertNonEmpty(idempotencyKey, "idempotencyKey");
    return this.request("POST", "/api/bridge/wtt/operations", request, false, { "Idempotency-Key": idempotencyKey });
  }

  /** Server-side discovery that survives cleared browser storage. */
  async findWttByRequestKey(idempotencyKey: string): Promise<Result<WttOperationView, SdkError>> {
    assertNonEmpty(idempotencyKey, "idempotencyKey");
    return this.request("GET", "/api/bridge/wtt/operations/by-request-key", undefined, false, { "Idempotency-Key": idempotencyKey });
  }

  async listWtt(cursor?: string, limit?: number): Promise<Result<WttOperationPage, SdkError>> {
    const query = new URLSearchParams();
    if (cursor) query.set("cursor", cursor);
    if (limit) query.set("limit", String(limit));
    const suffix = query.toString();
    return this.request("GET", `/api/bridge/wtt/operations${suffix ? `?${suffix}` : ""}`);
  }

  async getWtt(id: string): Promise<Result<WttOperationView, SdkError>> {
    return this.request("GET", this.wttOperationPath(id));
  }

  /** Cancels before any source publication by original request key; a tombstone blocks delayed admission. */
  async cancelWtt(request: WttCancelRequest, idempotencyKey: string): Promise<Result<WttOperationView, SdkError>> {
    assertNonEmpty(idempotencyKey, "idempotencyKey");
    return this.request("POST", "/api/bridge/wtt/operations/cancel", request, false, { "Idempotency-Key": idempotencyKey });
  }

  /** Prepares the next stage under current custody and optionally authorizes its first send. */
  async continueWtt(id: string, command: WttContinueCommand): Promise<Result<WttOperationView, SdkError>> {
    return this.request("POST", this.wttOperationPath(id, "/continue"), command);
  }

  async submitWtt(id: string, command: WttStageCommand): Promise<Result<WttOperationView, SdkError>> {
    assertHex64(command.stageId, "stageId", "identity");
    return this.request("POST", this.wttOperationPath(id, "/submit"), command);
  }

  /** Records exposure, then returns the exact unsigned participant message; replays the same message after response loss. */
  async exposeWttParticipant(id: string, command: WttStageCommand): Promise<Result<WttParticipantSigningView, SdkError>> {
    assertHex64(command.stageId, "stageId", "identity");
    return this.request("POST", this.wttOperationPath(id, "/participant"), command);
  }

  async acceptWttParticipantSignature(id: string, command: WttParticipantSignatureCommand): Promise<Result<WttOperationView, SdkError>> {
    assertHex64(command.stageId, "stageId", "identity");
    return this.request("POST", this.wttOperationPath(id, "/participant-signature"), command);
  }

  /** Observes and replays only already-authorized wire; never signs. */
  async refreshWtt(id: string): Promise<Result<WttOperationView, SdkError>> {
    return this.request("POST", this.wttOperationPath(id, "/refresh"));
  }

  async quoteWttNativeOutput(id: string): Promise<Result<WttNativeOutputOffer, SdkError>> {
    return this.request("GET", this.wttOperationPath(id, "/native-output/quote"));
  }

  async startWttNativeOutput(id: string, request: WttNativeOutputRequest): Promise<Result<WttOperationView, SdkError>> {
    return this.request("POST", this.wttOperationPath(id, "/native-output"), request);
  }

  async quoteWttCleanup(id: string, stageId: string): Promise<Result<WttCleanupOffer, SdkError>> {
    assertHex64(stageId, "stageId", "identity");
    return this.request("GET", this.wttOperationPath(id, `/cleanup/${stageId}/quote`));
  }

  /** Fresh direct-owner residual return; never revives the original transfer's authority. */
  async startWttCleanup(id: string, request: WttCleanupRequest): Promise<Result<WttOperationView, SdkError>> {
    assertHex64(request.stageId, "stageId", "identity");
    return this.request("POST", this.wttOperationPath(id, "/cleanup"), request);
  }

  /** WTT operation and stage identities are server-issued 64-character lowercase hex digests. */
  private wttOperationPath(id: string, suffix = ""): string {
    assertHex64(id, "operationId", "identity");
    return `/api/bridge/wtt/operations/${id}${suffix}`;
  }

  // ─── UserNFT ───

  /** Discloses costs for the caller-reviewed replacement generation without signing. */
  async quoteNftReplacementCosts(operationId: string, stage: NftStageAcceptance): Promise<Result<NftProtocolCostOffer, SdkError>> {
    assertHex32(operationId, "operationId", "NFT lifecycle reference");
    return this.request("POST", `/api/nft-replacement-costs/${encodeURIComponent(operationId)}/quote`, stage);
  }

  /** Accepts the exact saved replacement cost request without signing. */
  async acceptNftReplacementCosts(operationId: string, request: NftReplacementCostAcceptance): Promise<Result<NftLifecycleView, SdkError>> {
    assertHex32(operationId, "operationId", "NFT lifecycle reference");
    return this.request("POST", `/api/nft-replacement-costs/${encodeURIComponent(operationId)}/accept`, request);
  }

  /** Reads the saved request outcome before any fresh offer replaces it. */
  async reconcileNftReplacementCosts(operationId: string, request: NftReplacementCostAcceptance): Promise<Result<NftReplacementCostReconciliation, SdkError>> {
    assertHex32(operationId, "operationId", "NFT lifecycle reference");
    return this.request("POST", `/api/nft-replacement-costs/${encodeURIComponent(operationId)}/reconcile`, request);
  }

  /** Atomically retires an unanswered acceptance version, preserving a winning acceptance. */
  async retireNftReplacementCosts(operationId: string, request: NftReplacementCostAcceptance): Promise<Result<NftReplacementCostReconciliation, SdkError>> {
    assertHex32(operationId, "operationId", "NFT lifecycle reference");
    return this.request("POST", `/api/nft-replacement-costs/${encodeURIComponent(operationId)}/retire`, request);
  }

  /** Discloses costs for the original frozen execution without accepting them. */
  async quoteNftWorkflowCosts(executionId: string): Promise<Result<NftWorkflowCostDisclosure, SdkError>> {
    assertUuid(executionId, "executionId");
    return this.request("POST", `/api/nft-workflow-costs/${executionId}/quote`);
  }

  /** Accepts the caller-persisted exact request; replay preserves its original identity. */
  async acceptNftWorkflowCosts(request: NftWorkflowCostAcceptance): Promise<Result<NftLifecycleView, SdkError>> {
    assertUuid(request.executionId, "executionId");
    assertUuid(request.requestId, "requestId");
    return this.request("POST", "/api/nft-workflow-costs/accept", request);
  }

  /** Retires only an uncommitted acceptance; a winning admission remains Applied. */
  async retireNftWorkflowCosts(request: NftWorkflowCostAcceptance): Promise<Result<NftWorkflowCostResolution, SdkError>> {
    assertUuid(request.executionId, "executionId");
    assertUuid(request.requestId, "requestId");
    return this.request("POST", "/api/nft-workflow-costs/retire", request);
  }

  /** Resolves the saved acceptance without authorizing a new mint. */
  async reconcileNftWorkflowCosts(request: NftWorkflowCostAcceptance): Promise<Result<NftWorkflowCostResolution, SdkError>> {
    assertUuid(request.executionId, "executionId");
    assertUuid(request.requestId, "requestId");
    return this.request("POST", "/api/nft-workflow-costs/reconcile", request);
  }

  /** Returns costs for explicit consent without admitting or signing the mint. */
  async quoteUserNftMintCosts(params: UserNFTMintParams, idempotencyKey: string): Promise<Result<NftProtocolCostOffer, SdkError>> {
    assertNonEmpty(idempotencyKey, "idempotencyKey");
    return this.request("POST", "/api/usernft/mint-cost-quote", params, false, { "Idempotency-Key": idempotencyKey });
  }

  async mintUserNFT(params: UserNFTMintParams, idempotencyKey: string): Promise<Result<NftLifecycleView, SdkError>> {
    return this.request("POST", "/api/usernft/mint", params, false, { "Idempotency-Key": idempotencyKey });
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

  async transferUserNFT(id: string, recipientAddress: string, idempotencyKey: string, feeAuthorization?: NftFeeAuthorization): Promise<Result<NftLifecycleView, SdkError>> {
    assertUuid(id, "userNFTId");
    return this.request("POST", `/api/usernft/${id}/transfer`, { recipientAddress, feeAuthorization }, false, { "Idempotency-Key": idempotencyKey });
  }

  async burnUserNFT(id: string, idempotencyKey: string, feeAuthorization?: NftFeeAuthorization): Promise<Result<NftLifecycleView, SdkError>> {
    assertUuid(id, "userNFTId");
    const query = feeAuthorization ? `?feeRevision=${feeAuthorization.revision}&maxNativeFeeBaseUnits=${encodeURIComponent(feeAuthorization.maxNativeBaseUnits)}&feeQuoteId=${encodeURIComponent(feeAuthorization.quoteId)}` : "";
    return this.request("DELETE", `/api/usernft/${id}${query}`, undefined, false, { "Idempotency-Key": idempotencyKey });
  }

  async getNftLifecycleByRequest(operation: NftOperation, requestKey: string): Promise<Result<NftLifecycleView, SdkError>> {
    return this.request("GET", `/api/usernft/lifecycle/request?${new URLSearchParams({ operation, requestKey })}`);
  }

  async cancelNftRequest(operation: NftOperation, requestKey: string): Promise<Result<NftLifecycleView, SdkError>> {
    return this.request("POST", "/api/usernft/lifecycle/request/cancel", { operation, requestKey });
  }

  async listNftLifecycles(cursor?: string, limit = 25): Promise<Result<NftLifecyclePage, SdkError>> {
    const query = new URLSearchParams({ limit: String(limit) });
    if (cursor) query.set("cursor", cursor);
    return this.request("GET", `/api/usernft/lifecycle?${query}`);
  }

  async getNftFeeQuote(chainType: string, operation: NftOperation, context?: { nftId?: string; sourceOperationId?: string }): Promise<Result<NftFeeQuote, SdkError>> {
    const query = new URLSearchParams({ chainType, operation });
    if (context?.nftId) query.set("nftId", context.nftId);
    if (context?.sourceOperationId) query.set("sourceOperationId", context.sourceOperationId);
    return this.request("GET", `/api/usernft/fee-quote?${query}`);
  }

  async refundNftTransfer(operationId: string, idempotencyKey: string, feeAuthorization?: NftFeeAuthorization): Promise<Result<NftLifecycleView, SdkError>> {
    assertHex32(operationId, "operationId", "NFT lifecycle reference");
    return this.request("POST", `/api/usernft/lifecycle/${encodeURIComponent(operationId)}/refund`, { feeAuthorization }, false, { "Idempotency-Key": idempotencyKey });
  }

  async getNftLifecycle(operationId: string): Promise<Result<NftLifecycleView, SdkError>> {
    assertHex32(operationId, "operationId", "NFT lifecycle reference");
    return this.request("GET", `/api/UserNFT/lifecycle/${encodeURIComponent(operationId)}`);
  }

  /** Returns costs for explicit consent without admitting or signing the mint. */
  async quoteCredentialIssueCosts(recipientId: string, params: UserNFTMintParams, idempotencyKey: string): Promise<Result<NftProtocolCostOffer, SdkError>> {
    assertNonEmpty(idempotencyKey, "idempotencyKey");
    return this.request("POST", `/api/Credential/issue/${encodeURIComponent(recipientId)}/cost-quote`, params, false, { "Idempotency-Key": idempotencyKey });
  }

  async issueCredential(recipientId: string, params: UserNFTMintParams, idempotencyKey: string): Promise<Result<NftLifecycleView, SdkError>> {
    assertUuid(recipientId, "recipientId");
    return this.request("POST", `/api/Credential/issue/${encodeURIComponent(recipientId)}`, params, false, { "Idempotency-Key": idempotencyKey });
  }

  async refreshNftLifecycle(operationId: string): Promise<Result<NftLifecycleView, SdkError>> {
    assertHex32(operationId, "operationId", "NFT lifecycle reference");
    return this.request("POST", `/api/usernft/lifecycle/${encodeURIComponent(operationId)}/refresh`);
  }

  async acceptNftLifecycle(operationId: string, acceptance: NftStageAcceptance): Promise<Result<NftLifecycleView, SdkError>> {
    assertHex32(operationId, "operationId", "NFT lifecycle reference");
    return this.request("POST", `/api/usernft/lifecycle/${encodeURIComponent(operationId)}/accept`, acceptance);
  }

  /** Records the original requester's fresh authority for the exact reviewed stage; it never signs or prepares. */
  async renewNftLifecycleAuthority(operationId: string, request: NftStageAcceptance): Promise<Result<NftLifecycleView, SdkError>> {
    assertHex32(operationId, "operationId", "NFT lifecycle reference");
    return this.request("POST", `/api/usernft/lifecycle/${encodeURIComponent(operationId)}/renew-authority`, request);
  }

  async cancelNftLifecycle(operationId: string): Promise<Result<NftLifecycleView, SdkError>> {
    assertHex32(operationId, "operationId", "NFT lifecycle reference");
    return this.request("POST", `/api/usernft/lifecycle/${encodeURIComponent(operationId)}/cancel`);
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

  async verifyNFTOwnership(params: { chainType: string; nftContractAddress: string; tokenId: string }): Promise<Result<boolean, SdkError>> {
    return this.request("POST", "/api/usernft/verify-ownership", params);
  }

  async verifyHolonAccess(params: { userNFTId: string; holonId: string; requiredPermission?: string }): Promise<Result<boolean, SdkError>> {
    return this.request("POST", "/api/usernft/verify-holon-access", params);
  }

  async verifyWalletAccess(params: { userNFTId: string; walletId: string; requiredAccess?: string }): Promise<Result<boolean, SdkError>> {
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
  async executeAction(actionId: string, options: StartWorkflowOptions): Promise<Result<ActionRunResult, SdkError>> {
    assertUuid(actionId, "actionId");
    return this.request("POST", `/api/action/${actionId}/execute${workflowStartQuery(options)}`, undefined, false, {
      "Idempotency-Key": options.idempotencyKey,
      ...(options.authToken ? { Authorization: `Bearer ${options.authToken}` } : {}),
    });
  }

  /** Admit a single published node; its runId identifies durable execution and recovery. */
  async executeActionNode(actionId: string, nodeId: string): Promise<Result<WorkflowNodeExecution, SdkError>> {
    assertUuid(actionId, "actionId");
    assertUuid(nodeId, "nodeId");
    return this.request("POST", `/api/action/${actionId}/nodes/${nodeId}/execute`);
  }

  /** Read the current durable outcome; admission does not imply completion. */
  async getActionRun(runId: string): Promise<Result<ActionRunResult, SdkError>> {
    assertUuid(runId, "runId");
    return this.request("GET", `/api/action/runs/${runId}`);
  }

  /** Read sanitized node outcomes for a durable run. */
  async getActionExecutionState(runId: string): Promise<Result<WorkflowExecutionState, SdkError>> {
    assertUuid(runId, "runId");
    return this.request("GET", `/api/action/runs/${runId}/execution-state`);
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
  // ─── Round 4 contracts (plan §4.9): KYC, wallet proof, client signing, operator exits ───

  /** Begins or resumes the caller's own hosted identity verification (first-party login only). */
  async startKycSession(): Promise<Result<KycSessionStart, SdkError>> {
    return this.request("POST", API_PATHS.KYC_SESSION, {});
  }

  /** The caller's current verification; absent before the first session. */
  async getKycStatus(): Promise<Result<KycSubmission, SdkError>> {
    return this.request("GET", API_PATHS.KYC_STATUS);
  }

  /** Re-fetches the provider's authoritative result for the caller's open attempt, then returns status. */
  async refreshKyc(): Promise<Result<KycSubmission, SdkError>> {
    return this.request("POST", API_PATHS.KYC_REFRESH, {});
  }

  /** Issues the exact message the caller's external wallet signs to prove it controls `walletId`. */
  async createWalletOwnershipChallenge(walletId: string): Promise<Result<WalletOwnershipChallenge, SdkError>> {
    assertUuid(walletId, "walletId");
    return this.request("POST", API_PATHS.WALLET_OWNERSHIP_CHALLENGE(walletId), {});
  }

  /** Submits the signature over the challenge message, verbatim; the server records the proof flag. */
  async proveWalletOwnership(walletId: string, signature: string, message?: string): Promise<Result<WalletOwnershipProof, SdkError>> {
    assertUuid(walletId, "walletId");
    assertNonEmpty(signature, "signature");
    return this.request("POST", API_PATHS.WALLET_OWNERSHIP_PROOF(walletId), { signature, message });
  }

  /** D-3: the exact unsigned bytes the external wallet must sign for an `AwaitingSignature` operation. */
  async getOperationSigningPlan(operationId: string): Promise<Result<ClientSigningPlanView, SdkError>> {
    assertUuid(operationId, "operationId");
    return this.request("GET", API_PATHS.BLOCKCHAIN_OPERATION_SIGNING_PLAN(operationId));
  }

  /** D-3: submits the signed plan; the node verifies the exact bytes, broadcasts and reconciles. Re-read status, never assume success. */
  async submitSignedOperation(operationId: string, submission: ClientSignedSubmission): Promise<Result<BlockchainOperationResult, SdkError>> {
    assertUuid(operationId, "operationId");
    assertNonEmpty(submission.signedTransactionBase64, "signedTransactionBase64");
    return this.request("POST", API_PATHS.BLOCKCHAIN_OPERATION_SUBMIT_SIGNED(operationId), submission);
  }

  /** Operator: client-signed operations unsigned past the TTL or parked as `NeedsOperator`. */
  async listStuckClientSignedOperations(limit = 50): Promise<Result<StuckClientSignedOperationView[], SdkError>> {
    return this.request("GET", `${API_PATHS.OPERATOR_STUCK_OPERATIONS}?limit=${clampLimit(limit, 200)}`);
  }

  /** Operator: resolves a stuck client-signed operation; the outcome is re-proven from chain state. */
  async resolveStuckClientSignedOperation(operationId: string, resolution: ClientSigningResolution): Promise<Result<BlockchainOperationResult, SdkError>> {
    assertUuid(operationId, "operationId");
    assertNonEmpty(resolution.reason, "reason");
    return this.request("POST", API_PATHS.OPERATOR_OPERATION_RESOLVE(operationId), resolution);
  }

  /** Operator: parked deployment operations, oldest first. */
  async listParkedDeployments(limit = 50): Promise<Result<ParkedDeployment[], SdkError>> {
    return this.request("GET", `${API_PATHS.OPERATOR_DEPLOYMENTS}?limit=${clampLimit(limit, 100)}`);
  }

  /** Operator: re-observe or close a parked deployment at its exact version (409 on refusal). */
  async resolveParkedDeployment(operationId: string, resolution: DeploymentOperatorResolution): Promise<Result<DeploymentReceipt, SdkError>> {
    assertHex32(operationId, "operationId", "deployment operation id");
    return this.request("POST", API_PATHS.OPERATOR_DEPLOYMENT_RESOLVE(operationId), resolution);
  }

  /** Operator: audited record-paid / return-units exit for a parked or reversed cash-out. */
  async resolveFiatPayout(payoutId: string, request: FiatPayoutResolveRequest): Promise<Result<FiatPayoutView, SdkError>> {
    assertNonEmpty(payoutId, "payoutId");
    assertNonEmpty(request.reason, "reason");
    return this.request("POST", API_PATHS.FIAT_PAYOUT_RESOLVE(payoutId), request);
  }

  /** Operator: settles a reversal parked on an ambiguous move from chain evidence and re-queues the unwind. */
  async resolveFiatReversal(reversalId: string, request: FiatReversalResolveRequest): Promise<Result<FiatReversalView, SdkError>> {
    assertNonEmpty(reversalId, "reversalId");
    assertNonEmpty(request.reason, "reason");
    return this.request("POST", API_PATHS.FIAT_REVERSAL_RESOLVE(reversalId), request);
  }

  /** Tenant: the payout float tenant-priced cash-outs draw on (D-8). */
  async getTenantPayoutBalance(): Promise<Result<TenantPayoutBalance, SdkError>> {
    return this.request("GET", API_PATHS.TENANT_PAYOUT_BALANCE);
  }

  /** Tenant: starts a top-up payment; it credits the float only after settlement and aging. */
  async startTenantPayoutTopUp(params: TenantPayoutTopUpParams): Promise<Result<TenantPayoutTopUp, SdkError>> {
    assertNonEmpty(params.requestKey, "requestKey");
    return this.request("POST", API_PATHS.TENANT_PAYOUT_TOP_UP, params);
  }

  /** Operator: one tenant float with its consistency check. */
  async getTenantPayoutFloat(tenantId: string): Promise<Result<TenantPayoutFloatView, SdkError>> {
    assertUuid(tenantId, "tenantId");
    return this.request("GET", API_PATHS.TENANT_PAYOUT_FLOAT(tenantId));
  }

  /** Custody liabilities visible to the caller. */
  async listHoldLiabilities(offset = 0, limit = 25): Promise<Result<HoldLiabilityPage, SdkError>> {
    return this.request("GET", `${API_PATHS.HOLD_LIABILITIES}?offset=${Math.max(0, Math.trunc(offset))}&limit=${clampLimit(limit, 100)}`);
  }

  /** One liability; not-found and not-visible are indistinguishable. */
  async getHoldLiability(holdRecordId: string): Promise<Result<HoldLiabilityView, SdkError>> {
    assertHex64(holdRecordId, "holdRecordId", "hold record id");
    return this.request("GET", API_PATHS.HOLD_LIABILITY(holdRecordId));
  }

  /** Reserve assets the caller may open a hold against, with the exact identity the intent must name. */
  async listHoldReserveAssets(): Promise<Result<HoldReserveAssetView[], SdkError>> {
    return this.request("GET", API_PATHS.HOLD_RESERVE_ASSETS);
  }

  /** Operator: retired exchange rows awaiting an audited ledger attestation. */
  async listRetiredExchangeRows(limit = 50): Promise<Result<RetiredExchangeRowView[], SdkError>> {
    return this.request("GET", `${API_PATHS.EXCHANGE_RETIRED_ROWS}?limit=${clampLimit(limit, 100)}`);
  }

  /** Operator: attests a retired row chain outcome; applied only when reconciliation agrees. */
  async resolveRetiredExchangeRow(operationId: string, resolution: RetiredExchangeResolution): Promise<Result<RetiredExchangeRowView, SdkError>> {
    assertUuid(operationId, "operationId");
    assertNonEmpty(resolution.reason, "reason");
    return this.request("POST", API_PATHS.EXCHANGE_RETIRED_ROW_RESOLVE(operationId), resolution);
  }

  /** Operator: wallet-identity migration status and conflicts. */
  async getWalletIdentityMigration(): Promise<Result<WalletIdentityMigrationView, SdkError>> {
    return this.request("GET", API_PATHS.OPERATOR_WALLET_IDENTITIES);
  }

  /** Operator: clears one conflicting wallet binding at the observed migration version. */
  async clearWalletIdentityConflict(repair: WalletIdentityConflictRepair): Promise<Result<WalletIdentityMigrationView, SdkError>> {
    assertNonEmpty(repair.reason, "reason");
    return this.request("POST", API_PATHS.OPERATOR_WALLET_IDENTITY_CLEAR, repair);
  }

  /** Operator: settles a swap parked for review at its exact version. */
  async settleDexOperatorReview(operationId: string, request: SwapOperatorSettlementRequest): Promise<Result<SwapOperationView, SdkError>> {
    assertHex64(operationId, "operationId", "swap operation id");
    assertNonEmpty(request.reason, "reason");
    return this.request("POST", API_PATHS.OPERATOR_DEX_SETTLE_REVIEW(operationId), request);
  }

  /** Operator: inspects one WTT operation. */
  async inspectWttOperation(operationId: string): Promise<Result<WttOperationView, SdkError>> {
    assertHex64(operationId, "operationId", "identity");
    return this.request("GET", API_PATHS.OPERATOR_WTT_OPERATION(operationId));
  }

  /** Operator: reconcile, re-fetch the guardian proof, or resume proof verification (each re-proven server-side). */
  async runWttOperatorAction(operationId: string, action: "reconcile" | "refetch-proof" | "resume-proof"): Promise<Result<WttOperationView, SdkError>> {
    assertHex64(operationId, "operationId", "identity");
    return this.request("POST", API_PATHS.OPERATOR_WTT_ACTION(operationId, action), {});
  }

  /** Operator: live WTT stage-key keyring readiness. */
  async getWttStageKeyReadiness(): Promise<Result<WttStageKeyReadinessReport, SdkError>> {
    return this.request("GET", API_PATHS.OPERATOR_WTT_STAGE_KEY_READINESS);
  }

  /** Operator: one bounded rewrap pass onto the active stage key. */
  async rewrapWttStageKeys(): Promise<Result<WttStageKeyRewrapReport, SdkError>> {
    return this.request("POST", API_PATHS.OPERATOR_WTT_STAGE_KEY_REWRAP, {});
  }

  /** Operator: re-probes every parked run. Read `scanned`; zero scanned or any `errors` is not a success. */
  async reconcileParkedRuns(): Promise<Result<WorkflowReconciliationSweep, SdkError>> {
    return this.request("POST", API_PATHS.ACTION_RUN_RECONCILE_SWEEP);
  }

  async request<T>(
    method: string,
    path: string,
    body?: unknown,
    _retried: boolean = false,
    extraHeaders?: Record<string, string>,
    anonymous: boolean = false
  ): Promise<Result<T, SdkError>> {
    this.logRequest(method, path, body);
    try {
      const resp = await this.fetchWithAuth(method, path, body, extraHeaders, anonymous);

      // An anonymous call carries no token to refresh, so a 401 falls straight
      // through to the plain !resp.ok branch below instead of this retry.
      if (!anonymous && resp.status === 401 && this.config.onTokenRefresh && !_retried) {
        try {
          await this.getOrRefreshToken(true);
        } catch (refreshErr) {
          return this.handleFetchError(method, path, refreshErr);
        }
        return this.request(method, path, body, true, extraHeaders, anonymous);
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
          code: data.code,
        }));
      }

      this.logResponse(method, path, resp.status);
      return ok(data.result as T);
    } catch (e) {
      return this.handleFetchError(method, path, e);
    }
  }

  /**
   * Like {@link request}, but never attaches a bearer token or API key, even
   * when this client is configured with one — for genuinely anonymous routes
   * such as NodeTransparencyController's `[AllowAnonymous]` reads (OI-70: a
   * doc-comment asserted this and nothing enforced it).
   */
  async requestAnonymous<T>(method: string, path: string): Promise<Result<T, SdkError>> {
    return this.request<T>(method, path, undefined, false, undefined, true);
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
  ): Promise<{ message?: string; detail?: SdkErrorDetail; code?: string }> {
    try {
      const body = (await resp.json()) as {
        message?: string;
        error?: string;
        detail?: SdkErrorDetail;
        code?: string;
      };
      return { message: body.message ?? body.error, detail: body.detail, code: typeof body.code === "string" ? body.code : undefined };
    } catch {
      return {}; // non-JSON / empty body (e.g. a bare 500 with no body)
    }
  }

  private apiError(
    method: string,
    path: string,
    status: number,
    parsed: { message?: string; detail?: SdkErrorDetail; code?: string }
  ): SdkError {
    const message = parsed.message
      ? `${method} ${path}: ${parsed.message}`
      : `${method} ${path} failed with HTTP ${status}`;
    return new SdkError(SdkErrorCode.API_ERROR, message, {
      status,
      method,
      path,
      detail: parsed.detail,
      serverCode: parsed.code,
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
    extraHeaders?: Record<string, string>,
    anonymous: boolean = false
  ): Promise<Response> {
    let token = anonymous ? undefined : this.config.token;
    if (!anonymous && !token && this.config.onTokenRefresh) {
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
    // `anonymous` routes (e.g. node transparency) never attach either credential,
    // even when this client instance is configured with a token or API key.
    if (!anonymous) {
      if (token) {
        headers["Authorization"] = `Bearer ${token}`;
      } else if (this.config.apiKey) {
        headers["X-Api-Key"] = this.config.apiKey;
      }
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
  const SECRET_RE = /pass(word)?|token|secret|api[-_]?key|mnemonic|privatekey|seed|signature/i;
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
    out[k] = SECRET_RE.test(k) ? "«redacted»" : v;
  }
  return out;
}
