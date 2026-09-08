// Tests for the W10.3 SDK parity additions: Exchange, off-ramp cash-out and
// public node-transparency reads. Lives under src/ (not tests/) because this
// track owns src/azoa-wallet/src/** only — vitest.config.ts's coverage
// section already anticipates *.test.ts files here (see its `exclude`).

import { describe, it, expect, vi, beforeEach } from "vitest";
import { AzoaApiClient } from "./client.js";
import { isOk, isErr } from "../core/result.js";
import { SdkErrorCode } from "../core/errors.js";

const mockFetch = vi.fn();
vi.stubGlobal("fetch", mockFetch);

function azoaResponse<T>(result: T, message = "Success") {
  return Promise.resolve({
    ok: true,
    status: 200,
    json: () => Promise.resolve({ isError: false, message, result }),
  });
}

const USER_ID = "00000000-0000-4000-8000-00000000000a";

describe("AzoaApiClient — W10.3 parity", () => {
  let client: AzoaApiClient;

  beforeEach(() => {
    mockFetch.mockReset();
    client = new AzoaApiClient({ baseUrl: "http://localhost:5000" });
  });

  describe("exchange (ExchangeController)", () => {
    it("POSTs to /api/exchange/{userId} with the body and the Idempotency-Key header", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse({
        receiptReference: "sha-1",
        operationId: null,
        state: "AwaitingReconciliation",
        isTerminal: false,
        requiresReconciliation: true,
        replayed: false,
        direction: "Buy",
        userId: USER_ID,
        projectReference: "proj-1",
        amountSpent: null,
        amountSpentAssetId: null,
        amountReceived: null,
        amountReceivedAssetId: null,
        projectCreditAmount: null,
        settlementReference: null,
        settlementLegReferences: [],
        holdReference: null,
        holdReleasesAt: null,
        rate: null,
        rateCurrencyCode: null,
        rateSource: null,
        rateVersion: null,
        rateAsOf: null,
        valueMinorUnits: null,
        createdAt: "2026-09-08T00:00:00Z",
        updatedAt: "2026-09-08T00:00:00Z",
        failureCode: null,
      }));

      const result = await client.exchange(
        USER_ID,
        {
          chainType: "Algorand",
          projectReference: "proj-1",
          projectAssetId: "12345",
          platformAmount: "1000000",
          projectUnitAmount: "500000",
          reservePartition: "contributor",
        },
        "exchange-key-1"
      );

      expect(isOk(result)).toBe(true);
      expect(mockFetch).toHaveBeenCalledWith(
        `http://localhost:5000/api/exchange/${USER_ID}`,
        expect.objectContaining({ method: "POST" })
      );
      const call = mockFetch.mock.calls[0][1];
      const headers = call.headers as Record<string, string>;
      expect(headers["Idempotency-Key"]).toBe("exchange-key-1");
      const body = JSON.parse(call.body);
      expect(body.chainType).toBe("Algorand");
      expect(body.reservePartition).toBe("contributor");
    });

    it("rejects a missing idempotencyKey before any request is sent", async () => {
      await expect(
        client.exchange(
          USER_ID,
          {
            chainType: "Algorand",
            projectReference: "proj-1",
            projectAssetId: "12345",
            platformAmount: "1",
            projectUnitAmount: "1",
            reservePartition: "contributor",
          },
          ""
        )
      ).rejects.toMatchObject({ code: SdkErrorCode.INVALID_INPUT });
      expect(mockFetch).not.toHaveBeenCalled();
    });
  });

  describe("getExchangeReceipt (ExchangeController)", () => {
    it("GETs /api/exchange/receipt/{idempotencyKey}", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse({
        receiptReference: "sha-1",
        operationId: "op-1",
        state: "Completed",
        isTerminal: true,
        requiresReconciliation: false,
        replayed: true,
        direction: "Buy",
        userId: USER_ID,
        projectReference: "proj-1",
        amountSpent: "1000000",
        amountSpentAssetId: "0",
        amountReceived: "500000",
        amountReceivedAssetId: "12345",
        projectCreditAmount: "1000000",
        settlementReference: "TXID",
        settlementLegReferences: ["TXID-1", "TXID-2"],
        holdReference: null,
        holdReleasesAt: null,
        rate: "0.01",
        rateCurrencyCode: "USD",
        rateSource: "ardanova:treasury-rate",
        rateVersion: "1",
        rateAsOf: "2026-09-08T00:00:00Z",
        valueMinorUnits: "1000",
        createdAt: "2026-09-08T00:00:00Z",
        updatedAt: "2026-09-08T00:00:00Z",
        failureCode: null,
      }));

      const result = await client.getExchangeReceipt("exchange-key-1");
      expect(isOk(result)).toBe(true);
      expect(mockFetch).toHaveBeenCalledWith(
        "http://localhost:5000/api/exchange/receipt/exchange-key-1",
        expect.objectContaining({ method: "GET" })
      );
    });
  });

  describe("off-ramp cash-out (FiatPayoutController)", () => {
    it("getCashOutAvailability GETs /api/fiat/payouts/{userId}/availability", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse({
        available: true,
        accountReady: true,
        cashableMinorUnits: 5000,
        onHoldMinorUnits: 0,
        availableFrom: null,
        minimumAmountMinorUnits: 100,
        maximumAmountMinorUnits: 1000000,
        currencyCode: "USD",
        statusMessage: "Ready to cash out.",
      }));

      const result = await client.getCashOutAvailability(USER_ID);
      expect(isOk(result)).toBe(true);
      expect(mockFetch).toHaveBeenCalledWith(
        `http://localhost:5000/api/fiat/payouts/${USER_ID}/availability`,
        expect.objectContaining({ method: "GET" })
      );
    });

    it("getPayoutAccountStatus GETs and beginPayoutAccountSetup POSTs the same /account path", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse({
        available: true,
        accountOpened: false,
        readyToReceive: false,
        detailsSubmitted: false,
        outstandingRequirementCount: 0,
        setupUrl: null,
        setupUrlExpiresAt: null,
        statusMessage: "Not set up yet.",
      }));
      await client.getPayoutAccountStatus(USER_ID);
      expect(mockFetch).toHaveBeenCalledWith(
        `http://localhost:5000/api/fiat/payouts/${USER_ID}/account`,
        expect.objectContaining({ method: "GET" })
      );

      mockFetch.mockReturnValueOnce(azoaResponse({
        available: true,
        accountOpened: true,
        readyToReceive: false,
        detailsSubmitted: false,
        outstandingRequirementCount: 2,
        setupUrl: "https://provider.example/setup",
        setupUrlExpiresAt: "2026-09-09T00:00:00Z",
        statusMessage: "Finish setup to receive money.",
      }));
      await client.beginPayoutAccountSetup(USER_ID);
      expect(mockFetch).toHaveBeenCalledWith(
        `http://localhost:5000/api/fiat/payouts/${USER_ID}/account`,
        expect.objectContaining({ method: "POST" })
      );
    });

    it("startCashOut POSTs the request key and amount to /api/fiat/payouts/{userId}", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse({
        id: "payout-1",
        status: "Requested",
        statusMessage: "Getting your money ready to send.",
        amountMinorUnits: 5000,
        currencyCode: "USD",
        requestedAt: "2026-09-08T00:00:00Z",
        paidAt: null,
        reversedAt: null,
      }));

      const result = await client.startCashOut(USER_ID, {
        requestKey: "cash-out-1",
        amountMinorUnits: 5000,
      });
      expect(isOk(result)).toBe(true);
      expect(mockFetch).toHaveBeenCalledWith(
        `http://localhost:5000/api/fiat/payouts/${USER_ID}`,
        expect.objectContaining({ method: "POST" })
      );
      const body = JSON.parse(mockFetch.mock.calls[0][1].body);
      expect(body.requestKey).toBe("cash-out-1");
      expect(body.amountMinorUnits).toBe(5000);
    });

    it("rejects an empty requestKey before any request is sent", async () => {
      await expect(
        client.startCashOut(USER_ID, { requestKey: "  ", amountMinorUnits: 5000 })
      ).rejects.toMatchObject({ code: SdkErrorCode.INVALID_INPUT });
      expect(mockFetch).not.toHaveBeenCalled();
    });

    it("listCashOutHistory GETs /api/fiat/payouts/{userId} with an optional limit", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse([]));
      await client.listCashOutHistory(USER_ID, 10);
      expect(mockFetch).toHaveBeenCalledWith(
        `http://localhost:5000/api/fiat/payouts/${USER_ID}?limit=10`,
        expect.objectContaining({ method: "GET" })
      );
    });
  });

  describe("node transparency reads (NodeTransparencyController)", () => {
    it("getNodeTransparencySnapshot GETs /api/node-transparency/current with no auth header", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse({
        governance: { allowedChains: ["Algorand"], allowedAssetTypes: null, version: 1, updatedAt: null },
        feeSchedule: {
          mint: { flatBaseUnits: "0", bps: 0 },
          transfer: { flatBaseUnits: "0", bps: 0 },
          swap: { flatBaseUnits: "0", bps: 0 },
          actionComplete: { flatBaseUnits: "0", bps: 0 },
          federationPublish: { flatBaseUnits: "0", bps: 0 },
          version: 1,
          updatedAt: null,
        },
        treasuryDestinations: [],
        lastUpdatedAt: null,
        contentSha256: "abc123",
        cryptographicHistoryProofAvailable: true,
      }));

      const result = await client.getNodeTransparencySnapshot();
      expect(isOk(result)).toBe(true);
      expect(mockFetch).toHaveBeenCalledWith(
        "http://localhost:5000/api/node-transparency/current",
        expect.objectContaining({ method: "GET" })
      );
      const headers = mockFetch.mock.calls[0][1].headers as Record<string, string>;
      expect(headers["Authorization"]).toBeUndefined();
    });

    it("listNodeGovernanceAudit appends limit and cursor as query params", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse({
        items: [],
        nextCursor: null,
        contentSha256: "abc123",
        cryptographicHistoryProofAvailable: true,
      }));
      await client.listNodeGovernanceAudit(25, "cursor-1");
      expect(mockFetch).toHaveBeenCalledWith(
        "http://localhost:5000/api/node-transparency/audit/governance?limit=25&cursor=cursor-1",
        expect.objectContaining({ method: "GET" })
      );
    });

    it("listNodeFeeAudit and listNodeTreasuryAudit hit their own audit paths", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse({
        items: [], nextCursor: null, contentSha256: "x", cryptographicHistoryProofAvailable: true,
      }));
      await client.listNodeFeeAudit();
      expect(mockFetch).toHaveBeenCalledWith(
        "http://localhost:5000/api/node-transparency/audit/fees",
        expect.objectContaining({ method: "GET" })
      );

      mockFetch.mockReturnValueOnce(azoaResponse({
        items: [], nextCursor: null, contentSha256: "x", cryptographicHistoryProofAvailable: true,
      }));
      await client.listNodeTreasuryAudit();
      expect(mockFetch).toHaveBeenCalledWith(
        "http://localhost:5000/api/node-transparency/audit/treasury",
        expect.objectContaining({ method: "GET" })
      );
    });

    it("getNodeTransparencyHistoryCheckpoint GETs /api/node-transparency/audit/checkpoint", async () => {
      mockFetch.mockReturnValueOnce(azoaResponse({
        checkpoint: {
          schemaVersion: 1,
          descriptor: {
            nodeId: "node-1",
            currentKey: { algorithm: "Ed25519", keyId: "k1", subjectPublicKeyInfoBase64: "AAA=" },
            previousKey: null,
          },
          issuedAt: "2026-09-08T00:00:00Z",
          auditEventCount: 3,
          headSha256: "def456",
          signatureBase64: "BBB=",
        },
        entries: [],
      }));

      const result = await client.getNodeTransparencyHistoryCheckpoint();
      expect(isOk(result)).toBe(true);
      if (isOk(result)) {
        expect(result.value.checkpoint.descriptor.nodeId).toBe("node-1");
      }
      expect(mockFetch).toHaveBeenCalledWith(
        "http://localhost:5000/api/node-transparency/audit/checkpoint",
        expect.objectContaining({ method: "GET" })
      );
    });

    it("propagates a 503 Unavailable refusal as an error Result, never a thrown exception", async () => {
      mockFetch.mockReturnValueOnce(Promise.resolve({
        ok: false,
        status: 503,
        json: () => Promise.resolve({ isError: true, message: "Not available.", result: null }),
      }));

      const result = await client.getNodeTransparencySnapshot();
      expect(isErr(result)).toBe(true);
    });
  });
});
