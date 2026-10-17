import { beforeEach, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";

const fetchMock = vi.fn();
vi.stubGlobal("fetch", fetchMock);
beforeEach(() => {
  fetchMock.mockReset();
  fetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => ({ isError: false, result: {} }) });
});

it("preserves the same recovery identity and expected version when retrying a lost response", async () => {
  const client = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "operator-token" });
  const request = { requestId: "stable-request", expectedVersion: 7, intentHash: "a".repeat(64), reason: "Original provider evidence reviewed." };
  fetchMock.mockRejectedValueOnce(new Error("response lost"));
  const lostResponse = await client.retryActionRecovery("execution/id", request);
  expect(lostResponse.ok).toBe(false);
  await client.retryActionRecovery("execution/id", request);
  expect(fetchMock.mock.calls[0][0]).toBe("https://azoa.example/api/operator/action-recovery/execution%2Fid/retry");
  expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual(request);
  expect(fetchMock.mock.calls[1][1].body).toBe(fetchMock.mock.calls[0][1].body);
});

it("bounds the parked list and reads history without triggering recovery", async () => {
  const client = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "operator-token" });
  await client.listActionRecovery(999);
  await client.getActionRecoveryHistory("execution/id");
  expect(fetchMock.mock.calls[0][0]).toBe("https://azoa.example/api/operator/action-recovery?limit=100");
  expect(fetchMock.mock.calls[1][0]).toBe("https://azoa.example/api/operator/action-recovery/execution%2Fid/history");
  expect(fetchMock.mock.calls.every(call => call[1].method === "GET")).toBe(true);
});
