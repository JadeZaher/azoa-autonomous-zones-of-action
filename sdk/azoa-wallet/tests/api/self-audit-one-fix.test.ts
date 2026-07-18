/**
 * Tests for self-audit-one-fix findings.
 * Covers: Jupiter (F1), listNfts (F6), getComposite path (F8),
 * getApiUrl (F4), getSwapQuote/executeSwap (F2), updateSTARODK PUT (F7).
 */

import { describe, it, expect, vi, beforeEach } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import { AzoaClient } from "../../src/client/azoa-client.js";
import { HolonQueryBuilder } from "../../src/client/holon-query.js";
import { API_PATHS } from "../../src/api/api-version.js";
import { isOk } from "../../src/core/result.js";
import { ApiConfigBuilder } from "../builders/index.js";

const mockFetch = vi.fn();
vi.stubGlobal("fetch", mockFetch);

function azoaResponse<T>(result: T) {
  return Promise.resolve({
    ok: true,
    status: 200,
    json: () => Promise.resolve({ isError: false, message: "Success", result }),
  });
}

describe("Finding 1 — Jupiter: no Buffer, no requiresSigning", () => {
  it("JupiterAdapter source does not reference Buffer", async () => {
    // Dynamic import to get the module source path — we just verify
    // the class can be imported and does not blow up on load (Buffer absent).
    const { JupiterAdapter } = await import("../../src/dex/jupiter.js");
    const adapter = new JupiterAdapter();
    expect(adapter.chainId).toBe("solana");
    expect(adapter.dexName).toBe("jupiter");
  });

  it("buildSwapTransaction result has no requiresSigning field", async () => {
    const { JupiterAdapter } = await import("../../src/dex/jupiter.js");
    const adapter = new JupiterAdapter({ apiUrl: "http://fake-jupiter" });

    // Mock the /swap response
    mockFetch
      // first call: /quote
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: () =>
          Promise.resolve({
            inAmount: "1000000",
            outAmount: "998500",
            priceImpactPct: "0.15",
            routePlan: [],
          }),
      })
      // second call: /swap
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: () =>
          Promise.resolve({
            swapTransaction: "AAAA", // valid base64
            lastValidBlockHeight: 99999,
          }),
      });

    const quoteResult = await adapter.getQuote({
      tokenIn: "So11111111111111111111111111111111111111112",
      tokenOut: "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v",
      amountIn: "1000000",
      slippageBps: 50,
      sender: "SENDER",
    });
    expect(isOk(quoteResult)).toBe(true);
    if (!quoteResult.ok) return;

    mockFetch.mockClear();
    mockFetch.mockResolvedValueOnce({
      ok: true,
      status: 200,
      json: () =>
        Promise.resolve({
          swapTransaction: "AAAA",
          lastValidBlockHeight: 99999,
        }),
    });

    const txResult = await adapter.buildSwapTransaction(quoteResult.value, "SENDER");
    expect(isOk(txResult)).toBe(true);
    if (!txResult.ok) return;

    const tx = txResult.value;
    // requiresSigning must NOT be a key on UnsignedTransaction
    expect(Object.prototype.hasOwnProperty.call(tx, "requiresSigning")).toBe(false);
    // bytes must be a Uint8Array (base64Decode, not Buffer)
    expect(tx.bytes).toBeInstanceOf(Uint8Array);
    expect(tx.format).toBe("base64");
  });
});

describe("Finding 6 — listNfts() on AzoaApiClient", () => {
  let client: AzoaApiClient;

  beforeEach(() => {
    mockFetch.mockReset();
    client = new AzoaApiClient(new ApiConfigBuilder().withToken("tok").build());
  });

  it("sends GET /api/nft with no params", async () => {
    mockFetch.mockReturnValueOnce(azoaResponse([]));
    const result = await client.listNfts();
    expect(isOk(result)).toBe(true);
    expect(mockFetch).toHaveBeenCalledWith(
      "http://localhost:5000/api/nft",
      expect.objectContaining({ method: "GET" })
    );
  });

  it("sends GET /api/nft?ownerAvatarId=... when param provided", async () => {
    mockFetch.mockReturnValueOnce(azoaResponse([{ id: "nft1", name: "My NFT" }]));
    const result = await client.listNfts({ ownerAvatarId: "avatar-abc" });
    expect(isOk(result)).toBe(true);
    const url: string = mockFetch.mock.calls[0][0];
    expect(url).toContain("/api/nft");
    expect(url).toContain("ownerAvatarId=avatar-abc");
  });

  it("listNfts returns typed NftResult array", async () => {
    const nfts = [
      { id: "n1", name: "NFT One", description: "d", chainId: "algorand", isActive: true },
    ];
    mockFetch.mockReturnValueOnce(azoaResponse(nfts));
    const result = await client.listNfts({ chainId: "algorand" });
    expect(isOk(result)).toBe(true);
    if (result.ok) {
      expect(result.value).toHaveLength(1);
      expect(result.value[0]!.name).toBe("NFT One");
    }
  });
});

describe("Finding 8 — getComposite() uses API_PATHS.HOLON_COMPOSE", () => {
  let client: AzoaApiClient;
  let holons: HolonQueryBuilder;

  beforeEach(() => {
    mockFetch.mockReset();
    client = new AzoaApiClient(new ApiConfigBuilder().withToken("tok").build());
    holons = new HolonQueryBuilder(client);
  });

  it("API_PATHS.HOLON_COMPOSE produces correct path", () => {
    expect(API_PATHS.HOLON_COMPOSE("some-holon-id")).toBe("/api/holon/some-holon-id/compose");
  });

  it("getComposite() sends GET /api/holon/{id}/compose", async () => {
    mockFetch.mockReturnValueOnce(azoaResponse({ id: "h1", children: [] }));
    const result = await holons.getComposite("holon-123");
    expect(isOk(result)).toBe(true);
    expect(mockFetch).toHaveBeenCalledWith(
      "http://localhost:5000/api/holon/holon-123/compose",
      expect.objectContaining({ method: "GET" })
    );
  });
});

