import type { AzoaMoneyTransport, MoneyRefusal, MoneyRefusalCode, MoneyResult } from "../../src/money/index.js";
import type { NodeEmulator } from "./node-emulator.js";

/** Subject, tenant and provider ids a node (or a careless BFF) can put on any object; the intake must drop every one. */
export const RAW_NODE_IDS = { tenantId: "t-1", subjectUserId: "u-1", externalSubject: "ext-1", connectedAccountId: "acct_1" };

/** Node envelope codes → refusal codes, as a host BFF maps them. */
const REFUSAL_FOR: Record<string, MoneyRefusalCode> = {
  INTENT_FINISHED: "definite",
  INTENT_EXPIRED: "definite",
  REQUEST_KEY_IN_USE: "definite",
  REQUEST_VOIDED: "definite",
  INTENT_NEEDS_REVIEW: "needs_review",
  RAIL_BUSY: "rail_busy",
  NOT_FOUND: "definite",
};

/**
 * A host BFF that forwards the node's envelopes without narrowing anything: every view arrives as the node's raw
 * object (`admissionKey` and all), and every refusal carries the envelope's raw `result` as `serverResult`. The
 * account answers are the node's tenant DTOs spread whole. The bug class the money intake exists for (LOW-2).
 */
export function rawHostTransport(node: NodeEmulator): AzoaMoneyTransport {
  const call = async <T>(method: "GET" | "POST", path: string, body?: unknown): Promise<MoneyResult<T>> => {
    let response: Response;
    try {
      response = await node.fetch(`${node.baseUrl}${path}`, {
        method,
        headers: { Authorization: `Bearer ${node.token}`, "Content-Type": "application/json" },
        ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      });
    } catch {
      return { ok: false, refusal: { code: "unconfirmed", message: "The answer did not arrive." } };
    }
    const envelope = (await response.json()) as { isError: boolean; message: string; result: unknown; code?: string };
    if (response.ok && !envelope.isError) return { ok: true, value: envelope.result as T };
    const refusal = { code: REFUSAL_FOR[envelope.code ?? ""] ?? "unconfirmed", message: envelope.message, serverResult: envelope.result };
    return { ok: false, refusal: refusal as MoneyRefusal };
  };
  const checkout = `/api/fiat/checkout/${node.userId}`;
  const payouts = `/api/fiat/payouts/${node.userId}`;
  const account = async () => ({
    ok: true as const,
    value: {
      walletId: "w-1", walletAddress: "ALGOADDRESS", kycStatus: "Approved" as const, identityReady: true, kycReady: true, walletReady: true, ready: true,
      unavailableReason: null, reviewDueAt: null, ardanovaUserId: "a-1", userId: node.userId, ...RAW_NODE_IDS,
    },
  });

  return {
    account: {
      capabilities: async () => ({ ok: true, value: { ready: true, kycAvailable: true, unavailableReason: null, ...RAW_NODE_IDS } }),
      status: account,
      ensure: account,
      beginKyc: () => call("POST", "/api/kyc/session"),
      revokeConsent: async () => ({ ok: true, value: { revokedAt: new Date().toISOString(), ...RAW_NODE_IDS } }),
    },
    checkout: {
      capabilities: () => call("GET", "/api/fiat/checkout/capabilities"),
      begin: request => call("POST", checkout, request),
      get: intentId => call("GET", `${checkout}/${intentId}`),
      void: requestKey => call("POST", `${checkout}/requests/${encodeURIComponent(requestKey)}/void`),
    },
    cashOut: {
      availability: () => call("GET", `${payouts}/availability`),
      destination: () => call("GET", `${payouts}/account`),
      beginDestinationSetup: () => call("POST", `${payouts}/account`),
      start: request => call("POST", payouts, request),
      history: () => call("GET", payouts),
    },
    balance: {
      get: async () => {
        const quote = await call<{ cashableMinorUnits: number }>("GET", `${payouts}/availability`);
        return quote.ok ? { ok: true, value: { ...quote.value, availableMinorUnits: quote.value.cashableMinorUnits, onHoldMinorUnits: 0, currencyCode: "USD", availableFrom: null } } : quote;
      },
    },
  };
}
