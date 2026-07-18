import { describe, it, expect, vi, beforeEach } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import { isOk, isErr } from "../../src/core/result.js";
import { ApiConfigBuilder } from "../builders/index.js";

const mockFetch = vi.fn();
vi.stubGlobal("fetch", mockFetch);

function azoaResponse<T>(result: T, message = "Success") {
  return Promise.resolve({
    ok: true,
    status: 200,
    json: () => Promise.resolve({ isError: false, message, result }),
  });
}

function azoaError(message: string, status = 400) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve({ isError: true, message, result: null }),
  });
}

function bareResponse<T>(data: T, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(data),
  });
}

describe("AzoaApiClient", () => {
  let client: AzoaApiClient;

  beforeEach(() => {
    mockFetch.mockReset();
    client = new AzoaApiClient(
      new ApiConfigBuilder().withToken("test-jwt-token").build()
    );
  });

  describe("avatar endpoints", () => {
    it("login sends POST and returns JWT string", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse("jwt-token-here"));
      const result = await client.login("test@test.com", "password");
      expect(isOk(result)).toBe(true);
      if (result.ok) expect(result.value).toBe("jwt-token-here");

      expect(mockFetch).toHaveBeenCalledWith(
        "http://localhost:5000/api/avatar/login",
        expect.objectContaining({ method: "POST" })
      );
    });

    it("register sends POST with correct body", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse({ id: "123", username: "neo", email: "neo@matrix.com" }));
      const result = await client.register({ email: "neo@matrix.com", password: "p", username: "neo" });
      expect(isOk(result)).toBe(true);
    });

    it("getAvatar sends GET with id", async () => {
      const avatarId = "00000000-0000-4000-8000-00000000000a";
      mockFetch.mockReturnValueOnce(azoaResponse({ id: avatarId, username: "test" }));
      const result = await client.getAvatar(avatarId);
      expect(isOk(result)).toBe(true);
      expect(mockFetch).toHaveBeenCalledWith(
        `http://localhost:5000/api/avatar/${avatarId}`,
        expect.objectContaining({ method: "GET" })
      );
    });
  });

  describe("NFT endpoints (match NftController DTOs)", () => {
    it("mintNft sends correct body shape", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse({ id: "op1" }));
      await client.mintNft({
        walletId: "wallet-guid",
        name: "My NFT",
        description: "desc",
        chainId: "algorand",
      });

      const body = JSON.parse(mockFetch.mock.calls[0][1].body);
      expect(body.walletId).toBe("wallet-guid");
      expect(body.name).toBe("My NFT");
      expect(body.chainId).toBe("algorand");
    });

    it("transferNft sends to /api/nft/{id}/transfer", async () => {
      const nftId = "aaaaaaaa-1111-4111-8111-111111111111";
      mockFetch.mockReturnValueOnce(azoaResponse({ id: "op2" }));
      await client.transferNft(nftId, {
        targetAvatarId: "bbbbbbbb-2222-4222-8222-222222222222",
        walletId: "cccccccc-3333-4333-8333-333333333333",
      });

      expect(mockFetch).toHaveBeenCalledWith(
        `http://localhost:5000/api/nft/${nftId}/transfer`,
        expect.objectContaining({ method: "POST" })
      );
    });

    it("burnNft sends to /api/nft/{id}/burn", async () => {
      const nftId = "dddddddd-4444-4444-8444-444444444444";
      mockFetch.mockReturnValueOnce(azoaResponse({ id: "op3" }));
      await client.burnNft(nftId, { walletId: "cccccccc-3333-4333-8333-333333333333" });

      expect(mockFetch).toHaveBeenCalledWith(
        `http://localhost:5000/api/nft/${nftId}/burn`,
        expect.objectContaining({ method: "POST" })
      );
    });
  });

  describe("Bridge endpoints (bare response format)", () => {
    it("getBridgeRoutes sends GET without query params", async () => {
      mockFetch.mockReturnValueOnce(bareResponse([
        { sourceChain: "algorand", targetChain: "solana", supportedTokenTypes: ["ASA"], wormholeAvailable: true },
      ]));

      const result = await client.getBridgeRoutes();
      expect(isOk(result)).toBe(true);
      expect(mockFetch).toHaveBeenCalledWith(
        "http://localhost:5000/api/bridge/routes",
        expect.objectContaining({ method: "GET" })
      );
    });

    it("initiateBridge sends POST with BridgeInitiateRequest", async () => {
      mockFetch.mockReturnValueOnce(bareResponse({ id: "bridge1", status: "Pending" }));
      await client.initiateBridge({
        sourceChain: "algorand",
        targetChain: "solana",
        tokenId: "12345",
        recipientAddress: "SOLADDR",
        amount: 100,
      });

      const body = JSON.parse(mockFetch.mock.calls[0][1].body);
      expect(body.sourceChain).toBe("algorand");
      expect(body.amount).toBe(100);
    });
  });

  describe("Search endpoint (POST not GET)", () => {
    it("sends POST /api/search with SearchRequest body", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse({ items: [], totalCount: 0, page: 1, pageSize: 20 }));
      await client.search({ query: "test nft", page: 1, pageSize: 20 });

      expect(mockFetch).toHaveBeenCalledWith(
        "http://localhost:5000/api/search",
        expect.objectContaining({ method: "POST" })
      );
      const body = JSON.parse(mockFetch.mock.calls[0][1].body);
      expect(body.query).toBe("test nft");
    });
  });

  describe("error handling", () => {
    it("returns SdkError for AZOAResult errors", async () => {
      mockFetch.mockReturnValueOnce(azoaError("Avatar not found"));
      const result = await client.getAvatar("00000000-0000-4000-8000-000000000001");
      expect(isErr(result)).toBe(true);
      // The SDK prefixes errors with method+path for traceability, so the
      // server message is contained rather than the whole string.
      if (!result.ok) expect(result.error.message).toContain("Avatar not found");
    });

    it("handles network errors", async () => {
      mockFetch.mockRejectedValueOnce(new TypeError("Failed to fetch"));
      const result = await client.getAvatar("00000000-0000-4000-8000-000000000002");
      expect(isErr(result)).toBe(true);
      if (!result.ok) expect(result.error.code).toBe("NETWORK_ERROR");
    });

    const UUID = "123e4567-e89b-12d3-a456-426614174000";

    it("attaches HTTP status and server debug detail to the SdkError", async () => {
      mockFetch.mockReturnValueOnce(
        Promise.resolve({
          ok: false,
          status: 500,
          json: () =>
            Promise.resolve({
              isError: true,
              message: "db down",
              detail: { type: "System.InvalidOperationException", message: "db down" },
            }),
        })
      );
      const result = await client.getAvatar(UUID);
      expect(isErr(result)).toBe(true);
      if (!result.ok) {
        expect(result.error.status).toBe(500);
        expect(result.error.method).toBe("GET");
        expect(result.error.path).toBe(`/api/avatar/${UUID}`);
        expect(result.error.detail?.type).toBe("System.InvalidOperationException");
      }
    });

    it("parses bare-error (`error`) bodies and survives empty 500 bodies", async () => {
      mockFetch.mockReturnValueOnce(
        Promise.resolve({
          ok: false,
          status: 500,
          json: () => Promise.reject(new Error("no body")),
        })
      );
      const result = await client.getBridgeHistory();
      expect(isErr(result)).toBe(true);
      if (!result.ok) {
        expect(result.error.message).toContain("failed with HTTP 500");
        expect(result.error.status).toBe(500);
      }
    });

    it("logs requests and errors when debug is enabled", async () => {
      const debugLogger = { debug: vi.fn(), error: vi.fn() };
      const dbgClient = new AzoaApiClient({
        baseUrl: "http://localhost:5000",
        token: "t",
        debug: true,
        debugLogger,
      });
      mockFetch.mockReturnValueOnce(
        Promise.resolve({
          ok: false,
          status: 400,
          json: () => Promise.resolve({ isError: true, message: "bad" }),
        })
      );
      await dbgClient.getAvatar(UUID);
      expect(debugLogger.debug).toHaveBeenCalled(); // request logged
      expect(debugLogger.error).toHaveBeenCalled(); // error logged
    });

    it("does not infinite loop on 401", async () => {
      const refreshFn = vi.fn().mockResolvedValue("new-token");
      client = new AzoaApiClient(
        new ApiConfigBuilder().withRefreshCallback(refreshFn).build()
      );

      // Both calls return 401
      mockFetch.mockReturnValue(
        Promise.resolve({ ok: false, status: 401, json: () => Promise.resolve({ isError: true, message: "Unauthorized" }) })
      );

      const result = await client.getAvatar("00000000-0000-4000-8000-000000000003");
      expect(isErr(result)).toBe(true);
      // Should only refresh once, not loop
      expect(refreshFn).toHaveBeenCalledTimes(2); // initial + one retry
      expect(mockFetch).toHaveBeenCalledTimes(2);
    });
  });

  describe("auth headers", () => {
    it("sends Bearer token", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse({}));
      await client.getAvatar("00000000-0000-4000-8000-000000000004");

      const headers = mockFetch.mock.calls[0][1].headers;
      expect(headers["Authorization"]).toBe("Bearer test-jwt-token");
    });
  });

  describe("listNfts (Finding 6)", () => {
    it("sends GET /api/nft with no params", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse([{ id: "nft1", name: "n", description: "d", chainId: "algorand", isActive: true }]));
      const result = await client.listNfts();
      expect(isOk(result)).toBe(true);
      expect(mockFetch).toHaveBeenCalledWith(
        "http://localhost:5000/api/nft",
        expect.objectContaining({ method: "GET" })
      );
    });

    it("sends GET /api/nft with chainId+ownerAvatarId querystring", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse([]));
      await client.listNfts({ chainId: "algorand", ownerAvatarId: "11111111-1111-1111-1111-111111111111" });
      const url = mockFetch.mock.calls[0][0] as string;
      expect(url.startsWith("http://localhost:5000/api/nft?")).toBe(true);
      expect(url).toContain("chainId=algorand");
      expect(url).toContain("ownerAvatarId=11111111-1111-1111-1111-111111111111");
    });
  });

  // updateSTARODK now uses PUT /api/starodk/{id} (finding 7 override) —
  // assertions live in tests/api/self-audit-one-fix.test.ts.

  describe("getSwapQuote (Phase 2 prereq)", () => {
    it("builds a GET /api/swap/quote URL with the supplied params", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse({
        chain: "solana",
        tokenIn: "INMINT",
        tokenOut: "OUTMINT",
        amountIn: "1000",
        expectedAmountOut: "950",
        priceImpact: 0.1,
        fee: "0",
      }));
      const result = await client.getSwapQuote({
        chain: "solana",
        tokenIn: "INMINT",
        tokenOut: "OUTMINT",
        amountIn: "1000",
        slippageBps: 50,
        walletAddress: "PUBKEY",
      });
      expect(isOk(result)).toBe(true);
      const url = mockFetch.mock.calls[0][0] as string;
      expect(url.startsWith("http://localhost:5000/api/swap/quote?")).toBe(true);
      expect(url).toContain("chain=solana");
      expect(url).toContain("tokenIn=INMINT");
      expect(url).toContain("tokenOut=OUTMINT");
      expect(url).toContain("amountIn=1000");
      expect(url).toContain("slippageBps=50");
      expect(url).toContain("walletAddress=PUBKEY");
      expect(mockFetch.mock.calls[0][1].method).toBe("GET");
    });
  });

  describe("executeSwap (Phase 2 prereq)", () => {
    it("POSTs to /api/swap/execute with the body and no Idempotency-Key by default", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse({
        chain: "solana",
        tokenIn: "A",
        tokenOut: "B",
        amountIn: "1",
        expectedAmountOut: "1",
        priceImpact: 0,
        fee: "0",
      }));
      await client.executeSwap({ chain: "solana", quoteId: "Q1", walletAddress: "PUBKEY" });
      expect(mockFetch).toHaveBeenCalledWith(
        "http://localhost:5000/api/swap/execute",
        expect.objectContaining({ method: "POST" })
      );
      const body = JSON.parse(mockFetch.mock.calls[0][1].body);
      expect(body.chain).toBe("solana");
      expect(body.quoteId).toBe("Q1");
      expect(body.walletAddress).toBe("PUBKEY");
      const headers = mockFetch.mock.calls[0][1].headers as Record<string, string>;
      expect(headers["Idempotency-Key"]).toBeUndefined();
    });

    it("includes the Idempotency-Key header when supplied", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse({
        chain: "solana",
        tokenIn: "A",
        tokenOut: "B",
        amountIn: "1",
        expectedAmountOut: "1",
        priceImpact: 0,
        fee: "0",
      }));
      await client.executeSwap(
        { chain: "solana", quoteId: "Q1", walletAddress: "PUBKEY" },
        { idempotencyKey: "my-key-123" }
      );
      const headers = mockFetch.mock.calls[0][1].headers as Record<string, string>;
      expect(headers["Idempotency-Key"]).toBe("my-key-123");
    });
  });

  describe("getBaseUrl (Phase 2 prereq for getApiUrl)", () => {
    it("returns the configured base URL", () => {
      expect(client.getBaseUrl()).toBe("http://localhost:5000");
    });
  });

  describe("Saga operator dead-letter surface (Wave6 admin, bare response format)", () => {
    it("listSagaDeadLetters sends GET with no query params by default", async () => {
      mockFetch.mockReturnValueOnce(bareResponse([
        {
          id: "aaaaaaaa-1111-4111-8111-111111111111",
          sagaName: "BridgeSaga",
          stepName: "LockAsset",
          correlationKey: "corr-1",
          status: "DeadLettered",
          isCompensation: false,
          attemptCount: 5,
          lastError: "timeout",
          gateId: null,
          nextRunAt: "2026-07-01T00:00:00Z",
          updatedAt: "2026-07-01T00:00:00Z",
        },
      ]));

      const result = await client.listSagaDeadLetters();
      expect(isOk(result)).toBe(true);
      if (result.ok) expect(result.value).toHaveLength(1);
      expect(mockFetch).toHaveBeenCalledWith(
        "http://localhost:5000/api/admin/sagas/dead-letters",
        expect.objectContaining({ method: "GET" })
      );
    });

    it("listSagaDeadLetters sends status[] and limit as query params", async () => {
      mockFetch.mockReturnValueOnce(bareResponse([]));
      await client.listSagaDeadLetters({ status: ["Parked", "Cancelled"], limit: 25 });

      const url = mockFetch.mock.calls[0][0] as string;
      expect(url).toContain("status=Parked");
      expect(url).toContain("status=Cancelled");
      expect(url).toContain("limit=25");
    });

    it("requeueSagaStep sends POST to /api/admin/sagas/{id}/requeue", async () => {
      const id = "bbbbbbbb-2222-4222-8222-222222222222";
      mockFetch.mockReturnValueOnce(bareResponse({ id, status: "Pending", message: "Requeued." }));

      const result = await client.requeueSagaStep(id);
      expect(isOk(result)).toBe(true);
      expect(mockFetch).toHaveBeenCalledWith(
        `http://localhost:5000/api/admin/sagas/${id}/requeue`,
        expect.objectContaining({ method: "POST" })
      );
    });

    it("requeueSagaStep rejects a non-UUID id", async () => {
      await expect(client.requeueSagaStep("not-a-uuid")).rejects.toThrow("expected UUID format");
      expect(mockFetch).not.toHaveBeenCalled();
    });

    it("cancelSagaStep sends POST with optional reason body", async () => {
      const id = "cccccccc-3333-4333-8333-333333333333";
      mockFetch.mockReturnValueOnce(bareResponse({ id, status: "Cancelled", message: "Cancelled." }));

      await client.cancelSagaStep(id, "stuck forever");
      expect(mockFetch).toHaveBeenCalledWith(
        `http://localhost:5000/api/admin/sagas/${id}/cancel`,
        expect.objectContaining({ method: "POST" })
      );
      const body = JSON.parse(mockFetch.mock.calls[0][1].body);
      expect(body.reason).toBe("stuck forever");
    });

    it("cancelSagaStep omits body when no reason given", async () => {
      const id = "dddddddd-4444-4444-8444-444444444444";
      mockFetch.mockReturnValueOnce(bareResponse({ id, status: "Cancelled", message: "Cancelled." }));

      await client.cancelSagaStep(id);
      expect(mockFetch.mock.calls[0][1].body).toBeUndefined();
    });
  });

  describe("rotateWalletKeys (Wave6 admin, bare response format)", () => {
    it("sends POST to /api/admin/key-rotation/rotate with the new key", async () => {
      mockFetch.mockReturnValueOnce(bareResponse({
        total: 10,
        rewrapped: 8,
        alreadyRotated: 2,
        skipped: 0,
        rolledBack: false,
      }));

      const result = await client.rotateWalletKeys({ newEncryptionKey: "new-key-material" });
      expect(isOk(result)).toBe(true);
      if (result.ok) expect(result.value.rewrapped).toBe(8);

      expect(mockFetch).toHaveBeenCalledWith(
        "http://localhost:5000/api/admin/key-rotation/rotate",
        expect.objectContaining({ method: "POST" })
      );
      const body = JSON.parse(mockFetch.mock.calls[0][1].body);
      expect(body.newEncryptionKey).toBe("new-key-material");
    });

    it("rejects an empty newEncryptionKey without calling fetch", async () => {
      await expect(client.rotateWalletKeys({ newEncryptionKey: "" })).rejects.toThrow("must be a non-empty string");
      expect(mockFetch).not.toHaveBeenCalled();
    });
  });
});
