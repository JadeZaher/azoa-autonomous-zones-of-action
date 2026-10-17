import { beforeEach, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import { isKybRequired } from "../../src/core/errors.js";

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
