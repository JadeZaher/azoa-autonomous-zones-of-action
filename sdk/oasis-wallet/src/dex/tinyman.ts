import type { DexAdapter, SwapParams, SwapQuote, UnsignedTransaction } from "../core/types.js";
import type { Result } from "../core/result.js";
import { ok, err } from "../core/result.js";
import { SdkError, SdkErrorCode } from "../core/errors.js";

// Tinyman V2 app IDs per network
const TINYMAN_APP_IDS: Record<string, number> = {
  mainnet: 1002541853,
  testnet: 148607000,
};

export interface AlgodClientConfig {
  /** Algod node URL, e.g. "https://mainnet-api.algonode.cloud" */
  url: string;
  /** Algod API token (empty string for public nodes) */
  token: string;
  /** Optional port, defaults to 443 for https / 80 for http */
  port?: string;
}

export interface TinymanConfig {
  /** "mainnet" or "testnet". Defaults to "mainnet". */
  network?: "mainnet" | "testnet";
  /** algod client configuration. Required for transaction building. */
  algod?: AlgodClientConfig;
}

// ─── Internal type shims for the Tinyman JS SDK ───────────────────────────────
// We dynamic-import the SDK to avoid hard runtime failure when it is absent.
// These types reflect the stable public surface of @tinymanorg/tinyman-js-sdk v2.

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type TinymanSdk = any;

interface TinymanPool {
  issuedPoolTokens: bigint;
  asset1: { id: number };
  asset2: { id: number };
}

// ─── Helpers ─────────────────────────────────────────────────────────────────

async function loadSdk(): Promise<Result<TinymanSdk, SdkError>> {
  try {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const sdk = await import("@tinymanorg/tinyman-js-sdk" as any) as TinymanSdk;
    return ok(sdk);
  } catch {
    return err(
      new SdkError(
        SdkErrorCode.UNSUPPORTED_OPERATION,
        "The @tinymanorg/tinyman-js-sdk package is not installed. " +
          "Run: npm install @tinymanorg/tinyman-js-sdk",
        { chain: "algorand" }
      )
    );
  }
}

/**
 * Convert slippage in basis points (bps) to a decimal ratio used by the SDK.
 * 50 bps → 0.005 (0.5 %)
 */
function bpsToDecimal(bps: number): number {
  return bps / 10_000;
}

/** Build an algosdk Algodv2 client from config via dynamic import. */
async function buildAlgodClient(cfg: AlgodClientConfig): Promise<Result<unknown, SdkError>> {
  try {
    // algosdk is a peer dep of the Tinyman SDK so it should always be available
    // when the Tinyman SDK is installed, but we guard anyway.
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const algosdk = await import("algosdk" as any) as { Algodv2: new (token: string, server: string, port?: string) => unknown };
    const client = new algosdk.Algodv2(cfg.token, cfg.url, cfg.port ?? "");
    return ok(client);
  } catch {
    return err(
      new SdkError(
        SdkErrorCode.UNSUPPORTED_OPERATION,
        "algosdk is not installed. It is required to build Algorand transactions. " +
          "Run: npm install algosdk",
        { chain: "algorand" }
      )
    );
  }
}

/** Encode an array of SDK transaction objects to Uint8Array[]. */
function encodeTransactionGroup(
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  txns: Array<any>
): Uint8Array[] {
  return txns.map((txn) => {
    // The Tinyman SDK returns algosdk Transaction objects.
    // algosdk Transaction has a .toByte() method.
    if (typeof txn.toByte === "function") {
      return txn.toByte() as Uint8Array;
    }
    // Fallback: some SDK versions wrap it in { txn }
    if (txn.txn && typeof txn.txn.toByte === "function") {
      return txn.txn.toByte() as Uint8Array;
    }
    // No globalThis fallback — avoid supply chain risk from untrusted globals.
    // If neither toByte() pattern works, the SDK version is unsupported.
    throw new Error(
      "Cannot encode transaction: unknown SDK transaction shape. " +
      "Ensure @tinymanorg/tinyman-js-sdk v2+ is installed."
    );
  });
}

// ─── Adapter ─────────────────────────────────────────────────────────────────

export class TinymanAdapter implements DexAdapter {
  readonly chainId = "algorand";
  readonly dexName = "tinyman";

  private readonly network: "mainnet" | "testnet";
  private readonly algodConfig: AlgodClientConfig | undefined;

  constructor(config?: TinymanConfig) {
    this.network = config?.network ?? "mainnet";
    this.algodConfig = config?.algod;
  }

  // ─── getQuote ─────────────────────────────────────────────────────────────

