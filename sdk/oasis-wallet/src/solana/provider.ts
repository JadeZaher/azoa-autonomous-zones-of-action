import type {
  ChainProvider,
  ChainProviderConfig,
  BalanceInfo,
  AssetInfo,
  TransactionResult,
  UnsignedTransaction,
  TransferParams,
  MintParams,
  BurnParams,
  Signer,
} from "../core/types.js";
import type { Result } from "../core/result.js";
import { ok, err } from "../core/result.js";
import { SdkError, SdkErrorCode } from "../core/errors.js";

// ─── Cross-platform base64 encoding ───
// btoa() is not available in React Native or some Node environments, so we
// implement our own lookup-table encoder that works everywhere.
const B64_CHARS = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

function bytesToBase64(bytes: Uint8Array): string {
  let result = "";
  for (let i = 0; i < bytes.length; i += 3) {
    const b0 = bytes[i]!;
    const b1 = bytes[i + 1] ?? 0;
    const b2 = bytes[i + 2] ?? 0;
    result += B64_CHARS[b0 >> 2];
    result += B64_CHARS[((b0 & 3) << 4) | (b1 >> 4)];
    result += i + 1 < bytes.length ? B64_CHARS[((b1 & 15) << 2) | (b2 >> 6)] : "=";
    result += i + 2 < bytes.length ? B64_CHARS[b2 & 63] : "=";
  }
  return result;
}

export interface SolanaProviderConfig extends ChainProviderConfig {
  /** JSON-RPC endpoint URL */
  rpcUrl: string;
}

interface RpcResponse<T> {
  jsonrpc: string;
  id: number;
  result?: T;
  error?: { code: number; message: string };
}

export class SolanaProvider implements ChainProvider {
  readonly chainId = "solana";
  readonly displayName = "Solana";
  readonly supportsDex = true;
  readonly supportsBridging = true;

  private readonly config: SolanaProviderConfig;
  private rpcId = 0;

  constructor(config: SolanaProviderConfig) {
    this.config = config;
  }

  // ─── Queries ───

