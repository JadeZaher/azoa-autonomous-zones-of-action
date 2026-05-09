// Core
export { OasisWallet } from "./wallet.js";
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
export { OasisApiClient } from "./api/index.js";
export type { OasisApiConfig } from "./api/index.js";

// High-level client
export { OasisClient } from "./client/index.js";
export type { OasisClientConfig } from "./client/index.js";
export { SessionManager, MemorySessionStorage } from "./client/index.js";
export type { SessionStorage, SessionState } from "./client/index.js";
export { HolonQueryBuilder } from "./client/index.js";
export type { HolonQueryParams, HolonResult } from "./client/index.js";
export { OasisAuthProvider } from "./client/index.js";
export type { AuthProfile } from "./client/index.js";
export { PortfolioAggregator } from "./client/index.js";
export type { ChainBalance, PortfolioSummary } from "./client/index.js";
