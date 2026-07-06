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
  isQuestConflict,
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
export { AzoaApiClient } from "./api/index.js";
export type {
  AzoaApiConfig,
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
  // Quest + Holon AssetType registry (final-hardening Phase C)
  QuestResult,
  HolonTypeResult,
  HolonTypeRegisterParams,
  // Admin surfaces: saga operator dead-letters + key rotation (final-hardening Wave6)
  SagaStepStatus,
  SagaDeadLetterListParams,
  SagaStepView,
  SagaStepRequeueResult,
  SagaStepCancelResult,
  KeyRotationReport,
  KeyRotationParams,
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

// Workflow SDK (workflow-sdk) — template authoring + the fluent quest() run driver
export {
  WorkflowClient,
  WorkflowRunHandle,
  createQuestFactory,
  nodeConfig,
  isAwaiting,
  isTerminal,
  AWAITING_STATUSES,
  TERMINAL_STATUSES,
} from "./workflow/index.js";
export type {
  QuestFactory,
  SuspendCallback,
  WorkflowRunStatus,
  WorkflowRunResult,
  WorkflowNodeExecution,
  WorkflowExecutionState,
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
