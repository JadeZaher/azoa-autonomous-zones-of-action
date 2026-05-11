import type { Result } from "../core/result.js";
import { ok } from "../core/result.js";
import type { SdkError } from "../core/errors.js";
import type { OasisWallet } from "../wallet.js";
import { OasisApiClient } from "../api/client.js";
import type { BalanceInfo } from "../core/types.js";

export interface ChainBalance {
  chain: string;
  address: string;
  balance: BalanceInfo;
}

export interface PortfolioSummary {
  chains: ChainBalance[];
  walletCount: number;
  fetchedAt: string;
}

/**
 * Aggregates balances across all wallets and chains for the authenticated avatar.
 *
 * ```ts
 * const portfolio = await oasis.portfolio.getAll();
 * for (const chain of portfolio.chains) {
 *   console.log(`${chain.chain}: ${chain.balance.amount} ${chain.balance.symbol}`);
 * }
 * ```
 */
export class PortfolioAggregator {
  private readonly api: OasisApiClient;
  private readonly wallet: OasisWallet;

  constructor(api: OasisApiClient, wallet: OasisWallet) {
    this.api = api;
    this.wallet = wallet;
  }

  /**
   * Fetch balances for all wallets registered to the given avatar.
   * Queries the OASIS API for wallet list, then queries each chain provider
   * for live balances.
   */
  async getAll(avatarId: string): Promise<Result<PortfolioSummary, SdkError>> {
    // Get wallets from the API
    const qs = new URLSearchParams({ avatarId });
    const walletsResult = await this.api.request<Array<{
      id: string;
      chainType: string;
      address: string;
      label?: string;
      isDefault: boolean;
    }>>("GET", `/api/wallet?${qs}`);

    if (!walletsResult.ok) return walletsResult;
    const wallets = walletsResult.value;

    // Query balance for each wallet via the chain provider
    const chains: ChainBalance[] = [];

    const balancePromises = wallets.map(async (w) => {
      const chainId = w.chainType.toLowerCase();
      const balanceResult = await this.wallet.getBalance(chainId, w.address);

      if (balanceResult.ok) {
        chains.push({
          chain: w.chainType,
          address: w.address,
          balance: balanceResult.value,
        });
      } else {
        // Include with zero balance if chain provider unavailable
        chains.push({
          chain: w.chainType,
          address: w.address,
          balance: { amount: "0", decimals: 0, symbol: w.chainType, raw: null },
        });
      }
    });

    await Promise.all(balancePromises);

    return ok({
      chains,
      walletCount: wallets.length,
      fetchedAt: new Date().toISOString(),
    });
  }

  /**
   * Fetch balance for a single chain/address.
   */
  async getChainBalance(chainId: string, address: string): Promise<Result<ChainBalance, SdkError>> {
    const balanceResult = await this.wallet.getBalance(chainId, address);
    if (!balanceResult.ok) return balanceResult;

    return ok({
      chain: chainId,
      address,
      balance: balanceResult.value,
    });
  }
}
