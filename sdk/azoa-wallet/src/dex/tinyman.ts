import type { DexAdapter, SwapParams, SwapQuote, UnsignedTransaction } from "../core/types.js";
import type { Result } from "../core/result.js";
import { ok, err } from "../core/result.js";
import { SdkError, SdkErrorCode } from "../core/errors.js";

// Tinyman V2 app IDs per network


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
  /**
   * Indexer configuration, used ONLY to resolve an ASA's real `decimals` when
   * the caller does not supply `assetInDecimals`/`assetOutDecimals`. Native
   * ALGO (asset id 0) needs no indexer lookup — its decimals are a fixed
   * protocol constant. Without this configured, an ASA leg with unknown
   * decimals fails closed rather than guessing.
   */
  indexer?: AlgodClientConfig;
}

// ─── Internal type shims for the Tinyman JS SDK ───────────────────────────────
// We dynamic-import the SDK to avoid hard runtime failure when it is absent.
// These types reflect the stable public surface of @tinymanorg/tinyman-js-sdk v2.

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type TinymanSdk = any;



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

// ─── Decimals resolution ───────────────────────────────────────────────────────

/** Native ALGO's fixed protocol decimals (asset id 0 is not a real ASA — never a "default"). */
const ALGO_DECIMALS = 6;

/**
 * Resolves one ASA's real `decimals` from the configured indexer. Native ALGO
 * (`assetId === 0`) needs no lookup. Fails closed — never guesses — when the
 * asset is a real ASA and no indexer is configured, or the lookup fails: a
 * wrong decimals guess silently misprices every swap quote and transaction
 * built through it.
 */
async function resolveAssetDecimals(
  assetId: number,
  indexer: AlgodClientConfig | undefined
): Promise<Result<number, SdkError>> {
  if (assetId === 0) return ok(ALGO_DECIMALS);

  if (!indexer) {
    return err(
      new SdkError(
        SdkErrorCode.UNSUPPORTED_OPERATION,
        `Cannot resolve decimals for ASA ${assetId}: no assetInDecimals/assetOutDecimals was ` +
          "supplied and no `indexer` is configured on TinymanConfig. Pass decimals explicitly or " +
          "configure an indexer.",
        { chain: "algorand" }
      )
    );
  }

  try {
    const headers: Record<string, string> = {};
    if (indexer.token) headers["X-Indexer-API-Token"] = indexer.token;
    const resp = await fetch(`${indexer.url}/v2/assets/${assetId}`, { headers });

    if (!resp.ok) {
      const text = await resp.text().catch(() => `HTTP ${resp.status}`);
      return err(
        new SdkError(
          SdkErrorCode.NETWORK_ERROR,
          `Indexer rejected the asset lookup for ASA ${assetId}: ${text}`,
          { chain: "algorand" }
        )
      );
    }

    const data = (await resp.json()) as { asset?: { params?: { decimals?: number } } };
    const decimals = data.asset?.params?.decimals;
    if (typeof decimals !== "number") {
      return err(
        new SdkError(
          SdkErrorCode.NETWORK_ERROR,
          `Indexer response for ASA ${assetId} carried no decimals field`,
          { chain: "algorand" }
        )
      );
    }

    return ok(decimals);
  } catch (e) {
    return err(
      new SdkError(SdkErrorCode.NETWORK_ERROR, `Indexer asset lookup failed for ASA ${assetId}: ${e}`, {
        chain: "algorand",
        cause: e instanceof Error ? e : undefined,
      })
    );
  }
}

/**
 * Resolves both swap legs' decimals: a caller-supplied value wins outright for
 * that leg; anything missing is resolved via {@link resolveAssetDecimals} —
 * both legs in parallel, since one leg's lookup never needs to wait on the
 * other's.
 */
async function resolveLegDecimals(
  assetInId: number,
  assetOutId: number,
  assetInDecimals: number | undefined,
  assetOutDecimals: number | undefined,
  indexer: AlgodClientConfig | undefined
): Promise<Result<{ assetIn: number; assetOut: number }, SdkError>> {
  const [inResult, outResult] = await Promise.all([
    assetInDecimals !== undefined
      ? Promise.resolve(ok(assetInDecimals))
      : resolveAssetDecimals(assetInId, indexer),
    assetOutDecimals !== undefined
      ? Promise.resolve(ok(assetOutDecimals))
      : resolveAssetDecimals(assetOutId, indexer),
  ]);
  if (!inResult.ok) return inResult;
  if (!outResult.ok) return outResult;
  return ok({ assetIn: inResult.value, assetOut: outResult.value });
}

