import { beforeEach, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import { isKybRequired, isNftStageCostsChanged } from "../../src/core/errors.js";

const fetchMock = vi.fn();
vi.stubGlobal("fetch", fetchMock);
const base = "https://azoa.example";
const uuid = "0f0e0d0c-0b0a-4908-8706-050403020100";
const hex32 = "ab".repeat(16);
const hex64 = "cd".repeat(32);
const api = () => new AzoaApiClient({ baseUrl: base, token: "token" });

beforeEach(() => {
  fetchMock.mockReset();
  fetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => ({ isError: false, result: {} }) });
});

it.each([
  ["start KYC", (c: AzoaApiClient) => c.startKycSession(), "POST", "/api/kyc/session"],
  ["refresh KYC", (c: AzoaApiClient) => c.refreshKyc(), "POST", "/api/kyc/refresh"],
  ["KYC status", (c: AzoaApiClient) => c.getKycStatus(), "GET", "/api/kyc/status"],
  ["operator hosted refresh", (c: AzoaApiClient) => c.refreshHostedSession(uuid), "POST", `/api/operator/hosted-sessions/${uuid}/refresh`],
  ["ownership challenge", (c: AzoaApiClient) => c.createWalletOwnershipChallenge(uuid), "POST", `/api/wallet/${uuid}/ownership-challenge`],
  ["signing plan", (c: AzoaApiClient) => c.getOperationSigningPlan(uuid), "GET", `/api/blockchain/operations/${uuid}/signing-plan`],
  ["stuck list (clamped)", (c: AzoaApiClient) => c.listStuckClientSignedOperations(5000), "GET", "/api/operator/blockchain-operations/stuck?limit=200"],
  ["parked deployments", (c: AzoaApiClient) => c.listParkedDeployments(0), "GET", "/api/operator/deployments?limit=1"],
  ["tenant float", (c: AzoaApiClient) => c.getTenantPayoutBalance(), "GET", "/api/fiat/tenant-payout-balance"],
  ["liabilities", (c: AzoaApiClient) => c.listHoldLiabilities(10, 25), "GET", "/api/hold-commands/liabilities?offset=10&limit=25"],
  ["liability", (c: AzoaApiClient) => c.getHoldLiability(hex64), "GET", `/api/hold-commands/liabilities/${hex64}`],
  ["reserve assets", (c: AzoaApiClient) => c.listHoldReserveAssets(), "GET", "/api/hold-commands/reserve-assets"],
  ["retired rows", (c: AzoaApiClient) => c.listRetiredExchangeRows(), "GET", "/api/exchange/operator/retired-rows?limit=50"],
  ["wtt readiness", (c: AzoaApiClient) => c.getWttStageKeyReadiness(), "GET", "/api/operator/wtt/stage-keys/readiness"],
  ["wtt reconcile", (c: AzoaApiClient) => c.runWttOperatorAction(hex64, "reconcile"), "POST", `/api/operator/wtt/operations/${hex64}/reconcile`],
  ["sweep", (c: AzoaApiClient) => c.reconcileParkedRuns(), "POST", "/api/action/runs/reconcile-sweep"],
])("%s hits the published route", async (_name, call, method, path) => {
  await call(api());
  expect(fetchMock.mock.calls[0][0]).toBe(`${base}${path}`);
  expect(fetchMock.mock.calls[0][1].method).toBe(method);
});

it("sends operator resolutions with the exact body the server re-proves", async () => {
  const c = api();
  await c.resolveFiatPayout("payout/1", { resolution: "return-units", reason: "Stripe shows the transfer was reversed" });
  expect(fetchMock.mock.calls[0][0]).toBe(`${base}/api/admin/fiat-payouts/payout%2F1/resolve`);
  expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({ resolution: "return-units", reason: "Stripe shows the transfer was reversed" });
  await c.resolveParkedDeployment(hex32, { expectedVersion: 4, action: "CloseUnresolvable", reason: "checked", acknowledgeNoFurtherEffect: true });
  expect(fetchMock.mock.calls[1][0]).toBe(`${base}/api/operator/deployments/${hex32}/resolve`);
  await c.submitSignedOperation(uuid, { signedTransactionBase64: "AQID" });
  expect(JSON.parse(fetchMock.mock.calls[2][1].body)).toEqual({ signedTransactionBase64: "AQID" });
});

