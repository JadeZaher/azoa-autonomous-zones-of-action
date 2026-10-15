// Core
export { AzoaWallet } from "./wallet.js";
export {
  ok,
  err,
  isOk,
  isErr,
  unwrap,
  map,
  mapErr,
  SdkError,
  SdkErrorCode,
  isActionConflict,
  toHex,
  fromHex,
  concatBytes,
  equalsBytes,
  withRetry,
  base64Encode,
  base64Decode,
  base58Encode,
  base58Decode,
  base32Encode,
  base32Decode,
  getRandomBytes,
  getPlatform,
} from "./core/index.js";

export type {
  Result,
  SdkErrorDetail,
  SdkErrorOptions,
  Signer,
  ChainNetwork,
  UnsignedTransaction,
  TransactionResult,
  BalanceInfo,
  AssetInfo,
  ChainProvider,
  ChainProviderConfig,
  TransferParams,
  MintParams,
  BurnParams,
  SwapQuote,
  SwapRouteStep,
  SwapParams,
  DexAdapter,
  ChainProviderRegistration,
  RetryOptions,
} from "./core/index.js";

// Chain providers (re-exported for convenience)
export { AlgorandProvider } from "./algorand/index.js";
export type { AlgorandProviderConfig } from "./algorand/index.js";
export { SolanaProvider } from "./solana/index.js";
export type { SolanaProviderConfig } from "./solana/index.js";

// DEX adapters
export { TinymanAdapter, JupiterAdapter } from "./dex/index.js";
export type { TinymanConfig, AlgodClientConfig } from "./dex/tinyman.js";
export type { JupiterConfig } from "./dex/jupiter.js";