// ─── Adapter ─────────────────────────────────────────────────────────────────

export class TinymanAdapter implements DexAdapter {
  readonly chainId = "algorand";
  readonly dexName = "tinyman";

  private readonly network: "mainnet" | "testnet";
  private readonly algodConfig: AlgodClientConfig | undefined;
  private readonly indexerConfig: AlgodClientConfig | undefined;

  constructor(config?: TinymanConfig) {
    this.network = config?.network ?? "mainnet";
    this.algodConfig = config?.algod;
    this.indexerConfig = config?.indexer;
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
      // SDK v2 API
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const { poolUtils, Swap, SwapType } = sdk as any;

      const poolData = await poolUtils.v2.getPoolInfo({
        client: algodClient,
        network: this.network as any,
        asset1ID: Math.min(assetInId, assetOutId),
        asset2ID: Math.max(assetInId, assetOutId),
      }) as any;

      if (!poolData) {
        return err(
          new SdkError(
            SdkErrorCode.DEX_ERROR,
            `No Tinyman V2 pool found for assets ${params.tokenIn} / ${params.tokenOut}`,
            { chain: "algorand" }
          )
        );
      }

      // Resolve decimals: caller-provided wins; otherwise resolved from the
      // indexer (native ALGO needs no lookup — its decimals are a fixed
      // protocol constant). Fails closed rather than guessing 6 for an
      // arbitrary ASA, which would silently misprice the quote.
      const decimalsResult = await resolveLegDecimals(
        assetInId,
        assetOutId,
        params.assetInDecimals,
        params.assetOutDecimals,
        this.indexerConfig
      );
      if (!decimalsResult.ok) return decimalsResult;
      const decimals = decimalsResult.value;

      // getQuote(type, pool, assetIn, decimals)
      const quote = Swap.v2.getQuote(
        SwapType.FixedInput,
        poolData,
        { id: assetInId, amount: amountIn },
        decimals
      ) as any;

      const expectedOut: bigint = quote.assetOutAmount;
      const feeAmount: bigint = quote.totalFeeAmount;
      const priceImpact: number = quote.priceImpact;

      return ok({
        chain: "algorand",
        tokenIn: params.tokenIn,
        tokenOut: params.tokenOut,
        amountIn: params.amountIn,
        expectedAmountOut: expectedOut.toString(),
        priceImpact,
        fee: feeAmount.toString(),
        raw: quote,
        slippageBps: params.slippageBps,
        assetInDecimals: decimals.assetIn,
        assetOutDecimals: decimals.assetOut,
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

      // Derive slippage ratio from the quote's bps (default 50 bps / 0.5%).
      const slippageBps = quote.slippageBps ?? 50;
      const slippage = slippageBps / 10_000;

      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const { poolUtils, Swap, SwapType } = sdk as any;

      const poolData = await poolUtils.v2.getPoolInfo({
        client: algodClient,
        network: this.network as any,
        asset1ID: Math.min(assetInId, assetOutId),
        asset2ID: Math.max(assetInId, assetOutId),
      }) as any;

      if (!poolData || poolData.issuedPoolTokens === 0n) {
        return err(
          new SdkError(
            SdkErrorCode.DEX_ERROR,
            `No Tinyman V2 pool found for assets ${quote.tokenIn} / ${quote.tokenOut}`,
            { chain: "algorand" }
          )
        );
      }

      // Re-derive the quote if raw is not present or is stale. Decimals come
      // from the quote when it carried them (caller-supplied or resolved by
      // getQuote); otherwise resolved fresh here the same fail-closed way.
      const decimalsResult = await resolveLegDecimals(
        assetInId,
        assetOutId,
        quote.assetInDecimals,
        quote.assetOutDecimals,
        this.indexerConfig
      );
      if (!decimalsResult.ok) return decimalsResult;
      const decimals = decimalsResult.value;
      const sdkQuote = quote.raw ?? Swap.v2.getQuote(
        SwapType.FixedInput,
        poolData,
        { id: assetInId, amount: amountIn },
        decimals
      );

      const assetInObj = { id: assetInId, amount: amountIn };
      const assetOutObj = { 
        id: assetOutId, 
        amount: sdkQuote.assetOutAmount 
      };

      // generateTxns returns SignerTransaction[]
      const txnGroup = await Swap.v2.generateTxns({
        client: algodClient,
        pool: poolData,
        swapType: SwapType.FixedInput,
        assetIn: assetInObj,
        assetOut: assetOutObj,
        initiatorAddr: sender,
        slippage,
      }) as any;

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
