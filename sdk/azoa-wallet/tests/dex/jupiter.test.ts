import { describe, it, expect, vi, beforeEach } from "vitest";
import { JupiterAdapter } from "../../src/dex/jupiter.js";
import { isOk } from "../../src/core/result.js";
import { base64Decode, base64Encode } from "../../src/core/encoding.js";
import type { SwapQuote } from "../../src/core/types.js";

const mockFetch = vi.fn();
vi.stubGlobal("fetch", mockFetch);

function jsonResponse<T>(data: T, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(data),
    text: () => Promise.resolve(""),
  });
}

describe("JupiterAdapter.buildSwapTransaction", () => {
  let adapter: JupiterAdapter;

  beforeEach(() => {
    mockFetch.mockReset();
    adapter = new JupiterAdapter();
  });

  it("uses pure-JS base64Decode (no Buffer) for swapTransaction bytes", async () => {
    // Encode a known byte payload so we can round-trip through base64Decode.
    const payload = new Uint8Array([0xde, 0xad, 0xbe, 0xef, 0x01, 0x02, 0x03, 0x04]);
    const b64 = base64Encode(payload);

    mockFetch.mockReturnValueOnce(
      jsonResponse({ swapTransaction: b64, lastValidBlockHeight: 12345 })
    );

    const quote: SwapQuote = {
      chain: "solana",
      tokenIn: "INMINT",
      tokenOut: "OUTMINT",
      amountIn: "1000",
      expectedAmountOut: "950",
      priceImpact: 0.1,
      fee: "0",
      raw: { dummy: true },
    };

    const result = await adapter.buildSwapTransaction(quote, "SENDER");
    expect(isOk(result)).toBe(true);
    if (!result.ok) return;

    // Bytes must equal the pure-JS base64Decode of the same input (proves
    // we did not regress to Buffer.from()).
    expect(Array.from(result.value.bytes)).toEqual(Array.from(base64Decode(b64)));
    expect(result.value.chain).toBe("solana");
    expect(result.value.format).toBe("base64");
  });

  it("does NOT include requiresSigning on the returned UnsignedTransaction", async () => {
    const payload = new Uint8Array([1, 2, 3, 4]);
    const b64 = base64Encode(payload);
    mockFetch.mockReturnValueOnce(
      jsonResponse({ swapTransaction: b64, lastValidBlockHeight: 1 })
    );

    const quote: SwapQuote = {
      chain: "solana",
      tokenIn: "A",
      tokenOut: "B",
      amountIn: "1",
      expectedAmountOut: "1",
      priceImpact: 0,
      fee: "0",
      raw: {},
    };

    const result = await adapter.buildSwapTransaction(quote, "SENDER");
    expect(isOk(result)).toBe(true);
    if (!result.ok) return;

    // requiresSigning was a non-interface property that consumers might have
    // read. Confirm it is gone from the returned object.
    expect((result.value as Record<string, unknown>).requiresSigning).toBeUndefined();
  });
});