it.each([
  ["hold record", (c: AzoaApiClient) => c.getHoldLiability("../liabilities")],
  ["deployment", (c: AzoaApiClient) => c.resolveParkedDeployment(hex64, { expectedVersion: 1, action: "Reobserve", acknowledgeNoFurtherEffect: false })],
  ["swap", (c: AzoaApiClient) => c.settleDexOperatorReview(hex32, { expectedVersion: 1, reason: "reviewed" })],
  ["wtt", (c: AzoaApiClient) => c.inspectWttOperation("A".repeat(64))],
  ["operation", (c: AzoaApiClient) => c.getOperationSigningPlan("../../admin")],
  ["empty signature", (c: AzoaApiClient) => c.proveWalletOwnership(uuid, " ")],
  ["empty reason", (c: AzoaApiClient) => c.resolveFiatReversal("rev", { reason: "" })],
])("refuses a malformed %s before any request leaves the client", async (_name, call) => {
  await expect(call(api())).rejects.toThrow(/Invalid|required|non-empty|expected/i);
  expect(fetchMock).not.toHaveBeenCalled();
});

it("keeps the server refusal code so a page can explain KYB_REQUIRED", async () => {
  fetchMock.mockResolvedValueOnce({ ok: false, status: 403, json: async () => ({ isError: true, code: "KYB_REQUIRED", message: "Business verification is required." }) });
  const result = await api().getTenantPayoutBalance();
  expect(result.ok).toBe(false);
  if (!result.ok) {
    expect(result.error.serverCode).toBe("KYB_REQUIRED");
    expect(isKybRequired(result.error)).toBe(true);
  }
  fetchMock.mockResolvedValueOnce({ ok: false, status: 403, json: async () => ({ isError: true, message: "Forbidden." }) });
  const other = await api().getTenantPayoutBalance();
  expect(!other.ok && isKybRequired(other.error)).toBe(false);
});

// Real server bodies (not the `{isError:false,result:{}}` default): bare-vs-envelope drift must go red here.
it.each([
  ["operator ethereum list (bare IReadOnlyList)", (c: AzoaApiClient) => c.listEthereumPreparations("Devnet", true),
    [{ id: "prep-1", network: "Devnet", sender: "0x" + "a".repeat(40), state: "Prepared", nonce: "7", transactionId: null, preparationKey: "k", updatedAt: "2026-10-17T00:00:00Z" }]],
  ["user ethereum list (bare IReadOnlyList)", (c: AzoaApiClient) => c.listEthereumPreparations("Devnet"), []],
  ["wallet identity migration (bare View)", (c: AzoaApiClient) => c.getWalletIdentityMigration(),
    { status: "Blocked", version: 3, conflicts: [{ userId: "u1", expectedIdentity: "algorand:ADDR", problem: "Claimed by two accounts" }] }],
])("%s resolves to the exact server body", async (_name, call, body) => {
  fetchMock.mockResolvedValueOnce({ ok: true, status: 200, json: async () => body });
  const result = await call(api());
  expect(result.ok && result.value).toEqual(body);
});

it("clears a wallet identity conflict as the server's AZOAResult<bool>", async () => {
  fetchMock.mockResolvedValueOnce({ ok: true, status: 200, json: async () => ({ isError: false, message: "Success", result: true }) });
  const result = await api().clearWalletIdentityConflict({ version: 3, userId: "u1", expectedIdentity: "algorand:ADDR", reason: "Stale binding confirmed" });
  expect(result.ok && result.value).toBe(true);
  expect(fetchMock.mock.calls[0][0]).toBe(`${base}/api/operator/wallet-identities/clear-conflicting-binding`);
});

it("quotes and accepts recipient-stage NFT costs on the F-M11 routes with the exact reviewed stage", async () => {
  const c = api();
  const stage = { requestId: uuid, expectedVersion: 4, state: "Preparing" as const, stage: "Transfer", generation: 0 };
  await c.quoteNftStageCosts(hex32, stage);
  expect(fetchMock.mock.calls[0][0]).toBe(`${base}/api/nft-stage-costs/${hex32}/quote`);
  expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual(stage);
  const costs = { quoteId: hex32, quoteHash: hex64, limits: { networkFee: "5000", retainedRent: "2039280", protocolLevy: "0" } };
  await c.acceptNftStageCosts(hex32, { stage, costs });
  expect(fetchMock.mock.calls[1][0]).toBe(`${base}/api/nft-stage-costs/${hex32}/accept`);
  expect(JSON.parse(fetchMock.mock.calls[1][1].body)).toEqual({ stage, costs });
  await expect(c.quoteNftStageCosts("../x", stage)).rejects.toThrow();
});

it("recognises the changed-costs refusal so the payer can re-quote", async () => {
  fetchMock.mockResolvedValueOnce({ ok: false, status: 400, json: async () => ({ isError: true, code: "NFT_STAGE_COSTS_CHANGED", message: "NFT stage costs changed since acceptance; review and accept a new quote." }) });
  const result = await api().refreshNftLifecycle(hex32);
  expect(!result.ok && isNftStageCostsChanged(result.error)).toBe(true);
});
