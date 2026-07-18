import { describe, it, expect, vi, beforeEach } from "vitest";
import { AlgorandProvider } from "../../src/algorand/provider.js";
import { isOk, isErr } from "../../src/core/result.js";
import { AlgorandConfigBuilder, createMockSigner } from "../builders/index.js";

// Mock global fetch
const mockFetch = vi.fn();
vi.stubGlobal("fetch", mockFetch);

function jsonResponse(data: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(data),
  });
}

describe("AlgorandProvider", () => {
  let provider: AlgorandProvider;

  beforeEach(() => {
    mockFetch.mockReset();
    provider = new AlgorandProvider(
      new AlgorandConfigBuilder()
        .onTestnet()
        .withIndexer("https://testnet-idx.algonode.cloud")
        .build()
    );
  });

  describe("properties", () => {
    it("has correct chainId", () => {
      expect(provider.chainId).toBe("algorand");
    });

    it("has correct displayName", () => {
      expect(provider.displayName).toBe("Algorand");
    });

    it("supports DEX and bridging", () => {
      expect(provider.supportsDex).toBe(true);
      expect(provider.supportsBridging).toBe(true);
    });
  });

  describe("getBalance", () => {
    it("returns ALGO balance for native currency", async () => {
      mockFetch.mockReturnValueOnce(jsonResponse({ amount: 5_000_000, assets: [] }));

      const result = await provider.getBalance("7J6ZZGF2UPNKKBCJA4DHFKVL6LXGKKDQM6KX4YZ5J5H5F7ZJGX6W4PUJJY");
      expect(isOk(result)).toBe(true);
      if (result.ok) {
        expect(result.value.symbol).toBe("ALGO");
        expect(result.value.amount).toBe("5.000000");
        expect(result.value.decimals).toBe(6);
      }
    });

    it("returns ASA balance when tokenId provided", async () => {
      mockFetch.mockReturnValueOnce(jsonResponse({
        amount: 1_000_000,
        assets: [{ "asset-id": 123, amount: 500 }],
      }));

      const result = await provider.getBalance("7J6ZZGF2UPNKKBCJA4DHFKVL6LXGKKDQM6KX4YZ5J5H5F7ZJGX6W4PUJJY", "123");
      expect(isOk(result)).toBe(true);
      if (result.ok) {
        expect(result.value.amount).toBe("500");
        expect(result.value.symbol).toBe("ASA#123");
      }
    });

    it("returns 0 for unowned ASA", async () => {
      mockFetch.mockReturnValueOnce(jsonResponse({ amount: 1_000_000, assets: [] }));

      const result = await provider.getBalance("7J6ZZGF2UPNKKBCJA4DHFKVL6LXGKKDQM6KX4YZ5J5H5F7ZJGX6W4PUJJY", "999");
      expect(isOk(result)).toBe(true);
      if (result.ok) expect(result.value.amount).toBe("0");
    });

    it("returns error on network failure", async () => {
      mockFetch.mockRejectedValueOnce(new TypeError("fetch failed"));

      const result = await provider.getBalance("7J6ZZGF2UPNKKBCJA4DHFKVL6LXGKKDQM6KX4YZ5J5H5F7ZJGX6W4PUJJY");
      expect(isErr(result)).toBe(true);
    });
  });

  describe("validateAddress", () => {
    it("accepts valid 58-char base32 address", async () => {
      const result = await provider.validateAddress("7J6ZZGF2UPNKKBCJA4DHFKVL6LXGKKDQM6KX4YZ5J5H5F7ZJGX6W4PUJJY");
      expect(isOk(result)).toBe(true);
      if (result.ok) expect(result.value).toBe(true);
    });

    it("rejects short address", async () => {
      const result = await provider.validateAddress("TOOSHORT");
      expect(isOk(result)).toBe(true);
      if (result.ok) expect(result.value).toBe(false);
    });

    it("rejects invalid characters", async () => {
      const result = await provider.validateAddress("0".repeat(58)); // 0 is not base32
      expect(isOk(result)).toBe(true);
      if (result.ok) expect(result.value).toBe(false);
    });
  });

  describe("buildTransfer", () => {
    it("builds payment tx for native ALGO", async () => {
      mockFetch.mockReturnValueOnce(jsonResponse({
        "min-fee": 1000, "last-round": 100, "genesis-hash": "abc", "genesis-id": "testnet-v1.0",
      }));

      const result = await provider.buildTransfer({
        from: "SENDER234567890123456789012345678901234567890ABCDEFGH",
        to: "RECEIVER34567890123456789012345678901234567890ABCDEFG",
        amount: "1.5",
      });

      expect(isOk(result)).toBe(true);
      if (result.ok) {
        expect(result.value.chain).toBe("algorand");
        expect(result.value.format).toBe("json-descriptor");
        const txObj = JSON.parse(new TextDecoder().decode(result.value.bytes));
        expect(txObj.type).toBe("pay");
        expect(txObj.amount).toBe(1500000); // 1.5 ALGO in microAlgos
      }
    });

    it("builds ASA transfer tx when tokenId present", async () => {
      mockFetch.mockReturnValueOnce(jsonResponse({
        "min-fee": 1000, "last-round": 100, "genesis-hash": "abc", "genesis-id": "testnet-v1.0",
      }));

      const result = await provider.buildTransfer({
        from: "SENDER234567890123456789012345678901234567890ABCDEFGH",
        to: "RECEIVER34567890123456789012345678901234567890ABCDEFG",
        amount: "100",
        tokenId: "12345",
      });

      expect(isOk(result)).toBe(true);
      if (result.ok) {
        const txObj = JSON.parse(new TextDecoder().decode(result.value.bytes));
        expect(txObj.type).toBe("axfer");
        expect(txObj.assetIndex).toBe(12345);
      }
    });
  });

  describe("buildMint", () => {
    it("builds ASA creation tx", async () => {
      mockFetch.mockReturnValueOnce(jsonResponse({
        "min-fee": 1000, "last-round": 100, "genesis-hash": "abc", "genesis-id": "testnet-v1.0",
      }));

      const result = await provider.buildMint({
        name: "TestToken",
        symbol: "TST",
        totalSupply: "1000000",
        decimals: 6,
        creator: "CREATOR567890123456789012345678901234567890ABCDEFGHI",
      });

      expect(isOk(result)).toBe(true);
      if (result.ok) {
        expect(result.value.format).toBe("json-descriptor");
        const txObj = JSON.parse(new TextDecoder().decode(result.value.bytes));
        expect(txObj.type).toBe("acfg");
        expect(txObj.assetName).toBe("TestToken");
        expect(txObj.assetUnitName).toBe("TST");
      }
    });
  });

  describe("signTransaction + submitTransaction", () => {
    it("signs with provided signer", async () => {
      const signer = createMockSigner();
      const tx = { chain: "algorand", format: "json-descriptor" as const, bytes: new Uint8Array([1, 2, 3]) };
      const result = await provider.signTransaction(tx, signer);
      expect(isOk(result)).toBe(true);
      if (result.ok) expect(result.value.length).toBe(64);
    });

    it("submits signed tx to algod", async () => {
      mockFetch.mockReturnValueOnce(jsonResponse({ txId: "TXHASH123" }));

      const result = await provider.submitTransaction(new Uint8Array([1, 2, 3]));
      expect(isOk(result)).toBe(true);
      if (result.ok) {
        expect(result.value.txHash).toBe("TXHASH123");
        expect(result.value.chain).toBe("algorand");
        expect(result.value.status).toBe("submitted");
      }
    });
  });

  describe("getChainInfo", () => {
    it("returns chain status", async () => {
      mockFetch.mockReturnValueOnce(jsonResponse({ "last-round": 50000, "last-version": "v2" }));

      const result = await provider.getChainInfo();
      expect(isOk(result)).toBe(true);
      if (result.ok) {
        expect(result.value.chain).toBe("algorand");
        expect(result.value.lastRound).toBe("50000");
      }
    });
  });
});
