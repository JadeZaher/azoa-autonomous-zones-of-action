import { beforeEach, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";

const fetchMock = vi.fn();
vi.stubGlobal("fetch", fetchMock);
beforeEach(() => fetchMock.mockReset());

it("keeps workflow disclosure separate and reconciles the exact lost acceptance", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  const executionId = "29f4bfce-e4d3-4af9-8ab9-976cbd18d511";
  const request = { executionId, requestId: "87841f2a-46bb-444f-b898-37157a2084fb", expectedExecutionVersion: 4,
    authorization: { quoteId: "original", quoteHash: "hash", limits: { networkFee: "1", retainedRent: "2", protocolLevy: "3" } } };
  fetchMock.mockResolvedValueOnce({ ok: true, status: 200, json: async () => ({ isError: false, result: {} }) });
  await api.quoteNftWorkflowCosts(executionId);
  expect(fetchMock).toHaveBeenCalledTimes(1);
  fetchMock.mockRejectedValueOnce(new Error("response lost"));
  await api.acceptNftWorkflowCosts(request);
  fetchMock.mockResolvedValueOnce({ ok: true, status: 200, json: async () => ({ isError: false, result: { state: "Unresolved" } }) });
  await api.reconcileNftWorkflowCosts(request);
  expect(fetchMock.mock.calls.map(call => call[0])).toEqual([
    `https://azoa.example/api/nft-workflow-costs/${executionId}/quote`,
    "https://azoa.example/api/nft-workflow-costs/accept",
    "https://azoa.example/api/nft-workflow-costs/reconcile",
  ]);
  expect(fetchMock.mock.calls[1][1].body).toBe(fetchMock.mock.calls[2][1].body);
});

it("quotes without minting and preserves accepted full-width category limits on retry", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  const limits = { networkFee: "18446744073709551615", retainedRent: "9646560", protocolLevy: "10000000" };
  const authorization = { quoteId: "receipt", quoteHash: "receipt-hash", limits };
  fetchMock.mockResolvedValueOnce({ ok: true, status: 200, json: async () => ({ isError: false, result: { quote: { limits }, authorization } }) });
  const draft = { name: "NFT", chainType: "Solana", metadataURI: "ipfs://draft" };
  const quote = await api.quoteUserNftMintCosts(draft, "original-key");
  expect(quote.ok).toBe(true);
  expect(fetchMock).toHaveBeenCalledTimes(1);
  expect(fetchMock.mock.calls[0][0]).toBe("https://azoa.example/api/usernft/mint-cost-quote");
  expect(fetchMock.mock.calls[0][1].headers["Idempotency-Key"]).toBe("original-key");
  const accepted = { ...draft, protocolCostAuthorization: authorization };
  fetchMock.mockRejectedValueOnce(new Error("response lost"));
  await api.mintUserNFT(accepted, "original-key");
  fetchMock.mockResolvedValueOnce({ ok: true, status: 202, json: async () => ({ isError: false, result: { state: "Submitted" } }) });
  await api.mintUserNFT(accepted, "original-key");
  expect(fetchMock.mock.calls[1][1].body).toBe(fetchMock.mock.calls[2][1].body);
  expect(JSON.parse(fetchMock.mock.calls[2][1].body).protocolCostAuthorization).toEqual(authorization);
});

it("uses matching alias and credential quote routes without issuing either operation", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  fetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => ({ isError: false, result: {} }) });
  await api.quoteNftMintCosts({ walletId: "wallet", name: "NFT", description: "", chainId: "Solana", metadataUri: "ipfs://draft" }, "alias-key");
  await api.quoteCredentialIssueCosts("recipient/path", { name: "Credential", chainType: "Solana", metadataURI: "ipfs://credential" }, "credential-key");
  expect(fetchMock.mock.calls.map(call => call[0])).toEqual([
    "https://azoa.example/api/nft/mint-cost-quote",
    "https://azoa.example/api/Credential/issue/recipient%2Fpath/cost-quote",
  ]);
});

it("retires and reconciles the identical saved workflow acceptance without issuing a mint", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  const request = { executionId: "29f4bfce-e4d3-4af9-8ab9-976cbd18d511", requestId: "87841f2a-46bb-444f-b898-37157a2084fb",
    expectedExecutionVersion: 4, authorization: { quoteId: "expired", quoteHash: "original-hash",
      limits: { networkFee: "1", retainedRent: "2", protocolLevy: "3" } } };
  fetchMock.mockRejectedValueOnce(new Error("retirement response lost"));
  await api.retireNftWorkflowCosts(request);
  fetchMock.mockResolvedValueOnce({ ok: true, status: 200, json: async () => ({ isError: false, result: { state: "Obsolete" } }) });
  await api.reconcileNftWorkflowCosts(request);
  expect(fetchMock.mock.calls.map(call => call[0])).toEqual([
    "https://azoa.example/api/nft-workflow-costs/retire", "https://azoa.example/api/nft-workflow-costs/reconcile",
  ]);
  expect(fetchMock.mock.calls[0][1].body).toBe(fetchMock.mock.calls[1][1].body);
});
