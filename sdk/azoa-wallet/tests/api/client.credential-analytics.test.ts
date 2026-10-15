/**
 * Credential revocation (D19) + analytics read surface (D16) on AzoaApiClient.
 * Covers: API_PATHS construction, revokeCredential body/Idempotency-Key, the
 * PendingConfirmation (202) outcome surfacing as NOT confirmed, analytics query
 * building, and the unpriced / unknown-quantity flags surviving the round trip.
 */

import { describe, it, expect, vi, beforeEach } from "vitest";
import { AzoaApiClient, isCredentialRevocationPending } from "../../src/api/client.js";
import type { CredentialRevokeResult, TransactionHistoryPage, PositionsResponse } from "../../src/api/client.js";
import { API_PATHS } from "../../src/api/api-version.js";
import { isOk, isErr } from "../../src/core/result.js";
import { SdkErrorCode } from "../../src/core/errors.js";
import { ApiConfigBuilder } from "../builders/index.js";

const mockFetch = vi.fn();
vi.stubGlobal("fetch", mockFetch);

function azoaResponse<T>(result: T, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve({ isError: false, message: "Success", result }),
  });
}

/** The controller's 202 shape: isError + a stable code + the populated result. */
function azoaPendingRevocation(result: CredentialRevokeResult, message: string) {
  return Promise.resolve({
    ok: true,
    status: 202,
    json: () =>
      Promise.resolve({ isError: true, message, code: "REVOCATION_PENDING_CONFIRMATION", result }),
  });
}

function revokeResult(overrides: Partial<CredentialRevokeResult> = {}): CredentialRevokeResult {
  return {
    assetId: "12345",
    credentialId: "11111111-1111-1111-1111-111111111111",
    issuerUserId: "22222222-2222-2222-2222-222222222222",
    holderUserId: "33333333-3333-3333-3333-333333333333",
    holderAddress: "HOLDERADDR",
    recipientAddress: "ISSUERADDR",
    chainType: "Algorand",
    status: "Completed",
    isConfirmed: true,
    txHash: "TX123",
    operationId: "44444444-4444-4444-4444-444444444444",
    replayed: false,
    ...overrides,
  };
}

describe("API_PATHS — credential + analytics", () => {
  it("CREDENTIAL_REVOKE builds /api/credential/{assetId}/revoke", () => {
    expect(API_PATHS.CREDENTIAL_REVOKE("12345")).toBe("/api/credential/12345/revoke");
  });

  it("CREDENTIAL_REVOKE URL-encodes the asset id", () => {
    expect(API_PATHS.CREDENTIAL_REVOKE("a/b c")).toBe("/api/credential/a%2Fb%20c/revoke");
  });

  it("analytics paths match AnalyticsController routes", () => {
    expect(API_PATHS.ANALYTICS_TRANSACTIONS).toBe("/api/analytics/transactions");
    expect(API_PATHS.ANALYTICS_POSITIONS).toBe("/api/analytics/positions");
  });
});

