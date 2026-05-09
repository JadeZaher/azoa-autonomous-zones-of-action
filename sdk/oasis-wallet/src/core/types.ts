import type { Result } from "./result.js";
import type { SdkError } from "./errors.js";

// ─── Signer ───
// Provided by the consumer: a browser wallet adapter, a KMS, or a raw keypair.
// The SDK never touches private keys directly.
export interface Signer {
  sign(message: Uint8Array): Promise<Uint8Array>;
  publicKey: Uint8Array;
}

// ─── Network ───
export type ChainNetwork = "devnet" | "testnet" | "mainnet";

// ─── Transaction lifecycle ───
export interface UnsignedTransaction {
  chain: string;
  bytes: Uint8Array;
  /**
   * Format of the bytes field:
   * - "json-descriptor": JSON-encoded tx params (needs wallet adapter to re-encode as native bytes)
   * - "native": chain-native binary format (ready for direct signing)
   * - "base64": base64-encoded native bytes (from DEX APIs like Jupiter)
   */
  format: "json-descriptor" | "native" | "base64";
  /** Human-readable description for wallet approval UIs */
  description?: string;
  /** For atomic groups (Algorand) or versioned txs (Solana) */
  group?: Uint8Array[];
}

export interface TransactionResult {
  txHash: string;
  chain: string;
  status: "submitted" | "confirmed" | "failed";
  raw?: unknown;
}

// ─── Normalized balance / asset ───
export interface BalanceInfo {
  amount: string;
  decimals: number;
  symbol: string;
  raw?: unknown;
}

export interface AssetInfo {
  id: string;
  name: string;
  symbol: string;
  amount: string;
  decimals: number;
  raw?: unknown;
}

// ─── Chain Provider Interface ───
// This is the contract every chain must implement.
// It mirrors the .NET BaseBlockchainProvider / IBlockchainProvider.
// To add a new chain: implement this interface in sdk/oasis-wallet/src/<chain>/provider.ts
// and implement IBlockchainProvider in Providers/Blockchain/<Chain>/<Chain>Provider.cs

export interface ChainProviderConfig {
  rpcUrl: string;
  network: ChainNetwork;
  [key: string]: unknown;
}

export interface ChainProvider {
  /** Unique chain identifier, e.g. "algorand", "solana", "ethereum" */
  readonly chainId: string;
  /** Display name */
  readonly displayName: string;
  /** Whether this provider supports DEX swaps */
  readonly supportsDex: boolean;
  /** Whether this provider supports cross-chain bridging */
  readonly supportsBridging: boolean;

  // ─── Queries (read-only, no signing needed) ───
  // Mirrors: GetBalanceAsync, ValidateAddressAsync, GetTokensByOwnerAsync,
  //          GetTransactionStatusAsync, GetTokenMetadataAsync, GetChainInfoAsync
  getBalance(address: string, tokenId?: string): Promise<Result<BalanceInfo, SdkError>>;
  validateAddress(address: string): Promise<Result<boolean, SdkError>>;
  getAssets(address: string): Promise<Result<AssetInfo[], SdkError>>;
  getTransactionStatus(txHash: string): Promise<Result<TransactionResult, SdkError>>;
  getTokenMetadata(tokenId: string): Promise<Result<Record<string, unknown>, SdkError>>;
  getChainInfo(): Promise<Result<Record<string, unknown>, SdkError>>;

  // ─── Transaction building (returns unsigned bytes or json-descriptors) ───
  // Mirrors: MintAsync, BurnAsync, TransferAsync
  buildTransfer(params: TransferParams): Promise<Result<UnsignedTransaction, SdkError>>;
  buildMint(params: MintParams): Promise<Result<UnsignedTransaction, SdkError>>;
  buildBurn(params: BurnParams): Promise<Result<UnsignedTransaction, SdkError>>;

  // ─── Signing + submission ───
  signTransaction(tx: UnsignedTransaction, signer: Signer): Promise<Result<Uint8Array, SdkError>>;
  submitTransaction(signedTx: Uint8Array): Promise<Result<TransactionResult, SdkError>>;

  // ─── Bridge primitives (server-side via API, but providers can override) ───
  // Mirrors: LockForBridgeAsync, MintWrappedAsync, BurnWrappedAsync, VerifyBridgeProofAsync
  // These are intentionally optional — bridging is typically handled server-side
  // via OasisApiClient.bridge.*, but providers CAN implement client-side bridge logic.

  // ─── Contract operations (optional, not all chains support) ───
  // Mirrors: DeployContractAsync, CallContractAsync
  // These are excluded from the base interface — chains that support them
  // expose chain-specific methods via provider.algorand.deployApp() etc.
}

// ─── Operation params (chain-agnostic) ───

export interface TransferParams {
  from: string;
  to: string;
  amount: string;
  tokenId?: string;
  memo?: string;
}

export interface MintParams {
  name: string;
  symbol: string;
  totalSupply: string;
  decimals: number;
  creator: string;
  metadataUri?: string;
}

export interface BurnParams {
  tokenId: string;
  amount: string;
  owner: string;
}

// ─── DEX Adapter Interface ───
// Each chain that supports swaps provides one. Mirrors the pattern of
// hooking DEX adapters to providers.

export interface SwapQuote {
  chain: string;
  tokenIn: string;
  tokenOut: string;
  amountIn: string;
  expectedAmountOut: string;
  priceImpact: number;
  fee: string;
  route?: SwapRouteStep[];
  raw?: unknown;
}

export interface SwapRouteStep {
  dex: string;
  tokenIn: string;
  tokenOut: string;
  amountIn: string;
  amountOut: string;
}

export interface SwapParams {
  tokenIn: string;
  tokenOut: string;
  amountIn: string;
  slippageBps: number;
  sender: string;
}

export interface DexAdapter {
  /** Which chain this DEX adapter serves */
  readonly chainId: string;
  /** DEX name, e.g. "tinyman", "jupiter" */
  readonly dexName: string;

  getQuote(params: SwapParams): Promise<Result<SwapQuote, SdkError>>;
  buildSwapTransaction(
    quote: SwapQuote,
    sender: string
  ): Promise<Result<UnsignedTransaction, SdkError>>;
}

// ─── Provider Registry ───
// Used by OasisWallet to discover registered chain providers.

export interface ChainProviderRegistration {
  provider: ChainProvider;
  dex?: DexAdapter;
}
