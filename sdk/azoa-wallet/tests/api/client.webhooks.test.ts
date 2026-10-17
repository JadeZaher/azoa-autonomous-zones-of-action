import { beforeEach, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import type { WebhookRegistrationCommand } from "../../src/api/webhooks.js";

const fetchMock = vi.fn();
vi.stubGlobal("fetch", fetchMock);
beforeEach(() => {
  fetchMock.mockReset();
  fetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => ({ isError: false, result: { replayed: true, newSecret: null } }) });
});

it("preserves an event retry command after response loss and scopes operator reads", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  const command = { requestId: "retry", expectedVersion: 7, reason: "Receiver authentication repaired." };
  fetchMock.mockRejectedValueOnce(new Error("response lost"));
  expect((await api.retryWebhookEvent("event/path", command)).ok).toBe(false);
  await api.retryWebhookEvent("event/path", command);
  expect(fetchMock.mock.calls[0][1].body).toBe(fetchMock.mock.calls[1][1].body);
  expect(fetchMock.mock.calls[1][0]).toBe("https://azoa.example/api/tenant/webhook-events/event%2Fpath/retry");
  await api.listOperatorWebhookEvents("tenant/path", 25, 25);
  expect(fetchMock.mock.calls[2][0]).toBe("https://azoa.example/api/operator/tenants/tenant%2Fpath/webhook-events?offset=25&limit=25");
  await api.getOperatorWebhookEvent("tenant/path", "event/path");
  expect(fetchMock.mock.calls[3][0]).toBe("https://azoa.example/api/operator/tenants/tenant%2Fpath/webhook-events/event%2Fpath");
});

it("retries an unchanged registration command after response loss", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  const command: WebhookRegistrationCommand = { requestId: "request", registrationId: "registration", expectedVersion: 2,
    url: "https://receiver.example/hook", isActive: true, rotateSecret: true, reason: "Rotate the receiver signing secret." };
  fetchMock.mockRejectedValueOnce(new Error("lost response"));
  expect((await api.updateWebhookRegistration(command)).ok).toBe(false);
  const result = await api.updateWebhookRegistration(command);
  expect(result.ok).toBe(true);
  expect(fetchMock.mock.calls[0][1].body).toBe(fetchMock.mock.calls[1][1].body);
  expect(fetchMock.mock.calls[1][0]).toBe("https://azoa.example/api/tenant/webhook-registration");
  expect(fetchMock.mock.calls[1][1].method).toBe("PUT");
  expect(JSON.parse(fetchMock.mock.calls[1][1].body)).toEqual(command);
});

it("encodes operator tenant identity as one path segment", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  await api.getOperatorWebhookRegistration("tenant/path");
  expect(fetchMock.mock.calls[0][0]).toBe("https://azoa.example/api/operator/tenants/tenant%2Fpath/webhook-registration");
  expect(fetchMock.mock.calls[0][1].method).toBe("GET");
});

it("keeps consent retry routing and commands distinct from action deliveries", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  const command = { requestId: "retry", expectedVersion: 7, reason: "Receiver authentication repaired." };
  fetchMock.mockRejectedValueOnce(new Error("response lost"));
  expect((await api.retryConsentWebhookEvent("event/path", command)).ok).toBe(false);
  await api.retryConsentWebhookEvent("event/path", command);
  expect(fetchMock.mock.calls[0][1].body).toBe(fetchMock.mock.calls[1][1].body);
  expect(fetchMock.mock.calls[1][0]).toBe("https://azoa.example/api/tenant/consent-webhook-events/event%2Fpath/retry");
  await api.listOperatorConsentWebhookEvents("tenant/path", 25, 25);
  expect(fetchMock.mock.calls[2][0]).toBe("https://azoa.example/api/operator/tenants/tenant%2Fpath/consent-webhook-events?offset=25&limit=25");
});

it("retains consent command identity across response loss and encodes offboarding references", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  const command = { requestId: "original", tenantId: "tenant", scopes: ["kyc:read"] };
  fetchMock.mockRejectedValueOnce(new Error("response lost"));
  await api.grantConsent(command);
  await api.grantConsent(command);
  expect(fetchMock.mock.calls[0][1].body).toBe(fetchMock.mock.calls[1][1].body);
  await api.offboardProvisionedUser("person/one & two");
  expect(fetchMock.mock.calls[2][0]).toBe("https://azoa.example/api/tenant/consent/provisioned?externalUserId=person%2Fone%20%26%20two");
  await api.loginByUserId("retained-id", "new-password");
  expect(JSON.parse(fetchMock.mock.calls[3][1].body)).toEqual({ userId: "retained-id", password: "new-password" });
});

it("withdraws the exact saved consent command after response loss", async () => {
  const api = new AzoaApiClient({ baseUrl: "https://azoa.example", token: "token" });
  const command = { requestId: "original", tenantId: "tenant", scopes: ["unknown:scope"] };
  fetchMock.mockRejectedValueOnce(new Error("response lost"));
  await api.withdrawConsentCommand(command);
  await api.withdrawConsentCommand(command);
  expect(fetchMock.mock.calls[0][1].body).toBe(fetchMock.mock.calls[1][1].body);
  expect(fetchMock.mock.calls[1][0]).toBe("https://azoa.example/api/user/consent/withdraw");
  expect(JSON.parse(fetchMock.mock.calls[1][1].body)).toEqual(command);
});
