import type {
  ChainProvider,
  ChainProviderRegistration,
  DexAdapter,
  BalanceInfo,
  AssetInfo,
  TransactionResult,
  UnsignedTransaction,
  TransferParams,
  MintParams,
  BurnParams,
  SwapParams,
  SwapQuote,
  Signer,
} from "./core/types.js";
import type { Result } from "./core/result.js";
import { err } from "./core/result.js";
import { SdkError, SdkErrorCode } from "./core/errors.js";

/**
 * AzoaWallet — the unified "wallet of wallets" facade.
 *
 * Register any number of chain providers. Each provider implements ChainProvider.
 * The wallet routes operations to the correct provider by chainId.
 *
 * Adding a new chain is just: wallet.register({ provider: new MyChainProvider(cfg) })
 */
export class AzoaWallet {
  private readonly providers = new Map<string, ChainProvider>();
  private readonly dexAdapters = new Map<string, DexAdapter>();

  /** Create a wallet and register providers in one call. */
  static create(registrations: Record<string, ChainProviderRegistration>): AzoaWallet {
    const wallet = new AzoaWallet();
    for (const [_key, reg] of Object.entries(registrations)) {
      wallet.register(reg);
    }
    return wallet;
  }

  /** Register a chain provider (and optional DEX adapter). */
  register(registration: ChainProviderRegistration): void {
    this.providers.set(registration.provider.chainId, registration.provider);
    if (registration.dex) {
      this.dexAdapters.set(registration.dex.chainId, registration.dex);
    }
  }

  /**
   * Remove every registered provider and DEX adapter. Used when switching the
   * active network so no stale-network provider can service an operation —
   * this is what structurally prevents cross-network (dev/test/main) asset ops.
   */
  clear(): void {
    this.providers.clear();
    this.dexAdapters.clear();
  }

  /** List all registered chain IDs. */
  get chains(): string[] {
    return [...this.providers.keys()];
  }

  /** Get a provider by chain ID — useful for chain-specific operations. */
  getProvider<T extends ChainProvider = ChainProvider>(chainId: string): T | undefined {
    return this.providers.get(chainId) as T | undefined;
  }

  /** Get a DEX adapter by chain ID. */
  getDex(chainId: string): DexAdapter | undefined {
    return this.dexAdapters.get(chainId);
  }

  // ─── Unified query methods ───

  async getBalance(chainId: string, address: string, tokenId?: string): Promise<Result<BalanceInfo, SdkError>> {
    const provider = this.providers.get(chainId);
    if (!provider) return err(new SdkError(SdkErrorCode.PROVIDER_NOT_FOUND, `No provider for chain: ${chainId}`));
    return provider.getBalance(address, tokenId);
  }

  async validateAddress(chainId: string, address: string): Promise<Result<boolean, SdkError>> {
    const provider = this.providers.get(chainId);
    if (!provider) return err(new SdkError(SdkErrorCode.PROVIDER_NOT_FOUND, `No provider for chain: ${chainId}`));
    return provider.validateAddress(address);
  }

  async getAssets(chainId: string, address: string): Promise<Result<AssetInfo[], SdkError>> {
    const provider = this.providers.get(chainId);
    if (!provider) return err(new SdkError(SdkErrorCode.PROVIDER_NOT_FOUND, `No provider for chain: ${chainId}`));
    return provider.getAssets(address);
  }

  async getTransactionStatus(chainId: string, txHash: string): Promise<Result<TransactionResult, SdkError>> {
    const provider = this.providers.get(chainId);
    if (!provider) return err(new SdkError(SdkErrorCode.PROVIDER_NOT_FOUND, `No provider for chain: ${chainId}`));
    return provider.getTransactionStatus(txHash);
  }

  async getChainInfo(chainId: string): Promise<Result<Record<string, unknown>, SdkError>> {
    const provider = this.providers.get(chainId);
    if (!provider) return err(new SdkError(SdkErrorCode.PROVIDER_NOT_FOUND, `No provider for chain: ${chainId}`));
    return provider.getChainInfo();
  }

  // ─── Unified transaction methods ───

  async buildTransfer(chainId: string, params: TransferParams): Promise<Result<UnsignedTransaction, SdkError>> {
    const provider = this.providers.get(chainId);
    if (!provider) return err(new SdkError(SdkErrorCode.PROVIDER_NOT_FOUND, `No provider for chain: ${chainId}`));
    return provider.buildTransfer(params);
  }

  async buildMint(chainId: string, params: MintParams): Promise<Result<UnsignedTransaction, SdkError>> {
    const provider = this.providers.get(chainId);
    if (!provider) return err(new SdkError(SdkErrorCode.PROVIDER_NOT_FOUND, `No provider for chain: ${chainId}`));
    return provider.buildMint(params);
  }

  async buildBurn(chainId: string, params: BurnParams): Promise<Result<UnsignedTransaction, SdkError>> {
    const provider = this.providers.get(chainId);
    if (!provider) return err(new SdkError(SdkErrorCode.PROVIDER_NOT_FOUND, `No provider for chain: ${chainId}`));
    return provider.buildBurn(params);
  }

  async signTransaction(chainId: string, tx: UnsignedTransaction, signer: Signer): Promise<Result<Uint8Array, SdkError>> {
    const provider = this.providers.get(chainId);
    if (!provider) return err(new SdkError(SdkErrorCode.PROVIDER_NOT_FOUND, `No provider for chain: ${chainId}`));
    return provider.signTransaction(tx, signer);
  }

  async submitTransaction(chainId: string, signedTx: Uint8Array): Promise<Result<TransactionResult, SdkError>> {
    const provider = this.providers.get(chainId);
    if (!provider) return err(new SdkError(SdkErrorCode.PROVIDER_NOT_FOUND, `No provider for chain: ${chainId}`));
    return provider.submitTransaction(signedTx);
  }

  // ─── Unified DEX methods ───

  async getSwapQuote(chainId: string, params: SwapParams): Promise<Result<SwapQuote, SdkError>> {
    const dex = this.dexAdapters.get(chainId);
    if (!dex) return err(new SdkError(SdkErrorCode.PROVIDER_NOT_FOUND, `No DEX adapter for chain: ${chainId}`));
    return dex.getQuote(params);
  }

  async buildSwap(chainId: string, quote: SwapQuote, sender: string): Promise<Result<UnsignedTransaction, SdkError>> {
    const dex = this.dexAdapters.get(chainId);
    if (!dex) return err(new SdkError(SdkErrorCode.PROVIDER_NOT_FOUND, `No DEX adapter for chain: ${chainId}`));
    return dex.buildSwapTransaction(quote, sender);
  }
}