  async getQuote(params: SwapParams): Promise<Result<SwapQuote, SdkError>> {
    const sdkResult = await loadSdk();
    if (!sdkResult.ok) return sdkResult;
    const sdk = sdkResult.value;

    if (!this.algodConfig) {
      return err(
        new SdkError(
          SdkErrorCode.UNSUPPORTED_OPERATION,
          "TinymanAdapter requires algod config to fetch pool quotes. " +
            "Pass algod: { url, token } in TinymanConfig.",
          { chain: "algorand" }
        )
      );
    }

    const algodResult = await buildAlgodClient(this.algodConfig);
    if (!algodResult.ok) return algodResult;
    const algodClient = algodResult.value;

    try {
      const assetInId = Number(params.tokenIn);   // Algorand ASA IDs are integers; 0 = ALGO
      const assetOutId = Number(params.tokenOut);
      const amountIn = BigInt(params.amountIn);

      // Fetch the pool for the asset pair
      // SDK v2 API: fetchPoolData({ client, network, asset1ID, asset2ID })
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const { fetchPoolData, getSwapQuote, SwapType } = sdk as any;

      const poolData = await fetchPoolData({
        client: algodClient,
        network: this.network,
        asset1ID: Math.min(assetInId, assetOutId),
        asset2ID: Math.max(assetInId, assetOutId),
      }) as TinymanPool;

      if (!poolData || poolData.issuedPoolTokens === 0n) {
        return err(
          new SdkError(
            SdkErrorCode.DEX_ERROR,
            `No Tinyman V2 pool found for assets ${params.tokenIn} / ${params.tokenOut}`,
            { chain: "algorand" }
          )
        );
      }

      const slippage = bpsToDecimal(params.slippageBps);

      // getSwapQuote({ pool, assetIn, assetOut, amount, swapType, slippage })
      const quote = await getSwapQuote({
        pool: poolData,
        assetIn: { id: assetInId, amount: amountIn },
        assetOut: { id: assetOutId },
        swapType: SwapType.FixedInput,
        slippage,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      }) as any;

      const expectedOut: bigint = quote.amountOut?.amount ?? quote.amount_out ?? 0n;
      const feeAmount: bigint = quote.swapFee?.amount ?? quote.swap_fee ?? 0n;
      const priceImpact: number =
        typeof quote.priceImpact === "number"
          ? quote.priceImpact
          : parseFloat(quote.price_impact ?? "0");

      return ok({
        chain: "algorand",
        tokenIn: params.tokenIn,
        tokenOut: params.tokenOut,
        amountIn: params.amountIn,
        expectedAmountOut: expectedOut.toString(),
        priceImpact,
        fee: feeAmount.toString(),
        raw: quote,
      });
    } catch (e) {
      return err(
        new SdkError(
          SdkErrorCode.DEX_ERROR,
          `Tinyman getQuote error: ${e instanceof Error ? e.message : String(e)}`,
          { chain: "algorand", cause: e instanceof Error ? e : undefined }
        )
      );
    }
  }

  // ─── buildSwapTransaction ─────────────────────────────────────────────────

  async buildSwapTransaction(
    quote: SwapQuote,
    sender: string
  ): Promise<Result<UnsignedTransaction, SdkError>> {
    const sdkResult = await loadSdk();
    if (!sdkResult.ok) return sdkResult;
    const sdk = sdkResult.value;

    if (!this.algodConfig) {
      return err(
        new SdkError(
          SdkErrorCode.UNSUPPORTED_OPERATION,
          "TinymanAdapter requires algod config to build transactions. " +
            "Pass algod: { url, token } in TinymanConfig.",
          { chain: "algorand" }
        )
      );
    }

    const algodResult = await buildAlgodClient(this.algodConfig);
    if (!algodResult.ok) return algodResult;
    const algodClient = algodResult.value;

    try {
      const assetInId = Number(quote.tokenIn);
      const assetOutId = Number(quote.tokenOut);
      const amountIn = BigInt(quote.amountIn);
      const slippage = 0.005; // Default 50 bps; quote no longer carries slippage at this stage

      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const { fetchPoolData, getSwapQuote, prepareSwapTransactions, SwapType } = sdk as any;

      const poolData = await fetchPoolData({
        client: algodClient,
        network: this.network,
        asset1ID: Math.min(assetInId, assetOutId),
        asset2ID: Math.max(assetInId, assetOutId),
      }) as TinymanPool;

      if (!poolData || poolData.issuedPoolTokens === 0n) {
        return err(
          new SdkError(
            SdkErrorCode.DEX_ERROR,
            `No Tinyman V2 pool found for assets ${quote.tokenIn} / ${quote.tokenOut}`,
            { chain: "algorand" }
          )
        );
      }

      // Re-derive the quote if raw is not present or is stale
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const sdkQuote = quote.raw ?? await getSwapQuote({
        pool: poolData,
        assetIn: { id: assetInId, amount: amountIn },
        assetOut: { id: assetOutId },
        swapType: SwapType.FixedInput,
        slippage,
      });

      const appId = TINYMAN_APP_IDS[this.network];

      // prepareSwapTransactions returns an array of algosdk Transaction objects
      // grouped into an atomic transaction group
      const txnGroup = await prepareSwapTransactions({
        client: algodClient,
        pool: poolData,
        swapQuote: sdkQuote,
        initiatorAddr: sender,
        appId,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      }) as Array<any>;

      let txnBytes: Uint8Array[];
      try {
        txnBytes = encodeTransactionGroup(txnGroup);
      } catch (encodeErr) {
        return err(
          new SdkError(
            SdkErrorCode.DEX_ERROR,
            `Failed to encode Tinyman transaction group: ${encodeErr instanceof Error ? encodeErr.message : String(encodeErr)}`,
            { chain: "algorand", cause: encodeErr instanceof Error ? encodeErr : undefined }
          )
        );
      }

      if (txnBytes.length === 0) {
        return err(
          new SdkError(
            SdkErrorCode.DEX_ERROR,
            "Tinyman SDK returned an empty transaction group",
            { chain: "algorand" }
          )
        );
      }

      return ok({
        chain: "algorand",
        format: "native" as const,
        // The first transaction in the group is the primary swap transaction
        bytes: txnBytes[0]!,
        group: txnBytes,
        description: `Swap ${quote.amountIn} of ASA ${quote.tokenIn} for ~${quote.expectedAmountOut} of ASA ${quote.tokenOut} via Tinyman V2`,
      });
    } catch (e) {
      return err(
        new SdkError(
          SdkErrorCode.DEX_ERROR,
          `Tinyman buildSwapTransaction error: ${e instanceof Error ? e.message : String(e)}`,
          { chain: "algorand", cause: e instanceof Error ? e : undefined }
        )
      );
    }
  }
}
