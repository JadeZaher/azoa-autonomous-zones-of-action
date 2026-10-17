import { beforeEach, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import type { WttParticipantSignatureCommand, WttQuoteRequest } from "../../src/api/wtt.js";

const fetchMock = vi.fn();
vi.stubGlobal("fetch", fetchMock);
beforeEach(() => {
  fetchMock.mockReset();
  fetchMock.mockResolvedValue({ ok: true, status: 202, json: async () => ({ isError: false, result: { operationId: "op", publicState: "Admitted" } }) });
});

const operationId = "a".repeat(64);
const quote: WttQuoteRequest = {
  requestKey: "transfer-1", source: { chainType: "Algorand", network: "Devnet", walletId: "w1" }, tokenAddress: "31566704",
  nativeAsset: false, amount: "18446744073709551617", target: { chainType: "Solana", network: "Devnet", walletId: "w2" },
  recipientAddress: "recipient", unwrapNative: false,
  sourceBudget: { maximumNetworkFee: "9007199254740993", maximumLockedDeposit: "0", maximumValue: "0" },
  targetBudget: { maximumNetworkFee: "5000", maximumLockedDeposit: "2039280", maximumValue: "0" },
};

it("keeps uint64-scale amounts lossless and replays the exact start under the original request key", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  await api.quoteWtt(quote);
  expect(fetchMock.mock.calls[0][0]).toBe("https://azoa.example/api/bridge/wtt/quotes");
  expect(fetchMock.mock.calls[0][1].body).toContain("\"18446744073709551617\"");

  fetchMock.mockRejectedValueOnce(new Error("start response lost"));
  expect((await api.startWtt({ offerId: "offer", offerHash: "h".repeat(64), acceptEconomicEffects: true }, "transfer-1")).ok).toBe(false);
  await api.startWtt({ offerId: "offer", offerHash: "h".repeat(64), acceptEconomicEffects: true }, "transfer-1");
  expect(fetchMock.mock.calls[1][1].body).toBe(fetchMock.mock.calls[2][1].body);
  expect(fetchMock.mock.calls[2][1].headers["Idempotency-Key"]).toBe("transfer-1");

  await api.findWttByRequestKey("transfer-1");
  expect(fetchMock.mock.calls[3][0]).toBe("https://azoa.example/api/bridge/wtt/operations/by-request-key");
  expect(fetchMock.mock.calls[3][1].method).toBe("GET");
  expect(fetchMock.mock.calls[3][1].headers["Idempotency-Key"]).toBe("transfer-1");
});

it("exposes then signs only the disclosed participant message and refreshes without signing", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  const stage = { expectedVersion: 7, stageId: "s".repeat(64), expectedStageVersion: 2 };
  await api.exposeWttParticipant(operationId, stage);
  const signature: WttParticipantSignatureCommand = { ...stage, signedTransactionBase64: "c2lnbmVk", authorizeSubmission: true };
  await api.acceptWttParticipantSignature(operationId, signature);
  await api.refreshWtt(operationId);
  expect(fetchMock.mock.calls.map((call) => call[0])).toEqual([
    `https://azoa.example/api/bridge/wtt/operations/${operationId}/participant`,
    `https://azoa.example/api/bridge/wtt/operations/${operationId}/participant-signature`,
    `https://azoa.example/api/bridge/wtt/operations/${operationId}/refresh`,
  ]);
  expect(JSON.parse(fetchMock.mock.calls[1][1].body)).toEqual(signature);
  expect(fetchMock.mock.calls[2][1].body).toBeUndefined();
});

it("routes cancellation through the request-key fence and cleanup through its own consent", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  await api.cancelWtt({ offerId: "offer", offerHash: "h".repeat(64) }, "transfer-1");
  await api.startWttCleanup(operationId, { expectedVersion: 9, stageId: "r".repeat(64), requestKey: "cleanup-1", maximumNetworkFee: "5000", acceptReturn: true });
  await api.listWtt("638000000000000000", 10);
  expect(fetchMock.mock.calls[0][0]).toBe("https://azoa.example/api/bridge/wtt/operations/cancel");
  expect(fetchMock.mock.calls[0][1].headers["Idempotency-Key"]).toBe("transfer-1");
  expect(JSON.parse(fetchMock.mock.calls[1][1].body)).toMatchObject({ acceptReturn: true, maximumNetworkFee: "5000" });
  expect(fetchMock.mock.calls[2][0]).toBe("https://azoa.example/api/bridge/wtt/operations?cursor=638000000000000000&limit=10");
});

it("refuses malformed operation or stage identities before any request leaves the client", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  await expect(api.getWtt("../dex/operations/x")).rejects.toThrow(/operationId/);
  await expect(api.submitWtt(operationId, { expectedVersion: 1, stageId: "S".repeat(64), expectedStageVersion: 0 })).rejects.toThrow(/stageId/);
  await expect(api.quoteWttCleanup(operationId, "short")).rejects.toThrow(/stageId/);
  expect(fetchMock).not.toHaveBeenCalled();
});
