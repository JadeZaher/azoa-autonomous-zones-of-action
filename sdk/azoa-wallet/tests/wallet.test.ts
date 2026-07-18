import { describe, it, expect, vi, beforeEach } from "vitest";
import { AzoaWallet } from "../src/wallet.js";
import { isOk, isErr } from "../src/core/result.js";
import { SdkErrorCode } from "../src/core/errors.js";
import type { ChainProvider, DexAdapter, SwapQuote } from "../src/core/types.js";
import { createMockSigner } from "./builders/index.js";

function createMockProvider(chainId: string): ChainProvider {
  return {
    chainId,
    displayName: chainId.charAt(0).toUpperCase() + chainId.slice(1),
    supportsDex: true,
    supportsBridging: true,
    getBalance: vi.fn().mockResolvedValue({ ok: true, value: { amount: "100", decimals: 6, symbol: "TEST" } }),
    validateAddress: vi.fn().mockResolvedValue({ ok: true, value: true }),
    getAssets: vi.fn().mockResolvedValue({ ok: true, value: [] }),
    getTransactionStatus: vi.fn().mockResolvedValue({ ok: true, value: { txHash: "abc", chain: chainId, status: "confirmed" } }),
    getTokenMetadata: vi.fn().mockResolvedValue({ ok: true, value: { name: "Test" } }),
    getChainInfo: vi.fn().mockResolvedValue({ ok: true, value: { chain: chainId } }),
    buildTransfer: vi.fn().mockResolvedValue({ ok: true, value: { chain: chainId, format: "json-descriptor", bytes: new Uint8Array([1, 2, 3]) } }),
    buildMint: vi.fn().mockResolvedValue({ ok: true, value: { chain: chainId, format: "json-descriptor", bytes: new Uint8Array([4, 5, 6]) } }),
    buildBurn: vi.fn().mockResolvedValue({ ok: true, value: { chain: chainId, format: "json-descriptor", bytes: new Uint8Array([7, 8, 9]) } }),
    signTransaction: vi.fn().mockResolvedValue({ ok: true, value: new Uint8Array([10, 11, 12]) }),
    submitTransaction: vi.fn().mockResolvedValue({ ok: true, value: { txHash: "xyz", chain: chainId, status: "submitted" } }),
  };
}

function createMockDex(chainId: string): DexAdapter {
  return {
    chainId,
    dexName: "mock-dex",
    getQuote: vi.fn().mockResolvedValue({
      ok: true,
      value: { chain: chainId, tokenIn: "A", tokenOut: "B", amountIn: "100", expectedAmountOut: "95", priceImpact: 0.5, fee: "5" },
    }),
    buildSwapTransaction: vi.fn().mockResolvedValue({
      ok: true,
      value: { chain: chainId, format: "native", bytes: new Uint8Array([20, 21]) },
    }),
  };
}

describe("AzoaWallet", () => {
  let wallet: AzoaWallet;
  let algoProvider: ChainProvider;
  let solProvider: ChainProvider;
  let algoDex: DexAdapter;

  beforeEach(() => {
    algoProvider = createMockProvider("algorand");
    solProvider = createMockProvider("solana");
    algoDex = createMockDex("algorand");

    wallet = AzoaWallet.create({
      algorand: { provider: algoProvider, dex: algoDex },
      solana: { provider: solProvider },
    });
  });

  describe("registration", () => {
    it("lists registered chains", () => {
      expect(wallet.chains).toEqual(["algorand", "solana"]);
    });

    it("gets provider by chainId", () => {
      expect(wallet.getProvider("algorand")).toBe(algoProvider);
      expect(wallet.getProvider("ethereum")).toBeUndefined();
    });

    it("gets dex adapter by chainId", () => {
      expect(wallet.getDex("algorand")).toBe(algoDex);
      expect(wallet.getDex("solana")).toBeUndefined();
    });
  });

  describe("unified queries", () => {
    it("delegates getBalance to correct provider", async () => {
      const result = await wallet.getBalance("algorand", "ADDR123");
      expect(isOk(result)).toBe(true);
      expect(algoProvider.getBalance).toHaveBeenCalledWith("ADDR123", undefined);
    });

    it("returns error for unknown chain", async () => {
      const result = await wallet.getBalance("ethereum", "ADDR123");
      expect(isErr(result)).toBe(true);
      if (!result.ok) expect(result.error.code).toBe(SdkErrorCode.PROVIDER_NOT_FOUND);
    });

    it("delegates validateAddress", async () => {
      await wallet.validateAddress("solana", "ADDR456");
      expect(solProvider.validateAddress).toHaveBeenCalledWith("ADDR456");
    });

    it("delegates getAssets", async () => {
      await wallet.getAssets("algorand", "ADDR789");
      expect(algoProvider.getAssets).toHaveBeenCalledWith("ADDR789");
    });

    it("delegates getChainInfo", async () => {
      await wallet.getChainInfo("solana");
      expect(solProvider.getChainInfo).toHaveBeenCalled();
    });
  });

  describe("unified transactions", () => {
    it("delegates buildTransfer", async () => {
      const params = { from: "A", to: "B", amount: "1.0" };
      const result = await wallet.buildTransfer("algorand", params);
      expect(isOk(result)).toBe(true);
      expect(algoProvider.buildTransfer).toHaveBeenCalledWith(params);
    });

    it("delegates signTransaction", async () => {
      const tx = { chain: "algorand", format: "json-descriptor" as const, bytes: new Uint8Array([1]) };
      const signer = createMockSigner();
      await wallet.signTransaction("algorand", tx, signer);
      expect(algoProvider.signTransaction).toHaveBeenCalledWith(tx, signer);
    });

    it("delegates submitTransaction", async () => {
      const signed = new Uint8Array([1, 2, 3]);
      await wallet.submitTransaction("algorand", signed);
      expect(algoProvider.submitTransaction).toHaveBeenCalledWith(signed);
    });
  });

  describe("unified DEX", () => {
    it("delegates getSwapQuote to dex adapter", async () => {
      const params = { tokenIn: "A", tokenOut: "B", amountIn: "100", slippageBps: 50, sender: "ADDR" };
      const result = await wallet.getSwapQuote("algorand", params);
      expect(isOk(result)).toBe(true);
      expect(algoDex.getQuote).toHaveBeenCalledWith(params);
    });

    it("returns error for chain without dex", async () => {
      const params = { tokenIn: "A", tokenOut: "B", amountIn: "100", slippageBps: 50, sender: "ADDR" };
      const result = await wallet.getSwapQuote("solana", params);
      expect(isErr(result)).toBe(true);
    });

    it("delegates buildSwap", async () => {
      const quote: SwapQuote = { chain: "algorand", tokenIn: "A", tokenOut: "B", amountIn: "100", expectedAmountOut: "95", priceImpact: 0.5, fee: "5" };
      await wallet.buildSwap("algorand", quote, "SENDER");
      expect(algoDex.buildSwapTransaction).toHaveBeenCalledWith(quote, "SENDER");
    });
  });
});