  async getBalance(address: string, tokenId?: string): Promise<Result<BalanceInfo, SdkError>> {
    try {
      if (!tokenId) {
        const resp = await this.rpcCall<{ value: number }>("getBalance", [address]);
        if (resp.error) return err(new SdkError(SdkErrorCode.NETWORK_ERROR, resp.error.message, { chain: "solana" }));
        const lamports = resp.result?.value ?? 0;
        return ok({
          amount: (lamports / 1_000_000_000).toFixed(9),
          decimals: 9,
          symbol: "SOL",
          raw: resp.result,
        });
      }

      // SPL token balance
      const resp = await this.rpcCall<{ value: Array<{ account: { data: { parsed: { info: { tokenAmount: { amount: string; decimals: number; uiAmountString: string } } } } } }> }>(
        "getTokenAccountsByOwner",
        [address, { mint: tokenId }, { encoding: "jsonParsed" }]
      );

      if (resp.error) return err(new SdkError(SdkErrorCode.NETWORK_ERROR, resp.error.message, { chain: "solana" }));
      const accounts = resp.result?.value ?? [];
      if (accounts.length === 0) {
        return ok({ amount: "0", decimals: 0, symbol: tokenId.slice(0, 8), raw: null });
      }

      const info = accounts[0]!.account.data.parsed.info.tokenAmount;
      return ok({
        amount: info.uiAmountString,
        decimals: info.decimals,
        symbol: tokenId.slice(0, 8),
        raw: accounts[0],
      });
    } catch (e) {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Balance fetch failed: ${e}`, { chain: "solana", cause: e as Error }));
    }
  }

  async validateAddress(address: string): Promise<Result<boolean, SdkError>> {
    if (address.length < 32 || address.length > 44) return ok(false);
    const base58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    for (const c of address) {
      if (!base58.includes(c)) return ok(false);
    }
    return ok(true);
  }

  async getAssets(address: string): Promise<Result<AssetInfo[], SdkError>> {
    try {
      const resp = await this.rpcCall<{ value: Array<{ account: { data: { parsed: { info: { mint: string; owner: string; state: string; tokenAmount: { amount: string; decimals: number; uiAmountString: string } } } } } }> }>(
        "getTokenAccountsByOwner",
        [address, { programId: "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA" }, { encoding: "jsonParsed" }]
      );

      if (resp.error) return err(new SdkError(SdkErrorCode.NETWORK_ERROR, resp.error.message, { chain: "solana" }));

      const assets: AssetInfo[] = (resp.result?.value ?? []).map((acc) => {
        const info = acc.account.data.parsed.info;
        return {
          id: info.mint,
          name: info.mint.slice(0, 8),
          symbol: info.mint.slice(0, 8),
          amount: info.tokenAmount.uiAmountString,
          decimals: info.tokenAmount.decimals,
          raw: acc,
        };
      });

      return ok(assets);
    } catch (e) {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Assets fetch failed: ${e}`, { chain: "solana", cause: e as Error }));
    }
  }

  async getTransactionStatus(txHash: string): Promise<Result<TransactionResult, SdkError>> {
    try {
      const resp = await this.rpcCall<{ slot: number; blockTime: number | null; meta: { err: unknown; fee: number } | null }>(
        "getTransaction",
        [txHash, { encoding: "json", commitment: "confirmed" }]
      );

      if (resp.error) return err(new SdkError(SdkErrorCode.NETWORK_ERROR, resp.error.message, { chain: "solana" }));

      return ok({
        txHash,
        chain: "solana",
        status: resp.result?.meta?.err == null ? "confirmed" : "failed",
        raw: resp.result,
      });
    } catch (e) {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Status fetch failed: ${e}`, { chain: "solana", cause: e as Error }));
    }
  }

  async getTokenMetadata(tokenId: string): Promise<Result<Record<string, unknown>, SdkError>> {
    try {
      const resp = await this.rpcCall<{ value: { amount: string; decimals: number; uiAmountString: string } }>(
        "getTokenSupply",
        [tokenId]
      );

      if (resp.error) return err(new SdkError(SdkErrorCode.NETWORK_ERROR, resp.error.message, { chain: "solana" }));

      return ok({
        chain: "solana",
        mintAddress: tokenId,
        totalSupply: resp.result?.value.amount ?? "0",
        decimals: resp.result?.value.decimals ?? 0,
        uiAmount: resp.result?.value.uiAmountString ?? "0",
      });
    } catch (e) {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Metadata fetch failed: ${e}`, { chain: "solana", cause: e as Error }));
    }
  }

  async getChainInfo(): Promise<Result<Record<string, unknown>, SdkError>> {
    try {
      const slotResp = await this.rpcCall<number>("getSlot", []);
      const supplyResp = await this.rpcCall<{ value: { total: number; circulating: number } }>("getSupply", []);

      return ok({
        chain: "solana",
        network: this.config.network,
        currentSlot: slotResp.result?.toString() ?? "unknown",
        totalSupply: supplyResp.result?.value.total.toString() ?? "unknown",
        circulatingSupply: supplyResp.result?.value.circulating.toString() ?? "unknown",
        rpcUrl: this.config.rpcUrl,
      });
    } catch (e) {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Chain info failed: ${e}`, { chain: "solana", cause: e as Error }));
    }
  }

  // ─── Transaction Building ───

  async buildTransfer(params: TransferParams): Promise<Result<UnsignedTransaction, SdkError>> {
    try {
      const blockhash = await this.getLatestBlockhash();

      if (params.tokenId) {
        return ok(this.buildSplTransferTx(params, blockhash));
      }
      return ok(this.buildSolTransferTx(params, blockhash));
    } catch (e) {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Build transfer failed: ${e}`, { chain: "solana", cause: e as Error }));
    }
  }

  async buildMint(params: MintParams): Promise<Result<UnsignedTransaction, SdkError>> {
    try {
      const blockhash = await this.getLatestBlockhash();

      const txObj = {
        type: "spl_create_mint",
        authority: params.creator,
        decimals: params.decimals,
        totalSupply: params.totalSupply,
        name: params.name,
        symbol: params.symbol,
        uri: params.metadataUri ?? "",
        recentBlockhash: blockhash,
      };

      return ok({
        chain: "solana",
        format: "json-descriptor" as const,
      bytes: new TextEncoder().encode(JSON.stringify(txObj)),
        description: `Create SPL token: ${params.name} (${params.symbol})`,
      });
    } catch (e) {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Build mint failed: ${e}`, { chain: "solana", cause: e as Error }));
    }
  }

  async buildBurn(params: BurnParams): Promise<Result<UnsignedTransaction, SdkError>> {
    try {
      const blockhash = await this.getLatestBlockhash();

      const txObj = {
        type: "spl_burn",
        mint: params.tokenId,
        owner: params.owner,
        amount: params.amount,
        recentBlockhash: blockhash,
      };

      return ok({
        chain: "solana",
        format: "json-descriptor" as const,
      bytes: new TextEncoder().encode(JSON.stringify(txObj)),
        description: `Burn ${params.amount} of ${params.tokenId}`,
      });
    } catch (e) {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Build burn failed: ${e}`, { chain: "solana", cause: e as Error }));
    }
  }

  // ─── Signing + Submission ───

  async signTransaction(tx: UnsignedTransaction, signer: Signer): Promise<Result<Uint8Array, SdkError>> {
    // The signing flow delegates entirely to signer.sign(), provided by the consumer.
    //
    // In production, consumers use wallet adapters (Phantom, Backpack, etc.) that
    // handle Ed25519 signing internally inside the wallet extension or app.
    //
    // For programmatic / server-side signing, consumers can build a signer using
    // @noble/curves (do NOT import that package in this SDK — keep it optional):
    //
    //   // Consumer code — not in the SDK:
    //   import { ed25519 } from "@noble/curves/ed25519";
    //   const signer = {
    //     publicKey: pubKey,
    //     sign: (msg: Uint8Array) => Promise.resolve(ed25519.sign(msg, privateKey)),
    //   };
    try {
      const signed = await signer.sign(tx.bytes);
      return ok(signed);
    } catch (e) {
      return err(new SdkError(SdkErrorCode.SIGNING_ERROR, `Signing failed: ${e}`, { chain: "solana", cause: e as Error }));
    }
  }

  async submitTransaction(signedTx: Uint8Array): Promise<Result<TransactionResult, SdkError>> {
    try {
      // Cross-platform base64 encode — bytesToBase64 works in React Native, Node,
      // and browsers without relying on btoa() or Buffer.
      const b64 = bytesToBase64(signedTx);
      const resp = await this.rpcCall<string>("sendTransaction", [b64, { encoding: "base64" }]);

      if (resp.error) return err(new SdkError(SdkErrorCode.NETWORK_ERROR, resp.error.message, { chain: "solana" }));

      return ok({
        txHash: resp.result ?? "",
        chain: "solana",
        status: "submitted",
        raw: resp,
      });
    } catch (e) {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Submit failed: ${e}`, { chain: "solana", cause: e as Error }));
    }
  }

  // ─── Internal helpers ───

  private buildSolTransferTx(params: TransferParams, blockhash: string): UnsignedTransaction {
    const lamports = Math.round(Number(params.amount) * 1_000_000_000);
    const txObj = {
      type: "system_transfer",
      from: params.from,
      to: params.to,
      lamports,
      recentBlockhash: blockhash,
    };

    return {
      chain: "solana",
      format: "json-descriptor" as const,
      bytes: new TextEncoder().encode(JSON.stringify(txObj)),
      description: `Send ${params.amount} SOL to ${params.to}`,
    };
  }

  private buildSplTransferTx(params: TransferParams, blockhash: string): UnsignedTransaction {
    const txObj = {
      type: "spl_transfer",
      from: params.from,
      to: params.to,
      mint: params.tokenId,
      amount: Number(params.amount),
      recentBlockhash: blockhash,
    };

    return {
      chain: "solana",
      format: "json-descriptor" as const,
      bytes: new TextEncoder().encode(JSON.stringify(txObj)),
      description: `Transfer ${params.amount} of ${params.tokenId} to ${params.to}`,
    };
  }

  private async getLatestBlockhash(): Promise<string> {
    const resp = await this.rpcCall<{ value: { blockhash: string } }>("getLatestBlockhash", []);
    if (resp.error) throw new Error(resp.error.message);
    return resp.result?.value.blockhash ?? "";
  }

  private async rpcCall<T>(method: string, params: unknown[]): Promise<RpcResponse<T>> {
    const id = ++this.rpcId;
    const response = await fetch(this.config.rpcUrl, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ jsonrpc: "2.0", id, method, params }),
    });
    return response.json() as Promise<RpcResponse<T>>;
  }
}