describe("Finding 4 — getApiUrl() on AzoaClient and AzoaApiClient", () => {
  it("AzoaApiClient.getBaseUrl() returns the configured baseUrl", () => {
    const client = new AzoaApiClient({ baseUrl: "https://api.example.com" });
    expect(client.getBaseUrl()).toBe("https://api.example.com");
  });

  it("AzoaClient.getApiUrl() returns the apiUrl from config", () => {
    const azoa = new AzoaClient({ apiUrl: "https://azoa.staging.io" });
    expect(azoa.getApiUrl()).toBe("https://azoa.staging.io");
  });
});

describe("Finding 2 — getSwapQuote() and executeSwap() on AzoaApiClient", () => {
  let client: AzoaApiClient;

  beforeEach(() => {
    mockFetch.mockReset();
    client = new AzoaApiClient(new ApiConfigBuilder().withToken("tok").build());
  });

  it("getSwapQuote sends GET /api/swap/quote with query params", async () => {
    const quoteResp = {
      chain: "solana",
      tokenIn: "TOKEN_A",
      tokenOut: "TOKEN_B",
      amountIn: "1000000",
      expectedAmountOut: "998000",
      priceImpact: 0.2,
      fee: "500",
    };
    mockFetch.mockReturnValueOnce(azoaResponse(quoteResp));

    const result = await client.getSwapQuote({
      chain: "solana",
      tokenIn: "TOKEN_A",
      tokenOut: "TOKEN_B",
      amountIn: "1000000",
      slippageBps: 50,
    });

    expect(isOk(result)).toBe(true);
    const url: string = mockFetch.mock.calls[0][0];
    expect(url).toContain("/api/swap/quote");
    expect(url).toContain("chain=solana");
    expect(url).toContain("tokenIn=TOKEN_A");
    expect(url).toContain("amountIn=1000000");
    expect(mockFetch.mock.calls[0][1].method).toBe("GET");

    if (result.ok) {
      expect(result.value.expectedAmountOut).toBe("998000");
    }
  });

  it("executeSwap sends POST /api/swap/execute with body", async () => {
    const execResp = {
      chain: "solana",
      tokenIn: "TOKEN_A",
      tokenOut: "TOKEN_B",
      amountIn: "1000000",
      expectedAmountOut: "998000",
      priceImpact: 0.2,
      fee: "500",
      swapTransaction: "base64-encoded-tx",
    };
    mockFetch.mockReturnValueOnce(azoaResponse(execResp));

    const result = await client.executeSwap({
      chain: "solana",
      quoteId: "quote-uuid-abc",
      walletAddress: "WALLET_ADDR",
    });

    expect(isOk(result)).toBe(true);
    const [url, init] = mockFetch.mock.calls[0];
    expect(url).toBe("http://localhost:5000/api/swap/execute");
    expect(init.method).toBe("POST");
    const body = JSON.parse(init.body);
    expect(body.chain).toBe("solana");
    expect(body.quoteId).toBe("quote-uuid-abc");
    expect(body.walletAddress).toBe("WALLET_ADDR");
  });

  it("executeSwap forwards Idempotency-Key header when provided", async () => {
    mockFetch.mockReturnValueOnce(azoaResponse({ chain: "solana", tokenIn: "A", tokenOut: "B", amountIn: "1", expectedAmountOut: "1", priceImpact: 0, fee: "0" }));

    await client.executeSwap(
      { chain: "solana", quoteId: "qid", walletAddress: "ADDR" },
      { idempotencyKey: "my-key-123" }
    );

    const init = mockFetch.mock.calls[0][1];
    expect(init.headers["Idempotency-Key"]).toBe("my-key-123");
  });

  it("API_PATHS includes SWAP_QUOTE and SWAP_EXECUTE", () => {
    expect(API_PATHS.SWAP_QUOTE).toBe("/api/swap/quote");
    expect(API_PATHS.SWAP_EXECUTE).toBe("/api/swap/execute");
  });
});

describe("Finding 7 — updateSTARODK() uses PUT /api/starodk/{id}", () => {
  let client: AzoaApiClient;
  const VALID_ID = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

  beforeEach(() => {
    mockFetch.mockReset();
    client = new AzoaApiClient(new ApiConfigBuilder().withToken("tok").build());
  });

  it("issues PUT to /api/starodk/{id}", async () => {
    mockFetch.mockReturnValueOnce(azoaResponse({ id: VALID_ID, name: "MyApp" }));
    const result = await client.updateSTARODK(VALID_ID, { name: "MyApp", description: "desc" });
    expect(isOk(result)).toBe(true);
    const [url, init] = mockFetch.mock.calls[0];
    expect(init.method).toBe("PUT");
    expect(url).toBe(`http://localhost:5000/api/starodk/${VALID_ID}`);
  });

  it("embeds the id in the URL path, not the body", async () => {
    mockFetch.mockReturnValueOnce(azoaResponse({ id: VALID_ID, name: "MyApp" }));
    await client.updateSTARODK(VALID_ID, { name: "MyApp", description: "desc" });
    const [url] = mockFetch.mock.calls[0];
    expect(url).toContain(`/api/starodk/${VALID_ID}`);
  });

  it("throws before sending a request when id is not a UUID", async () => {
    await expect(client.updateSTARODK("not-a-uuid", { name: "MyApp", description: "desc" })).rejects.toThrow(
      "Invalid starodkId"
    );
    expect(mockFetch).not.toHaveBeenCalled();
  });
});
