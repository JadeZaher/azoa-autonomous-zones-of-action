import { beforeEach, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import type { DeploymentRecoveryStartRequest, DeploymentStartRequest } from "../../src/api/deployments.js";

const fetchMock = vi.fn();
vi.stubGlobal("fetch", fetchMock);
const request: DeploymentStartRequest = { artifactId: "compiled", artifactHash: "a".repeat(64), target: { kind: "NativeContract", chainType: "Solana", network: "Devnet" } };
beforeEach(() => { fetchMock.mockReset(); fetchMock.mockResolvedValue({ ok: true, status: 202, json: async () => ({ isError: false, result: { operationId: "durable", state: "Submitted" } }) }); });

it("replays the exact linked recovery consent after response loss", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  const recovery: DeploymentRecoveryStartRequest = {
    requestKey: "one-recovery", acknowledgeCosts: true,
    quote: { parentVersion: 7, bindingHash: "binding", intent: { version: 1, operationKey: "child", kind: "Recovery",
      chainType: "Solana", network: "Devnet", protocolIdentity: "solana:genesis", artifactHash: "hash", input: {}, authorities: [],
      budget: { maximumNetworkFee: "9007199254740993", maximumLockedRent: "0", maximumValue: "0", quoteId: "quote" } } },
  };
  fetchMock.mockRejectedValueOnce(new Error("lost reply"));
  expect((await api.startDeploymentRecovery("parent/id", recovery)).ok).toBe(false);
  await api.startDeploymentRecovery("parent/id", recovery);
  expect(fetchMock.mock.calls[1][0]).toBe("https://azoa.example/api/deployments/parent%2Fid/recovery/start");
  expect(fetchMock.mock.calls[0][1].body).toBe(fetchMock.mock.calls[1][1].body);
  expect(JSON.parse(fetchMock.mock.calls[1][1].body)).toEqual(recovery);
  fetchMock.mockRejectedValueOnce(new Error("lost withdrawal reply"));
  expect((await api.invalidateDeploymentRecovery("parent/id", recovery)).ok).toBe(false);
  await api.invalidateDeploymentRecovery("parent/id", recovery);
  expect(fetchMock.mock.calls[3][0]).toBe("https://azoa.example/api/deployments/parent%2Fid/recovery/invalidate");
  expect(fetchMock.mock.calls[2][1].body).toBe(fetchMock.mock.calls[3][1].body);
  expect(JSON.parse(fetchMock.mock.calls[3][1].body)).toEqual(recovery);
});

it("keeps original-budget recovery consent separate from a new recovery quote", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example" });
  await api.recoverDeploymentWithinBudget("parent", { expectedVersion: 3, mode: "AbortCleanup", acknowledgeRecovery: true });
  await api.quoteDeploymentRecovery("parent", { expectedVersion: 4, requestKey: "new-key", mode: "AbortCleanup", payerAddress: "payer", authorities: [],
    budget: { maximumNetworkFee: "5000", maximumLockedRent: "0", maximumValue: "0", quoteId: "" } });
  expect(fetchMock.mock.calls[0][0]).toContain("/recovery/original-budget");
  expect(fetchMock.mock.calls[1][0]).toContain("/recovery/quote");
  expect(JSON.parse(fetchMock.mock.calls[1][1].body)).not.toHaveProperty("acknowledgeCosts");
});

it("inspects one server-owned ancestry page without accepting a caller cursor or signing consent", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example" });
  await api.inspectDeploymentRecoveryAncestry("parent/id");
  expect(fetchMock.mock.calls[0][0]).toBe("https://azoa.example/api/deployments/parent%2Fid/recovery/ancestry/inspect");
  expect(fetchMock.mock.calls[0][1].method).toBe("POST");
  expect(fetchMock.mock.calls[0][1].body).toBeUndefined();
});

it("preserves exact admission identity after response loss and keeps pending truthful", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  fetchMock.mockRejectedValueOnce(new Error("response lost"));
  expect((await api.startDeployment(request, "stable-key")).ok).toBe(false);
  const result = await api.startDeployment(request, "stable-key");
  expect(result.ok && result.value.state).toBe("Submitted");
  expect(fetchMock.mock.calls[0][1].body).toBe(fetchMock.mock.calls[1][1].body);
  expect(fetchMock.mock.calls[1][1].headers["Idempotency-Key"]).toBe("stable-key");
});

it("keeps lookup read-only and uses an explicit request-key cancellation fence", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example" });
  await api.getDeploymentByRequest("key/with spaces");
  await api.cancelDeploymentRequest("key/with spaces", request);
  expect(fetchMock.mock.calls[0][0]).toBe("https://azoa.example/api/deployments/request?requestKey=key%2Fwith+spaces");
  expect(fetchMock.mock.calls[0][1].method).toBe("GET");
  expect(JSON.parse(fetchMock.mock.calls[1][1].body)).toEqual({ requestKey: "key/with spaces", request });
});

it("passes immutable target and explicit key through legacy bundle and series adapters", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example" });
  const id = "11111111-1111-1111-1111-111111111111";
  await api.deployActionGraphBundle(id, request, "bundle-key");
  await api.deployDappSeries(id, request, "series-key");
  expect(fetchMock.mock.calls[0][0]).toContain(`/actiongraphbundle/${id}/deploy`);
  expect(fetchMock.mock.calls[1][0]).toContain(`/dapp-series/${id}/deploy`);
  expect(fetchMock.mock.calls[0][1].headers["Idempotency-Key"]).toBe("bundle-key");
  expect(fetchMock.mock.calls[1][1].headers["Idempotency-Key"]).toBe("series-key");
  expect(JSON.parse(fetchMock.mock.calls[1][1].body)).toEqual(request);
});

it("invocation binds the generated payload hash and caller consent without pretending the run finished", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example" });
  const body = { expectedPayloadHash: "b".repeat(64), input: { amount: "1000000000000000001" }, acknowledgeEconomicEffects: true };
  await api.invokeDeployment("operation/id", body, "invocation-key");
  expect(fetchMock.mock.calls[0][0]).toBe("https://azoa.example/api/deployments/operation%2Fid/invoke");
  expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual(body);
  expect(fetchMock.mock.calls[0][1].headers["Idempotency-Key"]).toBe("invocation-key");
});
