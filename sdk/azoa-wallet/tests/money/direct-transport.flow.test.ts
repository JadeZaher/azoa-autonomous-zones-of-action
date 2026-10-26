import { afterEach, describe, expect, it, vi } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import {
  MONEY_SCOPES,
  claimRequestKey,
  createDirectMoneyTransport,
  createKeyStore,
  findForbiddenViewKeys,
  memoryStorage,
  newRequestKey,
  readSavedRequest,
  sendCheckout,
  startCheckout,
} from "../../src/money/index.js";
import type { AzoaMoneyTransport, CheckoutView, KeyStorage } from "../../src/money/index.js";
import { runMoneyTransportContract } from "../../src/money/testing/index.js";
import { createNodeEmulator } from "./node-emulator.js";
import type { NodeEmulator } from "./node-emulator.js";

/** One page load: a real AzoaApiClient on the person's own session, the direct transport and a key store over the device's storage. */
function pageLoad(node: NodeEmulator, storage: KeyStorage) {
  const client = new AzoaApiClient({ baseUrl: node.baseUrl, token: node.token });
  return { transport: createDirectMoneyTransport(client, node.userId), keyStore: createKeyStore({ subject: node.userId, storage }) };
}

/** A fresh node with the global fetch pointed at it. */
function freshNode(): NodeEmulator {
  const node = createNodeEmulator();
  vi.stubGlobal("fetch", node.fetch);
  return node;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("money over the direct transport (P0 flows)", () => {
  it("a lost checkout answer survives a reload and resumes the one admitted payment", async () => {
    const node = freshNode();
    const device = memoryStorage();

    const firstVisit = pageLoad(node, device);
    node.loseNextAdmissionResponse();
    const lost = await startCheckout(firstVisit.transport, firstVisit.keyStore, 25_00);
    expect(lost).toMatchObject({ kind: "refused", refusal: { code: "unconfirmed" } });
    expect(node.admissions).toHaveLength(1);

    // Reload: a NEW key store instance over the SAME device storage.
    const reload = pageLoad(node, device);
    const saved = readSavedRequest(reload.keyStore, MONEY_SCOPES.checkout);
    expect(saved?.amountMinorUnits).toBe(25_00);
    // The unresolved key blocks a different payment instead of being silently replaced.
    expect(claimRequestKey(reload.keyStore, MONEY_SCOPES.checkout, 10_00).kind).toBe("pending");
    // Another person on the same device sees nothing of it.
    expect(readSavedRequest(createKeyStore({ subject: "someone-else", storage: device }), MONEY_SCOPES.checkout)).toBeNull();

    const resumed = await sendCheckout(reload.transport, reload.keyStore, saved!);
    if (resumed.kind !== "opened") throw new Error(`expected the resumed payment to open, got ${resumed.kind}`);
    expect(node.admissions).toHaveLength(1);
    expect(resumed.view).toMatchObject({ intentId: node.admissions[0]!.intentId, resumed: true });
    expect(resumed.paymentUrl).toBe(node.admissions[0]!.paymentUrl);
    // The key now names its intent, so a later visit resolves it by state; the subject-bearing admission key stayed server-side.
    expect(readSavedRequest(reload.keyStore, MONEY_SCOPES.checkout)?.serverId).toBe(resumed.view.intentId);
    expect(findForbiddenViewKeys(resumed.view)).toEqual([]);
  });

  it("a node 403 consent-revoked body becomes consent_revoked and releases the key", async () => {
    const node = freshNode();
    const visit = pageLoad(node, memoryStorage());
    node.revokeConsent();

    const attempt = await startCheckout(visit.transport, visit.keyStore, 25_00);

    expect(attempt).toMatchObject({ kind: "refused", refusal: { code: "consent_revoked" }, kept: null });
    expect(readSavedRequest(visit.keyStore, MONEY_SCOPES.checkout)).toBeNull();
    expect(node.admissions).toHaveLength(0);
  });

  it("refuses to build over a client that carries a tenant API key", () => {
    const node = freshNode();
    const tenantClient = new AzoaApiClient({ baseUrl: node.baseUrl, token: node.token, apiKey: "azoa_live_tenant_key" });
    expect(() => createDirectMoneyTransport(tenantClient, node.userId)).toThrow(/apiKey/);
  });

  it("passes the reusable transport contract with every case reached", async () => {
    const report = await runMoneyTransportContract(() => {
      const node = freshNode();
      return createDirectMoneyTransport(new AzoaApiClient({ baseUrl: node.baseUrl, token: node.token }), node.userId);
    }, { requireAll: true });

    expect(report.failed).toEqual([]);
    expect(report.skipped).toEqual([]);
    expect(report.passed).toHaveLength(7);
  });

  it("the contract fails a transport that opens a second checkout per key and leaks the admission key", async () => {
    const broken = (): AzoaMoneyTransport => {
      const node = freshNode();
      const direct = createDirectMoneyTransport(new AzoaApiClient({ baseUrl: node.baseUrl, token: node.token }), node.userId);
      return {
        ...direct,
        checkout: {
          ...direct.checkout,
          begin: async request => {
            const result = await direct.checkout.begin({ ...request, requestKey: newRequestKey() });
            return result.ok ? { ok: true, value: { ...result.value, admissionKey: "subject:tenant:key" } as CheckoutView } : result;
          },
        },
      };
    };

    const report = await runMoneyTransportContract(broken);

    expect(report.ok).toBe(false);
    expect(report.failed.map(failure => failure.name)).toEqual(expect.arrayContaining([
      "no view carries subject or tenant identifiers",
      "checkout.begin resumes the same intent for the same request key",
    ]));
  });
});
