import type { DexAdapter, SwapParams, SwapQuote, UnsignedTransaction } from "../core/types.js";
import type { Result } from "../core/result.js";
import { ok, err } from "../core/result.js";
import { SdkError, SdkErrorCode } from "../core/errors.js";
import { base64Decode } from "../core/encoding.js";

export interface JupiterConfig {
  /** Jupiter API base URL. Defaults to v2 Router API. */
  apiUrl?: string;
  /** Jupiter API key from developers.jup.ag/portal. */
  apiKey?: string;
}

const JUPITER_QUOTE_API = "https://api.jup.ag/swap/v2";

export class JupiterAdapter implements DexAdapter {
  readonly chainId = "solana";
  readonly dexName = "jupiter";

  private readonly apiUrl: string;
  private readonly apiKey?: string;

  constructor(config?: JupiterConfig) {
    this.apiUrl = config?.apiUrl ?? JUPITER_QUOTE_API;
    this.apiKey = config?.apiKey;
  }

  async getQuote(params: SwapParams): Promise<Result<SwapQuote, SdkError>> {
    try {
      const url = new URL(`${this.apiUrl}/quote`);
      url.searchParams.set("inputMint", params.tokenIn);
      url.searchParams.set("outputMint", params.tokenOut);
      url.searchParams.set("amount", params.amountIn);
      url.searchParams.set("slippageBps", params.slippageBps.toString());

      const headers: Record<string, string> = {};
      if (this.apiKey) {
        headers["x-api-key"] = this.apiKey;
      }

      const resp = await fetch(url.toString(), { headers });
      if (!resp.ok) {
        const body = await resp.text().catch(() => "");
        return err(
          new SdkError(
            SdkErrorCode.DEX_ERROR,
            `Jupiter quote failed: ${resp.status}${body ? ` — ${body}` : ""}`,
            { chain: "solana" }
          )
        );
      }

      interface QuoteResponse {
        inAmount: string;
        outAmount: string;
        priceImpactPct: string;
        routePlan: any[];
      }
      const data = await resp.json() as QuoteResponse;

      return ok({
        chain: "solana",
        tokenIn: params.tokenIn,
        tokenOut: params.tokenOut,
        amountIn: data.inAmount,
        expectedAmountOut: data.outAmount,
        priceImpact: parseFloat(data.priceImpactPct),
        fee: "0",
        raw: data,
        route: data.routePlan,
      });
    } catch (e) {
      return err(
        new SdkError(SdkErrorCode.DEX_ERROR, `Jupiter quote error: ${String(e)}`, {
          chain: "solana",
          cause: e instanceof Error ? e : undefined,
        })
      );
    }
  }

  async buildSwapTransaction(
    quote: SwapQuote,
    sender: string
  ): Promise<Result<UnsignedTransaction, SdkError>> {
    try {
      const headers: Record<string, string> = { "Content-Type": "application/json" };
      if (this.apiKey) {
        headers["x-api-key"] = this.apiKey;
      }

      const resp = await fetch(`${this.apiUrl}/swap`, {
        method: "POST",
        headers,
        body: JSON.stringify({
          quoteResponse: quote.raw,
          userPublicKey: sender,
          wrapAndUnwrapSol: true,
          dynamicComputeUnitLimit: true,
        }),
      });

      if (!resp.ok) {
        const body = await resp.text().catch(() => "");
        return err(
          new SdkError(
            SdkErrorCode.DEX_ERROR,
            `Jupiter swap build failed: ${resp.status}${body ? ` — ${body}` : ""}`,
            { chain: "solana" }
          )
        );
      }

      interface SwapResponse {
        swapTransaction: string;
        lastValidBlockHeight: number;
      }
      const data = await resp.json() as SwapResponse;

      return ok({
        chain: "solana",
        format: "base64" as const,
        bytes: base64Decode(data.swapTransaction),
        description: `Jupiter v2 swap: ${quote.amountIn} → ${quote.expectedAmountOut}`,
        raw: data,
      });
    } catch (e) {
      return err(
        new SdkError(
          SdkErrorCode.DEX_ERROR,
          `Jupiter swap build error: ${String(e)}`,
          { chain: "solana", cause: e instanceof Error ? e : undefined }
        )
      );
    }
  }
}
