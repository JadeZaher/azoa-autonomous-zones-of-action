import { beforeEach, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import type { DexQuoteRequest, SwapAdmissionRequest } from "../../src/api/dex.js";

const operationId = "ab".repeat(32);
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
  await api.exposeDexSwap(operationId, { ...command, acceptPotentialExternalSubmission: true });
  expect(fetchMock).toHaveBeenCalledTimes(1);
  expect(fetchMock.mock.calls[0][0]).toBe(`https://azoa.example/api/dex/operations/${operationId}/external-signing`);
  await api.acceptDexSwapSignature(operationId, { ...command, signedPayloadJson: "original-wire" });
  expect(fetchMock).toHaveBeenCalledTimes(2);
  expect(fetchMock.mock.calls[1][0]).toContain("/external-signature");
  await api.submitDexSwap(operationId, command);
  expect(fetchMock.mock.calls[2][0]).toContain("/submit");
  expect(JSON.parse(fetchMock.mock.calls[2][1].body)).toEqual(command);
  await api.withdrawDexSwapSubmission(operationId, command);
  expect(fetchMock.mock.calls[3][0]).toContain("/withdraw-submission");
});

it("preserves cleanup consent identity on the exact operation routes", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  const command = { expectedVersion: 9, requestKey: "cleanup-original", assetId: "18446744073709551615",
    maximumNetworkFee: "10000", acceptCurrentZeroHoldingReplacement: true };
  await api.beginDexSwapCleanup(operationId, command);
  await api.prepareDexSwapCleanup(operationId, { expectedVersion: 10, stage: "CloseHolding" });
  await api.refreshDexSwap(operationId);
  await api.getDexSwap(operationId);
  expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual(command);
  expect(fetchMock.mock.calls.map(call => call[0])).toEqual([
    `https://azoa.example/api/dex/operations/${operationId}/cleanup`,
    `https://azoa.example/api/dex/operations/${operationId}/cleanup/prepare`,
    `https://azoa.example/api/dex/operations/${operationId}/refresh`,
    `https://azoa.example/api/dex/operations/${operationId}`,
  ]);
});

it.each([
  ["operation path", (api: AzoaApiClient) => api.getDexSwap("../" + "ab".repeat(31))],
  ["uppercase operation", (api: AzoaApiClient) => api.refreshDexSwap("AB".repeat(32))],
  ["short operation", (api: AzoaApiClient) => api.cancelDexSwap("ab".repeat(16), { expectedVersion: 1 })],
  ["quote path", (api: AzoaApiClient) => api.getDexQuote("quote/../path")],
  ["operation-length quote", (api: AzoaApiClient) => api.getDexQuote(operationId)],
])("rejects a malformed %s id before any request leaves the client", async (_name, call) => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  await expect(call(api)).rejects.toMatchObject({ code: "INVALID_INPUT" });
  expect(fetchMock).not.toHaveBeenCalled();
});

it("discovers capabilities per concrete network and surfaces an unready module instead of a fixed chain list", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  const views = [
    { network: { chainType: "Ethereum", network: "Devnet", protocolIdentity: "eip155:31337" },
      capability: { provider: "UniswapV3", protocolVersion: "classic-exact-input/1", exactInput: true, exactOutput: false,
        nativeInput: true, nativeOutput: true, selfRecipientOnly: true, restrictions: ["one pool"] }, ready: true },
    { network: { chainType: "Solana", network: "Devnet", protocolIdentity: "" },
      capability: { provider: "Jupiter", protocolVersion: "6-whirlpool/1", exactInput: true, exactOutput: false,
        nativeInput: true, nativeOutput: true, selfRecipientOnly: true, restrictions: [] }, ready: false, detail: "genesis unavailable" },
  ];
  fetchMock.mockResolvedValueOnce({ ok: true, status: 200, json: async () => ({ isError: false, result: views }) });
  const result = await api.getDexCapabilities();
  expect(fetchMock.mock.calls[0][0]).toBe("https://azoa.example/api/dex/capabilities");
  expect(fetchMock.mock.calls[0][1].method).toBe("GET");
  expect(result.ok).toBe(true);
  if (result.ok) expect(result.value.filter(view => view.ready).map(view => view.capability.provider)).toEqual(["UniswapV3"]);
});

it("sends allowance revocation as its own consent flag, never as the zero-holding close", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  const command = { expectedVersion: 4, requestKey: "revoke-original", assetId: "0x0000000000000000000000000000000000000006",
    maximumNetworkFee: "5000000000000000", acceptCurrentZeroHoldingReplacement: false, acceptCurrentAllowanceRevocation: true };
  await api.beginDexSwapCleanup(operationId, command);
  await api.prepareDexSwapCleanup(operationId, { expectedVersion: 5, stage: "RevokeAllowance" });
  const body = JSON.parse(fetchMock.mock.calls[0][1].body);
  expect(body.acceptCurrentAllowanceRevocation).toBe(true);
  expect(body.acceptCurrentZeroHoldingReplacement).toBe(false);
  expect(JSON.parse(fetchMock.mock.calls[1][1].body).stage).toBe("RevokeAllowance");
});

it("updates the governed swap fee with an explicit input-unit denomination and exact expected version", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  await api.updateNodeFeeSchedule({ expectedVersion: 3, swap: { flatBaseUnits: "10", bps: 25 }, swapFlatDenomination: "input-base-units-v1" });
  expect(fetchMock.mock.calls[0][0]).toBe("https://azoa.example/api/node-governance/fee-schedule");
  expect(fetchMock.mock.calls[0][1].method).toBe("PUT");
  expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({
    expectedVersion: 3, swap: { flatBaseUnits: "10", bps: 25 }, swapFlatDenomination: "input-base-units-v1",
  });
});
