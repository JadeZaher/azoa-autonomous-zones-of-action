import { beforeEach, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import type { DexQuoteRequest, SwapAdmissionRequest } from "../../src/api/dex.js";

const fetchMock = vi.fn();
vi.stubGlobal("fetch", fetchMock);
beforeEach(() => {
  fetchMock.mockReset();
  fetchMock.mockResolvedValue({ ok: true, status: 202, json: async () => ({ isError: false, result: { state: "Pending" } }) });
});

it("replays the original admission body after a lost response without changing quote or consent", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  const command: SwapAdmissionRequest = { quoteId: "quote", quoteHash: "hash", requestKey: "saved-key", acceptEconomicEffects: true };
  fetchMock.mockRejectedValueOnce(new Error("response lost"));
  expect((await api.startDexSwap(command)).ok).toBe(false);
  expect((await api.startDexSwap(command)).ok).toBe(true);
  expect(fetchMock.mock.calls[0][1].body).toBe(fetchMock.mock.calls[1][1].body);
  expect(JSON.parse(fetchMock.mock.calls[1][1].body)).toEqual(command);
  expect(fetchMock.mock.calls[1][0]).toBe("https://azoa.example/api/dex/operations");
});

it("keeps all amounts as full-width decimal text and preserves unavailable as a successful quote response", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  fetchMock.mockResolvedValueOnce({ ok: true, status: 200, json: async () => ({ isError: false, result: { unavailable: true, reason: "Pool unavailable" } }) });
  const amount = "115792089237316195423570985008687907853269984665640564039457584007913129639935";
  const request: DexQuoteRequest = { walletId: "wallet", network: { chainType: "Ethereum", network: "Devnet", protocolIdentity: "eip155:31337" },
    inputAsset: { id: "input", symbol: "IN", decimals: 18, native: false }, outputAsset: { id: "output", symbol: "OUT", decimals: 18, native: false },
    grossInput: amount, slippageBps: 50, budget: { maximumNetworkFee: amount, maximumLockedBalance: "0", maximumCleanupFee: "100" } };
  const response = await api.quoteDex(request);
  expect(response.ok).toBe(true);
  if (response.ok) expect(response.value.unavailable).toBe(true);
  expect(JSON.parse(fetchMock.mock.calls[0][1].body).grossInput).toBe(amount);
});

it("keeps exposure, signature upload and server submission as separate exact-version commands", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  const command = { expectedVersion: 7, stageId: "stage", expectedStageVersion: 3 };
  await api.exposeDexSwap("operation/path", { ...command, acceptPotentialExternalSubmission: true });
  expect(fetchMock).toHaveBeenCalledTimes(1);
  expect(fetchMock.mock.calls[0][0]).toBe("https://azoa.example/api/dex/operations/operation%2Fpath/external-signing");
  await api.acceptDexSwapSignature("operation/path", { ...command, signedPayloadJson: "original-wire" });
  expect(fetchMock).toHaveBeenCalledTimes(2);
  expect(fetchMock.mock.calls[1][0]).toContain("/external-signature");
  await api.submitDexSwap("operation/path", command);
  expect(fetchMock.mock.calls[2][0]).toContain("/submit");
  expect(JSON.parse(fetchMock.mock.calls[2][1].body)).toEqual(command);
  await api.withdrawDexSwapSubmission("operation/path", command);
  expect(fetchMock.mock.calls[3][0]).toContain("/withdraw-submission");
});

it("preserves cleanup consent identity and encodes every operation reference", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  const command = { expectedVersion: 9, requestKey: "cleanup-original", assetId: "18446744073709551615",
    maximumNetworkFee: "10000", acceptCurrentZeroHoldingReplacement: true };
  await api.beginDexSwapCleanup("operation/path", command);
  await api.prepareDexSwapCleanup("operation/path", { expectedVersion: 10, stage: "CloseHolding" });
  await api.refreshDexSwap("operation/path");
  await api.getDexSwap("operation/path");
  expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual(command);
  expect(fetchMock.mock.calls.map(call => call[0])).toEqual([
    "https://azoa.example/api/dex/operations/operation%2Fpath/cleanup",
    "https://azoa.example/api/dex/operations/operation%2Fpath/cleanup/prepare",
    "https://azoa.example/api/dex/operations/operation%2Fpath/refresh",
    "https://azoa.example/api/dex/operations/operation%2Fpath",
  ]);
});