// API client
export { AzoaApiClient, DAPP_ROLES, isCredentialRevocationPending } from "./api/index.js";
export type {
  AzoaApiConfig,
  // user-dapp-rbac: assignable DApp role allowlist + user DTO
  UserResponse,
  DappRole,
  TenantKycStatus,
  TenantCustodialAccountStatus,
  TenantCustodialCapabilities,
  TenantKycSession,
  TenantKycDocumentReference,
  TenantKycSubmission,
  NftQueryParams,
  SwapQuoteParams,
  SwapExecuteParams,
  SwapQuoteResponse,
  // Fungible mint + render-model portfolio (fungible-mint-and-render-model)
  FungibleMintParams,
  FungibleTokenResult,
  PortfolioResult,
  PortfolioAsset,
  PortfolioAssetKind,
  NftHolding,
  // Action + Holon AssetType registry (final-hardening Phase C)
  ActionResult,
  // Action invitations + request/approval (action-invitations-approval)
  ActionRunAccess,
  ActionAccessRequestStatus,
  ActionAccessRequest,
  SetRunAccessRequest,
  InviteUserRequest,
  RequestAccessBody,
  DecideAccessRequestBody,
  HolonTypeResult,
  HolonTypeRegisterParams,
  // ApiKey scope discovery (dapp-developer-experience audit)
  SelfIssuableApiKeyScope,
  SelfIssuableApiKeyScopeInfo,
  ApiKeyScope,
  ApiKeyScopeInfo,
  // DappSeries CRUD (DappSeriesController)
  DappSeriesStatus,
  DappSeriesResult,
  DappSeriesCreateParams,
  DappSeriesUpdateParams,
  DappSeriesActionResult,
  DappSeriesAddActionParams,
  // Admin surfaces: saga operator dead-letters + key rotation (final-hardening Wave6)
  SagaStepStatus,
  SagaDeadLetterListParams,
  SagaStepView,
  SagaStepRequeueResult,
  SagaStepCancelResult,
  KeyRotationReport,
  KeyRotationParams,
  // Credential revocation (CredentialController, D19) — parity only; rail not enabled server-side
  CredentialRevokeParams,
  CredentialRevokeStatus,
  CredentialRevokeResult,
  // Allocation (AllocationController, D4/D47)
  AllocationRequest,
  AllocationResult,
  // Exchange (ExchangeController, W5.1/D50)
  ExchangeDirection,
  DeclaredExchangeRate,
  ExchangeParams,
  ExchangeReceiptState,
  ExchangeReceipt,
  // Hold rails (HoldController, D14 / W3.4)
  HoldState,
  HoldOpenParams,
  HoldSettlementParams,
  HoldChainParams,
  HoldView,
  // Fiat on-ramp checkout (FiatCheckoutController)
  FiatCheckoutStatus,
  FiatSettlementState,
  FiatCheckoutPurpose,
  HostedSessionCapabilities,
  FiatCheckoutStartParams,
  FiatCheckoutIntent,
  // Fiat off-ramp / cash-out (FiatPayoutController)
  CashOutQuote,
  PayoutAccountStatus,
  CashOutStartParams,
  CashOutView,
  // Platform token reserve/genesis read (PlatformTokenController). Operator-scoped.
  PlatformTokenDefinition,
  PlatformTokenSupply,
  PlatformTokenState,
  // Project treasury reads (ProjectTreasuryController). Operator-scoped.
  ProjectTreasuryAccount,
  ProjectTreasuryAsset,
  ProjectTreasuryLedger,
  ProjectTreasuryView,
  // Node transparency reads (NodeTransparencyController). Public, anonymous.
  NodeChainNetwork,
  NodeFeeScheduleEntry,
  NodeFeeSchedule,
  NodeGovernanceParameters,
  NodeTreasuryDestination,
  NodeTransparencySnapshot,
  NodeTransparencyPage,
  NodeGovernanceAuditEntry,
  NodeFeeAuditEntry,
  NodeTreasuryAuditEntry,
  NodePublicKey,
  NodePreviousKeyContinuity,
  NodeDescriptor,
  NodeTransparencyHistoryCheckpoint,
  NodeTransparencyHistoryEntry,
  NodeTransparencyHistoryDocument,
  // Fiat reversal types (FiatReversalOperatorController). Types only, no SDK methods.
  FiatReversalKind,
  FiatReversalStatus,
  FiatReversalRecoveryPath,
  FiatReversalView,
  // Analytics read surface (AnalyticsController, D16)
  AnalyticsQueryParams,
  PricingStatus,
  QuantityStatus,
  ValueStampResponse,
  TransactionHistoryEntry,
  TransactionHistoryPage,
  FiatValueResponse,
  PositionSummary,
  PositionsResponse,
} from "./api/index.js";

// High-level client
export { AzoaClient } from "./client/index.js";
export type { AzoaClientConfig } from "./client/index.js";
export { SessionManager, MemorySessionStorage } from "./client/index.js";
export type { SessionStorage, SessionState } from "./client/index.js";
export { HolonQueryBuilder } from "./client/index.js";
export type { HolonQueryParams, HolonResult } from "./client/index.js";
export { AzoaAuthProvider } from "./client/index.js";
export type { AuthProfile } from "./client/index.js";
export { PortfolioAggregator } from "./client/index.js";
export type { ChainBalance, PortfolioSummary } from "./client/index.js";

// Workflow SDK (workflow-sdk) — template authoring + the fluent action() run driver
export {
  WorkflowClient,
  WorkflowRunHandle,
  createActionFactory,
  nodeConfig,
  isAwaiting,
  isTerminal,
  AWAITING_STATUSES,
  TERMINAL_STATUSES,
} from "./workflow/index.js";
export type {
  ActionFactory,
  SuspendCallback,
  WorkflowRunStatus,
  WorkflowRunResult,
  WorkflowNodeExecution,
  WorkflowExecutionState,
  WorkflowReconciliationResult,
  AdvanceParams,
  SignalParams,
  StartRunParams,
  AdvanceOptions,
  ChildCredentialResult,
  GateCheckConfig,
  EmitConfig,
  SwapConfig,
  GrantConfig,
  TransferConfig,
  RefundConfig,
  NftMintRequestParams,
  NftTransferRequestParams,
} from "./workflow/index.js";