describe("AzoaApiClient credential + analytics", () => {
  let client: AzoaApiClient;

  beforeEach(() => {
    mockFetch.mockReset();
    client = new AzoaApiClient(new ApiConfigBuilder().withToken("test-jwt-token").build());
  });

  describe("revokeCredential", () => {
    it("POSTs to /api/credential/{assetId}/revoke with the body and no Idempotency-Key by default", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse(revokeResult()));
      const result = await client.revokeCredential("12345", { chainType: "Algorand", reason: "expired" });

      expect(isOk(result)).toBe(true);
      expect(mockFetch).toHaveBeenCalledWith(
        "http://localhost:5000/api/credential/12345/revoke",
        expect.objectContaining({ method: "POST" })
      );
      const body = JSON.parse(mockFetch.mock.calls[0][1].body);
      expect(body.chainType).toBe("Algorand");
      expect(body.reason).toBe("expired");
      const headers = mockFetch.mock.calls[0][1].headers as Record<string, string>;
      expect(headers["Idempotency-Key"]).toBeUndefined();
    });

    it("includes the Idempotency-Key header when supplied", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse(revokeResult({ replayed: true })));
      const result = await client.revokeCredential("12345", { chainType: "Algorand" }, { idempotencyKey: "revoke-key-1" });

      const headers = mockFetch.mock.calls[0][1].headers as Record<string, string>;
      expect(headers["Idempotency-Key"]).toBe("revoke-key-1");
      expect(isOk(result)).toBe(true);
      if (result.ok) expect(result.value.replayed).toBe(true);
    });

    it("a Completed result is confirmed", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse(revokeResult()));
      const result = await client.revokeCredential("12345", { chainType: "Algorand" });

      expect(isOk(result)).toBe(true);
      if (result.ok) {
        expect(result.value.status).toBe("Completed");
        expect(result.value.isConfirmed).toBe(true);
        expect(result.value.txHash).toBe("TX123");
      }
    });

    it("a PendingConfirmation (202) result is NOT surfaced as a confirmed success", async () => {
      mockFetch.mockReturnValueOnce(
        azoaPendingRevocation(
          revokeResult({ status: "PendingConfirmation", isConfirmed: false }),
          "Revocation of credential 12345 was submitted (tx TX123) but has NOT confirmed."
        )
      );
      const result = await client.revokeCredential("12345", { chainType: "Algorand" });

      expect(isOk(result)).toBe(false);
      expect(isErr(result)).toBe(true);
      if (!result.ok) {
        expect(result.error.code).toBe(SdkErrorCode.API_ERROR);
        expect(result.error.status).toBe(202);
        expect(result.error.message).toContain("NOT confirmed");
        expect(isCredentialRevocationPending(result.error)).toBe(true);
      }
    });

    it("a 4xx failure is not mistaken for a pending revocation", async () => {
      mockFetch.mockReturnValueOnce(
        Promise.resolve({
          ok: false,
          status: 403,
          json: () => Promise.resolve({ isError: true, message: "Caller is not the recorded issuer.", result: null }),
        })
      );
      const result = await client.revokeCredential("12345", { chainType: "Algorand" });

      expect(isErr(result)).toBe(true);
      if (!result.ok) {
        expect(result.error.status).toBe(403);
        expect(isCredentialRevocationPending(result.error)).toBe(false);
      }
    });

    it("rejects an empty assetId before any request is made", async () => {
      await expect(client.revokeCredential("", { chainType: "Algorand" })).rejects.toThrow(/assetId/);
      expect(mockFetch).not.toHaveBeenCalled();
    });
  });

  describe("getAnalyticsTransactions", () => {
    it("GETs /api/analytics/transactions with no querystring when no query is supplied", async () => {
      const page: TransactionHistoryPage = {
        items: [],
        skip: 0,
        take: 50,
        hasMore: false,
        unpricedEntryCount: 0,
        unknownAmountEntryCount: 0,
      };
      mockFetch.mockReturnValueOnce(azoaResponse(page));
      const result = await client.getAnalyticsTransactions();

      expect(isOk(result)).toBe(true);
      expect(mockFetch.mock.calls[0][0]).toBe("http://localhost:5000/api/analytics/transactions");
      expect(mockFetch.mock.calls[0][1].method).toBe("GET");
    });

    it("encodes from/to/skip/take as querystring and omits undefined keys", async () => {
      mockFetch.mockReturnValueOnce(
        azoaResponse({ items: [], skip: 10, take: 20, hasMore: false, unpricedEntryCount: 0, unknownAmountEntryCount: 0 })
      );
      await client.getAnalyticsTransactions({ from: "2026-01-01T00:00:00Z", skip: 10, take: 20 });

      const url = mockFetch.mock.calls[0][0] as string;
      expect(url.startsWith("http://localhost:5000/api/analytics/transactions?")).toBe(true);
      expect(url).toContain("from=2026-01-01T00%3A00%3A00Z");
      expect(url).toContain("skip=10");
      expect(url).toContain("take=20");
      expect(url).not.toContain("to=");
    });

    it("keeps unpriced / unknown-quantity rows flagged rather than zeroed (D16)", async () => {
      const page: TransactionHistoryPage = {
        items: [
          {
            operationId: "55555555-5555-5555-5555-555555555555",
            timestamp: "2026-09-01T00:00:00Z",
            operationType: "Transfer",
            status: "Completed",
            chainType: "Algorand",
            amountStatus: "Unknown",
            isStampable: true,
            pricing: "NeverPriced",
          },
        ],
        skip: 0,
        take: 50,
        hasMore: false,
        unpricedEntryCount: 1,
        unknownAmountEntryCount: 1,
      };
      mockFetch.mockReturnValueOnce(azoaResponse(page));
      const result = await client.getAnalyticsTransactions();

      expect(isOk(result)).toBe(true);
      if (result.ok) {
        const [entry] = result.value.items;
        expect(entry.pricing).toBe("NeverPriced");
        expect(entry.value).toBeUndefined();
        expect(entry.amountStatus).toBe("Unknown");
        expect(entry.amount).toBeUndefined();
        expect(result.value.unpricedEntryCount).toBe(1);
        expect(result.value.unknownAmountEntryCount).toBe(1);
      }
    });
  });

  describe("getAnalyticsPositions", () => {
    it("GETs /api/analytics/positions with the window as querystring", async () => {
      const positions: PositionsResponse = {
        items: [
          {
            chainType: "Algorand",
            quantity: "-1500",
            entryCount: 3,
            unsettledEntryCount: 1,
            unclassifiedEntryCount: 0,
            unknownAmountEntryCount: 0,
            isQuantityComplete: false,
            pricedEntryCount: 0,
            unpricedEntryCount: 3,
            unstampableEntryCount: 0,
            isValueComplete: false,
          },
        ],
        scannedEntryCount: 3,
        isComplete: true,
      };
      mockFetch.mockReturnValueOnce(azoaResponse(positions));
      const result = await client.getAnalyticsPositions({ from: "2026-01-01T00:00:00Z", to: "2026-12-31T00:00:00Z" });

      expect(isOk(result)).toBe(true);
      const url = mockFetch.mock.calls[0][0] as string;
      expect(url.startsWith("http://localhost:5000/api/analytics/positions?")).toBe(true);
      expect(url).toContain("from=2026-01-01T00%3A00%3A00Z");
      expect(url).toContain("to=2026-12-31T00%3A00%3A00Z");
      expect(mockFetch.mock.calls[0][1].method).toBe("GET");
      if (result.ok) {
        const [position] = result.value.items;
        expect(position.quantity).toBe("-1500");
        expect(position.isValueComplete).toBe(false);
        expect(position.realizedValue).toBeUndefined();
      }
    });
  });
});
